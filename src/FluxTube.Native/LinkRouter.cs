using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FluxTube.Services;
using Microsoft.Win32;

namespace FluxTube;

/// <summary>
/// Instant YouTube Link Interceptor &amp; Single-Instance IPC Router for Telegram / Discord / Windows:
/// 1. Fast CLI entry (--route-url &lt;url&gt;, fluxtube://..., or raw YouTube URL):
///    - Non-YouTube links are forwarded straight to the user's normal browser (Chrome/Edge/Firefox/etc.) in ~15ms without starting GUI.
///    - YouTube links are sent via WM_COPYDATA to an already-open FluxTube window (zero duplicate windows!), or start FluxTube on that video.
/// 2. Registers HKCU (zero UAC!) protocol "fluxtube://" + Windows RegisteredApplication "FluxTube" so setting FluxTube
///    as default browser intercepts 100% of YouTube clicks from Telegram/Discord while keeping normal browsing untouched.
/// 3. Optional zero-CPU Win32 Clipboard Listener (WM_CLIPBOARDUPDATE): copy any YouTube link in Telegram -> opens in FluxTube immediately!
/// </summary>
public static unsafe class LinkRouter
{
    public const nuint CopyDataMagic = 0x464C5558; // "FLUX"
    public const string ProgId = "FluxTubeURL";

    public static string? StartupUrl { get; private set; }
    private static string _lastClipUrl = "";
    private static long _lastClipAt;

    public static bool IsYouTubeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.StartsWith("fluxtube://", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https")) return false;
        var host = u.Host.ToLowerInvariant();
        return host == "youtu.be"
            || host == "youtube.com"
            || host.EndsWith(".youtube.com", StringComparison.Ordinal)
            || host == "youtube-nocookie.com"
            || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
    }

    public static string NormalizeUrl(string raw)
    {
        var s = raw.Trim().Trim('"');
        if (s.StartsWith("fluxtube://", StringComparison.OrdinalIgnoreCase))
        {
            s = s[11..].TrimStart('/');
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "https://" + s;
        }
        return s;
    }

    /// <summary>
    /// Runs at the very top of Main() BEFORE creating D3D11/window (~12ms exit for forwarded URLs!).
    /// Returns true if the URL was handled (forwarded to browser or sent to running FluxTube) and this process should exit.
    /// </summary>
    public static bool TryHandleCliRoute(string[] args)
    {
        if (args.Length == 0 || args.Contains("--test")) return false;

        string? url = null;
        bool isRoute = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if ((a == "--route-url" || a == "--open-url") && i + 1 < args.Length)
            {
                url = args[i + 1];
                isRoute = a == "--route-url";
                break;
            }
            if (!a.StartsWith('-') && (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("fluxtube://", StringComparison.OrdinalIgnoreCase)))
            {
                url = a;
                isRoute = true;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(url)) return false;

        var norm = NormalizeUrl(url);
        if (!IsYouTubeUrl(norm))
        {
            if (isRoute) ForwardToFallbackBrowser(norm);
            return true;
        }

        // YouTube URL -> deliver directly to already-running FluxTube instance via WM_COPYDATA!
        var existing = W.FindWindowW("FluxTubeMain", null);
        if (existing != 0 && SendUrlToWindow(existing, norm))
            return true;

        // No running FluxTube yet -> remember URL so Shell.Init opens it immediately on startup!
        StartupUrl = norm;
        return false;
    }

