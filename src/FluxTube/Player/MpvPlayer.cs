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
    private string _lastAppliedAf = "";
    private static Win32.WndProc? _wndProcKeep;

    public double TimePos { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public bool Buffering { get; private set; }
    public bool Fullscreen { get; private set; }
    public double Volume { get; private set; } = AppSettings.Current.Volume;
    public bool Muted { get; private set; }
    public double Speed { get; private set; } = 1.0;
    public bool VideoEnabled { get; private set; } = true;
    public string? CurrentVideoId { get; private set; }
    public string? CurrentUrl { get; private set; }
    public string? CurrentAudioUrl { get; private set; }
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
            Opt(ctx, "demuxer-max-bytes", "64MiB");
            Opt(ctx, "demuxer-max-back-bytes", "16MiB");
            Opt(ctx, "hr-seek-framedrop", "no");
            Opt(ctx, "cache", "yes");
            Opt(ctx, "demuxer-seekable-cache", "no");
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
            Opt(ctx, "cache-pause-wait", "0.4");
            Opt(ctx, "demuxer-readahead-secs", "45");
            Opt(ctx, "network-timeout", "15");
            Opt(ctx, "stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=2");
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
            mpv_observe_property(ctx, 9, "paused-for-cache", Format.Flag);
            mpv_observe_property(ctx, 10, "audio-codec-name", Format.String);
            mpv_observe_property(ctx, 11, "audio-params/samplerate", Format.Int64);
            mpv_observe_property(ctx, 12, "audio-bitrate", Format.Double);
            mpv_observe_property(ctx, 13, "audio-params/format", Format.String);

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
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
        AudioCodec = ""; AudioSampleFormat = ""; AudioSampleRate = 0; AudioBitrateKbps = 0;
        HasLosslessTrack = false; LosslessSearching = false; LosslessDownloadPercent = -1; LosslessTimingDiff = false; LosslessSourceLabel = null; ExpectedLosslessBitrateKbps = 0;
        if (startPaused) { Paused = true; Set("pause", "yes"); }
        // Keep vid strictly as a global property (never a sticky per-file loadfile option, which races with SetVideoEnabled(true)!)
        Set("vid", VideoEnabled ? "auto" : "no");
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
        TimePos = 0; Duration = 0; _lastTimePosted = double.NaN;
        AudioCodec = ""; AudioSampleFormat = ""; AudioSampleRate = 0; AudioBitrateKbps = 0;
        HasLosslessTrack = false; LosslessSearching = false; LosslessDownloadPercent = -1; LosslessTimingDiff = false; LosslessSourceLabel = null; ExpectedLosslessBitrateKbps = 0;
        if (keepPause) { Paused = true; Set("pause", "yes"); }
        // Direct HLS manifests (live streams) bypass StreamProxy, so pass the user's proxy directly when non-loopback
        bool directRemote = s.Video.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.Video.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || s.Video.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        Set("http-proxy", directRemote && !string.IsNullOrWhiteSpace(AppSettings.Current.Proxy) ? AppSettings.Current.Proxy! : "");
        // Keep vid strictly as a global property so loadfile never overrides SetVideoEnabled(true) into a black screen
        Set("vid", VideoEnabled ? "auto" : "no");
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
        });
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
