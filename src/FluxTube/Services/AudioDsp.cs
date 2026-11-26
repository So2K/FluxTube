using System.Globalization;

namespace FluxTube.Services;

/// <summary>
/// VST-Class Studio Mastering, True Stereo FDN/Dattorro/Shimmer Convolution Reverb (`afir`), and Pioneer DJM-900 FX Engine:
/// - 10-band constant-Q EQ + Steep 24 dB/oct 4-pole DJ Isolator Kills (LOW / MID / HIGH KILL)
/// - Resonant Pioneer DJM-900 Sound Color Filter (LPF &lt;- 0 -&gt; HPF with live resonant cutoff peak!)
/// - Waves MaxxBass-style Sub-Harmonics (`virtualbass` + stereo LFE foldback) + 62 Hz warm sub shelf
/// - Aphex Aural Exciter (`aexciter`) + Transient Punch (`crystalizer`) + Real 3-Band Xfer OTT Multiband (`mcompand`)
/// - 2x Oversampled Tube/Tape Drive (`asoftclip=tanh`) + PS1/SP-1200 Bitcrusher (`acrusher`) + Layered Club Phaser+Jet Flanger
/// - True Unity-Calibrated Parallel Stereo Convolution Reverb (`asplit` + Abbey Road HP/LP + Pre-Delay + `afir=dry=1:wet=1:irnorm=-1` + wide wet sheen + `amix`)
/// - Psychoacoustic Mastering Clipper (`apsyclip`) + Transparent Lookahead Limiter (`alimiter`) + Bauer BS2B 3D + 8D.
/// </summary>
public static class AudioDsp
{
    public static readonly int[] Freqs = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public static readonly string[] FreqLabels = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    public static readonly (string Key, string Ru, string En)[] Categories =
    [
        ("all", "Все", "All"),
        ("studio", "Студия", "Studio"),
        ("bass", "Бас & Клуб", "Bass & Club"),
        ("vibes", "Атмосфера", "Vibes"),
        ("dj", "DJ & Ретро", "DJ & Retro"),
        ("voice", "Голос & Кино", "Voice & Cinema"),
    ];

    public sealed record Preset(
        string Id,
        string NameRu,
        string NameEn,
        string Category,
        string Tag,
        float[] Eq,
        float Reverb,
        string Room,
        float Bass,
        float Exciter,
        float Stereo,
        float Speed,
        bool TapePitch,
        float PitchSemi,
        bool Space8D,
        bool Limiter,
        float DjFilter = 0f,
        float Crush = 0f,
        float Drive = 0f,
        float Mod = 0f,
        bool Headphone3D = false);

