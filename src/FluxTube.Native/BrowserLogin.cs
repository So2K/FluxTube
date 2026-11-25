using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxTube.Models;
using FluxTube.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace FluxTube;

public enum LoginPhase { Idle, Launching, WaitingForUser, Verifying, Success, Error }

/// <summary>
/// Unified, effortless YouTube sign-in engine:
/// 1. Primary 1-click sign-in via an isolated Chromium --app popup window (Chrome / Edge / Brave / Yandex / Opera / Vivaldi)
///    with live CDP cookie capture, brand-account / channel-switcher awareness, 2FA/Passkey support, and zero browser lock conflicts.
/// 2. Zero-friction verification mode that pre-injects existing cookies so bot checks take 2 seconds without re-typing passwords.
/// 3. Instant 1-click native SQLite import from Firefox / Zen / LibreWolf (works even while the browser is running).
/// 4. Universal clipboard/file cookie importer supporting Netscape cookies.txt, JSON (Cookie-Editor/EditThisCookie), and raw Cookie headers.
/// </summary>
public static class BrowserLogin
{
    private static int _authBusy;
    private static CancellationTokenSource? _authCts;
    private static Process? _authProc;
    private static volatile bool _finishRequested;
    private static string? _cachedBrowserName;
    private static string? _cachedBrowserExe;
    private static bool _cachedChromium;
    private static string? _chromiumExe;
    private static string? _chromiumName;
    private static bool _chromiumChecked;
    private static (string Name, string DbPath)? _firefoxCache;
    private static long _firefoxCheckedAt;

    public static LoginPhase Phase { get; private set; } = LoginPhase.Idle;
    public static string StatusText { get; private set; } = "";
    public static string ErrorText { get; private set; } = "";
    public static string ActiveEngineName { get; private set; } = "";
    public static bool IsVerification { get; private set; }
    public static bool HasCapturedCookies { get; private set; }
    public static Account? SignedInAccount { get; private set; }
    public static long SuccessAt { get; private set; }

    public static bool IsBusy => Volatile.Read(ref _authBusy) != 0;

    private static readonly (string ExeStem, string Friendly, bool Chromium)[] KnownBrowsers =
    [
        ("chrome", "Google Chrome", true),
        ("msedge", "Microsoft Edge", true),
        ("brave", "Brave", true),
        ("opera", "Opera", true),
        ("vivaldi", "Vivaldi", true),
        ("browser", "Yandex Browser", true),
        ("chromium", "Chromium", true),
        ("thorium", "Thorium", true),
        ("arc", "Arc", true),
        ("firefox", "Firefox", false),
        ("librewolf", "LibreWolf", false),
        ("zen", "Zen Browser", false),
    ];

    public static string DefaultBrowserName
    {
        get { EnsureDetected(); return _cachedBrowserName ?? L.T("браузер", "browser"); }
    }

    public static string BestSignInEngineName
    {
        get
        {
            EnsureChromiumEngine();
            if (!string.IsNullOrEmpty(_chromiumName)) return _chromiumName;
            if (HasWebViewHelper) return "WebView2";
            EnsureDetected();
            return _cachedBrowserName ?? "Browser";
        }
    }

    public static bool HasWebViewHelper => File.Exists(Path.Combine(AppContext.BaseDirectory, "web", "FluxTube.exe"));

    public static void ResetUiState()
    {
        if (IsBusy) return;
        Phase = LoginPhase.Idle;
        StatusText = "";
        ErrorText = "";
        HasCapturedCookies = false;
        SignedInAccount = null;
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

        EnsureChromiumEngine();
        if (_chromiumExe != null)
        {
            _cachedBrowserExe = _chromiumExe;
            _cachedBrowserName = _chromiumName;
            _cachedChromium = true;
            return;
        }
        _cachedBrowserName = "Browser";
        _cachedChromium = false;
    }

