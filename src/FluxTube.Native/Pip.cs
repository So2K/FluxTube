using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluxTube.Player;

namespace FluxTube;

/// <summary>
/// The video in its own small window above all others (picture-in-picture). The player surface itself moves into
/// that window, so nothing is decoded twice and the controls come along.
///
/// Locked: the window snaps into the nearest screen corner and stops taking the mouse. While the pointer is over
/// it, it hides, so whatever is under it can be clicked and worked with; it comes back when the pointer leaves.
/// Only the small bar next to it (unlock, pause, back to the app) takes clicks.
/// </summary>
public static unsafe class Pip
{
    public static bool Active { get; private set; }
    public static bool Locked { get; private set; }
    public static string? PinnedVideoId { get; private set; }
    public static string? PinnedTabKey { get; private set; }
    public static nint Hwnd => _win;

    public static void UpdatePinned(string? videoId, string? tabKey)
    {
        PinnedVideoId = videoId;
        PinnedTabKey = tabKey;
    }

    private static nint _win, _bar, _barFont, _barBgBr, _barHotBr;
    private static bool _registered, _lastPaused, _fastTimer;
    private static int _barDown = -1;
    // locked: how visible the window is (0..255). It dissolves while the pointer is over it and comes back after.
    private static float _alpha = 255;
    private static int _alphaSet = 255;
    private static long _lastTick;
    public static int TestAlpha = -1; // scripted checks: hold this opacity instead of following the pointer
    private static int _barHot = -1;
    private const int Border = 4;

