using System.Globalization;
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
    private double _lastTimePosted = double.NaN;
    private int _loadGen;
    private int _volRampGen;
    private double _volTarget = -1;
    private static Win32.WndProc? _wndProcKeep;

    public double TimePos { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public bool Fullscreen { get; private set; }
    public double Volume { get; private set; } = AppSettings.Current.Volume;
    public bool Muted { get; private set; }
    public double Speed { get; private set; } = 1.0;
    public string? CurrentVideoId { get; private set; }
    public string? CurrentUrl { get; private set; }
    public bool HasMedia => CurrentUrl != null;

    public event Action<double>? TimeChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<bool>? FullscreenChanged;
    public event Action<double>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    public event Action<double>? SpeedChanged;
    public event Action? FileLoaded;
    /// <summary>A click on the player's own controls that the app must handle (next, quality menu, ...).</summary>
    public event Action<string, string>? Message;
    /// <summary>Extra script options, e.g. the icon font for the controls.</summary>
    public string ScriptOpts { get; set; } = "";
    public event Action? PlaybackStarted; // decoder is producing frames (after load or seek)
    public event Action<bool>? EndOfFile; // true = played to end
    public event Action<string>? LoadFailed;

    public bool IsReady => Volatile.Read(ref _ctx) != IntPtr.Zero && !_disposed;

    public IntPtr Host => _host;
    public bool Mute { get; set; }

    /// <summary>hostHwnd: the child window mpv renders into (the shell owns it and its input).</summary>
    public void Init(IntPtr hostHwnd)
    {
        if (Volatile.Read(ref _ctx) != IntPtr.Zero || _disposed) return;
        _host = hostHwnd;

        var ctx = mpv_create();
        if (ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var mpvDir = Path.Combine(baseDir, "Assets", "mpv");
            Opt(ctx, "config-dir", mpvDir);
            Opt(ctx, "config", "yes");
            Opt(ctx, "wid", _host.ToInt64().ToString());
            Opt(ctx, "vo", "gpu-next");
            Opt(ctx, "gpu-api", "d3d11");
            Opt(ctx, "hwdec", "auto-safe");
            Opt(ctx, "idle", "yes");
            Opt(ctx, "input-ipc-server", @"\\.\pipe\fluxtube-mpv-" + Environment.ProcessId);
            Opt(ctx, "force-window", "immediate");
            Opt(ctx, "gpu-shader-cache", "yes");
            Opt(ctx, "gpu-shader-cache-dir", Path.Combine(Paths.Data, "mpv-shaders").Replace('\\', '/'));
            Opt(ctx, "keep-open", "yes");
            Opt(ctx, "osc", "no");          // scripts/fluxosc.lua draws the controls
            Opt(ctx, "osd-bar", "no");
            Opt(ctx, "border", "no");
            Opt(ctx, "input-default-bindings", "yes");
            Opt(ctx, "input-vo-keyboard", "yes");
            Opt(ctx, "cursor-autohide", "1000");
            Opt(ctx, "window-dragging", "no");
            Opt(ctx, "background-color", "#000000");
            Opt(ctx, "osd-font", "Segoe UI");
            Opt(ctx, "osd-font-size", "28");
            Opt(ctx, "demuxer-max-bytes", "12MiB");
            Opt(ctx, "demuxer-max-back-bytes", "4MiB");
            Opt(ctx, "cache", "yes");
            Opt(ctx, "demuxer-seekable-cache", "no");
            Opt(ctx, "volume-max", "150");
            Opt(ctx, "sub-font", "Segoe UI");
            Opt(ctx, "sub-font-size", "40");
            Opt(ctx, "sub-border-size", "2.2");
            Opt(ctx, "sub-shadow-offset", "0");
            Opt(ctx, "sub-margin-y", "64");
            Opt(ctx, "sub-auto", "no");
            Opt(ctx, "ytdl", "yes");
            Opt(ctx, "script-opts", "ytdl_hook-ytdl_path=" + Paths.YtDlp.Replace('\\', '/') + (ScriptOpts.Length > 0 ? "," + ScriptOpts : ""));
            Opt(ctx, "cache-pause-initial", "no");
            Opt(ctx, "cache-pause-wait", "0.4");
            Opt(ctx, "demuxer-readahead-secs", "20");
            Opt(ctx, "network-timeout", "15");
            Opt(ctx, "hr-seek", "yes");
            Opt(ctx, "vd-lavc-threads", "6");
            Opt(ctx, "ad-lavc-threads", "1");
            if (Mute) { Opt(ctx, "mute", "yes"); Muted = true; }
            if (Environment.GetEnvironmentVariable("FLUX_MPV_LOG") is { Length: > 0 } logFile) { Opt(ctx, "log-file", logFile); Opt(ctx, "msg-level", "all=v"); }

            ApplySettingsTo(ctx);
            Check(mpv_initialize(ctx), "mpv_initialize");

            mpv_observe_property(ctx, 1, "time-pos", Format.Double);
            mpv_observe_property(ctx, 2, "duration", Format.Double);
            mpv_observe_property(ctx, 3, "pause", Format.Flag);
            mpv_observe_property(ctx, 4, "fullscreen", Format.Flag);
            mpv_observe_property(ctx, 5, "eof-reached", Format.Flag);
            mpv_observe_property(ctx, 6, "volume", Format.Double);
            mpv_observe_property(ctx, 7, "mute", Format.Flag);
            mpv_observe_property(ctx, 8, "speed", Format.Double);

            // Publish _ctx only after full initialization so other threads never touch an uninitialized handle
            Volatile.Write(ref _ctx, ctx);
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
            _eventThread.Start();
        }
        catch
        {
            mpv_terminate_destroy(ctx);
            throw;
        }
    }

    private void ApplySettingsTo(IntPtr ctx)
    {
        var s = AppSettings.Current;
        var vol = Math.Clamp(s.Volume, 0, 150);
        Volume = vol;
        mpv_set_option_string(ctx, "ytdl-format", s.PlayerFormat);
        mpv_set_option_string(ctx, "volume", vol.ToString(CultureInfo.InvariantCulture));
        mpv_set_option_string(ctx, "ytdl-raw-options", "");
        foreach (var kv in YtDlp.CommonRawOptions(forPlayer: true))
            mpv_set_option_string(ctx, "ytdl-raw-options-append", kv);
        mpv_set_option_string(ctx, "http-proxy", string.IsNullOrWhiteSpace(s.Proxy) ? "" : s.Proxy);
        mpv_set_option_string(ctx, "af", s.PlayerAudio8D ? Audio8DFilter : "");
    }

    private const string Audio8DFilter = "lavfi=[apulsator=hz=0.14:width=1:amount=0.85,aecho=0.82:0.84:48|82:0.28|0.18]";

    /// <summary>Pushes current settings (quality, cookies, proxy, 8D audio...) into mpv asynchronously.</summary>
    public void ApplySettings()
    {
        if (!IsReady) return;
        var s = AppSettings.Current;
        var vol = Math.Clamp(s.Volume, 0, 150);
        Volume = vol;
        Set("ytdl-format", s.PlayerFormat);
        Set("volume", vol.ToString(CultureInfo.InvariantCulture));
        Set("ytdl-raw-options", "");
        foreach (var kv in YtDlp.CommonRawOptions(forPlayer: true))
            Command("set", "ytdl-raw-options-append", kv);
        Set("http-proxy", string.IsNullOrWhiteSpace(s.Proxy) ? "" : s.Proxy);
        Set("af", s.PlayerAudio8D ? Audio8DFilter : "");
    }

    /// <summary>Smoothly ramps mpv volume to target (0..150) over ~100ms instead of a harsh step jump.</summary>
    public void SetVolumeSmooth(double target, int durationMs = 95)
    {
        target = Math.Clamp(Math.Round(target), 0, 150);
        var start = _volTarget >= 0 ? Volume : Volume;
        _volTarget = target;
        if (!IsReady)
        {
            Volume = target;
            return;
        }
        if (target > 0 && Muted)
        {
            Muted = false;
            Set("mute", "no");
        }
        var delta = target - start;
        if (Math.Abs(delta) <= 2.5 || durationMs <= 0)
        {
            Interlocked.Increment(ref _volRampGen);
            Volume = target;
            Set("volume", target.ToString("0.#", CultureInfo.InvariantCulture));
            return;
        }
        var gen = Interlocked.Increment(ref _volRampGen);
        int steps = Math.Clamp(durationMs / 16, 3, 8);
        _ = Task.Run(async () =>
        {
            for (int i = 1; i <= steps; i++)
            {
                if (gen != Volatile.Read(ref _volRampGen) || !IsReady) return;
                double t = (double)i / steps;
                double ease = 1.0 - (1.0 - t) * (1.0 - t); // quad ease-out
                double v = i == steps ? target : Math.Round(start + delta * ease, 1);
                Volume = v;
                Command("set", "volume", v.ToString("0.#", CultureInfo.InvariantCulture));
                if (i < steps) await Task.Delay(16);
            }
        });
    }

    public void Load(string url, string? videoId, double startSeconds = 0)
    {
        if (!IsReady) return;
        Interlocked.Increment(ref _loadGen);
        CurrentUrl = url;
        CurrentVideoId = videoId;
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
        var opts = startSeconds > 0.3 ? $"start={startSeconds.ToString("0.00", CultureInfo.InvariantCulture)}" : "start=0";
        Command("loadfile", url, "replace", "-1", opts);
        Set("pause", "no");
    }

    /// <summary>Plays already-resolved googlevideo URLs directly: no yt-dlp on the critical path.</summary>
    public void LoadStream(StreamInfo s, string? title, double startSeconds, bool keepPause = false)
    {
        if (!IsReady) return;
        Interlocked.Increment(ref _loadGen);
        CurrentUrl = s.Video;
        CurrentVideoId = s.VideoId;
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
        var opts = new List<string> { "start=" + (startSeconds > 0.3 ? startSeconds.ToString("0.00", CultureInfo.InvariantCulture) : "0") };
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
        Interlocked.Increment(ref _loadGen);
        Command("stop");
        CurrentUrl = null; CurrentVideoId = null;
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
    }

    public void TogglePause() => Command("cycle", "pause");
    public void SetPause(bool p) => Set("pause", p ? "yes" : "no");
    public void SeekAbsolute(double s) => Command("seek", s.ToString(CultureInfo.InvariantCulture), "absolute");
    public void ShowText(string text, int ms = 1800) => Command("show-text", text, ms.ToString());
    public void SetSpeed(double v) { Speed = v; Set("speed", v.ToString(CultureInfo.InvariantCulture)); }

    public double GetDouble(string name)
    {
        var ctx = Volatile.Read(ref _ctx);
        if (ctx == IntPtr.Zero || _disposed) return 0;
        return mpv_get_property(ctx, name, Format.Double, out double v) >= 0 ? v : 0;
    }

    public string? GetString(string name)
    {
        var ctx = Volatile.Read(ref _ctx);
        if (ctx == IntPtr.Zero || _disposed) return null;
        var p = mpv_get_property_string(ctx, name);
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

    private static void Opt(IntPtr ctx, string n, string v) => mpv_set_option_string(ctx, n, v);
    private void Set(string n, string v) => Command("set", n, v);

    public void Command(params string[] args)
    {
        var ctx = Volatile.Read(ref _ctx);
        if (ctx == IntPtr.Zero || _disposed) return;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            mpv_command_async(ctx, 0, ptrs);
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
        bool volSeen = false, muteSeen = false, speedSeen = false;
        while (!_disposed)
        {
            var ctx = Volatile.Read(ref _ctx);
            if (ctx == IntPtr.Zero) return;
            var evPtr = mpv_wait_event(ctx, 0.5);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            switch (ev.event_id)
            {
                case EventId.None: break;
                case EventId.Shutdown: return;
                case EventId.FileLoaded:
                {
                    var gen = Volatile.Read(ref _loadGen);
                    Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) FileLoaded?.Invoke(); });
                    break;
                }
                case EventId.PlaybackRestart:
                {
                    var gen = Volatile.Read(ref _loadGen);
                    Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) PlaybackStarted?.Invoke(); });
                    break;
                }
                case EventId.ClientMessage:
                {
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
                        var gen = Volatile.Read(ref _loadGen);
                        var msg = PtrToUtf8(mpv_error_string(ef.error)) ?? "error";
                        Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) LoadFailed?.Invoke(msg); });
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
                            if (!double.IsNaN(_lastTimePosted) && Math.Abs(v - _lastTimePosted) < 0.25) break;
                            _lastTimePosted = v;
                            var gen = Volatile.Read(ref _loadGen);
                            Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) TimeChanged?.Invoke(v); });
                            break;
                        }
                        case 2:
                        {
                            var v = Marshal.PtrToStructure<double>(p.data);
                            Duration = v;
                            var gen = Volatile.Read(ref _loadGen);
                            Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) DurationChanged?.Invoke(v); });
                            break;
                        }
                        case 3:
                        {
                            var v = Marshal.ReadInt32(p.data) != 0;
                            Paused = v;
                            Ui(() => PauseChanged?.Invoke(v));
                            break;
                        }
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
                        case 5:
                            if (Marshal.ReadInt32(p.data) != 0)
                            {
                                var gen = Volatile.Read(ref _loadGen);
                                Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) EndOfFile?.Invoke(true); });
                            }
                            break;
                        case 6:
                        {
                            var v = Marshal.PtrToStructure<double>(p.data);
                            Volume = v;
                            if (volSeen) Ui(() => VolumeChanged?.Invoke(v));
                            volSeen = true;
                            break;
                        }
                        case 7:
                        {
                            var v = Marshal.ReadInt32(p.data) != 0;
                            Muted = v;
                            if (muteSeen) Ui(() => MuteChanged?.Invoke(v));
                            muteSeen = true;
                            break;
                        }
                        case 8:
                        {
                            var v = Marshal.PtrToStructure<double>(p.data);
                            Speed = v;
                            if (speedSeen) Ui(() => SpeedChanged?.Invoke(v));
                            speedSeen = true;
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
        var ctx = Interlocked.Exchange(ref _ctx, IntPtr.Zero);
        if (ctx != IntPtr.Zero)
        {
            mpv_wakeup(ctx);
            _eventThread?.Join(1500);
            mpv_terminate_destroy(ctx);
        }
    }
}
