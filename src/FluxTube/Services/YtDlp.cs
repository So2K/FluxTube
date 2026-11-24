using System.Diagnostics;

namespace FluxTube.Services;

public static class YtDlp
{
    /// <summary>Options shared by the player (mpv ytdl_hook) and the downloader, as key=value pairs.</summary>
    public static IEnumerable<string> CommonRawOptions(bool forPlayer)
    {
        var s = AppSettings.Current;
        if (File.Exists(Paths.Cookies) && CookieStore.IsLoggedIn) yield return "cookies=" + Paths.Cookies;
        // Chrome TLS impersonation costs ~3 s per YouTube extraction and YouTube does not need it; other sites (TikTok) do benefit
        if (s.Impersonate && !forPlayer) yield return "impersonate=chrome";
        if (!string.IsNullOrWhiteSpace(s.Proxy)) yield return "proxy=" + s.Proxy;
        if (forPlayer && s.MarkWatched && CookieStore.IsLoggedIn) yield return "mark-watched=";
        if (Paths.Ffmpeg != null) yield return "ffmpeg-location=" + Paths.Ffmpeg;
        // yt-dlp's automatic Deno discovery can fail on blocked Windows mount points in PATH.
        if (Paths.Deno is { } deno) yield return "js-runtimes=deno:" + deno;
    }

    /// <summary>Same options as command-line arguments.</summary>
    public static IEnumerable<string> CommonArgs(bool forPlayer)
    {
        foreach (var kv in CommonRawOptions(forPlayer))
        {
            var i = kv.IndexOf('=');
            yield return "--" + kv[..i];
            var v = kv[(i + 1)..];
            if (v.Length > 0) yield return v;
        }
    }

    /// <summary>Daily self-update in the background. The bundled copy lives in a writable folder for that.</summary>
    public static async Task UpdateIfStaleAsync()
    {
        var s = AppSettings.Current;
        if ((DateTime.UtcNow - s.LastYtDlpUpdate).TotalHours < 20) return;
        try
        {
            var psi = new ProcessStartInfo(Paths.YtDlp, "-U")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            Paths.Log("yt-dlp -U: " + output.Trim().Replace('\n', ' '));
            s.LastYtDlpUpdate = DateTime.UtcNow;
            s.Save();
        }
        catch (Exception e) { Paths.Log("yt-dlp update failed: " + e.Message); }
    }
}