    private static float Scale => _win != 0 ? W.GetDpiForWindow(_win) / 96f : Gfx.Scale;

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        var inst = W.GetModuleHandleW(0);
        fixed (char* a = "FluxPip")
        fixed (char* b = "FluxPipBar")
        {
            var wc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW), lpfnWndProc = &Proc, hInstance = inst, hCursor = W.LoadCursorW(0, 32512), lpszClassName = a,
                hbrBackground = W.CreateSolidBrush(0x00101010),
                hIcon = W.LoadIconW(inst, 32512),
            };
            W.RegisterClassExW(&wc);
            var bc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW), lpfnWndProc = &BarProc, hInstance = inst, hCursor = W.LoadCursorW(0, 32649), lpszClassName = b,
            };
            W.RegisterClassExW(&bc);
        }
    }

    public static void Toggle() { if (Active) Exit(); else Enter(); }

    public static void Enter()
    {
        if (Active || !MpvPlayer.Instance.IsReady) return;
        if (Shell.IsFullscreen) MpvPlayer.Instance.SetFullscreenFlag(false);
        AccountMenu.Close();
        Shell.VideoHostReturned(); // the page no longer has the surface: no hit area, no cut-out left on it
        Register();
        // bottom-right of the screen the app is on, a quarter of its width
        var mi = new W.MONITORINFO { cbSize = (uint)sizeof(W.MONITORINFO) };
        W.GetMonitorInfoW(W.MonitorFromWindow(App.Hwnd, 2), &mi);
        var work = mi.rcWork;
        var s = W.GetDpiForWindow(App.Hwnd) / 96f;
        int w = (int)Math.Clamp((work.R - work.L) * 0.24f, 360 * s, 720 * s), h = w * 9 / 16 + Border * 2;
        int m = (int)(16 * s);
        _win = W.CreateWindowExW(W.WS_EX_TOPMOST | W.WS_EX_TOOLWINDOW, "FluxPip", "FluxTube", W.WS_POPUP | W.WS_CLIPCHILDREN,
            work.R - w - m, work.B - h - m, w, h, 0, 0, W.GetModuleHandleW(0), 0);
        int round = 2; // DWMWCP_ROUND
        W.DwmSetWindowAttribute(_win, 33, &round, 4);
        Active = true;
        PinnedVideoId = PlaybackTracker.Instance.Current?.VideoId;
        PinnedTabKey = Shell.MainTabKey;
        W.SetWindowRgn(App.VideoHwnd, 0, false);
        W.SetParent(App.VideoHwnd, _win);
        Layout();
        W.ShowWindow(_win, 4 /*SW_SHOWNOACTIVATE*/);
        W.ShowWindow(App.VideoHwnd, W.SW_SHOW);
        Shell.PushPlayerState();
        App.Invalidate();
    }

    public static void Exit()
    {
        if (!Active) return;
        Loupe.Hide();
        SetLocked(false);
        Active = false;
        PinnedVideoId = null;
        PinnedTabKey = null;
        W.ShowWindow(App.VideoHwnd, W.SW_HIDE);
        W.SetParent(App.VideoHwnd, App.Hwnd);
        if (_win != 0)
        {
            W.KillTimer(_win, 1);
            var old = _win;
            _win = 0;
            W.DestroyWindow(old);
        }
        Shell.VideoHostReturned();
        Shell.PushPlayerState();
        App.Invalidate();
    }

    /// <summary>Back into the app, jumping cleanly to the pinned video's own tab without overwriting other tabs.</summary>
    public static void BackToApp()
    {
        var tabKey = PinnedTabKey;
        var vid = PinnedVideoId;
        Exit();
        if (W.IsIconic(App.Hwnd)) W.ShowWindow(App.Hwnd, 9 /*SW_RESTORE*/);
        W.SetForegroundWindow(App.Hwnd);
        Shell.FocusVideoTab(tabKey, vid);
    }

    private static void Layout()
    {
        W.RECT rc;
        W.GetClientRect(_win, &rc);
        W.SetWindowPos(App.VideoHwnd, 0, Border, Border, Math.Max(1, rc.R - Border * 2), Math.Max(1, rc.B - Border * 2), W.SWP_NOZORDER | W.SWP_NOACTIVATE);
    }

    // ---------------- lock ----------------

    public static void SetLocked(bool on)
    {
        if (on == Locked || !Active) return;
        Locked = on;
        var ex = (uint)W.GetWindowLongPtrW(_win, W.GWL_EXSTYLE);
        if (on)
        {
            SnapToCorner();
            CreateBar();
            // locked: the mouse goes through the window to whatever is under it, and the window can fade
            W.SetWindowLongPtrW(_win, W.GWL_EXSTYLE, (nint)(ex | W.WS_EX_LAYERED | W.WS_EX_TRANSPARENT));
            _alpha = _alphaSet = 255;
            W.SetLayeredWindowAttributes(_win, 0, 255, 2 /*LWA_ALPHA*/);
            _lastTick = Environment.TickCount64;
            _fastTimer = false;
            W.SetTimer(_win, 1, 60, 0);
        }
        else
        {
            if (_win != 0) W.KillTimer(_win, 1);
            _fastTimer = false;
            _barHot = _barDown = -1;
            if (_bar != 0) { W.DestroyWindow(_bar); _bar = 0; }
            if (_barFont != 0) { W.DeleteObject(_barFont); _barFont = 0; }
            if (_win != 0)
            {
                W.SetLayeredWindowAttributes(_win, 0, 255, 2);
                W.SetWindowLongPtrW(_win, W.GWL_EXSTYLE, (nint)(ex & ~(W.WS_EX_LAYERED | W.WS_EX_TRANSPARENT)));
            }
            _alpha = _alphaSet = 255;
        }
    }

    private static void SnapToCorner()
    {
        W.RECT r;
        W.GetWindowRect(_win, &r);
        var mi = new W.MONITORINFO { cbSize = (uint)sizeof(W.MONITORINFO) };
        W.GetMonitorInfoW(W.MonitorFromWindow(_win, 2), &mi);
        var wk = mi.rcWork;
        int w = r.R - r.L, h = r.B - r.T, m = (int)(12 * Scale);
        bool right = (r.L + r.R) / 2 > (wk.L + wk.R) / 2, bottom = (r.T + r.B) / 2 > (wk.T + wk.B) / 2;
        // leave room for the bar between the window and the screen edge it faces
        int barH = (int)(44 * Scale);
        int x = right ? wk.R - w - m : wk.L + m;
        int y = bottom ? wk.B - h - m : wk.T + m + barH;
        W.SetWindowPos(_win, 0, x, y, 0, 0, W.SWP_NOSIZE | W.SWP_NOZORDER | W.SWP_NOACTIVATE);
    }

    private const int Buttons = 3;
    private static float BtnW => 40 * Scale;

    private static void CreateBar()
    {
        W.RECT r;
        W.GetWindowRect(_win, &r);
        int bw = (int)(BtnW * Buttons + 8 * Scale), bh = (int)(40 * Scale), gap = (int)(4 * Scale);
        var mi = new W.MONITORINFO { cbSize = (uint)sizeof(W.MONITORINFO) };
        W.GetMonitorInfoW(W.MonitorFromWindow(_win, 2), &mi);
        bool right = (r.L + r.R) / 2 > (mi.rcWork.L + mi.rcWork.R) / 2, bottom = (r.T + r.B) / 2 > (mi.rcWork.T + mi.rcWork.B) / 2;
        int x = right ? r.R - bw : r.L;
        int y = bottom ? r.T - bh - gap : r.B + gap;
        _bar = W.CreateWindowExW(W.WS_EX_TOPMOST | W.WS_EX_TOOLWINDOW | W.WS_EX_NOACTIVATE, "FluxPipBar", null, W.WS_POPUP, x, y, bw, bh, 0, 0, W.GetModuleHandleW(0), 0);
        W.SetWindowRgn(_bar, W.CreateRoundRectRgn(0, 0, bw + 1, bh + 1, bh, bh), true);
        if (_barFont != 0) W.DeleteObject(_barFont);
        _barFont = W.CreateFontW(-(int)(16 * Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, Gfx.IconFont);
        W.ShowWindow(_bar, 4);
    }

    /// <summary>Every 40 ms while open: hide under the pointer when locked, keep the bar's pause icon current.</summary>
    private static void Tick()
    {
        if (!Locked) return;
        W.POINT p;
        W.GetCursorPos(&p);
        W.RECT r, b = default;
        W.GetWindowRect(_win, &r);
        if (_bar != 0) W.GetWindowRect(_bar, &b);
        var overVideo = p.X >= r.L && p.X < r.R && p.Y >= r.T && p.Y < r.B;
        var overBar = _bar != 0 && p.X >= b.L && p.X < b.R && p.Y >= b.T && p.Y < b.B;
        // under the pointer the picture dissolves in about a fifth of a second and comes back the same way
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _lastTick) / 1000f, 0.001f, 0.1f);
        _lastTick = now;
        float target = TestAlpha >= 0 ? TestAlpha : overVideo && !overBar ? 0 : 255;
        var diff = target - _alpha;
        _alpha = MathF.Abs(diff) < 2 ? target : _alpha + diff * (1 - MathF.Exp(-dt * 13));
        var a = (int)MathF.Round(_alpha);
        if (a != _alphaSet) { _alphaSet = a; W.SetLayeredWindowAttributes(_win, 0, (byte)a, 2); }
        // Fast 16ms timer only while opacity is actively fading; relax to 60ms once settled!
        bool needFast = MathF.Abs(target - _alpha) >= 1f;
        if (needFast != _fastTimer)
        {
            _fastTimer = needFast;
            W.SetTimer(_win, 1, needFast ? 16u : 60u, 0);
        }
        var paused = MpvPlayer.Instance.Paused;
        if (paused != _lastPaused && _bar != 0) { _lastPaused = paused; W.InvalidateRect(_bar, null, 0); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case W.WM_CLOSE: Exit(); return 0;
            case W.WM_DESTROY:
                if (Active && h == _win)
                {
                    W.SetParent(App.VideoHwnd, App.Hwnd);
                    Active = false;
                    _win = 0;
                }
                return 0;
            case W.WM_MOUSEACTIVATE: return 3; // MA_NOACTIVATE: the app being worked in keeps the keyboard
            case W.WM_SIZE: if (Active && h == _win) Layout(); return 0;
            case W.WM_TIMER: Tick(); return 0;
            case W.WM_NCHITTEST:
            {
                // the thin frame around the picture resizes the window
                var hit = W.DefWindowProcW(h, m, w, l);
                if (hit != 1) return hit;
                W.RECT r;
                W.GetWindowRect(h, &r);
                int x = (short)(l & 0xFFFF), y = (short)((l >> 16) & 0xFFFF), e = (int)(8 * Scale);
                bool L = x < r.L + e, R = x >= r.R - e, T = y < r.T + e, B = y >= r.B - e;
                if (T && L) return 13; if (T && R) return 14; if (B && L) return 16; if (B && R) return 17;
                if (L) return 10; if (R) return 11; if (T) return 12; if (B) return 15;
                return 2; // HTCAPTION: the frame drags too
            }
            case W.WM_GETMINMAXINFO:
            {
                var mm = (int*)l; // MINMAXINFO: ptMinTrackSize at ints 6,7
                mm[6] = (int)(240 * Scale); mm[7] = (int)(135 * Scale);
                return 0;
            }
        }
        return W.DefWindowProcW(h, m, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint BarProc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case W.WM_MOUSEACTIVATE: return 3;
            case W.WM_ERASEBKGND: return 1;
            case W.WM_PAINT:
            {
                W.PAINTSTRUCT ps;
                var dc = W.BeginPaint(h, &ps);
                W.RECT rc;
                W.GetClientRect(h, &rc);
                if (_barBgBr == 0) _barBgBr = W.CreateSolidBrush(W.Rgb(0x1C, 0x1C, 0x20));
                if (_barHotBr == 0) _barHotBr = W.CreateSolidBrush(W.Rgb(0x3A, 0x3A, 0x42));
                W.FillRect(dc, &rc, _barBgBr);
                var oldFont = _barFont != 0 ? W.SelectObject(dc, _barFont) : 0;
                W.SetBkMode(dc, 1);
                int pad = (int)(4 * Scale), bw = (int)BtnW;
                string[] glyphs = ["", MpvPlayer.Instance.Paused ? "" : "", ""]; // locked, play/pause, back to the app
                for (int i = 0; i < Buttons; i++)
                {
                    var r = new W.RECT { L = pad + i * bw, T = 0, R = pad + (i + 1) * bw, B = rc.B };
                    if (i == _barHot)
                    {
                        var hr = new W.RECT { L = r.L + 2, T = r.T + 4, R = r.R - 2, B = r.B - 4 };
                        W.FillRect(dc, &hr, _barHotBr);
                    }
                    var c = Th.Brand;
                    W.SetTextColor(dc, i == 0 ? W.Rgb((byte)(c >> 16), (byte)(c >> 8), (byte)c) : W.Rgb(0xF2, 0xF2, 0xF4));
                    W.DrawTextW(dc, glyphs[i], 1, &r, 0x0001 | 0x0004 | 0x0020 /*CENTER|VCENTER|SINGLELINE*/);
                }
                if (oldFont != 0) W.SelectObject(dc, oldFont);
                W.EndPaint(h, &ps);
                return 0;
            }
            case W.WM_MOUSEMOVE:
            {
                var i = (int)MathF.Floor(((short)(l & 0xFFFF) - 4 * Scale) / BtnW);
                if (i < 0 || i >= Buttons) i = -1;
                if (i != _barHot) { _barHot = i; W.InvalidateRect(h, null, 0); }
                var tme = new W.TRACKMOUSEEVENT { cbSize = (uint)sizeof(W.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = h };
                W.TrackMouseEvent(&tme);
                return 0;
            }
            case 0x02A3: // WM_MOUSELEAVE
                _barHot = _barDown = -1; W.InvalidateRect(h, null, 0); return 0;
            case W.WM_LBUTTONDOWN:
            {
                var i = (int)MathF.Floor(((short)(l & 0xFFFF) - 4 * Scale) / BtnW);
                _barDown = i >= 0 && i < Buttons ? i : -1;
                return 0;
            }
            case W.WM_MOUSEWHEEL:
            {
                var delta = (short)((w >> 16) & 0xFFFF) / 120f;
                App.Post(() => Shell.AdjustMainVolume(delta > 0 ? 5 : -5));
                return 0;
            }
            case W.WM_LBUTTONUP:
            {
                var i = (int)MathF.Floor(((short)(l & 0xFFFF) - 4 * Scale) / BtnW);
                int hit = i >= 0 && i < Buttons && i == _barDown ? i : -1;
                _barDown = -1;
                if (hit < 0) return 0;
                App.Post(() =>
                {
                    switch (hit)
                    {
                        case 0: SetLocked(false); break;
                        case 1: MpvPlayer.Instance.TogglePause(); if (_bar != 0) W.InvalidateRect(_bar, null, 0); break;
                        case 2: BackToApp(); break;
                    }
                });
                return 0;
            }
        }
        return W.DefWindowProcW(h, m, w, l);
    }
}
