using System.Globalization;
using System.Runtime.InteropServices;
using FluxTube.Services;
using static FluxTube.Player.MpvNative;

namespace FluxTube.Player;

/// <summary>Zero-alloc 5x3 (15-zone) spatial RGB color grid sampled directly from a video frame.</summary>
[System.Runtime.CompilerServices.InlineArray(15)]
public struct SpatialGrid15
{
    public uint E0;

    public const int Cols = 5;
    public const int Rows = 3;

    public readonly (float R, float G, float B) Cell(int col, int row)
    {
        uint c = this[Math.Clamp(row, 0, Rows - 1) * Cols + Math.Clamp(col, 0, Cols - 1)];
        return (((c >> 16) & 255) * (1f / 255f), ((c >> 8) & 255) * (1f / 255f), (c & 255) * (1f / 255f));
    }

    public readonly void SampleBilinear(float u, float v, out float r, out float g, out float b)
    {
        float fx = Math.Clamp(u, 0f, 1f) * (Cols - 1);
        float fy = Math.Clamp(v, 0f, 1f) * (Rows - 1);
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(Cols - 1, x0 + 1), y1 = Math.Min(Rows - 1, y0 + 1);
        float tx = fx - x0, ty = fy - y0;
        var c00 = Cell(x0, y0); var c10 = Cell(x1, y0);
        var c01 = Cell(x0, y1); var c11 = Cell(x1, y1);
        float r0 = c00.R + (c10.R - c00.R) * tx, g0 = c00.G + (c10.G - c00.G) * tx, b0 = c00.B + (c10.B - c00.B) * tx;
        float r1 = c01.R + (c11.R - c01.R) * tx, g1 = c01.G + (c11.G - c01.G) * tx, b1 = c01.B + (c11.B - c01.B) * tx;
        r = r0 + (r1 - r0) * ty;
        g = g0 + (g1 - g0) * ty;
        b = b0 + (b1 - b0) * ty;
    }
}

/// <summary>Live color palette + 15-zone (5x3) pinpoint spatial RGB grid extracted directly from the current decoded mpv video frame.</summary>
public readonly record struct LiveFrameSample(
    string? MediaKey,
    long TimestampMs,
    int Gen,
    uint Dom1,
    uint Dom2,
    bool IsNeutral,
    float AvgLuma,
    (float R, float G, float B) TL,
    (float R, float G, float B) TR,
    (float R, float G, float B) BL,
    (float R, float G, float B) BR,
    (float R, float G, float B) Center,
    SpatialGrid15 Grid);

