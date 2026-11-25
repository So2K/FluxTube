using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Reaper-inspired floating frosted-glass DSP Studio Rack:
/// live spectrum + interactive 10-band EQ curve, 10 pro presets (Juicy, Nightcore, Slowed+Reverb, Bass Boost, Phonk, 8D...),
/// 10 vertical EQ faders, studio multi-tap reverb (Room/Plate/Hall/Cathedral), Bass, Exciter, 3D Stereo,
/// Tape-locked speed+pitch & formant-preserving semitone pitch, Limiter, and Audio Visualizer style selector.
/// </summary>
public static class AudioStudioPanel
{
    public static bool Open { get; private set; }
    public static R PanelRect { get; private set; }

    private static int _dragBand = -1;
    private static string? _dragKnob;
    private static long _lastApplyAt, _saveDueAt, _openedAt;
    private static bool _pendingApply, _pendingSpeed;

    private static readonly (string Key, string Ru, string En)[] VisStyles =
    [
        ("bars_wave", "Волны + спектр", "Waves + bars"),
        ("ribbon", "Шёлковые волны", "Silk waves"),
        ("bars", "Спектр", "Spectrum"),
        ("off", "Выкл", "Off"),
    ];

    public static void Toggle()
    {
        if (Open) Close();
        else Show();
    }

    public static void Show()
    {
        AccountMenu.Close();
        SharePanel.Close();
        ThemeCodeEditor.Close();
        LoginModal.Close();
        AudioDsp.EnsureEqArray(AppSettings.Current);
        Open = true;
        _openedAt = UI.Now;
        _dragBand = -1;
        _dragKnob = null;
        Sfx.Tab();
        App.Invalidate();
    }

    public static void Close()
    {
        if (!Open) return;
        Open = false;
        _dragBand = -1;
        _dragKnob = null;
        FlushPending();
        PanelRect = default;
        Sfx.Back();
        App.Invalidate();
    }

    private static void ApplyNow(bool syncSpeed = false)
    {
        _lastApplyAt = UI.Now;
        _pendingApply = false;
        _pendingSpeed = false;
        MpvPlayer.Instance.ApplyAudioDsp(syncSpeed);
        BgPlayers.ApplyAudioDsp(syncSpeed);
        Shell.PushPlayerState();
        _saveDueAt = UI.Now + 350;
    }

    private static void ScheduleApply(bool syncSpeed = false)
    {
        _pendingApply = true;
        _pendingSpeed |= syncSpeed;
        if (UI.Now - _lastApplyAt >= 65) ApplyNow(_pendingSpeed);
    }

    private static void FlushPending()
    {
        if (_pendingApply) ApplyNow(_pendingSpeed);
        if (_saveDueAt > 0) { _saveDueAt = 0; AppSettings.Current.Save(); }
    }

