namespace FluxTube;

/// <summary>
/// Palette and glyphs in one place. The colours belong to the current theme (Themes.cs sets them, easing from the old
/// theme to the new one), so read them every frame instead of keeping copies.
/// </summary>
public static class Th
{
    public static uint Bg = 0xFF0E0E11, Panel = 0xFF151519, Card = 0xFF1C1C22, Hover = 0x14FFFFFF, Press = 0x22FFFFFF,
        Text = 0xFFF2F2F4, Sub = 0xFFA6A6B2, Dim = 0xFF6C6C78, Brand = 0xFFFF3355, Brand2 = 0xFF7B61FF, Line = 0x18FFFFFF, Badge = 0xD9000000,
        Thumb = 0xFF1E1E25, Good = 0xFF7BD88F, Warn = 0xFFFFC857,
        Field = 0xFF1A1A20, Menu = 0xFF232329, OnBrand = 0xFFFFFFFF, Ink = 0xFFFFFFFF, Icon = 0xFFD8D8DE, Track = 0xFF3A3A44;
    public static bool Light;

    /// <summary>Translucent ink at this opacity: white on dark themes, black on light ones (hover, lines, tracks).</summary>
    public static uint A(float a) => ((uint)Math.Clamp(a * 255 + 0.5f, 0, 255) << 24) | (Ink & 0xFFFFFF);

    public const string Back = "", Home = "", Subs = "", Later = "", Like = "", Dislike = "", History = "",
        Eye = "", Download = "", TikTok = "", Settings = "", Refresh = "", Search = "", Account = "",
        Play = "", Pause = "", Close = "", More = "", Theater = "", Folder = "", Delete = "",
        Stop = "", Expand = "", Mini = "", Rewind = "", Forward = "", Chevron = "", Check = "", Link = "",
        Share = "\uE72D", Tab = "\uE8B7",
        Hamburger = "\uE700", Add = "\uE710", Volume = "\uE767", Mute = "\uE74F", Copy = "\uE8C8", Globe = "\uE774",
        Block = "\uF140", Comment = "\uE8BD", Speed = "\uEC4A", Quality = "\uE7F4", Subtitle = "\uED1E",
        Playlist = "\uE90B", Repeat = "\uE8EE", Headphones = "\uE7F6", Pin = "\uE718", Info = "\uE946",
        ChevronRight = "\uE76C", ChevronUp = "\uE70E", ChevronDown = "\uE70D", OpenNew = "\uE8A7", Restart = "\uE72C";
}

/// <summary>
/// Immediate-mode UI: every frame the page code lays out, draws and hit-tests in one pass.
/// There is no widget tree to keep in sync and nothing to lay out when nothing changed.
/// Motion is a table of numbers easing towards their targets; a frame is drawn only while one of them still moves.
/// </summary>
public static class UI
{
    public static float MX = -1, MY = -1;
    public static bool Down, Pressed, Released, RightClicked, MiddleClicked;
    public static bool MiddleDown;
    public static float MiddleDownX, MiddleDownY;
    public static long MiddleDownAt;
    public static float Wheel;
    public static bool BlockInput;           // a native child (the video) owns the mouse this frame
    private static int _active;
    private static bool _animate;
    private static bool _middleOverLink;
    private static int _cursorHand;
    public static readonly long StartTick = Environment.TickCount64;
    public static double Dt = 1 / 60.0;
    public static long Now = Environment.TickCount64;
    /// <summary>Last time the user did anything (for work that should only happen while idle).</summary>
    public static long LastInput = Environment.TickCount64;

    public static void Animate() => _animate = true;
    public static bool TakeAnimate() { var a = _animate; _animate = false; return a; }
    public static bool WantHand => _cursorHand > 0;

    public static int Id(string s, int i = 0) => HashCode.Combine(s, i);
    public static int Id(int a, int b) => HashCode.Combine(a, b);

    public static void BeginFrame() { _cursorHand = 0; _middleOverLink = false; Sel.BeginFrame(); }

