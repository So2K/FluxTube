namespace FluxTube;

public sealed class MenuItem(
    string text,
    Action? act = null,
    List<MenuItem>? sub = null,
    bool check = false,
    string? icon = null,
    string? shortcut = null,
    bool danger = false,
    bool header = false)
{
    public string Text = text;
    public Action? Act = act;
    public List<MenuItem>? Sub = sub;
    public bool Check = check;
    public string? Icon = icon;
    public string? Shortcut = shortcut;
    public bool Danger = danger;
    public bool Header = header;

    public static MenuItem Sep => new("-");
    public static MenuItem Title(string text) => new(text, header: true);
}

/// <summary>
/// Custom Direct2D context menu: themed, rounded, shadowed, with crisp icons, section headers,
/// shortcut hints, and nested submenus. Rendered above the page and cut out of the video window.
/// </summary>
public static class Menus
{
    private sealed class Panel
    {
        public required List<MenuItem> Items;
        public float X, Y, W, H, ContentH, Scroll;
        public int Hover = -1;
        public long OpenedAt;
        public Panel? Child;
        public int ChildIndex = -1;
    }

    private static Panel? _root;
    private static Panel? _pressedPanel;
    private static int _pressedIndex = -1;
    private static readonly List<R> _rects = new(4);

    public static bool Open => _root != null;
    public static IReadOnlyList<R> ActiveRects => _rects;
    /// <summary>Corner radius of every menu panel (also used for the cut-out in the video window).</summary>
    public const float Radius = 12;

    public static R Bounds
    {
        get
        {
            if (_rects.Count == 0) return default;
            float l = _rects[0].X, t = _rects[0].Y, r = _rects[0].Right, b = _rects[0].Bottom;
            for (int i = 1; i < _rects.Count; i++)
            {
                l = MathF.Min(l, _rects[i].X); t = MathF.Min(t, _rects[i].Y);
                r = MathF.Max(r, _rects[i].Right); b = MathF.Max(b, _rects[i].Bottom);
            }
            return new R(l, t, r - l, b - t);
        }
    }

    public static void Close()
    {
        if (_root == null) return;
        _root = null;
        _rects.Clear();
        App.Invalidate();
    }

    public static void Popup(List<MenuItem> items) => App.Post(() =>
    {
        if (items.Count == 0) return;
        AccountMenu.Close();
        Suggest.Hide();
        W.POINT pt;
        unsafe
        {
            W.GetCursorPos(&pt);
            W.ScreenToClient(App.Hwnd, &pt);
        }
        float mx = pt.X / Gfx.Scale, my = pt.Y / Gfx.Scale;
        if (mx < 0 || my < 0 || mx > Gfx.W || my > Gfx.H)
        {
            mx = UI.MX >= 0 ? UI.MX : Gfx.W / 2;
            my = UI.MY >= 0 ? UI.MY : Gfx.H / 2;
        }
        _root = BuildPanel(items, mx + 2, my + 2, null);
        UI.Down = false;
        App.Invalidate();
    });

    private const float RowH = 34, SepH = 9, HeaderH = 26, PadY = 6, PadX = 6, IconW = 26;

    private static Panel BuildPanel(List<MenuItem> items, float x, float y, Panel? parent)
    {
        // Built inside App.Post outside Gfx.Begin(): ensure text measurements are exact, never rough estimates
        Gfx.ResetShapeBudget();
        float maxW = 190;
        float h = PadY * 2;
        foreach (var it in items)
        {
            if (it.Text == "-") { h += SepH; continue; }
            if (it.Header)
            {
                var tr = Gfx.Text(it.Text, 11.5f, 600);
                maxW = MathF.Max(maxW, tr.W + 32);
                h += HeaderH;
                continue;
            }
            var run = Gfx.Text(it.Text, 13.5f, 400);
            float rw = PadX * 2 + 10 + IconW + 8 + run.W + 16;
            if (!string.IsNullOrEmpty(it.Shortcut)) rw += Gfx.Text(it.Shortcut, 11.5f, 400).W + 18;
            if (it.Sub != null) rw += 22;
            else if (it.Check && it.Icon != null) rw += 20;
            maxW = MathF.Max(maxW, rw);
            h += RowH;
        }
        float contentH = h;
        maxW = MathF.Min(maxW, MathF.Max(220, Gfx.W - 24));
        h = MathF.Min(h, MathF.Max(120, Gfx.H - 24));

        if (parent != null)
        {
            if (x + maxW > Gfx.W - 8) x = MathF.Max(8, parent.X - maxW + 4);
            if (y + h > Gfx.H - 8) y = MathF.Max(8, Gfx.H - h - 8);
        }
        else
        {
            if (x + maxW > Gfx.W - 8) x = MathF.Max(8, x - maxW - 4);
            if (y + h > Gfx.H - 8) y = MathF.Max(8, y - h - 4);
        }
        x = Math.Clamp(x, 8, MathF.Max(8, Gfx.W - maxW - 8));
        y = Math.Clamp(y, 8, MathF.Max(8, Gfx.H - h - 8));

        return new Panel { Items = items, X = Gfx.Snap(x), Y = Gfx.Snap(y), W = Gfx.Snap(maxW), H = Gfx.Snap(h), ContentH = contentH, OpenedAt = UI.Now };
    }

