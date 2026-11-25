using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Pro FabFilter / Reaper-style floating frosted-glass DSP Studio Rack:
/// - Real-time WASAPI spectrum (auto-requested even during video playback!) behind an interactive draggable cubic-spline EQ graph.
/// - 23 studio-calibrated presets across 5 categories (Studio, Bass &amp; Club, Vibes, Voice &amp; Cinema) + user-saved custom presets!
/// - Inline "+ Save" preset creator &amp; right-click update/delete for custom presets.
/// - Clean two-column 10-band EQ faders + Master FX Rack (Reverb + 4 spaces, Bass, Air/Exciter, 3D Stereo, Speed, Pitch).
/// - Zero overlapping buttons at any zoom/language.
/// </summary>
public static class AudioStudioPanel
{
    public static bool Open { get; private set; }
    public static R PanelRect { get; private set; }

    private static int _dragBand = -1;
    private static int _dragCurveBand = -1;
    private static string? _dragKnob;
    private static string _cat = "all";
    private static bool _savingPreset;
    private static Edit? _presetNameEdit;
    private static long _lastApplyAt, _saveDueAt, _openedAt;
    private static bool _pendingApply, _pendingSpeed;

    private static readonly (string Key, string Ru, string En)[] VisStyles =
    [
        ("bars_wave", "Волны+спектр", "Waves+bars"),
        ("ribbon", "Шёлк", "Silk"),
        ("bars", "Спектр", "Bars"),
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
        _dragCurveBand = -1;
        _dragKnob = null;
        _savingPreset = false;
        _presetNameEdit?.Hide();
        Sfx.Tab();
        App.Invalidate();
    }

    public static void Close()
    {
        if (!Open) return;
        Open = false;
        _dragBand = -1;
        _dragCurveBand = -1;
        _dragKnob = null;
        _savingPreset = false;
        _presetNameEdit?.Hide();
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

    private static void BeginSavePreset()
    {
        var s = AppSettings.Current;
        if (_presetNameEdit == null)
        {
            _presetNameEdit = new Edit(L.T("Название пресета…", "Preset name…")) { Bg = () => Th.Field };
            _presetNameEdit.Enter = CommitSavePreset;
            _presetNameEdit.Escape = () => { _savingPreset = false; _presetNameEdit?.Hide(); App.Invalidate(); };
        }
        else _presetNameEdit.SetPlaceholder(L.T("Название пресета…", "Preset name…"));

        string defName = s.AudioPreset.StartsWith("user:", StringComparison.Ordinal)
            ? s.AudioPreset[5..]
            : L.T($"Мой пресет {s.CustomAudioPresets.Count + 1}", $"My Preset {s.CustomAudioPresets.Count + 1}");
        _presetNameEdit.Text = defName;
        _savingPreset = true;
        App.Post(() => _presetNameEdit?.Focus(selectAll: true));
        App.Invalidate();
    }

    private static void CommitSavePreset()
    {
        var s = AppSettings.Current;
        var name = (_presetNameEdit?.Text ?? "").Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Введите название пресета", "Enter a preset name"), Th.Warn, 2000);
            return;
        }
        if (name.Length > 28) name = name[..28].Trim();

        var snap = AudioDsp.CaptureCurrent(s, name);
        int existing = s.CustomAudioPresets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) s.CustomAudioPresets[existing] = snap;
        else s.CustomAudioPresets.Add(snap);
        s.AudioPreset = "user:" + name;
        s.AudioFxEnabled = true;
        s.Save();

