using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluxTube;

/// <summary>
/// Crisp 2.5x square magnifying loupe that appears when hovering over any video
/// and holding Ctrl. Positioned cleanly beside the cursor so the sampled area is
/// never occluded and works on every Windows GPU driver at 60 FPS.
/// </summary>
internal static unsafe partial class Loupe
{
    private const int BoxDip = 260;
    private const float Zoom = 2.5f;

    private static nint _hwnd;
    private static bool _registered, _visible;
    private static int _lastCx, _lastCy;

    [LibraryImport("user32.dll")] private static partial nint GetDC(nint h);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint h, nint dc);
    [LibraryImport("gdi32.dll")] private static partial int SetStretchBltMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetBrushOrgEx(nint dc, int x, int y, nint prev);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool StretchBlt(nint dst, int dx, int dy, int dw, int dh, nint src, int sx, int sy, int sw, int sh, uint rop);
    [LibraryImport("gdi32.dll")] private static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] private static partial nint GetStockObject(int i);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool RoundRect(nint dc, int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool MoveToEx(nint dc, int x, int y, nint prev);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool LineTo(nint dc, int x, int y);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowDisplayAffinity(nint h, uint affinity);

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
                    style = 0x00020000 /*CS_DROPSHADOW*/,
                };
                W.RegisterClassExW(&wc);
            }
        }
        _hwnd = W.CreateWindowExW(
            W.WS_EX_TOPMOST | W.WS_EX_TOOLWINDOW | W.WS_EX_NOACTIVATE | W.WS_EX_TRANSPARENT,
            "FluxLoupe", null, W.WS_POPUP, 0, 0, BoxDip, BoxDip, App.Hwnd, 0, 0, 0);
        // Exclude from capture where supported so there is never self-capture recursion
        SetWindowDisplayAffinity(_hwnd, 0x00000011 /*WDA_EXCLUDEFROMCAPTURE*/);
    }

    public static void Update()
    {
        W.POINT pt = default;
        bool want = W.KeyDown(W.VK_CONTROL) && !Menus.Open && !AccountMenu.Open && IsCursorOverVideo(out pt);
        if (!want)
        {
            Hide();
            return;
        }
        EnsureWindow();
        var s = Gfx.Scale;
        int box = (int)MathF.Round(BoxDip * s);
        int srcHalf = (int)MathF.Round(box / (2f * Zoom));
        int offset = srcHalf + (int)MathF.Round(18 * s);

        // Place loupe cleanly to the top-right of cursor (flipping inside monitor bounds)
        var mon = W.MonitorFromWindow(App.Hwnd, 2);
        var mi = new W.MONITORINFO { cbSize = (uint)sizeof(W.MONITORINFO) };
        W.GetMonitorInfoW(mon, &mi);
        int wx = pt.X + offset;
        int wy = pt.Y - box / 2;
        if (wx + box > mi.rcMonitor.R - 8) wx = pt.X - offset - box;
        if (wx < mi.rcMonitor.L + 8) wx = mi.rcMonitor.L + 8;
        wy = Math.Clamp(wy, mi.rcMonitor.T + 8, Math.Max(mi.rcMonitor.T + 8, mi.rcMonitor.B - box - 8));

        _lastCx = pt.X;
        _lastCy = pt.Y;

        if (!_visible)
        {
            _visible = true;
            var rgn = W.CreateRoundRectRgn(0, 0, box + 1, box + 1, (int)(22 * s), (int)(22 * s));
            W.SetWindowRgn(_hwnd, rgn, false);
            W.SetWindowPos(_hwnd, (nint)(-1) /*HWND_TOPMOST*/, wx, wy, box, box, W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
            W.SetTimer(_hwnd, 1, 16, 0);
        }
        else
        {
            W.SetWindowPos(_hwnd, (nint)(-1), wx, wy, box, box, W.SWP_NOACTIVATE);
        }
        W.InvalidateRect(_hwnd, null, 0);
    }

    public static void Hide()
    {
        if (!_visible) return;
        _visible = false;
        W.KillTimer(_hwnd, 1);
        W.ShowWindow(_hwnd, W.SW_HIDE);
    }

    private static bool IsCursorOverVideo(out W.POINT pt)
    {
        W.POINT p;
        if (!W.GetCursorPos(&p)) { pt = default; return false; }
        pt = p;
        if (App.VideoHwnd == 0 || !W.IsWindowVisible(App.VideoHwnd)) return false;
        W.RECT rc;
        if (!W.GetWindowRect(App.VideoHwnd, &rc)) return false;
        return p.X >= rc.L && p.X < rc.R && p.Y >= rc.T && p.Y < rc.B;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case W.WM_MOUSEACTIVATE: return 3; // MA_NOACTIVATE
            case W.WM_ERASEBKGND: return 1;
            case W.WM_TIMER:
                Update();
                return 0;
            case W.WM_PAINT:
            {
                W.PAINTSTRUCT ps;
                var dc = W.BeginPaint(h, &ps);
                W.RECT rc;
                W.GetClientRect(h, &rc);
                int bw = rc.R - rc.L, bh = rc.B - rc.T;
                int sw = Math.Max(16, (int)MathF.Round(bw / Zoom));
                int sh = Math.Max(16, (int)MathF.Round(bh / Zoom));
                var screenDc = GetDC(0);
                if (screenDc != 0)
                {
                    SetStretchBltMode(dc, 4 /*HALFTONE*/);
                    SetBrushOrgEx(dc, 0, 0, 0);
                    StretchBlt(dc, 0, 0, bw, bh, screenDc, _lastCx - sw / 2, _lastCy - sh / 2, sw, sh, 0x00CC0020 /*SRCCOPY*/);
                    ReleaseDC(0, screenDc);
                }

                // Subtle center crosshair + crisp rounded border
                int cx = bw / 2, cy = bh / 2;
                var crossPen = CreatePen(0, 1, W.Rgb(255, 255, 255));
                var oldPen = W.SelectObject(dc, crossPen);
                MoveToEx(dc, cx - 6, cy, 0); LineTo(dc, cx + 7, cy);
                MoveToEx(dc, cx, cy - 6, 0); LineTo(dc, cx, cy + 7);
                W.SelectObject(dc, oldPen);
                W.DeleteObject(crossPen);

                var s = Gfx.Scale;
                var borderPen = CreatePen(0, Math.Max(2, (int)(2 * s)), W.Rgb(235, 235, 240));
                var oldP2 = W.SelectObject(dc, borderPen);
                var oldBr = W.SelectObject(dc, GetStockObject(5 /*NULL_BRUSH*/));
                RoundRect(dc, 1, 1, bw - 1, bh - 1, (int)(22 * s), (int)(22 * s));
                W.SelectObject(dc, oldBr);
                W.SelectObject(dc, oldP2);
                W.DeleteObject(borderPen);

                W.EndPaint(h, &ps);
                return 0;
            }
        }
        return W.DefWindowProcW(h, m, w, l);
    }
}
