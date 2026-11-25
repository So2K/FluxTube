using System.Runtime.InteropServices;
using FluxTube.Services;
using static FluxTube.Player.MpvNative;

namespace FluxTube.Player;

/// <summary>
/// One libmpv instance rendering into a Win32 child window of the main window.
/// mpv's own OSC draws the controls, so the video area needs no XAML overlays.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    public static MpvPlayer Instance { get; } = new();

    private IntPtr _ctx;
    private IntPtr _host;
    private Thread? _eventThread;
    private volatile bool _disposed;
    private double _lastTimePosted;
    private static Win32.WndProc? _wndProcKeep;

    public double TimePos { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public bool Fullscreen { get; private set; }
    public string? CurrentVideoId { get; private set; }
    public string? CurrentUrl { get; private set; }
    public bool HasMedia => CurrentUrl != null;

    public event Action<double>? TimeChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<bool>? FullscreenChanged;
    public event Action? FileLoaded;
    /// <summary>A click on the player's own controls that the app must handle (next, quality menu, ...).</summary>
    public event Action<string, string>? Message;
    /// <summary>Extra script options, e.g. the icon font for the controls.</summary>
    public string ScriptOpts { get; set; } = "";
    public event Action? PlaybackStarted; // decoder is producing frames (after load or seek)
    public event Action<bool>? EndOfFile; // true = played to end
    public event Action<string>? LoadFailed;

    public bool IsReady => _ctx != IntPtr.Zero;

    public IntPtr Host => _host;
    public bool Mute { get; set; }

    /// <summary>hostHwnd: the child window mpv renders into (the shell owns it and its input).</summary>
    public void Init(IntPtr hostHwnd)
    {
        if (_ctx != IntPtr.Zero) return;
        _host = hostHwnd;

        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

        var baseDir = AppContext.BaseDirectory;
        var mpvDir = Path.Combine(baseDir, "Assets", "mpv");
        Opt("config-dir", mpvDir);
        Opt("config", "yes");
        Opt("wid", _host.ToInt64().ToString());
        Opt("vo", "gpu-next");
        Opt("gpu-api", "d3d11");
        Opt("hwdec", "auto-safe");
        Opt("idle", "yes");
        Opt("input-ipc-server", @"\\.\pipe\fluxtube-mpv-" + Environment.ProcessId); // diagnostics: query state from scripts
        // "immediate": the GPU output (~90 ms to set up) is created with the player, not on the first video
        Opt("force-window", "immediate");
        Opt("gpu-shader-cache", "yes");
        Opt("gpu-shader-cache-dir", Path.Combine(Paths.Data, "mpv-shaders").Replace('\\', '/'));
        Opt("keep-open", "yes");
        Opt("osc", "no");          // scripts/fluxosc.lua draws the controls
        Opt("osd-bar", "no");
        Opt("border", "no");
        Opt("input-default-bindings", "yes");
        Opt("input-vo-keyboard", "yes");
        Opt("cursor-autohide", "1000");
        // mpv would start dragging its window once a held mouse button moves three pixels, and let go of the button
        // for our controls: the volume slider and the seek bar could be clicked but not dragged
        Opt("window-dragging", "no");
        Opt("background-color", "#000000");
        Opt("osd-font", "Segoe UI");
        Opt("osd-font-size", "28");
        // ~1-2 min of 1080p ahead is plenty with 30+ MB/s chunks; keeps memory low (audio-file adds a second demuxer)
        Opt("demuxer-max-bytes", "12MiB");
        Opt("demuxer-max-back-bytes", "4MiB");
        Opt("cache", "yes");
        // no in-memory seek ranges: with YouTube's fragmented mp4 a seek back makes lavf scan the fragment headers to
        // the end, mpv then joins the new range with the old one and every later seek into the gap lands back at the
        // old position. Seeking again is cheap anyway: the proxy serves played parts from disk.
        Opt("demuxer-seekable-cache", "no");
        Opt("volume-max", "150");
        // subtitles: readable on any picture, above the controls
        Opt("sub-font", "Segoe UI");
        Opt("sub-font-size", "40");
        Opt("sub-border-size", "2.2");
        Opt("sub-shadow-offset", "0");
        Opt("sub-margin-y", "64");
        Opt("sub-auto", "no");
        Opt("ytdl", "yes");
        Opt("script-opts", "ytdl_hook-ytdl_path=" + Paths.YtDlp.Replace('\\', '/') + (ScriptOpts.Length > 0 ? "," + ScriptOpts : ""));
        // fast start: play as soon as the first packets arrive, no initial buffering pause
        Opt("cache-pause-initial", "no");
        Opt("cache-pause-wait", "0.4");
        Opt("demuxer-readahead-secs", "20");
        Opt("network-timeout", "15");
        Opt("hr-seek", "yes");
        // decoding is on the GPU; a software fallback gets a few threads, not one per core (32 threads cost memory for nothing)
        Opt("vd-lavc-threads", "6");
        Opt("ad-lavc-threads", "1");
        if (Mute) Opt("mute", "yes");
        if (Environment.GetEnvironmentVariable("FLUX_MPV_LOG") is { Length: > 0 } logFile) { Opt("log-file", logFile); Opt("msg-level", "all=v"); }
        ApplySettings();

        Check(mpv_initialize(_ctx), "mpv_initialize");

        mpv_observe_property(_ctx, 1, "time-pos", Format.Double);
        mpv_observe_property(_ctx, 2, "duration", Format.Double);
        mpv_observe_property(_ctx, 3, "pause", Format.Flag);
        mpv_observe_property(_ctx, 4, "fullscreen", Format.Flag);
        // with keep-open the player pauses on the last frame and sends no end-file: this flag is how the end shows
        mpv_observe_property(_ctx, 5, "eof-reached", Format.Flag);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    /// <summary>Pushes current settings (quality, cookies, proxy...) into mpv.</summary>
    public void ApplySettings()
    {
        if (_ctx == IntPtr.Zero) return;
        var s = AppSettings.Current;
        Set("ytdl-format", s.PlayerFormat);
        Set("volume", s.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("ytdl-raw-options", "");
        foreach (var kv in YtDlp.CommonRawOptions(forPlayer: true))
            Set("ytdl-raw-options-append", kv);
        Set("http-proxy", string.IsNullOrWhiteSpace(s.Proxy) ? "" : s.Proxy);
    }

    public void Load(string url, string? videoId, double startSeconds = 0)
    {
        if (_ctx == IntPtr.Zero) return;
        CurrentUrl = url;
        CurrentVideoId = videoId;
        TimePos = 0; Duration = 0;
        var opts = startSeconds > 0.3 ? $"start={startSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}" : "start=0";
        Command("loadfile", url, "replace", "-1", opts);
        Set("pause", "no");
    }

    /// <summary>Plays already-resolved googlevideo URLs directly: no yt-dlp on the critical path.</summary>
    public void LoadStream(StreamInfo s, string? title, double startSeconds, bool keepPause = false)
    {
        if (_ctx == IntPtr.Zero) return;
        CurrentUrl = s.Video;
        CurrentVideoId = s.VideoId;
        TimePos = 0; Duration = 0;
        var opts = new List<string> { "start=" + (startSeconds > 0.3 ? startSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "0") };
        if (s.Audio != null) opts.Add("audio-file=" + Escaped(s.Audio));
        var t = string.IsNullOrEmpty(title) ? s.Title : title;
        if (!string.IsNullOrEmpty(t)) opts.Add("force-media-title=" + Escaped(t));
        Command("loadfile", s.Video, "replace", "-1", string.Join(',', opts));
        if (!keepPause) Set("pause", "no");
    }

    /// <summary>mpv's %bytes% quoting, so commas inside URLs and titles survive the option list.</summary>
    private static string Escaped(string v) => $"%{System.Text.Encoding.UTF8.GetByteCount(v)}%{v}";

    public void Stop()
    {
        Command("stop");
        CurrentUrl = null; CurrentVideoId = null;
        TimePos = 0; Duration = 0;
    }

    public void TogglePause() => Command("cycle", "pause");
    public void SetPause(bool p) => Set("pause", p ? "yes" : "no");
    public void SeekAbsolute(double s) => Command("seek", s.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
    public void ShowText(string text, int ms = 1800) => Command("show-text", text, ms.ToString());
    public void SetSpeed(double v) => Set("speed", v.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public double GetDouble(string name)
    {
        if (_ctx == IntPtr.Zero) return 0;
        return mpv_get_property(_ctx, name, Format.Double, out double v) >= 0 ? v : 0;
    }

    public string? GetString(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        var p = mpv_get_property_string(_ctx, name);
        if (p == IntPtr.Zero) return null;
        var s = PtrToUtf8(p);
        mpv_free(p);
        return s;
    }

    public void SetFullscreenFlag(bool v) => Set("fullscreen", v ? "yes" : "no");

    // ---- host window ----

    public void SetBounds(int x, int y, int w, int h)
    {
        if (_host == IntPtr.Zero) return;
        // HWND_TOP every time: WinUI's content bridge is a sibling child window and must stay below the video
        Win32.SetWindowPos(_host, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h), Win32.SWP_NOACTIVATE);
    }

    public void ShowVideo(bool visible)
    {
        if (_host == IntPtr.Zero) return;
        Win32.ShowWindow(_host, visible ? Win32.SW_SHOWNA : Win32.SW_HIDE);
        if (visible) Win32.SetWindowPos(_host, IntPtr.Zero, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public void FocusVideo()
    {
        if (_host != IntPtr.Zero) Win32.SetFocus(_host);
    }

    public static IntPtr CreateHostWindow(IntPtr parent)
    {
        _wndProcKeep = (h, m, w, l) => Win32.DefWindowProcW(h, m, w, l);
        var hInst = Win32.GetModuleHandleW(null);
        var wc = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeep),
            hInstance = hInst,
            hbrBackground = Win32.GetStockObject(4 /*BLACK_BRUSH*/),
            lpszClassName = "FluxTubeVideoHost",
            hCursor = Win32.LoadCursorW(IntPtr.Zero, 32512),
        };
        Win32.RegisterClassExW(ref wc);
        var hwnd = Win32.CreateWindowExW(0, "FluxTubeVideoHost", "video",
            Win32.WS_CHILD | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS,
            0, 0, 16, 9, parent, IntPtr.Zero, hInst, IntPtr.Zero);
        return hwnd;
    }

    // ---- plumbing ----

    private void Opt(string n, string v) => mpv_set_option_string(_ctx, n, v);
    private void Set(string n, string v) { if (_ctx != IntPtr.Zero) mpv_set_property_string(_ctx, n, v); }

    public void Command(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            mpv_command_async(_ctx, 0, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(ptrs[i]);
        }
    }

    private static void Check(int err, string what)
    {
        if (err < 0) throw new InvalidOperationException($"{what}: {PtrToUtf8(mpv_error_string(err))}");
    }

    private void Ui(Action a) => UiThread.Post(a);

    private void EventLoop()
    {
        while (!_disposed)
        {
            var evPtr = mpv_wait_event(_ctx, 0.5);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            switch (ev.event_id)
            {
                case EventId.None: break;
                case EventId.Shutdown: return;
                case EventId.FileLoaded: Ui(() => FileLoaded?.Invoke()); break;
                case EventId.PlaybackRestart: Ui(() => PlaybackStarted?.Invoke()); break;
                case EventId.ClientMessage:
                {
                    // mpv_event_client_message { int num_args; const char** args; }
                    var n = Marshal.ReadInt32(ev.data);
                    var argv = Marshal.ReadIntPtr(ev.data, IntPtr.Size);
                    if (n >= 2 && PtrToUtf8(Marshal.ReadIntPtr(argv)) == "flux")
                    {
                        var what = PtrToUtf8(Marshal.ReadIntPtr(argv, IntPtr.Size)) ?? "";
                        var arg = n >= 3 ? PtrToUtf8(Marshal.ReadIntPtr(argv, IntPtr.Size * 2)) ?? "" : "";
                        Ui(() => Message?.Invoke(what, arg));
                    }
                    break;
                }
                case EventId.EndFile:
                {
                    var ef = Marshal.PtrToStructure<MpvEventEndFile>(ev.data);
                    if (ef.reason == 4)
                    {
                        var msg = PtrToUtf8(mpv_error_string(ef.error)) ?? "error";
                        Ui(() => LoadFailed?.Invoke(msg));
                    }
                    break;
                }
                case EventId.PropertyChange:
                {
                    var p = Marshal.PtrToStructure<MpvEventProperty>(ev.data);
                    if (p.data == IntPtr.Zero) break;
                    switch (ev.reply_userdata)
                    {
                        case 1:
                        {
                            var v = Marshal.PtrToStructure<double>(p.data);
                            TimePos = v;
                            // mpv reports every frame; the UI needs a few updates a second
                            if (Math.Abs(v - _lastTimePosted) < 0.25) break;
                            _lastTimePosted = v;
                            Ui(() => TimeChanged?.Invoke(v));
                            break;
                        }
                        case 2:
                        {
                            var v = Marshal.PtrToStructure<double>(p.data);
                            Duration = v;
                            Ui(() => DurationChanged?.Invoke(v));
                            break;
                        }
                        case 3:
                        {
                            var v = Marshal.ReadInt32(p.data) != 0;
                            Paused = v;
                            Ui(() => PauseChanged?.Invoke(v));
                            break;
                        }
                        case 5:
                            if (Marshal.ReadInt32(p.data) != 0) Ui(() => EndOfFile?.Invoke(true));
                            break;
                        case 4:
                        {
                            var v = Marshal.ReadInt32(p.data) != 0;
                            if (v != Fullscreen)
                            {
                                Fullscreen = v;
                                Ui(() => FullscreenChanged?.Invoke(v));
                            }
                            break;
                        }
                    }
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ctx != IntPtr.Zero)
        {
            mpv_wakeup(_ctx);
            _eventThread?.Join(1500);
            mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
