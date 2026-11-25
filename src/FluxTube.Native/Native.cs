using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluxTube;

/// <summary>
/// A real Win32 EDIT inside a box the renderer draws. Text entry (IME, selection, clipboard, undo)
/// is handled natively; movement is deferred to AfterFrame() with SWP_NOCOPYBITS and seamlessly falls back
/// to crisp Direct2D text rendering while scrolling, clipped, or under overlays — zero floating or screen-capture artifacts.
/// </summary>
public sealed unsafe class Edit
{
    private static readonly Dictionary<nint, Edit> _all = new();
    private static nint _oldProc;
    private static readonly Dictionary<uint, nint> _brushes = new();

    public static bool AnyFocused => _all.ContainsKey(W.GetFocus());

    public static bool IsPointInFocusedSingleLine(float x, float y)
    {
        var fh = W.GetFocus();
        if (fh == 0 || !_all.TryGetValue(fh, out var e) || e.Multiline) return false;
        return Gfx.Frame - e._placedFrame <= 1 && e._lastBox.Contains(x, y);
    }

    public nint H { get; }
    public bool Multiline { get; }
    public Action? Enter, Changed, Escape;
    public Func<int, bool>? Arrow;
    /// <summary>Colour of the box the renderer draws behind the control (read on every repaint: themes change it).</summary>
    public Func<uint> Bg = () => Th.Field;
    private string _placeholder;
    private string _cachedText = "";
    private nint _font;
    private float _fontScale;
    private W.RECT _last, _wantRect;
    private R _lastBox;
    private long _placedFrame;
    private bool _wantShow;
    private bool _visible;
    private bool _silent;
    private bool _pendingFocus, _pendingSelectAll;
    // Direct2D visual text-box state for single-line inputs (zero Win32 GDI child-HWND over DXGI artifacts!)
    private float _scrollX;
    private bool _mouseSelecting;
    private int _dragAnchor;
    private long _caretBlinkAt = Environment.TickCount64;
    private long _lastClickAt;
    private int _lastClickPos, _clickStreak;
    private Vortice.DirectWrite.HitTestMetrics[] _selRects = new Vortice.DirectWrite.HitTestMetrics[8];

    public Edit(string placeholder, bool multiline = false)
    {
        _placeholder = placeholder;
        Multiline = multiline;
        uint style = W.WS_CHILD | W.WS_CLIPSIBLINGS | (multiline
            ? (W.ES_MULTILINE | W.ES_WANTRETURN | W.ES_AUTOVSCROLL | W.ES_AUTOHSCROLL | W.WS_VSCROLL)
            : W.ES_AUTOHSCROLL);
        // Single-line Edit stays off-screen as a native keyboard/IME/clipboard/undo engine while Direct2D draws 100% of pixels!
        H = W.CreateWindowExW(0, "EDIT", "", style, multiline ? 0 : -4000, multiline ? 0 : -4000, 320, multiline ? 200 : 28, App.Hwnd, 0, 0, 0);
        _all[H] = this;
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> proc = &Proc;
        var prev = W.SetWindowLongPtrW(H, W.GWLP_WNDPROC, (nint)proc);
        if (_oldProc == 0) _oldProc = prev;
        if (multiline) W.SetWindowTheme(H, "", "");
        W.SendMessageW(H, 0x00C5 /*EM_SETLIMITTEXT*/, multiline ? 16384u : 2000u, 0);
    }

    public void SetPlaceholder(string text)
    {
        if (_placeholder == text) return;
        _placeholder = text;
        App.Invalidate();
    }

    public string Text
    {
        get => W.GetText(H);
        set
        {
            _cachedText = value ?? "";
            _silent = true;
            W.SetWindowTextW(H, _cachedText);
            if (Focused)
            {
                W.SendMessageW(H, 0x00B1 /*EM_SETSEL*/, (nuint)_cachedText.Length, _cachedText.Length);
                W.SendMessageW(H, 0x00B7 /*EM_SCROLLCARET*/, 0, 0);
            }
            else
            {
                W.SendMessageW(H, 0x00B1 /*EM_SETSEL*/, 0, 0);
                W.SendMessageW(H, 0x00B7 /*EM_SCROLLCARET*/, 0, 0);
                _scrollX = 0;
            }
            _caretBlinkAt = Environment.TickCount64;
            _silent = false;
            if (Multiline && _visible) W.InvalidateRect(H, null, 1);
            App.Invalidate();
        }
    }