    /// <summary>The pointer is over something clickable that is not a plain Click rectangle.</summary>
    public static void Hand() => _cursorHand++;

    public static void EndFrame()
    {
        if (Released) _active = 0;
        Pressed = Released = RightClicked = MiddleClicked = false;
        Wheel = 0;
    }

    /// <summary>Clears all held/active input state (e.g. after returning from a native Windows dialog or modal window).</summary>
    public static void ResetInput()
    {
        Down = Pressed = Released = RightClicked = MiddleDown = MiddleClicked = false;
        BlockInput = false;
        Wheel = 0;
        _active = 0;
        _middleOverLink = false;
        _dragScroll = 0;
        Modal = null;
        StopAutoScroll();
    }

    /// <summary>While set, only what is inside this rectangle can be hovered or clicked (an open panel).</summary>
    public static R? Modal;

    public static bool Hover(R r)
        => !BlockInput && r.Contains(MX, MY) && Gfx.Clip.Contains(MX, MY) && (Modal is not { } m || m.Contains(MX, MY));

    /// <summary>Press-inside then release-inside, like a real button.</summary>
    public static bool Click(int id, R r, bool hand = true, bool playSfx = true)
    {
        var over = Hover(r);
        if (over && hand) _cursorHand++;
        if (over && Pressed) _active = id;
        var hit = Released && _active == id && over;
        if (hit && playSfx) Sfx.Click();
        return hit;
    }

    public static bool IsActive(int id) => _active == id && Down;

    public static bool RightClick(R r) => RightClicked && Hover(r);

    public static bool MiddleClick(R r)
    {
        if ((MiddleDown || MiddleClicked) && !BlockInput && r.Contains(MiddleDownX, MiddleDownY)
            && Gfx.Clip.Contains(MiddleDownX, MiddleDownY) && (Modal is not { } m || m.Contains(MiddleDownX, MiddleDownY)))
        {
            _middleOverLink = true;
        }
        if (!MiddleClicked || !Hover(r) || !r.Contains(MiddleDownX, MiddleDownY)) return false;
        MiddleClicked = false;
        return true;
    }

    // ---------------- motion ----------------

    private static readonly Dictionary<int, float> _anims = new();

    /// <summary>
    /// A value easing towards target (frame-rate independent, no overshoot). Something that rests at 0 costs nothing:
    /// its entry is removed when it gets there, so a list of a thousand cards animates only the one under the pointer.
    /// </summary>
    public static float Anim(int id, float target, float rate = 20f)
    {
        if (!_anims.TryGetValue(id, out var v))
        {
            if (target == 0) return 0;
            v = 0;
        }
        return Step(id, v, target, rate);
    }

    /// <summary>Like Anim, but a value seen for the first time starts at its target (a switch that is already on does not slide in).</summary>
    public static float Smooth(int id, float target, float rate = 20f)
    {
        if (_anims.TryGetValue(id, out var v)) return Step(id, v, target, rate);
        if (target == 0) return 0;
        return _anims[id] = target;
    }

    public static void PruneCaches()
    {
        if (_scrolls.Count > 48)
        {
            var stale = _scrolls.Where(kv => kv.Key != _autoScrollId && kv.Key != _dragScroll && Now - kv.Value.LastTouch > 300_000).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _scrolls.Remove(k);
        }
        if (_anims.Count > 512) _anims.Clear();
    }

    private static float Step(int id, float v, float target, float rate)
    {
        var d = target - v;
        if (MathF.Abs(d) < 0.004f * MathF.Max(1, MathF.Abs(target)))
        {
            if (target == 0) _anims.Remove(id); else _anims[id] = target;
            return target;
        }
        v += d * (1 - MathF.Exp(-(float)Dt * rate));
        _anims[id] = v;
        Animate();
        return v;
    }

