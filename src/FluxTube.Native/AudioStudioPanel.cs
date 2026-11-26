using System.Globalization;
using System.IO;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Pro 2-Tab Frosted-Glass Audio Studio &amp; Interactive Virtual DJ Deck:
/// - Tab 0 ("Студия &amp; Реверб"): Live WASAPI spectrum behind draggable Catmull-Rom 10-band EQ spline,
///   30 mastering &amp; creative presets across 6 categories + custom saved presets, True Stereo FFT Convolution Reverb
///   (8 spaces incl. EMT Plate, Lexicon 480L, IMAX Cinema, Shimmer, Sony PS1 SPU, 80s Tape + Custom .wav IR loader),
///   and 3D BS2B / 8D / Limiter / Tape Pitch toggles.
/// - Tab 1 ("DJ Пульт"): Spinning interactive SL-1200 vinyl platter with circular album art, grab-and-drag live
///   scratching/jogging, SL-1200 Vinyl Brake spin-down/up, 4 colored Hot Cue pads per track, 3-band Pioneer DJM
///   Isolator Kill switches, instant Beat-Repeat Loop pads (1/4s..2s), Hold 2× / 0.5× triggers, and live DJ FX
///   faders (Bi-Polar Resonant Filter LPF←0→HPF, PS1/SP-1200 Bitcrusher, Tape/Tube Drive, Jet Flanger, Tempo Fader).
/// </summary>
public static class AudioStudioPanel
{
    public static bool Open { get; private set; }
    public static R PanelRect { get; private set; }

    private static int _tab; // 0 = DSP Studio & Reverb, 1 = Virtual DJ Deck
    private static int _dragBand = -1;
    private static int _dragCurveBand = -1;
    private static string? _dragKnob;
    private static string _cat = "all";
    private static bool _savingPreset;
    private static Edit? _presetNameEdit;
    private static long _lastApplyAt, _saveDueAt, _openedAt;
    private static bool _pendingApply, _pendingSpeed;

    // ---- Virtual DJ Deck State ----
    private static float _platterAngle;
    private static long _lastFrameMs;
    private static bool _scratching;
    private static float _scratchLastAngle;
    private static float _scratchVel;
    private static float _scratchBaseSpeed = 1f;
    private static double _scratchPos;
    private static long _lastScratchCmdMs;
    private static bool _scratchWasPlaying;

    // SL-1200 Vinyl Brake & Backspin smooth speed ramp
    private static int _brakeState; // 0 = off, 1 = braking down, 2 = braked (paused), 3 = spinning back up, 4 = backspin zip
    private static long _brakeStartMs;
    private static float _brakeStartSpeed = 1f;
    private static float _brakeTargetSpeed = 1f;
    private static long _lastBrakeCmdMs;
    private static bool _backspinJumped;

    // Hold 2x / 0.5x momentary pitch-speed pads
    private static int _holdMode; // 0 = none, 1 = 2x, -1 = 0.5x
    private static float _holdBaseSpeed = 1f;

    // Hot Cues & Beat-Repeat Loop (auto-cleared on track switch)
    private static readonly Dictionary<string, double?[]> _hotCues = new(StringComparer.Ordinal);
    private static string? _lastDjVid;
    private static double _activeLoopLen;

    private static readonly uint[] CueColors = [0xFFFF4D94, 0xFF2EDBFF, 0xFFFFB830, 0xFF39E68B];

    public static readonly (string Key, string Ru, string En)[] VisStyles =
    [
        ("bars_wave", "Спектр+", "Spectrum+"),
        ("cyber", "Кибер", "Cyber"),
        ("bars", "LED-спектр", "LED Bars"),
        ("orbit", "Неон-волны", "Neon Waves"),
        ("matrix", "Матрица", "Matrix"),
        ("pulse", "Плазма", "Plasma"),
        ("ribbon", "Шёлк", "Silk"),
        ("off", "Выкл", "Off"),
    ];

    public static string CurrentVisualizerName(AppSettings s)
    {
        var cur = VisStyles.FirstOrDefault(v => v.Key == s.VisualizerStyle);
        return cur.Ru != null ? L.T(cur.Ru, cur.En) : L.T("Спектр+", "Spectrum+");
    }

    public static void CycleVisualizerStyle(AppSettings s)
    {
        int curIdx = Array.FindIndex(VisStyles, v => v.Key == s.VisualizerStyle);
        s.VisualizerStyle = VisStyles[(curIdx + 1) % VisStyles.Length].Key;
        s.Save();
        App.Invalidate();
    }

    public static bool IsDjFxActive(AppSettings s) =>
        _activeLoopLen > 0 || _brakeState != 0 || _holdMode != 0
        || (s.AudioFxEnabled && (MathF.Abs(s.FxDjFilter) > 1.5f || s.FxCrush > 1.5f || s.FxDrive > 1.5f
            || s.FxMod > 1.5f || s.FxKillLow || s.FxKillMid || s.FxKillHigh));

    public static void Toggle()
    {
        if (Open && _tab == 0) Close();
        else ShowTab(0);
    }

    public static void ToggleDj()
    {
        if (Open && _tab == 1) Close();
        else ShowTab(1);
    }

    public static void Show() => ShowTab(0);
    public static void ShowDj() => ShowTab(1);

    private static void ShowTab(int tab)
    {
        AccountMenu.Close();
        SharePanel.Close();
        ThemeCodeEditor.Close();
        LoginModal.Close();
        AudioDsp.EnsureEqArray(AppSettings.Current);
        _tab = tab;
        if (!Open) _openedAt = UI.Now;
        Open = true;
        _dragBand = -1;
        _dragCurveBand = -1;
        _dragKnob = null;
        _scratching = false;
        _savingPreset = false;
        _presetNameEdit?.Hide();
        Sfx.Tab();
        App.Invalidate();
    }

    public static void Close()
    {
        if (!Open) return;
        ReleaseHoldIfNeeded();
        EndScratchIfNeeded();
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
        if (syncSpeed) Shell.PushPlayerState();
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

    private static void ClearAbLoop(MpvPlayer mpv)
    {
        _activeLoopLen = 0;
        if (!mpv.IsReady) return;
        mpv.Command("set", "ab-loop-a", "no");
        mpv.Command("set", "ab-loop-b", "no");
    }

    private static void ToggleBeatLoop(MpvPlayer mpv, double lenSec)
    {
        if (!mpv.IsReady || !mpv.HasMedia) return;
        if (Math.Abs(_activeLoopLen - lenSec) < 0.01)
        {
            ClearAbLoop(mpv);
            Sfx.Back();
            Shell.Toast(L.T("Петля выключена", "Loop exit"), Th.Sub, 1200);
            return;
        }
        double a = Math.Max(0, mpv.TimePos);
        double b = mpv.Duration > 0 ? Math.Min(mpv.Duration - 0.05, a + lenSec) : a + lenSec;
        if (b <= a + 0.08) return;
        _activeLoopLen = lenSec;
        mpv.Command("set", "ab-loop-a", a.ToString("0.###", CultureInfo.InvariantCulture));
        mpv.Command("set", "ab-loop-b", b.ToString("0.###", CultureInfo.InvariantCulture));
        Sfx.Tick();
        Shell.Toast($"LOOP {FormatLoopLen(lenSec)}", Th.Brand, 1400);
    }

    private static string FormatLoopLen(double len) => len switch
    {
        <= 0.16 => "1/8s",
        <= 0.30 => "1/4s",
        <= 0.60 => "1/2s",
        <= 1.10 => "1s",
        _ => "2s"
    };

    private static void TriggerBackspin(MpvPlayer mpv, AppSettings s)
    {
        if (!mpv.IsReady || !mpv.HasMedia) return;
        _brakeStartSpeed = (float)Math.Clamp(mpv.Speed, 0.4, 2.0);
        _brakeTargetSpeed = s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 1.6f) : 1.0f;
        _brakeStartMs = UI.Now;
        _lastBrakeCmdMs = 0;
        _backspinJumped = false;
        _brakeState = 4;
        if (mpv.Paused) mpv.SetPause(false);
        mpv.Command("set", "audio-pitch-correction", "no");
        mpv.SetSpeed(2.45); // instant vinyl whip-up!
        Sfx.Tab();
    }

