using System.Runtime.InteropServices;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Synthesized PS1/PS2-era UI sound effects inspired by Silent Hill 1/2 and Metal Gear Solid 1/2.
/// Pure in-memory 16-bit 44.1 kHz PCM WAV buffers played asynchronously via winmm.dll at ~4x lower
/// level than video audio so every click, toggle, and slider feels tactile without ever intruding.
/// </summary>
public static unsafe partial class Sfx
{
    private const int Rate = 44100;
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    [LibraryImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(byte* pszSound, nint hmod, uint fdwSound);

    private static byte* _click, _toggleOn, _toggleOff, _tick, _tab, _back;
    private static bool _ready;
    private static long _lastTickAt, _lastClickAt;

    private static void EnsureInit()
    {
        if (_ready) return;
        _ready = true;
        // Master gain ~0.09 (approx 4x quieter than normal video playback)
        _click = BuildWav(0.068, (t, d) =>
        {
            // MGS / Silent Hill glassy metallic confirm chime
            double env = Math.Exp(-t * 58.0) * Math.Min(1.0, t / 0.0022);
            double f1 = 1046.5 * (1.0 + 0.04 * Math.Exp(-t * 140.0));
            double f2 = 1567.98;
            double f3 = 2637.02;
            double fm = Math.Sin(2 * Math.PI * f1 * t + 0.35 * Math.Sin(2 * Math.PI * f2 * t) * Math.Exp(-t * 90.0));
            double bell = 0.35 * Math.Sin(2 * Math.PI * f3 * t) * Math.Exp(-t * 85.0);
            return (fm * 0.72 + bell) * env * 0.095;
        });

        _toggleOn = BuildWav(0.052, (t, d) =>
        {
            // Silent Hill 2 inventory tactile click (rising)
            double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
            double freq = 460.0 + 240.0 * (1.0 - Math.Exp(-t * 95.0));
            double body = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2.02 * t);
            double snap = Math.Sin(2 * Math.PI * 2200.0 * t) * Math.Exp(-t * 320.0) * 0.35;
            return (body * 0.75 + snap) * env * 0.09;
        });

        _toggleOff = BuildWav(0.052, (t, d) =>
        {
            // Silent Hill 2 inventory tactile click (falling)
            double env = Math.Exp(-t * 68.0) * Math.Min(1.0, t / 0.0015);
            double freq = 540.0 - 210.0 * (1.0 - Math.Exp(-t * 85.0));
            double body = Math.Sin(2 * Math.PI * freq * t) + 0.22 * Math.Sin(2 * Math.PI * freq * 1.98 * t);
            double snap = Math.Sin(2 * Math.PI * 1800.0 * t) * Math.Exp(-t * 340.0) * 0.30;
            return (body * 0.75 + snap) * env * 0.085;
        });

        _tick = BuildWav(0.022, (t, d) =>
        {
            // MGS codec / radar precision slider tick
            double env = Math.Exp(-t * 175.0) * Math.Min(1.0, t / 0.001);
            double s = Math.Sin(2 * Math.PI * 1680.0 * t) + 0.3 * Math.Sin(2 * Math.PI * 3360.0 * t);
            return s * env * 0.065;
        });

        _tab = BuildWav(0.085, (t, d) =>
        {
            // MGS soft dual-tone harmonic open
            double env1 = Math.Exp(-t * 48.0) * Math.Min(1.0, t / 0.002);
            double t2 = Math.Max(0.0, t - 0.022);
            double env2 = t > 0.022 ? Math.Exp(-t2 * 52.0) * Math.Min(1.0, t2 / 0.002) : 0.0;
            double s1 = Math.Sin(2 * Math.PI * 698.46 * t) * env1 * 0.55;
            double s2 = Math.Sin(2 * Math.PI * 1046.5 * t2) * env2 * 0.75;
            return (s1 + s2) * 0.085;
        });

        _back = BuildWav(0.072, (t, d) =>
        {
            // Silent Hill muted metallic cancel/close
            double env = Math.Exp(-t * 50.0) * Math.Min(1.0, t / 0.002);
            double freq = 470.0 - 150.0 * Math.Min(1.0, t / 0.045);
            double s = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 1.5 * t) * Math.Exp(-t * 80.0);
            return s * env * 0.085;
        });
    }

    private static byte* BuildWav(double seconds, Func<double, double, double> sample)
    {
        int samples = (int)(Rate * seconds);
        int dataBytes = samples * 2;
        int total = 44 + dataBytes;
        var ptr = (byte*)NativeMemory.Alloc((nuint)total);
        var span = new Span<byte>(ptr, total);

        // Standard RIFF/WAVE 16-bit mono PCM header
        "RIFF"u8.CopyTo(span[0..4]);
        BitConverter.TryWriteBytes(span[4..8], total - 8);
        "WAVE"u8.CopyTo(span[8..12]);
        "fmt "u8.CopyTo(span[12..16]);
        BitConverter.TryWriteBytes(span[16..20], 16);
        BitConverter.TryWriteBytes(span[20..22], (short)1);     // PCM
        BitConverter.TryWriteBytes(span[22..24], (short)1);     // mono
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

    private static void Play(byte* buf)
    {
        if (App.Mute || App.Test || !AppSettings.Current.UiSounds) return;
        EnsureInit();
        if (buf != null) PlaySoundW(buf, 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    public static void Click()
    {
        var now = Environment.TickCount64;
        if (now - _lastClickAt < 35) return;
        _lastClickAt = now;
        EnsureInit();
        Play(_click);
    }

    public static void Toggle(bool on)
    {
        EnsureInit();
        Play(on ? _toggleOn : _toggleOff);
    }

    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (now - _lastTickAt < 48) return;
        _lastTickAt = now;
        EnsureInit();
        Play(_tick);
    }

    public static void Tab()
    {
        EnsureInit();
        Play(_tab);
    }

    public static void Back()
    {
        EnsureInit();
        Play(_back);
    }
}