    public static void Draw()
    {
        if (!Open)
        {
            PanelRect = default;
            if (_saveDueAt > 0 && UI.Now >= _saveDueAt) { _saveDueAt = 0; AppSettings.Current.Save(); }
            return;
        }

        if (!UI.Down && (_dragBand >= 0 || _dragKnob != null))
        {
            _dragBand = -1;
            _dragKnob = null;
            if (_pendingApply) ApplyNow(_pendingSpeed);
        }
        if (_pendingApply && UI.Now - _lastApplyAt >= 70) ApplyNow(_pendingSpeed);
        if (_saveDueAt > 0 && UI.Now >= _saveDueAt && !UI.Down) { _saveDueAt = 0; AppSettings.Current.Save(); }

        var s = AppSettings.Current;
        AudioDsp.EnsureEqArray(s);

        float w = Gfx.W, h = Gfx.H;
        float pw = MathF.Min(820, w - 40);
        float ph = MathF.Min(610, h - 40);
        float ot = Math.Clamp((UI.Now - _openedAt) / 145f, 0f, 1f);
        if (ot < 1f) UI.Animate();
        float oEase = 1f - (1f - ot) * (1f - ot);
        float dy = (1f - oEase) * 14f;
        float velY = ot < 1f ? -2f * (1f - ot) * 14f / 0.145f : 0f;
        var panel = new R(Gfx.Snap((w - pw) / 2), Gfx.Snap((h - ph) / 2 + dy), pw, ph);
        PanelRect = panel;

        // Dim backdrop — clicking outside closes the studio rack
        Gfx.Fill(new R(0, 0, w, h), Gfx.Fade(0xFF000000, (Th.Light ? 0.28f : 0.56f) * oEase));
        if (UI.Pressed && !panel.Contains(UI.MX, UI.MY) && !Menus.Open) { Close(); return; }

        var savedModal = UI.Modal;
        UI.Modal = panel;

        bool mb = Gfx.PushMotionBlur(panel.Inset(-8), 0f, velY);
        Gfx.Fill(panel.Inset(-4), Gfx.Fade(0xFF000000, 0.36f * oEase), 20);
        Gfx.FillFrosted(panel, Th.Panel, 16, opacity: 0.94f, border: true);

        // ---- Header ----
        float pad = 18;
        float y = panel.Y + 14;
        var title = Gfx.Text(L.T("DSP Студия · Эквалайзер и эффекты", "DSP Studio · Equalizer & FX"), 17, 600);
        Gfx.Draw(title, panel.X + pad, y + 6, Th.Text);

        var presetBadge = AudioDsp.DisplayPresetName(s);
        var bRun = Gfx.Text(presetBadge, 11.5f, 600);
        var bRect = new R(panel.X + pad + title.W + 10, y + 6, bRun.W + 16, 22);
        Gfx.Fill(bRect, s.AudioFxEnabled ? Gfx.Fade(Th.Brand, 0.22f) : Th.A(0.08f), 11);
        Gfx.Stroke(bRect, s.AudioFxEnabled ? Gfx.Fade(Th.Brand, 0.55f) : Th.Line, 11, 1f);
        Gfx.Draw(bRun, bRect.X + 8, bRect.Y + (bRect.H - bRun.H) / 2, s.AudioFxEnabled ? Th.Brand : Th.Sub);

        float rx = panel.Right - pad;
        if (UI.IconButton(UI.Id("dsp:close"), new R(rx - 32, y + 1, 32, 32), Th.Close, 12, Th.Sub)) { Gfx.PopMotionBlur(mb); Close(); UI.Modal = savedModal; return; }
        rx -= 38;

        var resetLbl = L.T("Сброс", "Reset");
        var resetW = UI.PillWidth(Th.Restart, resetLbl);
        rx -= resetW;
        if (UI.Pill(UI.Id("dsp:reset"), rx, y + 1, 32, Th.Restart, resetLbl, out _))
        {
            AudioDsp.ApplyPreset(s, "Flat");
            ApplyNow(syncSpeed: true);
            Shell.Toast(L.T("DSP сброшен на Чистый", "DSP reset to Flat"), Th.Sub, 1800);
        }
        rx -= 8;

        var bypassLabel = s.AudioFxEnabled ? L.T("FX: ВКЛ", "FX: ON") : "FX: BYPASS";
        var byW = UI.PillWidth(Th.Headphones, bypassLabel);
        rx -= byW;
        if (UI.Pill(UI.Id("dsp:bypass"), rx, y + 1, 32, Th.Headphones, bypassLabel, out _, accent: s.AudioFxEnabled))
        {
            s.AudioFxEnabled = !s.AudioFxEnabled;
            ApplyNow(syncSpeed: true);
        }

        y += 42;
        Gfx.Fill(new R(panel.X + pad, y, panel.W - pad * 2, 1), Th.Line);
        y += 10;

        // ---- Live Spectrum + Smooth EQ Curve Preview Banner ----
        var vizR = new R(panel.X + pad, y, panel.W - pad * 2, 74);
        Gfx.Fill(vizR, Gfx.Fade(0xFF0A0A10, Th.Light ? 0.14f : 0.72f), 10);
        Gfx.Stroke(vizR, Th.Line, 10, 1f);
        Gfx.PushClip(vizR.Inset(1));
        AudioAnalyzer.DrawStageVisualizer(vizR, s.VisualizerStyle == "off" ? "bars_wave" : s.VisualizerStyle, opacity: 0.72f);
        // Zero dB gridline + live EQ curve
        float midY = vizR.Y + vizR.H * 0.5f;
        Gfx.Line(vizR.X + 8, midY, vizR.Right - 8, midY, Th.A(0.14f), 1f);
        float px0 = vizR.X + 24, px1 = vizR.Right - 24;
        float prevCx = 0, prevCy = 0;
        uint curveCol = s.AudioFxEnabled ? Th.Brand : Th.Sub;
        for (int i = 0; i < AudioDsp.Freqs.Length; i++)
        {
            float cx = px0 + (px1 - px0) * (i / (float)(AudioDsp.Freqs.Length - 1));
            float cy = midY - Math.Clamp(s.EqBands[i], -12f, 12f) / 12f * (vizR.H * 0.39f);
            if (i > 0)
            {
                Gfx.Line(prevCx, prevCy, cx, cy, Gfx.Fade(curveCol, 0.28f), 5f);
                Gfx.Line(prevCx, prevCy, cx, cy, curveCol, 2f);
            }
            Gfx.Circle(cx, cy, 3.2f, curveCol);
            prevCx = cx; prevCy = cy;
        }
        Gfx.PopClip();
        y += vizR.H + 10;

        // ---- Reaper-style Preset Strip (wrapping cleanly across 2 compact rows) ----
        float px = panel.X + pad;
        float maxRight = panel.Right - pad;
        float rowH = 28;
        for (int i = 0; i < AudioDsp.Presets.Length; i++)
        {
            var pr = AudioDsp.Presets[i];
            var lbl = AudioDsp.PresetLabel(pr);
            var pwBtn = UI.PillWidth(null, lbl) - 8;
            if (px + pwBtn > maxRight && px > panel.X + pad) { px = panel.X + pad; y += rowH + 6; }
            bool active = (s.AudioFxEnabled && s.AudioPreset.Equals(pr.Id, StringComparison.OrdinalIgnoreCase))
                       || (!s.AudioFxEnabled && pr.Id == "Flat");
            var btnR = new R(px, y, pwBtn, rowH);
            Gfx.Fill(btnR, active ? Th.Brand : Th.A(0.08f), rowH / 2);
            if (!active) UI.Glow(UI.Id("dsp:p", i), btnR, rowH / 2, UI.Hover(btnR));
            var lr = Gfx.Text(lbl, 12f, active ? 600 : 500);
            Gfx.Draw(lr, btnR.X + (btnR.W - lr.W) / 2, btnR.Y + (btnR.H - lr.H) / 2, active ? Th.OnBrand : Th.Text);
            if (UI.Click(UI.Id("dsp:p", i), btnR))
            {
                AudioDsp.ApplyPreset(s, pr.Id);
                ApplyNow(syncSpeed: true);
                Shell.Toast(L.T("Пресет: ", "Preset: ") + lbl, Th.Good, 1600);
            }
            px += pwBtn + 6;
        }
        y += rowH + 12;

        // ---- Main Split: Left 10-Band EQ Faders | Right Master FX Rack ----
        float bodyH = panel.Bottom - y - 50;
        float eqW = MathF.Round((panel.W - pad * 2 - 14) * 0.56f);
        var eqBox = new R(panel.X + pad, y, eqW, bodyH);
        var fxBox = new R(eqBox.Right + 14, y, panel.Right - pad - (eqBox.Right + 14), bodyH);

        // Left: 10-band graphic EQ
        Gfx.FillFrosted(eqBox, Th.Card, 12, opacity: 0.42f, border: true);
        Gfx.Draw(L.T("10-полосный эквалайзер (ПКМ — сбросить полосу)", "10-band graphic EQ (right-click to reset band)"), eqBox.X + 12, eqBox.Y + 8, 11.5f, Th.Sub, 500);
        float faderTop = eqBox.Y + 42;
        float faderBot = eqBox.Bottom - 26;
        float faderH = MathF.Max(60, faderBot - faderTop);
        float faderMid = faderTop + faderH * 0.5f;
        Gfx.Line(eqBox.X + 12, faderMid, eqBox.Right - 12, faderMid, Th.A(0.14f), 1f);

        float colW = (eqBox.W - 20) / AudioDsp.Freqs.Length;
        for (int i = 0; i < AudioDsp.Freqs.Length; i++)
        {
            float cx = eqBox.X + 10 + colW * (i + 0.5f);
            var hit = new R(cx - colW * 0.46f, faderTop - 6, colW * 0.92f, faderH + 12);
            bool over = UI.Hover(hit);
            bool dragging = _dragBand == i;

            if (UI.Pressed && over) _dragBand = i;
            if (UI.RightClick(hit))
            {
                s.EqBands[i] = 0f;
                AudioDsp.MarkCustom(s);
                ApplyNow();
            }
            else if (over && UI.Wheel != 0)
            {
                s.EqBands[i] = Math.Clamp(MathF.Round((s.EqBands[i] + (UI.Wheel > 0 ? 1f : -1f)) * 2f) / 2f, -12f, 12f);
                UI.Wheel = 0;
                AudioDsp.MarkCustom(s);
                ScheduleApply();
            }
            if (dragging)
            {
                float norm = 1f - Math.Clamp((UI.MY - faderTop) / faderH, 0f, 1f);
                float db = MathF.Round((norm * 24f - 12f) * 2f) / 2f; // 0.5 dB steps
                if (MathF.Abs(db) < 0.4f) db = 0f; // gentle snap to 0 dB center!
                if (MathF.Abs(db - s.EqBands[i]) > 0.01f)
                {
                    s.EqBands[i] = db;
                    AudioDsp.MarkCustom(s);
                    ScheduleApply();
                }
            }
            if (over || dragging) UI.Hand();

            float g = Math.Clamp(s.EqBands[i], -12f, 12f);
            float ky = faderMid - (g / 12f) * (faderH * 0.5f);

            // Track
            Gfx.Fill(new R(cx - 2f, faderTop, 4f, faderH), Th.A(0.16f), 2f);
            // Active fill from 0 dB center to knob
            if (MathF.Abs(g) > 0.1f)
            {
                float fy = MathF.Min(faderMid, ky);
                float fh = MathF.Abs(faderMid - ky);
                Gfx.Fill(new R(cx - 2.5f, fy, 5f, fh), s.AudioFxEnabled ? Th.Brand : Th.Sub, 2.5f);
            }
            // Knob
            Gfx.Circle(cx, ky, dragging || over ? 6.5f : 5f, s.AudioFxEnabled ? Th.Brand : Th.Sub);
            Gfx.Circle(cx, ky, 2.4f, 0xFFFFFFFF);

            // Value above & frequency below
            var valTxt = g == 0 ? "0" : (g > 0 ? $"+{g:0.#}" : $"{g:0.#}");
            var vr = Gfx.Text(valTxt, 9.5f, dragging ? 700 : 500);
            Gfx.Draw(vr, cx - vr.W / 2, faderTop - 15, dragging || MathF.Abs(g) > 0.1f ? Th.Text : Th.Dim);
            var fr = Gfx.Text(AudioDsp.FreqLabels[i], 10f, 500);
            Gfx.Draw(fr, cx - fr.W / 2, faderBot + 6, Th.Sub);
        }

        // Right: Master FX Rack (Reverb, Bass, Exciter, Stereo, Speed/Nightcore, Pitch)
        Gfx.FillFrosted(fxBox, Th.Card, 12, opacity: 0.42f, border: true);
        float fx = fxBox.X + 12, fw = fxBox.W - 24, fyCur = fxBox.Y + 10;

        float DrawFxSlider(string key, string name, float val, float min, float max, float defVal, string fmt, bool enabled = true, bool affectsSpeed = false)
        {
            var hit = new R(fx, fyCur, fw, 32);
            bool over = enabled && UI.Hover(hit);
            bool drag = _dragKnob == key;
            if (enabled && UI.Pressed && over) _dragKnob = key;
            if (enabled && UI.RightClick(hit))
            {
                val = defVal;
                AudioDsp.MarkCustom(s);
                ApplyNow(affectsSpeed);
            }
            else if (enabled && over && UI.Wheel != 0)
            {
                float step = (max - min) / 25f;
                val = Math.Clamp(val + (UI.Wheel > 0 ? step : -step), min, max);
                UI.Wheel = 0;
                AudioDsp.MarkCustom(s);
                ScheduleApply(affectsSpeed);
            }
            if (drag)
            {
                float frac = Math.Clamp((UI.MX - fx) / MathF.Max(1, fw), 0f, 1f);
                float nv = min + frac * (max - min);
                if (MathF.Abs(nv - defVal) < (max - min) * 0.025f) nv = defVal;
                if (MathF.Abs(nv - val) > 0.001f)
                {
                    val = nv;
                    AudioDsp.MarkCustom(s);
                    ScheduleApply(affectsSpeed);
                }
            }
            if (over || drag) UI.Hand();

            Gfx.Draw(name, fx, fyCur, 11.5f, enabled ? Th.Text : Th.Dim, 500);
            var vr = Gfx.Text(fmt, 11f, 600);
            Gfx.Draw(vr, fx + fw - vr.W, fyCur, enabled ? (MathF.Abs(val - defVal) > 0.01f ? Th.Brand : Th.Sub) : Th.Dim);

            float ty = fyCur + 20;
            float pct = Math.Clamp((val - min) / MathF.Max(0.001f, max - min), 0f, 1f);
            UI.Progress(new R(fx, ty, fw, drag || over ? 5f : 4f), pct, Th.A(0.18f), enabled && s.AudioFxEnabled ? Th.Brand : Th.Dim);
            Gfx.Circle(fx + fw * pct, ty + (drag || over ? 2.5f : 2f), drag || over ? 5.5f : 4.2f, enabled ? Th.Text : Th.Dim);

            fyCur += 36;
            return val;
        }

        s.FxReverb = DrawFxSlider("rev", L.T("Реверберация (Reverb)", "Reverb"), s.FxReverb, 0, 100, 0, $"{s.FxReverb:0}%");

        // Room space selector pills
        float rpx = fx;
        float rbw = (fw - 12) / AudioDsp.Rooms.Length;
        for (int i = 0; i < AudioDsp.Rooms.Length; i++)
        {
            var (rk, _) = AudioDsp.Rooms[i];
            var rl = AudioDsp.RoomLabel(rk);
            var rr = new R(rpx, fyCur - 2, rbw, 22);
            bool on = s.FxReverbRoom == rk;
            Gfx.Fill(rr, on ? Gfx.Fade(Th.Brand, 0.25f) : Th.A(0.07f), 6);
            if (on) Gfx.Stroke(rr, Gfx.Fade(Th.Brand, 0.65f), 6, 1f);
            var lr = Gfx.Text(rl, 10.5f, on ? 600 : 500);
            Gfx.Draw(lr, rr.X + (rr.W - lr.W) / 2, rr.Y + (rr.H - lr.H) / 2, on ? Th.Brand : Th.Sub);
            if (UI.Click(UI.Id("dsp:room", i), rr))
            {
                s.FxReverbRoom = rk;
                if (s.FxReverb < 8) s.FxReverb = 32;
                AudioDsp.MarkCustom(s);
                ApplyNow();
            }
            rpx += rbw + 4;
        }
        fyCur += 28;

        s.FxBass = DrawFxSlider("bass", L.T("Бас / Суб-удар", "Bass / Sub punch"), s.FxBass, 0, 100, 0, $"{s.FxBass:0}%");
        s.FxExciter = DrawFxSlider("exc", L.T("Сочность / Exciter + Компрессор", "Exciter + Punch"), s.FxExciter, 0, 100, 0, $"{s.FxExciter:0}%");
        s.FxStereo = DrawFxSlider("st", L.T("Сцена / 3D Stereo", "3D Stereo width"), s.FxStereo, 0, 100, 0, $"{s.FxStereo:0}%");
        s.FxSpeed = DrawFxSlider("spd", L.T("Скорость (Nightcore / Slowed)", "Speed (Nightcore / Slowed)"), s.FxSpeed, 0.70f, 1.45f, 1.0f, $"{s.FxSpeed:0.00}×", enabled: true, affectsSpeed: true);
        var stUnit = L.T(" пт", " st");
        s.FxPitchSemitones = DrawFxSlider("pit", s.FxTapePitch ? L.T("Тон привязан к скорости (Tape)", "Tape-linked pitch") : L.T("Высота тона (полутона)", "Pitch shift (semitones)"),
            s.FxPitchSemitones, -6f, 6f, 0f, s.FxTapePitch ? "Auto" : (s.FxPitchSemitones >= 0 ? $"+{s.FxPitchSemitones:0.#}{stUnit}" : $"{s.FxPitchSemitones:0.#}{stUnit}"),
            enabled: !s.FxTapePitch);

        // ---- Footer Studio Toggles ----
        float footY = panel.Bottom - 40;
        float bx = panel.X + pad;

        void MiniToggle(string id, string label, bool on, Action click)
        {
            var tw = UI.PillWidth(null, label) - 6;
            var r = new R(bx, footY, tw, 28);
            Gfx.Fill(r, on ? Gfx.Fade(Th.Brand, 0.24f) : Th.A(0.08f), 14);
            if (on) Gfx.Stroke(r, Gfx.Fade(Th.Brand, 0.65f), 14, 1f);
            else UI.Glow(UI.Id(id), r, 14, UI.Hover(r));
            var tr = Gfx.Text(label, 11.5f, on ? 600 : 500);
            Gfx.Draw(tr, r.X + (r.W - tr.W) / 2, r.Y + (r.H - tr.H) / 2, on ? Th.Brand : Th.Sub);
            if (UI.Click(UI.Id(id), r)) click();
            bx += tw + 8;
        }

        MiniToggle("dsp:tape", L.T("Тон со скоростью", "Tape pitch"), s.FxTapePitch, () => { s.FxTapePitch = !s.FxTapePitch; AudioDsp.MarkCustom(s); ApplyNow(syncSpeed: true); });
        MiniToggle("dsp:8d", L.T("8D Орбита", "8D Orbit"), s.PlayerAudio8D, () => { s.PlayerAudio8D = !s.PlayerAudio8D; AudioDsp.MarkCustom(s); ApplyNow(); });
        MiniToggle("dsp:lim", L.T("Лимитер (без клиппинга)", "Limiter (no clip)"), s.FxLimiter, () => { s.FxLimiter = !s.FxLimiter; ApplyNow(); });
        MiniToggle("dsp:audioonly", L.T("Только звук (0% GPU)", "Audio only (0% GPU)"), s.AudioOnlyMode, () => Shell.ToggleAudioOnlyMode());

        // Right-aligned visualizer style cycler
        var curVis = VisStyles.FirstOrDefault(v => v.Key == s.VisualizerStyle);
        var visName = curVis.Ru != null ? L.T(curVis.Ru, curVis.En) : L.T("Волны + спектр", "Waves + bars");
        var visLbl = L.T("Волны: ", "Visual: ") + visName;
        var vw = UI.PillWidth(null, visLbl) - 6;
        var vrBtn = new R(panel.Right - pad - vw, footY, vw, 28);
        Gfx.Fill(vrBtn, Th.A(0.08f), 14);
        UI.Glow(UI.Id("dsp:vis"), vrBtn, 14, UI.Hover(vrBtn));
        var vtr = Gfx.Text(visLbl, 11.5f, 500);
        Gfx.Draw(vtr, vrBtn.X + (vrBtn.W - vtr.W) / 2, vrBtn.Y + (vrBtn.H - vtr.H) / 2, Th.Text);
        if (UI.Click(UI.Id("dsp:vis"), vrBtn))
        {
            int curIdx = Array.FindIndex(VisStyles, v => v.Key == s.VisualizerStyle);
            s.VisualizerStyle = VisStyles[(curIdx + 1) % VisStyles.Length].Key;
            s.Save();
            App.Invalidate();
        }

        Gfx.PopMotionBlur(mb);
        UI.Modal = savedModal;
    }
}
