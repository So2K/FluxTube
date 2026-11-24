namespace FluxTube.Services;

public static class Paths
{
    /// <summary>
    /// %USERPROFILE%\.fluxtube, not AppData: packaged launchers virtualize AppData, which would split
    /// the login and history between "started from a terminal" and "started from the shortcut".
    /// </summary>
    public static string Data { get; } = Dir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fluxtube"));
    public static string YtCache => Dir(Path.Combine(Data, "ytcache"));
    public static string PyEnv => Path.Combine(Data, "py");
    public static string PyExe => Path.Combine(PyEnv, "Scripts", "python.exe");
    public static string Cookies => Path.Combine(Data, "cookies.txt");
    public static string Database => Path.Combine(Data, "fluxtube.db");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string WebViewData => Dir(Path.Combine(Data, "WebView2"));
    public static string Logs => Dir(Path.Combine(Data, "logs"));

    /// <summary>Writable yt-dlp copy so it can self-update (YouTube changes often).</summary>
    public static string YtDlp
    {
        get
        {
            var local = Path.Combine(Data, "yt-dlp.exe");
            if (!File.Exists(local))
            {
                var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
                if (File.Exists(bundled)) File.Copy(bundled, local);
            }
            return local;
        }
    }

    private static string? _ffmpeg;
    public static string? Ffmpeg => _ffmpeg ??= FindOnPath("ffmpeg.exe");
    private static string? _deno;
    public static string? Deno => _deno ??= FindOnPath("deno.exe");

    public static string? FindOnPath(string exe)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", exe);
        if (File.Exists(bundled)) return bundled;
        // The packaged WinUI helper lives in web/ and shares the native app's tools.
        var baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Path.GetFileName(baseDirectory).Equals("web", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(baseDirectory) is { } installDirectory &&
            File.Exists(Path.Combine(installDirectory, "FluxTube.exe")))
        {
            var sharedTool = Path.Combine(installDirectory, "tools", exe);
            if (File.Exists(sharedTool)) return sharedTool;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private static string Dir(string p) { Directory.CreateDirectory(p); return p; }

    /// <summary>One-time move from the first builds' %LOCALAPPDATA%\FluxTube (login, history, settings, web profile).</summary>
    public static void MigrateOldData()
    {
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxTube");
            if (!Directory.Exists(old) || File.Exists(Database)) return;
            foreach (var name in new[] { "cookies.txt", "settings.json", "fluxtube.db", "fluxtube.db-wal", "fluxtube.db-shm" })
            {
                var src = Path.Combine(old, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(Data, name), overwrite: false);
            }
            var web = Path.Combine(old, "WebView2");
            if (Directory.Exists(web)) CopyDir(web, WebViewData);
        }
        catch (Exception e) { Log("migrate: " + e.Message); }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false); } catch { }
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Logs, "app.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}"); }
        catch { }
    }
}