    /// <summary>Hover and press highlight that fades in and out. Returns the highlight amount (0..1, more when pressed).</summary>
    public static float Glow(int id, R r, float radius, bool over)
    {
        var a = Anim(id ^ 0x5A5A, over ? (IsActive(id) ? 1.7f : 1f) : 0, 24);
        if (a > 0.01f) Gfx.Fill(r, Th.A(0.078f * a), radius);
        return a;
    }

    /// <summary>What a Glow of strength a looks like on the page background: the colour a thumbnail's corner mask must use.</summary>
    public static uint GlowOn(uint bg, float a) => Gfx.Mix(bg, Th.Ink, 0.078f * a);

    // ---------------- widgets ----------------

    public static bool IconButton(int id, R r, string glyph, float size = 16, uint? color = null, bool enabled = true)
    {
        if (!enabled) { Gfx.Icon(glyph, r, size, Th.Dim); return false; }
        Glow(id, r, MathF.Min(r.H, r.W) / 2 > 18 ? 8 : 6, Hover(r));
        Gfx.Icon(glyph, r, size, color ?? Th.Text);
        return Click(id, r);
    }

    /// <summary>Pill button; returns its width through w so callers can flow a row. bg 0 = the theme's default pill.</summary>
    public static bool Pill(int id, float x, float y, float h, string? glyph, string? text, out float w, uint bg = 0, uint? fg = null, bool accent = false)
    {
        var run = string.IsNullOrEmpty(text) ? null : Gfx.Text(text, 13.5f, 600);
        w = 14 + (glyph != null ? 18 : 0) + (glyph != null && run != null ? 6 : 0) + (run?.W ?? 0) + 14;
        var r = new R(x, y, w, h);
        var f = fg ?? (accent ? Th.OnBrand : Th.Text);
        Gfx.Fill(r, accent ? Th.Brand : bg != 0 ? bg : Th.A(0.12f), h / 2);
        if (accent && Services.AppSettings.Current.UiNeonGlow) Gfx.NeonGlow(r, h / 2, Th.Brand, 0.72f);
        Glow(id, r, h / 2, Hover(r));
        var cx = x + 14;
        if (glyph != null) { Gfx.Icon(glyph, new R(cx, y, 18, h), 15, f); cx += 18 + 6; }
        if (run != null) Gfx.Draw(run, cx, Gfx.Snap(y + (h - run.H) / 2), f);
        return Click(id, r);
    }

    public static float PillWidth(string? glyph, string? text)
    {
        var run = string.IsNullOrEmpty(text) ? null : Gfx.Text(text, 13.5f, 600);
        return 14 + (glyph != null ? 18 : 0) + (glyph != null && run != null ? 6 : 0) + (run?.W ?? 0) + 14;
    }

    /// <summary>Square checkbox with a label, the whole row is clickable.</summary>
    public static bool Check(int id, R r, bool value, string label, bool enabled = true)
    {
        var over = enabled && Hover(r);
        var t = Smooth(id ^ 0x6161, value ? 1 : 0, 24);
        var box = new R(r.X, Gfx.Snap(r.Y + (r.H - 19) / 2), 19, 19);
        if (enabled) Glow(id, new R(r.X - 6, r.Y, r.W + 12, r.H), 8, over);
        Gfx.Fill(box, Gfx.Mix(Th.Brand & 0xFFFFFF, Th.Brand, t), 5);
        if (t < 0.99f) Gfx.Stroke(box, Gfx.Fade(Th.A(enabled ? 0.5f : 0.25f), 1 - t), 5, 1.4f);
        if (t > 0.01f) Gfx.Icon(Th.Check, box, 12, Gfx.Fade(Th.OnBrand, t));
        var run = Gfx.Text(label, 13.5f, 400, r.W - 30);
        Gfx.Draw(run, box.Right + 10, Gfx.Snap(r.Y + (r.H - run.H) / 2), enabled ? Th.Text : Th.Dim);
        return enabled && Click(id, r);
    }