    /// <summary>Shows the start of the text (a long link otherwise sits scrolled to its end).</summary>
    public void ShowStart()
    {
        _scrollX = 0;
        W.SendMessageW(H, 0x00B1 /*EM_SETSEL*/, 0, 0);
        W.SendMessageW(H, 0x00B7 /*EM_SCROLLCARET*/, 0, 0);
    }

    public bool Focused => W.GetFocus() == H;
    public void Focus(bool selectAll = true)
    {
        _caretBlinkAt = Environment.TickCount64;
        if (!Multiline)
        {
            if (!_visible)
            {
                _visible = true;
                W.SetWindowPos(H, 0, -4000, -4000, 320, 28, W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW | W.SWP_NOCOPYBITS);
            }
            W.SetFocus(H);
            if (selectAll) W.SendMessageW(H, 0x00B1, 0, -1);
            else { var len = W.GetWindowTextLengthW(H); W.SendMessageW(H, 0x00B1, (nuint)len, len); }
            App.Invalidate();
            return;
        }
        if (!_visible)
        {
            _pendingFocus = true;
            _pendingSelectAll = selectAll;
            App.Invalidate();
            return;
        }
        W.SetFocus(H);
        if (selectAll) W.SendMessageW(H, 0x00B1, 0, -1);
        else { var len = W.GetWindowTextLengthW(H); W.SendMessageW(H, 0x00B1, (nuint)len, len); }
        W.SendMessageW(H, 0x00B7 /*EM_SCROLLCARET*/, 0, 0);
    }

    private (int A, int B) GetSelection()
    {
        uint a = 0, b = 0;
        W.SendMessageW(H, 0x00B0 /*EM_GETSEL*/, (nuint)(&a), (nint)(&b));
        int len = _cachedText.Length;
        return (Math.Clamp((int)a, 0, len), Math.Clamp((int)b, 0, len));
    }

