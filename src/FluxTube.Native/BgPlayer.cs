using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;
using static FluxTube.Player.MpvNative;

namespace FluxTube;

/// <summary>
/// Lightweight audio-only libmpv instances for background tabs so 2+ videos can play
/// simultaneously, be paused/muted directly from the tab strip, and seamlessly hand off
/// their playback position when switching back to the main video player.
/// </summary>
public static class BgPlayers
{
    private sealed class Instance : IDisposable
    {
        public readonly string VideoId;
        private IntPtr _ctx;
        private Thread? _thread;
        private volatile bool _disposed;
        public volatile bool Paused;
        public volatile bool Muted;
        public volatile bool Loading = true;
        public long PausedSince;
        public double TimePos;
        public double Duration;

        public Instance(string videoId) { VideoId = videoId; }

        public void Start(string? localFile, StreamInfo? stream, string fallbackUrl, double startSeconds)
        {
            _ctx = mpv_create();
            if (_ctx == IntPtr.Zero) return;
            Opt("vid", "no");
            Opt("vo", "null");
            Opt("idle", "yes");
            Opt("keep-open", "yes");
            Opt("osc", "no");
            Opt("osd-bar", "no");
            Opt("cache", "yes");
            Opt("demuxer-max-bytes", "4MiB");
            Opt("demuxer-max-back-bytes", "1MiB");
            Opt("demuxer-seekable-cache", "no");
            Opt("cache-pause-initial", "no");
            Opt("network-timeout", "15");
            Opt("ad-lavc-threads", "1");
            var liveVol = MpvPlayer.Instance.IsReady ? MpvPlayer.Instance.GetDouble("volume") : 0;
            var vol = liveVol > 1 ? liveVol : Math.Clamp(AppSettings.Current.Volume, 1, 150);
            Opt("volume", vol.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            if (App.Mute) Opt("mute", "yes");
            var proxy = AppSettings.Current.Proxy;
            if (!string.IsNullOrWhiteSpace(proxy)) Opt("http-proxy", proxy);
            Opt("ytdl", "yes");
            Opt("ytdl-format", "ba/b");
            Opt("script-opts", "ytdl_hook-ytdl_path=" + Paths.YtDlp.Replace('\\', '/'));
            foreach (var kv in YtDlp.CommonRawOptions(forPlayer: true))
                Opt("ytdl-raw-options-append", kv);

            if (mpv_initialize(_ctx) < 0) { mpv_terminate_destroy(_ctx); _ctx = IntPtr.Zero; return; }
            mpv_observe_property(_ctx, 1, "time-pos", Format.Double);
            mpv_observe_property(_ctx, 2, "duration", Format.Double);
            mpv_observe_property(_ctx, 3, "pause", Format.Flag);
            mpv_observe_property(_ctx, 4, "mute", Format.Flag);

            var startOpt = startSeconds > 0.3 ? "start=" + startSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "start=0";
            if (!string.IsNullOrEmpty(localFile))
                Cmd("loadfile", localFile, "replace", "-1", startOpt);
            else if (stream != null)
            {
                var url = stream.Audio ?? stream.Video;
                Cmd("loadfile", url, "replace", "-1", startOpt);
            }
            else
                Cmd("loadfile", fallbackUrl, "replace", "-1", startOpt);
            Set("pause", "no");
            Paused = false;
            Loading = false;

            _thread = new Thread(Loop) { IsBackground = true, Name = "mpv-bg-" + VideoId };
            _thread.Start();
            App.WakeUi();
        }

        private void Loop()
        {
            while (!_disposed && _ctx != IntPtr.Zero)
            {
                var evPtr = mpv_wait_event(_ctx, 0.5);
                if (evPtr == IntPtr.Zero) continue;
                var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
                if (ev.event_id == EventId.Shutdown) return;
                if (ev.event_id == EventId.PropertyChange)
                {
                    var p = Marshal.PtrToStructure<MpvEventProperty>(ev.data);
                    if (p.data == IntPtr.Zero) continue;
                    switch (ev.reply_userdata)
                    {
                        case 1: TimePos = Marshal.PtrToStructure<double>(p.data); break;
                        case 2: Duration = Marshal.PtrToStructure<double>(p.data); break;
                        case 3:
                            Paused = Marshal.ReadInt32(p.data) != 0;
                            PausedSince = Paused ? Environment.TickCount64 : 0;
                            App.WakeUi();
                            break;
                        case 4: Muted = Marshal.ReadInt32(p.data) != 0; App.WakeUi(); break;
                    }
                }
            }
        }

        private void Opt(string n, string v) => mpv_set_option_string(_ctx, n, v);
        public void Set(string n, string v) { if (_ctx != IntPtr.Zero && !_disposed) mpv_set_property_string(_ctx, n, v); }
        public void Cmd(params string[] args)
        {
            if (_ctx == IntPtr.Zero || _disposed) return;
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

        public void TogglePause() => Cmd("cycle", "pause");
        public void SetPause(bool p) => Set("pause", p ? "yes" : "no");
        public void ToggleMute() => Cmd("cycle", "mute");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var c = _ctx;
            _ctx = IntPtr.Zero;
            if (c != IntPtr.Zero)
            {
                try
                {
                    mpv_wakeup(c);
                    _thread?.Join(800);
                    mpv_terminate_destroy(c);
                }
                catch { }
            }
        }
    }

    private static readonly ConcurrentDictionary<string, Instance> _all = new();

    public static bool Has(string videoId) => _all.ContainsKey(videoId);
    public static bool IsPlaying(string videoId) => _all.TryGetValue(videoId, out var i) && !i.Paused;
    public static bool IsPaused(string videoId) => _all.TryGetValue(videoId, out var i) && i.Paused;
    public static bool IsMuted(string videoId) => _all.TryGetValue(videoId, out var i) && i.Muted;
    public static bool IsLoading(string videoId) => _all.TryGetValue(videoId, out var i) && i.Loading;
    public static int ActiveCount => _all.Values.Count(i => !i.Paused);

    /// <summary>Starts playing videoId in a background audio player (or resumes it if already loaded).</summary>
    public static void Play(string videoId, double? startOverride = null)
    {
        if (string.IsNullOrEmpty(videoId)) return;
        if (_all.TryGetValue(videoId, out var existing))
        {
            existing.SetPause(false);
            App.Invalidate();
            return;
        }
        var inst = new Instance(videoId);
        if (!_all.TryAdd(videoId, inst)) return;
        App.Invalidate();
        var start = startOverride ?? Db.ResumePosition(videoId) ?? 0;
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
                if (!_all.ContainsKey(videoId)) { inst.Dispose(); return; }
                inst.Start(local, stream, "https://www.youtube.com/watch?v=" + videoId, start);
            }
            catch (Exception e)
            {
                Paths.Log($"bg play {videoId}: {e.Message}");
                Stop(videoId);
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

    /// <summary>Stops the background player for videoId and saves its last position to DB. Returns its last TimePos.</summary>
    public static double Stop(string videoId)
    {
        if (!_all.TryRemove(videoId, out var inst)) return 0;
        var pos = inst.TimePos;
        var dur = inst.Duration;
        if (pos > 2)
        {
            var id = videoId;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var sid = Db.StartWatch(new VideoItem { VideoId = id }, "tab");
                    Db.UpdateWatch(sid, null, null, pos, dur, 0);
                }
                catch { }
            });
        }
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
