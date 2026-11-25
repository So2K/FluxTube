using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using FluxTube.Player;
using FluxTube.Services;
using static FluxTube.Player.MpvNative;

namespace FluxTube;

/// <summary>
/// Lightweight audio-only libmpv instances for background tabs so up to 4 videos can play
/// simultaneously, be paused/muted/volume-adjusted smoothly directly from the tab strip,
/// and seamlessly hand off their exact playback position when switching back to the main player.
/// </summary>
public static class BgPlayers
{
    private const int MaxInstances = 4;

    private sealed class Instance : IDisposable
    {
        public readonly string VideoId;
        public readonly string Key;
        public readonly long CreatedAt = Environment.TickCount64;
        private readonly object _stateLock = new();
        private IntPtr _ctx;
        private Thread? _thread;
        private volatile bool _disposed;
        private volatile bool _fileLoaded;
        private bool _wantPaused;
        private bool _wantMuted;
        private int _wantVol = -1;
        private int _volRampGen;
        private double _timePos;
        private double _duration;

        public volatile bool Paused;
        public volatile bool Muted;
        public volatile bool Loading = true;
        public volatile int Volume = 100;
        public long PausedSince;

        public Instance(string key, string videoId, double initialPos, int? volumeOverride = null, bool? muteOverride = null)
        {
            Key = key;
            VideoId = videoId;
            Volatile.Write(ref _timePos, initialPos);
            var liveVol = volumeOverride ?? (MpvPlayer.Instance.IsReady ? (int)Math.Round(MpvPlayer.Instance.Volume) : AppSettings.Current.Volume);
            Volume = Math.Clamp(liveVol, 0, 150);
            _wantMuted = muteOverride ?? (App.Mute || Shell.UserMuted);
            Muted = _wantMuted;
        }

        public double TimePos => Volatile.Read(ref _timePos);
        public double Duration => Volatile.Read(ref _duration);

        /// <summary>Non-blocking cached live position (updated continuously by mpv's event thread).</summary>
        public double LivePos()
        {
            lock (_stateLock)
            {
                var ctx = Volatile.Read(ref _ctx);
                if (ctx != IntPtr.Zero && !_disposed && mpv_get_property(ctx, "time-pos", Format.Double, out double pos) >= 0 && pos > 0)
                {
                    Volatile.Write(ref _timePos, pos);
                    return pos;
                }
            }
            return Volatile.Read(ref _timePos);
        }