    /// <summary>Finds the best Chromium browser on Windows (default browser first if Chromium, otherwise Chrome -> Edge -> Brave -> Yandex -> Vivaldi -> Opera).</summary>
    private static void EnsureChromiumEngine()
    {
        if (_chromiumChecked) return;
        _chromiumChecked = true;
        EnsureDetected();
        if (_cachedChromium && !string.IsNullOrEmpty(_cachedBrowserExe) && File.Exists(_cachedBrowserExe))
        {
            _chromiumExe = _cachedBrowserExe;
            _chromiumName = _cachedBrowserName;
            return;
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        (string Path, string Name)[] candidates =
        [
            (Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), "Google Chrome"),
            (Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"), "Google Chrome"),
            (Path.Combine(local, @"Google\Chrome\Application\chrome.exe"), "Google Chrome"),
            (Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), "Microsoft Edge"),
            (Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"), "Microsoft Edge"),
            (Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"), "Brave"),
            (Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"), "Brave"),
            (Path.Combine(local, @"Yandex\YandexBrowser\Application\browser.exe"), "Yandex Browser"),
            (Path.Combine(local, @"Vivaldi\Application\vivaldi.exe"), "Vivaldi"),
            (Path.Combine(local, @"Programs\Opera\opera.exe"), "Opera"),
            (Path.Combine(local, @"Programs\Opera GX\opera.exe"), "Opera GX"),
        ];
        foreach (var (p, name) in candidates)
        {
            if (File.Exists(p))
            {
                _chromiumExe = p;
                _chromiumName = name;
                return;
            }
        }
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

    // ---------------- Firefox / Zen / LibreWolf instant 1-click native SQLite detector ----------------

    public static (string Name, string DbPath)? DetectFirefoxSession()
    {
        long now = Environment.TickCount64;
        if (_firefoxCheckedAt > 0 && now - _firefoxCheckedAt < 15_000) return _firefoxCache;
        _firefoxCheckedAt = now;
        _firefoxCache = null;
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            (string Name, string Dir)[] roots =
            [
                ("Firefox", Path.Combine(appData, @"Mozilla\Firefox\Profiles")),
                ("Zen Browser", Path.Combine(appData, @"zen\Profiles")),
                ("LibreWolf", Path.Combine(appData, @"librewolf\Profiles")),
            ];
            DateTime bestTime = DateTime.MinValue;
            foreach (var (name, root) in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var prof in Directory.GetDirectories(root))
                {
                    var db = Path.Combine(prof, "cookies.sqlite");
                    if (!File.Exists(db)) continue;
                    var mt = File.GetLastWriteTimeUtc(db);
                    if (mt > bestTime)
                    {
                        bestTime = mt;
                        _firefoxCache = (name, db);
                    }
                }
            }
        }
        catch { }
        return _firefoxCache;
    }

    public static bool HasFirefox => DetectFirefoxSession() != null;

    public static void ImportFromFirefox() => ImportFromFirefoxDirect();

    /// <summary>Copies Firefox's cookies.sqlite (+ WAL/SHM) to temp and reads YouTube cookies natively in C# — works even while Firefox is open.</summary>
    public static void ImportFromFirefoxDirect()
    {
        var found = DetectFirefoxSession();
        if (found == null)
        {
            Shell.Toast(L.T("Профиль Firefox не найден", "No Firefox profile found"), Th.Warn);
            return;
        }
        var (name, dbPath) = found.Value;
        Phase = LoginPhase.Verifying;
        StatusText = L.T($"Читаю сессию {name}…", $"Reading {name} session…");
        App.Invalidate();

        _ = Task.Run(async () =>
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "fluxtube-ff-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(tmpDir);
                var tmpDb = Path.Combine(tmpDir, "cookies.sqlite");
                CopyUnlocked(dbPath, tmpDb);
                if (File.Exists(dbPath + "-wal")) CopyUnlocked(dbPath + "-wal", tmpDb + "-wal");
                if (File.Exists(dbPath + "-shm")) CopyUnlocked(dbPath + "-shm", tmpDb + "-shm");

                var cookies = ReadFirefoxCookies(tmpDb);
                if (!HasAuthCookies(cookies))
                {
                    App.Post(() =>
                    {
                        Phase = LoginPhase.Error;
                        ErrorText = L.T($"В {name} не найден активный вход в YouTube. Войдите через Google выше.",
                                        $"No active YouTube sign-in found in {name}. Use Sign in with Google above.");
                        App.Invalidate();
                    });
                    return;
                }
                await FinalizeSignInAsync(cookies, name, isVerification: false);
            }
            catch (Exception e)
            {
                Paths.Log("firefox direct import: " + e.Message);
                App.Post(() =>
                {
                    Phase = LoginPhase.Error;
                    ErrorText = L.T($"Не удалось импортировать из {name}: ", $"Could not import from {name}: ") + e.Message;
                    App.Invalidate();
                });
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        });
    }

    private static void CopyUnlocked(string src, string dst)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static List<CookieStore.Cookie> ReadFirefoxCookies(string cookieDb)
    {
        var cookies = new List<CookieStore.Cookie>();
        var cs = new SqliteConnectionStringBuilder { DataSource = cookieDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var db = new SqliteConnection(cs);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT host, path, isSecure, expiry, name, value, isHttpOnly FROM moz_cookies WHERE originAttributes = ''";
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var domain = rows.GetString(0);
            if (!IsGoogleOrYouTubeDomain(domain.TrimStart('.'))) continue;
            cookies.Add(new CookieStore.Cookie(domain, domain.StartsWith('.'), rows.GetString(1), rows.GetInt64(2) != 0,
                rows.GetInt64(3), rows.GetString(4), rows.GetString(5), rows.GetInt64(6) != 0));
        }
        return cookies;
    }

    // ---------------- Primary 1-Click Clean App-Mode Sign-In (Chromium CDP) ----------------

    public static void FocusAuthWindow()
    {
        try
        {
            var p = _authProc;
            if (p == null || p.HasExited) return;
            p.Refresh();
            var h = p.MainWindowHandle;
            if (h != 0)
            {
                if (W.IsIconic(h)) W.ShowWindow(h, W.SW_SHOWNORMAL);
                W.SetForegroundWindow(h);
            }
        }
        catch { }
    }

    public static void RequestFinishNow() => _finishRequested = true;

    public static void CancelAuth()
    {
        try { _authCts?.Cancel(); } catch { }
        try
        {
            var p = _authProc;
            if (p != null && !p.HasExited) p.Kill(true);
        }
        catch { }
        Phase = LoginPhase.Idle;
        StatusText = "";
        ErrorText = "";
        HasCapturedCookies = false;
        App.Invalidate();
    }

    /// <summary>Opens the clean isolated app-mode Google sign-in window (no tabs, no Chrome locks, auto-closes when signed in).</summary>
    public static void StartGoogleSignIn(bool verification = false, string? verifyVideoId = null)
    {
        EnsureChromiumEngine();
        if (string.IsNullOrEmpty(_chromiumExe) || !File.Exists(_chromiumExe))
        {
            if (HasWebViewHelper)
            {
                LoginModal.Close();
                Shell.LoginWebView();
                return;
            }
            Phase = LoginPhase.Error;
            ErrorText = L.T("Не найден Chrome/Edge для окна входа. Используйте импорт cookies ниже.",
                            "No Chromium browser (Chrome/Edge) found for sign-in popup. Use cookie import below.");
            App.Invalidate();
            return;
        }

        if (Interlocked.CompareExchange(ref _authBusy, 1, 0) != 0)
        {
            FocusAuthWindow();
            return;
        }

        AccountMenu.SaveActiveToVault();
        var browserExe = _chromiumExe;
        var browserName = _chromiumName ?? "Browser";
        ActiveEngineName = browserName;
        IsVerification = verification;
        HasCapturedCookies = false;
        SignedInAccount = null;
        ErrorText = "";
        _finishRequested = false;
        Phase = LoginPhase.Launching;
        StatusText = L.T($"Запуск окна входа ({browserName})…", $"Opening sign-in window ({browserName})…");
        App.Invalidate();

        // Center the clean 540x760 app popup right over FluxTube
        int popW = 540, popH = 760, popX = 160, popY = 90;
        unsafe
        {
            W.RECT rc;
            if (App.Hwnd != 0 && W.GetWindowRect(App.Hwnd, &rc))
            {
                popX = Math.Max(20, rc.L + ((rc.R - rc.L) - popW) / 2);
                popY = Math.Max(20, rc.T + ((rc.B - rc.T) - popH) / 2);
            }
        }
        bool darkMode = !Themes.Current.Light;
        var hl = Uri.EscapeDataString(AppSettings.Current.Hl);
        var existingCookies = verification && CookieStore.IsLoggedIn ? CookieStore.All().ToList() : null;
        var targetUrl = verification && existingCookies is { Count: > 0 }
            ? (!string.IsNullOrEmpty(verifyVideoId) ? $"https://www.youtube.com/watch?v={verifyVideoId}" : "https://www.youtube.com/")
            : $"https://accounts.google.com/ServiceLogin?service=youtube&hl={hl}&continue=" +
              Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&app=desktop&next=%2F");

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(verification ? 15 : 8));
        _authCts = cts;

        _ = Task.Run(async () =>
        {
            var profileDir = Path.Combine(Path.GetTempPath(), "fluxtube-auth-" + Guid.NewGuid().ToString("N")[..8]);
            Process? proc = null;
            try
            {
                Directory.CreateDirectory(profileDir);
                int port = PickFreePort();
                var startUrl = existingCookies is { Count: > 0 } ? "about:blank" : targetUrl;

                var psi = new ProcessStartInfo(browserExe) { UseShellExecute = false };
                psi.ArgumentList.Add($"--user-data-dir={profileDir}");
                psi.ArgumentList.Add($"--remote-debugging-port={port}");
                psi.ArgumentList.Add("--remote-allow-origins=*");
                psi.ArgumentList.Add($"--app={startUrl}");
                psi.ArgumentList.Add($"--window-size={popW},{popH}");
                psi.ArgumentList.Add($"--window-position={popX},{popY}");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
                psi.ArgumentList.Add("--disable-extensions");
                psi.ArgumentList.Add("--disable-sync");
                psi.ArgumentList.Add("--disable-default-apps");
                psi.ArgumentList.Add("--disable-features=Translate");
                if (darkMode) psi.ArgumentList.Add("--force-dark-mode");

                proc = Process.Start(psi);
                _authProc = proc;
                if (proc == null) throw new IOException($"Could not start {browserName}");

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                string? wsUrl = null;
                var startAt = Environment.TickCount64;
                while (Environment.TickCount64 - startAt < 12_000 && !proc.HasExited && !cts.IsCancellationRequested)
                {
                    try
                    {
                        var verJson = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cts.Token);
                        wsUrl = JsonNode.Parse(verJson)?["webSocketDebuggerUrl"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(wsUrl)) break;
                    }
                    catch { }
                    await Task.Delay(180, cts.Token);
                }

                if (string.IsNullOrEmpty(wsUrl))
                    throw new IOException(L.T($"Не удалось подключиться к {browserName}. Попробуйте ещё раз или выберите другой способ.",
                                              $"Could not attach to {browserName}. Try again or pick another method."));

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
                int msgId = 1;

                // Verification with existing account: pre-seed cookies into the popup so user doesn't have to re-type password!
                if (existingCookies is { Count: > 0 })
                {
                    await SeedCookiesOverCdpAsync(ws, msgId++, existingCookies, cts.Token);
                    await NavigatePageOverCdpAsync(http, port, targetUrl, cts.Token);
                }

                App.Post(() =>
                {
                    Phase = LoginPhase.WaitingForUser;
                    StatusText = verification
                        ? L.T("Пройдите проверку YouTube в открывшемся окне, затем нажмите «Готово» или закройте окно",
                              "Complete YouTube verification in the popup window, then click Done or close it")
                        : L.T("Войдите в аккаунт Google в открывшемся окне",
                              "Sign in to Google in the popup window");
                    App.Invalidate();
                });

                JsonArray? captured = null;
                long homeLandedAt = 0;

                while (!proc.HasExited && ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    JsonNode? resp;
                    try { resp = await CdpCallAsync(ws, msgId++, "Storage.getCookies", null, cts.Token); }
                    catch when (captured != null) { break; }

                    var cookies = resp?["result"]?["cookies"] as JsonArray;
                    if (cookies != null && HasYouTubeAuthCookies(cookies))
                    {
                        captured = cookies;
                        if (!HasCapturedCookies)
                        {
                            HasCapturedCookies = true;
                            App.Post(App.Invalidate);
                        }

                        if (_finishRequested)
                            break;

                        if (!verification)
                        {
                            // Wait until user finishes Google 2FA AND any YouTube channel/brand-account chooser!
                            bool onYtHome = await IsActivePageOnYouTubeHomeAsync(http, port, cts.Token);
                            if (onYtHome)
                            {
                                if (homeLandedAt == 0) homeLandedAt = Environment.TickCount64;
                                if (Environment.TickCount64 - homeLandedAt >= 1100)
                                {
                                    // Final read so rotated __Secure-1PSIDTS / __Secure-3PSIDTS are captured
                                    try
                                    {
                                        var fin = await CdpCallAsync(ws, msgId++, "Storage.getCookies", null, cts.Token);
                                        captured = fin?["result"]?["cookies"] as JsonArray ?? captured;
                                    }
                                    catch { }
                                    break;
                                }
                            }
                            else homeLandedAt = 0;
                        }
                    }
                    await Task.Delay(450, cts.Token);
                }

                if (cts.IsCancellationRequested)
                {
                    try { if (!proc.HasExited) _ = await CdpCallAsync(ws, msgId++, "Browser.close", null, new CancellationTokenSource(800).Token); } catch { }
                    return;
                }

                if (captured == null)
                {
                    App.Post(() =>
                    {
                        Phase = LoginPhase.Idle;
                        StatusText = "";
                        App.Invalidate();
                    });
                    return;
                }

                // Gracefully close popup window
                try { if (!proc.HasExited) _ = await CdpCallAsync(ws, msgId++, "Browser.close", null, new CancellationTokenSource(1200).Token); } catch { }

                var parsed = ConvertCdpCookies(captured);
                await FinalizeSignInAsync(parsed, browserName, verification);
            }
            catch (OperationCanceledException)
            {
                App.Post(() => { Phase = LoginPhase.Idle; StatusText = ""; App.Invalidate(); });
            }
            catch (Exception e)
            {
                Paths.Log("app-mode login: " + e.Message);
                App.Post(() =>
                {
                    Phase = LoginPhase.Error;
                    ErrorText = e.Message;
                    App.Invalidate();
                });
            }
            finally
            {
                _authProc = null;
                _authCts = null;
                try { if (proc is { HasExited: false } && !proc.WaitForExit(1200)) proc.Kill(true); } catch { }
                proc?.Dispose();
                Interlocked.Exchange(ref _authBusy, 0);
                _ = Task.Run(async () =>
                {
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        await Task.Delay(1200);
                        try { DeleteTempProfile(profileDir); break; } catch { }
                    }
                });
            }
        });
    }

    private static async Task FinalizeSignInAsync(List<CookieStore.Cookie> cookies, string sourceName, bool isVerification)
    {
        App.Post(() =>
        {
            Phase = LoginPhase.Verifying;
            StatusText = L.T("Сохраняю сессию и загружаю профиль…", "Saving session & loading profile…");
            App.Invalidate();
        });

        AccountMenu.SaveActiveToVault();
        CookieStore.Save(cookies);

        Account? verifiedAcc = null;
        try
        {
            InnerTube.Instance.ResetSession();
            verifiedAcc = await InnerTube.Instance.AccountAsync();
        }
        catch (Exception e) { Paths.Log("account verify: " + e.Message); }

        App.Post(() =>
        {
            Shell.SyncCookieStamp();
            Shell.OnAccountChanged();
            SignedInAccount = verifiedAcc;
            SuccessAt = Environment.TickCount64;
            Phase = LoginPhase.Success;
            StatusText = verifiedAcc is { Name.Length: > 0 }
                ? L.T($"Добро пожаловать, {verifiedAcc.Name}!", $"Welcome, {verifiedAcc.Name}!")
                : (isVerification
                    ? L.T("Проверка YouTube завершена!", "YouTube verification complete!")
                    : L.T($"Вы вошли в YouTube ({sourceName})!", $"Signed in to YouTube ({sourceName})!"));
            Shell.Toast(StatusText, Th.Good, 3600);
            if (!isVerification) Shell.OpenRoot("home");
            if (App.Hwnd != 0)
            {
                if (W.IsIconic(App.Hwnd)) W.ShowWindow(App.Hwnd, W.SW_SHOWNORMAL);
                W.SetForegroundWindow(App.Hwnd);
            }
            App.Invalidate();
            App.After(1150, () =>
            {
                if (Phase == LoginPhase.Success)
                {
                    LoginModal.Close();
                    ResetUiState();
                }
            });
        });
    }

    private static async Task<bool> IsActivePageOnYouTubeHomeAsync(HttpClient http, int port, CancellationToken ct)
    {
        try
        {
            var raw = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
            if (JsonNode.Parse(raw) is not JsonArray targets) return false;
            foreach (var t in targets)
            {
                if (t?["type"]?.GetValue<string>() != "page") continue;
                var url = t["url"]?.GetValue<string>() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) continue;
                if (!u.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase)
                    && !u.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                var path = u.AbsolutePath.ToLowerInvariant();
                if (path.StartsWith("/signin") || path.StartsWith("/channel_switcher")
                    || path.StartsWith("/create_channel") || path.StartsWith("/oops"))
                    continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static async Task SeedCookiesOverCdpAsync(ClientWebSocket ws, int id, List<CookieStore.Cookie> cookies, CancellationToken ct)
    {
        try
        {
            var arr = new JsonArray();
            foreach (var c in cookies)
            {
                var dom = c.Domain.TrimStart('.');
                if (!IsGoogleOrYouTubeDomain(dom)) continue;
                var o = new JsonObject
                {
                    ["name"] = c.Name,
                    ["value"] = c.Value,
                    ["domain"] = c.Domain,
                    ["path"] = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    ["secure"] = c.Secure,
                    ["httpOnly"] = c.HttpOnly,
                };
                if (c.Expires > 0) o["expires"] = c.Expires;
                arr.Add(o);
            }
            if (arr.Count > 0)
                await CdpCallAsync(ws, id, "Storage.setCookies", new JsonObject { ["cookies"] = arr }, ct);
        }
        catch (Exception e) { Paths.Log("cdp seed cookies: " + e.Message); }
    }

    private static async Task NavigatePageOverCdpAsync(HttpClient http, int port, string url, CancellationToken ct)
    {
        try
        {
            var raw = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
            var pageWs = (JsonNode.Parse(raw) as JsonArray)?
                .FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page")?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageWs)) return;
            using var pws = new ClientWebSocket();
            await pws.ConnectAsync(new Uri(pageWs), ct);
            await CdpCallAsync(pws, 1, "Page.navigate", new JsonObject { ["url"] = url }, ct);
        }
        catch (Exception e) { Paths.Log("cdp nav: " + e.Message); }
    }

    private static void DeleteTempProfile(string profileDir)
    {
        var full = Path.GetFullPath(profileDir);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(temp), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("fluxtube-auth-", StringComparison.Ordinal))
            throw new IOException("Unsafe auth profile path");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private static async Task<JsonNode?> CdpCallAsync(ClientWebSocket ws, int id, string method, JsonObject? prms, CancellationToken ct)
    {
        var payload = new JsonObject { ["id"] = id, ["method"] = method };
        if (prms != null) payload["params"] = prms;
        var reqBytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
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
        bool hasSapisid = false, hasLogin = false;
        foreach (var c in cookies)
        {
            var dom = (c?["domain"]?.GetValue<string>() ?? "").TrimStart('.');
            var name = c?["name"]?.GetValue<string>() ?? "";
            if (dom.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || dom.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                if (name is "SAPISID" or "__Secure-3PAPISID" or "__Secure-1PAPISID") hasSapisid = true;
                if (name is "LOGIN_INFO" or "SID" or "__Secure-1PSID" or "__Secure-3PSID") hasLogin = true;
            }
        }
        return hasSapisid && hasLogin;
    }

    private static bool HasAuthCookies(IEnumerable<CookieStore.Cookie> cookies)
        => cookies.Any(c => c.Domain.TrimStart('.').EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
            && c.Name is "SAPISID" or "__Secure-3PAPISID" or "__Secure-1PAPISID");

    private static List<CookieStore.Cookie> ConvertCdpCookies(JsonArray cookies)
    {
        var list = new List<CookieStore.Cookie>(cookies.Count);
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
            list.Add(new CookieStore.Cookie(rawDom, rawDom.StartsWith('.'), path, secure, exp, name, val, httpOnly));
        }
        return list;
    }

    private static bool IsGoogleOrYouTubeDomain(string dom)
        => dom.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || dom.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
        || dom.Equals("google.com", StringComparison.OrdinalIgnoreCase)
        || dom.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);

    // ---------------- Universal Cookie Importer (Netscape cookies.txt / JSON / raw Cookie header) ----------------

    public static void ImportFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Shell.Toast(L.T("Файл cookies не найден", "Cookies file not found"), Th.Warn);
            return;
        }
        try { ImportFromText(File.ReadAllText(path, Encoding.UTF8), Path.GetFileName(path)); }
        catch (Exception e) { Shell.Toast(L.T("Ошибка чтения файла: ", "File read error: ") + e.Message, Th.Warn); }
    }

    public static void ImportFromClipboard()
    {
        var raw = W.GetClipboardText(App.Hwnd, 512 * 1024);
        if (string.IsNullOrWhiteSpace(raw))
        {
            Shell.Toast(L.T("Буфер обмена пуст — скопируйте cookies.txt, JSON или строку Cookie", "Clipboard is empty — copy cookies.txt, JSON, or Cookie header"), Th.Warn);
            return;
        }
        ImportFromText(raw, L.T("буфера обмена", "clipboard"));
    }

    public static void ImportFromText(string raw, string sourceLabel)
    {
        var cookies = ParseAnyCookies(raw);
        if (!HasAuthCookies(cookies))
        {
            var msg = L.T("Не найдены YouTube-cookies входа (SAPISID). Проверьте файл или скопированные cookies.",
                          "No YouTube sign-in cookies (SAPISID) found. Check the file or copied cookies.");
            if (LoginModal.Open) { Phase = LoginPhase.Error; ErrorText = msg; App.Invalidate(); }
            else Shell.Toast(msg, Th.Warn, 5000);
            return;
        }
        _ = Task.Run(() => FinalizeSignInAsync(cookies, sourceLabel, isVerification: false));
    }

    private static List<CookieStore.Cookie> ParseAnyCookies(string raw)
    {
        var list = new List<CookieStore.Cookie>();
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return list;

        // 1. JSON array (Cookie-Editor, EditThisCookie, J2TEAM, browser extensions)
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            try
            {
                var node = JsonNode.Parse(trimmed);
                var arr = node as JsonArray ?? node?["cookies"] as JsonArray;
                if (arr != null)
                {
                    foreach (var c in arr)
                    {
                        var dom = c?["domain"]?.GetValue<string>() ?? "";
                        if (!IsGoogleOrYouTubeDomain(dom.TrimStart('.'))) continue;
                        var name = c?["name"]?.GetValue<string>() ?? "";
                        var val = c?["value"]?.GetValue<string>() ?? "";
                        if (string.IsNullOrEmpty(name)) continue;
                        var path = c?["path"]?.GetValue<string>() ?? "/";
                        bool sec = c?["secure"]?.GetValue<bool>() ?? true;
                        bool http = c?["httpOnly"]?.GetValue<bool>() ?? false;
                        long exp = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
                        try
                        {
                            var ed = c?["expirationDate"]?.GetValue<double>() ?? c?["expires"]?.GetValue<double>() ?? 0;
                            if (ed > 0) exp = (long)ed;
                        }
                        catch { }
                        list.Add(new CookieStore.Cookie(dom, dom.StartsWith('.'), path, sec, exp, name, val, http));
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { }
        }

        // 2. Netscape tab-separated cookies.txt
        foreach (var rawLine in trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            bool httpOnly = false;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) { httpOnly = true; line = line[10..]; }
            else if (line.StartsWith('#') || line.Length == 0) continue;
            var f = line.Split('\t');
            if (f.Length < 7) continue;
            var dom = f[0];
            if (!IsGoogleOrYouTubeDomain(dom.TrimStart('.'))) continue;
            long.TryParse(f[4], out var exp);
            list.Add(new CookieStore.Cookie(dom, f[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase), f[2],
                f[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase), exp, f[5], f[6], httpOnly));
        }
        if (list.Count > 0) return list;

        // 3. Raw HTTP "Cookie: SAPISID=...; __Secure-3PAPISID=..." header string from DevTools
        var hdr = trimmed;
        if (hdr.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)) hdr = hdr[7..].Trim();
        long expDefault = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        foreach (var pair in hdr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq].Trim();
            var val = pair[(eq + 1)..].Trim();
            if (name.Length == 0 || val.Length == 0) continue;
            list.Add(new CookieStore.Cookie(".youtube.com", true, "/", true, expDefault, name, val, false));
        }
        return list;
    }
}