    private static bool SendUrlToWindow(nint hwnd, string url)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(url);
            fixed (byte* p = bytes)
            {
                var cds = new W.COPYDATASTRUCT
                {
                    dwData = CopyDataMagic,
                    cbData = (uint)bytes.Length,
                    lpData = (nint)p,
                };
                return W.SendMessageW(hwnd, W.WM_COPYDATA, 0, (nint)(&cds)) != 0;
            }
        }
        catch { return false; }
    }

    /// <summary>Called inside App.WndProc on WM_COPYDATA. Returns true if message belonged to FluxTube.</summary>
    public static bool HandleCopyData(nint lParam)
    {
        if (lParam == 0) return false;
        var cds = *(W.COPYDATASTRUCT*)lParam;
        if (cds.dwData != CopyDataMagic || cds.lpData == 0 || cds.cbData == 0 || cds.cbData > 16384) return false;
        var url = Encoding.UTF8.GetString((byte*)cds.lpData, (int)cds.cbData).Trim();
        if (url.Length == 0) return false;
        App.Post(() => Shell.HandleDropped(url, newTab: true, backgroundTab: false));
        return true;
    }

    /// <summary>Called on WM_CLIPBOARDUPDATE (0% idle CPU!). Opens copied YouTube links when copied outside FluxTube.</summary>
    public static void OnClipboardUpdated()
    {
        if (!AppSettings.Current.AutoOpenCopiedYouTubeLinks || App.Test) return;
        // Ignore copies initiated from inside FluxTube itself!
        if (W.GetForegroundWindow() == App.Hwnd) return;

        var text = ReadClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.Length > 320 || text.Contains('\n') || text.Contains('\r')) return;
        if (!IsYouTubeUrl(text)) return;
        // Only auto-open actual video/short/playlist/channel links (never bare youtube.com homepage)
        if (Shell.ExtractYouTubeId(text) == null && !text.Contains("list=", StringComparison.OrdinalIgnoreCase)) return;

        long now = Environment.TickCount64;
        if (text == _lastClipUrl && now - _lastClipAt < 5000) return;
        _lastClipUrl = text;
        _lastClipAt = now;

        App.Post(() =>
        {
            Shell.HandleDropped(text, newTab: true, backgroundTab: false);
            Shell.Toast(L.T("Ссылка YouTube открыта из буфера", "YouTube link opened from clipboard"), Th.Good, 2400);
        });
    }

    private static string? ReadClipboardText()
    {
        const uint CF_UNICODETEXT = 13;
        if (!W.IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!W.OpenClipboard(App.Hwnd)) return null;
        try
        {
            var h = W.GetClipboardData(CF_UNICODETEXT);
            if (h == 0) return null;
            var p = W.GlobalLock(h);
            if (p == 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { W.GlobalUnlock(h); }
        }
        finally { W.CloseClipboard(); }
    }

    /// <summary>Detects the user's current real browser (Chrome/Edge/Firefox/Yandex/Opera/Brave) so non-YouTube links open seamlessly.</summary>
    public static string DetectFallbackBrowserCommand()
    {
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = uc?.GetValue("ProgId") as string;
            if (!string.IsNullOrWhiteSpace(progId) && !progId.Equals(ProgId, StringComparison.OrdinalIgnoreCase))
            {
                var cmd = ReadOpenCommand(progId);
                if (!string.IsNullOrWhiteSpace(cmd) && !cmd.Contains("FluxTube.exe", StringComparison.OrdinalIgnoreCase))
                    return cmd;
            }
        }
        catch { }

        // Probe well-known browsers if UserChoice is already FluxTubeURL
        string[] knownProgIds = ["ChromeHTML", "MSEdgeHTM", "YandexBrowserHTML", "BraveHTML", "OperaStable", "FirefoxURL-308046B0AF4A39CB"];
        foreach (var pid in knownProgIds)
        {
            var cmd = ReadOpenCommand(pid);
            if (!string.IsNullOrWhiteSpace(cmd) && !cmd.Contains("FluxTube.exe", StringComparison.OrdinalIgnoreCase))
                return cmd;
        }

        string[] knownExes =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
        ];
        foreach (var exe in knownExes)
            if (File.Exists(exe)) return $"\"{exe}\" \"%1\"";

        return "";
    }

    private static string? ReadOpenCommand(string progId)
    {
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            return k?.GetValue(null) as string;
        }
        catch { return null; }
    }

    public static string FriendlyBrowserName(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return L.T("Браузер по умолчанию", "Default browser");
        var lo = cmd.ToLowerInvariant();
        if (lo.Contains("chrome.exe")) return "Google Chrome";
        if (lo.Contains("msedge.exe")) return "Microsoft Edge";
        if (lo.Contains("firefox.exe")) return "Firefox";
        if (lo.Contains("browser.exe") && lo.Contains("yandex")) return "Яндекс Браузер";
        if (lo.Contains("brave.exe")) return "Brave";
        if (lo.Contains("opera")) return "Opera";
        if (lo.Contains("vivaldi")) return "Vivaldi";
        return Path.GetFileNameWithoutExtension(ExtractExe(cmd));
    }

    private static string ExtractExe(string cmd)
    {
        var s = cmd.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : s.Trim('"');
        }
        int sp = s.IndexOf(' ');
        return sp > 0 ? s[..sp] : s;
    }

    private static void ForwardToFallbackBrowser(string url)
    {
        try
        {
            var cmd = AppSettings.Current.FallbackBrowserCmd;
            if (string.IsNullOrWhiteSpace(cmd) || cmd.Contains("FluxTube.exe", StringComparison.OrdinalIgnoreCase))
                cmd = DetectFallbackBrowserCommand();
            if (string.IsNullOrWhiteSpace(cmd)) return;

            var exe = ExtractExe(cmd);
            string argsPart;
            var trimmed = cmd.Trim();
            if (trimmed.StartsWith('"'))
            {
                int end = trimmed.IndexOf('"', 1);
                argsPart = end > 0 && end + 1 < trimmed.Length ? trimmed[(end + 1)..].Trim() : "\"%1\"";
            }
            else
            {
                int sp = trimmed.IndexOf(' ');
                argsPart = sp > 0 ? trimmed[(sp + 1)..].Trim() : "\"%1\"";
            }

            string finalArgs = argsPart.Contains("%1", StringComparison.Ordinal)
                ? argsPart.Replace("\"%1\"", $"\"{url}\"").Replace("%1", $"\"{url}\"")
                : $"{argsPart} \"{url}\"";

            Process.Start(new ProcessStartInfo(exe, finalArgs) { UseShellExecute = false });
        }
        catch (Exception e) { Paths.Log("link forward: " + e.Message); }
    }

    public static bool IsSystemRouterActive()
    {
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return string.Equals(uc?.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Registers HKCU protocol "fluxtube://" and Windows RegisteredApplication "FluxTube" (zero UAC required!).
    /// Captures the user's current default browser into AppSettings.FallbackBrowserCmd before anything changes.
    /// </summary>
    public static void RegisterProtocolAndRouter()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            if (exe.Contains(@"\verify-", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\local-install\", StringComparison.OrdinalIgnoreCase))
                return;

            // Remember real browser before user ever switches default browser to FluxTube!
            var s = AppSettings.Current;
            if (string.IsNullOrWhiteSpace(s.FallbackBrowserCmd) || s.FallbackBrowserCmd.Contains("FluxTube.exe", StringComparison.OrdinalIgnoreCase))
            {
                var detected = DetectFallbackBrowserCommand();
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    s.FallbackBrowserCmd = detected;
                    s.Save();
                }
            }

            // 1. Custom URI scheme: fluxtube://https://youtu.be/...
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\fluxtube"))
            {
                k.SetValue(null, "URL:FluxTube Protocol");
                k.SetValue("URL Protocol", "");
                using var ic = k.CreateSubKey("DefaultIcon");
                ic.SetValue(null, $"\"{exe}\",0");
                using var cmd = k.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, $"\"{exe}\" --open-url \"%1\"");
            }

            // 2. HTTP/HTTPS YouTube URL Router ProgId (shows up cleanly in Windows Default Apps -> Browser!)
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                k.SetValue(null, "FluxTube (YouTube Router)");
                using var ic = k.CreateSubKey("DefaultIcon");
                ic.SetValue(null, $"\"{exe}\",0");
                using var cmd = k.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, $"\"{exe}\" --route-url \"%1\"");
            }

            // 3. RegisteredApplications + StartMenuInternet so Windows 10/11 Default Apps lists FluxTube cleanly
            using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Clients\StartMenuInternet\FluxTube"))
            {
                app.SetValue(null, "FluxTube");
                using var cap = app.CreateSubKey("Capabilities");
                cap.SetValue("ApplicationName", "FluxTube");
                cap.SetValue("ApplicationDescription", "Открывает ссылки YouTube в FluxTube, а остальные сайты — в вашем обычном браузере");
                using var urls = cap.CreateSubKey("URLAssociations");
                urls.SetValue("http", ProgId);
                urls.SetValue("https", ProgId);
                using var ic = app.CreateSubKey("DefaultIcon");
                ic.SetValue(null, $"\"{exe}\",0");
                using var cmd = app.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, $"\"{exe}\"");
            }

            using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                reg.SetValue("FluxTube", @"Software\Clients\StartMenuInternet\FluxTube\Capabilities");
        }
        catch (Exception e) { Paths.Log("link router reg: " + e.Message); }
    }

    /// <summary>Opens Windows Default Apps directly on FluxTube so the user can enable 1-click Telegram/Discord YouTube interception.</summary>
    public static void OpenDefaultAppsForFluxTube()
    {
        RegisterProtocolAndRouter();
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=FluxTube") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
        }
    }
}
