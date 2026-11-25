using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Synthesized PS1/PS2-era UI sound effects inspired by Silent Hill 1/2 and Metal Gear Solid 1/2,
/// with per-slot custom sound replacement (WAV, MP3, OGG, FLAC, M4A) and 1-click ZIP soundpack import.
/// Thread-safe, zero UI-thread freezes, ~4x quieter than video playback.
/// </summary>
public static partial class Sfx
{
    private const int Rate = 44100;
    private const int MaxWavBytes = 4 * 1024 * 1024;
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

    private static readonly Dictionary<string, nint> _builtin = new();
    private static readonly Dictionary<string, nint> _custom = new();
    private static readonly object _lock = new();
    private static volatile bool _ready;
    private static long _lastTickAt, _lastClickAt, _lastVolAt;

    private static unsafe void EnsureInit()
    {
        if (_ready) return;
        lock (_lock)
        {
            if (_ready) return;

            // Master gain ~0.085 (approx 4x quieter than normal video playback)
            _builtin["click"] = (nint)BuildWav(0.062, (t, d) =>
            {
                double env = Math.Exp(-t * 62.0) * Math.Min(1.0, t / 0.002);
                double f1 = 1046.5 * (1.0 + 0.03 * Math.Exp(-t * 140.0));
                double fm = Math.Sin(2 * Math.PI * f1 * t + 0.30 * Math.Sin(2 * Math.PI * 1568.0 * t) * Math.Exp(-t * 95.0));
                return fm * env * 0.085;
            });

            _builtin["video_open"] = (nint)BuildWav(0.145, (t, d) =>
            {
                // Distinct deep MGS/SH cinematic crystalline confirm chord (completely different from button click)
                double env1 = Math.Exp(-t * 22.0) * Math.Min(1.0, t / 0.004);
                double t2 = Math.Max(0.0, t - 0.028);
                double env2 = t > 0.028 ? Math.Exp(-t2 * 24.0) * Math.Min(1.0, t2 / 0.004) : 0.0;
                double low = Math.Sin(2 * Math.PI * 392.0 * t) * env1 * 0.45;
                double mid = Math.Sin(2 * Math.PI * 587.33 * t + 0.25 * Math.Sin(2 * Math.PI * 1174.66 * t) * Math.Exp(-t * 40.0)) * env1 * 0.55;
                double shimmer = Math.Sin(2 * Math.PI * 880.0 * t2) * env2 * 0.65;
                return (low + mid + shimmer) * 0.075;
            });

            _builtin["like"] = (nint)BuildWav(0.115, (t, d) =>
            {
                // Warm harmonic rising fifth chime (SH save-point / MGS item pickup vibe)
                double env1 = Math.Exp(-t * 38.0) * Math.Min(1.0, t / 0.002);
                double t2 = Math.Max(0.0, t - 0.032);
                double env2 = t > 0.032 ? Math.Exp(-t2 * 30.0) * Math.Min(1.0, t2 / 0.003) : 0.0;
                double n1 = Math.Sin(2 * Math.PI * 880.0 * t) * env1 * 0.55;
                double n2 = (Math.Sin(2 * Math.PI * 1318.5 * t2) + 0.28 * Math.Sin(2 * Math.PI * 2637.0 * t2) * Math.Exp(-t2 * 55.0)) * env2 * 0.72;
                return (n1 + n2) * 0.085;
            });

            _builtin["channel"] = (nint)BuildWav(0.110, (t, d) =>
            {
                // Soft metallic sweep for entering a channel page
                double env = Math.Exp(-t * 32.0) * Math.Min(1.0, t / 0.003);
                double freq = 523.25 + 261.6 * (1.0 - Math.Exp(-t * 45.0));
                double s = Math.Sin(2 * Math.PI * freq * t) + 0.3 * Math.Sin(2 * Math.PI * freq * 1.5 * t) * Math.Exp(-t * 50.0);
                return s * env * 0.08;
            });

            _builtin["volume"] = (nint)BuildWav(0.024, (t, d) =>
            {
                // Crisp MGS codec/radar precision tick for volume & sliders
                double env = Math.Exp(-t * 165.0) * Math.Min(1.0, t / 0.001);
                double s = Math.Sin(2 * Math.PI * 1640.0 * t) + 0.28 * Math.Sin(2 * Math.PI * 3280.0 * t);
                return s * env * 0.065;
            });

            _builtin["toggle_on"] = (nint)BuildWav(0.052, (t, d) =>
            {
                double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
                double freq = 460.0 + 240.0 * (1.0 - Math.Exp(-t * 95.0));
                double body = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2.02 * t);
                double snap = Math.Sin(2 * Math.PI * 2200.0 * t) * Math.Exp(-t * 320.0) * 0.35;
                return (body * 0.75 + snap) * env * 0.085;
            });

            _builtin["toggle_off"] = (nint)BuildWav(0.052, (t, d) =>
            {
                double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
                double freq = 540.0 - 210.0 * (1.0 - Math.Exp(-t * 85.0));
                double body = Math.Sin(2 * Math.PI * freq * t) + 0.22 * Math.Sin(2 * Math.PI * freq * 1.98 * t);
                double snap = Math.Sin(2 * Math.PI * 1800.0 * t) * Math.Exp(-t * 340.0) * 0.30;
                return (body * 0.75 + snap) * env * 0.08;
            });

            _builtin["tab"] = (nint)BuildWav(0.085, (t, d) =>
            {
                double env1 = Math.Exp(-t * 48.0) * Math.Min(1.0, t / 0.002);
                double t2 = Math.Max(0.0, t - 0.022);
                double env2 = t > 0.022 ? Math.Exp(-t2 * 52.0) * Math.Min(1.0, t2 / 0.002) : 0.0;
                double s1 = Math.Sin(2 * Math.PI * 698.46 * t) * env1 * 0.55;
                double s2 = Math.Sin(2 * Math.PI * 1046.5 * t2) * env2 * 0.75;
                return (s1 + s2) * 0.08;
            });

            _builtin["back"] = (nint)BuildWav(0.072, (t, d) =>
            {
                double env = Math.Exp(-t * 50.0) * Math.Min(1.0, t / 0.002);
                double freq = 470.0 - 150.0 * Math.Min(1.0, t / 0.045);
                double s = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 1.5 * t) * Math.Exp(-t * 80.0);
                return s * env * 0.08;
            });