    public static bool Key(int vk)
    {
        if (_root == null) return false;
        var active = Deepest(_root);
        switch (vk)
        {
            case W.VK_ESCAPE:
                if (active != _root) CloseChildOf(_root, active);
                else Close();
                return true;
            case W.VK_DOWN:
                MoveSelection(active, 1);
                return true;
            case W.VK_UP:
                MoveSelection(active, -1);
                return true;
            case W.VK_RIGHT:
                if (active.Hover >= 0 && active.Hover < active.Items.Count && active.Items[active.Hover].Sub is { Count: > 0 } sub)
                {
                    OpenChild(active, active.Hover, sub);
                    if (active.Child != null) MoveSelection(active.Child, 1);
                }
                return true;
            case W.VK_LEFT:
                if (active != _root) CloseChildOf(_root, active);
                return true;
            case W.VK_RETURN:
                if (active.Hover >= 0 && active.Hover < active.Items.Count)
                {
                    var it = active.Items[active.Hover];
                    if (it.Sub is { Count: > 0 } sub2) OpenChild(active, active.Hover, sub2);
                    else if (it.Act != null) { var a = it.Act; Close(); App.Post(a); }
                }
                return true;
        }
        return false;
    }

    private static Panel Deepest(Panel p) => p.Child != null ? Deepest(p.Child) : p;

    private static void CloseChildOf(Panel cur, Panel target)
    {
        if (cur.Child == target) { cur.Child = null; cur.ChildIndex = -1; App.Invalidate(); return; }
        if (cur.Child != null) CloseChildOf(cur.Child, target);
    }

    private static void MoveSelection(Panel p, int dir)
    {
        int n = p.Items.Count;
        if (n == 0) return;
        int cur = p.Hover;
        for (int step = 0; step < n; step++)
        {
            cur = (cur + dir + n) % n;
            var it = p.Items[cur];
            if (it.Text != "-" && !it.Header && (it.Act != null || it.Sub != null))
            {
                p.Hover = cur;
                App.Invalidate();
                return;
            }
        }
    }

    private static void OpenChild(Panel p, int idx, List<MenuItem> sub)
    {
        if (p.ChildIndex == idx && p.Child != null) return;
        float ry = p.Y + PadY;
        for (int i = 0; i < idx; i++)
            ry += p.Items[i].Text == "-" ? SepH : p.Items[i].Header ? HeaderH : RowH;
        p.ChildIndex = idx;
        p.Child = BuildPanel(sub, p.X + p.W - 4, ry - PadY, p);
        App.Invalidate();
    }

    /// <summary>Draws the open menu tree at the very end of the frame.</summary>
    public static void Draw()
    {
        _rects.Clear();
        if (_root == null) return;

        DrawPanel(_root, 0);

        // Collect rects AFTER DrawPanel so newly opened submenus are immediately cut out of VideoHwnd on this frame!
        bool insideAny = false;
        for (var p = _root; p != null; p = p.Child)
        {
            var r = new R(p.X, p.Y, p.W, p.H);
            _rects.Add(r);
            if (r.Contains(UI.MX, UI.MY)) insideAny = true;
        }

        if ((UI.Pressed || UI.RightClicked) && !insideAny) Close();
    }

    private static bool AnyDescendantContains(Panel? p, float mx, float my)
    {
        for (; p != null; p = p.Child)
            if (new R(p.X, p.Y, p.W, p.H).Contains(mx, my)) return true;
        return false;
    }

