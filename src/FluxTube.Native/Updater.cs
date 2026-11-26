using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Next-gen GitHub Releases auto-updater:
/// - Rate-limit-free version discovery via GitHub web redirect (releases/latest + expanded_assets),
///   never hitting GitHub API's 60 req/hr 403 limit; falls back to ETag-cached REST API if needed.
/// - Resumable downloads (HTTP Range + 3 auto-retries + live MB/s speed readout).
/// - Mandatory SHA-256 verification against release SHA256SUMS.txt before launching any update.
/// - In-place silent upgrade preserving exact install dir &amp; all Desktop/Taskbar/Start Menu shortcuts,
///   with guaranteed clean relaunch (and automatic ZIP in-place upgrade for Portable installs!).
/// </summary>
public static class Updater
{
    public const string CurrentVersion = "9.2.32";
    public const string Repo = "So2K/FluxTube";

    private static HttpClient _http = MakeClient(allowRedirect: true);
    private static HttpClient _noRedirHttp = MakeClient(allowRedirect: false);
    private static string _proxySnap = AppSettings.Current.Proxy ?? "";

    private static volatile bool _checking, _downloading;
    private static string? _lastEtag;
    private static Timer? _periodicTimer;

    public static bool Checking => _checking;
    public static bool Downloading => _downloading;
    public static double Progress { get; private set; }
    public static string? AvailableVersion { get; private set; }
    public static string? DownloadUrl { get; private set; }
    public static string? PortableUrl { get; private set; }
    public static string? ShaSumsUrl { get; private set; }
    public static string? AssetName { get; private set; }
    public static long AssetSize { get; private set; }
    public static string StatusText { get; private set; } = L.T($"Версия {CurrentVersion}", $"Version {CurrentVersion}");

    public static bool HasUpdate => AvailableVersion != null && (DownloadUrl != null || PortableUrl != null);