        public void Start(string? localFile, StreamInfo? stream, string fallbackUrl, double startSeconds)
        {
            IntPtr ctx;
            lock (_stateLock)
            {
                if (_disposed) return;
                ctx = mpv_create();
                if (ctx == IntPtr.Zero) return;
            }
            try
            {
                Opt(ctx, "vid", "no");
                Opt(ctx, "vo", "null");
                Opt(ctx, "idle", "yes");
                Opt(ctx, "keep-open", "yes");
                Opt(ctx, "osc", "no");
                Opt(ctx, "osd-bar", "no");
                Opt(ctx, "cache", "yes");
                Opt(ctx, "demuxer-max-bytes", "4MiB");
                Opt(ctx, "demuxer-max-back-bytes", "1MiB");
                Opt(ctx, "demuxer-seekable-cache", "no");
                Opt(ctx, "cache-pause-initial", "no");
                Opt(ctx, "network-timeout", "15");
                Opt(ctx, "stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=2");
                Opt(ctx, "ad-lavc-threads", "1");
                Opt(ctx, "volume-max", "150");

                int vol;
                bool muted, paused;
                lock (_stateLock)
                {
                    if (_disposed) { mpv_terminate_destroy(ctx); return; }
                    vol = _wantVol >= 0 ? _wantVol : Volume;
                    Volume = vol;
                    muted = _wantMuted;
                    paused = _wantPaused;
                }
                Opt(ctx, "volume", vol.ToString(CultureInfo.InvariantCulture));
                if (muted) Opt(ctx, "mute", "yes");
                if (AppSettings.Current.PlayerAudio8D)
                    Opt(ctx, "af", "lavfi=[apulsator=hz=0.14:width=1:amount=0.85,aecho=0.82:0.84:48|82:0.28|0.18]");
                Opt(ctx, "ytdl", "yes");
                Opt(ctx, "ytdl-format", "ba/b");
                Opt(ctx, "script-opts", "ytdl_hook-ytdl_path=" + Paths.YtDlp.Replace('\\', '/'));
                foreach (var kv in YtDlp.CommonRawOptions(forPlayer: true))
                    Opt(ctx, "ytdl-raw-options-append", kv);

                if (mpv_initialize(ctx) < 0)
                {
                    mpv_terminate_destroy(ctx);
                    return;
                }
                mpv_observe_property(ctx, 1, "time-pos", Format.Double);
                mpv_observe_property(ctx, 2, "duration", Format.Double);
                mpv_observe_property(ctx, 3, "pause", Format.Flag);
                mpv_observe_property(ctx, 4, "mute", Format.Flag);

                lock (_stateLock)
                {
                    if (_disposed)
                    {
                        mpv_terminate_destroy(ctx);
                        return;
                    }
                    Volatile.Write(ref _ctx, ctx);
                    Paused = paused;
                    Muted = muted;
                    Loading = false;
                }

                var startOpt = startSeconds > 0.3 ? "start=" + startSeconds.ToString("0.00", CultureInfo.InvariantCulture) : "start=0";
                if (!string.IsNullOrEmpty(localFile))
                    Cmd("loadfile", localFile, "replace", "-1", startOpt);
                else if (stream != null)
                    Cmd("loadfile", stream.Audio ?? stream.Video, "replace", "-1", startOpt);
                else
                    Cmd("loadfile", fallbackUrl, "replace", "-1", startOpt);
                Cmd("set", "pause", paused ? "yes" : "no");

                _thread = new Thread(Loop) { IsBackground = true, Name = "mpv-bg-" + VideoId };
                _thread.Start();
                App.WakeUi();
            }
            catch
            {
                lock (_stateLock)
                {
                    if (Volatile.Read(ref _ctx) == IntPtr.Zero)
                        try { mpv_terminate_destroy(ctx); } catch { }
                }
                throw;
            }
        }

        private void Loop()
        {
            while (!_disposed)
            {
                var ctx = Volatile.Read(ref _ctx);
                if (ctx == IntPtr.Zero) return;
                var evPtr = mpv_wait_event(ctx, 0.5);
                if (evPtr == IntPtr.Zero) continue;
                var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
                if (ev.event_id == EventId.Shutdown) return;
                if (ev.event_id == EventId.FileLoaded) { _fileLoaded = true; continue; }
                if (ev.event_id == EventId.EndFile)
                {
                    var ef = Marshal.PtrToStructure<MpvEventEndFile>(ev.data);
                    if (ef.reason == 4)
                    {
                        var key = Key;
                        App.Post(() => Stop(key, saveToDb: false));
                        return;
                    }
                    continue;
                }
                if (ev.event_id == EventId.PropertyChange)
                {
                    var p = Marshal.PtrToStructure<MpvEventProperty>(ev.data);
                    if (p.data == IntPtr.Zero) continue;
                    switch (ev.reply_userdata)
                    {
                        case 1:
                            if (_fileLoaded) Volatile.Write(ref _timePos, Marshal.PtrToStructure<double>(p.data));
                            break;
                        case 2:
                            Volatile.Write(ref _duration, Marshal.PtrToStructure<double>(p.data));
                            break;
                        case 3:
                            Paused = Marshal.ReadInt32(p.data) != 0;
                            PausedSince = Paused ? Environment.TickCount64 : 0;
                            App.WakeUi();
                            break;
                        case 4:
                            Muted = Marshal.ReadInt32(p.data) != 0;
                            App.WakeUi();
                            break;
                    }
                }
            }
        }

        private static void Opt(IntPtr ctx, string n, string v) => mpv_set_option_string(ctx, n, v);

        public void Cmd(params string[] args)
        {
            lock (_stateLock)
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
        }

