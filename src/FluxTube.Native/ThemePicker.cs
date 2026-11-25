namespace FluxTube;

/// <summary>
/// Theme cards with a live miniature of the window in each palette, and the accent swatches.
/// Clicking a card switches the whole app at once (the colours ease over).
/// </summary>
public static class ThemePicker
{
    private const float CardW = 176, CardH = 104, Gap = 14;

    /// <summary>Draws the picker at (x, y) within width w; returns the height used.</summary>
    public static float Draw(float x, float y, float w)
    {
        var y0 = y;
        var clip = Gfx.Clip;
        var cols = Math.Max(1, (int)((w + Gap) / (CardW + Gap)));
        foreach (var (groupName, items) in Themes.Groups)
        {
            if (y + 24 >= clip.Y && y <= clip.Bottom)
                Gfx.Draw(groupName, x, y, 12.5f, Th.Sub, 600);
            y += 24;
            int rows = (items.Length + cols - 1) / cols;
            float rowStride = CardH + 34 + Gap;
            for (int i = 0; i < items.Length; i++)
            {
                float cx = x + i % cols * (CardW + Gap), cy = y + i / cols * rowStride;
                if (cy + CardH + 34 < clip.Y || cy > clip.Bottom) continue;
                Card(items[i], new R(cx, cy, CardW, CardH));
            }
            y += rows * rowStride + 6;
        }

        // accents: the theme's own, then the fixed set
        if (y + 26 >= clip.Y && y <= clip.Bottom)
            Gfx.Draw("Акцентный цвет", x, y, 12.5f, Th.Sub, 600);
        y += 26;
        var cur = Themes.AccentOverride;
        float sx = x;
        string? tipName = null;
        float tipX = 0, tipY = 0;

        if (Swatch(UI.Id("acc:own"), sx, y, Themes.Current.Brand, cur == null, true, () => Themes.Apply(accent: "")))
        {
            tipName = "Как в теме"; tipX = sx + 17; tipY = y + 40;
        }
        sx += 46;
        for (int i = 0; i < Themes.Accents.Length; i++)
        {
            var (name, color) = Themes.Accents[i];
            if (sx > x + w - 34) { sx = x; y += 44; }
            var c = color;
            if (Swatch(UI.Id("acc:" + name), sx, y, c, cur == c, false, () => Themes.Apply(accent: Themes.Hex(c))))
            {
                tipName = name; tipX = sx + 17; tipY = y + 40;
            }
            sx += 40;
        }
        if (tipName != null) Tip(tipName, tipX, tipY, x, x + w);
        y += 48;
        return y - y0;
    }

    private static bool Swatch(int id, float x, float y, uint color, bool on, bool isOwn, Action pick)
    {
        var r = new R(x, y, 34, 34);
        var over = UI.Hover(r);
        var ring = UI.Anim(id ^ 0x2121, on ? 1 : over ? 0.5f : 0, 20);
        if (ring > 0.01f) Gfx.Circle(x + 17, y + 17, 17, Gfx.Fade(Th.Text, ring));
        Gfx.Circle(x + 17, y + 17, 17 - 3 * ring, Th.Bg);
        Gfx.Circle(x + 17, y + 17, 12.5f, color);
        if (isOwn) Gfx.Icon(Th.Refresh, new R(x, y, 34, 34), 11, 0xFFFFFFFF);
        if (UI.Click(id, r)) App.Post(pick);
        return over;
    }

    private static void Tip(string text, float cx, float y, float minX, float maxX)
    {
        var run = Gfx.Text(text, 12, 500);
        float bw = run.W + 16;
        float bx = Math.Clamp(cx - bw / 2, minX, MathF.Max(minX, maxX - bw));
        var b = new R(bx, y, bw, run.H + 8);
        Gfx.Fill(b, Th.Menu, 6);
        Gfx.Stroke(b, Th.Line, 6);
        Gfx.Draw(run, b.X + 8, b.Y + 4, Th.Text);
    }

    /// <summary>A miniature of the window in theme t: top bar, side bar, a few video cards.</summary>
    private static void Card(Theme t, R r)
    {
        var id = UI.Id("theme:" + t.Name);
        var on = Themes.Current.Name == t.Name;
        var over = UI.Hover(new R(r.X, r.Y, r.W, r.H + 30));
        var lift = UI.Anim(id ^ 0x4141, over ? 1 : 0, 22);
        var sel = UI.Anim(id ^ 0x4242, on ? 1 : 0, 18);
        r = new R(r.X, r.Y - 3 * lift, r.W, r.H);
        var p = Themes.Preview(t);

        if (sel > 0.01f) Gfx.Fill(r.Inset(-3), Gfx.Fade(p.Brand, sel), 13);
        Gfx.Fill(r, p.Bg, 11);
        Gfx.PushClip(r);
        // top bar: logo, search box, account dot
        Gfx.Fill(new R(r.X + 8, r.Y + 7, 12, 8), p.Brand, 2.5f);
        Gfx.Fill(new R(r.X + 24, r.Y + 8, 20, 5), p.Text, 2);
        Gfx.Fill(new R(r.X + r.W / 2 - 30, r.Y + 6, 60, 10), p.Card, 5);
        Gfx.Circle(r.Right - 12, r.Y + 11, 4.5f, p.Sub);
        // side bar
        for (int i = 0; i < 4; i++) Gfx.Fill(new R(r.X + 8, r.Y + 26 + i * 15, 9, 9), i == 0 ? p.Text : p.Sub, 2.5f);
        // two rows of cards
        float gx = r.X + 26, gw = (r.W - 26 - 8 - 2 * 6) / 3;
        for (int row = 0; row < 2; row++)
            for (int c = 0; c < 3; c++)
            {
                float cx = gx + c * (gw + 6), cy = r.Y + 24 + row * 40;
                Gfx.Fill(new R(cx, cy, gw, gw * 9 / 16), p.Card, 3);
                if (row == 0 && c == 1) Gfx.Fill(new R(cx + 2, cy + gw * 9 / 16 - 2.5f, gw * 0.6f, 2), p.Brand, 1);
                Gfx.Fill(new R(cx, cy + gw * 9 / 16 + 3, gw * 0.9f, 3), p.Text, 1.5f);
                Gfx.Fill(new R(cx, cy + gw * 9 / 16 + 8, gw * 0.55f, 2.5f), p.Sub, 1.2f);
            }
        // a button in the accent colour
        var bw = 34f;
        Gfx.Fill(new R(r.Right - bw - 8, r.Bottom - 15, bw, 9), p.Brand, 4.5f);
        Gfx.Fill(new R(r.Right - bw - 2, r.Bottom - 12, bw - 12, 3), p.OnBrand, 1.5f);
        Gfx.PopClip();
        Gfx.Stroke(r, on ? 0 : Gfx.Mix(p.Line, Th.Line, 0.5f), 11);

        Gfx.Draw(t.Name, r.X + 2, r.Bottom + 8, 13, on ? Th.Text : Th.Sub, on ? 600 : 400, r.W - 20);
        if (on) Gfx.Icon(Th.Check, new R(r.Right - 16, r.Bottom + 7, 16, 18), 11, Th.Brand);
        if (UI.Click(id, new R(r.X, r.Y, r.W, r.H + 30)) && !on) App.Post(() => Themes.Apply(theme: t.Name));
    }
}
