using FluxTube.Pages;

namespace FluxTube;

/// <summary>
/// Theme cards with a live miniature of the window in each palette, accent swatches,
/// and right-click edit/delete for custom code themes.
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

    /// <summary>Draws a live miniature preview of any theme (shared between picker cards and live Code Editor).</summary>
    public static void DrawMiniature(Theme t, R r, bool useAccentOverride = true)
    {
        var p = Themes.Preview(t, useAccentOverride);
        Gfx.Fill(r, p.Bg, 11);
        Gfx.PushClip(r);
        Gfx.Fill(new R(r.X + 8, r.Y + 7, 12, 8), p.Brand, 2.5f);
        Gfx.Fill(new R(r.X + 24, r.Y + 8, 20, 5), p.Text, 2);
        Gfx.Fill(new R(r.X + r.W / 2 - 30, r.Y + 6, 60, 10), p.Card, 5);
        Gfx.Circle(r.Right - 12, r.Y + 11, 4.5f, p.Sub);
        for (int i = 0; i < 4; i++) Gfx.Fill(new R(r.X + 8, r.Y + 26 + i * 15, 9, 9), i == 0 ? p.Text : p.Sub, 2.5f);
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
        var bw = 34f;
        Gfx.Fill(new R(r.Right - bw - 8, r.Bottom - 15, bw, 9), p.Brand, 4.5f);
        Gfx.Fill(new R(r.Right - bw - 2, r.Bottom - 12, bw - 12, 3), p.OnBrand, 1.5f);
        Gfx.PopClip();
    }

    private static void Card(Theme t, R r)
    {
        var id = UI.Id("theme:" + t.Name);
        var on = Themes.Current.Name == t.Name;
        var hit = new R(r.X, r.Y, r.W, r.H + 30);
        var over = UI.Hover(hit);
        var lift = UI.Anim(id ^ 0x4141, over ? 1 : 0, 22);
        var sel = UI.Anim(id ^ 0x4242, on ? 1 : 0, 18);
        r = new R(r.X, r.Y - 3 * lift, r.W, r.H);
        var p = Themes.Preview(t);

        if (sel > 0.01f) Gfx.Fill(r.Inset(-3), Gfx.Fade(p.Brand, sel), 13);
        DrawMiniature(t, r);
        Gfx.Stroke(r, on ? 0 : Gfx.Mix(p.Line, Th.Line, 0.5f), 11);

        Gfx.Draw(t.Name, r.X + 2, r.Bottom + 8, 13, on ? Th.Text : Th.Sub, on ? 600 : 400, r.W - 20);
        if (on) Gfx.Icon(Th.Check, new R(r.Right - 16, r.Bottom + 7, 16, 18), 11, Th.Brand);

        if (UI.RightClick(hit))
        {
            var items = new List<MenuItem>
            {
                new("Открыть код этой темы…", () => ThemeCodeEditor.Show(t), icon: Th.Settings),
                new("Копировать код темы", () => Cards.CopyText(Themes.ExportCode(t), "Код темы скопирован"), icon: Th.Copy),
            };
            if (Themes.IsCustom(t.Name))
            {
                items.Add(MenuItem.Sep);
                items.Add(new("Удалить свою тему", () =>
                {
                    if (Themes.DeleteCustom(t.Name)) Shell.Toast($"Тема «{t.Name}» удалена", Th.Good, 2200);
                }, icon: Th.Delete, danger: true));
            }
            Menus.Popup(items);
        }
        else if (UI.Click(id, hit) && !on) App.Post(() => Themes.Apply(theme: t.Name));
    }
}

/// <summary>
/// Built-in Theme Code Studio modal: write custom themes in JSON or simple "key: #RRGGBB" lines
/// with real-time live miniature preview, colour swatches, clipboard import/export, and 1-click save.
/// </summary>
public static class ThemeCodeEditor
{
    public static bool Open { get; private set; }
    public static R Rect { get; private set; }

    private static Edit? _edit;
    private static string? _editingCustomName;
    private static Theme? _liveTheme;
    private static string? _liveErr;
    private static string _lastParsed = "";
    private static long _openedAt;