        public void TogglePause()
        {
            lock (_stateLock)
            {
                _wantPaused = !Paused;
                if (Volatile.Read(ref _ctx) == IntPtr.Zero) { Paused = _wantPaused; return; }
            }
            Cmd("cycle", "pause");
        }

        public void SetPause(bool p)
        {
            lock (_stateLock)
            {
                _wantPaused = p;
                if (Volatile.Read(ref _ctx) == IntPtr.Zero) { Paused = p; return; }
            }
            Cmd("set", "pause", p ? "yes" : "no");
        }

        public void ToggleMute()
        {
            lock (_stateLock)
            {
                _wantMuted = !Muted;
                if (Volatile.Read(ref _ctx) == IntPtr.Zero) { Muted = _wantMuted; return; }
            }
            Cmd("cycle", "mute");
        }

        public void SetVolumeSmooth(int target)
        {
            target = Math.Clamp(target, 0, 150);
            int start;
            lock (_stateLock)
            {
                start = Volume;
                _wantVol = target;
                Volume = target;
                if (target > 0 && Muted)
                {
                    Muted = false;
                    _wantMuted = false;
                    if (Volatile.Read(ref _ctx) != IntPtr.Zero) Cmd("set", "mute", "no");
                }
                if (Volatile.Read(ref _ctx) == IntPtr.Zero) { App.WakeUi(); return; }
            }
            var delta = target - start;
            if (Math.Abs(delta) <= 2)
            {
                Interlocked.Increment(ref _volRampGen);
                Cmd("set", "volume", target.ToString(CultureInfo.InvariantCulture));
                App.WakeUi();
                return;
            }
            var gen = Interlocked.Increment(ref _volRampGen);
            _ = Task.Run(async () =>
            {
                const int steps = 5;
                for (int i = 1; i <= steps; i++)
                {
                    if (gen != Volatile.Read(ref _volRampGen) || _disposed) return;
                    double t = (double)i / steps;
                    double v = i == steps ? target : Math.Round(start + delta * (1.0 - (1.0 - t) * (1.0 - t)), 1);
                    Cmd("set", "volume", v.ToString("0.#", CultureInfo.InvariantCulture));
                    if (i < steps) await Task.Delay(16);
                }
            });
            App.WakeUi();
        }