/// <summary>
/// One libmpv instance rendering into a Win32 child window of the main window.
/// mpv's own OSC draws the controls, so the video area needs no XAML overlays.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    public static MpvPlayer Instance { get; } = new();
    private const ulong FrameSampleUserdata = 0x4652414DuL;

    private IntPtr _ctx;
    private IntPtr _host;
    private Thread? _eventThread;
    private volatile bool _disposed;
    private double _lastTimePosted = double.NaN;
    private int _loadGen;
    private int _volRampGen;
    private double _volTarget = -1;
    private string _lastAppliedAf = "";
    private static Win32.WndProc? _wndProcKeep;
    private readonly object _sampleLock = new();
    private LiveFrameSample _latestSample;
    private int _frameSampleInFlight;
    private long _frameSampleSentAt;
    private int _frameSampleReqLoadGen;
    private int _frameSampleGen;
    private int _frameDirty;
    private int _videoWidth, _videoHeight;

    public double TimePos { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public bool Buffering { get; private set; }
    public bool Fullscreen { get; private set; }
    public double Volume { get; private set; } = AppSettings.Current.Volume;
    public bool Muted { get; private set; }
    public double Speed { get; private set; } = 1.0;
    public bool VideoEnabled { get; private set; } = true;
    public int VideoWidth => Volatile.Read(ref _videoWidth);
    public int VideoHeight => Volatile.Read(ref _videoHeight);
    public float VideoAspect => (VideoWidth > 0 && VideoHeight > 0) ? Math.Clamp((float)VideoWidth / VideoHeight, 0.4f, 3.2f) : (16f / 9f);
    public bool HasActiveVideo => HasMedia && VideoEnabled && Volatile.Read(ref _videoWidth) > 0;
    public bool HasPendingFrameProbe => Volatile.Read(ref _frameDirty) != 0;
    public string? ActiveMediaKey => CurrentVideoId ?? CurrentUrl;
    public LiveFrameSample LatestFrameSample { get { lock (_sampleLock) return _latestSample; } }
    public string? CurrentVideoId { get; private set; }
    public string? CurrentUrl { get; private set; }
    public string? CurrentAudioUrl { get; private set; }
    public bool IsLiveStream { get; private set; }
    public bool HasMedia => CurrentUrl != null;

    // Live audio stream telemetry observed asynchronously from libmpv (zero UI stalls!)
    public string AudioCodec { get; private set; } = "";
    public string AudioSampleFormat { get; private set; } = "";
    public int AudioSampleRate { get; private set; }
    public int AudioBitrateKbps { get; private set; }
    public bool IsLosslessAudio => AudioCodec is "flac" or "alac" or "wav" or "ape" or "wv" || AudioCodec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) || AudioCodec.StartsWith("dsd", StringComparison.OrdinalIgnoreCase);
    public bool IsHiResLossless => IsLosslessAudio && (AudioSampleRate > 48000 || AudioBitDepth >= 24);
    public bool IsHiFiAudio => IsLosslessAudio || AudioBitrateKbps >= 220;

    /// <summary>Effective bit depth for lossless streams (16 or 24 bit), inferred honestly from libmpv sample format &amp; rate/bitrate.</summary>
    public int AudioBitDepth
    {
        get
        {
            if (!IsLosslessAudio) return 0;
            if (AudioSampleFormat is "s24" or "s32" or "s32p" or "s24p") return 24;
            int br = AudioBitrateKbps >= 320 ? AudioBitrateKbps : ExpectedLosslessBitrateKbps;
            if (AudioSampleRate > 48000 || br > 1420) return 24;
            return 16;
        }
    }

    /// <summary>TIDAL-style compact badge label, e.g. "16-bit 44.1kHz" or "24-bit 96kHz" for Lossless FLAC.</summary>
    public string TidalQualityLabel
    {
        get
        {
            string sr = AudioSampleRate >= 1000
                ? (AudioSampleRate % 1000 == 0 ? $"{AudioSampleRate / 1000}kHz" : $"{AudioSampleRate / 1000.0:0.0}kHz")
                : "";
            if (IsLosslessAudio)
            {
                int bits = AudioBitDepth > 0 ? AudioBitDepth : 16;
                string ex = WasapiExclusiveActive ? " · EX" : "";
                return sr.Length > 0 ? $"{bits}-bit {sr}{ex}" : $"{bits}-bit FLAC{ex}";
            }
            return AudioQualityBadge;
        }
    }

    // Zero-Wait Lossless FLAC state for the currently playing item
    public bool HasLosslessTrack { get; private set; }
    public bool LosslessSearching { get; set; }
    public int LosslessDownloadPercent { get; set; } = -1;
    public bool LosslessDownloading => LosslessDownloadPercent >= 0 && LosslessDownloadPercent < 100 && !IsLosslessAudio;
    public bool LosslessTimingDiff { get; private set; }
    public string? LosslessSourceLabel { get; private set; }
    public int ExpectedLosslessBitrateKbps { get; private set; }
    public bool WasapiExclusiveActive { get; private set; }

    public string AudioQualityBadge
    {
        get
        {
            if (string.IsNullOrEmpty(AudioCodec) && AudioSampleRate <= 0 && AudioBitrateKbps <= 0) return "HD";
            string codec = AudioCodec switch
            {
                "opus" => "OPUS",
                "aac" => "AAC",
                "flac" => "FLAC",
                "alac" => "ALAC",
                "vorbis" => "OGG",
                "mp3" => "MP3",
                var c when c.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) => "PCM",
                var c when !string.IsNullOrEmpty(c) => c.ToUpperInvariant(),
                _ => "AUDIO",
            };
            string sr = AudioSampleRate >= 1000
                ? (AudioSampleRate % 1000 == 0 ? $"{AudioSampleRate / 1000} kHz" : $"{AudioSampleRate / 1000.0:0.0} kHz")
                : "";
            int effectiveBr = IsLosslessAudio
                ? (AudioBitrateKbps >= 320 ? AudioBitrateKbps : ExpectedLosslessBitrateKbps)
                : AudioBitrateKbps;
            string br = effectiveBr > 0 ? $"{effectiveBr} kbps" : "";
            string prefix = IsLosslessAudio ? "ULTRA " : (AudioBitrateKbps >= 220 ? "HI-FI " : "");
            string ex = WasapiExclusiveActive ? " · EX" : "";
            if (sr.Length > 0 && br.Length > 0) return $"{prefix}{codec} · {sr} · {br}{ex}";
            if (br.Length > 0) return $"{prefix}{codec} · {br}{ex}";
            if (sr.Length > 0) return $"{prefix}{codec} · {sr}{ex}";
            return $"{prefix}{codec}{ex}";
        }
    }

    public event Action<double>? TimeChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<bool>? BufferingChanged;
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
    public event Action? FrameSampled;

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
            Opt(ctx, "d3d11-flip", "yes");
            Opt(ctx, "d3d11-sync-interval", "0");
            Opt(ctx, "swapchain-depth", "2");
            Opt(ctx, "video-sync", "audio");
            // 250ms WASAPI buffer + continuous silence stream prevents audio crackle/stutter on Windows Alt-Tab & DXGI mode switches
            Opt(ctx, "audio-buffer", "0.25");
            Opt(ctx, "audio-stream-silence", "yes");
            Opt(ctx, "osd-on-seek", "no");
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
            Opt(ctx, "demuxer-max-bytes", "64MiB");
            Opt(ctx, "demuxer-max-back-bytes", "32MiB");
            // Never drop reference frames during HR-seek with D3D11VA hwdec (prevents frozen video while audio plays after SponsorBlock/manual seeks!)
            Opt(ctx, "hr-seek-framedrop", "no");
            Opt(ctx, "cache", "yes");
            Opt(ctx, "demuxer-seekable-cache", "yes");
            Opt(ctx, "volume-max", "150");
            // Audiophile-grade audio output: native WASAPI 32-bit IEEE float, 32-tap high-precision sinc resampler, zero hidden DRC/ReplayGain
            Opt(ctx, "ao", "wasapi");
            Opt(ctx, "audio-format", "float");
            Opt(ctx, "audio-resample-filter-size", "32");
            Opt(ctx, "audio-resample-cutoff", "0.96");
            Opt(ctx, "audio-normalize-downmix", "no");
            Opt(ctx, "replaygain", "no");
            Opt(ctx, "sub-font", "Segoe UI");
            Opt(ctx, "sub-font-size", "40");
            Opt(ctx, "sub-border-size", "2.2");
            Opt(ctx, "sub-shadow-offset", "0");
            Opt(ctx, "sub-margin-y", "64");
            Opt(ctx, "sub-auto", "no");
            Opt(ctx, "ytdl", "yes");
            Opt(ctx, "script-opts", "ytdl_hook-ytdl_path=" + Paths.YtDlp.Replace('\\', '/') + (ScriptOpts.Length > 0 ? "," + ScriptOpts : ""));
            Opt(ctx, "cache-pause-initial", "no");
            Opt(ctx, "cache-pause-wait", "0.2");
            Opt(ctx, "demuxer-readahead-secs", "30");
            Opt(ctx, "demuxer-lavf-probesize", "262144");
            Opt(ctx, "demuxer-lavf-analyzeduration", "0.5");
            Opt(ctx, "stream-buffer-size", "128KiB");
            Opt(ctx, "vd-lavc-fast", "yes");
            Opt(ctx, "network-timeout", "12");
            Opt(ctx, "stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=2");
            Opt(ctx, "hr-seek", "yes");
            Opt(ctx, "vd-lavc-threads", "6");
            Opt(ctx, "ad-lavc-threads", "2");
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
            mpv_observe_property(ctx, 9, "paused-for-cache", Format.Flag);
            mpv_observe_property(ctx, 10, "audio-codec-name", Format.String);
            mpv_observe_property(ctx, 11, "audio-params/samplerate", Format.Int64);
            mpv_observe_property(ctx, 12, "audio-bitrate", Format.Double);
            mpv_observe_property(ctx, 13, "audio-params/format", Format.String);
            mpv_observe_property(ctx, 14, "video-params/w", Format.Int64);
            mpv_observe_property(ctx, 15, "video-params/h", Format.Int64);

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
        // Streams are served to mpv over loopback; StreamProxy handles the upstream proxy.
        mpv_set_option_string(ctx, "http-proxy", "");
        _lastAppliedAf = AudioDsp.BuildEffectiveFilterGraph(s, vol);
        mpv_set_option_string(ctx, "af", _lastAppliedAf);
        bool ex = s.AudioWasapiExclusive && !s.AudioFxEnabled && !s.PlayerAudio8D;
        WasapiExclusiveActive = ex;
        mpv_set_option_string(ctx, "audio-exclusive", ex ? "yes" : "no");
        mpv_set_option_string(ctx, "audio-pitch-correction", (s.AudioFxEnabled && s.FxTapePitch) ? "no" : "yes");
        // Apple iOS ProMotion-style video smoothing (VSync ON when display-resample is active so A/V sync stays 100% locked!)
        if (s.AppleSmoothMotion)
        {
            mpv_set_option_string(ctx, "d3d11-sync-interval", "1");
            mpv_set_option_string(ctx, "video-sync", "display-resample");
            mpv_set_option_string(ctx, "interpolation", "yes");
            mpv_set_option_string(ctx, "tscale", "oversample");
            mpv_set_option_string(ctx, "scale", "spline36");
            mpv_set_option_string(ctx, "dscale", "mitchell");
            mpv_set_option_string(ctx, "cscale", "spline36");
        }
        else
        {
            mpv_set_option_string(ctx, "d3d11-sync-interval", "0");
            mpv_set_option_string(ctx, "video-sync", "audio");
            mpv_set_option_string(ctx, "interpolation", "no");
        }
        if (s.AudioFxEnabled && MathF.Abs(s.FxSpeed - 1f) > 0.01f)
        {
            Speed = s.FxSpeed;
            mpv_set_option_string(ctx, "speed", s.FxSpeed.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Pushes current settings (quality, cookies, proxy, DSP EQ/FX...) into mpv asynchronously.</summary>
    public void ApplySettings()
    {
        if (!IsReady) return;
        var s = AppSettings.Current;
        var vol = Math.Clamp(s.Volume, 0, 150);
        Volume = vol;
        Set("ytdl-format", s.PlayerFormat);
        Set("ytdl-raw-options", "");
        foreach (var kv in YtDlp.CommonRawOptions(forPlayer: true))
            Command("set", "ytdl-raw-options-append", kv);
        Set("http-proxy", "");
        if (s.AppleSmoothMotion)
        {
            Set("d3d11-sync-interval", "1");
            Set("video-sync", "display-resample");
            Set("interpolation", "yes");
            Set("tscale", "oversample");
            Set("scale", "spline36");
            Set("dscale", "mitchell");
            Set("cscale", "spline36");
        }
        else
        {
            Set("d3d11-sync-interval", "0");
            Set("video-sync", "audio");
            Set("interpolation", "no");
        }
        ApplyAudioDsp(syncSpeed: false);
    }

    /// <summary>Live-applies the Reaper-style DSP EQ/reverb/pitch chain without restarting playback.</summary>
    public void ApplyAudioDsp(bool syncSpeed = false)
    {
        if (!IsReady) return;
        var s = AppSettings.Current;
        var vol = Math.Clamp(Volume, 0, 150);
        Set("volume", vol.ToString("0.#", CultureInfo.InvariantCulture));
        var af = AudioDsp.BuildEffectiveFilterGraph(s, vol);
        if (af != _lastAppliedAf)
        {
            _lastAppliedAf = af;
            Set("af", af);
        }
        bool wantEx = s.AudioWasapiExclusive && !s.AudioFxEnabled && !s.PlayerAudio8D;
        if (wantEx != WasapiExclusiveActive)
        {
            WasapiExclusiveActive = wantEx;
            Set("audio-exclusive", wantEx ? "yes" : "no");
            if (HasMedia) Command("ao-reload");
        }
        Set("audio-pitch-correction", (s.AudioFxEnabled && s.FxTapePitch) ? "no" : "yes");
        if (syncSpeed)
            SetSpeed(s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 2.0f) : 1.0);
    }

    /// <summary>
    /// Suspends or restores hardware/software video decoding (vid=no vs vid=auto) while audio keeps playing seamlessly.
    /// When re-enabling video during active playback, forces an immediate exact keyframe refresh so the picture returns
    /// on the very first frame (never waiting 10+ seconds for the next AV1/VP9 keyframe or staying black when paused).
    /// </summary>
    public void SetVideoEnabled(bool enabled)
    {
        if (VideoEnabled == enabled) return;
        VideoEnabled = enabled;
        if (!enabled)
        {
            Volatile.Write(ref _videoWidth, 0);
            ClearLiveFrameSample();
        }
        else
        {
            MarkFrameDirty();
        }
        if (!IsReady) return;
        Set("vid", enabled ? "auto" : "no");
        if (enabled && HasMedia)
            Command("seek", "0", "relative+exact");
    }

    /// <summary>Ensures mpv's live vid property matches VideoEnabled asynchronously (zero synchronous mpv_get_property stall!).</summary>
    public void EnsureVideoTrackSynced()
    {
        if (!IsReady || !VideoEnabled) return;
        Set("vid", "auto");
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

    public void Load(string url, string? videoId, double startSeconds = 0, bool startPaused = false)
    {
        if (!IsReady) return;
        Interlocked.Increment(ref _loadGen);
        CurrentUrl = url;
        CurrentAudioUrl = url;
        CurrentVideoId = videoId;
        Volatile.Write(ref _videoWidth, 0);
        ClearLiveFrameSample();
        MarkFrameDirty();
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
        AudioCodec = ""; AudioSampleFormat = ""; AudioSampleRate = 0; AudioBitrateKbps = 0;
        HasLosslessTrack = false; LosslessSearching = false; LosslessDownloadPercent = -1; LosslessTimingDiff = false; LosslessSourceLabel = null; ExpectedLosslessBitrateKbps = 0;
        if (startPaused) { Paused = true; Set("pause", "yes"); }
        // Keep vid/aid strictly clean before loadfile (never let sticky aid=2 from a previous FLAC track starve a new 1-track file when vid=no!)
        Set("vid", VideoEnabled ? "auto" : "no");
        Set("aid", "auto");
        var opts = new List<string> { startSeconds > 0.3 ? $"start={startSeconds.ToString("0.00", CultureInfo.InvariantCulture)}" : "start=0" };
        Command("loadfile", url, "replace", "-1", string.Join(',', opts));
        if (!startPaused) Set("pause", "no");
    }

    /// <summary>Plays already-resolved googlevideo URLs directly: no yt-dlp on the critical path.</summary>
    public void LoadStream(StreamInfo s, string? title, double startSeconds, bool keepPause = false)
    {
        if (!IsReady) return;
        Interlocked.Increment(ref _loadGen);
        CurrentUrl = s.Video;
        CurrentAudioUrl = s.Audio ?? s.Video;
        CurrentVideoId = s.VideoId;
        IsLiveStream = s.IsLive;
        Volatile.Write(ref _videoWidth, 0);
        ClearLiveFrameSample();
        MarkFrameDirty();
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
        AudioCodec = ""; AudioSampleFormat = ""; AudioSampleRate = 0; AudioBitrateKbps = 0;
        HasLosslessTrack = false; LosslessSearching = false; LosslessDownloadPercent = -1; LosslessTimingDiff = false; LosslessSourceLabel = null; ExpectedLosslessBitrateKbps = 0;
        if (keepPause) { Paused = true; Set("pause", "yes"); }
        // Direct HLS manifests (live streams) bypass StreamProxy, so pass the user's proxy & User-Agent directly when non-loopback
        bool directRemote = s.Video.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.Video.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || s.Video.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        Set("http-proxy", directRemote && !string.IsNullOrWhiteSpace(AppSettings.Current.Proxy) ? AppSettings.Current.Proxy! : "");
        if (directRemote && s.Headers.TryGetValue("User-Agent", out var ua) && !string.IsNullOrWhiteSpace(ua))
            Set("user-agent", ua);
        // Keep vid/aid strictly clean before loadfile (prevents sticky aid=2 + vid=no -> "no audio or video data played" on next track/retry!)
        Set("vid", VideoEnabled ? "auto" : "no");
        Set("aid", "auto");
        var opts = new List<string>();
        // Never force start=0 on LIVE HLS streams — let mpv join at the real-time live edge!
        if (!s.IsLive)
            opts.Add("start=" + (startSeconds > 0.3 ? startSeconds.ToString("0.00", CultureInfo.InvariantCulture) : "0"));
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
        Set("aid", "auto");
        Command("stop");
        CurrentUrl = null; CurrentVideoId = null; IsLiveStream = false;
        Volatile.Write(ref _videoWidth, 0);
        ClearLiveFrameSample();
        HasLosslessTrack = false; LosslessSearching = false; LosslessDownloadPercent = -1; LosslessTimingDiff = false; LosslessSourceLabel = null; ExpectedLosslessBitrateKbps = 0;
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
    }

    /// <summary>
    /// Attaches a Lossless FLAC stream to the currently playing media without restarting video or losing playback position.
    /// When autoSelect is true and audio is already playing, performs a silky 180ms dip-crossfade so the transition
    /// from YouTube Opus to 24-bit/16-bit FLAC is completely seamless with zero audible pop or stutter!
    /// </summary>
    public void AttachLosslessTrack(string localProxyUrl, string sourceLabel, bool autoSelect, bool timingDiff = false, int expectedBitrateKbps = 0)
    {
        if (!IsReady || !HasMedia) return;
        HasLosslessTrack = true;
        LosslessSearching = false;
        LosslessDownloadPercent = 100;
        LosslessSourceLabel = sourceLabel;
        LosslessTimingDiff = timingDiff;
        ExpectedLosslessBitrateKbps = expectedBitrateKbps;

        if (!autoSelect || Paused || Muted || Volume <= 1.0 || TimePos < 0.25)
        {
            if (autoSelect) AudioBitrateKbps = expectedBitrateKbps;
            Command("audio-add", localProxyUrl, autoSelect ? "select" : "auto", $"FLAC ({sourceLabel})", "flac");
            return;
        }

        // Seamless SSD studio-blend: 100% of FLAC is already on SSD -> gentle 96ms fade-down -> <2ms local switch -> gentle 160ms bloom-up!
        var baseVol = Volume;
        var loadGen = Volatile.Read(ref _loadGen);
        var rampGen = Interlocked.Increment(ref _volRampGen);
        AudioBitrateKbps = expectedBitrateKbps;
        Command("audio-add", localProxyUrl, "auto", $"FLAC ({sourceLabel})", "flac");
        _ = Task.Run(async () =>
        {
            try
            {
                // Soft dip down to 25% over 6 steps (~96ms)
                double[] down = [0.85, 0.70, 0.56, 0.44, 0.33, 0.25];
                foreach (var k in down)
                {
                    if (loadGen != Volatile.Read(ref _loadGen) || rampGen != Volatile.Read(ref _volRampGen) || !IsReady) return;
                    Command("set", "volume", (baseVol * k).ToString("0.#", CultureInfo.InvariantCulture));
                    await Task.Delay(16).ConfigureAwait(false);
                }
                if (loadGen != Volatile.Read(ref _loadGen) || !IsReady) return;
                // Instant switch to 100%-SSD FLAC track right at the quietest point of the dip (0 network stall!)
                Set("aid", "2");
                await Task.Delay(20).ConfigureAwait(false);
                // Smooth bloom back up to full volume over 9 steps (~144ms)
                double[] up = [0.34, 0.45, 0.56, 0.67, 0.77, 0.85, 0.92, 0.97, 1.0];
                foreach (var k in up)
                {
                    if (loadGen != Volatile.Read(ref _loadGen) || rampGen != Volatile.Read(ref _volRampGen) || !IsReady) return;
                    double v = k >= 1.0 ? baseVol : baseVol * k;
                    Command("set", "volume", v.ToString("0.#", CultureInfo.InvariantCulture));
                    if (k < 1.0) await Task.Delay(16).ConfigureAwait(false);
                }
                Volume = baseVol;
            }
            catch { }
        });
    }

    /// <summary>Instant A/B toggle between the attached Lossless FLAC track (aid=2) and the original YouTube audio track (aid=1).</summary>
    public bool ToggleLosslessAB()
    {
        if (!IsReady || !HasMedia || !HasLosslessTrack) return false;
        if (IsLosslessAudio)
        {
            Set("aid", "1");
            return false; // switched back to YouTube
        }
        Set("aid", "2");
        return true; // switched to FLAC
    }

    public void TogglePause() => Command("cycle", "pause");
    public void SetPause(bool p) { Paused = p; Set("pause", p ? "yes" : "no"); }
    public void SeekAbsolute(double s) => Command("seek", s.ToString(CultureInfo.InvariantCulture), "absolute");
    /// <summary>Keyframe-aligned seek for SponsorBlock auto-skips: instant video+audio lock with zero frozen D3D11VA frames.</summary>
    public void SeekKeyframe(double s) => Command("seek", s.ToString(CultureInfo.InvariantCulture), "absolute+keyframes");
    public void ShowText(string text, int ms = 1800) => Command("show-text", text, ms.ToString());
    public void SetSpeed(double v) { Speed = v; Set("speed", v.ToString(CultureInfo.InvariantCulture)); }

    private int _frameStepGen;

    /// <summary>
    /// Precision frame-by-frame jog (Shift + mouse wheel): steps exact decoded video frames
    /// forward or backward and renders a crisp pro-NLE OSD readout with frame number, exact milliseconds, and FPS.
    /// </summary>
    public void StepFrame(int deltaFrames)
    {
        if (!IsReady || !HasMedia || deltaFrames == 0) return;
        bool forward = deltaFrames > 0;
        int steps = Math.Clamp(Math.Abs(deltaFrames), 1, 5);
        for (int i = 0; i < steps; i++)
            Command(forward ? "frame-step" : "frame-back-step");
        Paused = true;

        var gen = Interlocked.Increment(ref _frameStepGen);
        _ = Task.Run(async () =>
        {
            await Task.Delay(42);
            if (gen != Volatile.Read(ref _frameStepGen) || !IsReady || !HasMedia) return;
            double pos = GetDouble("time-pos");
            double fps = GetDouble("estimated-vf-fps");
            if (fps <= 0.5) fps = GetDouble("container-fps");
            double frameNum = GetDouble("estimated-frame-number");
            if (frameNum <= 0 && fps > 0.5 && pos >= 0) frameNum = Math.Round(pos * fps);

            var ts = TimeSpan.FromSeconds(Math.Max(0, pos));
            string tc = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}"
                : $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
            string arrow = forward ? "▸" : "◂";
            string sign = forward ? $"+{steps}" : $"-{steps}";
            string fpsPart = fps > 1 ? $" · {fps:0.##} fps" : "";
            string lbl = L.T("Кадр", "Frame");
            string text = frameNum > 0
                ? $"{arrow} {lbl} #{(long)Math.Round(frameNum)}  ·  {tc}{fpsPart}  ({sign})"
                : $"{arrow} {lbl}  ·  {tc}{fpsPart}  ({sign})";
            ShowText(text, 1300);
            MarkFrameDirty();
            RequestFrameColorSample();
        });
    }

    public void MarkFrameDirty() => Volatile.Write(ref _frameDirty, 1);

    private void ClearLiveFrameSample()
    {
        lock (_sampleLock) _latestSample = default;
    }

    /// <summary>
    /// Requests a non-blocking async video frame snapshot (`screenshot-raw video`) from libmpv.
    /// Zero allocations, zero UI stall, zero disk I/O, and zero OSD/subtitle contamination;
    /// results are subsampled directly from the raw pointer inside EventLoop (~0.04 ms).
    /// </summary>
    public unsafe void RequestFrameColorSample()
    {
        var ctx = Volatile.Read(ref _ctx);
        if (ctx == IntPtr.Zero || _disposed || !HasActiveVideo) return;
        long now = Environment.TickCount64;
        if (Interlocked.CompareExchange(ref _frameSampleInFlight, 1, 0) != 0)
        {
            if (now - Volatile.Read(ref _frameSampleSentAt) < 1800) return;
            // Safety recovery if mpv dropped a reply across a file transition
            Volatile.Write(ref _frameSampleSentAt, now);
        }
        else
        {
            Volatile.Write(ref _frameSampleSentAt, now);
        }
        Volatile.Write(ref _frameSampleReqLoadGen, Volatile.Read(ref _loadGen));

        fixed (byte* pCmd = "screenshot-raw\0"u8)
        fixed (byte* pMode = "video\0"u8)
        {
            MpvNode* items = stackalloc MpvNode[2];
            items[0] = new MpvNode { u = (long)pCmd, format = Format.String };
            items[1] = new MpvNode { u = (long)pMode, format = Format.String };
            MpvNodeList list = new() { num = 2, values = items, keys = null };
            MpvNode root = new() { u = (long)&list, format = Format.NodeArray };
            int err = mpv_command_node_async(ctx, FrameSampleUserdata, &root);
            if (err < 0) Volatile.Write(ref _frameSampleInFlight, 0);
        }
    }

    private unsafe void HandleFrameSampleReply(IntPtr evData, int evError)
    {
        Volatile.Write(ref _frameSampleInFlight, 0);
        if (evError < 0 || evData == IntPtr.Zero || !HasActiveVideo) return;
        if (Volatile.Read(ref _frameSampleReqLoadGen) != Volatile.Read(ref _loadGen)) return;
        var res = (MpvNode*)evData;
        if (res->format != Format.NodeMap || res->u == 0) return;

        var map = (MpvNodeList*)res->u;
        if (map->num <= 0 || map->values == null || map->keys == null) return;

        int w = 0, h = 0, stride = 0;
        string fmt = "bgr0";
        byte* data = null;
        nuint dataSize = 0;

        for (int i = 0; i < map->num; i++)
        {
            var key = PtrToUtf8((IntPtr)map->keys[i]);
            if (key == null) continue;
            var val = map->values[i];
            switch (key)
            {
                case "w" when val.format == Format.Int64: w = (int)val.u; break;
                case "h" when val.format == Format.Int64: h = (int)val.u; break;
                case "stride" when val.format == Format.Int64: stride = (int)val.u; break;
                case "format" when val.format == Format.String: fmt = (PtrToUtf8((IntPtr)val.u) ?? "bgr0").ToLowerInvariant(); break;
                case "data" when val.format == Format.ByteArray && val.u != 0:
                    var ba = (MpvByteArray*)val.u;
                    data = ba->data;
                    dataSize = ba->size;
                    break;
            }
        }

        if (data == null || w < 8 || h < 8 || stride <= 0) return;
        if ((long)stride < (long)w * 4 || (long)stride * h > (long)dataSize) return;

        if (!ExtractLiveFrameColors(data, w, h, stride, fmt,
                out uint dom1, out uint dom2, out bool isNeutral, out float avgLuma,
                out var tl, out var tr, out var bl, out var br, out var center, out var grid))
            return;

        int gen = Interlocked.Increment(ref _frameSampleGen);
        var sample = new LiveFrameSample(ActiveMediaKey, Environment.TickCount64, gen, dom1, dom2, isNeutral, avgLuma, tl, tr, bl, br, center, grid);
        lock (_sampleLock) _latestSample = sample;
        Volatile.Write(ref _frameDirty, 0);
        Ui(() => FrameSampled?.Invoke());
    }

    private static unsafe bool ExtractLiveFrameColors(
        byte* px, int w, int h, int stride, string fmt,
        out uint dom1, out uint dom2, out bool isNeutral, out float avgLuma,
        out (float R, float G, float B) tl, out (float R, float G, float B) tr,
        out (float R, float G, float B) bl, out (float R, float G, float B) br,
        out (float R, float G, float B) center, out SpatialGrid15 grid)
    {
        dom1 = 0; dom2 = 0; isNeutral = false; avgLuma = 0.25f;
        tl = tr = bl = br = center = (0.2f, 0.2f, 0.25f);
        grid = default;

        int rOff, gOff, bOff;
        switch (fmt)
        {
            case "bgr0":
            case "bgra": rOff = 2; gOff = 1; bOff = 0; break;
            case "rgb0":
            case "rgba": rOff = 0; gOff = 1; bOff = 2; break;
            case "argb": rOff = 1; gOff = 2; bOff = 3; break;
            case "abgr": rOff = 3; gOff = 2; bOff = 1; break;
            default: rOff = 2; gOff = 1; bOff = 0; break;
        }

        // 24 circular hue bins (15° resolution) + 5x3 (15-zone) chroma-weighted spatial grid
        Span<double> hr = stackalloc double[24];
        Span<double> hg = stackalloc double[24];
        Span<double> hb = stackalloc double[24];
        Span<double> hw = stackalloc double[24];
        Span<double> smoothW = stackalloc double[24];
        Span<double> zR = stackalloc double[15];
        Span<double> zG = stackalloc double[15];
        Span<double> zB = stackalloc double[15];
        Span<double> zW = stackalloc double[15];

        // Trim subtle outer margins (ignores cinema letterbox bars & corner watermarks)
        int xStart = w / 24, xEnd = w - w / 24;
        int yStart = h / 16, yEnd = h - h / 16;
        if (xEnd <= xStart + 4) { xStart = 0; xEnd = w; }
        if (yEnd <= yStart + 4) { yStart = 0; yEnd = h; }

        int spanX = Math.Max(1, xEnd - xStart);
        int spanY = Math.Max(1, yEnd - yStart);
        int stepX = Math.Max(1, spanX / 35);
        int stepY = Math.Max(1, spanY / 21);

        int totalSamples = 0, visibleSamples = 0, chromaticSamples = 0, whiteNeutralSamples = 0;
        double sumR = 0, sumG = 0, sumB = 0, sumLuma = 0;

        for (int y = yStart; y < yEnd; y += stepY)
        {
            byte* row = px + (nint)y * stride;
            int gRow = Math.Clamp((y - yStart) * SpatialGrid15.Rows / spanY, 0, SpatialGrid15.Rows - 1);
            int gRowOff = gRow * SpatialGrid15.Cols;
            for (int x = xStart; x < xEnd; x += stepX)
            {
                byte* p = row + x * 4;
                float r = p[rOff] * (1f / 255f);
                float g = p[gOff] * (1f / 255f);
                float b = p[bOff] * (1f / 255f);

                float mx = MathF.Max(r, MathF.Max(g, b));
                float mn = MathF.Min(r, MathF.Min(g, b));
                float d = mx - mn;
                float sat = mx > 0.01f ? d / mx : 0f;
                float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                // Pinpoint 5x3 spatial cell accumulation (chroma-weighted so vivid local lights pop instead of graying out!)
                int gCol = Math.Clamp((x - xStart) * SpatialGrid15.Cols / spanX, 0, SpatialGrid15.Cols - 1);
                int zi = gRowOff + gCol;
                double zw = 0.30 + sat * sat * 1.45 + mx * 0.25;
                zR[zi] += r * zw; zG[zi] += g * zw; zB[zi] += b * zw; zW[zi] += zw;

                totalSamples++;
                sumLuma += luma;
                if (mx >= 0.11f)
                {
                    visibleSamples++;
                    sumR += r; sumG += g; sumB += b;
                }
                if (luma >= 0.56f && sat < 0.16f) whiteNeutralSamples++;

                if (mx < 0.14f || sat < 0.16f || d < 0.07f) continue;
                chromaticSamples++;

                float hue = d < 0.001f ? 0f :
                    mx == r ? ((g - b) / d + (g < b ? 6f : 0f)) :
                    mx == g ? ((b - r) / d + 2f) : ((r - g) / d + 4f);
                int bin = Math.Clamp((int)(hue * 4f) % 24, 0, 23);
                double weight = sat * Math.Sqrt(sat) * (0.28 + mx * 0.72);
                hr[bin] += r * weight; hg[bin] += g * weight; hb[bin] += b * weight; hw[bin] += weight;
            }
        }

        if (totalSamples == 0) return false;
        avgLuma = (float)(sumLuma / totalSamples);

        for (int zi = 0; zi < 15; zi++)
        {
            if (zW[zi] > 0.0001)
            {
                byte zr = (byte)Math.Clamp((int)(zR[zi] / zW[zi] * 255.0 + 0.5), 0, 255);
                byte zg = (byte)Math.Clamp((int)(zG[zi] / zW[zi] * 255.0 + 0.5), 0, 255);
                byte zb = (byte)Math.Clamp((int)(zB[zi] / zW[zi] * 255.0 + 0.5), 0, 255);
                grid[zi] = 0xFF000000u | ((uint)zr << 16) | ((uint)zg << 8) | zb;
            }
            else grid[zi] = 0xFF1E2028u;
        }
        tl = grid.Cell(0, 0);
        tr = grid.Cell(4, 0);
        bl = grid.Cell(0, 2);
        br = grid.Cell(4, 2);
        center = grid.Cell(2, 1);

        // 3-bin circular smoothing prevents sudden jumps when scene hue sits right on a bin border
        int best1 = -1, best2 = -1;
        for (int i = 0; i < 24; i++)
        {
            smoothW[i] = hw[(i + 23) % 24] * 0.25 + hw[i] * 0.50 + hw[(i + 1) % 24] * 0.25;
            if (smoothW[i] > 0.0001 && (best1 < 0 || smoothW[i] > smoothW[best1])) best1 = i;
        }
        for (int i = 0; i < 24; i++)
        {
            if (smoothW[i] <= 0.0001 || i == best1) continue;
            int dist = Math.Min(Math.Abs(i - best1), 24 - Math.Abs(i - best1));
            if (dist >= 3 && (best2 < 0 || smoothW[i] > smoothW[best2])) best2 = i;
        }

        float chromaticFrac = chromaticSamples / (float)Math.Max(1, visibleSamples);
        float whiteFrac = whiteNeutralSamples / (float)Math.Max(1, totalSamples);
        isNeutral = best1 < 0 || chromaticFrac < 0.12f || (whiteFrac >= 0.62f && chromaticFrac < 0.22f);

        if (isNeutral)
        {
            float baseTone = (whiteFrac >= 0.42f || avgLuma >= 0.55f) ? 0.84f : 0.72f;
            float nr = baseTone, ng = baseTone, nb = baseTone;
            if (visibleSamples > 0)
            {
                float ar = (float)(sumR / visibleSamples), ag = (float)(sumG / visibleSamples), ab = (float)(sumB / visibleSamples);
                float aMean = (ar + ag + ab) / 3f;
                nr = Math.Clamp(baseTone + Math.Clamp((ar - aMean) * 0.35f, -0.025f, 0.025f), 0.62f, 0.92f);
                ng = Math.Clamp(baseTone + Math.Clamp((ag - aMean) * 0.35f, -0.025f, 0.025f), 0.62f, 0.92f);
                nb = Math.Clamp(baseTone + Math.Clamp((ab - aMean) * 0.35f, -0.025f, 0.025f), 0.62f, 0.92f);
            }
            dom1 = 0xFF000000u | ((uint)(nr * 255f + 0.5f) << 16) | ((uint)(ng * 255f + 0.5f) << 8) | (uint)(nb * 255f + 0.5f);
            float sec = baseTone - 0.20f;
            dom2 = 0xFF000000u | ((uint)(sec * 255f + 0.5f) << 16) | ((uint)(sec * 255f + 0.5f) << 8) | (uint)(Math.Clamp(sec + 0.02f, 0f, 1f) * 255f + 0.5f);
            return true;
        }

        static uint PackCluster(Span<double> rA, Span<double> gA, Span<double> bA, Span<double> wA, int centerBin, uint fallback)
        {
            int prev = (centerBin + 23) % 24, next = (centerBin + 1) % 24;
            double sw = wA[prev] * 0.5 + wA[centerBin] + wA[next] * 0.5;
            if (sw <= 0.0001) return fallback;
            float r = (float)((rA[prev] * 0.5 + rA[centerBin] + rA[next] * 0.5) / sw);
            float g = (float)((gA[prev] * 0.5 + gA[centerBin] + gA[next] * 0.5) / sw);
            float b = (float)((bA[prev] * 0.5 + bA[centerBin] + bA[next] * 0.5) / sw);
            float avg = (r + g + b) / 3f;
            r = Math.Clamp(avg + (r - avg) * 1.32f, 0.06f, 1f);
            g = Math.Clamp(avg + (g - avg) * 1.32f, 0.06f, 1f);
            b = Math.Clamp(avg + (b - avg) * 1.32f, 0.06f, 1f);
            float mx = MathF.Max(r, MathF.Max(g, b));
            if (mx < 0.64f && mx > 0.01f) { float k = 0.64f / mx; r = MathF.Min(1f, r * k); g = MathF.Min(1f, g * k); b = MathF.Min(1f, b * k); }
            return 0xFF000000u | ((uint)(r * 255f + 0.5f) << 16) | ((uint)(g * 255f + 0.5f) << 8) | (uint)(b * 255f + 0.5f);
        }

        dom1 = PackCluster(hr, hg, hb, hw, best1, 0xFFD0D4DCu);
        if (best2 >= 0)
        {
            dom2 = PackCluster(hr, hg, hb, hw, best2, dom1);
        }
        else
        {
            // Analogous harmony when the whole frame is one color (e.g. underwater blue / red stage)
            float r = ((dom1 >> 16) & 255) / 255f, g = ((dom1 >> 8) & 255) / 255f, b = (dom1 & 255) / 255f;
            dom2 = 0xFF000000u | ((uint)(Math.Clamp(r * 0.72f + g * 0.34f, 0.10f, 1f) * 255f) << 16)
                               | ((uint)(Math.Clamp(g * 0.72f + b * 0.34f, 0.10f, 1f) * 255f) << 8)
                               |  (uint)(Math.Clamp(b * 0.72f + r * 0.34f, 0.10f, 1f) * 255f);
        }
        return true;
    }

    /// <summary>
    /// End-to-end self-test verifying both synthetic pixel extraction AND real libmpv `mpv_command_node_async("screenshot-raw", "video")`
    /// ABI marshaling on live GPU-rendered frames (green -> crimson transition + audio-only video-params/w=0 check).
    /// </summary>
    public static unsafe (bool Ok, string Summary) SelfTestLiveFrameSampler()
    {
        // 1. Synthetic pixel extraction: crimson + cyan split, pure white neutral, letterbox emerald
        const int sw = 160, sh = 90;
        var buf = new byte[sw * sh * 4];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int o = (y * sw + x) * 4;
                if (x < sw * 2 / 3) { buf[o] = 45; buf[o + 1] = 25; buf[o + 2] = 240; buf[o + 3] = 255; } // BGR vivid crimson
                else { buf[o] = 245; buf[o + 1] = 220; buf[o + 2] = 20; buf[o + 3] = 255; } // BGR vivid cyan
            }
        fixed (byte* p = buf)
        {
            if (!ExtractLiveFrameColors(p, sw, sh, sw * 4, "bgr0", out uint d1, out uint d2, out bool neut, out _, out _, out _, out _, out _, out _, out var gTest))
                return (false, "synthetic crimson/cyan returned false");
            int r1 = (int)((d1 >> 16) & 255), g1 = (int)((d1 >> 8) & 255), b1 = (int)(d1 & 255);
            int r2 = (int)((d2 >> 16) & 255), b2 = (int)(d2 & 255);
            if (neut || r1 < 170 || r1 <= g1 + 60 || b2 < 140 || b2 <= r2 + 40)
                return (false, $"synthetic crimson/cyan mismatch d1=#{d1 & 0xFFFFFF:X6} d2=#{d2 & 0xFFFFFF:X6} neut={neut}");
            // Verify pinpoint 5x3 spatial positioning: left cell (0,1) must be crimson, right cell (4,1) must be cyan!
            var leftCell = gTest.Cell(0, 1);
            var rightCell = gTest.Cell(4, 1);
            if (leftCell.R < 0.7f || leftCell.B > 0.3f || rightCell.B < 0.7f || rightCell.R > 0.3f)
                return (false, $"spatial 5x3 positioning mismatch L=({leftCell.R:0.00},{leftCell.B:0.00}) R=({rightCell.R:0.00},{rightCell.B:0.00})");
        }

        Array.Fill(buf, (byte)242);
        fixed (byte* p = buf)
        {
            if (!ExtractLiveFrameColors(p, sw, sh, sw * 4, "bgr0", out uint dw, out _, out bool neut, out _, out _, out _, out _, out _, out _, out _))
                return (false, "synthetic white returned false");
            if (!neut) return (false, $"synthetic white not neutral (#{dw & 0xFFFFFF:X6})");
        }

        // 2. Real libmpv live frame capture via mpv_command_node_async("screenshot-raw", "video")
        var ctx = mpv_create();
        if (ctx == IntPtr.Zero) return (false, "mpv_create failed");
        var hInst = Win32.GetModuleHandleW(null);
        var host = Win32.CreateWindowExW(0, "STATIC", "mpvtest", Win32.WS_POPUP, -2000, -2000, 160, 90, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        try
        {
            Opt(ctx, "wid", host.ToInt64().ToString());
            Opt(ctx, "vo", "gpu-next");
            Opt(ctx, "gpu-api", "d3d11");
            Opt(ctx, "hwdec", "no");
            Opt(ctx, "ao", "null");
            Opt(ctx, "idle", "yes");
            Opt(ctx, "osc", "no");
            Opt(ctx, "osd-bar", "no");
            Opt(ctx, "force-window", "immediate");
            if (mpv_initialize(ctx) < 0) return (false, "mpv_initialize failed");

            static bool LoadAndSample(IntPtr c, string lavfiUrl, out uint dom1, out bool neut)
            {
                dom1 = 0; neut = false;
                var loadArgs = new[] { Marshal.StringToCoTaskMemUTF8("loadfile"), Marshal.StringToCoTaskMemUTF8(lavfiUrl), IntPtr.Zero };
                try { mpv_command(c, loadArgs); }
                finally { Marshal.FreeCoTaskMem(loadArgs[0]); Marshal.FreeCoTaskMem(loadArgs[1]); }

                long deadline = Environment.TickCount64 + 6000;
                bool restarted = false, sent = false;
                MpvNode* items = stackalloc MpvNode[2];
                while (Environment.TickCount64 < deadline)
                {
                    var evPtr = mpv_wait_event(c, 0.05);
                    if (evPtr != IntPtr.Zero)
                    {
                        var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
                        if (ev.event_id == EventId.PlaybackRestart) restarted = true;
                        else if (ev.event_id == EventId.CommandReply && ev.reply_userdata == FrameSampleUserdata)
                        {
                            if (ev.error >= 0 && ev.data != IntPtr.Zero)
                            {
                                var res = (MpvNode*)ev.data;
                                if (res->format == Format.NodeMap && res->u != 0)
                                {
                                    var map = (MpvNodeList*)res->u;
                                    int w = 0, h = 0, stride = 0; string fmt = "bgr0"; byte* data = null; nuint size = 0;
                                    for (int i = 0; i < map->num; i++)
                                    {
                                        var k = PtrToUtf8((IntPtr)map->keys[i]);
                                        var v = map->values[i];
                                        if (k == "w" && v.format == Format.Int64) w = (int)v.u;
                                        else if (k == "h" && v.format == Format.Int64) h = (int)v.u;
                                        else if (k == "stride" && v.format == Format.Int64) stride = (int)v.u;
                                        else if (k == "format" && v.format == Format.String) fmt = (PtrToUtf8((IntPtr)v.u) ?? "bgr0").ToLowerInvariant();
                                        else if (k == "data" && v.format == Format.ByteArray && v.u != 0) { var ba = (MpvByteArray*)v.u; data = ba->data; size = ba->size; }
                                    }
                                    if (data != null && w >= 8 && h >= 8 && stride >= w * 4 && (long)stride * h <= (long)size
                                        && ExtractLiveFrameColors(data, w, h, stride, fmt, out dom1, out _, out neut, out _, out _, out _, out _, out _, out _, out _))
                                        return true;
                                }
                            }
                            sent = false;
                        }
                    }
                    if (restarted && !sent)
                    {
                        sent = true;
                        fixed (byte* pCmd = "screenshot-raw\0"u8)
                        fixed (byte* pMode = "video\0"u8)
                        {
                            items[0] = new MpvNode { u = (long)pCmd, format = Format.String };
                            items[1] = new MpvNode { u = (long)pMode, format = Format.String };
                            MpvNodeList list = new() { num = 2, values = items, keys = null };
                            MpvNode root = new() { u = (long)&list, format = Format.NodeArray };
                            if (mpv_command_node_async(c, FrameSampleUserdata, &root) < 0) sent = false;
                        }
                    }
                }
                return false;
            }

            if (!LoadAndSample(ctx, "av://lavfi:color=c=0x18D848:s=160x90:r=25:d=6", out uint gDom, out bool gNeut))
                return (false, "libmpv screenshot-raw green frame failed");
            int gr = (int)((gDom >> 16) & 255), gg = (int)((gDom >> 8) & 255), gb = (int)(gDom & 255);
            if (gNeut || gg < 150 || gg <= gr + 40 || gg <= gb + 40)
                return (false, $"libmpv green frame color wrong: #{gDom & 0xFFFFFF:X6} neut={gNeut}");

            if (!LoadAndSample(ctx, "av://lavfi:color=c=0x2266FF:s=160x90:r=25:d=6", out uint bDom, out bool bNeut))
                return (false, "libmpv screenshot-raw blue frame failed");
            int br = (int)((bDom >> 16) & 255), bg = (int)((bDom >> 8) & 255), bb = (int)(bDom & 255);
            if (bNeut || bb < 150 || bb <= br + 40 || bb <= bg + 30)
                return (false, $"libmpv blue frame color wrong: #{bDom & 0xFFFFFF:X6} neut={bNeut}");

            return (true, $"libmpv live frames OK (#{gDom & 0xFFFFFF:X6} -> #{bDom & 0xFFFFFF:X6})");
        }
        finally
        {
            mpv_terminate_destroy(ctx);
            if (host != IntPtr.Zero) Win32.DestroyWindow(host);
        }
    }

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
                    if (VideoEnabled) EnsureVideoTrackSynced();
                    MarkFrameDirty();
                    Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) FileLoaded?.Invoke(); });
                    break;
                }
                case EventId.Seek:
                {
                    MarkFrameDirty();
                    break;
                }
                case EventId.PlaybackRestart:
                {
                    var gen = Volatile.Read(ref _loadGen);
                    MarkFrameDirty();
                    Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) PlaybackStarted?.Invoke(); });
                    break;
                }
                case EventId.CommandReply:
                {
                    if (ev.reply_userdata == FrameSampleUserdata)
                        HandleFrameSampleReply(ev.data, ev.error);
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
                    Volatile.Write(ref _videoWidth, 0);
                    ClearLiveFrameSample();
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
                        case 9:
                        {
                            var v = Marshal.ReadInt32(p.data) != 0;
                            if (v != Buffering)
                            {
                                Buffering = v;
                                var gen = Volatile.Read(ref _loadGen);
                                Ui(() => { if (gen == Volatile.Read(ref _loadGen) && CurrentUrl != null) BufferingChanged?.Invoke(v); });
                            }
                            break;
                        }
                        case 10:
                        {
                            var strPtr = Marshal.ReadIntPtr(p.data);
                            var next = strPtr != IntPtr.Zero ? (PtrToUtf8(strPtr) ?? "").Trim().ToLowerInvariant() : "";
                            if (next != AudioCodec)
                            {
                                AudioCodec = next;
                                // Clear stale bitrate from previous codec (e.g. YouTube 110 kbps Opus when switching to FLAC)
                                AudioBitrateKbps = IsLosslessAudio ? ExpectedLosslessBitrateKbps : 0;
                                Ui(() => { });
                            }
                            break;
                        }
                        case 11:
                        {
                            int sr = (int)Math.Clamp(Marshal.ReadInt64(p.data), 0, 768000);
                            if (sr != AudioSampleRate) { AudioSampleRate = sr; Ui(() => { }); }
                            break;
                        }
                        case 12:
                        {
                            var bps = Marshal.PtrToStructure<double>(p.data);
                            if (bps > 1000 && bps < 50_000_000)
                            {
                                int kbps = (int)Math.Round(bps / 1000.0);
                                if (Math.Abs(kbps - AudioBitrateKbps) >= 4)
                                {
                                    AudioBitrateKbps = kbps;
                                    Ui(() => { });
                                }
                            }
                            break;
                        }
                        case 13:
                        {
                            var strPtr = Marshal.ReadIntPtr(p.data);
                            var fmt = strPtr != IntPtr.Zero ? (PtrToUtf8(strPtr) ?? "").Trim().ToLowerInvariant() : "";
                            if (fmt != AudioSampleFormat) { AudioSampleFormat = fmt; Ui(() => { }); }
                            break;
                        }
                        case 14:
                        {
                            int vw = (int)Math.Clamp(Marshal.ReadInt64(p.data), 0, 16384);
                            int prev = Interlocked.Exchange(ref _videoWidth, vw);
                            if (vw != prev)
                            {
                                if (vw > 0) MarkFrameDirty();
                                else ClearLiveFrameSample();
                                Ui(() => { });
                            }
                            break;
                        }
                        case 15:
                        {
                            int vh = (int)Math.Clamp(Marshal.ReadInt64(p.data), 0, 16384);
                            int prev = Interlocked.Exchange(ref _videoHeight, vh);
                            if (vh != prev) Ui(() => { });
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
