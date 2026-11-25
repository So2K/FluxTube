using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Synthesized PS1/PS2-era UI sound effects inspired by Silent Hill 1/2 and Metal Gear Solid 1/2,
/// with per-slot custom sound replacement (WAV, MP3, OGG, FLAC, M4A), 1-click ZIP soundpack import,
/// adjustable master UI sound volume (10..300%), video-volume-scaled slider ticks, and stereo 8D reverb.
/// </summary>
public static partial class Sfx
{
    private const int Rate = 44100;
    private const int MaxWavBytes = 6 * 1024 * 1024;
    private const int VolTiers = 11; // 0..10 amplitude tiers mapped to video playback volume
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    public static readonly (string Key, string Label)[] Slots =
    [
        ("click", "Клик (кнопки, меню)"),
        ("video_open", "Открытие видео"),
        ("like", "Лайк / дизлайк"),
        ("channel", "Переход на канал"),
        ("volume", "Громкость (плеер / вкладки / ползунки)"),
        ("toggle_on", "Переключатель (вкл)"),
        ("toggle_off", "Переключатель (выкл)"),
        ("tab", "Вкладка"),
        ("back", "Назад / закрытие"),
    ];

    public const string NamingGuide =
        "Файлы в .zip (WAV, MP3, OGG, FLAC, M4A):\n" +
        "• click.mp3 — клик по кнопкам\n" +
        "• video_open.mp3 — открытие видео\n" +
        "• like.mp3 — лайк / дизлайк\n" +
        "• channel.mp3 — переход на канал\n" +
        "• volume.mp3 — громкость (плеер / вкладка)\n" +
        "• toggle_on.mp3 / toggle_off.mp3 — тумблеры\n" +
        "• tab.mp3 — вкладка   • back.mp3 — назад";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["click"] = "click", ["btn"] = "click", ["button"] = "click", ["confirm"] = "click", ["ok"] = "click", ["select"] = "click",
        ["video_open"] = "video_open", ["video"] = "video_open", ["open"] = "video_open", ["play"] = "video_open", ["start"] = "video_open",
        ["like"] = "like", ["heart"] = "like", ["rate"] = "like", ["fav"] = "like",
        ["channel"] = "channel", ["ch"] = "channel", ["profile"] = "channel", ["author"] = "channel",
        ["volume"] = "volume", ["vol"] = "volume", ["tick"] = "volume", ["slider"] = "volume", ["scroll"] = "volume", ["wheel"] = "volume",
        ["toggle_on"] = "toggle_on", ["on"] = "toggle_on", ["switch_on"] = "toggle_on", ["enable"] = "toggle_on",
        ["toggle_off"] = "toggle_off", ["off"] = "toggle_off", ["switch_off"] = "toggle_off", ["disable"] = "toggle_off",
        ["tab"] = "tab", ["newtab"] = "tab", ["codec"] = "tab",
        ["back"] = "back", ["close"] = "back", ["cancel"] = "back", ["exit"] = "back",
    };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".opus", ".aac", ".wma" };

    private static string SfxDir
    {
        get
        {
            var d = Path.Combine(Paths.Data, "sfx");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    [LibraryImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool PlaySoundW(byte* pszSound, nint hmod, uint fdwSound);

    private static readonly Dictionary<string, float[]> _rawBuiltin = new();
    private static readonly Dictionary<string, float[]> _rawCustom = new();
    private static readonly Dictionary<string, nint> _rendered = new();
    private static readonly nint[] _volTierBufs = new nint[VolTiers];
    private static readonly object _lock = new();
    private static volatile bool _ready;
    private static long _lastTickAt, _lastClickAt, _lastVolAt;

    private static void EnsureInit()
    {
        if (_ready) return;
        lock (_lock)
        {
            if (_ready) return;

            // Built-in synthesized mono float PCM (master ~0.10 so 100% is subtle, 200-300% is punchy)
            _rawBuiltin["click"] = Synth(0.062, t =>
            {
                double env = Math.Exp(-t * 62.0) * Math.Min(1.0, t / 0.002);
                double f1 = 1046.5 * (1.0 + 0.03 * Math.Exp(-t * 140.0));
                double fm = Math.Sin(2 * Math.PI * f1 * t + 0.30 * Math.Sin(2 * Math.PI * 1568.0 * t) * Math.Exp(-t * 95.0));
                return fm * env * 0.11;
            });

            _rawBuiltin["video_open"] = Synth(0.145, t =>
            {
                double env1 = Math.Exp(-t * 22.0) * Math.Min(1.0, t / 0.004);
                double t2 = Math.Max(0.0, t - 0.028);
                double env2 = t > 0.028 ? Math.Exp(-t2 * 24.0) * Math.Min(1.0, t2 / 0.004) : 0.0;
                double low = Math.Sin(2 * Math.PI * 392.0 * t) * env1 * 0.45;
                double mid = Math.Sin(2 * Math.PI * 587.33 * t + 0.25 * Math.Sin(2 * Math.PI * 1174.66 * t) * Math.Exp(-t * 40.0)) * env1 * 0.55;
                double shimmer = Math.Sin(2 * Math.PI * 880.0 * t2) * env2 * 0.65;
                return (low + mid + shimmer) * 0.095;
            });

            _rawBuiltin["like"] = Synth(0.115, t =>
            {
                double env1 = Math.Exp(-t * 38.0) * Math.Min(1.0, t / 0.002);
                double t2 = Math.Max(0.0, t - 0.032);
                double env2 = t > 0.032 ? Math.Exp(-t2 * 30.0) * Math.Min(1.0, t2 / 0.003) : 0.0;
                double n1 = Math.Sin(2 * Math.PI * 880.0 * t) * env1 * 0.55;
                double n2 = (Math.Sin(2 * Math.PI * 1318.5 * t2) + 0.28 * Math.Sin(2 * Math.PI * 2637.0 * t2) * Math.Exp(-t2 * 55.0)) * env2 * 0.72;
                return (n1 + n2) * 0.105;
            });

            _rawBuiltin["channel"] = Synth(0.110, t =>
            {
                double env = Math.Exp(-t * 32.0) * Math.Min(1.0, t / 0.003);
                double freq = 523.25 + 261.6 * (1.0 - Math.Exp(-t * 45.0));
                double s = Math.Sin(2 * Math.PI * freq * t) + 0.3 * Math.Sin(2 * Math.PI * freq * 1.5 * t) * Math.Exp(-t * 50.0);
                return s * env * 0.10;
            });

            _rawBuiltin["volume"] = Synth(0.026, t =>
            {
                double env = Math.Exp(-t * 155.0) * Math.Min(1.0, t / 0.001);
                double s = Math.Sin(2 * Math.PI * 1580.0 * t) + 0.28 * Math.Sin(2 * Math.PI * 3160.0 * t);
                return s * env * 0.095;
            });

            _rawBuiltin["toggle_on"] = Synth(0.052, t =>
            {
                double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
                double freq = 460.0 + 240.0 * (1.0 - Math.Exp(-t * 95.0));
                double body = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2.02 * t);
                double snap = Math.Sin(2 * Math.PI * 2200.0 * t) * Math.Exp(-t * 320.0) * 0.35;
                return (body * 0.75 + snap) * env * 0.105;
            });

            _rawBuiltin["toggle_off"] = Synth(0.052, t =>
            {
                double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
                double freq = 540.0 - 210.0 * (1.0 - Math.Exp(-t * 85.0));
                double body = Math.Sin(2 * Math.PI * freq * t) + 0.22 * Math.Sin(2 * Math.PI * freq * 1.98 * t);
                double snap = Math.Sin(2 * Math.PI * 1800.0 * t) * Math.Exp(-t * 340.0) * 0.30;
                return (body * 0.75 + snap) * env * 0.10;
            });

            _rawBuiltin["tab"] = Synth(0.085, t =>
            {
                double env1 = Math.Exp(-t * 48.0) * Math.Min(1.0, t / 0.002);
                double t2 = Math.Max(0.0, t - 0.022);
                double env2 = t > 0.022 ? Math.Exp(-t2 * 52.0) * Math.Min(1.0, t2 / 0.002) : 0.0;
                double s1 = Math.Sin(2 * Math.PI * 698.46 * t) * env1 * 0.55;
                double s2 = Math.Sin(2 * Math.PI * 1046.5 * t2) * env2 * 0.75;
                return (s1 + s2) * 0.10;
            });

            _rawBuiltin["back"] = Synth(0.072, t =>
            {
                double env = Math.Exp(-t * 50.0) * Math.Min(1.0, t / 0.002);
                double freq = 470.0 - 150.0 * Math.Min(1.0, t / 0.045);
                double s = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 1.5 * t) * Math.Exp(-t * 80.0);
                return s * env * 0.10;
            });

            var oldTick = Path.Combine(SfxDir, "tick.wav");
            var volWav = Path.Combine(SfxDir, "volume.wav");
            if (File.Exists(oldTick) && !File.Exists(volWav))
            {
                try { File.Move(oldTick, volWav); } catch { }
            }

            foreach (var (key, _) in Slots)
            {
                var path = Path.Combine(SfxDir, key + ".wav");
                if (File.Exists(path)) LoadCustomPcmLocked(key, path);
            }
            RebuildAllLocked();
            _ready = true;
        }
    }

    private static float[] Synth(double seconds, Func<double, double> sample)
    {
        int n = (int)(Rate * seconds);
        var buf = new float[n];
        for (int i = 0; i < n; i++) buf[i] = (float)sample((double)i / Rate);
        return buf;
    }

    private static float[]? ReadWavPcm(string wavPath)
    {
        try
        {
            var fi = new FileInfo(wavPath);
            if (!fi.Exists || fi.Length < 44 || fi.Length > MaxWavBytes) return null;
            var bytes = File.ReadAllBytes(wavPath);
            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;
            int channels = 1, bits = 16;
            int pos = 12;
            int dataOff = -1, dataLen = 0;
            while (pos + 8 <= bytes.Length)
            {
                int sz = BitConverter.ToInt32(bytes, pos + 4);
                if (sz < 0 || pos + 8 + sz > bytes.Length) break;
                if (bytes[pos] == 'f' && bytes[pos + 1] == 'm' && bytes[pos + 2] == 't' && bytes[pos + 3] == ' ' && sz >= 16)
                {
                    channels = BitConverter.ToInt16(bytes, pos + 10);
                    bits = BitConverter.ToInt16(bytes, pos + 22);
                }
                else if (bytes[pos] == 'd' && bytes[pos + 1] == 'a' && bytes[pos + 2] == 't' && bytes[pos + 3] == 'a')
                {
                    dataOff = pos + 8;
                    dataLen = sz;
                    break;
                }
                pos += 8 + sz + (sz & 1);
            }
            if (dataOff < 0 || bits != 16 || channels < 1 || channels > 2) return null;
            int frames = Math.Min(Rate * 3, dataLen / (2 * channels));
            var pcm = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                if (channels == 1)
                    pcm[i] = BitConverter.ToInt16(bytes, dataOff + i * 2) / 32768f;
                else
                {
                    float l = BitConverter.ToInt16(bytes, dataOff + i * 4) / 32768f;
                    float r = BitConverter.ToInt16(bytes, dataOff + i * 4 + 2) / 32768f;
                    pcm[i] = (l + r) * 0.5f;
                }
            }
            return pcm;
        }
        catch (Exception e) { Paths.Log($"sfx wav read: {e.Message}"); return null; }
    }

    private static void LoadCustomPcmLocked(string slot, string wavPath)
    {
        if (ReadWavPcm(wavPath) is { Length: > 0 } pcm) _rawCustom[slot] = pcm;
    }

    /// <summary>Re-renders all cached native stereo WAV buffers (call when UI volume or 8D Reverb setting changes).</summary>
    public static void RebuildBuffers()
    {
        EnsureInit();
        lock (_lock) RebuildAllLocked();
    }

    private static unsafe void FreeAllRenderedLocked()
    {
        PlaySoundW(null, 0, 0);
        foreach (var p in _rendered.Values) if (p != 0) NativeMemory.Free((void*)p);
        _rendered.Clear();
        for (int i = 0; i < _volTierBufs.Length; i++)
        {
            if (_volTierBufs[i] != 0) { NativeMemory.Free((void*)_volTierBufs[i]); _volTierBufs[i] = 0; }
        }
    }

    private static void RebuildAllLocked()
    {
        FreeAllRenderedLocked();
        double masterGain = Math.Clamp(AppSettings.Current.UiSoundsVolume, 10, 300) / 100.0;
        bool reverb8d = AppSettings.Current.UiSoundsReverb8D;

        for (int s = 0; s < Slots.Length; s++)
        {
            var key = Slots[s].Key;
            if (!_rawCustom.TryGetValue(key, out var src))
                _rawBuiltin.TryGetValue(key, out src);
            if (src == null || src.Length == 0) continue;

            double phase = s * 0.85;
            _rendered[key] = RenderStereoWav(src, masterGain, reverb8d, phase);

            if (key == "volume")
            {
                for (int t = 0; t < VolTiers; t++)
                {
                    // Tier 0 (0% video vol) = subtle 0.16x whisper; Tier 8 (100% video vol) = 1.02x; Tier 10 (150% video vol) = 1.38x
                    double tierMul = 0.16 + 1.22 * Math.Pow(t / (double)(VolTiers - 1), 1.15);
                    // Subtle pitch lift on higher volume tiers when using built-in volume tick
                    float[] tierSrc = src;
                    if (!_rawCustom.ContainsKey("volume"))
                    {
                        double pitch = 0.82 + 0.36 * (t / (double)(VolTiers - 1));
                        tierSrc = Synth(0.026, sec =>
                        {
                            double env = Math.Exp(-sec * 155.0) * Math.Min(1.0, sec / 0.001);
                            double f = 1480.0 * pitch;
                            return (Math.Sin(2 * Math.PI * f * sec) + 0.28 * Math.Sin(2 * Math.PI * f * 2.0 * sec)) * env * 0.095;
                        });
                    }
                    _volTierBufs[t] = RenderStereoWav(tierSrc, masterGain * tierMul, reverb8d, t * 0.62);
                }
            }
        }
    }

    /// <summary>
    /// Renders mono float PCM to a native 16-bit 44.1kHz stereo WAV buffer with master gain scaling,
    /// soft tanh limiting, and optional spatial 8D stereo hall reverb (Haas ITD rotation + 4 detuned stereo combs + 2 allpasses).
    /// </summary>
    private static unsafe nint RenderStereoWav(float[] src, double gain, bool reverb8d, double panPhase)
    {
        int tail = reverb8d ? (int)(Rate * 0.54) : 0;
        int frames = src.Length + tail;
        var outL = new float[frames];
        var outR = new float[frames];

        if (!reverb8d)
        {
            for (int i = 0; i < src.Length; i++)
            {
                float v = SoftClip(src[i] * gain);
                outL[i] = v;
                outR[i] = v;
            }
        }
        else
        {
            // 1. 8D spatial panning + Haas interaural micro-delay (~0.36ms = 16 samples)
            const int haas = 16;
            var dryL = new float[frames];
            var dryR = new float[frames];
            for (int i = 0; i < src.Length; i++)
            {
                double t = (double)i / Rate;
                double pan = Math.Sin(panPhase + t * 2.0 * Math.PI * 0.85); // -1..+1 sweeping around listener
                float gL = (float)Math.Cos((pan + 1.0) * Math.PI * 0.25);
                float gR = (float)Math.Sin((pan + 1.0) * Math.PI * 0.25);
                float s = (float)(src[i] * gain);
                int idxL = pan > 0 ? Math.Min(frames - 1, i + (int)(haas * pan)) : i;
                int idxR = pan < 0 ? Math.Min(frames - 1, i + (int)(haas * -pan)) : i;
                dryL[idxL] += s * gL;
                dryR[idxR] += s * gR;
            }

            // 2. Stereo Schroeder/Moorer hall reverb (detuned L/R comb lengths for ultra-wide 8D space)
            ReadOnlySpan<int> combsL = [1309, 1637, 1811, 1931];
            ReadOnlySpan<int> combsR = [1381, 1583, 1889, 1997];
            ReadOnlySpan<float> fbs = [0.74f, 0.72f, 0.75f, 0.71f];
            var wetL = new float[frames];
            var wetR = new float[frames];
            const float damp = 0.34f;

            for (int c = 0; c < 4; c++)
            {
                var bufL = new float[combsL[c]];
                var bufR = new float[combsR[c]];
                int pL = 0, pR = 0;
                float lpL = 0, lpR = 0, fb = fbs[c];
                for (int i = 0; i < frames; i++)
                {
                    float yL = bufL[pL];
                    lpL = yL * (1f - damp) + lpL * damp;
                    bufL[pL] = (dryL[i] + dryR[i] * 0.22f) + lpL * fb;
                    if (++pL >= bufL.Length) pL = 0;
                    wetL[i] += yL;

                    float yR = bufR[pR];
                    lpR = yR * (1f - damp) + lpR * damp;
                    bufR[pR] = (dryR[i] + dryL[i] * 0.22f) + lpR * fb;
                    if (++pR >= bufR.Length) pR = 0;
                    wetR[i] += yR;
                }
            }

            // 3. Two stereo allpass diffusers for silky reverberation tail
            ApplyAllpass(wetL, 241, 0.5f);
            ApplyAllpass(wetL, 557, 0.5f);
            ApplyAllpass(wetR, 263, 0.5f);
            ApplyAllpass(wetR, 587, 0.5f);

            // 4. Slow 8D rotation on the wet tail + fade-out end guard (no click)
            int fadeLen = Math.Min(1200, frames / 4);
            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / Rate;
                double rot = 0.5 + 0.5 * Math.Sin(panPhase * 1.3 + t * 2.0 * Math.PI * 0.9);
                float wL = wetL[i] * 0.22f * (float)(0.65 + 0.35 * rot);
                float wR = wetR[i] * 0.22f * (float)(0.65 + 0.35 * (1.0 - rot));
                float fade = i > frames - fadeLen ? (frames - i) / (float)fadeLen : 1f;
                outL[i] = SoftClip((dryL[i] * 0.80f + wL) * fade);
                outR[i] = SoftClip((dryR[i] * 0.80f + wR) * fade);
            }
        }

        int dataBytes = frames * 4; // 2 channels * 2 bytes (16-bit stereo)
        int total = 44 + dataBytes;
        var ptr = (byte*)NativeMemory.Alloc((nuint)total);
        var span = new Span<byte>(ptr, total);
        "RIFF"u8.CopyTo(span[0..4]);
        BitConverter.TryWriteBytes(span[4..8], total - 8);
        "WAVE"u8.CopyTo(span[8..12]);
        "fmt "u8.CopyTo(span[12..16]);
        BitConverter.TryWriteBytes(span[16..20], 16);
        BitConverter.TryWriteBytes(span[20..22], (short)1);   // PCM
        BitConverter.TryWriteBytes(span[22..24], (short)2);   // Stereo
        BitConverter.TryWriteBytes(span[24..28], Rate);
        BitConverter.TryWriteBytes(span[28..32], Rate * 4);
        BitConverter.TryWriteBytes(span[32..34], (short)4);
        BitConverter.TryWriteBytes(span[34..36], (short)16);
        "data"u8.CopyTo(span[36..40]);
        BitConverter.TryWriteBytes(span[40..44], dataBytes);

        for (int i = 0; i < frames; i++)
        {
            short l16 = (short)Math.Round(Math.Clamp(outL[i], -1f, 1f) * 32767f);
            short r16 = (short)Math.Round(Math.Clamp(outR[i], -1f, 1f) * 32767f);
            int off = 44 + i * 4;
            BitConverter.TryWriteBytes(span[off..(off + 2)], l16);
            BitConverter.TryWriteBytes(span[(off + 2)..(off + 4)], r16);
        }
        return (nint)ptr;
    }

    private static float SoftClip(double x) => (float)Math.Tanh(x);

    private static void ApplyAllpass(float[] data, int delay, float g)
    {
        var buf = new float[delay];
        int p = 0;
        for (int i = 0; i < data.Length; i++)
        {
            float x = data[i];
            float b = buf[p];
            float y = -x * g + b;
            buf[p] = x + b * g;
            if (++p >= delay) p = 0;
            data[i] = y;
        }
    }

    private static async Task<bool> ConvertToSlotWavAsync(string sourceFile, string destWav)
    {
        var tmp = destWav + ".tmp";
        try
        {
            var ffmpeg = Paths.Ffmpeg;
            if (ffmpeg != null)
            {
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourceFile);
                psi.ArgumentList.Add("-ac");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-ar");
                psi.ArgumentList.Add("44100");
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add("2.5");
                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add("volume=0.36");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("wav");
                psi.ArgumentList.Add(tmp);
                using var p = Process.Start(psi)!;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var errTask = p.StandardError.ReadToEndAsync(cts.Token);
                var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    return false;
                }
                var err = await errTask;
                _ = await outTask;
                if (p.ExitCode != 0 || !File.Exists(tmp))
                {
                    Paths.Log($"sfx ffmpeg failed: {err}");
                    return false;
                }
                File.Move(tmp, destWav, overwrite: true);
                return true;
            }
            else if (sourceFile.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(sourceFile);
                if (fi.Length > MaxWavBytes) return false;
                File.Copy(sourceFile, tmp, overwrite: true);
                File.Move(tmp, destWav, overwrite: true);
                return true;
            }
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static void SetCustom(string slot, string sourceFile)
    {
        if (!File.Exists(sourceFile)) return;
        EnsureInit();
        var label = Path.GetFileName(sourceFile);
        var dest = Path.Combine(SfxDir, slot + ".wav");
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await ConvertToSlotWavAsync(sourceFile, dest);
                if (!ok)
                {
                    App.Post(() => Shell.Toast("Не удалось загрузить звук (проверьте формат WAV/MP3/OGG)", Th.Warn));
                    return;
                }
                lock (_lock)
                {
                    LoadCustomPcmLocked(slot, dest);
                    RebuildAllLocked();
                }
                App.Post(() =>
                {
                    AppSettings.Current.CustomSfx[slot] = label;
                    AppSettings.Current.UiSounds = true;
                    AppSettings.Current.Save();
                    Preview(slot);
                    Shell.Toast("Звук заменён: " + label, Th.Good, 2400);
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log($"sfx custom {slot}: {e.Message}");
                App.Post(() => Shell.Toast("Ошибка загрузки звука: " + e.Message, Th.Warn));
            }
        });
    }

    /// <summary>Imports an entire .zip soundpack in one click, auto-matching filenames.</summary>
    public static void ImportZip(string zipPath)
    {
        if (!File.Exists(zipPath)) return;
        EnsureInit();
        Shell.Toast("Импортирую звуки из архива…", Th.Sub, 3000);
        _ = Task.Run(async () =>
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "fluxtube-sfx-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(tmpDir);
                var matched = new Dictionary<string, (string SrcPath, string Label)>();
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var ext = Path.GetExtension(entry.Name);
                        if (!AudioExts.Contains(ext)) continue;
                        var stem = Path.GetFileNameWithoutExtension(entry.Name).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                        if (!Aliases.TryGetValue(stem, out var slot)) continue;
                        var outFile = Path.Combine(tmpDir, slot + ext);
                        entry.ExtractToFile(outFile, overwrite: true);
                        matched[slot] = (outFile, entry.Name);
                    }
                }
                if (matched.Count == 0)
                {
                    App.Post(() => Shell.Toast("В архиве не найдено подходящих файлов (click, video_open, like, channel, volume, toggle_on, toggle_off, tab, back)", Th.Warn, 6000));
                    return;
                }
                var applied = new List<(string Slot, string Label)>();
                foreach (var (slot, (src, label)) in matched)
                {
                    var dest = Path.Combine(SfxDir, slot + ".wav");
                    if (await ConvertToSlotWavAsync(src, dest))
                    {
                        lock (_lock) LoadCustomPcmLocked(slot, dest);
                        applied.Add((slot, label));
                    }
                }
                if (applied.Count > 0)
                    lock (_lock) RebuildAllLocked();

                App.Post(() =>
                {
                    foreach (var (slot, label) in applied) AppSettings.Current.CustomSfx[slot] = label;
                    if (applied.Count > 0) AppSettings.Current.UiSounds = true;
                    AppSettings.Current.Save();
                    if (applied.Count > 0)
                    {
                        Preview(applied[0].Slot);
                        Shell.Toast($"Установлено звуков из архива: {applied.Count} из {Slots.Length}", Th.Good, 3500);
                    }
                    else Shell.Toast("Не удалось конвертировать файлы из архива", Th.Warn);
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("sfx zip: " + e.Message);
                App.Post(() => Shell.Toast("Ошибка распаковки архива: " + e.Message, Th.Warn));
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
            }
        });
    }

    public static void ResetSlot(string slot)
    {
        EnsureInit();
        lock (_lock)
        {
            _rawCustom.Remove(slot);
            RebuildAllLocked();
        }
        try { var p = Path.Combine(SfxDir, slot + ".wav"); if (File.Exists(p)) File.Delete(p); } catch { }
        AppSettings.Current.CustomSfx.Remove(slot);
        AppSettings.Current.Save();
        Preview(slot);
        App.Invalidate();
    }

    public static void ResetAll()
    {
        EnsureInit();
        lock (_lock)
        {
            _rawCustom.Clear();
            RebuildAllLocked();
        }
        foreach (var (key, _) in Slots)
            try { var p = Path.Combine(SfxDir, key + ".wav"); if (File.Exists(p)) File.Delete(p); } catch { }
        AppSettings.Current.CustomSfx.Clear();
        AppSettings.Current.Save();
        Shell.Toast("Все звуки сброшены на стандартные (SH / MGS)", Th.Good, 2200);
        App.Invalidate();
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            FreeAllRenderedLocked();
            _rawCustom.Clear();
            _rawBuiltin.Clear();
            _ready = false;
        }
    }

    public static void Preview(string slot)
    {
        if (App.Mute || App.Test) return;
        EnsureInit();
        PlaySlot(slot, force: true);
    }

    private static unsafe void PlaySlot(string slot, bool force = false)
    {
        if (App.Mute || App.Test) return;
        if (!force && !AppSettings.Current.UiSounds) return;
        EnsureInit();
        lock (_lock)
        {
            if (_rendered.TryGetValue(slot, out var ptr) && ptr != 0)
                PlaySoundW((byte*)ptr, 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
    }

    private static unsafe void PlayVolumeTier(double videoVolPct, bool force = false)
    {
        if (App.Mute || App.Test) return;
        if (!force && !AppSettings.Current.UiSounds) return;
        EnsureInit();
        int tier = Math.Clamp((int)Math.Round(videoVolPct / 12.0), 0, VolTiers - 1);
        lock (_lock)
        {
            var ptr = _volTierBufs[tier];
            if (ptr == 0) _rendered.TryGetValue("volume", out ptr);
            if (ptr != 0) PlaySoundW((byte*)ptr, 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
    }

    public static void Click()
    {
        var now = Environment.TickCount64;
        if (now - _lastClickAt < 35) return;
        _lastClickAt = now;
        PlaySlot("click");
    }

    public static void VideoOpen() => PlaySlot("video_open");
    public static void Like() => PlaySlot("like");
    public static void Channel() => PlaySlot("channel");
    public static void Toggle(bool on) => PlaySlot(on ? "toggle_on" : "toggle_off");

    /// <summary>Slider/volume sound dynamically scaled to the current video volume (0..150%).</summary>
    public static void Volume(double? videoVolumePct = null)
    {
        var now = Environment.TickCount64;
        if (now - _lastVolAt < 45) return;
        _lastVolAt = now;
        double v = videoVolumePct ?? (MpvPlayer.Instance.IsReady
            ? (MpvPlayer.Instance.Muted ? 0 : MpvPlayer.Instance.Volume)
            : AppSettings.Current.Volume);
        PlayVolumeTier(v);
    }

    public static void Tick(double? videoVolumePct = null)
    {
        var now = Environment.TickCount64;
        if (now - _lastTickAt < 48) return;
        _lastTickAt = now;
        double v = videoVolumePct ?? (MpvPlayer.Instance.IsReady
            ? (MpvPlayer.Instance.Muted ? 0 : MpvPlayer.Instance.Volume)
            : AppSettings.Current.Volume);
        PlayVolumeTier(v);
    }

    public static void Tab() => PlaySlot("tab");
    public static void Back() => PlaySlot("back");
}