    public static void Show(Theme? basis = null)
    {
        basis ??= Themes.Current;
        var isCustom = Themes.IsCustom(basis.Name);
        _editingCustomName = isCustom ? basis.Name : null;
        var code = Themes.ExportCode(basis, isCustom ? basis.Name : basis.Name + " Custom").Replace("\n", "\r\n");

        if (_edit == null)
        {
            _edit = new Edit("", multiline: true) { Bg = () => Th.Field };
            _edit.Changed = () => { Reparse(); App.Invalidate(); };
            _edit.Escape = Close;
            _edit.Enter = SaveAndApply; // Ctrl+Enter saves immediately
        }
        _edit.Text = code;
        _lastParsed = "";
        Reparse();
        Open = true;
        _openedAt = UI.Now;
        AccountMenu.Close();
        Menus.Close();
        PreviewPlayer.HideNow();
        App.Invalidate();
        App.Post(() => _edit?.Focus(selectAll: false));
    }

    public static void Close()
    {
        if (!Open) return;
        Open = false;
        Rect = default;
        _edit?.Hide();
        Sfx.Back();
        App.Invalidate();
    }

    private static void Reparse()
    {
        if (_edit == null) return;
        var text = _edit.Text;
        if (text == _lastParsed) return;
        _lastParsed = text;
        Themes.TryParseCode(text, out _liveTheme, out _liveErr);
    }

    private static void SaveAndApply()
    {
        if (_edit == null) return;
        if (Themes.SaveCustomFromCode(_edit.Text, out var name, out var err))
        {
            Sfx.Like();
            Shell.Toast($"Тема «{name}» сохранена и применена!", Th.Good, 3000);
            Close();
        }
        else
        {
            _liveErr = err ?? "Проверьте формат кода темы";
            Shell.Toast(_liveErr, Th.Warn, 3500);
            App.Invalidate();
        }
    }

