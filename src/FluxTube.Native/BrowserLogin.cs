using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using FluxTube.Services;
using Microsoft.Win32;

namespace FluxTube;

/// <summary>
/// Account sign-in using the user's DEFAULT Windows browser (Chrome, Edge, Brave, Opera, Vivaldi, Yandex, etc.)
/// via an isolated clean profile + local CDP WebSocket session, plus optional Netscape cookies.txt import fallback.
/// Supports adding unlimited Google/YouTube accounts with 1-click swapping in AccountMenu.
/// </summary>
public static class BrowserLogin
{
    private static int _authBusy;
    private static string? _cachedBrowserName;
    private static string? _cachedBrowserExe;
    private static bool _cachedChromium;

    private static readonly (string ExeStem, string Friendly, bool Chromium)[] KnownBrowsers =
    [
        ("chrome", "Google Chrome", true),
        ("msedge", "Microsoft Edge", true),
        ("brave", "Brave", true),
        ("opera", "Opera", true),
        ("vivaldi", "Vivaldi", true),
        ("browser", "Яндекс Браузер", true),
        ("chromium", "Chromium", true),
        ("thorium", "Thorium", true),
        ("arc", "Arc", true),
        ("firefox", "Firefox", false),
        ("librewolf", "LibreWolf", false),
        ("zen", "Zen Browser", false),
    ];

    public static string DefaultBrowserName
    {
        get { EnsureDetected(); return _cachedBrowserName ?? "браузер"; }
    }

    public static bool IsDefaultBrowserChromium
    {
        get { EnsureDetected(); return _cachedChromium && !string.IsNullOrEmpty(_cachedBrowserExe); }
    }

