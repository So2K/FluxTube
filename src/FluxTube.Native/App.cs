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
    private static bool _inFrame, _tracking, _wakePending;
    private static long _lastFrameTick;
    private static nint _arrow, _hand, _ibeam;
    private const uint WM_POST = W.WM_APP + 1, WM_WAKE = W.WM_APP + 2;

    // frame time statistics (CPU time to build and submit a frame, without waiting for vsync)
    private static readonly List<double> _frameMs = new(4096);
    public static double LastFrameMs { get; private set; }

    /// <summary>Runs a on the UI thread after the current message; safe from any thread.</summary>
    public static void Post(Action a)
    {
        _posts.Enqueue(a);
        W.PostMessageW(Hwnd, WM_POST, 0, 0);
    }

    /// <summary>Cheap "something changed" from worker threads; many calls collapse into one frame.</summary>
    public static void WakeUi()
    {
        if (Interlocked.Exchange(ref _wakePending, true)) return;
        W.PostMessageW(Hwnd, WM_WAKE, 0, 0);
    }

    public static void Invalidate() => W.InvalidateRect(Hwnd, null, 0);

    public static void After(int ms, Action a)
    {
        _timers.Add((Environment.TickCount64 + ms, a));
        W.SetTimer(Hwnd, 2, 30, 0);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Paths.MigrateOldData();
        ProfileOptimization.SetProfileRoot(Paths.Data);
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

        Gfx.Init(Hwnd);
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

        W.MSG msg;
        while (W.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            W.TranslateMessage(&msg);
            W.DispatchMessageW(&msg);
        }
        return 0;
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
            UI.Dt = Math.Clamp((now - _lastFrameTick) / 1000.0, 0.001, 0.05);
            UI.Now = now;
            _lastFrameTick = now;

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
    public static void ResetBench() { _frameMs.Clear(); _sumDraw = _sumFlush = _sumUp = 0; Gfx.TextDrawMs = 0; Gfx.TextDraws = 0; }

    private static void UpdateMouse(nint l)
    {
        UI.MX = W.LoWord(l) / Gfx.Scale;
        UI.MY = W.HiWord(l) / Gfx.Scale;
        UI.LastInput = Environment.TickCount64;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint h, uint m, nuint w, nint l)
    {
        try
        {
            switch (m)
            {
                case W.WM_PAINT:
                    Frame();
                    W.ValidateRect(h, null);
                    if (UI.TakeAnimate()) W.InvalidateRect(h, null, 0);
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
                    if (Gfx.DC != null && (int)w != 1)
                    {
                        Gfx.Resize();
                        Invalidate();
                    }
                    return 0;
                case W.WM_DPICHANGED:
                {
                    var rc = (W.RECT*)l;
                    W.SetWindowPos(h, 0, rc->L, rc->T, rc->R - rc->L, rc->B - rc->T, W.SWP_NOZORDER | W.SWP_NOACTIVATE);
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
                    UpdateMouse(l);
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
                    if (!UI.Down) { UI.MX = UI.MY = -1; }
                    Invalidate();
                    return 0;
                case W.WM_LBUTTONDOWN:
                case W.WM_LBUTTONDBLCLK:
                    UpdateMouse(l);
                    if (UI.AutoScrollActive) { UI.StopAutoScroll(); Invalidate(); return 0; }
                    W.SetCapture(h);
                    if (W.GetFocus() != h) W.SetFocus(h);
                    Suggest.Hide();
                    UI.Down = UI.Pressed = true;
                    Invalidate();
                    return 0;
                case W.WM_LBUTTONUP:
                    UpdateMouse(l);
                    W.ReleaseCapture();
                    UI.Down = false;
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
                    Invalidate();
                    return 0;
                case W.WM_MBUTTONUP:
                    UpdateMouse(l);
                    W.ReleaseCapture();
                    UI.MiddleDown = false;
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
                case W.WM_XBUTTONUP:
                    if (((int)w >> 16) == 1) Shell.Back();
                    return 1;
                case W.WM_MOUSEWHEEL:
                {
                    var p = new W.POINT { X = W.LoWord(l), Y = W.HiWord(l) };
                    W.ScreenToClient(h, &p);
                    UI.MX = p.X / Gfx.Scale; UI.MY = p.Y / Gfx.Scale;
                    UI.LastInput = Environment.TickCount64;
                    var delta = (short)((w >> 16) & 0xFFFF) / 120f;
                    if (W.KeyDown(W.VK_CONTROL))
                    {
                        Shell.AdjustZoom(delta > 0 ? 0.05f : -0.05f);
                        return 0;
                    }
                    if (Shell.OverVideo(UI.MX, UI.MY)) MpvPlayer.Instance.Command("keypress", delta > 0 ? "WHEEL_UP" : "WHEEL_DOWN");
                    else { UI.Wheel += delta; Invalidate(); }
                    return 0;
                }
                case W.WM_KEYDOWN:
                case W.WM_SYSKEYDOWN:
                    UI.LastInput = Environment.TickCount64;
                    if (Shell.Key((int)w, W.KeyDown(W.VK_CONTROL), W.KeyDown(W.VK_MENU))) return 0;
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
                        for (int i = _timers.Count - 1; i >= 0; i--)
                        {
                            if (_timers[i].Due > now) continue;
                            var act = _timers[i].Act;
                            _timers.RemoveAt(i);
                            try { act(); } catch (Exception e) { Paths.Log("timer: " + e); }
                        }
                        if (_timers.Count == 0) W.KillTimer(h, 2);
                        Invalidate();
                    }
                    return 0;
                case W.WM_ACTIVATE:
                    if (((int)w & 0xFFFF) == 0) Suggest.Hide();
                    break;
                case W.WM_MOVE:
                    Suggest.Hide();
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
    private static long _lastMove = Environment.TickCount64;
    private static bool _cursorHidden;
    private static int _lastX = -1, _lastY = -1;

    private static void Cmd(params string[] a) => MpvPlayer.Instance.Command(a);

    // in the separate window a press on the picture may become a drag of the whole window
    private static int _pipDownX = -1, _pipDownY;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static nint Proc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case W.WM_MOUSEMOVE:
            {
                int x = W.LoWord(l), y = W.HiWord(l);
                if (x == _lastX && y == _lastY) return 0;
                _lastX = x; _lastY = y;
                _lastMove = Environment.TickCount64;
                if (_cursorHidden) { _cursorHidden = false; W.SetCursor(W.LoadCursorW(0, 32512)); }
                if (_pipDownX >= 0 && Math.Abs(x - _pipDownX) + Math.Abs(y - _pipDownY) > 6)
                {
                    _pipDownX = -1;
                    W.ReleaseCapture();
                    W.SendMessageW(Pip.Hwnd, 0x00A1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
                    return 0;
                }
                Cmd("mouse", x.ToString(), y.ToString());
                if (!_tracking)
                {
                    var tme = new W.TRACKMOUSEEVENT { cbSize = (uint)sizeof(W.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = h };
                    W.TrackMouseEvent(&tme);
                    _tracking = true;
                    W.SetTimer(h, 1, 400, 0);
                }
                return 0;
            }
            case W.WM_MOUSELEAVE:
            {
                _tracking = false;
                _lastX = _lastY = -1;
                W.KillTimer(h, 1);
                // (scripted checks post mouse messages without a real pointer, which would read as an instant leave)
                if (!App.Test) Cmd("script-message-to", "fluxosc", "flux-leave"); // the controls hide like on YouTube when the pointer leaves
                return 0;
            }
            case W.WM_TIMER:
                if (!_cursorHidden && Environment.TickCount64 - _lastMove > 1600 && !MpvPlayer.Instance.Paused)
                {
                    _cursorHidden = true;
                    W.SetCursor(0);
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
                    Cmd("keydown", "MBTN_LEFT");
                    return 0;
                }
                if (W.GetFocus() != App.Hwnd) W.SetFocus(App.Hwnd);
                Suggest.Hide();
                if (Menus.Open) { Menus.Close(); return 0; }
                if (AccountMenu.Open) { AccountMenu.Close(); return 0; } // a click outside the menu only closes it
                Cmd("keydown", "MBTN_LEFT");
                return 0;
            case W.WM_LBUTTONUP:
                W.ReleaseCapture();
                if (_pipDownX >= 0) { _pipDownX = -1; Cmd("keydown", "MBTN_LEFT"); }
                Cmd("keyup", "MBTN_LEFT");
                return 0;
            case W.WM_RBUTTONDOWN:
                if (Menus.Open) { Menus.Close(); return 0; }
                Cmd("keydown", "MBTN_RIGHT");
                return 0;
            case W.WM_RBUTTONUP:
                Cmd("keyup", "MBTN_RIGHT");
                return 0;
            case W.WM_MBUTTONUP:
                Cmd("keypress", "MBTN_MID");
                return 0;
            case W.WM_XBUTTONUP:
                if (((int)w >> 16) == 1) Shell.Back();
                return 1;
            case W.WM_MOUSEWHEEL:
            {
                var delta = (short)((w >> 16) & 0xFFFF) / 120f;
                // in Shorts: wheel over the bottom OSC bar (or with Shift) adjusts global volume; elsewhere steps to next/prev Short
                if (!Pip.Active && Shell.Current is FluxTube.Pages.ShortsPage sp)
                {
                    W.RECT rc;
                    W.GetClientRect(h, &rc);
                    var pt = new W.POINT { X = W.LoWord(l), Y = W.HiWord(l) };
                    W.ScreenToClient(h, &pt);
                    var s = W.GetDpiForWindow(h) / 96f;
                    if (W.KeyDown(0x10 /*VK_SHIFT*/) || pt.Y > rc.B - (int)(68 * s))
                    {
                        sp.AdjustVolume(delta > 0 ? 5 : -5);
                        return 0;
                    }
                    sp.Step(delta > 0 ? -1 : 1);
                    return 0;
                }
                // in whole-page scroll mode, wheel over the video scrolls the watch page so you can read comments
                if (!Pip.Active && !Shell.IsFullscreen && AppSettings.Current.ScrollWholeWatchPage && Shell.Current is FluxTube.Pages.WatchPage wp)
                {
                    UI.ScrollBy(wp.MainScroll, -delta * 110);
                    App.Invalidate();
                    return 0;
                }
                Cmd("keypress", delta > 0 ? "WHEEL_UP" : "WHEEL_DOWN"); // volume
                return 0;
            }
            case W.WM_ERASEBKGND:
                return 1;
        }
        return W.DefWindowProcW(h, m, w, l);
    }
}