    private static void DrawPanel(Panel p, int depth)
    {
        var t = Math.Clamp((UI.Now - p.OpenedAt) / 210f, 0f, 1f);
        if (t < 1f) UI.Animate();
        var inv = 1f - t;
        var ease = 1f - inv * inv * inv * inv;
        float dy = (1f - ease) * 10f;
        var r = new R(p.X, Gfx.Snap(p.Y + dy), p.W, p.H);

        // soft layered shadow + frosted matte glass surface + crisp border
        Gfx.Fill(new R(r.X + 2, r.Y + 6, r.W - 2, r.H + 2), Gfx.Fade(0xFF000000, 0.36f * ease), 12);
        Gfx.Fill(new R(r.X + 1, r.Y + 2, r.W, r.H), Gfx.Fade(0xFF000000, 0.22f * ease), 12);
        Gfx.FillFrosted(r, Gfx.Fade(Th.Menu, ease), 12, opacity: 0.94f, border: true);

        // Deepest-first hit ownership: if pointer is inside an overlapping child submenu, parent must NOT steal hover/click!
        var panelHovered = r.Contains(UI.MX, UI.MY) && !AnyDescendantContains(p.Child, UI.MX, UI.MY);
        float maxScroll = MathF.Max(0, p.ContentH - p.H);
        if (panelHovered && maxScroll > 0 && UI.Wheel != 0)
        {
            p.Scroll = Math.Clamp(p.Scroll - UI.Wheel * 48f, 0, maxScroll);
            UI.Wheel = 0;
        }
        p.Scroll = Math.Clamp(p.Scroll, 0, maxScroll);

        Gfx.PushClip(r);
        float y = r.Y + PadY - p.Scroll;

        for (int i = 0; i < p.Items.Count; i++)
        {
            var it = p.Items[i];
            if (it.Text == "-")
            {
                if (y + SepH >= r.Y && y <= r.Bottom)
                    Gfx.Fill(new R(r.X + 12, Gfx.Snap(y + SepH / 2), r.W - 24, 1), Gfx.Fade(Th.Line, ease));
                y += SepH;
                continue;
            }
            if (it.Header)
            {
                if (y + HeaderH >= r.Y && y <= r.Bottom)
                    Gfx.Draw(it.Text, r.X + 14, Gfx.Snap(y + 6), 11.5f, Gfx.Fade(Th.Dim, ease), 600, r.W - 28);
                y += HeaderH;
                continue;
            }

            if (y + RowH < r.Y || y > r.Bottom) { y += RowH; continue; }
            var row = new R(r.X + PadX, y, r.W - PadX * 2, RowH);
            bool interactive = it.Act != null || it.Sub != null;
            bool mouseOver = panelHovered && row.Contains(UI.MX, UI.MY);
            if (mouseOver && interactive)
            {
                p.Hover = i;
                UI.Hand();
                if (UI.Pressed) { _pressedPanel = p; _pressedIndex = i; }
                if (it.Sub is { Count: > 0 } sub) OpenChild(p, i, sub);
                else if (p.Child != null && p.ChildIndex != i) { p.Child = null; p.ChildIndex = -1; }
            }
            bool hot = interactive && (p.Hover == i || p.ChildIndex == i);

            uint dangerCol = 0xFFFF5C6C;
            if (hot)
            {
                uint bg = it.Danger ? Gfx.Fade(dangerCol, 0.16f * ease) : Gfx.Fade(Th.A(0.11f), ease);
                Gfx.Fill(row, bg, 7);
            }

            uint fg = !interactive ? Th.Dim
                : it.Danger ? (hot ? 0xFFFF7A87 : dangerCol)
                : it.Check ? Th.Brand
                : Th.Text;
            uint iconCol = !interactive ? Th.Dim
                : it.Danger ? dangerCol
                : it.Check ? Th.Brand
                : (hot ? Th.Text : Th.Icon);

            float ix = row.X + 8;
            var iconBox = new R(ix, row.Y, IconW, row.H);
            if (!string.IsNullOrEmpty(it.Icon))
                Gfx.Icon(it.Icon, iconBox, 14, Gfx.Fade(iconCol, ease));
            else if (it.Check)
                Gfx.Icon(Th.Check, iconBox, 12.5f, Gfx.Fade(Th.Brand, ease));

            float rightPad = 12;
            if (it.Sub != null)
            {
                Gfx.Icon(Th.ChevronRight, new R(row.Right - 22, row.Y, 16, row.H), 10, Gfx.Fade(Th.Sub, ease));
                rightPad = 26;
            }
            else if (it.Check && !string.IsNullOrEmpty(it.Icon))
            {
                Gfx.Icon(Th.Check, new R(row.Right - 24, row.Y, 18, row.H), 11.5f, Gfx.Fade(Th.Brand, ease));
                rightPad = 28;
            }
            else if (!string.IsNullOrEmpty(it.Shortcut))
            {
                var sr = Gfx.Text(it.Shortcut, 11.5f, 400);
                Gfx.Draw(sr, row.Right - sr.W - 10, Gfx.Snap(row.Y + (row.H - sr.H) / 2), Gfx.Fade(Th.Dim, ease));
                rightPad = sr.W + 22;
            }

            float tx = ix + IconW + 6;
            float maxTextW = MathF.Max(20, row.Right - rightPad - tx);
            var tr = Gfx.Text(it.Text, 13.5f, it.Check ? 600 : 400, maxTextW);
            Gfx.Draw(tr, tx, Gfx.Snap(row.Y + (row.H - tr.H) / 2), Gfx.Fade(fg, ease));

            if (mouseOver && interactive && UI.Released && _pressedPanel == p && _pressedIndex == i)
            {
                UI.Released = false;
                UI.Pressed = false;
                _pressedPanel = null; _pressedIndex = -1;
                Sfx.Click();
                if (it.Sub is { Count: > 0 } sub) OpenChild(p, i, sub);
                else if (it.Act != null) { var a = it.Act; Close(); App.Post(a); }
            }

            y += RowH;
        }
        if (maxScroll > 0)
        {
            float barH = MathF.Max(24, r.H * r.H / p.ContentH);
            float barY = r.Y + 4 + (r.H - 8 - barH) * (p.Scroll / maxScroll);
            Gfx.Fill(new R(r.Right - 5, barY, 3, barH), Gfx.Fade(Th.A(0.32f), ease), 1.5f);
        }
        Gfx.PopClip();

        if (p.Child != null) DrawPanel(p.Child, depth + 1);
    }
}