    private static void EnsureDetected()
    {
        if (_cachedBrowserName != null) return;
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = uc?.GetValue("ProgId") as string;
            if (!string.IsNullOrWhiteSpace(progId))
            {
                using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command")
                                ?? Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\{progId}\shell\open\command");
                var raw = cmdKey?.GetValue(null) as string;
                var exe = ExtractExePath(raw);
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    var stem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                    foreach (var (k, name, chrom) in KnownBrowsers)
                    {
                        if (stem == k || stem.Contains(k, StringComparison.Ordinal))
                        {
                            _cachedBrowserExe = exe;
                            _cachedBrowserName = name;
                            _cachedChromium = chrom;
                            return;
                        }
                    }
                    _cachedBrowserExe = exe;
                    _cachedBrowserName = Path.GetFileNameWithoutExtension(exe);
                    _cachedChromium = false;
                    return;
                }
            }
        }
        catch (Exception e) { Paths.Log("default browser detect: " + e.Message); }

        // Fallback probe if UserChoice was unreadable
        string[] fallbacks =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        ];
        foreach (var f in fallbacks)
        {
            if (File.Exists(f))
            {
                _cachedBrowserExe = f;
                _cachedBrowserName = f.Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome" : "Microsoft Edge";
                _cachedChromium = true;
                return;
            }
        }
        _cachedBrowserName = "браузер";
        _cachedChromium = false;
    }

    private static string? ExtractExePath(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        int exeIdx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIdx > 0 ? cmd[..(exeIdx + 4)].Trim() : cmd.Split(' ')[0];
    }

    private static int PickFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>
    /// Opens the user's real default browser (Chrome / Edge / Brave / Opera / Vivaldi / Yandex) in an isolated
    /// sign-in window, waits for Google/YouTube login via local CDP WebSocket, imports cookies automatically,
    /// saves to the multi-account vault, and closes the auth window. Falls back to built-in WebView2 if non-Chromium.
    /// </summary>
    public static void LoginWithDefaultBrowser()
    {
        EnsureDetected();
        if (!_cachedChromium || string.IsNullOrEmpty(_cachedBrowserExe) || !File.Exists(_cachedBrowserExe))
        {
            Shell.Toast($"Браузер {_cachedBrowserName ?? "по умолчанию"} не поддерживает авто-вход, открываю встроенное окно…", Th.Sub, 3500);
            Shell.LoginWebView();
            return;
        }

        if (Interlocked.CompareExchange(ref _authBusy, 1, 0) != 0)
        {
            Shell.Toast($"Окно входа в {_cachedBrowserName} уже открыто — завершите вход там", Th.Sub, 3000);
            return;
        }

        AccountMenu.SaveActiveToVault();
        var browserExe = _cachedBrowserExe!;
        var browserName = _cachedBrowserName ?? "браузер";
        Shell.Toast($"Открываю {browserName} для входа в YouTube…", Th.Good, 4000);

        _ = Task.Run(async () =>
        {
            var profileDir = Path.Combine(Path.GetTempPath(), "fluxtube-auth-" + Guid.NewGuid().ToString("N")[..8]);
            Process? proc = null;
            try
            {
                Directory.CreateDirectory(profileDir);
                int port = PickFreePort();
                var loginUrl = "https://accounts.google.com/ServiceLogin?service=youtube&hl=" + Uri.EscapeDataString(AppSettings.Current.Hl) + "&continue=https%3A%2F%2Fwww.youtube.com%2F";
                var psi = new ProcessStartInfo(browserExe) { UseShellExecute = false };
                psi.ArgumentList.Add($"--user-data-dir={profileDir}");
                psi.ArgumentList.Add($"--remote-debugging-port={port}");
                psi.ArgumentList.Add("--remote-allow-origins=*");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
                psi.ArgumentList.Add("--window-size=560,740");
                psi.ArgumentList.Add("--new-window");
                psi.ArgumentList.Add(loginUrl);

                proc = Process.Start(psi);
                if (proc == null)
                {
                    App.Post(() => { Shell.Toast("Не удалось запустить " + browserName + ", открываю встроенное окно…", Th.Warn); Shell.LoginWebView(); });
                    return;
                }

                // 1. Wait up to 10s for CDP endpoint to come up
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                string? wsUrl = null;
                var startAt = Environment.TickCount64;
                while (Environment.TickCount64 - startAt < 10_000 && !proc.HasExited)
                {
                    try
                    {
                        var verJson = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version");
                        var vNode = JsonNode.Parse(verJson);
                        wsUrl = vNode?["webSocketDebuggerUrl"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(wsUrl)) break;
                    }
                    catch { }
                    await Task.Delay(220);
                }

                if (string.IsNullOrEmpty(wsUrl))
                {
                    Paths.Log($"default browser CDP unavailable for {browserName}, falling back to WebView2");
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                    App.Post(() => { Shell.Toast($"Не удалось подключиться к {browserName}, открываю встроенное окно входа…", Th.Warn, 3500); Shell.LoginWebView(); });
                    return;
                }

                // 2. Connect CDP WebSocket & poll Storage.getCookies until YouTube SAPISID appears
                using var ws = new ClientWebSocket();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

                int msgId = 1;
                JsonArray? capturedCookies = null;
                while (!proc.HasExited && ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var resp = await CdpCallAsync(ws, msgId++, "Storage.getCookies", cts.Token);
                    var cookies = resp?["result"]?["cookies"] as JsonArray;
                    if (cookies != null && HasYouTubeAuthCookies(cookies))
                    {
                        // Give YouTube ~900ms to finish writing final session cookies (__Secure-1PAPISID, LOGIN_INFO, etc.)
                        await Task.Delay(900, cts.Token);
                        var finalResp = await CdpCallAsync(ws, msgId++, "Storage.getCookies", cts.Token);
                        capturedCookies = finalResp?["result"]?["cookies"] as JsonArray ?? cookies;
                        break;
                    }
                    await Task.Delay(650, cts.Token);
                }

                if (capturedCookies != null)
                {
                    var lines = FormatNetscapeCookies(capturedCookies);
                    if (lines.Count > 1)
                    {
                        // Gracefully close the temporary browser window via CDP
                        try { _ = await CdpCallAsync(ws, msgId++, "Browser.close", new CancellationTokenSource(1200).Token); } catch { }

                        App.Post(() =>
                        {
                            try
                            {
                                AccountMenu.SaveActiveToVault();
                                var tmp = Paths.Cookies + ".tmp";
                                File.WriteAllLines(tmp, lines);
                                File.Move(tmp, Paths.Cookies, overwrite: true);
                                Shell.SyncCookieStamp();
                                Shell.OnAccountChanged();
                                Shell.Toast($"Вы вошли в YouTube через {browserName}!", Th.Good, 3600);
                                Shell.OpenRoot("home");
                                W.SetForegroundWindow(App.Hwnd);
                            }
                            catch (Exception ex)
                            {
                                Paths.Log("default browser save cookies: " + ex.Message);
                                Shell.Toast("Ошибка сохранения сессии: " + ex.Message, Th.Warn);
                            }
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Paths.Log("default browser login: " + e.Message);
            }
            finally
            {
                try
                {
                    if (proc != null && !proc.HasExited)
                    {
                        if (!proc.WaitForExit(1500)) proc.Kill(true);
                    }
                }
                catch { }
                proc?.Dispose();
                Interlocked.Exchange(ref _authBusy, 0);
                // Clean up temporary auth profile in background after browser releases locks
                _ = Task.Run(async () =>
                {
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        await Task.Delay(1200);
                        try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, recursive: true); break; } catch { }
                    }
                });
            }
        });
    }

    private static async Task<JsonNode?> CdpCallAsync(ClientWebSocket ws, int id, string method, CancellationToken ct)
    {
        var reqBytes = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"method\":\"{method}\"}}");
        await ws.SendAsync(reqBytes, WebSocketMessageType.Text, true, ct);
        var buf = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, res.Count);
            }
            while (!res.EndOfMessage);

            var node = JsonNode.Parse(ms.ToArray());
            if (node?["id"]?.GetValue<int>() == id) return node;
        }
        return null;
    }

    private static bool HasYouTubeAuthCookies(JsonArray cookies)
    {
        bool hasYtDomain = false, hasSapisid = false;
        foreach (var c in cookies)
        {
            var dom = (c?["domain"]?.GetValue<string>() ?? "").TrimStart('.');
            var name = c?["name"]?.GetValue<string>() ?? "";
            if (dom.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || dom.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                hasYtDomain = true;
                if (name is "SAPISID" or "__Secure-3PAPISID" or "__Secure-1PAPISID") hasSapisid = true;
            }
        }
        return hasYtDomain && hasSapisid;
    }

    private static List<string> FormatNetscapeCookies(JsonArray cookies)
    {
        var lines = new List<string>(cookies.Count + 1) { "# Netscape HTTP Cookie File" };
        foreach (var c in cookies)
        {
            var rawDom = c?["domain"]?.GetValue<string>() ?? "";
            var cleanDom = rawDom.TrimStart('.');
            if (!IsGoogleOrYouTubeDomain(cleanDom)) continue;
            var name = c?["name"]?.GetValue<string>() ?? "";
            var val = c?["value"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            var path = c?["path"]?.GetValue<string>() ?? "/";
            bool httpOnly = c?["httpOnly"]?.GetValue<bool>() ?? false;
            bool secure = c?["secure"]?.GetValue<bool>() ?? false;
            double expD = 0;
            try { expD = c?["expires"]?.GetValue<double>() ?? 0; } catch { }
            long exp = expD > 0 ? (long)expD : DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
            bool includeSub = rawDom.StartsWith('.');
            var domCol = (httpOnly ? "#HttpOnly_" : "") + rawDom;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{domCol}\t{(includeSub ? "TRUE" : "FALSE")}\t{path}\t{(secure ? "TRUE" : "FALSE")}\t{exp}\t{name}\t{val}"));
        }
        return lines;
    }

    private static bool IsGoogleOrYouTubeDomain(string dom)
        => dom.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || dom.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
        || dom.Equals("google.com", StringComparison.OrdinalIgnoreCase)
        || dom.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);

    public static void ImportFromFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Shell.Toast("Файл cookies.txt не найден", Th.Warn);
                return;
            }
            var keep = File.ReadAllLines(path).Where(l =>
            {
                var d = l.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? l[10..] : l;
                if (d.StartsWith('#') || d.Length == 0) return false;
                var tab = d.IndexOf('\t');
                if (tab <= 0) return false;
                var dom = d[..tab].TrimStart('.');
                return IsGoogleOrYouTubeDomain(dom);
            }).ToList();

            var hasAuth = keep.Any(l => l.Contains("\tSAPISID\t") || l.Contains("\t__Secure-3PAPISID\t") || l.Contains("\tSID\t"));
            if (!hasAuth)
            {
                Shell.Toast("В выбранном файле нет активного входа в YouTube", Th.Warn);
                return;
            }

            AccountMenu.SaveActiveToVault();

            var tmp = Paths.Cookies + ".tmp";
            File.WriteAllLines(tmp, new[] { "# Netscape HTTP Cookie File" }.Concat(keep));
            File.Move(tmp, Paths.Cookies, overwrite: true);
            Shell.SyncCookieStamp();
            Shell.OnAccountChanged();
            Shell.Toast(CookieStore.IsLoggedIn ? "Аккаунт импортирован из cookies.txt" : "В файле нет входа в YouTube",
                CookieStore.IsLoggedIn ? Th.Good : Th.Warn);
            if (CookieStore.IsLoggedIn) Shell.OpenRoot("home");
        }
        catch (Exception e)
        {
            Paths.Log("cookies import: " + e.Message);
            Shell.Toast("Ошибка импорта cookies.txt: " + e.Message, Th.Warn);
        }
    }
}