        _savingPreset = false;
        _presetNameEdit?.Hide();
        _cat = "saved";
        Sfx.Like();
        Shell.Toast(L.T($"Пресет «{name}» сохранён!", $"Preset \"{name}\" saved!"), Th.Good, 2400);
        App.Invalidate();
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    public static void Draw()
    {
        if (!Open)
        {
            PanelRect = default;
            if (_saveDueAt > 0 && UI.Now >= _saveDueAt) { _saveDueAt = 0; AppSettings.Current.Save(); }
            return;
        }

        if (!UI.Down && (_dragBand >= 0 || _dragCurveBand >= 0 || _dragKnob != null))
        {
            _dragBand = -1;
            _dragCurveBand = -1;
            _dragKnob = null;
            if (_pendingApply) ApplyNow(_pendingSpeed);
        }
        if (_pendingApply && UI.Now - _lastApplyAt >= 70) ApplyNow(_pendingSpeed);
        if (_saveDueAt > 0 && UI.Now >= _saveDueAt && !UI.Down) { _saveDueAt = 0; AppSettings.Current.Save(); }

        var s = AppSettings.Current;
        AudioDsp.EnsureEqArray(s);
        // Request live WASAPI spectrum frames while the studio panel is visible!
        AudioAnalyzer.RequestFrame();

        float w = Gfx.W, h = Gfx.H;
        float pw = MathF.Min(900, w - 36);
        float ph = MathF.Min(670, h - 36);
        float ot = Math.Clamp((UI.Now - _openedAt) / 145f, 0f, 1f);
        if (ot < 1f) UI.Animate();
        float oEase = 1f - (1f - ot) * (1f - ot);
        float dy = (1f - oEase) * 14f;
        float velY = ot < 1f ? -2f * (1f - ot) * 14f / 0.145f : 0f;
        var panel = new R(Gfx.Snap((w - pw) / 2), Gfx.Snap((h - ph) / 2 + dy), pw, ph);
        PanelRect = panel;

        // Dim backdrop — clicking outside closes the studio rack
        Gfx.Fill(new R(0, 0, w, h), Gfx.Fade(0xFF000000, (Th.Light ? 0.28f : 0.58f) * oEase));
        if (UI.Pressed && !panel.Contains(UI.MX, UI.MY) && !Menus.Open) { Close(); return; }

        var savedModal = UI.Modal;
        UI.Modal = panel;

        bool mb = Gfx.PushMotionBlur(panel.Inset(-8), 0f, velY);
        float pRad = Gfx.Corner(16);
        Gfx.Fill(panel.Inset(-4), Gfx.Fade(0xFF000000, 0.36f * oEase), pRad + 4);
        Gfx.FillFrosted(panel, Th.Panel, pRad, opacity: 0.96f, border: true);
        if (s.UiNeonGlow) Gfx.NeonGlow(panel, pRad, Th.Brand, 0.35f);

        // ---- Header ----
        float pad = 18;
        float y = panel.Y + 14;
        var title = Gfx.Text(L.T("DSP Студия · Эквалайзер", "DSP Studio · Equalizer"), 17, 600);
        Gfx.Draw(title, panel.X + pad, y + 5, Th.Text);

        var presetBadge = AudioDsp.DisplayPresetName(s);
        var bRun = Gfx.Text(presetBadge, 11.5f, 600);
        var bRect = new R(panel.X + pad + title.W + 10, y + 6, bRun.W + 16, 22);
        Gfx.Fill(bRect, s.AudioFxEnabled ? Gfx.Fade(Th.Brand, 0.22f) : Th.A(0.08f), Gfx.Corner(11));
        Gfx.Stroke(bRect, s.AudioFxEnabled ? Gfx.Fade(Th.Brand, 0.55f) : Th.Line, Gfx.Corner(11), 1f);
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

        var saveBtnLbl = L.T("Сохранить", "Save");
        var saveBtnW = UI.PillWidth(Th.Add, saveBtnLbl);
        rx -= saveBtnW;
        if (UI.Pill(UI.Id("dsp:savebtn"), rx, y + 1, 32, Th.Add, saveBtnLbl, out _, _savingPreset ? Gfx.Fade(Th.Good, 0.25f) : Th.A(0.10f)))
        {
            if (_savingPreset) { _savingPreset = false; _presetNameEdit?.Hide(); }
            else BeginSavePreset();
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

        y += 40;

        // Optional inline "Save Custom Preset" bar (zero modal clutter!)
        if (_savingPreset)
        {
            var saveBar = new R(panel.X + pad, y, panel.W - pad * 2, 38);
            Gfx.Fill(saveBar, Gfx.Fade(Th.Brand, 0.10f), Gfx.Corner(10));
            Gfx.Stroke(saveBar, Gfx.Fade(Th.Brand, 0.45f), Gfx.Corner(10), 1f);
            Gfx.Draw(L.T("Имя пресета:", "Preset name:"), saveBar.X + 12, saveBar.Y + 10, 12.5f, Th.Text, 600);
            float labelW = Gfx.Text(L.T("Имя пресета:", "Preset name:"), 12.5f, 600).W + 24;
            var okLbl = L.T("Сохранить пресет", "Save preset");
            var okW = UI.PillWidth(Th.Check, okLbl);
            var canW = UI.PillWidth(null, L.T("Отмена", "Cancel"));
            float editW = MathF.Max(140, saveBar.W - labelW - okW - canW - 28);
            var editBox = new R(saveBar.X + labelW, saveBar.Y + 4, editW, 30);
            Gfx.Fill(editBox, Th.Field, Gfx.Corner(8));
            Gfx.Stroke(editBox, Th.Brand, Gfx.Corner(8), 1.2f);
            _presetNameEdit?.Place(editBox, 10, 10, allowInModal: true);
            if (UI.Pill(UI.Id("dsp:saveok"), editBox.Right + 8, saveBar.Y + 4, 30, Th.Check, okLbl, out _, accent: true))
                CommitSavePreset();
            if (UI.Pill(UI.Id("dsp:savecan"), editBox.Right + 8 + okW + 6, saveBar.Y + 4, 30, null, L.T("Отмена", "Cancel"), out _))
            { _savingPreset = false; _presetNameEdit?.Hide(); }
            y += 44;
        }
        else _presetNameEdit?.Hide();

        Gfx.Fill(new R(panel.X + pad, y, panel.W - pad * 2, 1), Th.Line);
        y += 10;

        // ---- Interactive FabFilter-Style EQ Spline Graph + Live WASAPI Spectrum ----
        var vizR = new R(panel.X + pad, y, panel.W - pad * 2, 100);
        Gfx.Fill(vizR, Gfx.Fade(0xFF07070C, Th.Light ? 0.14f : 0.78f), Gfx.Corner(10));
        Gfx.Stroke(vizR, Th.Line, Gfx.Corner(10), 1f);
        Gfx.PushClip(vizR.Inset(1));
        AudioAnalyzer.RequestFrame(force: true);
        AudioAnalyzer.DrawStageVisualizer(vizR, s.VisualizerStyle == "off" ? "bars_wave" : s.VisualizerStyle, opacity: 0.65f);

        float midY = vizR.Y + vizR.H * 0.5f;
        float dbSpan = vizR.H * 0.39f;
        Gfx.Line(vizR.X + 34, midY - dbSpan * 0.5f, vizR.Right - 10, midY - dbSpan * 0.5f, Th.A(0.07f), 1f);
        Gfx.Line(vizR.X + 34, midY, vizR.Right - 10, midY, Th.A(0.16f), 1f);
        Gfx.Line(vizR.X + 34, midY + dbSpan * 0.5f, vizR.Right - 10, midY + dbSpan * 0.5f, Th.A(0.07f), 1f);
        Gfx.Draw("+12", vizR.X + 6, vizR.Y + 4, 9f, Th.Dim, 500);
        Gfx.Draw("0 dB", vizR.X + 6, midY - 6, 9f, Th.Sub, 600);
        Gfx.Draw("-12", vizR.X + 6, vizR.Bottom - 14, 9f, Th.Dim, 500);

        float px0 = vizR.X + 46, px1 = vizR.Right - 20;
        Span<float> nodeX = stackalloc float[AudioDsp.Freqs.Length];
        Span<float> nodeY = stackalloc float[AudioDsp.Freqs.Length];
        for (int i = 0; i < AudioDsp.Freqs.Length; i++)
        {
            nodeX[i] = px0 + (px1 - px0) * (i / (float)(AudioDsp.Freqs.Length - 1));
            nodeY[i] = midY - Math.Clamp(s.EqBands[i], -12f, 12f) / 12f * dbSpan;
        }

        // Direct node dragging right on the EQ curve!
        if (UI.Pressed && UI.Hover(vizR))
        {
            int best = -1; float bestD = 26f;
            for (int i = 0; i < nodeX.Length; i++)
            {
                float d = MathF.Sqrt((UI.MX - nodeX[i]) * (UI.MX - nodeX[i]) + (UI.MY - nodeY[i]) * (UI.MY - nodeY[i]));
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) _dragCurveBand = best;
        }
        if (_dragCurveBand >= 0)
        {
            float db = MathF.Round((Math.Clamp((midY - UI.MY) / dbSpan, -1f, 1f) * 12f) * 2f) / 2f;
            if (MathF.Abs(db) < 0.4f) db = 0f;
            if (MathF.Abs(db - s.EqBands[_dragCurveBand]) > 0.01f)
            {
                s.EqBands[_dragCurveBand] = db;
                AudioDsp.MarkCustom(s);
                ScheduleApply();
            }
            nodeY[_dragCurveBand] = midY - Math.Clamp(s.EqBands[_dragCurveBand], -12f, 12f) / 12f * dbSpan;
            UI.Hand();
        }

        // Draw silky Catmull-Rom spline EQ curve with glowing translucent area fill
        uint curveCol = s.AudioFxEnabled ? Th.Brand : Th.Sub;
        uint curveCol2 = s.AudioFxEnabled && Th.Brand2 != 0 ? Th.Brand2 : curveCol;
        const int segSteps = 8;
        float lastX = nodeX[0], lastY = nodeY[0];
        for (int i = 0; i < nodeX.Length - 1; i++)
        {
            float y0 = nodeY[Math.Max(0, i - 1)], y1 = nodeY[i], y2 = nodeY[i + 1], y3 = nodeY[Math.Min(nodeX.Length - 1, i + 2)];
            for (int st = 1; st <= segSteps; st++)
            {
                float t = st / (float)segSteps;
                float sx = nodeX[i] + (nodeX[i + 1] - nodeX[i]) * t;
                float sy = Math.Clamp(CatmullRom(y0, y1, y2, y3, t), vizR.Y + 4, vizR.Bottom - 4);
                float mixT = (i + t) / (nodeX.Length - 1);
                uint segCol = Gfx.Mix(curveCol, curveCol2, mixT);
                // Soft vertical fill toward 0 dB axis
                float fy = MathF.Min(sy, midY);
                float fh = MathF.Abs(sy - midY);
                if (fh > 1f) Gfx.Fill(new R(lastX, fy, sx - lastX + 0.8f, fh), Gfx.Fade(segCol, 0.14f));
                Gfx.Line(lastX, lastY, sx, sy, Gfx.Fade(segCol, 0.28f), 5f);
                Gfx.Line(lastX, lastY, sx, sy, segCol, 2.2f);
                lastX = sx; lastY = sy;
            }
        }
        for (int i = 0; i < nodeX.Length; i++)
        {
            bool activeNode = _dragCurveBand == i || _dragBand == i;
            Gfx.Circle(nodeX[i], nodeY[i], activeNode ? 5.5f : 3.6f, curveCol);
            Gfx.Circle(nodeX[i], nodeY[i], activeNode ? 2.4f : 1.5f, 0xFFFFFFFF);
        }
        Gfx.PopClip();
        y += vizR.H + 10;

        // ---- Category Tabs (Studio / Bass & Club / Vibes / Voice & Cinema / Saved) ----
        float cxCat = panel.X + pad;
        void CatTab(string key, string label)
        {
            bool on = _cat == key;
            var tw = UI.PillWidth(null, label) - 10;
            var cr = new R(cxCat, y, tw, 24);
            Gfx.Fill(cr, on ? Gfx.Fade(Th.Brand, 0.22f) : Th.A(0.06f), Gfx.Corner(12));
            if (on) Gfx.Stroke(cr, Gfx.Fade(Th.Brand, 0.55f), Gfx.Corner(12), 1f);
            else UI.Glow(UI.Id("dsp:cat:" + key), cr, Gfx.Corner(12), UI.Hover(cr));
            var tr = Gfx.Text(label, 11.5f, on ? 600 : 500);
            Gfx.Draw(tr, cr.X + (cr.W - tr.W) / 2, cr.Y + (cr.H - tr.H) / 2, on ? Th.Brand : Th.Sub);
            if (UI.Click(UI.Id("dsp:cat:" + key), cr)) _cat = key;
            cxCat += tw + 6;
        }
        foreach (var (ck, cru, cen) in AudioDsp.Categories) CatTab(ck, L.T(cru, cen));
        CatTab("saved", L.T($"Мои ({s.CustomAudioPresets.Count})", $"Saved ({s.CustomAudioPresets.Count})"));
        y += 30;

        // ---- Preset Strip (clean bounded height so lower EQ/FX rack NEVER gets squished!) ----
        float px = panel.X + pad, maxRight = panel.Right - pad;
        float rowH = 26;
        int presetRows = 0;
        const int maxPresetRows = 3;

        void DrawPresetPill(int uid, string lbl, bool active, Action onClick, Action? onRightClick = null)
        {
            if (presetRows >= maxPresetRows) return;
            var pwBtn = UI.PillWidth(null, lbl) - 10;
            if (px + pwBtn > maxRight && px > panel.X + pad)
            {
                px = panel.X + pad;
                y += rowH + 5;
                presetRows++;
                if (presetRows >= maxPresetRows) return;
            }
            var btnR = new R(px, y, pwBtn, rowH);
            float br = Gfx.Corner(rowH / 2);
            Gfx.Fill(btnR, active ? Th.Brand : Th.A(0.08f), br);
            if (active && s.UiNeonGlow) Gfx.NeonGlow(btnR, br, Th.Brand, 0.5f);
            if (!active) UI.Glow(uid, btnR, br, UI.Hover(btnR));
            var lr = Gfx.Text(lbl, 11.5f, active ? 600 : 500);
            Gfx.Draw(lr, btnR.X + (btnR.W - lr.W) / 2, btnR.Y + (btnR.H - lr.H) / 2, active ? Th.OnBrand : Th.Text);
            if (onRightClick != null && UI.RightClick(btnR)) onRightClick();
            else if (UI.Click(uid, btnR)) onClick();
            px += pwBtn + 5;
        }

        if (_cat == "saved")
        {
            if (s.CustomAudioPresets.Count == 0)
            {
                Gfx.Draw(L.T("Пока нет сохранённых пресетов — настройте звук и нажмите «+ Сохранить» сверху.", "No saved presets yet — tune your EQ and click \"+ Save\" above."),
                    panel.X + pad, y + 5, 12f, Th.Sub);
            }
            else
            {
                for (int i = 0; i < s.CustomAudioPresets.Count; i++)
                {
                    var cp = s.CustomAudioPresets[i];
                    var cpName = cp.Name;
                    bool active = s.AudioFxEnabled && s.AudioPreset.Equals("user:" + cpName, StringComparison.OrdinalIgnoreCase);
                    DrawPresetPill(UI.Id("dsp:up", i), "★ " + cpName, active,
                        onClick: () =>
                        {
                            AudioDsp.ApplySavedPreset(s, cp);
                            ApplyNow(syncSpeed: true);
                            Shell.Toast(L.T("Пресет: ", "Preset: ") + cpName, Th.Good, 1600);
                        },
                        onRightClick: () => Menus.Popup([
                            new(L.T("Обновить текущими настройками", "Overwrite with current EQ"), () =>
                            {
                                int idx = s.CustomAudioPresets.FindIndex(x => x.Name == cpName);
                                if (idx >= 0) s.CustomAudioPresets[idx] = AudioDsp.CaptureCurrent(s, cpName);
                                s.Save();
                                Shell.Toast(L.T($"Пресет «{cpName}» обновлён", $"Preset \"{cpName}\" updated"), Th.Good, 1800);
                            }, icon: Th.Refresh),
                            new(L.T("Удалить пресет", "Delete preset"), () =>
                            {
                                s.CustomAudioPresets.RemoveAll(x => x.Name == cpName);
                                if (s.AudioPreset.Equals("user:" + cpName, StringComparison.OrdinalIgnoreCase))
                                    s.AudioPreset = "Custom";
                                s.Save();
                                Shell.Toast(L.T($"Пресет «{cpName}» удалён", $"Preset \"{cpName}\" deleted"), Th.Sub, 1800);
                            }, icon: Th.Delete, danger: true),
                        ]));
                }
            }
        }
        else
        {
            for (int i = 0; i < AudioDsp.Presets.Length; i++)
            {
                var pr = AudioDsp.Presets[i];
                if (_cat != "all" && pr.Category != _cat && pr.Id != "Flat") continue;
                var lbl = AudioDsp.PresetLabel(pr);
                bool active = (s.AudioFxEnabled && s.AudioPreset.Equals(pr.Id, StringComparison.OrdinalIgnoreCase))
                           || (!s.AudioFxEnabled && pr.Id == "Flat");
                DrawPresetPill(UI.Id("dsp:p", i), lbl, active, () =>
                {
                    AudioDsp.ApplyPreset(s, pr.Id);
                    ApplyNow(syncSpeed: true);
                    Shell.Toast(L.T("Пресет: ", "Preset: ") + lbl, Th.Good, 1600);
                });
            }
            if (_cat == "all")
            {
                for (int i = 0; i < s.CustomAudioPresets.Count; i++)
                {
                    var cp = s.CustomAudioPresets[i];
                    var cpName = cp.Name;
                    bool active = s.AudioFxEnabled && s.AudioPreset.Equals("user:" + cpName, StringComparison.OrdinalIgnoreCase);
                    DrawPresetPill(UI.Id("dsp:upall", i), "★ " + cpName, active, () =>
                    {
                        AudioDsp.ApplySavedPreset(s, cp);
                        ApplyNow(syncSpeed: true);
                        Shell.Toast(L.T("Пресет: ", "Preset: ") + cpName, Th.Good, 1600);
                    });
                }
            }
        }
        y += rowH + 10;

        // ---- Main Split: Left 10-Band EQ Faders | Right Master FX Rack ----
        float bodyH = MathF.Max(148, panel.Bottom - y - 46);
        float eqW = MathF.Round((panel.W - pad * 2 - 14) * 0.56f);
        var eqBox = new R(panel.X + pad, y, eqW, bodyH);
        var fxBox = new R(eqBox.Right + 14, y, panel.Right - pad - (eqBox.Right + 14), bodyH);

        // Left: 10-band graphic EQ
        Gfx.FillFrosted(eqBox, Th.Card, Gfx.Corner(12), opacity: 0.44f, border: true);
        Gfx.Draw(L.T("10-полосный эквалайзер (ПКМ — сброс · колёсико — шаг 1 дБ)", "10-band EQ (right-click reset · wheel ±1 dB)"), eqBox.X + 12, eqBox.Y + 8, 11.5f, Th.Sub, 500, eqBox.W - 24);
        float faderTop = eqBox.Y + 40;
        float faderBot = eqBox.Bottom - 24;
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
                float db = MathF.Round((norm * 24f - 12f) * 2f) / 2f;
                if (MathF.Abs(db) < 0.4f) db = 0f;
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

            Gfx.Fill(new R(cx - 2f, faderTop, 4f, faderH), Th.A(0.16f), 2f);
            if (MathF.Abs(g) > 0.1f)
            {
                float fy = MathF.Min(faderMid, ky);
                float fh = MathF.Abs(faderMid - ky);
                Gfx.Fill(new R(cx - 2.5f, fy, 5f, fh), s.AudioFxEnabled ? Th.Brand : Th.Sub, 2.5f);
            }
            Gfx.Circle(cx, ky, dragging || over ? 6.5f : 5f, s.AudioFxEnabled ? Th.Brand : Th.Sub);
            Gfx.Circle(cx, ky, 2.4f, 0xFFFFFFFF);

            var valTxt = g == 0 ? "0" : (g > 0 ? $"+{g:0.#}" : $"{g:0.#}");
            var vr = Gfx.Text(valTxt, 9.5f, dragging ? 700 : 500);
            Gfx.Draw(vr, cx - vr.W / 2, faderTop - 15, dragging || MathF.Abs(g) > 0.1f ? Th.Text : Th.Dim);
            var fr = Gfx.Text(AudioDsp.FreqLabels[i], 10f, 500);
            Gfx.Draw(fr, cx - fr.W / 2, faderBot + 6, Th.Sub);
        }

        // Right: Master FX Rack (dynamically spaced so all 6 sliders + 4 room pills ALWAYS fit cleanly!)
        Gfx.FillFrosted(fxBox, Th.Card, Gfx.Corner(12), opacity: 0.44f, border: true);
        float fx = fxBox.X + 12, fw = fxBox.W - 24;
        float rowStride = Math.Clamp((fxBox.H - 42) / 6f, 30f, 38f);
        float fyCur = fxBox.Y + 10;

        float DrawFxSlider(string key, string name, float val, float min, float max, float defVal, string fmt, bool enabled = true, bool affectsSpeed = false)
        {
            var hit = new R(fx, fyCur, fw, MathF.Min(32, rowStride));
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

            Gfx.Draw(name, fx, fyCur, 11.5f, enabled ? Th.Text : Th.Dim, 500, fw - 56);
            var vr = Gfx.Text(fmt, 11f, 600);
            Gfx.Draw(vr, fx + fw - vr.W, fyCur, enabled ? (MathF.Abs(val - defVal) > 0.01f ? Th.Brand : Th.Sub) : Th.Dim);

            float ty = fyCur + 19;
            float pct = Math.Clamp((val - min) / MathF.Max(0.001f, max - min), 0f, 1f);
            UI.Progress(new R(fx, ty, fw, drag || over ? 5f : 4f), pct, Th.A(0.18f), enabled && s.AudioFxEnabled ? Th.Brand : Th.Dim);
            Gfx.Circle(fx + fw * pct, ty + (drag || over ? 2.5f : 2f), drag || over ? 5.5f : 4.2f, enabled ? Th.Text : Th.Dim);

            fyCur += rowStride;
            return val;
        }

        s.FxReverb = DrawFxSlider("rev", L.T("Реверберация (Reverb)", "Reverb"), s.FxReverb, 0, 100, 0, $"{s.FxReverb:0}%");

        float rpx = fx, rbw = (fw - 12) / AudioDsp.Rooms.Length;
        for (int i = 0; i < AudioDsp.Rooms.Length; i++)
        {
            var (rk, _) = AudioDsp.Rooms[i];
            var rl = AudioDsp.RoomLabel(rk);
            var rr = new R(rpx, fyCur, rbw, 22);
            bool on = s.FxReverbRoom == rk;
            Gfx.Fill(rr, on ? Gfx.Fade(Th.Brand, 0.25f) : Th.A(0.07f), Gfx.Corner(6));
            if (on) Gfx.Stroke(rr, Gfx.Fade(Th.Brand, 0.65f), Gfx.Corner(6), 1f);
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
        fyCur += 24;

        s.FxBass = DrawFxSlider("bass", L.T("Бас / Суб-удар", "Sub-Bass Punch"), s.FxBass, 0, 100, 0, $"{s.FxBass:0}%");
        s.FxExciter = DrawFxSlider("exc", L.T("Воздух / Яркость + Компрессор", "Air / Brilliance + Glue"), s.FxExciter, 0, 100, 0, $"{s.FxExciter:0}%");
        s.FxStereo = DrawFxSlider("st", L.T("Сцена / 3D Stereo", "3D Stereo Width"), s.FxStereo, 0, 100, 0, $"{s.FxStereo:0}%");
        s.FxSpeed = DrawFxSlider("spd", L.T("Скорость (Nightcore / Slowed)", "Speed (Nightcore / Slowed)"), s.FxSpeed, 0.70f, 1.45f, 1.0f, $"{s.FxSpeed:0.00}×", enabled: true, affectsSpeed: true);
        var stUnit = L.T(" пт", " st");
        s.FxPitchSemitones = DrawFxSlider("pit", s.FxTapePitch ? L.T("Тон привязан к скорости", "Tape-linked pitch") : L.T("Высота тона (полутона)", "Pitch shift (semitones)"),
            s.FxPitchSemitones, -6f, 6f, 0f, s.FxTapePitch ? "Auto" : (s.FxPitchSemitones >= 0 ? $"+{s.FxPitchSemitones:0.#}{stUnit}" : $"{s.FxPitchSemitones:0.#}{stUnit}"),
            enabled: !s.FxTapePitch);

        // ---- Clean Non-Overlapping Footer Studio Toggles ----
        float footY = panel.Bottom - 36;
        var curVis = VisStyles.FirstOrDefault(v => v.Key == s.VisualizerStyle);
        var visName = curVis.Ru != null ? L.T(curVis.Ru, curVis.En) : L.T("Волны", "Waves");
        var visLbl = L.T("Визуал: ", "Visual: ") + visName;
        var vw = UI.PillWidth(null, visLbl) - 10;
        var vrBtn = new R(panel.Right - pad - vw, footY, vw, 26);
        Gfx.Fill(vrBtn, Th.A(0.08f), Gfx.Corner(13));
        UI.Glow(UI.Id("dsp:vis"), vrBtn, Gfx.Corner(13), UI.Hover(vrBtn));
        var vtr = Gfx.Text(visLbl, 11f, 500);
        Gfx.Draw(vtr, vrBtn.X + (vrBtn.W - vtr.W) / 2, vrBtn.Y + (vrBtn.H - vtr.H) / 2, Th.Text);
        if (UI.Click(UI.Id("dsp:vis"), vrBtn))
        {
            int curIdx = Array.FindIndex(VisStyles, v => v.Key == s.VisualizerStyle);
            s.VisualizerStyle = VisStyles[(curIdx + 1) % VisStyles.Length].Key;
            s.Save();
            App.Invalidate();
        }

        float bx = panel.X + pad;
        float footMaxX = vrBtn.X - 8;
        void MiniToggle(string id, string label, bool on, Action click)
        {
            var tw = UI.PillWidth(null, label) - 10;
            if (bx + tw > footMaxX) return;
            var r = new R(bx, footY, tw, 26);
            float rad = Gfx.Corner(13);
            Gfx.Fill(r, on ? Gfx.Fade(Th.Brand, 0.24f) : Th.A(0.08f), rad);
            if (on) Gfx.Stroke(r, Gfx.Fade(Th.Brand, 0.65f), rad, 1f);
            else UI.Glow(UI.Id(id), r, rad, UI.Hover(r));
            var tr = Gfx.Text(label, 11f, on ? 600 : 500);
            Gfx.Draw(tr, r.X + (r.W - tr.W) / 2, r.Y + (r.H - tr.H) / 2, on ? Th.Brand : Th.Sub);
            if (UI.Click(UI.Id(id), r)) click();
            bx += tw + 6;
        }

        MiniToggle("dsp:tape", L.T("Тон=Скорость", "Tape pitch"), s.FxTapePitch, () => { s.FxTapePitch = !s.FxTapePitch; AudioDsp.MarkCustom(s); ApplyNow(syncSpeed: true); });
        MiniToggle("dsp:8d", "8D", s.PlayerAudio8D, () => { s.PlayerAudio8D = !s.PlayerAudio8D; AudioDsp.MarkCustom(s); ApplyNow(); });
        MiniToggle("dsp:lim", L.T("Лимитер", "Limiter"), s.FxLimiter, () => { s.FxLimiter = !s.FxLimiter; ApplyNow(); });
        MiniToggle("dsp:audioonly", L.T("Только звук", "Audio only"), s.AudioOnlyMode, () => Shell.ToggleAudioOnlyMode());
        MiniToggle("dsp:scwave", L.T("Комменты / Волна", "Comments / Wave"), s.SoundCloudWave, () => { s.SoundCloudWave = !s.SoundCloudWave; s.Save(); Shell.PushPlayerState(); App.Invalidate(); });

        Gfx.PopMotionBlur(mb);
        UI.Modal = savedModal;
    }
}
