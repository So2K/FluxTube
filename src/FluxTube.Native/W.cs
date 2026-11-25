using System.Runtime.InteropServices;

namespace FluxTube;

/// <summary>The Win32 surface the shell uses. Blittable signatures only, so it compiles ahead of time.</summary>
internal static unsafe partial class W
{
    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_PAINT = 0x000F, WM_CLOSE = 0x0010,
        WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020, WM_GETMINMAXINFO = 0x0024, WM_SETFONT = 0x0030, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102,
        WM_SYSKEYDOWN = 0x0104, WM_COMMAND = 0x0111, WM_TIMER = 0x0113, WM_CTLCOLOREDIT = 0x0133, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A,
        WM_XBUTTONUP = 0x020C, WM_MOUSELEAVE = 0x02A3, WM_DPICHANGED = 0x02E0, WM_MOVE = 0x0003, WM_ACTIVATE = 0x0006, WM_APP = 0x8000,
        WM_MOUSEACTIVATE = 0x0021, WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_GETTEXTLENGTH = 0x000E, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000, WS_CLIPCHILDREN = 0x02000000,
        WS_CLIPSIBLINGS = 0x04000000, WS_POPUP = 0x80000000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008,
        ES_AUTOHSCROLL = 0x0080;

    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;
    public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNA = 8, SW_SHOWNOACTIVATE = 4, SW_SHOWNORMAL = 1, SW_MAXIMIZE = 3;
    public const int GWL_STYLE = -16, GWLP_WNDPROC = -4;
    public const int VK_BACK = 8, VK_RETURN = 13, VK_ESCAPE = 27, VK_SPACE = 32, VK_LEFT = 37, VK_UP = 38, VK_RIGHT = 39, VK_DOWN = 40, VK_CONTROL = 17, VK_MENU = 18,
        VK_HOME = 36, VK_END = 35, VK_PRIOR = 33, VK_NEXT = 34, VK_F5 = 116, VK_OEM_COMMA = 0xBC, VK_OEM_PERIOD = 0xBE, VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; public uint priv; }
    [StructLayout(LayoutKind.Sequential)] public struct MINMAXINFO { public POINT reserved, maxSize, maxPosition, minTrackSize, maxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public uint length, flags, showCmd; public POINT ptMin, ptMax; public RECT rcNormal; }
    [StructLayout(LayoutKind.Sequential)] public struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

    [LibraryImport("user32.dll")] public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(MSG* msg, nint h, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial nint SendMessageW(nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint h, RECT* r, int erase);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ValidateRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint h);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] public static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32.dll")] public static partial nint LoadIconW(nint inst, nint id);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")] public static partial nint LoadImageById(nint inst, nint id, uint type, int cx, int cy, uint load);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial nint LoadCursorW(nint inst, nint id);
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cur);
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int vk);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint h, nuint id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* e);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EnableWindow(nint h, int enable);
    [LibraryImport("user32.dll")] public static partial nint GetFocus();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(POINT* p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ScreenToClient(nint h, POINT* p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ClientToScreen(nint h, POINT* p);
    [LibraryImport("user32.dll")] public static partial nint SetWindowLongPtrW(nint h, int idx, nint v);
    [LibraryImport("user32.dll")] public static partial nint GetWindowLongPtrW(nint h, int idx);
    [LibraryImport("user32.dll")] public static partial nint CallWindowProcW(nint prev, nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowTextW(nint h, string s);
    [LibraryImport("user32.dll")] public static partial int GetWindowTextW(nint h, char* buf, int max);
    [LibraryImport("user32.dll")] public static partial int GetWindowTextLengthW(nint h);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromWindow(nint h, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfoW(nint mon, MONITORINFO* mi);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowPlacement(nint h, WINDOWPLACEMENT* p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPlacement(nint h, WINDOWPLACEMENT* p);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint h, nint rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] public static partial nint BeginPaint(nint h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EndPaint(nint h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] public static partial int FillRect(nint dc, RECT* r, nint brush);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawTextW(nint dc, string s, int n, RECT* r, uint fmt);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool OpenClipboard(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] public static partial nint SetClipboardData(uint fmt, nint mem);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsIconic(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsZoomed(nint h);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("kernel32.dll")] public static partial nint GetModuleHandleW(nint name);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalLock(nint mem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint mem);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outp, uint clip, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] public static partial nint CreateRectRgn(int l, int t, int r, int b);
    [LibraryImport("gdi32.dll")] public static partial int CombineRgn(nint dst, nint a, nint b, int mode);
    [LibraryImport("gdi32.dll")] public static partial nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [LibraryImport("user32.dll")] public static partial nint SetParent(nint child, nint parent);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    public const int GWL_EXSTYLE = -20;
    public const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] public static partial int SetWindowRgn(nint h, nint rgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] public static partial uint SetBkColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint h, uint attr, void* value, uint size);
    [LibraryImport("uxtheme.dll", EntryPoint = "#135")] public static partial int SetPreferredAppMode(int mode);
    [LibraryImport("uxtheme.dll", EntryPoint = "#136")] public static partial void FlushMenuThemes();
    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int SetWindowTheme(nint h, string? app, string? ids);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint ShellExecuteW(nint h, string? op, string file, string? args, string? dir, int show);
    [LibraryImport("ole32.dll")] public static partial int CoInitializeEx(nint reserved, uint coinit);

    public static int LoWord(nint l) => (short)(l.ToInt64() & 0xFFFF);
    public static int HiWord(nint l) => (short)((l.ToInt64() >> 16) & 0xFFFF);
    public static bool KeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16);

    public static void SetClipboardText(nint owner, string text)
    {
        for (int attempt = 0; !OpenClipboard(owner); attempt++)
        {
            if (attempt >= 8) { Services.Paths.Log("clipboard is busy, nothing copied"); return; }
            Thread.Sleep(25);
        }
        EmptyClipboard();
        var bytes = (nuint)((text.Length + 1) * 2);
        var mem = GlobalAlloc(0x0002 /*GMEM_MOVEABLE*/, bytes);
        var p = (char*)GlobalLock(mem);
        text.AsSpan().CopyTo(new Span<char>(p, text.Length));
        p[text.Length] = '\0';
        GlobalUnlock(mem);
        SetClipboardData(13 /*CF_UNICODETEXT*/, mem);
        CloseClipboard();
    }

    public static string GetText(nint h)
    {
        var n = GetWindowTextLengthW(h);
        if (n <= 0) return "";
        var buf = stackalloc char[n + 1];
        var got = GetWindowTextW(h, buf, n + 1);
        return new string(buf, 0, got);
    }

    public static void Open(string target, string? args = null) => ShellExecuteW(0, "open", target, args, null, SW_SHOWNORMAL);
}