    public static bool Toggle(int id, R r, bool value)
    {
        var t = Smooth(id ^ 0x7070, value ? 1 : 0, 22);
        var track = new R(r.Right - 40, r.Y + (r.H - 20) / 2, 40, 20);
        Gfx.Fill(track, Gfx.Mix(Th.Track, Th.Brand, t), 10);
        Gfx.Circle(track.X + 10 + 20 * t, track.Y + 10, 7 + (IsActive(id) ? 1 : 0), 0xFFFFFFFF);
        var over = Hover(r);
        if (over) _cursorHand++;
        if (over && Pressed) _active = id;
        if (Released && _active == id && over) { Sfx.Toggle(!value); return true; }
        return false;
    }

    // ---------------- scrolling ----------------

    private sealed class Scroll { public float Pos, Target, Max, VelY; public long LastTouch; public float DragOffset; }
    private static readonly Dictionary<int, Scroll> _scrolls = new();
    private static readonly bool[] _mbActiveStack = new bool[8];
    private static int _dragScroll;

    // Browser-style middle-click autoscroll (click MMB on empty area, or hold MMB and move up/down)
    private static int _autoScrollId;
    private static float _autoScrollX, _autoScrollY;
    public static bool AutoScrollActive => _autoScrollId != 0;
    public static bool AutoScrollHold { get; private set; }
    public static void StopAutoScroll() { _autoScrollId = 0; AutoScrollHold = false; }

    public static void DrawAutoScrollIndicator()
    {
        if (_autoScrollId == 0) return;
        const float rad = 14;
        var c = new R(_autoScrollX - rad, _autoScrollY - rad, rad * 2, rad * 2);
        Gfx.Fill(new R(c.X + 1, c.Y + 2, c.W, c.H), Gfx.Fade(0xFF000000, 0.35f), rad);
        Gfx.Fill(c, Gfx.Fade(Th.Menu, 0.94f), rad);
        Gfx.Stroke(c, Th.A(0.35f), rad, 1.2f);
        Gfx.Circle(_autoScrollX, _autoScrollY, 2.5f, Th.Brand);
        Gfx.Icon(Th.ChevronUp, new R(_autoScrollX - 6, _autoScrollY - 13, 12, 10), 7.5f, Th.Text);
        Gfx.Icon(Th.ChevronDown, new R(_autoScrollX - 6, _autoScrollY + 3, 12, 10), 7.5f, Th.Text);
    }

    public static float ScrollTarget(int id) => _scrolls.TryGetValue(id, out var s) ? s.Target : 0;
    public static float ScrollMax(int id) => _scrolls.TryGetValue(id, out var s) ? s.Max : 0;
    public static void ScrollBy(int id, float d) { if (_scrolls.TryGetValue(id, out var s)) { s.Target = Math.Clamp(s.Target + d, 0, s.Max); Animate(); } }
    public static void ScrollJump(int id, float d) { if (_scrolls.TryGetValue(id, out var s)) { s.Target = s.Pos = Math.Clamp(s.Pos + d, 0, s.Max); s.VelY = 0; Animate(); } }
    public static void ScrollReset(int id) { if (_scrolls.TryGetValue(id, out var s)) s.Pos = s.Target = 0; }
    public static bool ScrollMoving(int id) => _scrolls.TryGetValue(id, out var s) && (MathF.Abs(s.Target - s.Pos) > 0.5f || _dragScroll == id || _autoScrollId == id);
    /// <summary>The scroll container currently open on the clip stack (0 when outside any BeginScroll/EndScroll).</summary>
    public static int CurrentScrollId => _scrollDepth > 0 ? _scrollCandStack[_scrollDepth - 1].Id : 0;

