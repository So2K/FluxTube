using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluxTube.Player;
using FluxTube.Services;
using static FluxTube.Player.MpvNative;

namespace FluxTube;

/// <summary>
/// Single reusable hardware-decoded muted HD live video preview player for hovered cards on Home/Feed grids.
/// Renders into a mouse-transparent Win32 child HWND (WM_NCHITTEST -> HTTRANSPARENT) clipped to the card's
/// 12px rounded rectangle so 100% of clicks, middle-clicks, right-clicks, and scrolling pass straight through!
/// Shows crisp hq720.jpg underneath until the very first decoded video frame arrives (zero black flash).
/// </summary>
public static class PreviewPlayer
{
    public static nint Hwnd { get; private set; }

    private static IntPtr _ctx;
    private static Thread? _eventThread;
    private static readonly object _initLock = new();
    private static volatile bool _disposed, _initializing;

    private static string? _wantId, _activeId, _loadingId, _readyId;
    private static R _wantRect, _wantClip;
    private static float _wantRadius;
    private static long _wantFrame, _lastWantedAt;
    private static int _reqGen;

    private static bool _visible;
    private static W.RECT _lastPx;
    private static int _lastRgnHash;

    public static bool IsReadyFor(string videoId) => _readyId == videoId && !_disposed;