        public void Dispose()
        {
            IntPtr ctx;
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
                ctx = Interlocked.Exchange(ref _ctx, IntPtr.Zero);
            }
            if (ctx != IntPtr.Zero)
            {
                try
                {
                    mpv_wakeup(ctx);
                    _thread?.Join(800);
                    mpv_terminate_destroy(ctx);
                }
                catch { }
            }
        }
    }

    private static readonly ConcurrentDictionary<string, Instance> _all = new();

    public static bool Has(string key) => _all.ContainsKey(key);
    public static bool HasVideo(string videoId) => _all.Values.Any(i => i.VideoId == videoId);
    public static bool IsPlaying(string videoId) => _all.TryGetValue(videoId, out var i) && !i.Paused;
    public static bool IsPaused(string videoId) => _all.TryGetValue(videoId, out var i) && i.Paused;
    public static bool IsMuted(string videoId) => _all.TryGetValue(videoId, out var i) && i.Muted;
    public static bool IsLoading(string videoId) => _all.TryGetValue(videoId, out var i) && i.Loading;
    public static int GetVolume(string videoId) => _all.TryGetValue(videoId, out var i) ? i.Volume : Math.Clamp(AppSettings.Current.Volume, 0, 150);
    public static double GetTimePos(string videoId) => _all.TryGetValue(videoId, out var i) ? i.LivePos() : 0;
    public static double GetDuration(string videoId) => _all.TryGetValue(videoId, out var i) ? i.Duration : 0;
    public static void AdjustVolume(string videoId, int delta)
    {
        if (_all.TryGetValue(videoId, out var i)) i.SetVolumeSmooth(i.Volume + delta);
    }
    public static int ActiveCount => _all.Values.Count(i => !i.Paused);

    /// <summary>Starts playing videoId in a background audio player (or resumes it if already loaded).</summary>
    public static void Play(string key, double? startOverride = null, int? volumeOverride = null, bool? muteOverride = null)
    {
        var split = key.IndexOf('|');
        var videoId = split >= 0 ? key[(split + 1)..] : key;
        if (string.IsNullOrEmpty(videoId)) return;
        if (_all.TryGetValue(key, out var existing))
        {
            if (volumeOverride.HasValue) existing.SetVolumeSmooth(volumeOverride.Value);
            existing.SetPause(false);
            App.Invalidate();
            return;
        }
        // Keep RAM bounded: cap simultaneous background mpv instances
        while (_all.Count >= MaxInstances)
        {
            var victim = _all.Values.OrderByDescending(i => i.Paused).ThenBy(i => i.CreatedAt).FirstOrDefault();
            if (victim == null) break;
            var victimKey = _all.FirstOrDefault(kv => ReferenceEquals(kv.Value, victim)).Key;
            if (victimKey != null) Stop(victimKey);
        }
        var start = startOverride ?? Shell.GetSavedTabPos(videoId) ?? Db.ResumePosition(videoId) ?? 0;
        var inst = new Instance(key, videoId, start, volumeOverride, muteOverride);
        if (!_all.TryAdd(key, inst)) return;
        App.Invalidate();
        _ = Task.Run(async () =>
        {
            try
            {
                string? local = null;
                if (Downloader.Instance.IsDownloaded(videoId, out var dl)) local = dl?.File;
                StreamInfo? stream = null;
                if (local == null)
                {
                    stream = Resolver.Instance.TryGet(videoId);
                    if (stream == null)
                    {
                        try { stream = await QuickStream.GetAsync(videoId); } catch { }
                    }
                    if (stream == null)
                    {
                        try { stream = await Resolver.Instance.GetAsync(videoId); } catch { }
                    }
                }
                if (!_all.TryGetValue(key, out var current) || !ReferenceEquals(current, inst))
                {
                    inst.Dispose();
                    return;
                }
                inst.Start(local, stream, "https://www.youtube.com/watch?v=" + videoId, start);
            }
            catch (Exception e)
            {
                Paths.Log($"bg play {videoId}: {e.Message}");
                if (_all.TryGetValue(key, out var current) && ReferenceEquals(current, inst))
                    Stop(key, saveToDb: false);
                else
                    inst.Dispose();
            }
        });
    }

    public static void TogglePause(string videoId)
    {
        if (_all.TryGetValue(videoId, out var inst)) inst.TogglePause();
        else Play(videoId);
        App.Invalidate();
    }

    public static void ToggleMute(string videoId)
    {
        if (_all.TryGetValue(videoId, out var inst)) inst.ToggleMute();
        App.Invalidate();
    }

    /// <summary>Stops the background player for videoId and optionally saves its position to DB (without creating ghost rows).</summary>
    public static double Stop(string videoId, bool saveToDb = true)
    {
        if (!_all.TryRemove(videoId, out var inst)) return 0;
        var pos = inst.LivePos();
        var dur = inst.Duration;
        if (saveToDb && pos > 2)
        {
            var id = inst.VideoId;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Db.UpdateLatestPosition(id, pos, dur); }
                catch { }
            });
        }
        inst.Cmd("set", "mute", "yes");
        inst.Cmd("set", "pause", "yes");
        ThreadPool.QueueUserWorkItem(_ => inst.Dispose());
        App.Invalidate();
        return pos;
    }

    /// <summary>Frees any background player that has been paused for >15 seconds (its position stays saved in DB).</summary>
    public static void ReapIdle()
    {
        var now = Environment.TickCount64;
        foreach (var kv in _all)
        {
            var inst = kv.Value;
            if (!inst.Loading && inst.Paused && inst.PausedSince > 0 && now - inst.PausedSince > 15_000)
                Stop(kv.Key);
        }
    }

    public static void StopAll()
    {
        foreach (var id in _all.Keys.ToArray()) Stop(id);
    }

    public static void DisposeAll()
    {
        foreach (var kv in _all.ToArray())
        {
            if (_all.TryRemove(kv.Key, out var inst)) inst.Dispose();
        }
    }
}