    private void SetSelection(int a, int b)
    {
        W.SendMessageW(H, 0x00B1 /*EM_SETSEL*/, (nuint)Math.Max(0, a), Math.Max(0, b));
        _caretBlinkAt = Environment.TickCount64;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    /// <summary>
    /// Renders single-line text boxes 100% natively in Direct2D (crisp vertical centering, zero Win32 child-HWND artifacts,
    /// zero clipped letters, smooth caret &amp; selection, native keyboard/IME via off-screen Win32 EDIT).
    /// Multiline editors (ThemeCodeEditor) place their native EDIT cleanly inside the modal.
    /// </summary>
    public void Place(R box, float padLeft = 14, float padRight = 14, bool allowInModal = false)
    {
        _placedFrame = Gfx.Frame;

        if (!Multiline)
        {
            DrawSingleLineD2D(box, padLeft, padRight, allowInModal);
            return;
        }

        // Multiline Win32 EDIT placement (ThemeCodeEditor)
        bool blocked = (!allowInModal && (ThemeCodeEditor.Open || AudioStudioPanel.Open || LoginModal.Open))
            || (AccountMenu.Open && AccountMenu.Rect is { W: > 0 } m && box.Right > m.X && box.X < m.Right && box.Bottom > m.Y && box.Y < m.Bottom)
            || (Menus.Open && Menus.Bounds is { W: > 0 } mb && box.Right > mb.X && box.X < mb.Right && box.Bottom > mb.Y && box.Y < mb.Bottom)
            || (SharePanel.Open && SharePanel.Rect is { W: > 0 } sp && box.Right > sp.X && box.X < sp.Right && box.Bottom > sp.Y && box.Y < sp.Bottom)
            || (Shell.TabPreviewRect is { W: > 0 } tp && box.Right > tp.X && box.X < tp.Right && box.Bottom > tp.Y && box.Y < tp.Bottom);

        int scrollId = UI.CurrentScrollId;
        bool scrolling = scrollId != 0 && UI.ScrollMoving(scrollId);
        var clip = Gfx.Clip;
        bool clipped = box.Y < clip.Y - 0.5f || box.Bottom > clip.Bottom + 0.5f || box.X < clip.X - 0.5f || box.Right > clip.Right + 0.5f;

        if (blocked || scrolling || clipped || box.W < padLeft + padRight + 8 || box.H < 14)
        {
            _wantShow = false;
            DrawFallback(box, padLeft, padRight);
            return;
        }

        if (UI.Pressed && UI.Hover(box) && !Focused)
        {
            UI.Pressed = false;
            App.Post(() => Focus(selectAll: false));
        }

        if (!Focused && !_pendingFocus)
        {
            _wantShow = false;
            DrawFallback(box, padLeft, padRight);
            return;
        }

        var s = Gfx.Scale;
        if (_fontScale != s)
        {
            var oldFont = _font;
            _font = W.CreateFontW(-(int)MathF.Round(13.5f * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /*CLEARTYPE*/, 0, "Consolas");
            _fontScale = s;
            W.SendMessageW(H, W.WM_SETFONT, (nuint)_font, 1);
            if (oldFont != 0) W.DeleteObject(oldFont);
        }
        int l = (int)MathF.Round((box.X + padLeft) * s);
        int r = Math.Max(l + 1, (int)MathF.Round((box.Right - padRight) * s));
        int padY = (int)MathF.Round(10 * s);
        var rc = new W.RECT { L = l, T = (int)MathF.Round(box.Y * s) + padY, R = r, B = Math.Max((int)MathF.Round(box.Y * s) + padY + 20, (int)MathF.Round(box.Bottom * s) - padY) };
        _wantRect = rc;
        _wantShow = true;
    }

    private void DrawSingleLineD2D(R box, float padLeft, float padRight, bool allowInModal)
    {
        _lastBox = box;
        float innerW = MathF.Max(8, box.W - padLeft - padRight);
        var textClip = new R(box.X + padLeft, box.Y + 2, innerW, MathF.Max(4, box.H - 4));
        bool focused = Focused;
        _wantShow = focused; // keep off-screen HWND alive only while focused

        bool canInteract = !Menus.Open && !AccountMenu.Open && (allowInModal || (!ThemeCodeEditor.Open && !AudioStudioPanel.Open && !LoginModal.Open));
        bool over = canInteract && UI.Hover(box);
        if (over) Sel.RequestIBeam();

        // Measure unwrapped full text for exact hit-testing, selection, and smooth horizontal caret scroll
        var txt = _cachedText;
        var run = txt.Length > 0 ? Gfx.Text(txt, 14f, 400, 0, 1) : null;
        var layout = run?.Layout;
        float textH = run?.H ?? Gfx.LineHeight(14f);
        float textY = Gfx.Snap(box.Y + (box.H - textH) * 0.5f);

        int HitPos(float mx)
        {
            if (layout == null || txt.Length == 0) return 0;
            layout.HitTestPoint(mx - (textClip.X - _scrollX), textH * 0.5f, out var trailing, out _, out var m);
            return Math.Clamp((int)m.TextPosition + (trailing ? (int)m.Length : 0), 0, txt.Length);
        }

        if (over && UI.Pressed)
        {
            UI.Pressed = false;
            int pos = HitPos(UI.MX);
            bool rapid = UI.Now - _lastClickAt < 400 && Math.Abs(pos - _lastClickPos) <= 2;
            _clickStreak = rapid ? (_clickStreak % 3) + 1 : 1;
            _lastClickAt = UI.Now;
            _lastClickPos = pos;

            if (!focused) Focus(selectAll: false);
            if (_clickStreak == 3)
            {
                SetSelection(0, txt.Length);
                _mouseSelecting = false;
            }
            else if (_clickStreak == 2 && txt.Length > 0)
            {
                int a = pos, b = pos;
                while (a > 0 && IsWordChar(txt[a - 1])) a--;
                while (b < txt.Length && IsWordChar(txt[b])) b++;
                SetSelection(a, b);
                _mouseSelecting = false;
            }
            else
            {
                _dragAnchor = pos;
                SetSelection(pos, pos);
                _mouseSelecting = true;
            }
            focused = true;
        }
        else if (_mouseSelecting)
        {
            if (!UI.Down || !focused) _mouseSelecting = false;
            else
            {
                int pos = HitPos(UI.MX);
                SetSelection(Math.Min(_dragAnchor, pos), Math.Max(_dragAnchor, pos));
                UI.Animate();
            }
        }

        if (over && UI.RightClicked)
        {
            if (!focused) Focus(selectAll: false);
            var (sa, sb) = GetSelection();
            bool hasSel = sb > sa;
            var items = new List<MenuItem>();
            if (hasSel)
            {
                items.Add(new(Services.L.T("Вырезать", "Cut"), () => W.SendMessageW(H, 0x0300 /*WM_CUT*/, 0, 0), icon: Th.Copy, shortcut: "Ctrl+X"));
                items.Add(new(Services.L.T("Копировать", "Copy"), () => W.SendMessageW(H, 0x0301 /*WM_COPY*/, 0, 0), icon: Th.Copy, shortcut: "Ctrl+C"));
            }
            items.Add(new(Services.L.T("Вставить", "Paste"), () => { Focus(selectAll: false); W.SendMessageW(H, 0x0302 /*WM_PASTE*/, 0, 0); }, icon: Th.Link, shortcut: "Ctrl+V"));
            if (txt.Length > 0)
            {
                items.Add(MenuItem.Sep);
                items.Add(new(Services.L.T("Выделить всё", "Select all"), () => { Focus(selectAll: true); }, shortcut: "Ctrl+A"));
            }
            Menus.Popup(items);
        }

        var (selA, selB) = focused ? GetSelection() : (0, 0);

        // Smooth horizontal scroll keeping caret visible inside the pill
        float caretX = 0f;
        if (layout != null && txt.Length > 0)
        {
            int caretPos = Math.Clamp(selB, 0, txt.Length);
            if (caretPos == txt.Length) caretX = run!.W;
            else
            {
                layout.HitTestTextPosition((uint)caretPos, false, out var px, out _, out _);
                caretX = px;
            }
        }
        if (focused)
        {
            if (caretX - _scrollX > innerW - 4) _scrollX = caretX - (innerW - 4);
            if (caretX - _scrollX < 0) _scrollX = caretX;
            float maxScr = MathF.Max(0, (run?.W ?? 0) - innerW + 4);
            _scrollX = Math.Clamp(_scrollX, 0, maxScr);
        }
        else _scrollX = 0;

        Gfx.PushClip(textClip);
        if (txt.Length == 0)
        {
            if (_placeholder.Length > 0)
            {
                var pr = Gfx.Text(_placeholder, 14f, 400, innerW, 1);
                Gfx.Draw(pr, textClip.X, Gfx.Snap(box.Y + (box.H - pr.H) * 0.5f), Th.Dim);
            }
        }
        else
        {
            float drawX = textClip.X - _scrollX;
            // Selection highlight
            if (focused && selB > selA && layout != null)
            {
                int n = 0;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var hr = layout.HitTestTextRange((uint)selA, (uint)(selB - selA), drawX, textY, _selRects, out var got);
                    if (hr.Success) { n = (int)got; break; }
                    _selRects = new Vortice.DirectWrite.HitTestMetrics[Math.Max((int)got, _selRects.Length * 2)];
                }
                var hl = Gfx.Fade(Th.Brand, Th.Light ? 0.28f : 0.42f);
                for (int i = 0; i < n; i++)
                    Gfx.Fill(new R(_selRects[i].Left, _selRects[i].Top, _selRects[i].Width, _selRects[i].Height), hl, 2f);
            }
            Gfx.Draw(run!, drawX, textY, Th.Text);
        }

        // Crisp blinking caret when focused
        if (focused)
        {
            long elapsed = UI.Now - _caretBlinkAt;
            bool caretOn = (elapsed / 530) % 2 == 0;
            if (caretOn)
            {
                float cx = Gfx.Snap(textClip.X + caretX - _scrollX);
                Gfx.Fill(new R(cx, textY + 1, 1.5f, MathF.Max(14, textH - 2)), Th.Brand, 0.75f);
            }
            // Schedule a clean blink wakeup (zero 60 FPS idle spin while just sitting in search box!)
            long nextFlip = 535 - (elapsed % 530);
            if (_caretWakeAt < UI.Now + 10)
            {
                _caretWakeAt = UI.Now + nextFlip;
                App.After((int)Math.Clamp(nextFlip, 40, 560), App.Invalidate);
            }
        }
        Gfx.PopClip();
    }

    private long _caretWakeAt;

    private void DrawFallback(R box, float padLeft, float padRight)
    {
        float tw = MathF.Max(8, box.W - padLeft - padRight);
        string shown = _cachedText.Length > 0 ? _cachedText : _placeholder;
        if (shown.Length == 0) return;
        uint col = _cachedText.Length > 0 ? Th.Text : Th.Dim;
        var run = Gfx.Text(shown, Multiline ? 13.5f : 14f, 400, tw, Multiline ? 0 : 1);
        Gfx.PushClip(box);
        Gfx.Draw(run, box.X + padLeft, Gfx.Snap(Multiline ? box.Y + 10 : box.Y + (box.H - run.H) / 2), col);
        Gfx.PopClip();
    }

    public void Hide()
    {
        _wantShow = false;
        _pendingFocus = false;
        _mouseSelecting = false;
        _lastBox = default;
        if (!_visible) return;
        _visible = false;
        if (Focused) W.SetFocus(App.Hwnd);
        W.ShowWindow(H, W.SW_HIDE);
    }

    /// <summary>
    /// Runs once per frame AFTER Gfx.End() / DXGI Present: commits multiline Win32 EDIT positions
    /// and keeps off-screen single-line input engines cleanly synced.
    /// </summary>
    public static void AfterFrame()
    {
        var curFrame = Gfx.Frame;
        foreach (var e in _all.Values)
        {
            if (!e.Multiline)
            {
                if (e._placedFrame != curFrame && e._visible)
                {
                    e._visible = false;
                    if (e.Focused) W.SetFocus(App.Hwnd);
                    W.ShowWindow(e.H, W.SW_HIDE);
                }
                continue;
            }
            if (e._placedFrame != curFrame || !e._wantShow)
            {
                if (e._visible)
                {
                    e._visible = false;
                    if (e.Focused) W.SetFocus(App.Hwnd);
                    W.ShowWindow(e.H, W.SW_HIDE);
                }
                continue;
            }
            var rc = e._wantRect;
            int w = Math.Max(1, rc.R - rc.L), h = Math.Max(1, rc.B - rc.T);
            int oldW = e._last.R - e._last.L;
            bool changed = !e._visible || rc.L != e._last.L || rc.T != e._last.T || rc.R != e._last.R || rc.B != e._last.B;
            if (changed)
            {
                bool wasVisible = e._visible;
                e._last = rc;
                e._visible = true;
                W.SetWindowPos(e.H, 0, rc.L, rc.T, w, h, W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW | W.SWP_NOCOPYBITS);
                if (!e.Multiline && !e.Focused && !e._pendingFocus && (!wasVisible || oldW != w))
                    e.ShowStart();
                W.InvalidateRect(e.H, null, 1);
            }
            if (e._pendingFocus)
            {
                e._pendingFocus = false;
                W.SetFocus(e.H);
                if (e._pendingSelectAll) W.SendMessageW(e.H, 0x00B1, 0, -1);
                else { var len = W.GetWindowTextLengthW(e.H); W.SendMessageW(e.H, 0x00B1, (nuint)len, len); }
                W.SendMessageW(e.H, 0x00B7 /*EM_SCROLLCARET*/, 0, 0);
            }
        }
    }

    /// <summary>After a theme/DPI/monitor change: the controls repaint their own background and text cleanly.</summary>
    public static void RepaintAll()
    {
        foreach (var e in _all.Values)
            if (e._visible) W.InvalidateRect(e.H, null, 1);
    }

    public static void OnCommand(nint h)
    {
        if (_all.TryGetValue(h, out var e))
        {
            e._cachedText = W.GetText(h);
            e._caretBlinkAt = Environment.TickCount64;
            if (!e._silent) e.Changed?.Invoke();
            App.Invalidate();
        }
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
                e._caretBlinkAt = Environment.TickCount64;
                if ((int)w == 'R' && W.KeyDown(W.VK_CONTROL))
                {
                    Shell.Key('R', true, false);
                    return 0;
                }
                if ((int)w == 'A' && W.KeyDown(W.VK_CONTROL))
                {
                    W.SendMessageW(h, 0x00B1 /*EM_SETSEL*/, 0, -1);
                    App.Invalidate();
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
                var retK = W.CallWindowProcW(_oldProc, h, m, w, l);
                if (!e.Multiline) App.Invalidate();
                return retK;
            }
            else if (m == W.WM_CHAR)
            {
                e._caretBlinkAt = Environment.TickCount64;
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
                    if (!e.Multiline) App.Invalidate();
                    return 0;
                }
                var retC = W.CallWindowProcW(_oldProc, h, m, w, l);
                if (!e.Multiline) App.Invalidate();
                return retC;
            }
            else if (m == W.WM_MOUSEWHEEL && !e.Multiline)
            {
                // Forward mouse wheel over single-line text boxes to the main window so page scrolling never stalls!
                W.SendMessageW(App.Hwnd, m, w, l);
                return 0;
            }
            else if (m == W.WM_KILLFOCUS) { Suggest.Hide(); e._mouseSelecting = false; if (!e.Multiline) e.ShowStart(); if (e.Multiline) W.InvalidateRect(h, null, 1); App.Invalidate(); }
            else if (m == W.WM_SETFOCUS) { e._caretBlinkAt = Environment.TickCount64; if (e.Multiline) W.InvalidateRect(h, null, 1); App.Invalidate(); }
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
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AllowSetForegroundWindow(int dwProcessId);
    [LibraryImport("kernel32.dll")] private static partial int GetCurrentProcessId();

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
    private static int _ownerClassReg;
    public static bool IsDialogOpen => Volatile.Read(ref _dialogBusy) != 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OwnerProc(nint h, uint m, nuint w, nint l) => W.DefWindowProcW(h, m, w, l);

    /// <summary>
    /// Creates a tiny invisible same-thread popup window centered on App.Hwnd to own GetOpenFileNameW / SHBrowseForFolderW.
    /// Avoids cross-thread hwndOwner attachment (which deadlocks Direct2D/OLE or opens behind App.Hwnd) AND never disables App.Hwnd!
    /// </summary>
    private static nint CreateSameThreadDialogOwner(int cx, int cy)
    {
        var inst = W.GetModuleHandleW(0);
        if (Interlocked.CompareExchange(ref _ownerClassReg, 1, 0) == 0)
        {
            fixed (char* cls = "FluxDlgOwner")
            {
                var wc = new W.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(W.WNDCLASSEXW),
                    lpfnWndProc = &OwnerProc,
                    hInstance = inst,
                    lpszClassName = cls,
                };
                W.RegisterClassExW(&wc);
            }
        }
        var hwnd = W.CreateWindowExW(W.WS_EX_TOOLWINDOW | W.WS_EX_TOPMOST, "FluxDlgOwner", "", W.WS_POPUP, cx, cy, 1, 1, 0, 0, inst, 0);
        if (hwnd != 0)
        {
            W.ShowWindow(hwnd, W.SW_SHOWNORMAL);
            W.SetForegroundWindow(hwnd);
        }
        return hwnd;
    }

