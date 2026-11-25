using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Optional Netscape cookies.txt import fallback (browser DB scraping was removed because modern Chromium
/// App-Bound Encryption blocks external readers; primary login is the built-in browser login window).
/// </summary>
public static class BrowserLogin
{
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

            // Preserve active account cookies in the vault before importing a new cookies.txt
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
