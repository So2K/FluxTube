using System.Diagnostics;
using FluxTube.Services;
using Microsoft.Win32;

namespace FluxTube;

/// <summary>
/// Account sign-in using the user's own default web browser (where they are usually already signed in),
/// with automatic cookie import via yt-dlp --cookies-from-browser and seamless fallback to opening
/// YouTube in their browser or importing cookies.txt / WebView2.
/// </summary>
public static class BrowserLogin
{
    public sealed record BrowserInfo(string Id, string Name);

    public static readonly BrowserInfo[] Supported =
    [
        new("chrome", "Google Chrome"),
        new("edge", "Microsoft Edge"),
        new("firefox", "Firefox"),
        new("brave", "Brave"),
        new("opera", "Opera"),
        new("vivaldi", "Vivaldi"),
        new("chromium", "Chromium"),
    ];

    private static volatile bool _busy;
    public static bool Busy => _busy;

    /// <summary>Detects the user's Windows default HTTPS browser.</summary>
    public static BrowserInfo DetectDefault()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var prog = (k?.GetValue("ProgId") as string ?? "").ToLowerInvariant();
            if (prog.Contains("firefox")) return Supported[2];
            if (prog.Contains("edge")) return Supported[1];
            if (prog.Contains("brave")) return Supported[3];
            if (prog.Contains("opera")) return Supported[4];
            if (prog.Contains("vivaldi")) return Supported[5];
            if (prog.Contains("chrome")) return Supported[0];
        }
        catch { }
        return Supported[0];
    }

    /// <summary>
    /// Smart login: tries to pull an existing YouTube session from the user's default browser right away.
    /// If not signed in (or locked), opens YouTube in the user's real browser so they can sign in there.
    /// </summary>
    public static void StartSmartLogin()
    {
        if (_busy) { Shell.Toast("Уже проверяю вход в браузере…", Th.Sub, 2000); return; }
        var def = DetectDefault();
        _busy = true;
        Shell.Toast($"Ищу активный вход YouTube в {def.Name}…", Th.Sub, 3000);
        _ = Task.Run(async () =>
        {
            try
            {
                var (ok, err) = await TryImportBrowserAsync(def.Id);
                if (ok)
                {
                    App.Post(() =>
                    {
                        _busy = false;
                        Shell.OnAccountChanged();
                        Shell.Toast($"Вы вошли в YouTube через {def.Name}", Th.Good, 3500);
                        Shell.OpenRoot("home");
                    });
                    return;
                }

                // Try other common browsers if default failed (e.g., user keeps YouTube in Firefox/Edge)
                foreach (var b in Supported.Take(4))
                {
                    if (b.Id == def.Id) continue;
                    var (ok2, _) = await TryImportBrowserAsync(b.Id);
                    if (ok2)
                    {
                        App.Post(() =>
                        {
                            _busy = false;
                            Shell.OnAccountChanged();
                            Shell.Toast($"Вы вошли в YouTube через {b.Name}", Th.Good, 3500);
                            Shell.OpenRoot("home");
                        });
                        return;
                    }
                }

                // Could not auto-grab: open YouTube directly in the user's real default browser!
                App.Post(() =>
                {
                    _busy = false;
                    W.Open("https://www.youtube.com/");
                    var locked = err.Contains("DPAPI", StringComparison.OrdinalIgnoreCase)
                                 || err.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
                                 || err.Contains("cookie", StringComparison.OrdinalIgnoreCase);
                    Shell.Toast(locked
                        ? $"Открыл YouTube в {def.Name}. Для импорта из {def.Name} закройте браузер и нажмите «Подхватить вход», либо используйте cookies.txt."
                        : $"Открыл YouTube в {def.Name}. Войдите там и нажмите «Подхватить вход из {def.Name}» в меню аккаунта.",
                        Th.Warn, 8000);
                });
            }
            catch (Exception e)
            {
                Paths.Log("browser login: " + e.Message);
                App.Post(() => { _busy = false; W.Open("https://www.youtube.com/"); Shell.Toast("Открыл YouTube в вашем браузере", Th.Warn, 5000); });
            }
        });
    }

    public static void ImportFromBrowser(BrowserInfo browser)
    {
        if (_busy) return;
        _busy = true;
        Shell.Toast($"Импортирую вход из {browser.Name}…", Th.Sub, 3000);
        _ = Task.Run(async () =>
        {
            var (ok, err) = await TryImportBrowserAsync(browser.Id);
            App.Post(() =>
            {
                _busy = false;
                if (ok)
                {
                    Shell.OnAccountChanged();
                    Shell.Toast($"Вы вошли в YouTube через {browser.Name}", Th.Good, 3500);
                    Shell.OpenRoot("home");
                }
                else
                {
                    bool locked = err.Contains("Could not copy", StringComparison.OrdinalIgnoreCase)
                                  || err.Contains("DPAPI", StringComparison.OrdinalIgnoreCase)
                                  || err.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
                                  || err.Contains("7271", StringComparison.OrdinalIgnoreCase);
                    Shell.Toast(locked
                        ? $"{browser.Name} блокирует cookies пока открыт. Закройте {browser.Name} и повторите, либо войдите через окно входа / cookies.txt."
                        : $"{browser.Name}: активный вход YouTube не найден. Войдите в {browser.Name} или используйте окно входа.",
                        Th.Warn, 7500);
                }
            });
        });
    }

    public static async Task<(bool Ok, string Error)> TryImportBrowserAsync(string browserId)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"fluxtube-{browserId}-{Environment.ProcessId}.txt");
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            var psi = new ProcessStartInfo(Paths.YtDlp)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "--cookies-from-browser", browserId, "--cookies", tmp, "--skip-download", "--no-warnings", "--flat-playlist", "--playlist-items", "1", "https://www.youtube.com/feed/history" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (false, "timeout");
            }
            var err = (await errTask).Trim();
            _ = await outTask;
            if (!File.Exists(tmp)) return (false, err);

            var keep = File.ReadAllLines(tmp).Where(l =>
            {
                var d = l.StartsWith("#HttpOnly_") ? l[10..] : l;
                if (d.StartsWith('#') || d.Length == 0) return false;
                var dom = d.Split('\t')[0].TrimStart('.');
                return dom.EndsWith("youtube.com") || dom.EndsWith("google.com");
            }).ToList();
            try { File.Delete(tmp); } catch { }

            if (keep.Count == 0) return (false, err);
            // Check that an auth cookie is actually present before overwriting
            var hasAuth = keep.Any(l => l.Contains("\tSAPISID\t") || l.Contains("\t__Secure-3PAPISID\t") || l.Contains("\tSID\t"));
            if (!hasAuth) return (false, "В браузере нет активного входа в YouTube");

            File.WriteAllLines(Paths.Cookies, new[] { "# Netscape HTTP Cookie File" }.Concat(keep));
            CookieStore.NotifyExternalChange();
            return (CookieStore.IsLoggedIn, "");
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return (false, e.Message);
        }
    }

    public static void ImportFromFile(string path)
    {
        var keep = File.ReadAllLines(path).Where(l =>
        {
            var d = l.StartsWith("#HttpOnly_") ? l[10..] : l;
            if (d.StartsWith('#') || d.Length == 0) return false;
            var dom = d.Split('\t')[0].TrimStart('.');
            return dom.EndsWith("youtube.com") || dom.EndsWith("google.com");
        });
        File.WriteAllLines(Paths.Cookies, new[] { "# Netscape HTTP Cookie File" }.Concat(keep));
        Shell.OnAccountChanged();
        Shell.Toast(CookieStore.IsLoggedIn ? "Вход импортирован" : "В файле нет входа в YouTube", CookieStore.IsLoggedIn ? Th.Good : Th.Warn);
    }
}