    public static void Draw()
    {
        if (!Open) return;
        Reparse();

        float t = Math.Clamp((UI.Now - _openedAt) / 140f, 0, 1);
        if (t < 1) UI.Animate();
        float ease = 1f - (1f - t) * (1f - t);

        // Backdrop scrim
        Gfx.Fill(new R(0, 0, Gfx.W, Gfx.H), Gfx.Fade(0xFF000000, 0.58f * ease));

        float mw = MathF.Min(740, Gfx.W - 60);
        float mh = MathF.Min(540, Gfx.H - 60);
        var card = new R(Gfx.Snap((Gfx.W - mw) / 2), Gfx.Snap((Gfx.H - mh) / 2 + (1f - ease) * 14), mw, mh);
        Rect = card;

        Gfx.Fill(card.Inset(-3), Gfx.Fade(0xFF000000, 0.35f * ease), 18);
        Gfx.FillFrosted(card, Th.Menu, 16, opacity: 0.96f, border: true);

        float pad = 20, x = card.X + pad, y = card.Y + 16, innerW = card.W - pad * 2;
        Gfx.Draw("Редактор темы (код)", x, y, 18, Th.Text, 600);
        Gfx.Draw("JSON или строки вида bg: #0A0D16  ·  Ctrl+Enter — сохранить", x, y + 24, 12, Th.Sub);
        if (UI.IconButton(UI.Id("tce:close"), new R(card.Right - 46, y, 32, 32), Th.Close, 12, Th.Sub)) { Close(); return; }
        y += 50;

        // Top row: Live preview miniature + palette swatches
        float prevW = 200, prevH = 114;
        var prevR = new R(x, y, prevW, prevH);
        if (_liveTheme != null)
        {
            ThemePicker.DrawMiniature(_liveTheme, prevR, useAccentOverride: false);
            Gfx.Stroke(prevR, Th.A(0.22f), 11, 1f);

            float sx = prevR.Right + 16, sw = card.Right - pad - sx;
            Gfx.Draw(_liveTheme.Name, sx, y + 2, 15, Th.Text, 600, sw);
            Gfx.Draw(_liveTheme.Light ? "Светлая тема · Превью обновляется на лету" : "Тёмная тема · Превью обновляется на лету", sx, y + 23, 12, Th.Good, 400, sw);

            (string Lbl, uint Col)[] swatches =
            [
                ("bg", _liveTheme.Bg), ("panel", _liveTheme.Panel), ("card", _liveTheme.Card),
                ("text", _liveTheme.Text), ("sub", _liveTheme.Sub), ("brand", _liveTheme.Brand),
            ];
            float swX = sx, swY = y + 48;
            for (int i = 0; i < swatches.Length; i++)
            {
                if (swX + 74 > card.Right - pad) { swX = sx; swY += 32; }
                Gfx.Circle(swX + 9, swY + 11, 9, swatches[i].Col);
                Gfx.Stroke(new R(swX, swY + 2, 18, 18), Th.A(0.32f), 9);
                Gfx.Draw(swatches[i].Lbl, swX + 23, swY + 4, 11.5f, Th.Sub, 500);
                swX += 76;
            }
        }
        else
        {
            Gfx.Fill(prevR, Th.Card, 11);
            Gfx.Stroke(prevR, Gfx.Fade(Th.Warn, 0.7f), 11);
            Gfx.Draw("Ошибка кода", prevR.X + 14, prevR.Y + 42, 13.5f, Th.Warn, 600);
            Gfx.Draw(_liveErr ?? "Проверьте синтаксис", prevR.Right + 16, y + 20, 13, Th.Warn, 500, card.Right - pad - (prevR.Right + 16), 4);
        }

        y += prevH + 14;

        // Multiline monospace code editor box
        float footerH = 54;
        var editBox = new R(x, y, innerW, MathF.Max(120, card.Bottom - footerH - y - 10));
        Gfx.Fill(editBox, Th.Field, 10);
        Gfx.Stroke(editBox, _edit?.Focused == true ? Gfx.Fade(Th.Brand, 0.85f) : Th.Line, 10, 1.2f);
        _edit?.Place(editBox, 12, 12, allowInModal: true);

        // Footer buttons
        float by = card.Bottom - 44;
        float lx = x;
        if (UI.Pill(UI.Id("tce:paste"), lx, by, 34, Th.Link, "Вставить", out var pw1, Th.A(0.08f)))
        {
            var clip = W.GetClipboardText(App.Hwnd);
            if (!string.IsNullOrWhiteSpace(clip) && _edit != null)
            {
                _edit.Text = clip.Replace("\r\n", "\n").Replace("\n", "\r\n");
                Reparse();
                Shell.Toast("Код вставлен из буфера", Th.Good, 1600);
            }
        }
        lx += pw1 + 8;
        if (UI.Pill(UI.Id("tce:copy"), lx, by, 34, Th.Copy, "Копировать", out var pw2, Th.A(0.08f)))
            if (_edit != null) Cards.CopyText(_edit.Text, "Код темы скопирован");
        lx += pw2 + 8;
        if (UI.Pill(UI.Id("tce:dir"), lx, by, 34, Th.Folder, "Папка тем", out var pw3, Th.A(0.08f)))
            W.Open(Themes.CustomDir);
        lx += pw3 + 8;
        if (_editingCustomName != null && UI.Pill(UI.Id("tce:del"), lx, by, 34, Th.Delete, "Удалить", out _, Gfx.Fade(0xFFE05252, 0.2f), 0xFFFF7A7A))
        {
            var delName = _editingCustomName;
            Close();
            if (Themes.DeleteCustom(delName)) Shell.Toast($"Тема «{delName}» удалена", Th.Good, 2200);
            return;
        }

        var saveW = UI.PillWidth(Th.Check, "Сохранить и применить");
        if (UI.Pill(UI.Id("tce:save"), card.Right - pad - saveW, by, 34, Th.Check, "Сохранить и применить", out _, accent: true))
            SaveAndApply();

        // Click outside modal closes it
        if (UI.Pressed && !card.Contains(UI.MX, UI.MY)) Close();
    }
}