    private static bool IsPortableInstall()
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        if (File.Exists(Path.Combine(dir, "unins000.exe")) || File.Exists(Path.Combine(dir, "unins001.exe")))
            return false;
        if (dir.Contains(@"\Programs\FluxTube", StringComparison.OrdinalIgnoreCase))
            return false;
        // Dev / repo build or portable zip extraction
        return !dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
    }

    public static void RefreshStatusText()
    {
        if (_checking || _downloading) return;
        StatusText = HasUpdate
            ? L.T($"Доступно обновление {AvailableVersion}", $"Update {AvailableVersion} available") + (AssetSize > 0 ? $" ({Fmt.Size(AssetSize)})" : "")
            : L.T($"Версия {CurrentVersion}", $"Version {CurrentVersion}");
    }

    private static HttpClient MakeClient(bool allowRedirect)
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowRedirect,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy))
        {
            h.Proxy = new WebProxy(AppSettings.Current.Proxy);
            h.UseProxy = true;
        }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"FluxTube/{CurrentVersion}");
        return c;
    }

    private static void EnsureClients()
    {
        var cur = AppSettings.Current.Proxy ?? "";
        if (cur == _proxySnap) return;
        _proxySnap = cur;
        _http = MakeClient(allowRedirect: true);
        _noRedirHttp = MakeClient(allowRedirect: false);
    }

    private sealed record ReleaseInfo(string Tag, string Version, string? SetupUrl, string? SetupName, long SetupSize, string? PortableUrl, string? PortableName, string? ShaSumsUrl);

    /// <summary>Primary rate-limit-free discovery via github.com/releases/latest redirect + expanded_assets HTML.</summary>
    private static async Task<ReleaseInfo?> FetchViaWebAsync(CancellationToken ct)
    {
        using var headReq = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{Repo}/releases/latest");
        using var headResp = await _noRedirHttp.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
        string? loc = headResp.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(loc)) return null;

        // Location looks like "https://github.com/So2K/FluxTube/releases/tag/v9.2.5"
        int tagIdx = loc.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
        if (tagIdx < 0) return null;
        string rawTag = loc[(tagIdx + 5)..].Split('?', '#')[0].Trim();
        if (string.IsNullOrWhiteSpace(rawTag)) return null;
        string ver = rawTag.TrimStart('v', 'V').Trim();

        string? setupUrl = null, setupName = null, portUrl = null, portName = null, shaUrl = null;
        using var assetsResp = await _http.GetAsync($"https://github.com/{Repo}/releases/expanded_assets/{Uri.EscapeDataString(rawTag)}", ct);
        if (assetsResp.IsSuccessStatusCode)
        {
            var html = await assetsResp.Content.ReadAsStringAsync(ct);
            foreach (Match m in Regex.Matches(html, @"href=""(/[^""]+/releases/download/[^""]+)"""))
            {
                string fullUrl = "https://github.com" + m.Groups[1].Value;
                string fileName = Path.GetFileName(Uri.UnescapeDataString(m.Groups[1].Value));
                if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && setupUrl == null)
                {
                    setupUrl = fullUrl;
                    setupName = fileName;
                }
                else if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && portUrl == null)
                {
                    portUrl = fullUrl;
                    portName = fileName;
                }
                else if (fileName.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    shaUrl = fullUrl;
                }
            }
        }

        // Probe exact asset byte size via lightweight HEAD on the download URL if we found an update
        long size = 0;
        var probeUrl = IsPortableInstall() && portUrl != null ? portUrl : setupUrl;
        if (probeUrl != null && IsNewer(ver, CurrentVersion))
        {
            try
            {
                using var pr = new HttpRequestMessage(HttpMethod.Head, probeUrl);
                using var pResp = await _http.SendAsync(pr, HttpCompletionOption.ResponseHeadersRead, ct);
                if (pResp.IsSuccessStatusCode) size = pResp.Content.Headers.ContentLength ?? 0;
            }
            catch { }
        }

        return new ReleaseInfo(rawTag, ver, setupUrl, setupName, size, portUrl, portName, shaUrl);
    }

    /// <summary>Fallback REST API discovery using ETag so 304 responses consume zero API quota.</summary>
    private static async Task<(ReleaseInfo? Info, int StatusCode)> FetchViaApiAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(_lastEtag)) req.Headers.TryAddWithoutValidation("If-None-Match", _lastEtag);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified) return (null, 304);
        if (!resp.IsSuccessStatusCode) return (null, (int)resp.StatusCode);

        if (resp.Headers.ETag is { } etag) _lastEtag = etag.Tag;
        var json = await resp.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var rawTag = (root.S("tag_name") ?? "").Trim();
        var ver = rawTag.TrimStart('v', 'V').Trim();
        string? setupUrl = null, setupName = null, portUrl = null, portName = null, shaUrl = null;
        long setupSize = 0;

        if (root?["assets"] is JsonArray assets)
        {
            foreach (var a in assets)
            {
                var an = a.S("name") ?? "";
                var au = a.S("browser_download_url");
                if (string.IsNullOrEmpty(au)) continue;
                if (an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && setupUrl == null)
                {
                    setupName = Path.GetFileName(an);
                    setupUrl = au;
                    try { setupSize = a?["size"]?.GetValue<long>() ?? 0; } catch { }
                }
                else if (an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && portUrl == null)
                {
                    portName = Path.GetFileName(an);
                    portUrl = au;
                }
                else if (an.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    shaUrl = au;
                }
            }
        }
        return (new ReleaseInfo(rawTag, ver, setupUrl, setupName, setupSize, portUrl, portName, shaUrl), 200);
    }

    public static void Check(bool manual = false)
    {
        // Start silent periodic background check every 2 hours (rate-limit-free web redirect!)
        if (_periodicTimer == null && !App.Test)
        {
            var t = new Timer(_ => { if (!_checking && !_downloading && !HasUpdate) Check(manual: false); }, null, TimeSpan.FromHours(2), TimeSpan.FromHours(2));
            if (Interlocked.CompareExchange(ref _periodicTimer, t, null) != null) t.Dispose();
        }

        if (_checking || _downloading) return;
        _checking = true;
        if (manual) StatusText = L.T("Проверяю обновления…", "Checking for updates…");
        App.WakeUi();

        _ = Task.Run(async () =>
        {
            try
            {
                EnsureClients();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                ReleaseInfo? info = null;
                int errCode = 0;
                try { info = await FetchViaWebAsync(cts.Token); }
                catch (Exception ex) { Paths.Log("updater web check: " + ex.Message); }

                if (info == null)
                {
                    var apiRes = await FetchViaApiAsync(cts.Token);
                    info = apiRes.Info;
                    errCode = apiRes.StatusCode;
                }

                if (info == null && errCode != 304)
                {
                    App.Post(() =>
                    {
                        _checking = false;
                        RefreshStatusText();
                        if (manual)
                            Shell.Toast(L.T("Не удалось проверить обновления", "Could not check for updates") + (errCode > 0 ? $" ({errCode})" : ""), Th.Warn);
                        App.Invalidate();
                    });
                    return;
                }

                if (info == null && errCode == 304)
                {
                    App.Post(() =>
                    {
                        _checking = false;
                        RefreshStatusText();
                        if (manual && !HasUpdate)
                            Shell.Toast(L.T($"У вас последняя версия FluxTube ({CurrentVersion})", $"FluxTube is up to date ({CurrentVersion})"), Th.Good, 3000);
                        App.Invalidate();
                    });
                    return;
                }

                bool portable = IsPortableInstall();
                string? chosenUrl = portable && info!.PortableUrl != null ? info.PortableUrl : info!.SetupUrl;
                string? chosenName = portable && info.PortableName != null ? info.PortableName : info.SetupName;
                bool newer = IsNewer(info.Version, CurrentVersion) && chosenUrl != null;

                App.Post(() =>
                {
                    _checking = false;
                    if (newer)
                    {
                        AvailableVersion = info.Version;
                        DownloadUrl = info.SetupUrl;
                        PortableUrl = info.PortableUrl;
                        ShaSumsUrl = info.ShaSumsUrl;
                        AssetName = chosenName ?? $"FluxTube-{info.Version}-Setup-x64.exe";
                        AssetSize = info.SetupSize;
                        StatusText = L.T($"Доступно обновление {info.Version}", $"Update {info.Version} available") + (info.SetupSize > 0 ? $" ({Fmt.Size(info.SetupSize)})" : "");
                        if (manual) Shell.Toast(L.T($"Доступна новая версия FluxTube {info.Version}", $"New FluxTube {info.Version} available"), Th.Good, 4500);
                    }
                    else
                    {
                        AvailableVersion = null;
                        DownloadUrl = null;
                        PortableUrl = null;
                        ShaSumsUrl = null;
                        StatusText = L.T($"Установлена последняя версия ({CurrentVersion})", $"Up to date ({CurrentVersion})");
                        if (manual) Shell.Toast(L.T($"У вас последняя версия FluxTube ({CurrentVersion})", $"FluxTube is up to date ({CurrentVersion})"), Th.Good, 3000);
                    }
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("updater check: " + e.Message);
                App.Post(() =>
                {
                    _checking = false;
                    RefreshStatusText();
                    if (manual) Shell.Toast(L.T("Ошибка проверки обновлений: ", "Update check error: ") + e.Message, Th.Warn);
                    App.Invalidate();
                });
            }
        });
    }

    private static async Task<string?> FetchExpectedSha256Async(string? shaUrl, string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(shaUrl)) return null;
        try
        {
            var bytes = await _http.GetByteArrayAsync(shaUrl, ct);
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                int sp = line.IndexOf(' ');
                if (sp <= 32) continue;
                var hash = line[..sp].Trim().ToLowerInvariant();
                var name = line[(sp + 1)..].Trim().TrimStart('*');
                if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && hash.Length == 64)
                    return hash;
            }
        }
        catch (Exception ex) { Paths.Log("updater sha256 fetch: " + ex.Message); }
        return null;
    }

    public static void DownloadAndInstall()
    {
        bool usePortable = IsPortableInstall() && !string.IsNullOrWhiteSpace(PortableUrl);
        var url = usePortable ? PortableUrl : DownloadUrl;
        if (_downloading || string.IsNullOrWhiteSpace(url) || AvailableVersion == null) return;

        var ver = AvailableVersion;
        var safeName = Path.GetFileName(AssetName ?? (usePortable ? $"FluxTube-{ver}-Portable-win-x64.zip" : $"FluxTube-{ver}-Setup-x64.exe"));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"FluxTube-{ver}-Setup-x64.exe";
        var dest = Path.Combine(Path.GetTempPath(), safeName);
        var part = dest + ".part";
        var shaUrl = ShaSumsUrl;

        _downloading = true;
        Progress = 0;
        StatusText = L.T($"Скачиваю FluxTube {ver}…", $"Downloading FluxTube {ver}…");
        Shell.Toast(L.T($"Скачиваю обновление {ver}…", $"Downloading update {ver}…"), Th.Good, 3000);
        App.Invalidate();

        _ = Task.Run(async () =>
        {
            try
            {
                EnsureClients();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var expectedShaTask = FetchExpectedSha256Async(shaUrl, safeName, cts.Token);

                // Resumable download with up to 3 automatic retries on mid-stream network drops
                long total = AssetSize;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

                        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            // Already complete
                            break;
                        }
                        resp.EnsureSuccessStatusCode();

                        bool append = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                        if (!append) existing = 0;
                        total = resp.Content.Headers.ContentRange?.Length
                            ?? (append ? existing + (resp.Content.Headers.ContentLength ?? 0) : (resp.Content.Headers.ContentLength ?? AssetSize));

                        await using var input = await resp.Content.ReadAsStreamAsync(cts.Token);
                        await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
                        var buf = new byte[128 * 1024];
                        long readTotal = existing;
                        long speedBytes = 0;
                        long speedAt = Environment.TickCount64;
                        double mbps = 0;
                        long lastUi = 0;
                        int n;
                        while ((n = await input.ReadAsync(buf, cts.Token)) > 0)
                        {
                            await output.WriteAsync(buf.AsMemory(0, n), cts.Token);
                            readTotal += n;
                            speedBytes += n;
                            var now = Environment.TickCount64;
                            if (now - speedAt >= 600)
                            {
                                mbps = speedBytes / (1024.0 * 1024.0) / ((now - speedAt) / 1000.0);
                                speedBytes = 0;
                                speedAt = now;
                            }
                            if (now - lastUi > 120)
                            {
                                lastUi = now;
                                var prog = total > 0 ? Math.Clamp((double)readTotal / total, 0, 1) : 0;
                                var spd = mbps > 0.05 ? $" · {mbps:0.0} {L.T("МБ/с", "MB/s")}" : "";
                                var st = total > 0
                                    ? L.T($"Скачивание {ver}: {Fmt.Size(readTotal)} / {Fmt.Size(total)} ({(int)(prog * 100)}%){spd}",
                                          $"Downloading {ver}: {Fmt.Size(readTotal)} / {Fmt.Size(total)} ({(int)(prog * 100)}%){spd}")
                                    : L.T($"Скачивание {ver}: {Fmt.Size(readTotal)}{spd}", $"Downloading {ver}: {Fmt.Size(readTotal)}{spd}");
                                App.Post(() => { Progress = prog; StatusText = st; });
                            }
                        }
                        break; // success
                    }
                    catch when (attempt < 2)
                    {
                        await Task.Delay(900 * (attempt + 1), cts.Token);
                    }
                }

                // Verify SHA-256 integrity before touching installed binaries!
                var expectedSha = await expectedShaTask;
                if (!string.IsNullOrEmpty(expectedSha))
                {
                    App.Post(() => { Progress = 1; StatusText = L.T($"Проверяю целостность {ver} (SHA-256)…", $"Verifying {ver} (SHA-256)…"); });
                    await using var fs = File.OpenRead(part);
                    var actualBytes = await SHA256.HashDataAsync(fs, cts.Token);
                    var actualSha = Convert.ToHexString(actualBytes).ToLowerInvariant();
                    if (!actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(L.T("Контрольная сумма SHA-256 не совпала", "SHA-256 checksum mismatch"));
                }

                File.Move(part, dest, overwrite: true);
                App.Post(() => { Progress = 1; StatusText = L.T($"Устанавливаю {ver}…", $"Installing {ver}…"); });

                LaunchSeamlessApply(dest, usePortable);
                App.Post(() => App.After(350, () => W.PostMessageW(App.Hwnd, W.WM_CLOSE, 0, 0)));
            }
            catch (Exception e)
            {
                Paths.Log("updater download: " + e.Message);
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                App.Post(() =>
                {
                    _downloading = false;
                    Progress = 0;
                    StatusText = L.T($"Ошибка скачивания ({ver})", $"Download failed ({ver})");
                    Shell.Toast(L.T("Не удалось скачать обновление: ", "Failed to download update: ") + e.Message, Th.Warn, 6000);
                    App.Invalidate();
                });
            }
        });
    }

    /// <summary>
    /// Spawns a tiny hidden helper script that waits for the current FluxTube PID to exit cleanly,
    /// applies the update strictly into the exact current directory (preserving all Desktop/Taskbar/Start Menu shortcuts),
    /// cleans up the temp installer, and guarantees FluxTube relaunches automatically.
    /// </summary>
    private static void LaunchSeamlessApply(string packagePath, bool isPortableZip)
    {
        var pid = Environment.ProcessId;
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exePath = Path.Combine(installDir, "FluxTube.exe");
        var cmdPath = Path.Combine(Path.GetTempPath(), $"fluxtube-update-{pid}.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)");
        if (isPortableZip)
        {
            sb.AppendLine($"powershell -NoProfile -WindowStyle Hidden -Command \"Expand-Archive -LiteralPath '{packagePath.Replace("'", "''")}' -DestinationPath '{installDir.Replace("'", "''")}' -Force\"");
        }
        else
        {
            sb.AppendLine($"start \"\" /wait \"{packagePath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /DIR=\"{installDir}\"");
        }
        sb.AppendLine($"del /f /q \"{packagePath}\" >nul 2>&1");
        sb.AppendLine($"if exist \"{exePath}\" start \"\" /D \"{installDir}\" \"{exePath}\"");
        sb.AppendLine("del /f /q \"%~f0\" >nul 2>&1");

        File.WriteAllText(cmdPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmdPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        return Version.TryParse(Normalize(remote), out var rv)
            && Version.TryParse(Normalize(local), out var lv)
            && rv > lv;
    }

    private static string Normalize(string v)
    {
        var dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        var parts = v.Split('.');
        return parts.Length == 1 ? v + ".0" : v;
    }
}
