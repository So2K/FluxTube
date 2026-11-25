using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluxTube;

/// <summary>
/// GPU-composited square magnifying loupe (DWM Thumbnail) that smoothly animates in
/// when hovering over any video and holding Ctrl. Supports live D3D11/mpv hardware video
/// at 60 FPS with Ctrl+Wheel zoom adjustment (1.5x..6.0x).
/// </summary>
internal static unsafe partial class Loupe
{
    private const int BoxDip = 276;
    private const int FooterDip = 24;
    private const int BorderPx = 2;

    private static nint _hwnd;
    private static nint _thumb;
    private static nint _thumbSrc;
    private static bool _registered, _shown, _timerOn;

    private static float _anim;        // 0..1 smooth pop-in / fade-out
    private static float _zoomTarget = 2.5f;
    private static float _zoomCur = 2.5f;
    private static float _wx, _wy;     // smoothed window center
    private static bool _posInit;
    private static W.POINT _lastPt;
    private static nint _font;
    private static float _fontScale;
    private static long _lastTick;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public W.RECT rcDestination;
        public W.RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    private const uint DWM_TNP_RECTSOURCE = 0x00000002;
    private const uint DWM_TNP_OPACITY = 0x00000004;
    private const uint DWM_TNP_VISIBLE = 0x00000008;
    private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [LibraryImport("dwmapi.dll")] private static partial int DwmRegisterThumbnail(nint hwndDest, nint hwndSrc, nint* phThumb);
    [LibraryImport("dwmapi.dll")] private static partial int DwmUnregisterThumbnail(nint hThumb);
    [LibraryImport("dwmapi.dll")] private static partial int DwmUpdateThumbnailProperties(nint hThumb, DWM_THUMBNAIL_PROPERTIES* props);
    [LibraryImport("gdi32.dll")] private static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] private static partial nint GetStockObject(int i);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Rectangle(nint dc, int l, int t, int r, int b);

    public static bool Active => _shown && _anim > 0.05f;

    public static void AdjustZoom(float delta)
    {
        _zoomTarget = Math.Clamp(MathF.Round((_zoomTarget + delta) * 10f) / 10f, 1.5f, 6.0f);
        Poll();
        if (_hwnd != 0 && _shown) W.InvalidateRect(_hwnd, null, 0);
    }

    private static void EnsureWindow()
    {
        if (_hwnd != 0) return;
        if (!_registered)
        {
            _registered = true;
            fixed (char* cls = "FluxLoupe")
            {
                var wc = new W.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(W.WNDCLASSEXW),
                    lpfnWndProc = &Proc,
                    hInstance = W.GetModuleHandleW(0),
                    lpszClassName = cls,
                };
                W.RegisterClassExW(&wc);
            }
        }
        _hwnd = W.CreateWindowExW(
            W.WS_EX_TOPMOST | W.WS_EX_TOOLWINDOW | W.WS_EX_NOACTIVATE | W.WS_EX_TRANSPARENT | W.WS_EX_LAYERED,
            "FluxLoupe", null, W.WS_POPUP, 0, 0, BoxDip, BoxDip + FooterDip, App.Hwnd, 0, 0, 0);
        // Native Windows 11 rounded corners + shadow
        int corner = 2; // DWMWCP_ROUND
        W.DwmSetWindowAttribute(_hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &corner, 4);
        int dark = 1;
        W.DwmSetWindowAttribute(_hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, &dark, 4);
        W.SetLayeredWindowAttributes(_hwnd, 0, 0, 2 /*LWA_ALPHA*/);
    }

    private static void EnsureThumbnail(nint srcWin)
    {
        if (_thumb != 0 && _thumbSrc == srcWin) return;
        if (_thumb != 0) { DwmUnregisterThumbnail(_thumb); _thumb = 0; _thumbSrc = 0; }
        nint t = 0;
        if (DwmRegisterThumbnail(_hwnd, srcWin, &t) == 0 && t != 0)
        {
            _thumb = t;
            _thumbSrc = srcWin;
        }
    }

    /// <summary>Called on Ctrl press, mouse move over video, or lightweight video hover timer.</summary>
    public static void Poll()
    {
        bool want = WantsLoupe(out var pt, out var srcWin, out var vidScreenRc);
        if (!want && !_shown) return;
        EnsureWindow();
        if (want)
        {
            _lastPt = pt;
            EnsureThumbnail(srcWin);
            if (!_shown)
            {
                _shown = true;
                _posInit = false;
                _lastTick = Environment.TickCount64;
                W.ShowWindow(_hwnd, W.SW_SHOWNOACTIVATE);
            }
            if (!_timerOn)
            {
                _timerOn = true;
                W.SetTimer(_hwnd, 1, 16, 0);
            }
        }
        Step(want, want ? pt : _lastPt, srcWin, vidScreenRc);
    }

    public static void Hide()
    {
        if (!_shown) return;
        _shown = false;
        _anim = 0;
        _posInit = false;
        if (_timerOn && _hwnd != 0) { W.KillTimer(_hwnd, 1); _timerOn = false; }
        if (_thumb != 0) { DwmUnregisterThumbnail(_thumb); _thumb = 0; _thumbSrc = 0; }
        if (_hwnd != 0) W.ShowWindow(_hwnd, W.SW_HIDE);
    }

    private static bool WantsLoupe(out W.POINT pt, out nint srcWin, out W.RECT vidRc)
    {
        pt = default;
        vidRc = default;
        srcWin = Pip.Active && Pip.Hwnd != 0 ? Pip.Hwnd : App.Hwnd;
        if (srcWin == 0 || App.VideoHwnd == 0 || !W.IsWindowVisible(App.VideoHwnd)) return false;
        if (Menus.Open || AccountMenu.Open) return false;
        if (!W.AsyncKeyDown(W.VK_CONTROL)) return false;
        W.POINT p;
        if (!W.GetCursorPos(&p)) return false;
        W.RECT rc;
        if (!W.GetWindowRect(App.VideoHwnd, &rc)) return false;
        if (p.X < rc.L || p.X >= rc.R || p.Y < rc.T || p.Y >= rc.B) return false;
        pt = p;
        vidRc = rc;
        return true;
    }

    private static void Step(bool want, W.POINT pt, nint srcWin, W.RECT vidScreenRc)
    {
        var now = Environment.TickCount64;
        float dt = Math.Clamp((now - _lastTick) / 1000f, 0.005f, 0.05f);
        _lastTick = now;

        // Smooth pop-in / fade-out animation
        float target = want ? 1f : 0f;
        float speed = want ? 10f : 13f;
        _anim = want ? MathF.Min(1f, _anim + dt * speed) : MathF.Max(0f, _anim - dt * speed);
        if (!want && _anim <= 0.01f)
        {
            Hide();
            return;
        }

        _zoomCur += (_zoomTarget - _zoomCur) * MathF.Min(1f, dt * 18f);

        var dpi = W.GetDpiForWindow(srcWin) / 96f;
        if (dpi < 0.5f) dpi = 1f;

        float ease = 1f - MathF.Pow(1f - _anim, 3f);
        float scaleAnim = 0.78f + 0.22f * ease;

        int fullW = (int)MathF.Round(BoxDip * dpi);
        int footH = (int)MathF.Round(FooterDip * dpi);
        int fullH = fullW + footH;

        int curW = Math.Max(48, (int)MathF.Round(fullW * scaleAnim));
        int curH = Math.Max(56, (int)MathF.Round(fullH * scaleAnim));
        int curFoot = Math.Max(16, (int)MathF.Round(footH * scaleAnim));

        // Position loupe cleanly beside the cursor (right side by default, flips left near monitor edge)
        int srcHalf = (int)MathF.Round(fullW / (2f * _zoomCur));
        int offset = Math.Max((int)(24 * dpi), srcHalf + (int)(16 * dpi));

        var mon = W.MonitorFromWindow(srcWin, 2);
        var mi = new W.MONITORINFO { cbSize = (uint)sizeof(W.MONITORINFO) };
        W.GetMonitorInfoW(mon, &mi);

        float targetCx = pt.X + offset + fullW * 0.5f;
        if (targetCx + fullW * 0.5f > mi.rcMonitor.R - 8)
            targetCx = pt.X - offset - fullW * 0.5f;
        targetCx = Math.Clamp(targetCx, mi.rcMonitor.L + fullW * 0.5f + 8, mi.rcMonitor.R - fullW * 0.5f - 8);
        float targetCy = Math.Clamp(pt.Y, mi.rcMonitor.T + fullH * 0.5f + 8, Math.Max(mi.rcMonitor.T + fullH * 0.5f + 8, mi.rcMonitor.B - fullH * 0.5f - 8));

        if (!_posInit)
        {
            _wx = targetCx;
            _wy = targetCy;
            _posInit = true;
        }
        else
        {
            float follow = MathF.Min(1f, dt * 28f);
            _wx += (targetCx - _wx) * follow;
            _wy += (targetCy - _wy) * follow;
        }

        int winX = (int)MathF.Round(_wx - curW * 0.5f);
        int winY = (int)MathF.Round(_wy - curH * 0.5f);

        byte alpha = (byte)Math.Clamp((int)MathF.Round(ease * 255f), 1, 255);
        W.SetLayeredWindowAttributes(_hwnd, 0, alpha, 2 /*LWA_ALPHA*/);
        W.SetWindowPos(_hwnd, (nint)(-1) /*HWND_TOPMOST*/, winX, winY, curW, curH, W.SWP_NOACTIVATE);

        // Update DWM GPU thumbnail source/dest rects
        if (_thumb != 0 && want)
        {
            // Convert cursor and video rect into srcWin client coordinates
            var cPt = pt;
            W.ScreenToClient(srcWin, &cPt);

            var vTL = new W.POINT { X = vidScreenRc.L, Y = vidScreenRc.T };
            var vBR = new W.POINT { X = vidScreenRc.R, Y = vidScreenRc.B };
            W.ScreenToClient(srcWin, &vTL);
            W.ScreenToClient(srcWin, &vBR);

            int vw = Math.Max(16, vBR.X - vTL.X);
            int vh = Math.Max(16, vBR.Y - vTL.Y);
            int viewW = Math.Max(16, curW - BorderPx * 2);
            int viewH = Math.Max(16, curH - curFoot - BorderPx * 2);

            int sw = Math.Clamp((int)MathF.Round(viewW / _zoomCur), 16, vw);
            int sh = Math.Clamp((int)MathF.Round(viewH / _zoomCur), 16, vh);

            int sx = Math.Clamp(cPt.X - sw / 2, vTL.X, Math.Max(vTL.X, vBR.X - sw));
            int sy = Math.Clamp(cPt.Y - sh / 2, vTL.Y, Math.Max(vTL.Y, vBR.Y - sh));

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                rcSource = new W.RECT { L = sx, T = sy, R = sx + sw, B = sy + sh },
                rcDestination = new W.RECT { L = BorderPx, T = BorderPx, R = curW - BorderPx, B = curH - curFoot },
                opacity = alpha,
                fVisible = 1,
                fSourceClientAreaOnly = 1,
            };
            DwmUpdateThumbnailProperties(_thumb, &props);
        }
        else if (_thumb != 0)
        {
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_OPACITY | DWM_TNP_VISIBLE,
                opacity = alpha,
                fVisible = alpha > 4 ? 1 : 0,
            };
            DwmUpdateThumbnailProperties(_thumb, &props);
        }

        W.InvalidateRect(_hwnd, null, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case W.WM_MOUSEACTIVATE: return 3; // MA_NOACTIVATE
            case W.WM_ERASEBKGND: return 1;
            case W.WM_TIMER:
                Poll();
                return 0;
            case W.WM_PAINT:
            {
                W.PAINTSTRUCT ps;
                var dc = W.BeginPaint(h, &ps);
                W.RECT rc;
                W.GetClientRect(h, &rc);
                int bw = rc.R - rc.L, bh = rc.B - rc.T;

                // Dark sleek background for border & footer strip
                var bgBr = W.CreateSolidBrush(W.Rgb(18, 18, 22));
                W.FillRect(dc, &rc, bgBr);
                W.DeleteObject(bgBr);

                // Subtle crisp outer border
                var pen = CreatePen(0, 1, W.Rgb(72, 72, 82));
                var oldP = W.SelectObject(dc, pen);
                var oldB = W.SelectObject(dc, GetStockObject(5 /*NULL_BRUSH*/));
                Rectangle(dc, 0, 0, bw, bh);
                W.SelectObject(dc, oldB);
                W.SelectObject(dc, oldP);
                W.DeleteObject(pen);

                // Footer label showing current magnification & hint
                var dpi = W.GetDpiForWindow(h) / 96f;
                if (dpi < 0.5f) dpi = 1f;
                if (_font == 0 || MathF.Abs(_fontScale - dpi) > 0.05f)
                {
                    if (_font != 0) W.DeleteObject(_font);
                    _font = W.CreateFontW(-(int)(11.5f * dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                    _fontScale = dpi;
                }
                int footH = Math.Max(16, (int)(FooterDip * dpi * (0.78f + 0.22f * (1f - MathF.Pow(1f - _anim, 3f)))));
                var tr = new W.RECT { L = 8, T = bh - footH, R = bw - 8, B = bh };
                W.SetBkMode(dc, 1 /*TRANSPARENT*/);
                W.SetTextColor(dc, W.Rgb(215, 215, 224));
                var oldF = W.SelectObject(dc, _font);
                W.DrawTextW(dc, $"{_zoomTarget:0.0}×  ·  Ctrl + колесо", -1, &tr, 0x00000025 /*DT_CENTER|DT_VCENTER|DT_SINGLELINE*/);
                W.SelectObject(dc, oldF);

                W.EndPaint(h, &ps);
                return 0;
            }
        }
        return W.DefWindowProcW(h, m, w, l);
    }
}
