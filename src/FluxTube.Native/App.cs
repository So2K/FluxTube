using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

public static unsafe class App
{
    public static nint Hwnd { get; private set; }
    public static nint VideoHwnd { get; private set; }
    public static bool Mute, NoActivate, Bench, Test;

    private static readonly ConcurrentQueue<Action> _posts = new();
    private static readonly List<(long Due, Action Act)> _timers = new();
    private static bool _inFrame, _tracking, _wakePending, _occludedTimer, _animating;
    private static long _lastFrameTs;
    private static nint _arrow, _hand, _ibeam;
    private const uint WM_POST = W.WM_APP + 1, WM_WAKE = W.WM_APP + 2;

    // frame time statistics (CPU time to build and submit a frame, without waiting for vsync)
    private static readonly List<double> _frameMs = new(4096);
    public static double LastFrameMs { get; private set; }

    /// <summary>Runs a on the UI thread after the current message; safe from any thread.</summary>
    public static void Post(Action a)
    {
        if (Hwnd == 0) return;
        _posts.Enqueue(a);
        W.PostMessageW(Hwnd, WM_POST, 0, 0);
    }

    /// <summary>Cheap "something changed" from worker threads; many calls collapse into one frame.</summary>
    public static void WakeUi()
    {
        if (Hwnd == 0) return;
        if (Interlocked.Exchange(ref _wakePending, true)) return;
        W.PostMessageW(Hwnd, WM_WAKE, 0, 0);
    }

    public static void Invalidate() { if (Hwnd != 0) W.InvalidateRect(Hwnd, null, 0); }

    public static void After(int ms, Action a)
    {
        if (Hwnd == 0) return;
        _timers.Add((Environment.TickCount64 + Math.Max(1, ms), a));
        RescheduleAfterTimer();
    }

    private static void RescheduleAfterTimer()
    {
        if (_timers.Count == 0) { W.KillTimer(Hwnd, 2); return; }
        long now = Environment.TickCount64, earliest = long.MaxValue;
        for (int i = 0; i < _timers.Count; i++) if (_timers[i].Due < earliest) earliest = _timers[i].Due;
        uint delay = (uint)Math.Clamp(earliest - now, 15, 60_000);
        W.SetTimer(Hwnd, 2, delay, 0);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Paths.MigrateOldData();
        // Fast ~12ms URL router: forwards non-YouTube URLs straight to user's browser & routes YouTube URLs to open FluxTube!
        if (LinkRouter.TryHandleCliRoute(args)) return 0;
        ProfileOptimization.SetProfileRoot(Paths.Data);
        // SustainedLowLatency prevents stop-the-world Gen2 GC pauses during 144Hz scrolling & Music Mode
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        try
        {
            return Run(args);
        }
        catch (Exception e)
        {
            Paths.Log("FATAL: " + e);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Contains("--test-loopback")) return AudioAnalyzer.SelfTestProcessLoopback();
        if (args.Contains("--self-test")) return Gfx.RunGpuSelfTest();
        Mute = args.Contains("--mute");
        NoActivate = args.Contains("--noactivate");
        Bench = args.Contains("--bench");
        // --test: for scripted checks while the user works. Silent, never takes focus or input, stays behind other windows.
        Test = args.Contains("--test");
        if (Test) Mute = NoActivate = true;

        W.SetPreferredAppMode(2); // dark menus
        var inst = W.GetModuleHandleW(0);
        _arrow = W.LoadCursorW(0, 32512);
        _hand = W.LoadCursorW(0, 32649);
        _ibeam = W.LoadCursorW(0, 32513);
        fixed (char* cls = "FluxTubeMain")
        fixed (char* vcls = "FluxVideoHost")
        {
            var wc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst, hCursor = _arrow, lpszClassName = cls,
                // 32512 is where the build puts the application icon
                hIcon = W.LoadIconW(inst, 32512), hIconSm = W.LoadImageById(inst, 32512, 1, 0, 0, 0x0040 | 0x8000),
            };
            W.RegisterClassExW(&wc);
            var vc = new W.WNDCLASSEXW { cbSize = (uint)sizeof(W.WNDCLASSEXW), lpfnWndProc = &VideoHost.Proc, hInstance = inst, hCursor = _arrow, lpszClassName = vcls,
                hbrBackground = W.CreateSolidBrush(0) };
            W.RegisterClassExW(&vc);
        }
        Suggest.Register();
        Themes.Load();

