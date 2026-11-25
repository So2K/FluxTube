using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Built-in GitHub Releases auto-updater: silently checks for a newer release,
/// downloads the Setup EXE with live progress (atomic .part write), and launches a clean in-place upgrade.
/// </summary>
public static class Updater
{
    public const string CurrentVersion = "9.1.6";
    public const string Repo = "So2K/FluxTube";

    private static readonly HttpClient Http = MakeClient();
    private static volatile bool _checking, _downloading;

    public static bool Checking => _checking;
    public static bool Downloading => _downloading;
    public static double Progress { get; private set; }
    public static string? AvailableVersion { get; private set; }
    public static string? DownloadUrl { get; private set; }
    public static string? AssetName { get; private set; }
    public static long AssetSize { get; private set; }
    public static string StatusText { get; private set; } = $"Версия {CurrentVersion}";

    public static bool HasUpdate => AvailableVersion != null && DownloadUrl != null;

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { h.Proxy = new WebProxy(AppSettings.Current.Proxy); h.UseProxy = true; }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"FluxTube/{CurrentVersion}");
        return c;
    }

    public static void Check(bool manual = false)
    {
        if (_checking || _downloading) return;
        _checking = true;
        if (manual) StatusText = "Проверяю обновления…";
        App.WakeUi();
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    App.Post(() =>
                    {
                        _checking = false;
                        StatusText = $"Версия {CurrentVersion}";
                        if (manual) Shell.Toast("Не удалось проверить обновления (" + (int)resp.StatusCode + ")", Th.Warn);
                        App.Invalidate();
                    });
                    return;
                }
                var json = await resp.Content.ReadAsStringAsync(cts.Token);
                var root = JsonNode.Parse(json);
                var tag = (root.S("tag_name") ?? "").TrimStart('v', 'V').Trim();
                string? url = null, name = null;
                long size = 0;
                if (root?["assets"] is JsonArray assets)
                {
                    foreach (var a in assets)
                    {
                        var an = a.S("name") ?? "";
                        var au = a.S("browser_download_url");
                        if (an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(au))
                        {
                            name = Path.GetFileName(an);
                            url = au;
                            try { size = a?["size"]?.GetValue<long>() ?? 0; } catch { }
                            break;
                        }
                    }
                }
                bool newer = IsNewer(tag, CurrentVersion) && url != null;
                App.Post(() =>
                {
                    _checking = false;
                    if (newer)
                    {
                        AvailableVersion = tag;
                        DownloadUrl = url;
                        AssetName = name ?? $"FluxTube-{tag}-Setup-x64.exe";
                        AssetSize = size;
                        StatusText = $"Доступно обновление {tag}" + (size > 0 ? $" ({Fmt.Size(size)})" : "");
                        if (manual) Shell.Toast($"Доступна новая версия FluxTube {tag}", Th.Good, 4500);
                    }
                    else
                    {
                        AvailableVersion = null;
                        DownloadUrl = null;
                        StatusText = $"Установлена последняя версия ({CurrentVersion})";
                        if (manual) Shell.Toast($"У вас последняя версия FluxTube ({CurrentVersion})", Th.Good, 3000);
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
                    StatusText = $"Версия {CurrentVersion}";
                    if (manual) Shell.Toast("Ошибка проверки обновлений: " + e.Message, Th.Warn);
                    App.Invalidate();
                });
            }
        });
    }

    public static void DownloadAndInstall()
    {
        if (_downloading || DownloadUrl == null || AvailableVersion == null) return;
        var url = DownloadUrl;
        var ver = AvailableVersion;
        var safeName = Path.GetFileName(AssetName ?? $"FluxTube-{ver}-Setup-x64.exe");
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"FluxTube-{ver}-Setup-x64.exe";
        var dest = Path.Combine(Path.GetTempPath(), safeName);
        var part = dest + ".part";
        _downloading = true;
        Progress = 0;
        StatusText = $"Скачиваю FluxTube {ver}…";
        Shell.Toast($"Скачиваю обновление {ver}…", Th.Good, 3000);
        App.Invalidate();

        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? AssetSize;
                await using var input = await resp.Content.ReadAsStreamAsync();
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buf = new byte[128 * 1024];
                    long readTotal = 0;
                    long lastUi = 0;
                    int n;
                    while ((n = await input.ReadAsync(buf)) > 0)
                    {
                        await output.WriteAsync(buf.AsMemory(0, n));
                        readTotal += n;
                        var now = Environment.TickCount64;
                        if (now - lastUi > 120)
                        {
                            lastUi = now;
                            var prog = total > 0 ? Math.Clamp((double)readTotal / total, 0, 1) : 0;
                            var st = total > 0
                                ? $"Скачивание {ver}: {Fmt.Size(readTotal)} / {Fmt.Size(total)} ({(int)(prog * 100)}%)"
                                : $"Скачивание {ver}: {Fmt.Size(readTotal)}";
                            App.Post(() => { Progress = prog; StatusText = st; });
                        }
                    }
                }
                File.Move(part, dest, overwrite: true);
                App.Post(() => { Progress = 1; StatusText = $"Запускаю установку {ver}…"; });
                Process.Start(new ProcessStartInfo(dest, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART")
                {
                    UseShellExecute = true,
                });
                App.Post(() => App.After(400, () => W.PostMessageW(App.Hwnd, W.WM_CLOSE, 0, 0)));
            }
            catch (Exception e)
            {
                Paths.Log("updater download: " + e.Message);
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                App.Post(() =>
                {
                    _downloading = false;
                    Progress = 0;
                    StatusText = $"Ошибка скачивания ({ver})";
                    Shell.Toast("Не удалось скачать обновление: " + e.Message, Th.Warn, 6000);
                    App.Invalidate();
                });
            }
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