    public static unsafe void RegisterAndCreate(nint parent)
    {
        if (Hwnd != 0) return;
        var inst = W.GetModuleHandleW(0);
        fixed (char* cls = "FluxPreviewHost")
        {
            var wc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW),
                lpfnWndProc = &Proc,
                hInstance = inst,
                lpszClassName = cls,
                hbrBackground = W.CreateSolidBrush(0x000E0A0A),
            };
            W.RegisterClassExW(&wc);
        }
        Hwnd = W.CreateWindowExW(W.WS_EX_TRANSPARENT, "FluxPreviewHost", null,
            W.WS_CHILD | W.WS_CLIPSIBLINGS, 0, 0, 16, 9, parent, 0, inst, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint h, uint m, nuint w, nint l)
    {
        if (m == W.WM_NCHITTEST) return -1; // HTTRANSPARENT: every click/hover/wheel passes cleanly to App.Hwnd
        if (m == W.WM_ERASEBKGND) return 1;
        return W.DefWindowProcW(h, m, w, l);
    }

    private static void EnsureMpv()
    {
        if (Volatile.Read(ref _ctx) != IntPtr.Zero || _disposed || Hwnd == 0) return;
        lock (_initLock)
        {
            if (Volatile.Read(ref _ctx) != IntPtr.Zero || _disposed || _initializing) return;
            _initializing = true;
            var hwnd = Hwnd;
            try
            {
                var ctx = mpv_create();
                if (ctx == IntPtr.Zero) return;
                try
                {
                    mpv_set_option_string(ctx, "wid", hwnd.ToInt64().ToString());
                    mpv_set_option_string(ctx, "vo", "gpu-next");
                    mpv_set_option_string(ctx, "gpu-api", "d3d11");
                    mpv_set_option_string(ctx, "hwdec", "auto-safe");
                    mpv_set_option_string(ctx, "aid", "no");
                    mpv_set_option_string(ctx, "mute", "yes");
                    mpv_set_option_string(ctx, "osc", "no");
                    mpv_set_option_string(ctx, "osd-bar", "no");
                    mpv_set_option_string(ctx, "border", "no");
                    mpv_set_option_string(ctx, "idle", "yes");
                    mpv_set_option_string(ctx, "keep-open", "yes");
                    mpv_set_option_string(ctx, "loop-file", "inf");
                    mpv_set_option_string(ctx, "cache", "yes");
                    mpv_set_option_string(ctx, "demuxer-max-bytes", "4MiB");
                    mpv_set_option_string(ctx, "demuxer-max-back-bytes", "1MiB");
                    mpv_set_option_string(ctx, "demuxer-seekable-cache", "no");
                    mpv_set_option_string(ctx, "cache-pause-initial", "no");
                    mpv_set_option_string(ctx, "network-timeout", "8");
                    mpv_set_option_string(ctx, "vd-lavc-threads", "4");
                    mpv_set_option_string(ctx, "background-color", "#0A0A0E");
                    mpv_set_option_string(ctx, "ytdl", "no");
                    mpv_set_option_string(ctx, "input-default-bindings", "no");
                    mpv_set_option_string(ctx, "input-vo-keyboard", "no");
                    mpv_set_option_string(ctx, "gpu-shader-cache", "yes");
                    mpv_set_option_string(ctx, "gpu-shader-cache-dir", Path.Combine(Paths.Data, "mpv-shaders").Replace('\\', '/'));

                    if (mpv_initialize(ctx) < 0)
                    {
                        mpv_terminate_destroy(ctx);
                        return;
                    }
                    Volatile.Write(ref _ctx, ctx);
                    _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-preview" };
                    _eventThread.Start();
                }
                catch
                {
                    mpv_terminate_destroy(ctx);
                    throw;
                }
            }
            catch (Exception e) { Paths.Log("preview mpv init: " + e.Message); }
            finally { _initializing = false; }
        }
    }

    private static void EventLoop()
    {
        while (!_disposed)
        {
            var ctx = Volatile.Read(ref _ctx);
            if (ctx == IntPtr.Zero) return;
            var evPtr = mpv_wait_event(ctx, 0.5);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            if (ev.event_id == EventId.Shutdown) return;
            if (ev.event_id is EventId.PlaybackRestart or EventId.VideoReconfig)
            {
                var gen = Volatile.Read(ref _reqGen);
                var lid = _loadingId;
                App.Post(() =>
                {
                    if (gen == Volatile.Read(ref _reqGen) && lid != null && lid == _activeId)
                    {
                        _readyId = lid;
                        App.Invalidate();
                    }
                });
            }
            else if (ev.event_id == EventId.EndFile)
            {
                var ef = Marshal.PtrToStructure<MpvEventEndFile>(ev.data);
                if (ef.reason == 4)
                {
                    var gen = Volatile.Read(ref _reqGen);
                    App.Post(() => { if (gen == Volatile.Read(ref _reqGen)) { _readyId = null; App.Invalidate(); } });
                }
            }
        }
    }

    private static void Cmd(params string[] args)
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

    /// <summary>Called by Cards.GridCard when hovering a video card on Home/Feed for >= 0.35s.</summary>
    public static void Want(string videoId, R rect, R clip, float radius)
    {
        if (_disposed || App.Test || Hwnd == 0) return;
        if (Menus.Open || AccountMenu.Open || SharePanel.Open || ThemeCodeEditor.Open || AudioStudioPanel.Open || Shell.TabPreviewRect.W > 0 || UI.AutoScrollActive) return;

        _wantId = videoId;
        _wantRect = rect;
        _wantClip = clip;
        _wantRadius = radius;
        _wantFrame = Gfx.Frame;
        _lastWantedAt = UI.Now;

        if (_activeId != videoId)
        {
            _activeId = videoId;
            _readyId = null;
            _loadingId = videoId;
            int gen = Interlocked.Increment(ref _reqGen);
            _ = Task.Run(async () =>
            {
                try
                {
                    EnsureMpv();
                    if (gen != Volatile.Read(ref _reqGen) || Volatile.Read(ref _ctx) == IntPtr.Zero) return;
                    var stream = QuickStream.TryGet(videoId) ?? await QuickStream.GetAsync(videoId);
                    if (gen != Volatile.Read(ref _reqGen) || stream == null || string.IsNullOrEmpty(stream.Video)) return;
                    Cmd("loadfile", stream.Video, "replace", "-1", "start=0");
                    Cmd("set", "pause", "no");
                }
                catch (Exception e) { Paths.Log($"preview {videoId}: {e.Message}"); }
            });
        }
    }

    /// <summary>Immediately hides & stops any active card preview (e.g. when navigating or clicking a video).</summary>
    public static void HideNow()
    {
        _wantId = null;
        _activeId = null;
        _loadingId = null;
        _readyId = null;
        Interlocked.Increment(ref _reqGen);
        if (_visible && Hwnd != 0)
        {
            _visible = false;
            W.ShowWindow(Hwnd, W.SW_HIDE);
        }
        if (Volatile.Read(ref _ctx) != IntPtr.Zero) Cmd("stop");
    }

    /// <summary>Called once per frame after drawing, right after Shell.AfterFrame().</summary>
    public static void AfterFrame(R? miniVideoRect, R toastRect)
    {
        if (_disposed || Hwnd == 0) return;
        bool blocked = Shell.IsFullscreen || Menus.Open || AccountMenu.Open || SharePanel.Open || ThemeCodeEditor.Open || AudioStudioPanel.Open
            || Shell.TabPreviewRect.W > 0 || UI.AutoScrollActive || W.IsIconic(App.Hwnd);

        bool activeThisFrame = !blocked && _wantFrame == Gfx.Frame && _wantId != null;
        if (!activeThisFrame)
        {
            if (_visible)
            {
                _visible = false;
                W.ShowWindow(Hwnd, W.SW_HIDE);
            }
            if (_activeId != null && (blocked || UI.Now - _lastWantedAt > 200))
            {
                _activeId = null;
                _loadingId = null;
                _readyId = null;
                Interlocked.Increment(ref _reqGen);
                if (Volatile.Read(ref _ctx) != IntPtr.Zero) Cmd("stop");
            }
            return;
        }

        if (_readyId != _wantId)
        {
            if (_visible) { _visible = false; W.ShowWindow(Hwnd, W.SW_HIDE); }
            return;
        }

        var vis = _wantRect.Intersect(_wantClip);
        if (vis.W < 24 || vis.H < 24)
        {
            if (_visible) { _visible = false; W.ShowWindow(Hwnd, W.SW_HIDE); }
            return;
        }

        var s = Gfx.Scale;
        var px = new W.RECT
        {
            L = (int)MathF.Round(_wantRect.X * s),
            T = (int)MathF.Round(_wantRect.Y * s),
            R = (int)MathF.Round(_wantRect.Right * s),
            B = (int)MathF.Round(_wantRect.Bottom * s),
        };
        int vw = Math.Max(1, px.R - px.L), vh = Math.Max(1, px.B - px.T);

        bool moved = !_visible || px.L != _lastPx.L || px.T != _lastPx.T || px.R != _lastPx.R || px.B != _lastPx.B;
        if (moved)
        {
            _lastPx = px;
            _visible = true;
            // Place below App.VideoHwnd (floating mini-player) if visible, otherwise top of child z-order
            nint zAfter = !Pip.Active && App.VideoHwnd != 0 && W.IsWindowVisible(App.VideoHwnd) ? App.VideoHwnd : 0;
            W.SetWindowPos(Hwnd, zAfter, px.L, px.T, vw, vh, W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
        }

        int cl = Math.Clamp((int)MathF.Round((vis.X - _wantRect.X) * s), 0, vw);
        int ct = Math.Clamp((int)MathF.Round((vis.Y - _wantRect.Y) * s), 0, vh);
        int cr = Math.Clamp((int)MathF.Round((vis.Right - _wantRect.X) * s), 0, vw);
        int cb = Math.Clamp((int)MathF.Round((vis.Bottom - _wantRect.Y) * s), 0, vh);
        bool clipped = cl > 0 || ct > 0 || cr < vw || cb < vh;

        int radPx = _wantRadius > 0.5f ? Math.Max(1, (int)MathF.Round(_wantRadius * s)) : 0;
        bool cutMini = miniVideoRect is { W: > 0 } mr && mr.Intersect(_wantRect) is { W: > 0, H: > 0 };
        bool cutToast = toastRect.W > 0 && toastRect.Intersect(_wantRect) is { W: > 0, H: > 0 };

        int hash = HashCode.Combine(cl, ct, cr, cb, vw, vh, radPx, cutMini ? 1 : 0);
        if (cutMini) hash = HashCode.Combine(hash, (int)miniVideoRect!.Value.X, (int)miniVideoRect.Value.Y);
        if (cutToast) hash = HashCode.Combine(hash, (int)toastRect.X, (int)toastRect.Y);

        if (hash != _lastRgnHash || moved)
        {
            _lastRgnHash = hash;
            nint rgn = radPx > 0
                ? W.CreateRoundRectRgn(0, 0, vw + 1, vh + 1, radPx * 2, radPx * 2)
                : W.CreateRectRgn(cl, ct, cr, cb);
            if (radPx > 0 && clipped)
            {
                var crgn = W.CreateRectRgn(cl, ct, cr, cb);
                W.CombineRgn(rgn, rgn, crgn, 1 /*RGN_AND*/);
                W.DeleteObject(crgn);
            }
            void Subtract(R hr)
            {
                var cut = W.CreateRectRgn(
                    (int)MathF.Floor((hr.X - _wantRect.X) * s), (int)MathF.Floor((hr.Y - _wantRect.Y) * s),
                    (int)MathF.Ceiling((hr.Right - _wantRect.X) * s), (int)MathF.Ceiling((hr.Bottom - _wantRect.Y) * s));
                W.CombineRgn(rgn, rgn, cut, 4 /*RGN_DIFF*/);
                W.DeleteObject(cut);
            }
            if (cutMini) Subtract(miniVideoRect!.Value);
            if (cutToast) Subtract(toastRect);
            W.SetWindowRgn(Hwnd, rgn, true);
        }
    }

    public static void Shutdown()
    {
        _disposed = true;
        var ctx = Interlocked.Exchange(ref _ctx, IntPtr.Zero);
        if (ctx != IntPtr.Zero)
        {
            try
            {
                mpv_wakeup(ctx);
                _eventThread?.Join(800);
                mpv_terminate_destroy(ctx);
            }
            catch { }
        }
    }
}