        var s = AppSettings.Current;
        var sys = W.GetDpiForSystem() / 96f;
        Hwnd = W.CreateWindowExW(0, "FluxTubeMain", "FluxTube", W.WS_OVERLAPPEDWINDOW | W.WS_CLIPCHILDREN,
            unchecked((int)0x80000000), unchecked((int)0x80000000), (int)(s.WindowWidth * sys), (int)(s.WindowHeight * sys), 0, 0, inst, 0);
        FrameColors();
        Themes.Changed += () => { FrameColors(); Edit.RepaintAll(); Shell.PushPlayerState(); };

        // the system caption is replaced by our own top bar: apply the new frame now
        W.SetWindowPos(Hwnd, 0, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_FRAMECHANGED);
        VideoHwnd = W.CreateWindowExW(0, "FluxVideoHost", null, W.WS_CHILD | W.WS_CLIPCHILDREN | W.WS_CLIPSIBLINGS, 0, 0, 16, 9, Hwnd, 0, inst, 0);
        PreviewPlayer.RegisterAndCreate(Hwnd);

        Gfx.Init(Hwnd);
        DropTarget.InitOle();
        DropTarget.Attach(Hwnd);
        DropTarget.Attach(VideoHwnd);
        W.AddClipboardFormatListener(Hwnd);
        if (!Test) LinkRouter.RegisterProtocolAndRouter();
        UiThread.Post = a => Post(a);
        Shell.Init(args);

        if (Test)
        {
            W.EnableWindow(Hwnd, 0);
            W.SetWindowPos(Hwnd, 1 /*HWND_BOTTOM*/, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOACTIVATE);
        }
        W.ShowWindow(Hwnd, NoActivate ? W.SW_SHOWNOACTIVATE : W.SW_SHOW);
        if (Test) W.SetWindowPos(Hwnd, 1, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOACTIVATE);
        W.SetTimer(Hwnd, 1, 500, 0);
        ProfileOptimization.StartProfile("native.jitprofile");