    /// <summary>Starts a clipped scroll region and returns the current offset. Pair with EndScroll.</summary>
    public static float BeginScroll(int id, R view, float contentH)
    {
        if (!_scrolls.TryGetValue(id, out var s)) _scrolls[id] = s = new Scroll();
        s.Max = MathF.Max(0, contentH - view.H);
        float posBefore = s.Pos;
        if (s.Max > 0)
        {
            if (_autoScrollId == id)
            {
                float dy = MY - _autoScrollY;
                if (MathF.Abs(dy) > 10)
                {
                    float sign = MathF.Sign(dy);
                    float mag = MathF.Abs(dy) - 10;
                    // Smooth progressive speed like Chrome (gentle near center, fast further away)
                    float speed = sign * (mag * 5.5f + mag * mag * 0.045f);
                    float prev = s.Pos;
                    s.Target = s.Pos = Math.Clamp(s.Pos + speed * (float)Dt, 0, s.Max);
                    s.LastTouch = Now;
                    if (MathF.Abs(s.Pos - prev) > 0.01f) Animate();
                }
            }
            _scrollCandStack[_scrollDepth] = (id, view);
        }
        else
        {
            if (_autoScrollId == id) StopAutoScroll();
            _scrollCandStack[_scrollDepth] = default;
        }

        if (Wheel != 0 && Hover(view))
        {
            s.Target -= Wheel * 110;
            s.LastTouch = Now;
            Wheel = 0;
        }
        s.Target = Math.Clamp(s.Target, 0, s.Max);

        // scrollbar
        if (s.Max > 0)
        {
            var barH = MathF.Max(32, view.H * view.H / contentH);
            var track = new R(view.Right - 10, view.Y, 10, view.H);
            var thumbY = view.Y + (view.H - barH) * (s.Pos / s.Max);
            var thumb = new R(view.Right - 8, thumbY, 5, barH);
            var overTrack = Hover(track);
            if (Pressed && overTrack)
            {
                _dragScroll = id;
                s.DragOffset = thumb.Contains(MX, MY) ? MY - thumbY : barH / 2;
            }
            if (_dragScroll == id)
            {
                if (!Down) _dragScroll = 0;
                else
                {
                    s.Target = s.Pos = Math.Clamp((MY - s.DragOffset - view.Y) / MathF.Max(1, view.H - barH) * s.Max, 0, s.Max);
                    s.LastTouch = Now;
                }
            }
            var hot = overTrack || _dragScroll == id;
            long since = Now - s.LastTouch;
            _pendingBarStack[_scrollDepth] = (id, thumb, hot, since < 900 || hot, s.LastTouch + 900);
        }
        else _pendingBarStack[_scrollDepth] = default;

        // critically damped approach: quick, no overshoot, frame-rate independent
        var diff = s.Target - s.Pos;
        bool moving = MathF.Abs(diff) > 0.4f;
        if (moving)
        {
            s.Pos += diff * (1 - MathF.Exp(-(float)Dt * 24f));
            s.LastTouch = Now;
            Animate();
        }
        else s.Pos = s.Target;

        // Track smoothed vertical scroll velocity (DIPs/sec) for hardware GPU DirectionalBlur
        if (!moving && _dragScroll != id && _autoScrollId != id)
        {
            s.VelY = 0f;
        }
        else
        {
            float instVel = (s.Pos - posBefore) / MathF.Max(0.002f, (float)Dt);
            s.VelY += (instVel - s.VelY) * (1f - MathF.Exp(-(float)Dt * 34f));
            if (MathF.Abs(s.VelY) < 25f) s.VelY = 0f;
        }

        _mbActiveStack[_scrollDepth] = Gfx.PushMotionBlur(view, 0f, -s.VelY);
        _scrollDepth++;
        Gfx.PushClip(view);
        // Sub-pixel smooth scroll while moving (silky at 120-240Hz); snap to crisp physical pixel when settled
        return moving || MathF.Abs(s.VelY) > 15f ? s.Pos : Gfx.Snap(s.Pos);
    }

    private static readonly (int Id, R View)[] _scrollCandStack = new (int, R)[8];
    private static readonly (int Id, R Thumb, bool Hot, bool Visible, long HideAt)[] _pendingBarStack = new (int, R, bool, bool, long)[8];
    private static int _scrollDepth;
    private static long _barHideScheduledAt;

