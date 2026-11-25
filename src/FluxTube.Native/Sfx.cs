using System.Diagnostics;
using System.Runtime.InteropServices;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Synthesized PS1/PS2-era UI sound effects inspired by Silent Hill 1/2 and Metal Gear Solid 1/2,
/// with per-slot custom sound replacement (WAV, MP3, OGG, FLAC, M4A normalized via bundled ffmpeg).
/// Pure in-memory 16-bit 44.1 kHz PCM WAV buffers played asynchronously via winmm.dll at ~4x lower
/// level than video audio so every click, toggle, and slider feels tactile without ever intruding.
/// </summary>
public static unsafe partial class Sfx
{
    private const int Rate = 44100;
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    public static readonly (string Key, string Label)[] Slots =
    [
        ("click", "Клик (кнопки, карточки)"),
        ("toggle_on", "Переключатель (вкл)"),
        ("toggle_off", "Переключатель (выкл)"),
        ("tick", "Ползунок / громкость колёсиком"),
        ("tab", "Вкладка / включение звуков"),
        ("back", "Назад / закрытие"),
    ];

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
    private static partial bool PlaySoundW(byte* pszSound, nint hmod, uint fdwSound);

    private static readonly Dictionary<string, nint> _builtin = new();
    private static readonly Dictionary<string, nint> _custom = new();
    private static readonly object _lock = new();
    private static bool _ready;
    private static long _lastTickAt, _lastClickAt;

    private static void EnsureInit()
    {
        if (_ready) return;
        lock (_lock)
        {
            if (_ready) return;
            _ready = true;

            // Master gain ~0.09 (approx 4x quieter than normal video playback)
            _builtin["click"] = (nint)BuildWav(0.068, (t, d) =>
            {
                double env = Math.Exp(-t * 58.0) * Math.Min(1.0, t / 0.0022);
                double f1 = 1046.5 * (1.0 + 0.04 * Math.Exp(-t * 140.0));
                double f2 = 1567.98;
                double f3 = 2637.02;
                double fm = Math.Sin(2 * Math.PI * f1 * t + 0.35 * Math.Sin(2 * Math.PI * f2 * t) * Math.Exp(-t * 90.0));
                double bell = 0.35 * Math.Sin(2 * Math.PI * f3 * t) * Math.Exp(-t * 85.0);
                return (fm * 0.72 + bell) * env * 0.095;
            });

            _builtin["toggle_on"] = (nint)BuildWav(0.052, (t, d) =>
            {
                double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
                double freq = 460.0 + 240.0 * (1.0 - Math.Exp(-t * 95.0));
                double body = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2.02 * t);
                double snap = Math.Sin(2 * Math.PI * 2200.0 * t) * Math.Exp(-t * 320.0) * 0.35;
                return (body * 0.75 + snap) * env * 0.09;
            });

            _builtin["toggle_off"] = (nint)BuildWav(0.052, (t, d) =>
            {
                double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
                double freq = 540.0 - 210.0 * (1.0 - Math.Exp(-t * 85.0));
                double body = Math.Sin(2 * Math.PI * freq * t) + 0.22 * Math.Sin(2 * Math.PI * freq * 1.98 * t);
                double snap = Math.Sin(2 * Math.PI * 1800.0 * t) * Math.Exp(-t * 340.0) * 0.30;
                return (body * 0.75 + snap) * env * 0.085;
            });

            _builtin["tick"] = (nint)BuildWav(0.022, (t, d) =>
            {
                double env = Math.Exp(-t * 175.0) * Math.Min(1.0, t / 0.001);
                double s = Math.Sin(2 * Math.PI * 1680.0 * t) + 0.3 * Math.Sin(2 * Math.PI * 3360.0 * t);
                return s * env * 0.065;
            });

            _builtin["tab"] = (nint)BuildWav(0.085, (t, d) =>
            {
                double env1 = Math.Exp(-t * 48.0) * Math.Min(1.0, t / 0.002);
                double t2 = Math.Max(0.0, t - 0.022);
                double env2 = t > 0.022 ? Math.Exp(-t2 * 52.0) * Math.Min(1.0, t2 / 0.002) : 0.0;
                double s1 = Math.Sin(2 * Math.PI * 698.46 * t) * env1 * 0.55;
                double s2 = Math.Sin(2 * Math.PI * 1046.5 * t2) * env2 * 0.75;
                return (s1 + s2) * 0.085;
            });

            _builtin["back"] = (nint)BuildWav(0.072, (t, d) =>
            {
                double env = Math.Exp(-t * 50.0) * Math.Min(1.0, t / 0.002);
                double freq = 470.0 - 150.0 * Math.Min(1.0, t / 0.045);
                double s = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 1.5 * t) * Math.Exp(-t * 80.0);
                return s * env * 0.085;
            });

