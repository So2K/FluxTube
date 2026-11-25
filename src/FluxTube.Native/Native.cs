using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluxTube;

/// <summary>
/// A real Win32 EDIT inside a box the renderer draws. Text entry (IME, selection, clipboard, undo)
/// is the one thing not worth re-implementing; everything around it is ours.
/// </summary>
public sealed unsafe class Edit
{
    private static readonly Dictionary<nint, Edit> _all = new();
    private static nint _oldProc;
    private static readonly Dictionary<uint, nint> _brushes = new();

    public static bool AnyFocused => _all.ContainsKey(W.GetFocus());

    public nint H { get; }
    public bool Multiline { get; }
    public Action? Enter, Changed, Escape;
    public Func<int, bool>? Arrow;
    /// <summary>Colour of the box the renderer draws behind the control (read on every repaint: themes change it).</summary>
    public Func<uint> Bg = () => Th.Field;
    private nint _font;
    private float _fontScale;
    private W.RECT _last;
    private bool _visible;
    private bool _silent;

    public Edit(string placeholder, bool multiline = false)
    {
        Multiline = multiline;
        uint style = W.WS_CHILD | W.WS_CLIPSIBLINGS | (multiline
            ? (W.ES_MULTILINE | W.ES_WANTRETURN | W.ES_AUTOVSCROLL | W.ES_AUTOHSCROLL | W.WS_VSCROLL)
            : W.ES_AUTOHSCROLL);
        H = W.CreateWindowExW(0, "EDIT", "", style, 0, 0, 10, 10, App.Hwnd, 0, 0, 0);
        _all[H] = this;
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> proc = &Proc;
        var prev = W.SetWindowLongPtrW(H, W.GWLP_WNDPROC, (nint)proc);
        if (_oldProc == 0) _oldProc = prev;
        if (!multiline)
        {
            fixed (char* p = placeholder) W.SendMessageW(H, 0x1501 /*EM_SETCUEBANNER*/, 1, (nint)p);
        }
        W.SendMessageW(H, 0x00C5 /*EM_SETLIMITTEXT*/, multiline ? 16384u : 2000u, 0);
    }

    public string Text
    {
        get => W.GetText(H);
        set { _silent = true; W.SetWindowTextW(H, value); W.SendMessageW(H, 0x00B1 /*EM_SETSEL*/, (nuint)value.Length, value.Length); _silent = false; }
    }

    /// <summary>Shows the start of the text (a long link otherwise sits scrolled to its end).</summary>
    public void ShowStart()
    {
        W.SendMessageW(H, 0x00B1 /*EM_SETSEL*/, 0, 0);
        W.SendMessageW(H, 0x00B7 /*EM_SCROLLCARET*/, 0, 0);
    }

    public bool Focused => W.GetFocus() == H;
    public void Focus(bool selectAll = true)
    {
        W.SetFocus(H);
        if (selectAll) W.SendMessageW(H, 0x00B1, 0, -1);
        else { var len = W.GetWindowTextLengthW(H); W.SendMessageW(H, 0x00B1, (nuint)len, len); }
    }