    public static void EndScroll()
    {
        Gfx.PopClip();
        if (_scrollDepth <= 0) return;
        _scrollDepth--;
        Gfx.PopMotionBlur(_mbActiveStack[_scrollDepth]);
        _mbActiveStack[_scrollDepth] = false;
        var cand = _scrollCandStack[_scrollDepth];
        var bar = _pendingBarStack[_scrollDepth];
        _scrollCandStack[_scrollDepth] = default;
        _pendingBarStack[_scrollDepth] = default;

        // Browser-style autoscroll: only activates when MMB was pressed on empty space (not over a video card / link).
        if (!_middleOverLink && Modal == null && _autoScrollId == 0 && cand.Id != 0
            && cand.View.Contains(MiddleDownX, MiddleDownY) && Gfx.Clip.Contains(MiddleDownX, MiddleDownY))
        {
            if (MiddleDown && (MathF.Abs(MY - MiddleDownY) > 10 || Now - MiddleDownAt > 220))
            {
                _autoScrollId = cand.Id;
                _autoScrollX = MiddleDownX;
                _autoScrollY = MiddleDownY;
                AutoScrollHold = true;
                Animate();
            }
            else if (MiddleClicked && cand.View.Contains(MX, MY) && Gfx.Clip.Contains(MX, MY))
            {
                _autoScrollId = cand.Id;
                _autoScrollX = MX;
                _autoScrollY = MY;
                AutoScrollHold = false;
                MiddleClicked = false;
                Animate();
            }
        }
        if (bar.Id != 0)
        {
            // the bar fades in when the view moves and out a moment after it stops; it thickens under the pointer
            var a = Anim(bar.Id ^ 0x3C3C, bar.Visible ? 1 : 0, 10);
            var wide = Anim(bar.Id ^ 0x3D3D, bar.Hot ? 1 : 0, 22);
            if (a > 0.01f)
            {
                var t = bar.Thumb;
                t = new R(t.X - 3 * wide, t.Y, t.W + 3 * wide, t.H);
                Gfx.Fill(t, Th.A((0.27f + 0.33f * wide) * a), t.W / 2);
            }
            // Schedule a single wake-up when the 900ms hide timeout expires instead of spinning 60 FPS!
            if (bar.Visible && !bar.Hot && bar.HideAt > Now && _barHideScheduledAt < bar.HideAt)
            {
                _barHideScheduledAt = bar.HideAt;
                App.After((int)Math.Clamp(bar.HideAt - Now + 16, 30, 1000), App.Invalidate);
            }
        }
    }

    // ---------------- small drawing helpers ----------------

    public static void Spinner(float cx, float cy, float radius, uint color)
    {
        var t = Now / 110.0;
        for (int i = 0; i < 8; i++)
        {
            var a = (float)(i * Math.PI / 4);
            var phase = (float)((t - i) % 8 + 8) % 8 / 8f;
            Gfx.Circle(cx + MathF.Cos(a) * radius, cy + MathF.Sin(a) * radius, radius * 0.22f, Gfx.Fade(color, 1 - phase * 0.85f));
        }
        Animate();
    }

    public static void Progress(R r, double value, uint? track = null, uint? fill = null)
    {
        Gfx.Fill(r, track ?? Th.A(0.4f), r.H / 2);
        if (value > 0) Gfx.Fill(new R(r.X, r.Y, (float)(r.W * Math.Clamp(value, 0, 1)), r.H), fill ?? Th.Brand, r.H / 2);
    }

    /// <summary>Loading placeholder with a slow pulse, so a page that is on its way does not look frozen.</summary>
    public static uint Pulse(uint color)
    {
        Animate();
        var p = 0.5f + 0.5f * MathF.Sin((float)((Now - StartTick) / 320.0));
        return Gfx.Mix(color, Gfx.Mix(Th.Card, Th.Ink, 0.06f), p * 0.6f);
    }
}