            // Migrate legacy tick.wav -> volume.wav if user customized it earlier
            var oldTick = Path.Combine(SfxDir, "tick.wav");
            var volWav = Path.Combine(SfxDir, "volume.wav");
            if (File.Exists(oldTick) && !File.Exists(volWav))
            {
                try { File.Move(oldTick, volWav); } catch { }
            }

            foreach (var (key, _) in Slots)
            {
                var path = Path.Combine(SfxDir, key + ".wav");
                if (File.Exists(path)) LoadCustomWavLocked(key, path);
            }
            _ready = true;
        }
    }

    private static unsafe void LoadCustomWavLocked(string slot, string wavPath)
    {
        try
        {
            var fi = new FileInfo(wavPath);
            if (!fi.Exists || fi.Length < 44 || fi.Length > MaxWavBytes) return;
            var bytes = File.ReadAllBytes(wavPath);
            var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.AsSpan().CopyTo(new Span<byte>(ptr, bytes.Length));
            if (_custom.TryGetValue(slot, out var old) && old != 0)
            {
                PlaySoundW(null, 0, 0);
                NativeMemory.Free((void*)old);
            }
            _custom[slot] = (nint)ptr;
        }
        catch (Exception e) { Paths.Log($"sfx load {slot}: {e.Message}"); }
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
                psi.ArgumentList.Add("volume=0.24");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("wav");
                psi.ArgumentList.Add(tmp);
                using var p = Process.Start(psi)!;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                // Always drain stdout/stderr concurrently BEFORE WaitForExitAsync to prevent pipe deadlocks!
                var errTask = p.StandardError.ReadToEndAsync(cts.Token);
                var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
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

    /// <summary>Imports an entire .zip soundpack in one click, auto-matching filenames (click.mp3, video_open.wav, like.ogg, ...).</summary>
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
                    App.Post(() => Shell.Toast("В архиве не найдено подходящих файлов (назовите их click, video_open, like, channel, volume, toggle_on, toggle_off, tab, back)", Th.Warn, 6000));
                    return;
                }
                var applied = new List<(string Slot, string Label)>();
                foreach (var (slot, (src, label)) in matched)
                {
                    var dest = Path.Combine(SfxDir, slot + ".wav");
                    if (await ConvertToSlotWavAsync(src, dest))
                    {
                        lock (_lock) LoadCustomWavLocked(slot, dest);
                        applied.Add((slot, label));
                    }
                }
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

    public static unsafe void ResetSlot(string slot)
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

    public static unsafe void ResetAll()
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

    public static unsafe void Shutdown()
    {
        lock (_lock)
        {
            PlaySoundW(null, 0, 0);
            foreach (var p in _custom.Values) if (p != 0) NativeMemory.Free((void*)p);
            foreach (var p in _builtin.Values) if (p != 0) NativeMemory.Free((void*)p);
            _custom.Clear();
            _builtin.Clear();
            _ready = false;
        }
    }

    public static void Preview(string slot)
    {
        if (App.Mute || App.Test) return;
        EnsureInit();
        PlaySlot(slot, force: true);
    }

    private static unsafe byte* BuildWav(double seconds, Func<double, double, double> sample)
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

    private static unsafe void PlaySlot(string slot, bool force = false)
    {
        if (App.Mute || App.Test) return;
        if (!force && !AppSettings.Current.UiSounds) return;
        EnsureInit();
        lock (_lock)
        {
            if (!_custom.TryGetValue(slot, out var ptr) || ptr == 0)
                _builtin.TryGetValue(slot, out ptr);
            // Executed INSIDE lock so a concurrent SetCustom/Reset cannot free ptr before PlaySoundW starts!
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

    public static void Volume()
    {
        var now = Environment.TickCount64;
        if (now - _lastVolAt < 45) return;
        _lastVolAt = now;
        PlaySlot("volume");
    }

    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (now - _lastTickAt < 48) return;
        _lastTickAt = now;
        PlaySlot("volume");
    }

    public static void Tab() => PlaySlot("tab");
    public static void Back() => PlaySlot("back");
}
