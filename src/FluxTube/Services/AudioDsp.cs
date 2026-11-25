using System.Globalization;

namespace FluxTube.Services;

/// <summary>
/// Reaper-inspired studio DSP audio chain for libmpv (libavfilter / lavfi):
/// 10-band graphic EQ, cascaded mutually-prime multi-tap studio reverb (Room / Plate / Hall / Cathedral),
/// clean sub-bass punch, harmonic exciter + glue compression, 3D stereo widener, 8D spatial orbit,
/// tape-locked Nightcore / Slowed+Reverb speed+pitch, formant-preserving rubberband pitch shift,
/// and transparent master bus limiter so heavy boosts stay loud, punchy, and distortion-free.
/// </summary>
public static class AudioDsp
{
    public static readonly int[] Freqs = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public static readonly string[] FreqLabels = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    public sealed record Preset(
        string Id,
        string Name,
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
        new("Flat", "Чистый", "Bypass",
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            0, "hall", 0, 0, 0, 1.00f, false, 0, false, true),

        new("Juicy", "Сочный Мастер", "Studio",
            [2.5f, 3.5f, 1.5f, -1.5f, -0.5f, 0.8f, 2.0f, 2.8f, 3.2f, 3.8f],
            14, "plate", 32, 46, 30, 1.00f, false, 0, false, true),

        new("Nightcore", "Nightcore", "1.22×",
            [3.0f, 2.5f, 1.0f, -1.0f, 0.0f, 1.0f, 2.5f, 3.5f, 4.0f, 4.5f],
            16, "room", 26, 38, 28, 1.22f, true, 0, false, true),

        new("NightcoreUltra", "Nightcore Ultra", "1.30×",
            [4.5f, 3.5f, 1.5f, -1.5f, -0.5f, 1.5f, 3.0f, 4.5f, 5.0f, 5.5f],
            22, "plate", 40, 54, 42, 1.30f, true, 0, false, true),

        new("SlowedReverb", "Slowed + Reverb", "0.83×",
            [4.5f, 3.8f, 2.2f, 0.0f, -1.0f, -0.5f, 1.0f, 2.2f, 3.0f, 2.5f],
            72, "cathedral", 42, 24, 52, 0.83f, true, 0, false, true),

        new("BassBoost", "Bass Boost", "Sub",
            [6.5f, 5.8f, 3.2f, 0.0f, -1.0f, 0.0f, 1.5f, 2.0f, 2.5f, 2.0f],
            10, "room", 78, 32, 24, 1.00f, false, 0, false, true),

        new("Phonk", "Phonk / Hard", "Punch",
            [5.5f, 4.8f, 2.0f, -2.0f, -1.0f, 1.5f, 3.5f, 4.5f, 4.0f, 3.2f],
            18, "plate", 64, 68, 40, 1.00f, false, 0, false, true),

        new("Space8D", "8D Космос", "3D",
            [3.0f, 2.5f, 1.0f, -1.0f, 0.0f, 0.5f, 2.0f, 3.0f, 3.5f, 4.0f],
            60, "cathedral", 32, 32, 56, 1.00f, false, 0, true, true),

        new("Lofi", "Lofi / Винил", "Warm",
            [2.5f, 3.5f, 2.5f, 1.5f, 0.5f, -0.5f, -2.0f, -4.5f, -6.5f, -8.0f],
            36, "room", 22, 12, 20, 0.94f, true, 0, false, true),

        new("Vocal", "Вокал / Подкаст", "Clear",
            [-6.0f, -3.0f, -1.0f, 0.5f, 1.5f, 2.8f, 4.2f, 3.5f, 2.0f, 1.0f],
            8, "room", 0, 38, 10, 1.00f, false, 0, false, true),
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

    public static string PresetLabel(Preset p) => p.Id switch
    {
        "Flat" => L.T("Чистый", "Flat"),
        "Juicy" => L.T("Сочный Мастер", "Juicy Master"),
        "Space8D" => L.T("8D Космос", "8D Space"),
        "Lofi" => L.T("Lofi / Винил", "Lofi / Vinyl"),
        "Vocal" => L.T("Вокал / Подкаст", "Vocal / Podcast"),
        _ => p.Name,
    };

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
        return p != null ? PresetLabel(p) : L.T("Свой", "Custom");
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

    public static void MarkCustom(AppSettings s)
    {
        s.AudioFxEnabled = true;
        s.AudioPreset = "Custom";
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the exact mpv `af` option string (`lavfi=[...]`) or empty string when bypassed.
    /// Uses mutually-prime delay taps for lush metallic-free reverb and transparent headroom/limiter staging.
    /// </summary>
    public static string BuildFilterGraph(AppSettings s)
    {
        EnsureEqArray(s);
        if (!s.AudioFxEnabled)
        {
            // Keep legacy 8D toggle working seamlessly even when full DSP Studio is bypassed
            return s.PlayerAudio8D
                ? "lavfi=[apulsator=hz=0.14:width=1:amount=0.85,aecho=0.82:0.84:37|71|113:0.32|0.22|0.14]"
                : "";
        }

        var parts = new List<string>(20);
        float bass = Math.Clamp(s.FxBass, 0f, 100f) / 100f;
        float exc = Math.Clamp(s.FxExciter, 0f, 100f) / 100f;
        float rev = Math.Clamp(s.FxReverb, 0f, 100f) / 100f;
        float stereo = Math.Clamp(s.FxStereo, 0f, 100f) / 100f;
        float maxBoost = 0f;
        for (int i = 0; i < Freqs.Length; i++)
            if (s.EqBands[i] > maxBoost) maxBoost = s.EqBands[i];

        // 1. Clean gain-staging headroom before heavy boosts (prevents internal float clipping)
        float preCutDb = Math.Clamp(maxBoost * 0.42f + bass * 4.5f + exc * 1.4f, 0f, 8.5f);
        if (preCutDb > 0.3f)
            parts.Add($"volume=-{F(preCutDb)}dB");

        // 2. Formant-preserving independent pitch shift when tape pitch isn't active
        if (!s.FxTapePitch && MathF.Abs(s.FxPitchSemitones) >= 0.25f)
        {
            double ratio = Math.Pow(2.0, Math.Clamp(s.FxPitchSemitones, -6f, 6f) / 12.0);
            parts.Add($"rubberband=pitch={ratio.ToString("0.0000", CultureInfo.InvariantCulture)}:formant=preserved");
        }

        // 3. 10-band studio graphic EQ
        for (int i = 0; i < Freqs.Length; i++)
        {
            float g = Math.Clamp(s.EqBands[i], -12f, 12f);
            if (MathF.Abs(g) < 0.15f) continue;
            parts.Add($"equalizer=f={Freqs[i]}:t=q:w=1.4:g={F(g)}");
        }

        // 4. Clean low-shelf sub-bass punch
        if (bass > 0.01f)
            parts.Add($"bass=g={F(bass * 8.5f)}:f=72:w=0.65");

        // 5. Juicy harmonic exciter + warm Reaper-style bus glue compression
        if (exc > 0.01f)
        {
            parts.Add($"crystalizer=i={F(0.9f + exc * 2.6f)}:c=1");
            parts.Add($"acompressor=threshold=-16dB:ratio={F(1.8f + exc * 1.4f)}:attack=10:release=115:makeup={F(1.2f + exc * 2.8f)}dB");
        }

        // 6. Mid/Side 3D stereo stage widening
        if (stereo > 0.01f)
            parts.Add($"extrastereo=m={F(1.0f + stereo * 1.15f)}:c=1");

        // 7. 8D binaural spatial orbit
        if (s.PlayerAudio8D)
            parts.Add("apulsator=hz=0.14:width=1:amount=0.85");

        // 8. Lush cascaded mutually-prime multi-tap studio reverb (zero metallic ringing)
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

        // 9. Transparent master bus soft limiter (keeps bass & master loud, fat, and 100% clip-free)
        if (s.FxLimiter && parts.Count > 0)
        {
            float makeup = Math.Clamp(preCutDb * 0.65f, 0f, 5.2f);
            if (makeup > 0.3f) parts.Add($"volume=+{F(makeup)}dB");
            parts.Add("alimiter=level_in=1:level_out=0.96:limit=0.94:attack=4:release=45");
        }

        return parts.Count > 0 ? "lavfi=[" + string.Join(',', parts) + "]" : "";
    }
}