    /// <summary>Puts the control inside box (DIPs), vertically centred (single-line) or filling box (multiline), and shows it.</summary>
    public void Place(R box, float padLeft = 14, float padRight = 14, bool allowInModal = false)
    {
        // a native control paints above everything the renderer draws: step aside for open overlays
        if (!allowInModal && ThemeCodeEditor.Open) { Hide(); return; }
        if (AccountMenu.Open && AccountMenu.Rect is { W: > 0 } m && box.Right > m.X && box.X < m.Right && box.Bottom > m.Y && box.Y < m.Bottom) { Hide(); return; }
        if (Menus.Open && Menus.Bounds is { W: > 0 } mb && box.Right > mb.X && box.X < mb.Right && box.Bottom > mb.Y && box.Y < mb.Bottom) { Hide(); return; }
        if (Shell.TabPreviewRect is { W: > 0 } tp && box.Right > tp.X && box.X < tp.Right && box.Bottom > tp.Y && box.Y < tp.Bottom) { Hide(); return; }
        var s = Gfx.Scale;
        if (_fontScale != s)
        {
            var oldFont = _font;
            _font = W.CreateFontW(-(int)MathF.Round((Multiline ? 13.5f : 14.5f) * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /*CLEARTYPE*/, 0, Multiline ? "Consolas" : Gfx.Font);
            _fontScale = s;
            W.SendMessageW(H, W.WM_SETFONT, (nuint)_font, 1);
            if (oldFont != 0) W.DeleteObject(oldFont);
        }
        int l = (int)MathF.Round((box.X + padLeft) * s);
        int r = Math.Max(l + 1, (int)MathF.Round((box.Right - padRight) * s));
        W.RECT rc;
        if (Multiline)
        {
            int padY = (int)MathF.Round(10 * s);
            rc = new W.RECT { L = l, T = (int)MathF.Round(box.Y * s) + padY, R = r, B = Math.Max((int)MathF.Round(box.Y * s) + padY + 20, (int)MathF.Round(box.Bottom * s) - padY) };
        }
        else
        {
            int h = (int)MathF.Round(21 * s);
            rc = new W.RECT { L = l, T = (int)MathF.Round((box.Y + box.H / 2) * s) - h / 2, R = r };
            rc.B = rc.T + h;
        }
        if (!_visible || rc.L != _last.L || rc.T != _last.T || rc.R != _last.R || rc.B != _last.B)
        {
            _last = rc; _visible = true;
            W.SetWindowPos(H, 0, rc.L, rc.T, rc.R - rc.L, rc.B - rc.T, W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
        }
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        if (Focused) W.SetFocus(App.Hwnd);
        W.ShowWindow(H, W.SW_HIDE);
    }

    /// <summary>After a theme change: the controls paint their own background and text.</summary>
    public static void RepaintAll()
    {
        foreach (var h in _all.Keys) W.InvalidateRect(h, null, 1);
    }

    public static void OnCommand(nint h)
    {
        if (_all.TryGetValue(h, out var e) && !e._silent) e.Changed?.Invoke();
    }

    public static nint CtlColor(nint hdc, nint h)
    {
        if (!_all.TryGetValue(h, out var e)) return 0;
        var bg = e.Bg();
        var c = W.Rgb((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg);
        W.SetTextColor(hdc, W.Rgb((byte)(Th.Text >> 16), (byte)(Th.Text >> 8), (byte)Th.Text));
        W.SetBkColor(hdc, c);
        if (!_brushes.TryGetValue(c, out var b))
        {
            if (_brushes.Count > 24) { foreach (var old in _brushes.Values) W.DeleteObject(old); _brushes.Clear(); }
            _brushes[c] = b = W.CreateSolidBrush(c);
        }
        return b;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint h, uint m, nuint w, nint l)
    {
        if (_all.TryGetValue(h, out var e))
        {
            if (m == W.WM_KEYDOWN)
            {
                if ((int)w == 'A' && W.KeyDown(W.VK_CONTROL))
                {
                    W.SendMessageW(h, 0x00B1 /*EM_SETSEL*/, 0, -1);
                    return 0;
                }
                switch ((int)w)
                {
                    case W.VK_RETURN:
                        if (!e.Multiline || W.KeyDown(W.VK_CONTROL)) { e.Enter?.Invoke(); return 0; }
                        break;
                    case W.VK_ESCAPE: e.Escape?.Invoke(); W.SetFocus(App.Hwnd); return 0;
                    case W.VK_UP: if (!e.Multiline && e.Arrow?.Invoke(-1) == true) return 0; break;
                    case W.VK_DOWN: if (!e.Multiline && e.Arrow?.Invoke(1) == true) return 0; break;
                }
            }
            else if (m == W.WM_CHAR)
            {
                if ((!e.Multiline && w == 13) || w == 27 || w == 1) return 0; // no beep / no control char
                if (w == 0x7F) // Ctrl+Backspace: delete previous word instead of inserting a tofu square
                {
                    uint sel0 = 0, sel1 = 0;
                    W.SendMessageW(h, 0x00B0 /*EM_GETSEL*/, (nuint)(&sel0), (nint)(&sel1));
                    int a = (int)sel0, b = (int)sel1;
                    if (a == b && a > 0)
                    {
                        var txt = W.GetText(h);
                        int p = Math.Min(a, txt.Length);
                        while (p > 0 && char.IsWhiteSpace(txt[p - 1])) p--;
                        while (p > 0 && !char.IsWhiteSpace(txt[p - 1])) p--;
                        W.SendMessageW(h, 0x00B1 /*EM_SETSEL*/, (nuint)p, a);
                    }
                    fixed (char* empty = "") W.SendMessageW(h, 0x00C2 /*EM_REPLACESEL*/, 1, (nint)empty);
                    return 0;
                }
            }
            else if (m == W.WM_KILLFOCUS) { Suggest.Hide(); App.Invalidate(); }
            else if (m == W.WM_SETFOCUS) App.Invalidate();
        }
        return W.CallWindowProcW(_oldProc, h, m, w, l);
    }
}

/// <summary>Search suggestions: a tiny no-activate popup, so it sits above the video and the box keeps focus.</summary>
public static unsafe class Suggest
{
    private static nint _hwnd, _font, _bg, _hot;
    private static uint _bgColor;
    private static List<string> _items = [];
    private static int _sel = -1;
    private static float _scale = 1;
    public static Action<string>? Chosen;
    public static bool IsOpen { get; private set; }

    public static void Register()
    {
        fixed (char* cls = "FluxSuggest")
        {
            var wc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW), lpfnWndProc = &Proc, hInstance = W.GetModuleHandleW(0),
                hCursor = W.LoadCursorW(0, 32649), lpszClassName = cls, style = 0x00020000 /*CS_DROPSHADOW*/,
            };
            W.RegisterClassExW(&wc);
        }
        _bg = W.CreateSolidBrush(W.Rgb(0x1E, 0x1E, 0x25));
        _hot = W.CreateSolidBrush(W.Rgb(0x30, 0x30, 0x3A));
    }

    /// <param name="box">The search box in DIPs; the list opens right under it.</param>
    public static void Show(List<string> items, R box)
    {
        if (items.Count == 0) { Hide(); return; }
        _items = items.Count > 10 ? items.GetRange(0, 10) : items;
        _sel = -1;
        var s = Gfx.Scale;
        if (_hwnd == 0)
            _hwnd = W.CreateWindowExW(W.WS_EX_NOACTIVATE | W.WS_EX_TOOLWINDOW | W.WS_EX_TOPMOST, "FluxSuggest", null, W.WS_POPUP, 0, 0, 10, 10, App.Hwnd, 0, 0, 0);
        if (_font == 0 || _scale != s)
        {
            if (_font != 0) W.DeleteObject(_font);
            _font = W.CreateFontW(-(int)MathF.Round(14.5f * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, Gfx.Font);
            _scale = s;
        }
        var p = new W.POINT { X = (int)(box.X * s), Y = (int)((box.Bottom + 4) * s) };
        W.ClientToScreen(App.Hwnd, &p);
        W.SetWindowPos(_hwnd, 0, p.X, p.Y, (int)(box.W * s), (int)(_items.Count * 34 * s + 8 * s), W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW | W.SWP_NOZORDER);
        W.InvalidateRect(_hwnd, null, 0);
        IsOpen = true;
    }

    public static void Hide()
    {
        if (!IsOpen) return;
        IsOpen = false;
        W.ShowWindow(_hwnd, W.SW_HIDE);
    }

    /// <summary>Arrow keys from the search box. Returns the highlighted text.</summary>
    public static string? Move(int d)
    {
        if (!IsOpen || _items.Count == 0) return null;
        _sel = (_sel + d + _items.Count + (_sel < 0 && d < 0 ? 1 : 0)) % _items.Count;
        W.InvalidateRect(_hwnd, null, 0);
        return _items[_sel];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case W.WM_MOUSEACTIVATE: return 3; // MA_NOACTIVATE
            case W.WM_ERASEBKGND: return 1;
            case W.WM_PAINT:
            {
                W.PAINTSTRUCT ps;
                var dc = W.BeginPaint(h, &ps);
                W.RECT rc;
                W.GetClientRect(h, &rc);
                static uint Rgb(uint c) => W.Rgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
                if (_bgColor != Th.Menu) { W.DeleteObject(_bg); W.DeleteObject(_hot); _bgColor = Th.Menu; _bg = W.CreateSolidBrush(Rgb(Th.Menu)); _hot = W.CreateSolidBrush(Rgb(Gfx.Mix(Th.Menu, Th.Ink, 0.1f))); }
                W.FillRect(dc, &rc, _bg);
                var oldF = _font != 0 ? W.SelectObject(dc, _font) : 0;
                W.SetBkMode(dc, 1);
                W.SetTextColor(dc, Rgb(Th.Text));
                int rowH = (int)(34 * _scale), pad = (int)(4 * _scale);
                for (int i = 0; i < _items.Count; i++)
                {
                    var r = new W.RECT { L = pad, T = pad + i * rowH, R = rc.R - pad, B = pad + (i + 1) * rowH };
                    if (i == _sel) W.FillRect(dc, &r, _hot);
                    r.L += (int)(14 * _scale);
                    W.DrawTextW(dc, _items[i], _items[i].Length, &r, 0x0004 | 0x0020 | 0x8000 | 0x0800 /*VCENTER|SINGLELINE|END_ELLIPSIS|NOPREFIX*/);
                }
                if (oldF != 0) W.SelectObject(dc, oldF);
                W.EndPaint(h, &ps);
                return 0;
            }
            case W.WM_MOUSEMOVE:
            {
                var i = (int)MathF.Floor((W.HiWord(l) - 4 * _scale) / (34 * _scale));
                if (i != _sel && i >= 0 && i < _items.Count) { _sel = i; W.InvalidateRect(h, null, 0); }
                var tme = new W.TRACKMOUSEEVENT { cbSize = (uint)sizeof(W.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = h };
                W.TrackMouseEvent(&tme);
                return 0;
            }
            case W.WM_MOUSELEAVE:
                if (_sel != -1) { _sel = -1; W.InvalidateRect(h, null, 0); }
                return 0;
            case W.WM_LBUTTONUP:
                if (_sel >= 0 && _sel < _items.Count) { var s = _items[_sel]; Hide(); Chosen?.Invoke(s); }
                return 0;
        }
        return W.DefWindowProcW(h, m, w, l);
    }
}

public static unsafe partial class Dialogs
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int MessageBoxW(nint h, string text, string caption, uint type);

    [StructLayout(LayoutKind.Sequential)]
    private struct BROWSEINFOW { public nint hwndOwner, pidlRoot; public char* pszDisplayName, lpszTitle; public uint ulFlags; public nint lpfn, lParam; public int iImage; }
    [LibraryImport("shell32.dll")] private static partial nint SHBrowseForFolderW(BROWSEINFOW* bi);
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SHGetPathFromIDListW(nint pidl, char* path);
    [LibraryImport("ole32.dll")] private static partial void CoTaskMemFree(nint p);

    [StructLayout(LayoutKind.Sequential)]
    private struct OPENFILENAMEW
    {
        public uint lStructSize; public nint hwndOwner, hInstance; public char* lpstrFilter, lpstrCustomFilter; public uint nMaxCustFilter, nFilterIndex;
        public char* lpstrFile; public uint nMaxFile; public char* lpstrFileTitle; public uint nMaxFileTitle; public char* lpstrInitialDir, lpstrTitle;
        public uint Flags; public ushort nFileOffset, nFileExtension; public char* lpstrDefExt; public nint lCustData, lpfnHook; public char* lpTemplateName;
        public void* pvReserved; public uint dwReserved, FlagsEx;
    }
    [LibraryImport("comdlg32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetOpenFileNameW(OPENFILENAMEW* ofn);

    private static int _dialogBusy;
    public static bool IsDialogOpen => Volatile.Read(ref _dialogBusy) != 0;

    private static void DrainMouseMessages()
    {
        W.MSG m;
        while (W.PeekMessageW(&m, App.Hwnd, W.WM_LBUTTONDOWN, W.WM_MBUTTONUP, 1 /*PM_REMOVE*/)) { }
    }

    private static void RestoreMainWindow()
    {
        if (App.Hwnd != 0)
        {
            W.EnableWindow(App.Hwnd, 1);
            W.ReleaseCapture();
            W.SetForegroundWindow(App.Hwnd);
            W.SetFocus(App.Hwnd);
            DrainMouseMessages();
        }
        UI.ResetInput();
        App.Invalidate();
    }

    /// <summary>1 = yes/ok, 0 = no, -1 = cancel.</summary>
    public static int Ask(string text, string caption, bool threeWay = false)
    {
        Menus.Close();
        AccountMenu.Close();
        W.ReleaseCapture();
        UI.ResetInput();
        var r = MessageBoxW(App.Hwnd, text, caption, (threeWay ? 0x3u : 0x4u) | 0x20);
        RestoreMainWindow();
        return r == 6 ? 1 : r == 7 ? 0 : -1;
    }

    /// <summary>
    /// Runs the native Windows Explorer file picker on a dedicated STA thread so it NEVER nests inside WM_POST
    /// or leaves App.Hwnd disabled / click-locked on repeated invocations.
    /// </summary>
    public static void PickFileAsync(string filter, Action<string> onPicked)
    {
        if (Interlocked.CompareExchange(ref _dialogBusy, 1, 0) != 0) return;
        Menus.Close();
        AccountMenu.Close();
        Suggest.Hide();
        W.ReleaseCapture();
        UI.ResetInput();
        var owner = App.Hwnd;
        if (owner != 0) W.EnableWindow(owner, 0);

        var t = new Thread(() =>
        {
            string? picked = null;
            try
            {
                W.CoInitializeEx(0, 0x2 /*COINIT_APARTMENTTHREADED*/);
                var buf = new char[4096];
                fixed (char* pBuf = buf)
                fixed (char* pFilter = filter)
                {
                    pBuf[0] = '\0';
                    var ofn = new OPENFILENAMEW
                    {
                        lStructSize = (uint)sizeof(OPENFILENAMEW),
                        hwndOwner = owner,
                        lpstrFilter = pFilter,
                        lpstrFile = pBuf,
                        nMaxFile = (uint)buf.Length,
                        Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008 /*EXPLORER|FILEMUSTEXIST|PATHMUSTEXIST|NOCHANGEDIR*/,
                    };
                    if (GetOpenFileNameW(&ofn))
                        picked = new string(pBuf);
                }
            }
            catch (Exception e)
            {
                Services.Paths.Log("PickFileAsync: " + e.Message);
            }
            finally
            {
                if (owner != 0)
                {
                    W.EnableWindow(owner, 1);
                    W.SetForegroundWindow(owner);
                }
                Interlocked.Exchange(ref _dialogBusy, 0);
                var result = picked;
                App.Post(() =>
                {
                    RestoreMainWindow();
                    if (!string.IsNullOrEmpty(result)) onPicked(result);
                });
            }
        })
        { IsBackground = true, Name = "file-dialog" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Runs the native folder browser on a dedicated STA thread, always restoring focus & click handling cleanly.</summary>
    public static void PickFolderAsync(string title, Action<string> onPicked)
    {
        if (Interlocked.CompareExchange(ref _dialogBusy, 1, 0) != 0) return;
        Menus.Close();
        AccountMenu.Close();
        Suggest.Hide();
        W.ReleaseCapture();
        UI.ResetInput();
        var owner = App.Hwnd;
        if (owner != 0) W.EnableWindow(owner, 0);

        var t = new Thread(() =>
        {
            string? picked = null;
            try
            {
                W.CoInitializeEx(0, 0x2 /*COINIT_APARTMENTTHREADED*/);
                var buf = new char[1024];
                fixed (char* pBuf = buf)
                fixed (char* pTitle = title)
                {
                    var bi = new BROWSEINFOW
                    {
                        hwndOwner = owner,
                        pszDisplayName = pBuf,
                        lpszTitle = pTitle,
                        ulFlags = 0x0001 | 0x0040 /*RETURNONLYFSDIRS|NEWDIALOGSTYLE*/,
                    };
                    var pidl = SHBrowseForFolderW(&bi);
                    if (pidl != 0)
                    {
                        if (SHGetPathFromIDListW(pidl, pBuf)) picked = new string(pBuf);
                        CoTaskMemFree(pidl);
                    }
                }
            }
            catch (Exception e)
            {
                Services.Paths.Log("PickFolderAsync: " + e.Message);
            }
            finally
            {
                if (owner != 0)
                {
                    W.EnableWindow(owner, 1);
                    W.SetForegroundWindow(owner);
                }
                Interlocked.Exchange(ref _dialogBusy, 0);
                var result = picked;
                App.Post(() =>
                {
                    RestoreMainWindow();
                    if (!string.IsNullOrEmpty(result)) onPicked(result);
                });
            }
        })
        { IsBackground = true, Name = "folder-dialog" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