    private static (int X, int Y) GetMainCenterScreen()
    {
        if (App.Hwnd != 0)
        {
            W.RECT rc;
            if (W.GetWindowRect(App.Hwnd, &rc))
                return ((rc.L + rc.R) / 2, (rc.T + rc.B) / 2);
        }
        return (400, 300);
    }

    private static void DrainMouseMessages()
    {
        W.MSG m;
        while (W.PeekMessageW(&m, App.Hwnd, W.WM_LBUTTONDOWN, W.WM_MBUTTONUP, 1 /*PM_REMOVE*/)) { }
    }

    private static void RestoreMainWindow()
    {
        if (App.Hwnd != 0 && !App.Test)
        {
            if (!W.IsWindowEnabled(App.Hwnd)) W.EnableWindow(App.Hwnd, 1);
            W.ReleaseCapture();
            W.SetForegroundWindow(App.Hwnd);
            W.SetFocus(App.Hwnd);
            DrainMouseMessages();
        }
        UI.Down = UI.Pressed = UI.Released = UI.RightClicked = UI.MiddleDown = UI.MiddleClicked = false;
        UI.BlockInput = false;
        UI.Wheel = 0;
        UI.StopAutoScroll();
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
    /// Runs the native Windows Explorer file picker on a dedicated STA thread with its own same-thread owner HWND:
    /// NEVER disables App.Hwnd (clicks in FluxTube never break!) and ALWAYS opens cleanly in the foreground.
    /// </summary>
    public static void PickFileAsync(string filter, Action<string> onPicked)
    {
        if (Interlocked.CompareExchange(ref _dialogBusy, 1, 0) != 0) return;
        Menus.Close();
        AccountMenu.Close();
        Suggest.Hide();
        W.ReleaseCapture();
        UI.Down = UI.Pressed = UI.Released = UI.RightClicked = UI.MiddleDown = UI.MiddleClicked = false;
        var (cx, cy) = GetMainCenterScreen();
        AllowSetForegroundWindow(GetCurrentProcessId());

        try
        {
            var t = new Thread(() =>
            {
                string? picked = null;
                nint dlgOwner = 0;
                int hrCo = -1;
                try
                {
                    hrCo = W.CoInitializeEx(0, 0x2 /*COINIT_APARTMENTTHREADED*/);
                    dlgOwner = CreateSameThreadDialogOwner(cx, cy);
                    var buf = new char[4096];
                    fixed (char* pBuf = buf)
                    fixed (char* pFilter = filter)
                    {
                        pBuf[0] = '\0';
                        var ofn = new OPENFILENAMEW
                        {
                            lStructSize = (uint)sizeof(OPENFILENAMEW),
                            hwndOwner = dlgOwner,
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
                    if (dlgOwner != 0) W.DestroyWindow(dlgOwner);
                    if (hrCo >= 0) W.CoUninitialize();
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
        catch (Exception e)
        {
            Interlocked.Exchange(ref _dialogBusy, 0);
            Services.Paths.Log("PickFileAsync start: " + e.Message);
            RestoreMainWindow();
        }
    }

    /// <summary>Runs the native folder browser on a dedicated STA thread with a same-thread owner HWND (never disables App.Hwnd).</summary>
    public static void PickFolderAsync(string title, Action<string> onPicked)
    {
        if (Interlocked.CompareExchange(ref _dialogBusy, 1, 0) != 0) return;
        Menus.Close();
        AccountMenu.Close();
        Suggest.Hide();
        W.ReleaseCapture();
        UI.Down = UI.Pressed = UI.Released = UI.RightClicked = UI.MiddleDown = UI.MiddleClicked = false;
        var (cx, cy) = GetMainCenterScreen();
        AllowSetForegroundWindow(GetCurrentProcessId());

        try
        {
            var t = new Thread(() =>
            {
                string? picked = null;
                nint dlgOwner = 0;
                int hrCo = -1;
                try
                {
                    hrCo = W.CoInitializeEx(0, 0x2 /*COINIT_APARTMENTTHREADED*/);
                    dlgOwner = CreateSameThreadDialogOwner(cx, cy);
                    var buf = new char[1024];
                    fixed (char* pBuf = buf)
                    fixed (char* pTitle = title)
                    {
                        var bi = new BROWSEINFOW
                        {
                            hwndOwner = dlgOwner,
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
                    if (dlgOwner != 0) W.DestroyWindow(dlgOwner);
                    if (hrCo >= 0) W.CoUninitialize();
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
        catch (Exception e)
        {
            Interlocked.Exchange(ref _dialogBusy, 0);
            Services.Paths.Log("PickFolderAsync start: " + e.Message);
            RestoreMainWindow();
        }
    }
}
