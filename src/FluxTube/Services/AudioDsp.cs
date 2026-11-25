using System.Globalization;

namespace FluxTube.Services;

/// <summary>
/// Studio-grade Mastering, True Stereo Convolution Reverb (`afir` + Abbey Road parallel bus), and Live DJ FX rack for libmpv:
/// - 10-band constant-Q EQ + 3-band DJ Isolator Kill switches (LOW / MID / HIGH KILL)
/// - Bi-polar Pioneer CDJ Resonant Filter (LPF &lt;- 0 -&gt; HPF)
/// - PS1 / SP-1200 Bitcrusher (`acrusher`) + Analog Tube/Tape Soft-Clip Drive (`asoftclip`) + DJ Jet Flanger (`flanger`)
/// - 100% bit-untouched Parallel Dry/Wet FFT Convolution Reverb (`asplit` + Abbey Road HP/LP + Pre-Delay + `afir` + `amix`)
///   with 7 synthesized studio/cinema/PSX IRs + Custom `.wav` IR loader (REAPER ReaVerb / Bricasti / Lexicon compatible!)
/// - Tape-locked Nightcore / Slowed+Shimmer + Formant-preserving pitch shift + Bauer BS2B Headphone 3D + Lookahead Limiter.
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
            12, "plate", 24, 38, 24, 1.00f, false, 0, false, true, Drive: 14),

        new("OttMaster", "OTT Гипер-Мастер", "OTT Hyper-Master", "studio", "Loud",
            [3.2f, 3.6f, 0.8f, -2.0f, -0.8f, 1.4f, 2.8f, 3.8f, 4.4f, 4.2f],
            10, "plate", 36, 72, 32, 1.00f, false, 0, false, true, Drive: 26),

        new("WarmAnalog", "Тёплая Лента", "Tape Warmth", "studio", "Tape",
            [2.4f, 3.2f, 2.2f, 1.0f, 0.4f, 0.0f, -0.6f, -1.2f, -2.0f, -2.8f],
            16, "vintage", 22, 24, 18, 1.00f, false, 0, false, true, Drive: 42),

        new("StudioRef", "Студийный Баланс", "Studio Ref", "studio", "Crisp",
            [0.8f, 1.0f, 0.0f, -0.8f, 0.0f, 0.5f, 1.2f, 1.8f, 2.0f, 1.5f],
            0, "room", 8, 20, 12, 1.00f, false, 0, false, true),

        new("CrystalAir", "Кристальный Верх", "Crystal Air", "studio", "Hi-Fi",
            [0.5f, 1.0f, 0.0f, -1.0f, -0.5f, 0.8f, 2.2f, 3.5f, 4.5f, 5.5f],
            14, "plate", 10, 52, 32, 1.00f, false, 0, false, true),

        new("Headphone3D", "Наушники 3D (BS2B)", "Headphone 3D", "studio", "Binaural",
            [1.4f, 1.6f, 0.4f, -0.6f, 0.0f, 0.6f, 1.4f, 2.0f, 2.4f, 2.2f],
            16, "room", 16, 22, 18, 1.00f, false, 0, false, true, Headphone3D: true),

        // Bass & Club
        new("BassBoost", "Bass Boost", "Bass Boost", "bass", "Sub",
            [5.5f, 5.0f, 2.5f, -0.5f, -1.0f, 0.0f, 1.2f, 1.8f, 2.0f, 1.5f],
            10, "room", 58, 28, 20, 1.00f, false, 0, false, true, Drive: 18),

        new("SubMonster", "Саб-Монстр", "Sub Monster", "bass", "808",
            [8.0f, 6.8f, 3.0f, -1.5f, -1.5f, 0.0f, 1.5f, 2.2f, 2.5f, 1.5f],
            8, "room", 82, 34, 24, 1.00f, false, 0, false, true, Drive: 28),

        new("Phonk", "Phonk / Drift", "Phonk / Drift", "bass", "Punch",
            [5.0f, 4.2f, 1.5f, -2.0f, -0.5f, 1.8f, 3.5f, 4.2f, 3.5f, 2.8f],
            18, "plate", 56, 56, 34, 1.00f, false, 0, false, true, Drive: 48),

        new("Trap808", "Трэп / 808", "Trap / 808", "bass", "Hip-Hop",
            [6.5f, 5.2f, 1.8f, -1.8f, -0.8f, 0.5f, 1.5f, 2.8f, 4.0f, 3.5f],
            12, "room", 62, 36, 26, 1.00f, false, 0, false, true, Drive: 20),

        new("ClubEdm", "Клуб / EDM", "Club / EDM", "bass", "Wide",
            [4.5f, 4.0f, 1.5f, -1.5f, -0.8f, 0.8f, 2.2f, 3.2f, 3.8f, 3.5f],
            22, "hall", 45, 48, 44, 1.00f, false, 0, false, true, Drive: 16),

        new("DnBJungle", "DnB / Jungle", "DnB / Jungle", "bass", "Break",
            [5.2f, 4.5f, 1.5f, -1.5f, 0.0f, 1.2f, 2.8f, 3.5f, 4.2f, 3.5f],
            15, "plate", 48, 44, 34, 1.00f, false, 0, false, true, Drive: 22),

        new("RockMetal", "Рок / Метал", "Rock / Metal", "bass", "Drive",
            [3.5f, 3.0f, 1.0f, -2.0f, -1.2f, 1.5f, 3.2f, 3.8f, 2.8f, 2.0f],
            12, "room", 26, 42, 24, 1.00f, false, 0, false, true, Drive: 32),

        // Vibes & Shimmer
        new("Nightcore", "Nightcore", "Nightcore", "vibes", "1.22×",
            [2.5f, 2.2f, 0.8f, -1.0f, 0.0f, 1.0f, 2.2f, 3.0f, 3.6f, 4.0f],
            16, "plate", 24, 34, 26, 1.22f, true, 0, false, true),

        new("NightcoreUltra", "Hyperpop / Nightcore+", "Hyperpop / Nightcore+", "vibes", "1.30×",
            [3.8f, 3.0f, 1.2f, -1.5f, -0.5f, 1.5f, 2.8f, 3.8f, 4.6f, 5.0f],
            22, "plate", 34, 48, 38, 1.30f, true, 0, false, true, Drive: 24),

        new("ValhallaShimmer", "Valhalla Шиммер", "Valhalla Shimmer", "vibes", "Dream",
            [2.0f, 2.2f, 0.6f, -1.2f, -0.4f, 0.6f, 1.8f, 2.8f, 3.8f, 4.2f],
            58, "shimmer", 20, 26, 52, 1.00f, false, 0, false, true),

        new("SlowedReverb", "Slowed + Shimmer", "Slowed + Shimmer", "vibes", "0.84×",
            [3.8f, 3.4f, 1.6f, -0.8f, -1.0f, -0.2f, 1.2f, 2.0f, 2.8f, 2.5f],
            64, "shimmer", 34, 22, 48, 0.84f, true, 0, false, true),

        new("DoomerNight", "Doomer / Ночь", "Doomer / Night", "vibes", "0.90×",
            [3.2f, 3.8f, 2.2f, 0.2f, -1.2f, -1.5f, -0.8f, 0.4f, -1.5f, -3.5f],
            50, "vintage", 28, 14, 42, 0.90f, true, 0, false, true, Drive: 22),

        new("Space8D", "8D Космос", "8D Space", "vibes", "3D",
            [2.5f, 2.2f, 0.8f, -1.0f, 0.0f, 0.5f, 1.8f, 2.6f, 3.2f, 3.5f],
            52, "shimmer", 26, 28, 52, 1.00f, false, 0, true, true),

        new("Vaporwave", "Vaporwave 80s", "Vaporwave 80s", "vibes", "0.78×",
            [3.5f, 3.0f, 1.4f, 0.4f, -0.8f, -0.5f, 1.0f, 1.8f, 1.0f, -1.5f],
            58, "vintage", 30, 18, 60, 0.78f, true, 0, false, true, Drive: 28, Mod: 32),

        new("ConcertHall", "Lexicon 480L Зал", "Lexicon 480L Hall", "vibes", "Live",
            [2.0f, 2.4f, 0.8f, -0.6f, 0.0f, 0.8f, 1.8f, 2.5f, 2.2f, 1.6f],
            42, "hall", 20, 26, 44, 1.00f, false, 0, false, true),

        // DJ & Retro Hardware
        new("PsxJungle", "PS1 / PSX SPU", "PS1 / PSX SPU", "dj", "1996",
            [3.2f, 3.8f, 2.0f, 0.4f, -0.6f, 0.8f, 1.6f, 0.6f, -2.5f, -5.5f],
            46, "psx", 32, 20, 28, 1.00f, false, 0, false, true, Crush: 28, Drive: 22),

        new("Lofi", "SP-1200 / Винил", "SP-1200 / Vinyl", "dj", "Lo-Fi",
            [2.4f, 3.4f, 2.2f, 1.2f, 0.4f, -0.5f, -2.0f, -4.2f, -6.5f, -8.5f],
            30, "vintage", 20, 12, 18, 0.94f, true, 0, false, true, Crush: 36, Drive: 48),

        new("BitcrushHyper", "Bitcrush / Breakcore", "Bitcrush / Breakcore", "dj", "8-Bit",
            [4.0f, 3.5f, 1.0f, -1.5f, 0.0f, 1.6f, 3.0f, 3.8f, 3.2f, 2.0f],
            20, "psx", 42, 40, 32, 1.00f, false, 0, false, true, Crush: 62, Drive: 44),

        new("DjJetSwirl", "DJ Jet Flanger", "DJ Jet Flanger", "dj", "Club",
            [3.5f, 3.0f, 1.0f, -1.2f, 0.0f, 1.0f, 2.2f, 3.0f, 3.2f, 2.5f],
            26, "hall", 36, 36, 44, 1.00f, false, 0, false, true, Mod: 66),

        new("ClubUnderwater", "Клуб за стеной (LPF)", "Outside Club (LPF)", "dj", "LPF",
            [5.0f, 4.5f, 2.0f, 0.0f, -1.0f, -1.0f, -1.0f, 0.0f, 0.0f, 0.0f],
            42, "hall", 52, 10, 28, 1.00f, false, 0, false, true, DjFilter: -68),

        // Voice & Cinema
        new("CinemaImax", "Кино / IMAX Зал", "Cinema / IMAX", "voice", "IMAX",
            [4.5f, 3.8f, 1.2f, -1.0f, 0.4f, 1.8f, 2.8f, 2.4f, 2.6f, 2.2f],
            34, "cinema", 40, 34, 54, 1.00f, false, 0, false, true),

        new("Vocal", "Вокал / Подкаст", "Vocal / Podcast", "voice", "Clear",
            [-6.5f, -3.5f, -1.2f, 0.5f, 1.8f, 3.0f, 4.2f, 3.2f, 1.8f, 0.8f],
            6, "room", 0, 32, 8, 1.00f, false, 0, false, true),

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

    private static (int PreDelayMs, int HpHz, int LpHz, float WetScale) RoomParams(string room) => room switch
    {
        "room" => (14, 170, 11500, 0.72f),
        "plate" => (22, 230, 13500, 0.84f),
        "cinema" => (38, 165, 11000, 0.92f),
        "shimmer" or "cathedral" => (42, 210, 14000, 0.96f),
        "psx" => (18, 200, 7800, 0.88f),
        "vintage" => (26, 220, 6800, 0.86f),
        "custom" => (24, 180, 12500, 0.88f),
        _ => (32, 195, 11500, 0.86f), // "hall" 480L
    };

    /// <summary>
    /// Builds a clean, studio-grade mpv `af` filter graph (`lavfi=[...]`) featuring:
    /// - True parallel wet/dry FFT stereo convolution reverb (`asplit` + Abbey Road EQ + Pre-Delay + `afir` + `amix`)
    ///   so dry vocals &amp; drums stay 100% crisp, punchy, and comb-free!
    /// - Live DJ Color Filter, 3-band Isolator Kills, PS1 Bitcrusher, Analog Tube/Tape Drive, Jet Flanger, BS2B, and Lookahead Limiter.
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
                return $"lavfi=[apulsator=hz=0.14:width=1:amount=0.82,asplit=2[d8][w8];[w8]highpass=f=200:poles=2,adelay=28|28[wp8];amovie='{esc8}',aresample=48000[ir8];[wp8][ir8]afir=dry=0:wet=10:irnorm=-1[rv8];[d8][rv8]amix=inputs=2:weights=1 0.34:normalize=0:duration=first]";
            }
            return "lavfi=[apulsator=hz=0.14:width=1:amount=0.82]";
        }

        var pre = new List<string>(24);
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

        // 1. Gain-staging headroom before boosts (zero internal clipping)
        float preCutDb = Math.Clamp(maxBoost * 0.42f + bass * 4.2f + exc * 1.6f + drive * 2.4f, 0f, 10.5f);
        if (preCutDb > 0.25f)
            pre.Add($"volume=-{F(preCutDb)}dB");

        // 2. Formant-preserving independent pitch shift when tape pitch isn't active
        if (!s.FxTapePitch && MathF.Abs(s.FxPitchSemitones) >= 0.25f)
        {
            double ratio = Math.Pow(2.0, Math.Clamp(s.FxPitchSemitones, -6f, 6f) / 12.0);
            pre.Add($"rubberband=pitch={ratio.ToString("0.0000", CultureInfo.InvariantCulture)}:formant=preserved");
        }

        // 3. 3-Band DJ Isolator Kill Switches (Pioneer DJM-900 style instant kills!)
        if (s.FxKillLow) pre.Add("highpass=f=280:poles=2");
        if (s.FxKillMid) pre.Add("equalizer=f=1000:t=o:w=2.0:g=-28");
        if (s.FxKillHigh) pre.Add("lowpass=f=3200:poles=2");

        // 4. Bi-Polar Pioneer CDJ Resonant DJ Color Filter (-100 = LPF muffle, +100 = HPF drop riser!)
        if (djf < -0.02f)
        {
            float t = -djf; // 0..1
            float cutoff = 18000f * MathF.Pow(160f / 18000f, t);
            float q = 0.85f + t * 1.45f;
            pre.Add($"lowpass=f={F(cutoff)}:poles=2:width_type=q:w={F(q)}");
        }
        else if (djf > 0.02f)
        {
            float t = djf;
            float cutoff = 35f * MathF.Pow(4200f / 35f, t);
            float q = 0.85f + t * 1.45f;
            pre.Add($"highpass=f={F(cutoff)}:poles=2:width_type=q:w={F(q)}");
        }

        // 5. 10-band musical constant-Q graphic EQ
        for (int i = 0; i < Freqs.Length; i++)
        {
            float g = Math.Clamp(s.EqBands[i], -12f, 12f);
            if (MathF.Abs(g) < 0.15f) continue;
            pre.Add($"equalizer=f={Freqs[i]}:t=q:w=1.41:g={F(g)}");
        }

        // 6. Clean low-shelf sub-bass punch (65Hz warm shelf)
        if (bass > 0.01f && !s.FxKillLow)
            pre.Add($"bass=g={F(bass * 7.5f)}:f=65:t=s:w=0.55");

        // 7. Analog Tube/Tape Soft-Clip Drive + subtle vinyl pitch flutter
        if (drive > 0.02f)
        {
            float boostDb = drive * 7.5f;
            pre.Add($"volume=+{F(boostDb)}dB");
            pre.Add("asoftclip=type=tanh:threshold=0.78:output=0.92");
            pre.Add($"volume=-{F(boostDb * 0.58f)}dB");
            if (drive > 0.35f)
                pre.Add($"vibrato=f=1.6:d={F((drive - 0.35f) * 0.045f)}");
        }

        // 8. PS1 / SP-1200 Lo-Fi Bitcrusher & Sample-Rate Decimator
        if (crush > 0.02f)
        {
            float bits = Math.Clamp(14.5f - crush * 9.5f, 4.5f, 15f);
            int samples = Math.Clamp((int)MathF.Round(1f + crush * crush * 9f), 1, 10);
            float mix = Math.Clamp(0.28f + crush * 0.72f, 0.25f, 1f);
            pre.Add($"acrusher=level_in=1:level_out=1:bits={F(bits)}:samples={samples}:mode=log:aa=0.35:mix={F(mix)}");
        }

        // 9. Musical studio brilliance air shelf + smooth soft-knee bus glue compressor
        if (exc > 0.01f)
        {
            pre.Add($"treble=g={F(exc * 4.0f)}:f=6800:t=s:w=0.6");
            pre.Add($"acompressor=threshold=-18dB:ratio={F(1.6f + exc * 1.8f)}:attack=10:release=125:knee=4:makeup={F(0.8f + exc * 2.2f)}dB");
        }

        // 10. DJ Jet Flanger / Stereo Swirl Modulation
        if (mod > 0.02f)
        {
            float depth = 1.2f + mod * 5.2f;
            float regen = 18f + mod * 54f;
            float speed = 0.22f + mod * 0.65f;
            pre.Add($"flanger=delay=2.5:depth={F(depth)}:regen={F(regen)}:width=75:speed={F(speed)}:shape=sin:phase=45");
        }

        // 11. Mid/Side 3D stereo widening & optional Bauer BS2B binaural headphone crossfeed
        if (stereo > 0.01f)
            pre.Add($"extrastereo=m={F(1.0f + stereo * 1.05f)}:c=1");
        if (s.FxHeadphone3D)
            pre.Add("bs2b=profile=jmeier");
        if (s.PlayerAudio8D)
            pre.Add("apulsator=hz=0.14:width=1:amount=0.84");

        // 12. Post-reverb makeup & transparent lookahead master bus limiter
        var post = new List<string>(4);
        if (s.FxLimiter && (pre.Count > 0 || rev > 0.01f))
        {
            float makeup = Math.Clamp(preCutDb * 0.64f, 0f, 5.2f);
            if (makeup > 0.25f) post.Add($"volume=+{F(makeup)}dB");
            post.Add("alimiter=level_in=1:level_out=0.95:limit=0.93:attack=5:release=50");
        }

        // 13. TRUE PARALLEL STEREO CONVOLUTION REVERB (`asplit` + Abbey Road HP/LP + Pre-Delay + `afir` + `amix`)!
        // Dry signal stays 100% bit-untouched -> ZERO comb filtering, ZERO dullness, ZERO muddy sub-bass!
        float effRev = s.PlayerAudio8D ? MathF.Max(rev, 0.26f) : rev;
        if (effRev > 0.01f)
        {
            var irPath = ImpulseBank.GetImpulsePath(s.FxReverbRoom, s.CustomIrPath);
            if (irPath != null)
            {
                var (preMs, hpHz, lpHz, wetScale) = RoomParams(s.FxReverbRoom);
                // Smooth perceptual wet curve (gentle at 10-30% for mastering polish, lush & huge at 50-100% for Slowed/Shimmer!)
                float wetWeight = Math.Clamp(MathF.Pow(effRev, 1.15f) * 1.15f * wetScale, 0.03f, 1.35f);
                var escIr = ImpulseBank.EscapeForLavfi(irPath);
                string preChain = pre.Count > 0 ? string.Join(",", pre) + "," : "";
                string postChain = post.Count > 0 ? "," + string.Join(",", post) : "";
                return $"lavfi=[{preChain}asplit=2[dry][wet];[wet]highpass=f={hpHz}:poles=2,lowpass=f={lpHz}:poles=2,adelay={preMs}|{preMs}[wpre];amovie='{escIr}',aresample=48000[ir];[wpre][ir]afir=dry=0:wet=10:irnorm=-1[rv];[dry][rv]amix=inputs=2:weights=1 {F(wetWeight)}:normalize=0:duration=first{postChain}]";
            }
        }

        pre.AddRange(post);
        if (pre.Count == 0) return "";
        return "lavfi=[" + string.Join(",", pre) + "]";
    }
}