    private static void TriggerVinylBrake(MpvPlayer mpv, AppSettings s)
    {
        if (!mpv.IsReady || !mpv.HasMedia) return;
        if (_brakeState == 0 || _brakeState == 3)
        {
            if (mpv.Paused)
            {
                // Already paused -> spin up cleanly!
                _brakeTargetSpeed = s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 1.6f) : 1.0f;
                _brakeStartSpeed = 0.16f;
                _brakeStartMs = UI.Now;
                _brakeState = 3;
                mpv.Command("set", "audio-pitch-correction", "no");
                mpv.SetSpeed(0.16);
                mpv.SetPause(false);
                Sfx.Tab();
                return;
            }
            // Spin down to pause like Technics SL-1200 power-off!
            _brakeStartSpeed = (float)Math.Clamp(mpv.Speed, 0.4, 1.8);
            _brakeTargetSpeed = s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 1.6f) : 1.0f;
            _brakeStartMs = UI.Now;
            _brakeState = 1;
            mpv.Command("set", "audio-pitch-correction", "no");
            Sfx.Back();
        }
        else
        {
            // Spin back up from brake!
            _brakeTargetSpeed = s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 1.6f) : 1.0f;
            _brakeStartSpeed = 0.16f;
            _brakeStartMs = UI.Now;
            _brakeState = 3;
            mpv.Command("set", "audio-pitch-correction", "no");
            mpv.SetSpeed(0.16);
            mpv.SetPause(false);
            Sfx.Tab();
        }
    }

    private static void UpdateVinylBrake(MpvPlayer mpv, AppSettings s)
    {
        if (_brakeState == 0 || _brakeState == 2) return;
        if (!mpv.IsReady || !mpv.HasMedia) { _brakeState = 0; return; }

        long now = UI.Now;
        if (_brakeState == 1)
        {
            float t = Math.Clamp((now - _brakeStartMs) / 680f, 0f, 1f);
            float ease = 1f - (1f - t) * (1f - t);
            float sp = MathF.Max(0.12f, _brakeStartSpeed * (1f - ease) + 0.12f * ease);
            if (now - _lastBrakeCmdMs >= 32 || t >= 1f)
            {
                _lastBrakeCmdMs = now;
                mpv.SetSpeed(sp);
            }
            UI.Animate();
            if (t >= 1f)
            {
                mpv.SetPause(true);
                _brakeState = 2;
            }
        }
        else if (_brakeState == 3)
        {
            float t = Math.Clamp((now - _brakeStartMs) / 520f, 0f, 1f);
            float ease = 1f - (1f - t) * (1f - t);
            float sp = _brakeStartSpeed + (_brakeTargetSpeed - _brakeStartSpeed) * ease;
            if (now - _lastBrakeCmdMs >= 32 || t >= 1f)
            {
                _lastBrakeCmdMs = now;
                mpv.SetSpeed(sp);
            }
            UI.Animate();
            if (t >= 1f)
            {
                _brakeState = 0;
                mpv.SetSpeed(_brakeTargetSpeed);
                mpv.Command("set", "audio-pitch-correction", (s.AudioFxEnabled && s.FxTapePitch) ? "no" : "yes");
            }
        }
        else if (_brakeState == 4)
        {
            // Signature DJ Vinyl Backspin: rapid pitch whip down -> jump back 1.8s -> crisp spin up!
            float t = Math.Clamp((now - _brakeStartMs) / 560f, 0f, 1f);
            if (t < 0.48f)
            {
                float k = t / 0.48f;
                float sp = 2.45f - 2.15f * k; // 2.45x -> 0.30x audible vinyl whip!
                _platterAngle = (_platterAngle - 0.52f) % MathF.Tau;
                if (now - _lastBrakeCmdMs >= 28) { _lastBrakeCmdMs = now; mpv.SetSpeed(sp); }
            }
            else
            {
                if (!_backspinJumped)
                {
                    _backspinJumped = true;
                    double backTo = Math.Max(0, mpv.TimePos - 1.8);
                    mpv.Command("seek", backTo.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
                }
                float k = (t - 0.48f) / 0.52f;
                float ease = 1f - (1f - k) * (1f - k);
                float sp = 0.30f + (_brakeTargetSpeed - 0.30f) * ease;
                if (now - _lastBrakeCmdMs >= 28 || t >= 1f) { _lastBrakeCmdMs = now; mpv.SetSpeed(sp); }
            }
            UI.Animate();
            if (t >= 1f)
            {
                _brakeState = 0;
                mpv.SetSpeed(_brakeTargetSpeed);
                mpv.Command("set", "audio-pitch-correction", (s.AudioFxEnabled && s.FxTapePitch) ? "no" : "yes");
            }
        }
    }

    private static void ReleaseHoldIfNeeded()
    {
        if (_holdMode == 0) return;
        _holdMode = 0;
        var mpv = MpvPlayer.Instance;
        var s = AppSettings.Current;
        if (mpv.IsReady)
        {
            mpv.SetSpeed(_holdBaseSpeed);
            mpv.Command("set", "audio-pitch-correction", (s.AudioFxEnabled && s.FxTapePitch) ? "no" : "yes");
        }
    }

    private static void EndScratchIfNeeded()
    {
        if (!_scratching) return;
        _scratching = false;
        _scratchVel = 0f;
        var mpv = MpvPlayer.Instance;
        var s = AppSettings.Current;
        if (mpv.IsReady && mpv.HasMedia)
        {
            mpv.SetSpeed(_scratchBaseSpeed);
            mpv.Command("set", "audio-pitch-correction", (s.AudioFxEnabled && s.FxTapePitch) ? "no" : "yes");
            if (!_scratchWasPlaying) mpv.SetPause(true);
        }
    }

    private static void PickCustomIr(AppSettings s)
    {
        App.Post(() => Dialogs.PickFileAsync(
            "Impulse Response (*.wav;*.flac;*.mp3)\0*.wav;*.flac;*.mp3\0All files\0*.*\0\0",
            file =>
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
                _ = Task.Run(() =>
                {
                    ImpulseBank.PrepareCustomIr(file);
                    App.Post(() =>
                    {
                        s.CustomIrPath = file;
                        s.FxReverbRoom = "custom";
                        if (s.FxReverb < 8f) s.FxReverb = 36f;
                        AudioDsp.MarkCustom(s);
                        ApplyNow();
                        Shell.Toast(L.T("Загружен IR: ", "Loaded IR: ") + Path.GetFileName(file), Th.Good, 2400);
                        App.Invalidate();
                    });
                });
            }));
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
        var mpv = MpvPlayer.Instance;
        var s = AppSettings.Current;

        // Track switch cleanup (always clear stale AB-loop and vinyl brake)
        if (_lastDjVid != mpv.CurrentVideoId)
        {
            if (_activeLoopLen > 0) ClearAbLoop(mpv);
            _brakeState = 0;
            _holdMode = 0;
            _scratching = false;
            _lastDjVid = mpv.CurrentVideoId;
        }

        // Keep Vinyl Brake animation running even if user closes the panel mid-brake!
        UpdateVinylBrake(mpv, s);

        if (!Open)
        {
            PanelRect = default;
            if (_saveDueAt > 0 && UI.Now >= _saveDueAt) { _saveDueAt = 0; AppSettings.Current.Save(); }
            return;
        }

        if (!UI.Down)
        {
            if (_dragBand >= 0 || _dragCurveBand >= 0 || _dragKnob != null)
            {
                _dragBand = -1;
                _dragCurveBand = -1;
                _dragKnob = null;
                if (_pendingApply) ApplyNow(_pendingSpeed);
            }
            ReleaseHoldIfNeeded();
            EndScratchIfNeeded();
        }
        if (_pendingApply && UI.Now - _lastApplyAt >= 70) ApplyNow(_pendingSpeed);
        if (_saveDueAt > 0 && UI.Now >= _saveDueAt && !UI.Down) { _saveDueAt = 0; AppSettings.Current.Save(); }

        AudioDsp.EnsureEqArray(s);
        AudioAnalyzer.RequestFrame();

        float w = Gfx.W, h = Gfx.H;
        float pw = MathF.Min(920, w - 36);
        float ph = MathF.Min(680, h - 36);
        float ot = Math.Clamp((UI.Now - _openedAt) / 145f, 0f, 1f);
        if (ot < 1f) UI.Animate();
        float oEase = 1f - (1f - ot) * (1f - ot);
        float dy = (1f - oEase) * 14f;
        float velY = ot < 1f ? -2f * (1f - ot) * 14f / 0.145f : 0f;
        var panel = new R(Gfx.Snap((w - pw) / 2), Gfx.Snap((h - ph) / 2 + dy), pw, ph);
        PanelRect = panel;

        // Dim backdrop — clicking outside closes the studio rack
        Gfx.Fill(new R(0, 0, w, h), Gfx.Fade(0xFF000000, (Th.Light ? 0.28f : 0.60f) * oEase));
        if (UI.Pressed && !panel.Contains(UI.MX, UI.MY) && !Menus.Open) { Close(); return; }

        var savedModal = UI.Modal;
        UI.Modal = panel;

        bool mb = Gfx.PushMotionBlur(panel.Inset(-8), 0f, velY);
        float pRad = Gfx.Corner(16);
        Gfx.Fill(panel.Inset(-4), Gfx.Fade(0xFF000000, 0.38f * oEase), pRad + 4);
        Gfx.FillFrosted(panel, Th.Panel, pRad, opacity: 0.96f, border: true);
        if (s.UiNeonGlow) Gfx.NeonGlow(panel, pRad, Th.Brand, 0.35f);

        // ---- Header: 2-Tab Switcher (Studio & Reverb | DJ Deck) + Preset Badge + Global Controls ----
        float pad = 18;
        float y = panel.Y + 14;
        float hx = panel.X + pad;

        void DrawHeaderTab(int idx, string icon, string label)
        {
            bool on = _tab == idx;
            float tw = UI.PillWidth(icon, label);
            var tr = new R(hx, y + 1, tw, 32);
            float rad = Gfx.Corner(16);
            Gfx.Fill(tr, on ? Th.Brand : Th.A(0.08f), rad);
            if (!on) UI.Glow(UI.Id("dsp:tab", idx), tr, rad, UI.Hover(tr));
            Gfx.Icon(icon, new R(tr.X + 10, tr.Y, 16, tr.H), 13.5f, on ? Th.OnBrand : Th.Sub);
            var lblRun = Gfx.Text(label, 12.5f, on ? 700 : 500);
            Gfx.Draw(lblRun, tr.X + 30, tr.Y + (tr.H - lblRun.H) * 0.5f, on ? Th.OnBrand : Th.Text);
            if (UI.Click(UI.Id("dsp:tab", idx), tr))
            {
                _tab = idx;
                _savingPreset = false;
                _presetNameEdit?.Hide();
                Sfx.Tab();
            }
            hx += tw + 7;
        }

        DrawHeaderTab(0, Th.Headphones, L.T("Студия & Реверб", "Studio & Reverb"));
        DrawHeaderTab(1, "\uE958", L.T("DJ Пульт", "DJ Deck"));

        float rx = panel.Right - pad;
        if (UI.IconButton(UI.Id("dsp:close"), new R(rx - 32, y + 1, 32, 32), Th.Close, 12, Th.Sub)) { Gfx.PopMotionBlur(mb); Close(); UI.Modal = savedModal; return; }
        rx -= 38;

        var resetLbl = L.T("Сброс", "Reset");
        var resetW = UI.PillWidth(Th.Restart, resetLbl);
        rx -= resetW;
        if (UI.Pill(UI.Id("dsp:reset"), rx, y + 1, 32, Th.Restart, resetLbl, out _))
        {
            ClearAbLoop(mpv);
            _brakeState = 0;
            _holdMode = 0;
            AudioDsp.ApplyPreset(s, "Flat");
            ApplyNow(syncSpeed: true);
            Shell.Toast(L.T("DSP и DJ пульт сброшены", "DSP & DJ Deck reset"), Th.Sub, 1800);
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

        // Preset badge between tabs and right controls if space permits
        var presetBadge = AudioDsp.DisplayPresetName(s);
        var bRun = Gfx.Text(presetBadge, 11f, 600);
        if (hx + bRun.W + 22 < rx - 6)
        {
            var bRect = new R(hx + 2, y + 6, bRun.W + 16, 22);
            Gfx.Fill(bRect, s.AudioFxEnabled ? Gfx.Fade(Th.Brand, 0.20f) : Th.A(0.08f), Gfx.Corner(11));
            Gfx.Stroke(bRect, s.AudioFxEnabled ? Gfx.Fade(Th.Brand, 0.50f) : Th.Line, Gfx.Corner(11), 1f);
            Gfx.Draw(bRun, bRect.X + 8, bRect.Y + (bRect.H - bRun.H) / 2, s.AudioFxEnabled ? Th.Brand : Th.Sub);
        }

        y += 40;

        // Optional inline "Save Custom Preset" bar (works from both Studio and DJ Deck!)
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

        if (_tab == 0) DrawStudioTab(panel, pad, y, s);
        else DrawDjDeckTab(panel, pad, y, s, mpv);

        // ---- Clean Non-Overlapping Footer Studio Toggles ----
        float footY = panel.Bottom - 36;
        var visLbl = L.T("Визуал: ", "Visual: ") + CurrentVisualizerName(s);
        var vw = UI.PillWidth(null, visLbl) - 10;
        var vrBtn = new R(panel.Right - pad - vw, footY, vw, 26);
        Gfx.Fill(vrBtn, Th.A(0.08f), Gfx.Corner(13));
        UI.Glow(UI.Id("dsp:vis"), vrBtn, Gfx.Corner(13), UI.Hover(vrBtn));
        var vtr = Gfx.Text(visLbl, 11f, 500);
        Gfx.Draw(vtr, vrBtn.X + (vrBtn.W - vtr.W) / 2, vrBtn.Y + (vrBtn.H - vtr.H) / 2, Th.Text);
        if (UI.Click(UI.Id("dsp:vis"), vrBtn))
            CycleVisualizerStyle(s);

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
        MiniToggle("dsp:bs2b", "3D BS2B", s.FxHeadphone3D, () => { s.FxHeadphone3D = !s.FxHeadphone3D; AudioDsp.MarkCustom(s); ApplyNow(); });
        MiniToggle("dsp:8d", "8D", s.PlayerAudio8D, () => { s.PlayerAudio8D = !s.PlayerAudio8D; AudioDsp.MarkCustom(s); ApplyNow(); });
        MiniToggle("dsp:lim", L.T("Лимитер", "Limiter"), s.FxLimiter, () => { s.FxLimiter = !s.FxLimiter; ApplyNow(); });
        MiniToggle("dsp:audioonly", L.T("Только звук", "Audio only"), s.AudioOnlyMode, () => Shell.ToggleAudioOnlyMode());
        MiniToggle("dsp:scwave", L.T("Волна", "Wave"), s.SoundCloudWave, () => { s.SoundCloudWave = !s.SoundCloudWave; s.Save(); Shell.PushPlayerState(); App.Invalidate(); });

        Gfx.PopMotionBlur(mb);
        UI.Modal = savedModal;
    }

    // ==================== TAB 0: DSP STUDIO & TRUE CONVOLUTION REVERB ====================

    private static void DrawStudioTab(R panel, float pad, float y, AppSettings s)
    {
        // Interactive FabFilter-Style EQ Spline Graph + Live WASAPI Spectrum
        var vizR = new R(panel.X + pad, y, panel.W - pad * 2, 92);
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
        y += vizR.H + 8;

        // Category Tabs
        float cxCat = panel.X + pad;
        void CatTab(string key, string label)
        {
            bool on = _cat == key;
            var tw = UI.PillWidth(null, label) - 12;
            var cr = new R(cxCat, y, tw, 24);
            Gfx.Fill(cr, on ? Gfx.Fade(Th.Brand, 0.22f) : Th.A(0.06f), Gfx.Corner(12));
            if (on) Gfx.Stroke(cr, Gfx.Fade(Th.Brand, 0.55f), Gfx.Corner(12), 1f);
            else UI.Glow(UI.Id("dsp:cat:" + key), cr, Gfx.Corner(12), UI.Hover(cr));
            var tr = Gfx.Text(label, 11f, on ? 600 : 500);
            Gfx.Draw(tr, cr.X + (cr.W - tr.W) / 2, cr.Y + (cr.H - tr.H) / 2, on ? Th.Brand : Th.Sub);
            if (UI.Click(UI.Id("dsp:cat:" + key), cr)) _cat = key;
            cxCat += tw + 5;
        }
        foreach (var (ck, cru, cen) in AudioDsp.Categories) CatTab(ck, L.T(cru, cen));
        CatTab("saved", L.T($"Мои ({s.CustomAudioPresets.Count})", $"Saved ({s.CustomAudioPresets.Count})"));
        y += 29;

        // Preset Strip (bounded to 3 crisp rows so EQ & Convolution Reverb rack always has full breathing room)
        float px = panel.X + pad, maxRight = panel.Right - pad;
        float rowH = 25;
        int presetRows = 0;
        const int maxPresetRows = 3;

        void DrawPresetPill(int uid, string lbl, bool active, Action onClick, Action? onRightClick = null)
        {
            if (presetRows >= maxPresetRows) return;
            var pwBtn = UI.PillWidth(null, lbl) - 12;
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
            if (active && s.UiNeonGlow) Gfx.NeonGlow(btnR, br, Th.Brand, 0.45f);
            if (!active) UI.Glow(uid, btnR, br, UI.Hover(btnR));
            var lr = Gfx.Text(lbl, 11f, active ? 600 : 500);
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
                    panel.X + pad, y + 4, 12f, Th.Sub);
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

        // Main Split: Left 10-Band EQ Faders | Right Master FX & Convolution Reverb Rack
        float bodyH = MathF.Max(160, panel.Bottom - y - 46);
        float eqW = MathF.Round((panel.W - pad * 2 - 14) * 0.54f);
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

        // Right: Master FX & True Convolution Reverb Rack (8 rooms in 2 crisp rows of 4!)
        Gfx.FillFrosted(fxBox, Th.Card, Gfx.Corner(12), opacity: 0.44f, border: true);
        float fx = fxBox.X + 12, fw = fxBox.W - 24;
        float roomsH = 52f;
        float rowStride = Math.Clamp((fxBox.H - roomsH - 18f) / 6f, 28f, 36f);
        float fyCur = fxBox.Y + 9f;

        string revLabel = s.FxReverbRoom == "custom" && !string.IsNullOrWhiteSpace(s.CustomIrPath)
            ? L.T("Реверб IR: ", "IR Reverb: ") + Path.GetFileNameWithoutExtension(s.CustomIrPath)
            : L.T("Конволюция · Реверб (IR)", "Convolution Reverb (IR)");
        s.FxReverb = DrawFxSlider(s, "rev", revLabel, s.FxReverb, 0, 100, 0, $"{s.FxReverb:0}%", fx, fw, ref fyCur, rowStride);

        // 8 Convolution Spaces in 2 clean rows of 4 pills (Studio, Plate, 480L Hall, Cinema / Shimmer, PS1 SPU, 80s Tape, Custom IR…)
        const int perRow = 4;
        float rbw = (fw - (perRow - 1) * 4f) / perRow;
        for (int i = 0; i < AudioDsp.Rooms.Length; i++)
        {
            int row = i / perRow, col = i % perRow;
            var (rk, _, _) = AudioDsp.Rooms[i];
            var rl = AudioDsp.RoomLabel(rk);
            var rr = new R(fx + col * (rbw + 4f), fyCur + row * 24f, rbw, 21f);
            bool on = s.FxReverbRoom == rk;
            Gfx.Fill(rr, on ? Gfx.Fade(Th.Brand, 0.25f) : Th.A(0.07f), Gfx.Corner(6));
            if (on) Gfx.Stroke(rr, Gfx.Fade(Th.Brand, 0.65f), Gfx.Corner(6), 1f);
            else UI.Glow(UI.Id("dsp:room", i), rr, Gfx.Corner(6), UI.Hover(rr));
            var lr = Gfx.Text(rl, 10f, on ? 600 : 500, rbw - 6f);
            Gfx.Draw(lr, rr.X + (rr.W - lr.W) / 2, rr.Y + (rr.H - lr.H) / 2, on ? Th.Brand : Th.Sub);

            if (rk == "custom" && UI.RightClick(rr)) PickCustomIr(s);
            else if (UI.Click(UI.Id("dsp:room", i), rr))
            {
                if (rk == "custom" && (string.IsNullOrWhiteSpace(s.CustomIrPath) || !File.Exists(s.CustomIrPath) || on))
                {
                    PickCustomIr(s);
                }
                else
                {
                    s.FxReverbRoom = rk;
                    if (s.FxReverb < 8f) s.FxReverb = 32f;
                    AudioDsp.MarkCustom(s);
                    ApplyNow();
                }
            }
        }
        fyCur += 51f;

        s.FxBass = DrawFxSlider(s, "bass", L.T("Суб-Бас (MaxxBass Гармоники)", "Sub-Bass (MaxxBass Harmonics)"), s.FxBass, 0, 100, 0, $"{s.FxBass:0}%", fx, fw, ref fyCur, rowStride);
        s.FxExciter = DrawFxSlider(s, "exc", L.T("Воздух · Exciter + OTT (50%+)", "Air · Exciter + OTT (50%+)"), s.FxExciter, 0, 100, 0, $"{s.FxExciter:0}%", fx, fw, ref fyCur, rowStride);
        s.FxStereo = DrawFxSlider(s, "st", L.T("Сцена / 3D Stereo", "3D Stereo Width"), s.FxStereo, 0, 100, 0, $"{s.FxStereo:0}%", fx, fw, ref fyCur, rowStride);
        s.FxSpeed = DrawFxSlider(s, "spd", L.T("Скорость (Nightcore / Slowed)", "Speed (Nightcore / Slowed)"), s.FxSpeed, 0.50f, 1.60f, 1.0f, $"{s.FxSpeed:0.00}×", fx, fw, ref fyCur, rowStride, enabled: true, affectsSpeed: true);
        var stUnit = L.T(" пт", " st");
        s.FxPitchSemitones = DrawFxSlider(s, "pit", s.FxTapePitch ? L.T("Тон привязан к скорости", "Tape-linked pitch") : L.T("Высота тона (полутона)", "Pitch shift (semitones)"),
            s.FxPitchSemitones, -6f, 6f, 0f, s.FxTapePitch ? "Auto" : (s.FxPitchSemitones >= 0 ? $"+{s.FxPitchSemitones:0.#}{stUnit}" : $"{s.FxPitchSemitones:0.#}{stUnit}"),
            fx, fw, ref fyCur, rowStride, enabled: !s.FxTapePitch);
    }

    // ==================== TAB 1: INTERACTIVE VIRTUAL DJ TURNTABLE & FX DECK ====================

    private static void DrawDjDeckTab(R panel, float pad, float y, AppSettings s, MpvPlayer mpv)
    {
        long now = UI.Now;
        float dt = _lastFrameMs == 0 ? 0.016f : Math.Clamp((now - _lastFrameMs) / 1000f, 0.001f, 0.05f);
        _lastFrameMs = now;

        bool playing = mpv.HasMedia && !mpv.Paused;
        float liveSpd = (float)Math.Clamp(mpv.Speed, 0.12, 2.5);
        if (playing && !_scratching)
        {
            _platterAngle = (_platterAngle + dt * 3.49f * liveSpd) % (MathF.PI * 2f); // 33.3 RPM = 3.49 rad/s
            UI.Animate();
        }
        else if (_scratching || _brakeState != 0) UI.Animate();

        float kick = MathF.Max(MusicBounce.Kick, AudioAnalyzer.BeatMotionStrength);
        float bass = AudioAnalyzer.BassEnergy;

        // 1. Top Live DJ Status & Spectrum Strip
        var stripR = new R(panel.X + pad, y, panel.W - pad * 2, 66);
        Gfx.Fill(stripR, Gfx.Fade(0xFF07070D, Th.Light ? 0.18f : 0.84f), Gfx.Corner(12));
        Gfx.Stroke(stripR, Gfx.Fade(Th.Brand, 0.25f + kick * 0.45f), Gfx.Corner(12), 1.1f);
        Gfx.PushClip(stripR.Inset(1));
        AudioAnalyzer.RequestFrame(force: true);
        AudioAnalyzer.DrawStageVisualizer(stripR, "bars_wave", opacity: 0.56f, gain: 1.1f);

        // Beat LED + Track Title + Live Readout
        uint ledCol = playing ? Gfx.Mix(Th.Brand, 0xFFFFFFFF, Math.Clamp(kick, 0f, 1f)) : Th.Dim;
        Gfx.Circle(stripR.X + 20, stripR.Y + 22, 6f + kick * 2.5f, Gfx.Fade(ledCol, 0.28f));
        Gfx.Circle(stripR.X + 20, stripR.Y + 22, 4.5f, ledCol);

        var trackTitle = PlaybackTracker.Instance.Current?.Title is { Length: > 0 } pt ? pt
            : (Shell.Current is Pages.WatchPage wp && wp.Title.Length > 0 ? wp.Title : L.T("Включите трек для DJ пульта", "Play any track to use the DJ Deck"));
        Gfx.Draw(trackTitle, stripR.X + 34, stripR.Y + 12, 13.5f, 0xFFFFFFFF, 700, stripR.W - 290f);

        string subInfo = mpv.HasMedia
            ? $"{Fmt.Time(_scratching ? _scratchPos : mpv.TimePos)} / {Fmt.Time(mpv.Duration)}  ·  {liveSpd:0.00}×  ·  {(s.FxTapePitch ? L.T("Винил-тон", "Vinyl pitch") : L.T("Мастер-темп", "Master tempo"))}"
            : L.T("Крутите пластинку мышкой · колёсико меняет темп · ПКМ сбрасывает фейдеры", "Drag vinyl to scratch · wheel nudges tempo · right-click resets faders");
        Gfx.Draw(subInfo, stripR.X + 34, stripR.Y + 34, 11.5f, Gfx.Fade(0xFFFFFFFF, 0.72f), 500, stripR.W - 290f);

        // Right badges: LOOP status + SCRATCH status + BPM/Speed pill
        float srx = stripR.Right - 14f;
        var spdTxt = $"{liveSpd:0.00}×";
        var spdRun = Gfx.Text(spdTxt, 14f, 700);
        var spdBox = new R(srx - spdRun.W - 18f, stripR.Y + 16f, spdRun.W + 18f, 34f);
        Gfx.Fill(spdBox, Gfx.Fade(Th.Brand, 0.24f), 8f);
        Gfx.Stroke(spdBox, Gfx.Fade(Th.Brand, 0.65f), 8f, 1f);
        Gfx.Draw(spdRun, spdBox.X + 9f, spdBox.Y + (spdBox.H - spdRun.H) * 0.5f, 0xFFFFFFFF);
        srx = spdBox.X - 8f;

        if (_activeLoopLen > 0)
        {
            var lpTxt = "LOOP " + FormatLoopLen(_activeLoopLen);
            var lpRun = Gfx.Text(lpTxt, 11.5f, 700);
            var lpBox = new R(srx - lpRun.W - 16f, stripR.Y + 20f, lpRun.W + 16f, 26f);
            Gfx.Fill(lpBox, Gfx.Fade(0xFF39E68B, 0.24f), 7f);
            Gfx.Stroke(lpBox, 0xFF39E68B, 7f, 1f);
            Gfx.Draw(lpRun, lpBox.X + 8f, lpBox.Y + (lpBox.H - lpRun.H) * 0.5f, 0xFF39E68B);
            srx = lpBox.X - 8f;
        }
        if (_scratching)
        {
            var scRun = Gfx.Text("SCRATCH", 11.5f, 700);
            var scBox = new R(srx - scRun.W - 16f, stripR.Y + 20f, scRun.W + 16f, 26f);
            Gfx.Fill(scBox, Gfx.Fade(0xFFFFB830, 0.26f), 7f);
            Gfx.Stroke(scBox, 0xFFFFB830, 7f, 1f);
            Gfx.Draw(scRun, scBox.X + 8f, scBox.Y + (scBox.H - scRun.H) * 0.5f, 0xFFFFB830);
        }
        Gfx.PopClip();
        y += stripR.H + 12;

        // 2. Main 2-Column DJ Console
        float bodyH = MathF.Max(280, panel.Bottom - y - 46);
        float leftW = Math.Clamp(panel.W * 0.36f, 276f, 326f);
        var deckBox = new R(panel.X + pad, y, leftW, bodyH);
        var mixerBox = new R(deckBox.Right + 14f, y, panel.Right - pad - (deckBox.Right + 14f), bodyH);

        // ---- LEFT: SL-1200 SPINNING VINYL PLATTER & TRANSPORT ----
        Gfx.FillFrosted(deckBox, Th.Card, Gfx.Corner(14), opacity: 0.48f, border: true);

        float platterMax = MathF.Min(deckBox.W - 34f, deckBox.H - 122f);
        float platterD = Math.Clamp(platterMax, 168f, 232f);
        float cx = Gfx.Snap(deckBox.X + deckBox.W * 0.5f);
        float cy = Gfx.Snap(deckBox.Y + 24f + platterD * 0.5f);
        float rad = platterD * 0.5f;

        // Interactive Scratch/Jog + Wheel Tempo Nudge hit test on vinyl disc
        float dxM = UI.MX - cx, dyM = UI.MY - cy;
        float distM = MathF.Sqrt(dxM * dxM + dyM * dyM);
        bool overPlatter = distM <= rad + 4f && deckBox.Contains(UI.MX, UI.MY);
        if (overPlatter) UI.Hand();

        if (overPlatter && UI.Pressed && mpv.HasMedia)
        {
            _scratching = true;
            _scratchLastAngle = MathF.Atan2(dyM, dxM);
            _scratchPos = mpv.TimePos;
            _scratchVel = 0f;
            _scratchBaseSpeed = s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 1.6f) : 1.0f;
            _scratchWasPlaying = !mpv.Paused;
            _lastScratchCmdMs = 0;
            // Live audible vinyl touch! Keep playback running with vinyl pitch unlocked (Technics SL-1200 / CDJ jog wheel!)
            mpv.Command("set", "audio-pitch-correction", "no");
            if (mpv.Paused) mpv.SetPause(false);
            mpv.SetSpeed(0.28); // audible hand-on-vinyl drag slowdown immediately!
            Sfx.Tick();
        }
        if (_scratching && UI.Down && mpv.HasMedia)
        {
            float ang = MathF.Atan2(dyM, dxM);
            float dAng = ang - _scratchLastAngle;
            while (dAng > MathF.PI) dAng -= MathF.PI * 2f;
            while (dAng < -MathF.PI) dAng += MathF.PI * 2f;
            _scratchLastAngle = ang;
            _platterAngle = (_platterAngle + dAng) % (MathF.PI * 2f);

            // Angular velocity (rad/s) -> live audible vinyl speed & pitch warp!
            float instVel = dAng / MathF.Max(0.008f, dt);
            _scratchVel += (instVel - _scratchVel) * 0.42f;
            _scratchPos = mpv.TimePos;

            if (now - _lastScratchCmdMs >= 32)
            {
                _lastScratchCmdMs = now;
                if (_scratchVel < -1.1f)
                {
                    // Counter-clockwise flick -> audible vinyl back-scratch zip + crisp step back!
                    double stepBack = Math.Clamp(-_scratchVel * 0.055, 0.08, 0.42);
                    double target = Math.Max(0, mpv.TimePos - stepBack);
                    mpv.SetSpeed(Math.Clamp(0.45f + MathF.Min(1.6f, -_scratchVel * 0.14f), 0.45f, 2.1f));
                    mpv.Command("seek", target.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
                    _scratchPos = target;
                }
                else
                {
                    // Holding still = deep 0.22x drag; clockwise spin = live pitch-up 0.30x..2.50x!
                    float sp = Math.Clamp(0.22f + MathF.Max(0f, _scratchVel) * 0.34f, 0.22f, 2.50f);
                    mpv.SetSpeed(sp);
                }
            }
            UI.Hand();
        }
        if (overPlatter && UI.Wheel != 0)
        {
            s.FxSpeed = Math.Clamp(MathF.Round((s.FxSpeed + (UI.Wheel > 0 ? 0.02f : -0.02f)) * 100f) / 100f, 0.50f, 1.60f);
            UI.Wheel = 0;
            s.AudioFxEnabled = true;
            AudioDsp.MarkCustom(s);
            ScheduleApply(syncSpeed: true);
        }
        if (overPlatter && UI.RightClick(new R(cx - rad, cy - rad, platterD, platterD)))
        {
            s.FxTapePitch = !s.FxTapePitch;
            AudioDsp.MarkCustom(s);
            ApplyNow(syncSpeed: true);
            Shell.Toast(s.FxTapePitch ? L.T("Винил-тон ВКЛ (скорость меняет питч)", "Vinyl pitch ON") : L.T("Мастер-темп ВКЛ", "Master tempo ON"), Th.Brand, 1700);
        }

        // Draw turntable aluminum strobe platter + bass neon halo
        Gfx.Circle(cx, cy, rad + 6f + bass * 4f, Gfx.Fade(Th.Brand, (_scratching ? 0.34f : 0.14f) + kick * 0.24f));
        Gfx.Circle(cx, cy, rad + 3f, 0xFF1A1B24);
        // Rotating SL-1200 strobe dots around rim
        const int strobeDots = 36;
        for (int i = 0; i < strobeDots; i++)
        {
            float a = _platterAngle + i * (MathF.PI * 2f / strobeDots);
            float sx = cx + MathF.Cos(a) * (rad + 0.5f);
            float sy = cy + MathF.Sin(a) * (rad + 0.5f);
            Gfx.Circle(sx, sy, i % 3 == 0 ? 1.5f : 0.9f, Gfx.Fade(0xFFFFFFFF, i % 3 == 0 ? 0.65f : 0.25f));
        }

        // Dark glossy vinyl disc + micro-grooves
        Gfx.Circle(cx, cy, rad - 3f, 0xFF0B0B10);
        for (float gr = rad * 0.56f; gr < rad - 6f; gr += 6.5f)
            Gfx.Stroke(new R(cx - gr, cy - gr, gr * 2f, gr * 2f), Gfx.Fade(0xFFFFFFFF, 0.065f), gr, 0.85f);

        // Rotating specular groove marker so rotation is super clear even without album art
        float mkX = cx + MathF.Cos(_platterAngle) * (rad - 10f);
        float mkY = cy + MathF.Sin(_platterAngle) * (rad - 10f);
        float mkInX = cx + MathF.Cos(_platterAngle) * (rad * 0.54f);
        float mkInY = cy + MathF.Sin(_platterAngle) * (rad * 0.54f);
        Gfx.Line(mkInX, mkInY, mkX, mkY, Gfx.Fade(Th.Brand, 0.72f), 2.2f);
        Gfx.Circle(mkX, mkY, 2.6f, 0xFFFFFFFF);

        // Rotating Circular Album Cover Art Label in the center!
        float labelD = platterD * 0.48f;
        var labelR = new R(cx - labelD * 0.5f, cy - labelD * 0.5f, labelD, labelD);
        var curVid = mpv.CurrentVideoId ?? PlaybackTracker.Instance.Current?.VideoId;
        var artEntry = curVid != null ? MusicArt.Get(curVid) : null;
        if (artEntry?.SharpBmp != null)
            Gfx.ImageCircleRotated(artEntry.SharpBmp, cx, cy, labelD * 0.5f, _platterAngle, MusicArt.Alpha(artEntry));
        else
            Gfx.Circle(cx, cy, labelD * 0.5f, Gfx.Fade(Th.Brand, 0.40f));
        Gfx.Stroke(labelR, Gfx.Fade(0xFFFFFFFF, 0.35f), labelD * 0.5f, 1.2f);
        // Metallic center spindle
        Gfx.Circle(cx, cy, 7f, 0xFF121218);
        Gfx.Circle(cx, cy, 3.2f, 0xFFE5E7F0);

        // Animated metallic DJ Tonearm on top-right corner of platter
        float armBaseX = deckBox.Right - 22f, armBaseY = deckBox.Y + 28f;
        float armProg = mpv.Duration > 0 ? (float)Math.Clamp((_scratching ? _scratchPos : mpv.TimePos) / mpv.Duration, 0, 1) : 0.25f;
        float armTargetX = cx + rad * (0.78f - armProg * 0.28f);
        float armTargetY = cy + rad * (0.28f + armProg * 0.14f);
        Gfx.Circle(armBaseX, armBaseY, 10f, 0xFF282A36);
        Gfx.Circle(armBaseX, armBaseY, 5f, 0xFF8C92A8);
        Gfx.Line(armBaseX, armBaseY, armTargetX, armTargetY, 0xFFD0D5E4, 2.6f);
        Gfx.Circle(armTargetX, armTargetY, 3.6f, Th.Brand);

        // Hint line below platter
        var hintTxt = _scratching
            ? (_scratchVel < -1.1f ? L.T("⏪ Скрэтч назад · ", "⏪ Back-scratch · ") : L.T("Винил-джог · ", "Vinyl jog · ")) + $"{liveSpd:0.00}×"
            : L.T("Крути винил — живой скрэтч · колесо — темп", "Spin vinyl = live pitch/scratch · wheel = tempo");
        var hintRun = Gfx.Text(hintTxt, 10.5f, 500, deckBox.W - 20f);
        Gfx.Draw(hintRun, deckBox.X + (deckBox.W - hintRun.W) * 0.5f, cy + rad + 12f, _scratching ? 0xFFFFB830 : Th.Sub);

        // 4 Tactile DJ Transport Pads at bottom of Left Column
        float padGap = 7f;
        float padRowY = deckBox.Bottom - 78f;
        float halfW = (deckBox.W - 24f - padGap) * 0.5f;

        void DjPad(string pid, R pr, string lbl, bool active, uint actCol, Action onClick, bool momentary = false, Action? onHoldStart = null)
        {
            float cr = Gfx.Corner(9);
            bool ov = UI.Hover(pr);
            Gfx.Fill(pr, active ? Gfx.Fade(actCol, 0.30f) : Th.A(ov ? 0.14f : 0.08f), cr);
            Gfx.Stroke(pr, active ? actCol : Gfx.Fade(actCol, ov ? 0.55f : 0.22f), cr, active ? 1.4f : 1f);
            var tr = Gfx.Text(lbl, 11.5f, 700, pr.W - 8f);
            Gfx.Draw(tr, pr.X + (pr.W - tr.W) * 0.5f, pr.Y + (pr.H - tr.H) * 0.5f, active ? 0xFFFFFFFF : Th.Text);
            if (ov) UI.Hand();
            if (momentary)
            {
                if (UI.Pressed && ov) onHoldStart?.Invoke();
            }
            else if (UI.Click(UI.Id(pid), pr)) onClick();
        }

        // Row A: PLAY/PAUSE | VINYL BRAKE | BACKSPIN (3 crisp tactile DJ pads!)
        float triW = (deckBox.W - 24f - padGap * 2f) / 3f;
        DjPad("dj:play", new R(deckBox.X + 12f, padRowY, triW, 32f),
            mpv.Paused ? "▶ PLAY" : "⏸ PAUSE",
            playing, 0xFF39E68B, () => { _brakeState = 0; mpv.TogglePause(); });

        bool braked = _brakeState is 1 or 2 or 3;
        DjPad("dj:brake", new R(deckBox.X + 12f + triW + padGap, padRowY, triW, 32f),
            braked ? L.T("⚡ РАЗГОН", "⚡ UP") : "⚡ BRAKE",
            braked, 0xFFFF4D94, () => TriggerVinylBrake(mpv, s));

        DjPad("dj:spin", new R(deckBox.X + 12f + (triW + padGap) * 2f, padRowY, triW, 32f),
            "⏪ SPIN",
            _brakeState == 4, 0xFF9B6BFF, () => TriggerBackspin(mpv, s));

        // Row B: HOLD 2× | HOLD 0.5× (momentary DJ spin/drop pads!)
        float padRow2Y = padRowY + 38f;
        DjPad("dj:h2x", new R(deckBox.X + 12f, padRow2Y, halfW, 30f),
            L.T("⏩ 2× УДЕРЖ.", "⏩ 2× HOLD"),
            _holdMode == 1, 0xFF2EDBFF, () => { }, momentary: true, onHoldStart: () =>
            {
                if (!mpv.IsReady) return;
                _holdBaseSpeed = s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 1.6f) : 1.0f;
                _holdMode = 1;
                mpv.Command("set", "audio-pitch-correction", "no");
                mpv.SetSpeed(Math.Clamp(_holdBaseSpeed * 2.0f, 0.5f, 2.5f));
                Sfx.Tick();
            });

        DjPad("dj:h05", new R(deckBox.X + 12f + halfW + padGap, padRow2Y, halfW, 30f),
            L.T("⏪ 0.5× УДЕРЖ.", "⏪ 0.5× HOLD"),
            _holdMode == -1, 0xFFFFB830, () => { }, momentary: true, onHoldStart: () =>
            {
                if (!mpv.IsReady) return;
                _holdBaseSpeed = s.AudioFxEnabled ? Math.Clamp(s.FxSpeed, 0.5f, 1.6f) : 1.0f;
                _holdMode = -1;
                mpv.Command("set", "audio-pitch-correction", "no");
                mpv.SetSpeed(Math.Clamp(_holdBaseSpeed * 0.5f, 0.25f, 1.2f));
                Sfx.Tick();
            });

        // ---- RIGHT: HOT CUES + ISOLATOR KILLS + BEAT-REPEAT LOOPS + LIVE DJ FX ----
        Gfx.FillFrosted(mixerBox, Th.Card, Gfx.Corner(14), opacity: 0.48f, border: true);
        float mx = mixerBox.X + 14f, mw = mixerBox.W - 28f, my = mixerBox.Y + 11f;

        // Section 1: 4 Neon Hot Cue Pads (click = set / instant jump, right-click = clear)
        Gfx.Draw(L.T("HOT CUES (ЛКМ — поставить / прыгнуть · ПКМ — очистить)", "HOT CUES (click = set / jump · right-click = clear)"), mx, my, 10.5f, Th.Sub, 600);
        my += 16f;
        var cueKey = curVid ?? "_none";
        _hotCues.TryGetValue(cueKey, out var cues);
        float cueW = (mw - 18f) / 4f;
        for (int i = 0; i < 4; i++)
        {
            var cr = new R(mx + i * (cueW + 6f), my, cueW, 38f);
            double? cueVal = cues != null ? cues[i] : null;
            bool hasCue = cueVal.HasValue;
            uint col = CueColors[i];
            bool ov = UI.Hover(cr);
            Gfx.Fill(cr, hasCue ? Gfx.Fade(col, ov ? 0.36f : 0.25f) : Th.A(ov ? 0.12f : 0.06f), Gfx.Corner(9));
            Gfx.Stroke(cr, hasCue ? col : Gfx.Fade(col, ov ? 0.55f : 0.28f), Gfx.Corner(9), hasCue ? 1.4f : 1f);
            var topT = Gfx.Text($"CUE {i + 1}", 10f, 700);
            var botT = Gfx.Text(hasCue ? Fmt.Time(cueVal!.Value) : L.T("+ точка", "+ set"), 11.5f, hasCue ? 700 : 500);
            Gfx.Draw(topT, cr.X + (cr.W - topT.W) * 0.5f, cr.Y + 5f, col);
            Gfx.Draw(botT, cr.X + (cr.W - botT.W) * 0.5f, cr.Y + 19f, hasCue ? 0xFFFFFFFF : Th.Sub);
            if (ov) UI.Hand();
            if (UI.RightClick(cr)) { if (cues != null) cues[i] = null; Sfx.Back(); }
            else if (UI.Click(UI.Id("dj:cue", i), cr) && mpv.HasMedia)
            {
                if (!hasCue)
                {
                    if (cues == null)
                    {
                        if (_hotCues.Count > 64) _hotCues.Clear();
                        _hotCues[cueKey] = cues = new double?[4];
                    }
                    cues[i] = mpv.TimePos;
                    Sfx.Like();
                }
                else { mpv.SeekAbsolute(cueVal!.Value); if (mpv.Paused) mpv.SetPause(false); Sfx.Tick(); }
            }
        }
        my += 46f;

        // Section 2: 3-Band Pioneer DJM Isolator Kills + Instant Beat-Repeat Loops side by side!
        float killBoxW = MathF.Round(mw * 0.44f);
        float loopBoxX = mx + killBoxW + 10f;
        float loopBoxW = mw - killBoxW - 10f;

        Gfx.Draw(L.T("ИЗОЛЯТОР KILL", "ISOLATOR KILLS"), mx, my, 10.5f, Th.Sub, 600);
        Gfx.Draw(L.T("ПЕТЛЯ / BEAT LOOP", "BEAT-REPEAT LOOP"), loopBoxX, my, 10.5f, Th.Sub, 600);
        my += 16f;

        float kW = (killBoxW - 10f) / 3f;
        void KillSwitch(int idx, string lbl, bool killed, Action toggle)
        {
            var kr = new R(mx + idx * (kW + 5f), my, kW, 32f);
            bool ov = UI.Hover(kr);
            uint kCol = 0xFFFF3B5C;
            Gfx.Fill(kr, killed ? Gfx.Fade(kCol, 0.34f) : Th.A(ov ? 0.13f : 0.07f), Gfx.Corner(8));
            Gfx.Stroke(kr, killed ? kCol : Gfx.Fade(kCol, ov ? 0.45f : 0.20f), Gfx.Corner(8), killed ? 1.4f : 1f);
            var tr = Gfx.Text(lbl, 11f, 700);
            Gfx.Draw(tr, kr.X + (kr.W - tr.W) * 0.5f, kr.Y + (kr.H - tr.H) * 0.5f, killed ? 0xFFFFFFFF : Th.Text);
            if (ov) UI.Hand();
            if (UI.Click(UI.Id("dj:kill", idx), kr))
            {
                toggle();
                s.AudioFxEnabled = true;
                AudioDsp.MarkCustom(s);
                ApplyNow();
            }
        }
        KillSwitch(0, "LOW", s.FxKillLow, () => s.FxKillLow = !s.FxKillLow);
        KillSwitch(1, "MID", s.FxKillMid, () => s.FxKillMid = !s.FxKillMid);
        KillSwitch(2, "HIGH", s.FxKillHigh, () => s.FxKillHigh = !s.FxKillHigh);

        double[] loopLens = [0.125, 0.25, 0.5, 1.0];
        string[] loopLbls = ["1/8", "1/4", "1/2", "1s", "EXIT"];
        float lpW = (loopBoxW - 16f) / 5f;
        for (int i = 0; i < 5; i++)
        {
            var lr = new R(loopBoxX + i * (lpW + 4f), my, lpW, 32f);
            bool isExit = i == 4;
            bool on = !isExit && Math.Abs(_activeLoopLen - loopLens[i]) < 0.01;
            bool ov = UI.Hover(lr);
            uint lCol = isExit ? 0xFFFF6B6B : 0xFF39E68B;
            Gfx.Fill(lr, on ? Gfx.Fade(lCol, 0.30f) : Th.A(ov ? 0.13f : 0.07f), Gfx.Corner(8));
            Gfx.Stroke(lr, on ? lCol : Gfx.Fade(lCol, ov ? 0.45f : 0.18f), Gfx.Corner(8), on ? 1.4f : 1f);
            var tr = Gfx.Text(loopLbls[i], 10.5f, 700);
            Gfx.Draw(tr, lr.X + (lr.W - tr.W) * 0.5f, lr.Y + (lr.H - tr.H) * 0.5f, on ? 0xFFFFFFFF : (isExit && _activeLoopLen > 0 ? lCol : Th.Text));
            if (ov) UI.Hand();
            if (UI.Click(UI.Id("dj:loop", i), lr))
            {
                if (isExit) ClearAbLoop(mpv);
                else ToggleBeatLoop(mpv, loopLens[i]);
            }
        }
        my += 42f;

        Gfx.Fill(new R(mx, my, mw, 1), Th.Line);
        my += 10f;

        // Section 3: 5 Live DJ FX Faders (Bi-Polar DJ Filter, PS1 Bitcrusher, Tape Drive, Jet Flanger, Tempo Pitch Fader)
        float quickRowH = 38f;
        float availH = MathF.Max(155f, mixerBox.Bottom - my - quickRowH - 12f);
        float stride = Math.Clamp(availH / 5f, 30f, 38f);

        string fltTxt = MathF.Abs(s.FxDjFilter) < 2f ? "CENTER (OFF)"
            : s.FxDjFilter < 0 ? $"LPF {MathF.Abs(s.FxDjFilter):0}%" : $"HPF {s.FxDjFilter:0}%";
        s.FxDjFilter = DrawFxSlider(s, "djflt", L.T("DJ Фильтр (LPF ← 0 → HPF)", "DJ Filter (LPF ← 0 → HPF)"),
            s.FxDjFilter, -100f, 100f, 0f, fltTxt, mx, mw, ref my, stride, bipolar: true);

        s.FxCrush = DrawFxSlider(s, "djcr", L.T("PS1 / SP-1200 Крашер (Bitcrusher)", "PS1 / SP-1200 Bitcrusher"),
            s.FxCrush, 0f, 100f, 0f, $"{s.FxCrush:0}%", mx, mw, ref my, stride);

        s.FxDrive = DrawFxSlider(s, "djdr", L.T("Тёплый перегруз (Tape / Tube Drive)", "Analog Tape / Tube Drive"),
            s.FxDrive, 0f, 100f, 0f, $"{s.FxDrive:0}%", mx, mw, ref my, stride);

        s.FxMod = DrawFxSlider(s, "djmd", L.T("DJ Джет-Фейзер + Флэнжер", "DJ Jet Phaser + Flanger"),
            s.FxMod, 0f, 100f, 0f, $"{s.FxMod:0}%", mx, mw, ref my, stride);

        s.FxSpeed = DrawFxSlider(s, "djsp", L.T("Темп / Питч-фейдер (ПКМ = 1.00×)", "Tempo / Pitch Fader (right-click = 1.00×)"),
            s.FxSpeed, 0.50f, 1.60f, 1.0f, $"{s.FxSpeed:0.00}×", mx, mw, ref my, stride, enabled: true, affectsSpeed: true);

        // Quick 1-Click DJ Vibe Strip at the bottom of the mixer rack!
        float qy = MathF.Min(mixerBox.Bottom - 36f, my + 4f);
        (string Id, string Ru, string En, uint Col)[] djQuick =
        [
            ("PsxJungle", "PS1 / PSX", "PS1 / PSX", 0xFF2EDBFF),
            ("Lofi", "SP-1200", "SP-1200", 0xFFFFB830),
            ("BitcrushHyper", "8-Bit Crush", "8-Bit Crush", 0xFFFF4D94),
            ("ClubUnderwater", "Клуб LPF", "Club LPF", 0xFF9B6BFF),
            ("Flat", "Чистый DJ", "Clean DJ", 0xFF39E68B),
        ];
        float qw = (mw - (djQuick.Length - 1) * 5f) / djQuick.Length;
        for (int i = 0; i < djQuick.Length; i++)
        {
            var qp = djQuick[i];
            var qr = new R(mx + i * (qw + 5f), qy, qw, 26f);
            bool on = s.AudioFxEnabled && s.AudioPreset.Equals(qp.Id, StringComparison.OrdinalIgnoreCase);
            bool ov = UI.Hover(qr);
            Gfx.Fill(qr, on ? Gfx.Fade(qp.Col, 0.30f) : Th.A(ov ? 0.13f : 0.06f), Gfx.Corner(7));
            Gfx.Stroke(qr, on ? qp.Col : Gfx.Fade(qp.Col, ov ? 0.48f : 0.20f), Gfx.Corner(7), on ? 1.3f : 1f);
            var qLbl = L.T(qp.Ru, qp.En);
            var qrRun = Gfx.Text(qLbl, 10.5f, on ? 700 : 500, qw - 6f);
            Gfx.Draw(qrRun, qr.X + (qr.W - qrRun.W) * 0.5f, qr.Y + (qr.H - qrRun.H) * 0.5f, on ? 0xFFFFFFFF : Th.Text);
            if (ov) UI.Hand();
            if (UI.Click(UI.Id("dj:q", i), qr))
            {
                AudioDsp.ApplyPreset(s, qp.Id);
                ApplyNow(syncSpeed: true);
                Shell.Toast("DJ: " + qLbl, Th.Good, 1400);
            }
        }
    }

    // ==================== SHARED FX SLIDER HELPER (Supports Unipolar + Bi-Polar Center-Out!) ====================

    private static float DrawFxSlider(AppSettings s, string key, string name, float val, float min, float max, float defVal,
        string fmt, float fx, float fw, ref float fyCur, float rowStride, bool enabled = true, bool affectsSpeed = false, bool bipolar = false)
    {
        var hit = new R(fx, fyCur, fw, MathF.Min(32f, rowStride));
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
            if (MathF.Abs(nv - defVal) < (max - min) * 0.03f) nv = defVal;
            if (MathF.Abs(nv - val) > 0.001f)
            {
                val = nv;
                AudioDsp.MarkCustom(s);
                ScheduleApply(affectsSpeed);
            }
        }
        if (over || drag) UI.Hand();

        Gfx.Draw(name, fx, fyCur, 11.5f, enabled ? Th.Text : Th.Dim, 500, fw - 78f);
        var vr = Gfx.Text(fmt, 11f, 600);
        bool modified = MathF.Abs(val - defVal) > 0.01f;
        Gfx.Draw(vr, fx + fw - vr.W, fyCur, enabled ? (modified ? Th.Brand : Th.Sub) : Th.Dim);

        float ty = fyCur + 19f;
        float th = drag || over ? 5f : 4f;
        float pct = Math.Clamp((val - min) / MathF.Max(0.001f, max - min), 0f, 1f);
        if (bipolar)
        {
            float midX = fx + fw * 0.5f;
            float curX = fx + fw * pct;
            Gfx.Fill(new R(fx, ty, fw, th), Th.A(0.18f), th * 0.5f);
            Gfx.Fill(new R(midX - 0.75f, ty - 2f, 1.5f, th + 4f), Th.A(0.45f));
            if (MathF.Abs(curX - midX) > 1f)
            {
                uint biCol = val < 0 ? 0xFF2EDBFF : Th.Brand;
                Gfx.Fill(new R(MathF.Min(midX, curX), ty, MathF.Abs(curX - midX), th), enabled && s.AudioFxEnabled ? biCol : Th.Dim, th * 0.5f);
            }
            Gfx.Circle(curX, ty + th * 0.5f, drag || over ? 5.5f : 4.2f, enabled ? 0xFFFFFFFF : Th.Dim);
        }
        else
        {
            UI.Progress(new R(fx, ty, fw, th), pct, Th.A(0.18f), enabled && s.AudioFxEnabled ? Th.Brand : Th.Dim);
            Gfx.Circle(fx + fw * pct, ty + th * 0.5f, drag || over ? 5.5f : 4.2f, enabled ? Th.Text : Th.Dim);
        }

        fyCur += rowStride;
        return val;
    }
}