    public static readonly Preset[] Presets =
    [
        // Studio & Mastering
        new("Flat", "Чистый", "Flat", "studio", "Bypass",
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            0, "hall", 0, 0, 0, 1.00f, false, 0, false, true),

        new("Juicy", "Сочный Мастер", "Juicy Master", "studio", "Studio",
            [1.8f, 2.8f, 1.0f, -1.4f, -0.4f, 0.8f, 1.8f, 2.4f, 3.0f, 3.4f],
            16, "plate", 28, 42, 26, 1.00f, false, 0, false, true, Drive: 14),

        new("OttMaster", "OTT Гипер-Мастер", "OTT Hyper-Master", "studio", "OTT",
            [3.2f, 3.6f, 0.8f, -2.0f, -0.8f, 1.4f, 2.8f, 3.8f, 4.4f, 4.2f],
            14, "plate", 42, 78, 34, 1.00f, false, 0, false, true, Drive: 26),

        new("WarmAnalog", "Тёплая Лента", "Tape Warmth", "studio", "Tape",
            [2.4f, 3.2f, 2.2f, 1.0f, 0.4f, 0.0f, -0.6f, -1.2f, -2.0f, -2.8f],
            22, "vintage", 26, 22, 18, 1.00f, false, 0, false, true, Drive: 44),

        new("StudioRef", "Студийный Баланс", "Studio Ref", "studio", "Crisp",
            [0.8f, 1.0f, 0.0f, -0.8f, 0.0f, 0.5f, 1.2f, 1.8f, 2.0f, 1.5f],
            0, "room", 10, 24, 12, 1.00f, false, 0, false, true),

        new("CrystalAir", "Кристальный Верх", "Crystal Air", "studio", "Hi-Fi",
            [0.5f, 1.0f, 0.0f, -1.0f, -0.5f, 0.8f, 2.2f, 3.5f, 4.5f, 5.5f],
            18, "plate", 12, 58, 34, 1.00f, false, 0, false, true),

        new("Headphone3D", "Наушники 3D (BS2B)", "Headphone 3D", "studio", "Binaural",
            [1.4f, 1.6f, 0.4f, -0.6f, 0.0f, 0.6f, 1.4f, 2.0f, 2.4f, 2.2f],
            20, "room", 18, 24, 20, 1.00f, false, 0, false, true, Headphone3D: true),

        // Bass & Club
        new("BassBoost", "Bass Boost", "Bass Boost", "bass", "Sub",
            [5.5f, 5.0f, 2.5f, -0.5f, -1.0f, 0.0f, 1.2f, 1.8f, 2.0f, 1.5f],
            12, "room", 62, 32, 22, 1.00f, false, 0, false, true, Drive: 18),

        new("SubMonster", "Саб-Монстр", "Sub Monster", "bass", "808",
            [8.0f, 6.8f, 3.0f, -1.5f, -1.5f, 0.0f, 1.5f, 2.2f, 2.5f, 1.5f],
            10, "room", 86, 38, 26, 1.00f, false, 0, false, true, Drive: 28),

        new("Phonk", "Phonk / Drift", "Phonk / Drift", "bass", "OTT",
            [5.0f, 4.2f, 1.5f, -2.0f, -0.5f, 1.8f, 3.5f, 4.2f, 3.5f, 2.8f],
            22, "plate", 60, 64, 36, 1.00f, false, 0, false, true, Drive: 50),

        new("Trap808", "Трэп / 808", "Trap / 808", "bass", "Hip-Hop",
            [6.5f, 5.2f, 1.8f, -1.8f, -0.8f, 0.5f, 1.5f, 2.8f, 4.0f, 3.5f],
            16, "room", 66, 40, 28, 1.00f, false, 0, false, true, Drive: 22),

        new("ClubEdm", "Клуб / EDM", "Club / EDM", "bass", "Wide",
            [4.5f, 4.0f, 1.5f, -1.5f, -0.8f, 0.8f, 2.2f, 3.2f, 3.8f, 3.5f],
            26, "hall", 48, 56, 46, 1.00f, false, 0, false, true, Drive: 18),

        new("DnBJungle", "DnB / Jungle", "DnB / Jungle", "bass", "Break",
            [5.2f, 4.5f, 1.5f, -1.5f, 0.0f, 1.2f, 2.8f, 3.5f, 4.2f, 3.5f],
            18, "plate", 52, 52, 36, 1.00f, false, 0, false, true, Drive: 24),

        new("RockMetal", "Рок / Метал", "Rock / Metal", "bass", "Drive",
            [3.5f, 3.0f, 1.0f, -2.0f, -1.2f, 1.5f, 3.2f, 3.8f, 2.8f, 2.0f],
            16, "room", 30, 48, 26, 1.00f, false, 0, false, true, Drive: 34),

        // Vibes & Shimmer
        new("Nightcore", "Nightcore", "Nightcore", "vibes", "1.22×",
            [2.5f, 2.2f, 0.8f, -1.0f, 0.0f, 1.0f, 2.2f, 3.0f, 3.6f, 4.0f],
            20, "plate", 26, 38, 28, 1.22f, true, 0, false, true),

        new("NightcoreUltra", "Hyperpop / Nightcore+", "Hyperpop / Nightcore+", "vibes", "1.30×",
            [3.8f, 3.0f, 1.2f, -1.5f, -0.5f, 1.5f, 2.8f, 3.8f, 4.6f, 5.0f],
            24, "plate", 36, 56, 40, 1.30f, true, 0, false, true, Drive: 24),

        new("ValhallaShimmer", "Valhalla Шиммер", "Valhalla Shimmer", "vibes", "Dream",
            [2.0f, 2.2f, 0.6f, -1.2f, -0.4f, 0.6f, 1.8f, 2.8f, 3.8f, 4.2f],
            62, "shimmer", 22, 28, 54, 1.00f, false, 0, false, true),

        new("SlowedReverb", "Slowed + Shimmer", "Slowed + Shimmer", "vibes", "0.84×",
            [3.8f, 3.4f, 1.6f, -0.8f, -1.0f, -0.2f, 1.2f, 2.0f, 2.8f, 2.5f],
            66, "shimmer", 36, 24, 50, 0.84f, true, 0, false, true),

        new("DoomerNight", "Doomer / Ночь", "Doomer / Night", "vibes", "0.90×",
            [3.2f, 3.8f, 2.2f, 0.2f, -1.2f, -1.5f, -0.8f, 0.4f, -1.5f, -3.5f],
            54, "vintage", 30, 16, 44, 0.90f, true, 0, false, true, Drive: 24),

        new("Space8D", "8D Космос", "8D Space", "vibes", "3D",
            [2.5f, 2.2f, 0.8f, -1.0f, 0.0f, 0.5f, 1.8f, 2.6f, 3.2f, 3.5f],
            54, "shimmer", 28, 30, 54, 1.00f, false, 0, true, true),

        new("Vaporwave", "Vaporwave 80s", "Vaporwave 80s", "vibes", "0.78×",
            [3.5f, 3.0f, 1.4f, 0.4f, -0.8f, -0.5f, 1.0f, 1.8f, 1.0f, -1.5f],
            58, "vintage", 32, 20, 60, 0.78f, true, 0, false, true, Drive: 30, Mod: 36),

        new("ConcertHall", "Lexicon 480L Зал", "Lexicon 480L Hall", "vibes", "Live",
            [2.0f, 2.4f, 0.8f, -0.6f, 0.0f, 0.8f, 1.8f, 2.5f, 2.2f, 1.6f],
            46, "hall", 22, 28, 46, 1.00f, false, 0, false, true),

        // DJ & Retro Hardware
        new("PsxJungle", "PS1 / PSX SPU", "PS1 / PSX SPU", "dj", "1996",
            [3.2f, 3.8f, 2.0f, 0.4f, -0.6f, 0.8f, 1.6f, 0.6f, -2.5f, -5.5f],
            48, "psx", 34, 22, 30, 1.00f, false, 0, false, true, Crush: 28, Drive: 24),

        new("Lofi", "SP-1200 / Винил", "SP-1200 / Vinyl", "dj", "Lo-Fi",
            [2.4f, 3.4f, 2.2f, 1.2f, 0.4f, -0.5f, -2.0f, -4.2f, -6.5f, -8.5f],
            34, "vintage", 22, 14, 20, 0.94f, true, 0, false, true, Crush: 36, Drive: 48),

        new("BitcrushHyper", "Bitcrush / Breakcore", "Bitcrush / Breakcore", "dj", "8-Bit",
            [4.0f, 3.5f, 1.0f, -1.5f, 0.0f, 1.6f, 3.0f, 3.8f, 3.2f, 2.0f],
            24, "psx", 44, 48, 34, 1.00f, false, 0, false, true, Crush: 62, Drive: 46),

        new("DjJetSwirl", "DJ Jet Phaser", "DJ Jet Phaser", "dj", "Club",
            [3.5f, 3.0f, 1.0f, -1.2f, 0.0f, 1.0f, 2.2f, 3.0f, 3.2f, 2.5f],
            30, "hall", 38, 42, 46, 1.00f, false, 0, false, true, Mod: 70),

        new("ClubUnderwater", "Клуб за стеной (LPF)", "Outside Club (LPF)", "dj", "LPF",
            [5.0f, 4.5f, 2.0f, 0.0f, -1.0f, -1.0f, -1.0f, 0.0f, 0.0f, 0.0f],
            46, "hall", 54, 12, 30, 1.00f, false, 0, false, true, DjFilter: -68),

        // Voice & Cinema
        new("CinemaImax", "Кино / IMAX Зал", "Cinema / IMAX", "voice", "IMAX",
            [4.5f, 3.8f, 1.2f, -1.0f, 0.4f, 1.8f, 2.8f, 2.4f, 2.6f, 2.2f],
            38, "cinema", 44, 38, 54, 1.00f, false, 0, false, true),

        new("Vocal", "Вокал / Подкаст", "Vocal / Podcast", "voice", "Clear",
            [-6.5f, -3.5f, -1.2f, 0.5f, 1.8f, 3.0f, 4.2f, 3.2f, 1.8f, 0.8f],
            8, "room", 0, 34, 8, 1.00f, false, 0, false, true),

        new("LateNight", "Ночной режим", "Late Night", "voice", "Soft",
            [-3.0f, -1.5f, 0.5f, 1.2f, 2.0f, 2.5f, 2.5f, 1.5f, 0.5f, -1.0f],
            0, "room", 0, 48, 10, 1.00f, false, 0, false, true),
    ];

