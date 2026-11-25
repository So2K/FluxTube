using System.Globalization;

namespace FluxTube.Services;

/// <summary>
/// Studio-calibrated DSP audio rack for libmpv (libavfilter / lavfi):
/// 10-band graphic EQ, clean low-shelf sub-bass punch, musical brilliance/air exciter + smooth bus glue compression
/// (no harsh digital aliasing), 3D Mid/Side stereo widener, 8D binaural orbit, lush mutually-prime multi-tap reverb
/// (Room / Plate / Hall / Cathedral), tape-locked Nightcore / Slowed+Reverb speed+pitch, formant-preserving rubberband
/// pitch shift, and transparent lookahead master bus limiter.
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
        bool Limiter);

    public static readonly Preset[] Presets =
    [
        // Studio
        new("Flat", "Чистый", "Flat", "studio", "Bypass",
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            0, "hall", 0, 0, 0, 1.00f, false, 0, false, true),

        new("Juicy", "Сочный Мастер", "Juicy Master", "studio", "Studio",
            [2.0f, 3.0f, 1.2f, -1.5f, -0.5f, 0.8f, 1.8f, 2.5f, 3.0f, 3.5f],
            10, "plate", 24, 38, 24, 1.00f, false, 0, false, true),

        new("WarmAnalog", "Тёплый Аналог", "Warm Analog", "studio", "Tube",
            [2.5f, 3.5f, 2.5f, 1.2f, 0.5f, 0.0f, -0.5f, -1.0f, -1.8f, -2.5f],
            14, "room", 22, 22, 16, 1.00f, false, 0, false, true),

        new("StudioRef", "Студийный Баланс", "Studio Ref", "studio", "Crisp",
            [0.8f, 1.0f, 0.0f, -0.8f, 0.0f, 0.5f, 1.2f, 1.8f, 2.0f, 1.5f],
            0, "room", 8, 20, 12, 1.00f, false, 0, false, true),

        new("CrystalAir", "Кристальный Верх", "Crystal Air", "studio", "Hi-Fi",
            [0.5f, 1.0f, 0.0f, -1.0f, -0.5f, 0.8f, 2.2f, 3.5f, 4.5f, 5.5f],
            12, "plate", 10, 52, 32, 1.00f, false, 0, false, true),

        // Bass & Club
        new("BassBoost", "Bass Boost", "Bass Boost", "bass", "Sub",
            [5.5f, 5.0f, 2.5f, -0.5f, -1.0f, 0.0f, 1.2f, 1.8f, 2.0f, 1.5f],
            8, "room", 58, 28, 20, 1.00f, false, 0, false, true),

        new("SubMonster", "Саб-Монстр", "Sub Monster", "bass", "808",
            [8.0f, 6.8f, 3.0f, -1.5f, -1.5f, 0.0f, 1.5f, 2.2f, 2.5f, 1.5f],
            6, "room", 82, 34, 24, 1.00f, false, 0, false, true),

        new("Phonk", "Phonk / Drift", "Phonk / Drift", "bass", "Punch",
            [5.0f, 4.2f, 1.5f, -2.0f, -0.5f, 1.8f, 3.5f, 4.2f, 3.5f, 2.8f],
            16, "plate", 56, 58, 36, 1.00f, false, 0, false, true),

        new("Trap808", "Трэп / 808", "Trap / 808", "bass", "Hip-Hop",
            [6.5f, 5.2f, 1.8f, -1.8f, -0.8f, 0.5f, 1.5f, 2.8f, 4.0f, 3.5f],
            10, "room", 62, 36, 26, 1.00f, false, 0, false, true),

        new("ClubEdm", "Клуб / EDM", "Club / EDM", "bass", "Wide",
            [4.5f, 4.0f, 1.5f, -1.5f, -0.8f, 0.8f, 2.2f, 3.2f, 3.8f, 3.5f],
            18, "hall", 45, 48, 44, 1.00f, false, 0, false, true),

        new("DnBJungle", "DnB / Jungle", "DnB / Jungle", "bass", "Break",
            [5.2f, 4.5f, 1.5f, -1.5f, 0.0f, 1.2f, 2.8f, 3.5f, 4.2f, 3.5f],
            12, "plate", 48, 44, 34, 1.00f, false, 0, false, true),

        new("RockMetal", "Рок / Метал", "Rock / Metal", "bass", "Drive",
            [3.5f, 3.0f, 1.0f, -2.0f, -1.2f, 1.5f, 3.2f, 3.8f, 2.8f, 2.0f],
            10, "room", 26, 42, 24, 1.00f, false, 0, false, true),

        // Vibes
        new("Nightcore", "Nightcore", "Nightcore", "vibes", "1.22×",
            [2.5f, 2.2f, 0.8f, -1.0f, 0.0f, 1.0f, 2.2f, 3.0f, 3.6f, 4.0f],
            14, "room", 22, 34, 26, 1.22f, true, 0, false, true),

        new("NightcoreUltra", "Nightcore Ultra", "Nightcore Ultra", "vibes", "1.30×",
            [3.8f, 3.0f, 1.2f, -1.5f, -0.5f, 1.5f, 2.8f, 3.8f, 4.4f, 4.8f],
            20, "plate", 34, 46, 38, 1.30f, true, 0, false, true),

        new("SlowedReverb", "Slowed + Reverb", "Slowed + Reverb", "vibes", "0.84×",
            [4.0f, 3.5f, 2.0f, 0.0f, -1.0f, -0.5f, 0.8f, 1.8f, 2.5f, 2.0f],
            68, "cathedral", 36, 20, 48, 0.84f, true, 0, false, true),

        new("DoomerNight", "Doomer / Ночь", "Doomer / Night", "vibes", "0.90×",
            [3.2f, 3.8f, 2.4f, 0.5f, -1.2f, -1.5f, -0.8f, 0.5f, -1.5f, -3.5f],
            54, "hall", 28, 14, 42, 0.90f, true, 0, false, true),

        new("Space8D", "8D Космос", "8D Space", "vibes", "3D",
            [2.5f, 2.2f, 0.8f, -1.0f, 0.0f, 0.5f, 1.8f, 2.6f, 3.2f, 3.5f],
            56, "cathedral", 26, 28, 52, 1.00f, false, 0, true, true),

        new("Lofi", "Lofi / Винил", "Lofi / Vinyl", "vibes", "Warm",
            [2.2f, 3.2f, 2.4f, 1.4f, 0.5f, -0.5f, -2.2f, -4.5f, -6.5f, -8.5f],
            32, "room", 18, 10, 18, 0.94f, true, 0, false, true),

        new("Vaporwave", "Vaporwave", "Vaporwave", "vibes", "0.78×",
            [3.5f, 3.0f, 1.5f, 0.5f, -0.8f, -0.5f, 1.0f, 2.0f, 1.2f, -1.0f],
            64, "cathedral", 30, 18, 62, 0.78f, true, 0, false, true),

        new("ConcertHall", "Концертный зал", "Concert Hall", "vibes", "Live",
            [2.0f, 2.5f, 1.0f, -0.5f, 0.0f, 0.8f, 1.8f, 2.5f, 2.2f, 1.5f],
            44, "hall", 20, 26, 46, 1.00f, false, 0, false, true),

        // Voice & Cinema
        new("Vocal", "Вокал / Подкаст", "Vocal / Podcast", "voice", "Clear",
            [-6.5f, -3.5f, -1.2f, 0.5f, 1.8f, 3.0f, 4.2f, 3.2f, 1.8f, 0.8f],
            6, "room", 0, 32, 8, 1.00f, false, 0, false, true),

        new("CinemaImax", "Кино / IMAX", "Cinema / IMAX", "voice", "Wide",
            [5.0f, 4.0f, 1.5f, -1.0f, 0.5f, 2.0f, 3.0f, 2.2f, 2.5f, 2.0f],
            22, "hall", 42, 34, 54, 1.00f, false, 0, false, true),

        new("LateNight", "Ночной режим", "Late Night", "voice", "Soft",
            [-3.0f, -1.5f, 0.5f, 1.2f, 2.0f, 2.5f, 2.5f, 1.5f, 0.5f, -1.0f],
            0, "room", 0, 48, 10, 1.00f, false, 0, false, true),
    ];

    public static readonly (string Key, string Label)[] Rooms =
    [
        ("room", "Комната"),
        ("plate", "Плейт"),
        ("hall", "Зал"),
        ("cathedral", "Космос"),
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

    public static string RoomLabel(string key) => key switch
    {
        "room" => L.T("Комната", "Room"),
        "plate" => L.T("Плейт", "Plate"),
        "hall" => L.T("Зал", "Hall"),
        "cathedral" => L.T("Космос", "Cathedral"),
        _ => key,
    };

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
    }

    public static void ApplySavedPreset(AppSettings s, SavedAudioPreset cp)
    {
        EnsureEqArray(s);
        s.AudioPreset = "user:" + cp.Name;
        s.AudioFxEnabled = true;
        Array.Clear(s.EqBands);
        if (cp.Eq != null) Array.Copy(cp.Eq, s.EqBands, Math.Min(cp.Eq.Length, Freqs.Length));
        s.FxReverb = cp.Reverb;
        s.FxReverbRoom = cp.Room;
        s.FxBass = cp.Bass;
        s.FxExciter = cp.Exciter;
        s.FxStereo = cp.Stereo;
        s.FxSpeed = cp.Speed;
        s.FxTapePitch = cp.TapePitch;
        s.FxPitchSemitones = cp.PitchSemi;
        s.PlayerAudio8D = cp.Space8D;
        s.FxLimiter = cp.Limiter;
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
        };
    }

    public static void MarkCustom(AppSettings s)
    {
        s.AudioFxEnabled = true;
        s.AudioPreset = "Custom";
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a clean, studio-grade mpv `af` filter graph (`lavfi=[...]`) free of harsh digital clipping.
    /// Uses musical shelving brilliance + smooth bus glue compression instead of aliasing crystalizer.
    /// </summary>
    public static string BuildFilterGraph(AppSettings s)
    {
        EnsureEqArray(s);
        if (!s.AudioFxEnabled)
        {
            return s.PlayerAudio8D
                ? "lavfi=[apulsator=hz=0.14:width=1:amount=0.85,aecho=0.82:0.84:37|71|113:0.32|0.22|0.14]"
                : "";
        }

        var parts = new List<string>(22);
        float bass = Math.Clamp(s.FxBass, 0f, 100f) / 100f;
        float exc = Math.Clamp(s.FxExciter, 0f, 100f) / 100f;
        float rev = Math.Clamp(s.FxReverb, 0f, 100f) / 100f;
        float stereo = Math.Clamp(s.FxStereo, 0f, 100f) / 100f;
        float maxBoost = 0f;
        for (int i = 0; i < Freqs.Length; i++)
            if (s.EqBands[i] > maxBoost) maxBoost = s.EqBands[i];

        // 1. Clean gain-staging headroom before boosts (eliminates internal clipping & mud)
        float preCutDb = Math.Clamp(maxBoost * 0.45f + bass * 4.8f + exc * 1.8f, 0f, 10.0f);
        if (preCutDb > 0.25f)
            parts.Add($"volume=-{F(preCutDb)}dB");

        // 2. Formant-preserving independent pitch shift when tape pitch isn't active
        if (!s.FxTapePitch && MathF.Abs(s.FxPitchSemitones) >= 0.25f)
        {
            double ratio = Math.Pow(2.0, Math.Clamp(s.FxPitchSemitones, -6f, 6f) / 12.0);
            parts.Add($"rubberband=pitch={ratio.ToString("0.0000", CultureInfo.InvariantCulture)}:formant=preserved");
        }

        // 3. 10-band musical constant-Q graphic EQ
        for (int i = 0; i < Freqs.Length; i++)
        {
            float g = Math.Clamp(s.EqBands[i], -12f, 12f);
            if (MathF.Abs(g) < 0.15f) continue;
            parts.Add($"equalizer=f={Freqs[i]}:t=q:w=1.41:g={F(g)}");
        }

        // 4. Clean low-shelf sub-bass punch (65Hz warm shelf — punchy without muddying vocals)
        if (bass > 0.01f)
            parts.Add($"bass=g={F(bass * 7.5f)}:f=65:t=s:w=0.55");

        // 5. Musical studio brilliance air shelf + smooth soft-knee glue compressor (zero harsh aliasing!)
        if (exc > 0.01f)
        {
            parts.Add($"treble=g={F(exc * 4.2f)}:f=6800:t=s:w=0.6");
            parts.Add($"acompressor=threshold=-18dB:ratio={F(1.6f + exc * 1.6f)}:attack=12:release=140:knee=4:makeup={F(0.8f + exc * 2.2f)}dB");
        }

        // 6. Mid/Side 3D stereo stage widening
        if (stereo > 0.01f)
            parts.Add($"extrastereo=m={F(1.0f + stereo * 1.10f)}:c=1");

        // 7. 8D binaural spatial orbit
        if (s.PlayerAudio8D)
            parts.Add("apulsator=hz=0.14:width=1:amount=0.85");

        // 8. Lush cascaded mutually-prime multi-tap studio reverb
        float effRev = s.PlayerAudio8D ? MathF.Max(rev, 0.24f) : rev;
        if (effRev > 0.01f)
        {
            float k = Math.Clamp(effRev, 0.04f, 1f);
            switch (s.FxReverbRoom)
            {
                case "room":
                    parts.Add($"aecho=0.86:0.86:23|41|67:{F(0.26f * k)}|{F(0.18f * k)}|{F(0.11f * k)}");
                    break;
                case "plate":
                    parts.Add($"aecho=0.82:0.88:19|37|61|97:{F(0.36f * k)}|{F(0.27f * k)}|{F(0.19f * k)}|{F(0.12f * k)}");
                    break;
                case "cathedral":
                    parts.Add($"aecho=0.78:0.90:43|79|131|193:{F(0.46f * k)}|{F(0.35f * k)}|{F(0.25f * k)}|{F(0.17f * k)}");
                    parts.Add($"aecho=0.85:0.86:67|113:{F(0.30f * k)}|{F(0.20f * k)}");
                    break;
                default: // "hall"
                    parts.Add($"aecho=0.81:0.88:31|59|97:{F(0.38f * k)}|{F(0.28f * k)}|{F(0.18f * k)}");
                    parts.Add($"aecho=0.86:0.84:47|83:{F(0.24f * k)}|{F(0.15f * k)}");
                    break;
            }
        }

        // 9. Transparent lookahead master bus limiter (loud, fat, 100% clip-free)
        if (s.FxLimiter && parts.Count > 0)
        {
            float makeup = Math.Clamp(preCutDb * 0.62f, 0f, 5.0f);
            if (makeup > 0.25f) parts.Add($"volume=+{F(makeup)}dB");
            parts.Add("alimiter=level_in=1:level_out=0.95:limit=0.93:attack=5:release=50");
        }

        return parts.Count > 0 ? "lavfi=[" + string.Join(',', parts) + "]" : "";
    }
}