            // Load any user-customized WAVs from %USERPROFILE%\.fluxtube\sfx\
            foreach (var (key, _) in Slots)
            {
                var path = Path.Combine(SfxDir, key + ".wav");
                if (File.Exists(path)) LoadCustomWavLocked(key, path);
            }
        }
    }

    private static void LoadCustomWavLocked(string slot, string wavPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(wavPath);
            if (bytes.Length < 44) return;
            var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.AsSpan().CopyTo(new Span<byte>(ptr, bytes.Length));
            if (_custom.TryGetValue(slot, out var old) && old != 0)
            {
                PlaySoundW(null, 0, 0); // stop any active playback before freeing old buffer
                NativeMemory.Free((void*)old);
            }
            _custom[slot] = (nint)ptr;
        }
        catch (Exception e) { Paths.Log($"sfx load {slot}: {e.Message}"); }
    }

    /// <summary>
    /// Converts any user audio file (WAV, MP3, OGG, FLAC, M4A, OPUS, AAC) into a normalized
    /// 44.1 kHz mono PCM WAV at ~4x lower volume than video and activates it immediately for the given slot.
    /// </summary>
    public static void SetCustom(string slot, string sourceFile)
    {
        if (!File.Exists(sourceFile)) return;
        EnsureInit();
        var label = Path.GetFileName(sourceFile);
        var dest = Path.Combine(SfxDir, slot + ".wav");
        _ = Task.Run(() =>
        {
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
                    };
                    psi.ArgumentList.Add("-y");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(sourceFile);
                    psi.ArgumentList.Add("-ac");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-ar");
                    psi.ArgumentList.Add("44100");
                    psi.ArgumentList.Add("-t");
                    psi.ArgumentList.Add("2.5");
                    psi.ArgumentList.Add("-af");
                    psi.ArgumentList.Add("volume=0.24");
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("wav");
                    psi.ArgumentList.Add(dest);
                    using var p = Process.Start(psi)!;
                    p.WaitForExit(10_000);
                    if (p.ExitCode != 0 || !File.Exists(dest))
                    {
                        var err = p.StandardError.ReadToEnd();
                        Paths.Log($"sfx ffmpeg {slot}: {err}");
                        App.Post(() => Shell.Toast("Не удалось конвертировать звук", Th.Warn));
                        return;
                    }
                }
                else if (sourceFile.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceFile, dest, overwrite: true);
                }
                else
                {
                    App.Post(() => Shell.Toast("Для MP3/OGG нужен ffmpeg (он идёт в комплекте с установщиком)", Th.Warn));
                    return;
                }

                lock (_lock) LoadCustomWavLocked(slot, dest);
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

    public static void ResetSlot(string slot)
    {
        EnsureInit();
        lock (_lock)
        {
            if (_custom.Remove(slot, out var old) && old != 0)
            {
                PlaySoundW(null, 0, 0);
                NativeMemory.Free((void*)old);
            }
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
            PlaySoundW(null, 0, 0);
            foreach (var old in _custom.Values) if (old != 0) NativeMemory.Free((void*)old);
            _custom.Clear();
        }
        foreach (var (key, _) in Slots)
            try { var p = Path.Combine(SfxDir, key + ".wav"); if (File.Exists(p)) File.Delete(p); } catch { }
        AppSettings.Current.CustomSfx.Clear();
        AppSettings.Current.Save();
        Shell.Toast("Все звуки сброшены на стандартные (SH / MGS)", Th.Good, 2200);
        App.Invalidate();
    }

    public static void Preview(string slot)
    {
        if (App.Mute || App.Test) return;
        EnsureInit();
        PlaySlot(slot, force: true);
    }

    private static byte* BuildWav(double seconds, Func<double, double, double> sample)
    {
        int samples = (int)(Rate * seconds);
        int dataBytes = samples * 2;
        int total = 44 + dataBytes;
        var ptr = (byte*)NativeMemory.Alloc((nuint)total);
        var span = new Span<byte>(ptr, total);

        "RIFF"u8.CopyTo(span[0..4]);
        BitConverter.TryWriteBytes(span[4..8], total - 8);
        "WAVE"u8.CopyTo(span[8..12]);
        "fmt "u8.CopyTo(span[12..16]);
        BitConverter.TryWriteBytes(span[16..20], 16);
        BitConverter.TryWriteBytes(span[20..22], (short)1);
        BitConverter.TryWriteBytes(span[22..24], (short)1);
        BitConverter.TryWriteBytes(span[24..28], Rate);
        BitConverter.TryWriteBytes(span[28..32], Rate * 2);
        BitConverter.TryWriteBytes(span[32..34], (short)2);
        BitConverter.TryWriteBytes(span[34..36], (short)16);
        "data"u8.CopyTo(span[36..40]);
        BitConverter.TryWriteBytes(span[40..44], dataBytes);

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / Rate;
            double v = Math.Clamp(sample(t, seconds), -1.0, 1.0);
            short s16 = (short)Math.Round(v * 32767.0);
            BitConverter.TryWriteBytes(span[(44 + i * 2)..(46 + i * 2)], s16);
        }
        return ptr;
    }

    private static void PlaySlot(string slot, bool force = false)
    {
        if (App.Mute || App.Test) return;
        if (!force && !AppSettings.Current.UiSounds) return;
        EnsureInit();
        nint ptr = 0;
        lock (_lock)
        {
            if (!_custom.TryGetValue(slot, out ptr) || ptr == 0)
                _builtin.TryGetValue(slot, out ptr);
        }
        if (ptr != 0) PlaySoundW((byte*)ptr, 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    public static void Click()
    {
        var now = Environment.TickCount64;
        if (now - _lastClickAt < 35) return;
        _lastClickAt = now;
        PlaySlot("click");
    }

    public static void Toggle(bool on) => PlaySlot(on ? "toggle_on" : "toggle_off");

    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (now - _lastTickAt < 48) return;
        _lastTickAt = now;
        PlaySlot("tick");
    }

    public static void Tab() => PlaySlot("tab");
    public static void Back() => PlaySlot("back");
}