    public static readonly (string Key, string Ru, string En)[] Rooms =
    [
        ("room", "Студия", "Room"),
        ("plate", "Плейт", "Plate"),
        ("hall", "480L Зал", "480L Hall"),
        ("cinema", "Кино", "Cinema"),
        ("shimmer", "Шиммер", "Shimmer"),
        ("psx", "PS1 SPU", "PS1 SPU"),
        ("vintage", "80s Лента", "80s Tape"),
        ("custom", "Свой IR…", "Custom IR…"),
    ];

    public static void EnsureEqArray(AppSettings s)
    {
        if (s.EqBands == null || s.EqBands.Length != Freqs.Length)
        {
            var next = new float[Freqs.Length];
            if (s.EqBands != null)
                Array.Copy(s.EqBands, next, Math.Min(s.EqBands.Length, next.Length));
            s.EqBands = next;
        }
    }

    public static Preset? FindPreset(string id)
    {
        foreach (var p in Presets)
            if (p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    public static string PresetLabel(Preset p) => L.T(p.NameRu, p.NameEn);

    public static string RoomLabel(string key)
    {
        if (key == "cathedral") key = "shimmer";
        foreach (var r in Rooms)
            if (r.Key == key) return L.T(r.Ru, r.En);
        return key;
    }

    public static string DisplayPresetName(AppSettings s)
    {
        if (!s.AudioFxEnabled)
            return s.PlayerAudio8D ? "8D" : L.T("Выкл", "Off");
        var p = FindPreset(s.AudioPreset);
        if (p != null) return PresetLabel(p);
        if (s.AudioPreset.StartsWith("user:", StringComparison.Ordinal))
            return s.AudioPreset[5..];
        return L.T("Свой", "Custom");
    }

    public static void ApplyPreset(AppSettings s, string presetId)
    {
        EnsureEqArray(s);
        var p = FindPreset(presetId) ?? Presets[0];
        s.AudioPreset = p.Id;
        s.AudioFxEnabled = p.Id != "Flat";
        Array.Copy(p.Eq, s.EqBands, Freqs.Length);
        s.FxReverb = p.Reverb;
        s.FxReverbRoom = p.Room;
        s.FxBass = p.Bass;
        s.FxExciter = p.Exciter;
        s.FxStereo = p.Stereo;
        s.FxSpeed = p.Speed;
        s.FxTapePitch = p.TapePitch;
        s.FxPitchSemitones = p.PitchSemi;
        s.PlayerAudio8D = p.Space8D;
        s.FxLimiter = p.Limiter;
        s.FxDjFilter = p.DjFilter;
        s.FxCrush = p.Crush;
        s.FxDrive = p.Drive;
        s.FxMod = p.Mod;
        s.FxHeadphone3D = p.Headphone3D;
        s.FxKillLow = s.FxKillMid = s.FxKillHigh = false;
    }

    public static void ApplySavedPreset(AppSettings s, SavedAudioPreset cp)
    {
        EnsureEqArray(s);
        s.AudioPreset = "user:" + cp.Name;
        s.AudioFxEnabled = true;
        Array.Clear(s.EqBands);
        if (cp.Eq != null) Array.Copy(cp.Eq, s.EqBands, Math.Min(cp.Eq.Length, Freqs.Length));
        s.FxReverb = cp.Reverb;
        s.FxReverbRoom = cp.Room == "cathedral" ? "shimmer" : cp.Room;
        s.FxBass = cp.Bass;
        s.FxExciter = cp.Exciter;
        s.FxStereo = cp.Stereo;
        s.FxSpeed = cp.Speed;
        s.FxTapePitch = cp.TapePitch;
        s.FxPitchSemitones = cp.PitchSemi;
        s.PlayerAudio8D = cp.Space8D;
        s.FxLimiter = cp.Limiter;
        s.FxDjFilter = cp.DjFilter;
        s.FxCrush = cp.Crush;
        s.FxDrive = cp.Drive;
        s.FxMod = cp.Mod;
        s.FxHeadphone3D = cp.Headphone3D;
        s.FxKillLow = s.FxKillMid = s.FxKillHigh = false;
    }

    public static SavedAudioPreset CaptureCurrent(AppSettings s, string name)
    {
        EnsureEqArray(s);
        return new SavedAudioPreset
        {
            Name = name,
            Eq = [.. s.EqBands],
            Reverb = s.FxReverb,
            Room = s.FxReverbRoom,
            Bass = s.FxBass,
            Exciter = s.FxExciter,
            Stereo = s.FxStereo,
            Speed = s.FxSpeed,
            TapePitch = s.FxTapePitch,
            PitchSemi = s.FxPitchSemitones,
            Space8D = s.PlayerAudio8D,
            Limiter = s.FxLimiter,
            DjFilter = s.FxDjFilter,
            Crush = s.FxCrush,
            Drive = s.FxDrive,
            Mod = s.FxMod,
            Headphone3D = s.FxHeadphone3D,
        };
    }

    public static void MarkCustom(AppSettings s)
    {
        s.AudioFxEnabled = true;
        s.AudioPreset = "Custom";
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static (int PreDelayMs, int HpHz, int LpHz, float WetScale, float WetWide) RoomParams(string room) => room switch
    {
        "room" => (10, 155, 12500, 0.85f, 1.12f),
        "plate" => (18, 210, 15000, 0.96f, 1.18f),
        "cinema" => (32, 145, 11800, 1.08f, 1.24f),
        "shimmer" or "cathedral" => (36, 185, 16500, 1.16f, 1.28f),
        "psx" => (14, 180, 8400, 0.90f, 1.16f),
        "vintage" => (22, 195, 7200, 0.68f, 1.20f),
        "custom" => (20, 165, 14000, 1.00f, 1.16f),
        _ => (26, 175, 13000, 1.02f, 1.22f), // "hall" Lexicon 480L
    };

    /// <summary>
    /// Builds a clean, zero-warning, studio VST-grade mpv `af` filter graph (`lavfi=[...]`).
    /// IMPORTANT: in FFmpeg `afir`, `dry` is INPUT gain BEFORE convolution (must be `1`, NEVER `0`!),
    /// `wet` is OUTPUT gain (`1`), and `irnorm=-1` preserves our exact unity L2 normalization from ImpulseBank!
    /// </summary>
    public static string BuildFilterGraph(AppSettings s)
    {
        EnsureEqArray(s);
        if (!s.AudioFxEnabled)
        {
            if (!s.PlayerAudio8D) return "";
            var ir8d = ImpulseBank.GetImpulsePath("hall");
            if (ir8d != null)
            {
                var esc8 = ImpulseBank.EscapeForLavfi(ir8d);
                return $"lavfi=[aformat=channel_layouts=stereo:sample_rates=48000,apulsator=hz=0.14:width=1:amount=0.82,asplit=2[d8][w8];[w8]highpass=f=185:poles=2,adelay=24|24[wp8];amovie='{esc8}',aformat=channel_layouts=stereo:sample_rates=48000[ir8];[wp8][ir8]afir=dry=1:wet=1:irnorm=-1:minp=2048:maxp=8192,extrastereo=m=1.22:c=1[rv8];[d8][rv8]amix=inputs=2:weights=0.92 0.48:normalize=0:duration=first]";
            }
            return "lavfi=[apulsator=hz=0.14:width=1:amount=0.82]";
        }

        var pre = new List<string>(28);
        float bass = Math.Clamp(s.FxBass, 0f, 100f) / 100f;
        float exc = Math.Clamp(s.FxExciter, 0f, 100f) / 100f;
        float rev = Math.Clamp(s.FxReverb, 0f, 100f) / 100f;
        float stereo = Math.Clamp(s.FxStereo, 0f, 100f) / 100f;
        float djf = Math.Clamp(s.FxDjFilter, -100f, 100f) / 100f;
        float crush = Math.Clamp(s.FxCrush, 0f, 100f) / 100f;
        float drive = Math.Clamp(s.FxDrive, 0f, 100f) / 100f;
        float mod = Math.Clamp(s.FxMod, 0f, 100f) / 100f;

        float maxBoost = 0f;
        for (int i = 0; i < Freqs.Length; i++)
            if (s.EqBands[i] > maxBoost) maxBoost = s.EqBands[i];

        // 0. Guarantee clean stereo 48kHz floating-point processing bus for convolution & stereo VST filters
        bool needsStereo48 = rev > 0.01f || s.PlayerAudio8D || bass >= 0.22f || exc > 0.02f || mod > 0.02f;
        if (needsStereo48)
            pre.Add("aformat=channel_layouts=stereo:sample_rates=48000");

        // 1. Gain-staging headroom before boosts (zero internal clipping!)
        float preCutDb = Math.Clamp(maxBoost * 0.40f + bass * 4.4f + exc * 2.6f + drive * 2.2f, 0f, 11.0f);
        if (preCutDb > 0.25f)
            pre.Add($"volume=-{F(preCutDb)}dB");

        // 2. Formant-preserving independent pitch shift when tape pitch isn't active
        if (!s.FxTapePitch && MathF.Abs(s.FxPitchSemitones) >= 0.25f)
        {
            double ratio = Math.Pow(2.0, Math.Clamp(s.FxPitchSemitones, -6f, 6f) / 12.0);
            pre.Add($"rubberband=pitch={ratio.ToString("0.0000", CultureInfo.InvariantCulture)}:formant=preserved");
        }

        // 3. Pioneer DJM-900 Steep 24 dB/oct 4-Pole DJ Isolator Kill Switches!
        if (s.FxKillLow)
        {
            pre.Add("highpass=f=300:poles=2");
            pre.Add("highpass=f=300:poles=2");
        }
        if (s.FxKillMid)
        {
            pre.Add("equalizer=f=920:t=o:w=1.8:g=-30");
            pre.Add("equalizer=f=1850:t=o:w=1.1:g=-18");
        }
        if (s.FxKillHigh)
        {
            pre.Add("lowpass=f=2400:poles=2");
            pre.Add("lowpass=f=2400:poles=2");
        }

        // 4. Bi-Polar Pioneer DJM-900 Resonant Sound Color Filter (-100 = Resonant LPF, +100 = Resonant HPF Riser!)
        // Adding a sharp resonant Q=3.2 boost right at the moving cutoff frequency gives the authentic liquid club sweep!
        if (djf < -0.02f)
        {
            float t = -djf; // 0..1
            float cutoff = 17500f * MathF.Pow(145f / 17500f, t);
            float resDb = 2.4f + t * 5.2f;
            pre.Add($"lowpass=f={F(cutoff)}:poles=2");
            pre.Add($"lowpass=f={F(cutoff * 1.08f)}:poles=2");
            pre.Add($"equalizer=f={F(cutoff)}:t=q:w=3.2:g={F(resDb)}");
        }
        else if (djf > 0.02f)
        {
            float t = djf;
            float cutoff = 42f * MathF.Pow(4600f / 42f, t);
            float resDb = 2.4f + t * 5.4f;
            pre.Add($"highpass=f={F(cutoff)}:poles=2");
            pre.Add($"highpass=f={F(cutoff * 0.92f)}:poles=2");
            pre.Add($"equalizer=f={F(cutoff)}:t=q:w=3.2:g={F(resDb)}");
        }

        // 5. 10-band musical constant-Q graphic EQ
        for (int i = 0; i < Freqs.Length; i++)
        {
            float g = Math.Clamp(s.EqBands[i], -12f, 12f);
            if (MathF.Abs(g) < 0.15f) continue;
            pre.Add($"equalizer=f={Freqs[i]}:t=q:w=1.41:g={F(g)}");
        }

        // 6. Waves MaxxBass / RBass Sub-Bass Engine: warm 62 Hz sub shelf + psychoacoustic sub-harmonics folded cleanly back to stereo!
        if (bass > 0.01f && !s.FxKillLow)
        {
            pre.Add($"bass=g={F(bass * 6.8f)}:f=62:t=s:w=0.58");
            if (bass >= 0.22f)
            {
                float str = Math.Clamp(0.85f + (bass - 0.22f) * 2.55f, 0.85f, 2.85f);
                float lfeFold = Math.Clamp(0.88f + bass * 0.22f, 0.88f, 1.10f);
                pre.Add($"virtualbass=cutoff=195:strength={F(str)}");
                // virtualbass outputs 2.1 (FL, FR, LFE): fold LFE (c2) back into stereo L/R so headphone sub stays massive!
                pre.Add($"pan=stereo|c0=c0+{F(lfeFold)}*c2|c1=c1+{F(lfeFold)}*c2");
            }
        }

        // 7. 2x Oversampled Analog Tube/Tape Soft-Clip Drive (RC-20 / Sausage Fattener warmth!)
        if (drive > 0.02f)
        {
            float boostDb = drive * 8.2f;
            pre.Add($"volume=+{F(boostDb)}dB");
            pre.Add("asoftclip=type=tanh:threshold=0.72:output=0.94:oversample=2");
            pre.Add($"volume=-{F(boostDb * 0.62f)}dB");
            if (drive > 0.35f)
            {
                float tapeAmt = (drive - 0.35f) / 0.65f;
                pre.Add($"vibrato=f=1.45:d={F(tapeAmt * 0.042f)}");
                pre.Add($"treble=g=-{F(tapeAmt * 1.8f)}:f=10500:t=s:w=0.6");
            }
        }

        // 8. PS1 / SP-1200 Lo-Fi Bitcrusher & Sample-Rate Decimator
        if (crush > 0.02f)
        {
            float bits = Math.Clamp(14.0f - crush * 9.5f, 4.2f, 14.5f);
            int samples = Math.Clamp((int)MathF.Round(1f + crush * crush * 10f), 1, 11);
            float mix = Math.Clamp(0.32f + crush * 0.68f, 0.30f, 1f);
            pre.Add($"acrusher=level_in=1:level_out=1:bits={F(bits)}:samples={samples}:mode=log:aa=0.30:mix={F(mix)}");
        }

        // 9. Aphex Aural Exciter + Transient Crystalizer + Real 3-Band Xfer OTT Multiband Glue!
        if (exc > 0.02f)
        {
            // Real harmonic high-frequency brilliance generator (Aphex Exciter)
            float exAmt = Math.Clamp(0.45f + exc * 2.6f, 0.45f, 3.1f);
            float exDrv = Math.Clamp(4.0f + exc * 4.8f, 4.0f, 8.8f);
            pre.Add($"aexciter=level_in=1:level_out=1:amount={F(exAmt)}:drive={F(exDrv)}:blend=0:freq=6200:ceil=16500");

            // Transient crispness & punch shaper
            float crys = Math.Clamp(exc * 2.2f, 0.25f, 2.2f);
            pre.Add($"crystalizer=i={F(crys)}:c=false");

            if (exc >= 0.48f)
            {
                // Real 3-band Xfer OTT / Soundgoodizer multiband upward+downward compressor (220Hz / 2.4kHz / 20kHz)!
                float ott = (exc - 0.48f) / 0.52f; // 0..1 OTT intensity
                float upBoost = Math.Clamp(6f + ott * 7f, 6f, 13f);
                string tLow = $"-70/-{F(70 - upBoost)},-38/-{F(38 - upBoost)},-18/-17,0/-{F(4.5f + ott * 2.5f)}";
                string tMid = $"-70/-{F(70 - upBoost * 0.92f)},-38/-{F(38 - upBoost * 0.92f)},-18/-16.5,0/-{F(4.0f + ott * 2.2f)}";
                string tHi  = $"-70/-{F(70 - upBoost * 1.05f)},-38/-{F(38 - upBoost * 1.05f)},-18/-16,0/-{F(3.5f + ott * 2.0f)}";
                pre.Add($"mcompand=args='0.006,0.085 6 {tLow} 220 | 0.003,0.060 6 {tMid} 2400 | 0.001,0.042 6 {tHi} 20000'");
                pre.Add($"volume=-{F(2.2f + ott * 2.8f)}dB");
            }
            else
            {
                pre.Add($"acompressor=threshold=-18dB:ratio={F(1.6f + exc * 2.2f)}:attack=9:release=110:knee=4:makeup={F(0.8f + exc * 2.2f)}dB");
            }
        }

        // 10. Club DJ Jet Swirl: Layered Analog Phaser (`aphaser`) + Stereo Jet Flanger (`flanger`)!
        if (mod > 0.02f)
        {
            float phDecay = Math.Clamp(0.28f + mod * 0.40f, 0.25f, 0.68f);
            float phSpd = Math.Clamp(0.28f + mod * 0.85f, 0.25f, 1.25f);
            // Keep in_gain*(1+decay)*out_gain < 1.0 for zero headroom clipping warning, then clean makeup
            pre.Add($"aphaser=in_gain=0.52:out_gain=0.60:delay=3.2:decay={F(phDecay)}:speed={F(phSpd)}:type=t");
            pre.Add("volume=+2.2dB");

            float depth = 1.6f + mod * 5.8f;
            float regen = 24f + mod * 58f;
            float speed = 0.24f + mod * 0.72f;
            pre.Add($"flanger=delay=2.4:depth={F(depth)}:regen={F(regen)}:width=78:speed={F(speed)}:shape=s:phase=45");
        }

        // 11. Mid/Side 3D stereo widening & optional Bauer BS2B binaural headphone crossfeed + 8D
        if (stereo > 0.01f)
            pre.Add($"extrastereo=m={F(1.0f + stereo * 1.10f)}:c=1");
        if (s.FxHeadphone3D)
            pre.Add("bs2b=profile=jmeier");
        if (s.PlayerAudio8D)
            pre.Add("apulsator=hz=0.14:width=1:amount=0.84");

        // 12. Post-reverb makeup + Psychoacoustic Mastering Clipper (`apsyclip`) + Lookahead Safety Limiter!
        var post = new List<string>(4);
        if (s.FxLimiter && (pre.Count > 0 || rev > 0.01f))
        {
            float makeup = Math.Clamp(preCutDb * 0.68f, 0f, 5.6f);
            if (makeup > 0.25f) post.Add($"volume=+{F(makeup)}dB");
            post.Add("apsyclip=level_in=1:level_out=0.96:clip=0.94:adaptive=0.52:iterations=8");
            post.Add("alimiter=level_in=1:level_out=0.96:limit=0.95:attack=4:release=45");
        }

        // 13. TRUE UNITY-CALIBRATED PARALLEL STEREO CONVOLUTION REVERB!
        // Dry stays 100% crisp & punchy while the wet bus passes through Abbey Road HP/LP + Pre-Delay + `afir` (dry=1:wet=1:irnorm=-1!) + wide stereo bloom!
        float effRev = s.PlayerAudio8D ? MathF.Max(rev, 0.30f) : rev;
        if (effRev > 0.01f)
        {
            var irPath = ImpulseBank.GetImpulsePath(s.FxReverbRoom, s.CustomIrPath);
            if (irPath != null)
            {
                var (preMs, hpHz, lpHz, wetScale, wetWide) = RoomParams(s.FxReverbRoom);
                // Smooth equal-power-inspired studio dry/wet mix curve:
                // 20% = crisp dry (0.98) + noticeable studio sheen (0.28)
                // 55% = rich dry (0.86) + huge lush hall/shimmer (0.78)
                // 100% = dreamy ambient dry (0.45) + massive wash (1.32)
                float dryWeight = Math.Clamp(1.0f - MathF.Pow(effRev, 1.85f) * 0.55f, 0.42f, 1.0f);
                float wetWeight = Math.Clamp(MathF.Pow(effRev, 1.05f) * 1.25f * wetScale, 0.06f, 1.45f);
                var escIr = ImpulseBank.EscapeForLavfi(irPath);
                string preChain = pre.Count > 0 ? string.Join(",", pre) + "," : "";
                string postChain = post.Count > 0 ? "," + string.Join(",", post) : "";
                return $"lavfi=[{preChain}asplit=2[dry][wet];[wet]highpass=f={hpHz}:poles=2,lowpass=f={lpHz}:poles=2,adelay={preMs}|{preMs}[wpre];amovie='{escIr}',aformat=channel_layouts=stereo:sample_rates=48000[ir];[wpre][ir]afir=dry=1:wet=1:irnorm=-1:minp=2048:maxp=8192,extrastereo=m={F(wetWide)}:c=1[rv];[dry][rv]amix=inputs=2:weights={F(dryWeight)} {F(wetWeight)}:normalize=0:duration=first{postChain}]";
            }
        }

        pre.AddRange(post);
        if (pre.Count == 0) return "";
        return "lavfi=[" + string.Join(",", pre) + "]";
    }

    /// <summary>
    /// Zero-clipping Hi-Fi filter graph:
    /// - At Volume &lt;= 100% with FX off: returns "" (100% bit-transparent pure 32-bit float bypass).
    /// - At Volume &gt; 100% with FX off: applies clean software gain + transparent lookahead true-peak limiter
    ///   so boosting to 101..150% NEVER causes WASAPI square-wave digital clipping!
    /// </summary>
    public static string BuildEffectiveFilterGraph(AppSettings s, double volumePct)
    {
        var graph = BuildFilterGraph(s);
        if (graph.Length > 0) return graph;
        if (volumePct > 100.5)
        {
            float boostDb = Math.Clamp((float)((volumePct - 100.0) * 0.16), 0.2f, 8.0f);
            return $"lavfi=[volume=+{F(boostDb)}dB,alimiter=level_in=1:level_out=0.985:limit=0.965:attack=3:release=45:asc=1]";
        }
        return "";
    }
}