        try
        {
            W.MSG msg;
            while (true)
            {
                if (_animating && !Gfx.Occluded && !W.IsIconic(Hwnd))
                {
                    // Drain all queued high-polling-rate mouse/wheel messages first so scrolling never starves rendering
                    bool quit = false;
                    while (W.PeekMessageW(&msg, 0, 0, 0, 1 /*PM_REMOVE*/))
                    {
                        if (msg.message == 0x0012 /*WM_QUIT*/) { quit = true; break; }
                        W.TranslateMessage(&msg);
                        W.DispatchMessageW(&msg);
                    }
                    if (quit) break;
                    if (_animating && !Gfx.Occluded && !W.IsIconic(Hwnd))
                    {
                        _animating = false;
                        Frame();
                        W.ValidateRect(Hwnd, null);
                        if (UI.TakeAnimate())
                        {
                            if (Gfx.Occluded)
                            {
                                if (!_occludedTimer) { _occludedTimer = true; W.SetTimer(Hwnd, 3, 250, 0); }
                            }
                            else _animating = true;
                        }
                    }
                    continue;
                }
                int r = W.GetMessageW(&msg, 0, 0, 0);
                if (r <= 0) break;
                W.TranslateMessage(&msg);
                W.DispatchMessageW(&msg);
            }
            return 0;
        }
        finally
        {
            W.RemoveClipboardFormatListener(Hwnd);
            DropTarget.ShutdownOle();
        }
    }

    /// <summary>The window frame (resize border, snap preview) in the theme's colours.</summary>
    private static void FrameColors()
    {
        if (Hwnd == 0) return;
        int dark = Themes.Current.Light ? 0 : 1;
        W.DwmSetWindowAttribute(Hwnd, 20, &dark, 4);
        var bg = Themes.Current.Bg;
        uint caption = W.Rgb((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg);
        W.DwmSetWindowAttribute(Hwnd, 35, &caption, 4);
    }

    private static void Frame()
    {
        if (_inFrame || W.IsIconic(Hwnd)) return;
        _inFrame = true;
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            var now = Environment.TickCount64;
            UI.Dt = _lastFrameTs == 0 ? 1.0 / 120.0 : Math.Clamp(Stopwatch.GetElapsedTime(_lastFrameTs, t0).TotalSeconds, 0.0005, 0.05);
            UI.Now = now;
            _lastFrameTs = t0;

            int up0 = Images.Uploaded, tx0 = Gfx.TextCreated;
            Images.Upload();
            var upMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Themes.Tick();
            Gfx.Begin(Th.Bg);
            UI.BeginFrame();
            Shell.Draw();
            Sel.EndFrame();
            UI.EndFrame();
            var drawMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds - upMs;
            if (!Gfx.End()) UI.Animate();
            // build + flush: everything the CPU does for a frame; waiting for vsync is not included
            LastFrameMs = upMs + drawMs + Gfx.FlushMs;
            if (Bench)
            {
                _frameMs.Add(LastFrameMs);
                _sumDraw += drawMs; _sumFlush += Gfx.FlushMs; _sumUp += upMs;
                if (LastFrameMs > 8) Paths.Log($"slow frame {LastFrameMs:0.0} ms: upload {upMs:0.0} ({Images.Uploaded - up0} img) draw {drawMs:0.0} ({Gfx.TextCreated - tx0} new text) flush {Gfx.FlushMs:0.0}");
            }
            Shell.AfterFrame();
        }
        catch (Exception e)
        {
            Paths.Log("frame: " + e);
            // the click that led here is spent: left armed it would fire again on every following frame
            UI.EndFrame();
            UI.Modal = null;
            try { Gfx.End(); } catch { }
        }
        finally { _inFrame = false; }
    }

    public static string BenchReport()
    {
        if (_frameMs.Count == 0) return "no frames";
        var a = _frameMs.ToArray();
        Array.Sort(a);
        return $"frames={a.Length} cpu ms p50={a[a.Length / 2]:0.00} p95={a[(int)(a.Length * 0.95)]:0.00} p99={a[(int)(a.Length * 0.99)]:0.00} max={a[^1]:0.00} | avg upload={_sumUp / a.Length:0.00} draw={_sumDraw / a.Length:0.00} (text {Gfx.TextDrawMs / a.Length:0.00} in {Gfx.TextDraws / a.Length} calls) flush={_sumFlush / a.Length:0.00}";
    }

    private static double _sumDraw, _sumFlush, _sumUp;
    private static int _lastMouseRawX = int.MinValue, _lastMouseRawY = int.MinValue;
    public static void ResetBench() { _frameMs.Clear(); _sumDraw = _sumFlush = _sumUp = 0; Gfx.TextDrawMs = 0; Gfx.TextDraws = 0; }

    private static bool UpdateMouse(nint l)
    {
        int rx = W.LoWord(l), ry = W.HiWord(l);
        UI.LastInput = Environment.TickCount64;
        if (rx == _lastMouseRawX && ry == _lastMouseRawY) return false;
        _lastMouseRawX = rx; _lastMouseRawY = ry;
        UI.MX = rx / Gfx.Scale;
        UI.MY = ry / Gfx.Scale;
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint h, uint m, nuint w, nint l)
    {
        try
        {
            switch (m)
            {
                case W.WM_PAINT:
                    _animating = false;
                    Frame();
                    W.ValidateRect(h, null);
                    if (UI.TakeAnimate())
                    {
                        if (Gfx.Occluded)
                        {
                            // Window is occluded (covered/locked): Present(1,0) does not wait for VSync, so pause spin!
                            if (!_occludedTimer) { _occludedTimer = true; W.SetTimer(h, 3, 250, 0); }
                        }
                        else _animating = true;
                    }
                    return 0;
                case W.WM_ERASEBKGND:
                    return 1;
                case W.WM_NCCALCSIZE:
                    if (w != 0)
                    {
                        // no system caption or borders: the whole window is ours. Maximized windows hang over the screen
                        // edge by the frame thickness, so that much is cut off again.
                        if (W.IsZoomed(h) && !Shell.IsFullscreen)
                        {
                            var rc = (W.RECT*)l;
                            var dpi = W.GetDpiForWindow(h);
                            int fx = W.GetSystemMetricsForDpi(32, dpi) + W.GetSystemMetricsForDpi(92, dpi), fy = W.GetSystemMetricsForDpi(33, dpi) + W.GetSystemMetricsForDpi(92, dpi);
                            rc->L += fx; rc->R -= fx; rc->T += fy; rc->B -= fy;
                        }
                        return 0;
                    }
                    break;
                case W.WM_NCHITTEST:
                {
                    if (Shell.IsFullscreen) return 1;
                    var p = new W.POINT { X = W.LoWord(l), Y = W.HiWord(l) };
                    W.ScreenToClient(h, &p);
                    W.RECT rc;
                    W.GetClientRect(h, &rc);
                    var sc = W.GetDpiForWindow(h) / 96f;
                    if (!W.IsZoomed(h))
                    {
                        int b = (int)(6 * sc);
                        bool left = p.X < b, right = p.X >= rc.R - b, top = p.Y < b, bottom = p.Y >= rc.B - b;
                        if (top && left) return 13;
                        if (top && right) return 14;
                        if (bottom && left) return 16;
                        if (bottom && right) return 17;
                        if (left) return 10;
                        if (right) return 11;
                        if (top) return 12;
                        if (bottom) return 15;
                    }
                    // the top bar drags the window wherever it has no control of its own
                    if (p.Y / Gfx.Scale < Shell.TopH && !Shell.TopBarControlAt(p.X / Gfx.Scale, p.Y / Gfx.Scale)) return 2;
                    return 1;
                }
                case W.WM_SIZE:
                    Suggest.Hide();
                    if ((int)w == 1 /*SIZE_MINIMIZED*/)
                    {
                        PreviewPlayer.HideNow();
                        if (Gfx.DC != null)
                        {
                            Gfx.TrimMemory(minimized: true);
                            Images.TrimMemory(minimized: true);
                            MusicArt.Trim(MpvPlayer.Instance.CurrentVideoId);
                        }
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    }
                    else if (Gfx.DC != null)
                    {
                        Gfx.Resize();
                        Invalidate();
                    }
                    return 0;
                case W.WM_DPICHANGED:
                {
                    var rc = (W.RECT*)l;
                    W.SetWindowPos(h, 0, rc->L, rc->T, rc->R - rc->L, rc->B - rc->T, W.SWP_NOZORDER | W.SWP_NOACTIVATE);
                    if (Gfx.DC != null) { Gfx.Resize(); Edit.RepaintAll(); Invalidate(); }
                    return 0;
                }
                case W.WM_GETMINMAXINFO:
                {
                    var mm = (W.MINMAXINFO*)l;
                    var sc = W.GetDpiForWindow(h) / 96f;
                    mm->minTrackSize = new W.POINT { X = (int)(860 * sc), Y = (int)(540 * sc) };
                    return 0;
                }
                case W.WM_MOUSEMOVE:
                    if (!UpdateMouse(l) && _tracking) return 0;
                    if (!_tracking)
                    {
                        var tme = new W.TRACKMOUSEEVENT { cbSize = (uint)sizeof(W.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = h };
                        W.TrackMouseEvent(&tme);
                        _tracking = true;
                    }
                    Invalidate();
                    return 0;
                case W.WM_MOUSELEAVE:
                    _tracking = false;
                    _lastMouseRawX = _lastMouseRawY = int.MinValue;
                    if (!UI.Down) { UI.MX = UI.MY = -1; }
                    Invalidate();
                    return 0;
                case W.WM_LBUTTONDOWN:
                case W.WM_LBUTTONDBLCLK:
                    UpdateMouse(l);
                    if (UI.AutoScrollActive) { UI.StopAutoScroll(); Invalidate(); return 0; }
                    W.SetCapture(h);
                    bool inFocusedEdit = Edit.IsPointInFocusedSingleLine(UI.MX, UI.MY);
                    if (!inFocusedEdit && W.GetFocus() != h) W.SetFocus(h);
                    if (!inFocusedEdit) Suggest.Hide();
                    UI.Down = UI.Pressed = true;
                    Invalidate();
                    return 0;
                case W.WM_LBUTTONUP:
                    UpdateMouse(l);
                    UI.Down = false;
                    if (!UI.MiddleDown) W.ReleaseCapture();
                    UI.Released = true;
                    Invalidate();
                    return 0;
                case W.WM_RBUTTONUP:
                    UpdateMouse(l);
                    if (UI.AutoScrollActive) { UI.StopAutoScroll(); Invalidate(); return 0; }
                    UI.RightClicked = true;
                    Invalidate();
                    return 0;
                case W.WM_MBUTTONDOWN:
                    UpdateMouse(l);
                    if (UI.AutoScrollActive) { UI.StopAutoScroll(); Invalidate(); return 0; }
                    W.SetCapture(h);
                    UI.MiddleDown = true;
                    UI.MiddleDownX = UI.MX;
                    UI.MiddleDownY = UI.MY;
                    UI.MiddleDownAt = Environment.TickCount64;
                    After(225, Invalidate);
                    Invalidate();
                    return 0;
                case W.WM_MBUTTONUP:
                    UpdateMouse(l);
                    UI.MiddleDown = false;
                    if (!UI.Down) W.ReleaseCapture();
                    if (UI.AutoScrollActive && UI.AutoScrollHold)
                    {
                        UI.StopAutoScroll();
                    }
                    else if (!UI.AutoScrollActive)
                    {
                        UI.MiddleClicked = true;
                    }
                    UI.LastInput = Environment.TickCount64;
                    Invalidate();
                    return 0;
                case W.WM_CAPTURECHANGED:
                case W.WM_CANCELMODE:
                    if (UI.Down || UI.MiddleDown || UI.AutoScrollActive)
                    {
                        UI.Down = UI.MiddleDown = false;
                        UI.Released = true;
                        UI.StopAutoScroll();
                        Invalidate();
                    }
                    return 0;
                case W.WM_XBUTTONUP:
                {
                    var xb = (int)w >> 16;
                    if (xb == 1) Shell.Back();
                    else if (xb == 2) Shell.Forward();
                    return 1;
                }
                case W.WM_COPYDATA:
                    if (LinkRouter.HandleCopyData(l)) return 1;
                    break;
                case W.WM_CLIPBOARDUPDATE:
                    LinkRouter.OnClipboardUpdated();
                    return 0;
                case W.WM_MOUSEWHEEL:
                {
                    var p = new W.POINT { X = W.LoWord(l), Y = W.HiWord(l) };
                    W.ScreenToClient(h, &p);
                    UI.MX = p.X / Gfx.Scale; UI.MY = p.Y / Gfx.Scale;
                    UI.LastInput = Environment.TickCount64;
                    var delta = (short)((w >> 16) & 0xFFFF) / 120f;
                    if (W.KeyDown(W.VK_CONTROL))
                    {
                        if (Shell.OverVideo(UI.MX, UI.MY)) Loupe.AdjustZoom(delta > 0 ? 0.4f : -0.4f);
                        else Shell.AdjustZoom(delta > 0 ? 0.05f : -0.05f);
                        return 0;
                    }
                    if (W.KeyDown(W.VK_SHIFT) && AppSettings.Current.ShiftWheelFrameStep && Shell.OverVideo(UI.MX, UI.MY))
                    {
                        Shell.StepVideoFrame(delta);
                        return 0;
                    }
                    if (Shell.OverMusicStage(UI.MX, UI.MY) && AppSettings.Current.WheelVolumeOnVideo && !Shell.AnyModalOpen)
                    {
                        int curVol = MpvPlayer.Instance.Muted ? 0 : (int)Math.Round(MpvPlayer.Instance.Volume);
                        Shell.SetMusicVolume(curVol + (delta > 0 ? 5 : -5));
                        Invalidate();
                    }
                    else if (Shell.OverVideo(UI.MX, UI.MY) && AppSettings.Current.WheelVolumeOnVideo && !Shell.AnyModalOpen)
                    {
                        if (!Pip.Active && Shell.Current is FluxTube.Pages.ShortsPage spVol) spVol.AdjustVolume(delta > 0 ? 5 : -5);
                        else Shell.AdjustMainVolume(delta > 0 ? 5 : -5);
                    }
                    else if (!Pip.Active && Shell.Current is FluxTube.Pages.ShortsPage sp && Shell.OverVideo(UI.MX, UI.MY))
                        sp.Step(delta > 0 ? -1 : 1);
                    else { UI.Wheel += delta; Invalidate(); }
                    return 0;
                }
                case W.WM_KEYDOWN:
                case W.WM_SYSKEYDOWN:
                    UI.LastInput = Environment.TickCount64;
                    if ((int)w == W.VK_CONTROL) Loupe.Poll();
                    if ((int)w == W.VK_SPACE && !W.KeyDown(W.VK_CONTROL) && !W.KeyDown(W.VK_MENU))
                    {
                        bool repeat = (l.ToInt64() & (1L << 30)) != 0;
                        if (Shell.SpaceDown(repeat)) return 0;
                    }
                    if (Shell.Key((int)w, W.KeyDown(W.VK_CONTROL), W.KeyDown(W.VK_MENU))) return 0;
                    break;
                case W.WM_KEYUP:
                case W.WM_SYSKEYUP:
                    if ((int)w == W.VK_CONTROL) Loupe.Poll();
                    if ((int)w == W.VK_SPACE && Shell.SpaceUp()) return 0;
                    break;
                case W.WM_COMMAND:
                    if (((int)w >> 16) == 0x0300) Edit.OnCommand(l);
                    return 0;
                case W.WM_CTLCOLOREDIT:
                {
                    var b = Edit.CtlColor((nint)w, l);
                    if (b != 0) return b;
                    break;
                }
                case W.WM_SETCURSOR:
                    if (!Test && !W.IsWindowEnabled(h)) W.EnableWindow(h, 1);
                    if (W.LoWord(l) == 1 && (nint)w == h)
                    {
                        W.SetCursor(UI.WantHand ? _hand : Sel.WantIBeam ? _ibeam : _arrow);
                        return 1;
                    }
                    break;
                case WM_POST:
                    while (_posts.TryDequeue(out var a))
                    {
                        try { a(); }
                        catch (Exception e) { Paths.Log("post: " + e); }
                    }
                    Invalidate();
                    return 0;
                case WM_WAKE:
                    _wakePending = false;
                    Invalidate();
                    return 0;
                case W.WM_TIMER:
                    if (w == 1) Shell.Tick();
                    else if (w == 2)
                    {
                        var now = Environment.TickCount64;
                        bool fired = false;
                        for (int i = _timers.Count - 1; i >= 0; i--)
                        {
                            if (_timers[i].Due > now) continue;
                            var act = _timers[i].Act;
                            _timers.RemoveAt(i);
                            fired = true;
                            try { act(); } catch (Exception e) { Paths.Log("timer: " + e); }
                        }
                        RescheduleAfterTimer();
                        if (fired) Invalidate();
                    }
                    else if (w == 3)
                    {
                        if (!Gfx.TestOccluded())
                        {
                            _occludedTimer = false;
                            W.KillTimer(h, 3);
                            Invalidate();
                        }
                    }
                    return 0;
                case W.WM_ACTIVATE:
                    if (((int)w & 0xFFFF) == 0) { Suggest.Hide(); Shell.CancelSpace2x(); Loupe.Hide(); PreviewPlayer.HideNow(); }
                    else if (!Test)
                    {
                        if (!W.IsWindowEnabled(h)) W.EnableWindow(h, 1);
                        UI.Down = UI.Pressed = UI.Released = UI.RightClicked = UI.MiddleDown = UI.MiddleClicked = false;
                        UI.BlockInput = false;
                        UI.StopAutoScroll();
                        Invalidate();
                    }
                    break;
                case W.WM_MOVE:
                    Suggest.Hide();
                    Edit.RepaintAll();
                    break;
                case W.WM_EXITSIZEMOVE:
                    Edit.RepaintAll();
                    Invalidate();
                    break;
                case W.WM_CLOSE:
                    Shell.Shutdown();
                    break;
                case W.WM_DESTROY:
                    W.PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception e)
        {
            Paths.Log("wndproc: " + e);
        }
        return W.DefWindowProcW(h, m, w, l);
    }
}

/// <summary>
/// The child window mpv renders into. mpv's own window inside it does not take input,
/// so mouse events land here and are forwarded as mpv commands; keys stay with the main window.
/// </summary>
internal static unsafe class VideoHost
{
    private static bool _tracking;
    private static long _lastMove = Environment.TickCount64, _lastMpvMouseAt;
    private static bool _cursorHidden, _mousePending;
    private static int _lastX = -1, _lastY = -1;

    private static void Cmd(params string[] a) => MpvPlayer.Instance.Command(a);

    private static void FlushPendingMouse()
    {
        if (!_mousePending || _lastX < 0) return;
        _mousePending = false;
        _lastMpvMouseAt = Environment.TickCount64;
        Cmd("mouse", _lastX.ToString(), _lastY.ToString());
    }

    // in the separate window a press on the picture may become a drag of the whole window
    private static int _pipDownX = -1, _pipDownY;
    private static bool _lmbSent;

    private static void StopTimers(nint h)
    {
        if (_tracking)
        {
            _tracking = false;
            W.KillTimer(h, 1);
            W.KillTimer(h, 2);
        }
        _lastX = _lastY = -1;
        _cursorHidden = false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static nint Proc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case W.WM_SHOWWINDOW:
                if (w == 0) { StopTimers(h); Loupe.Hide(); }
                return 0;
            case W.WM_MOUSEMOVE:
            {
                int x = W.LoWord(l), y = W.HiWord(l);
                if (x == _lastX && y == _lastY) return 0;
                _lastX = x; _lastY = y;
                var now = Environment.TickCount64;
                _lastMove = now;
                if (W.AsyncKeyDown(W.VK_CONTROL)) Loupe.Poll();
                if (_cursorHidden) { _cursorHidden = false; W.SetCursor(W.LoadCursorW(0, 32512)); }
                if (_pipDownX >= 0 && Math.Abs(x - _pipDownX) + Math.Abs(y - _pipDownY) > 6)
                {
                    _pipDownX = -1;
                    W.ReleaseCapture();
                    W.SendMessageW(Pip.Hwnd, 0x00A1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
                    return 0;
                }
                // Coalesce 1000Hz-8000Hz gaming mouse packets to ~100Hz so libmpv's IPC ring & Lua OSC never choke!
                if (now - _lastMpvMouseAt >= 10)
                {
                    _mousePending = false;
                    _lastMpvMouseAt = now;
                    Cmd("mouse", x.ToString(), y.ToString());
                }
                else _mousePending = true;
                if (!_tracking)
                {
                    var tme = new W.TRACKMOUSEEVENT { cbSize = (uint)sizeof(W.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = h };
                    W.TrackMouseEvent(&tme);
                    _tracking = true;
                    W.SetTimer(h, 1, 400, 0);
                    W.SetTimer(h, 2, 50, 0); // instant Ctrl+hover detection & trailing mouse flush
                }
                return 0;
            }
            case W.WM_MOUSELEAVE:
            {
                _mousePending = false;
                StopTimers(h);
                Loupe.Poll();
                // (scripted checks post mouse messages without a real pointer, which would read as an instant leave)
                if (!App.Test) Cmd("script-message-to", "fluxosc", "flux-leave"); // the controls hide like on YouTube when the pointer leaves
                return 0;
            }
            case W.WM_TIMER:
                if (!W.IsWindowVisible(h)) { StopTimers(h); return 0; }
                if (w == 2)
                {
                    FlushPendingMouse();
                    Loupe.Poll();
                    return 0;
                }
                if (!_cursorHidden && Environment.TickCount64 - _lastMove > 1600 && !MpvPlayer.Instance.Paused)
                {
                    W.POINT cp; W.RECT vr;
                    if (W.GetCursorPos(&cp) && W.GetWindowRect(h, &vr) && cp.X >= vr.L && cp.X < vr.R && cp.Y >= vr.T && cp.Y < vr.B)
                    {
                        _cursorHidden = true;
                        W.SetCursor(0);
                    }
                    else StopTimers(h);
                }
                return 0;
            case W.WM_SETCURSOR:
                W.SetCursor(_cursorHidden ? 0 : W.LoadCursorW(0, 32512));
                return 1;
            case W.WM_LBUTTONDOWN:
            case W.WM_LBUTTONDBLCLK:
                W.SetCapture(h);
                if (Pip.Active)
                {
                    // the picture itself (not the controls at the bottom or the buttons at the top): wait and see if it is a drag
                    W.RECT rc;
                    W.GetClientRect(h, &rc);
                    int px = W.LoWord(l), py = W.HiWord(l);
                    var s = W.GetDpiForWindow(h) / 96f;
                    if (py > 52 * s && py < rc.B - 72 * s) { _pipDownX = px; _pipDownY = py; return 0; }
                    _lmbSent = true;
                    Cmd("keydown", "MBTN_LEFT");
                    return 0;
                }
                if (W.GetFocus() != App.Hwnd) W.SetFocus(App.Hwnd);
                Suggest.Hide();
                if (Shell.CloseModalOnOutsideClick()) return 0;
                _lmbSent = true;
                Cmd("keydown", "MBTN_LEFT");
                return 0;
            case W.WM_LBUTTONUP:
                W.ReleaseCapture();
                if (_pipDownX >= 0) { _pipDownX = -1; _lmbSent = true; Cmd("keydown", "MBTN_LEFT"); }
                if (_lmbSent) { _lmbSent = false; Cmd("keyup", "MBTN_LEFT"); }
                return 0;
            case W.WM_CAPTURECHANGED:
            case W.WM_CANCELMODE:
                _pipDownX = -1;
                if (_lmbSent) { _lmbSent = false; Cmd("keyup", "MBTN_LEFT"); }
                return 0;
            case W.WM_RBUTTONDOWN:
                if (Shell.CloseModalOnOutsideClick()) return 0;
                Cmd("keydown", "MBTN_RIGHT");
                return 0;
            case W.WM_RBUTTONUP:
                if (Shell.AnyModalOpen) return 0;
                Cmd("keyup", "MBTN_RIGHT");
                return 0;
            case W.WM_MBUTTONUP:
                if (Shell.AnyModalOpen) return 0;
                Cmd("keypress", "MBTN_MID");
                return 0;
            case W.WM_XBUTTONUP:
            {
                var xb = (int)w >> 16;
                if (xb == 1) Shell.Back();
                else if (xb == 2) Shell.Forward();
                return 1;
            }
            case W.WM_MOUSEWHEEL:
            {
                if (Shell.AnyModalOpen) return 0;
                var delta = (short)((w >> 16) & 0xFFFF) / 120f;
                if (W.AsyncKeyDown(W.VK_CONTROL))
                {
                    Loupe.AdjustZoom(delta > 0 ? 0.4f : -0.4f);
                    return 0;
                }
                // Shift + wheel over ANY video (Watch, Fullscreen, PiP, Shorts) -> precision frame-by-frame scrub!
                if (W.AsyncKeyDown(W.VK_SHIFT) && AppSettings.Current.ShiftWheelFrameStep)
                {
                    Shell.StepVideoFrame(delta);
                    return 0;
                }
                // Respect WheelVolumeOnVideo consistently across BOTH horizontal videos AND vertical Shorts!
                // Scrolling outside the vertical Shorts video (left/right stage margins) still flips Shorts.
                if (AppSettings.Current.WheelVolumeOnVideo)
                {
                    if (!Pip.Active && Shell.Current is FluxTube.Pages.ShortsPage spVol) spVol.AdjustVolume(delta > 0 ? 5 : -5);
                    else Shell.AdjustMainVolume(delta > 0 ? 5 : -5);
                    return 0;
                }
                if (!Pip.Active && Shell.Current is FluxTube.Pages.ShortsPage sp)
                {
                    sp.Step(delta > 0 ? -1 : 1);
                    return 0;
                }
                if (!Pip.Active && !Shell.IsFullscreen)
                {
                    UI.Wheel += delta;
                    App.Invalidate();
                }
                return 0;
            }
            case W.WM_ERASEBKGND:
                return 1;
        }
        return W.DefWindowProcW(h, m, w, l);
    }
}
