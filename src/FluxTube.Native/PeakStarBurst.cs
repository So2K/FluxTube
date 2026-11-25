using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluxTube.Services;

namespace FluxTube;

/// <summary>One star burst at the strongest 100 ms RMS window of a fully decoded track.</summary>
public static class PeakStarBurst
{
    private sealed record Peak(double Time, double Duration);
    private static readonly Dictionary<string, Task<Peak?>> Analyses = new();
    private static readonly HashSet<string> Fired = new();
    private static readonly SemaphoreSlim DecodeSlot = new(1);
    private static string? _current;
    private static double _previousPos;
    private static long _burstAt;

    public static void Draw(R stage, R art, string id, string? url, double pos, double duration, bool playing)
    {
        if (App.Test) return;
        if (_current != id) { _current = id; _previousPos = pos; _burstAt = 0; }
        double? peakTime = GetPeakTime(id, url, duration);
        if (playing && !Fired.Contains(id) && peakTime is { } peak)
        {
            bool continuous = pos >= _previousPos && pos - _previousPos < 0.75;
            if (continuous && _previousPos <= peak && pos >= peak && pos - peak < 0.25)
            {
                Fired.Add(id);
                _burstAt = UI.Now;
            }
        }
        _previousPos = pos;
        if (!playing) { _burstAt = 0; return; }
        float age = (UI.Now - _burstAt) / 2100f;
        if (_burstAt == 0 || age >= 1) return;
        Gfx.PushClip(stage);
        float cx = art.X + art.W / 2, cy = art.Y + art.H / 2;
        float ease = 1 - MathF.Pow(1 - age, 3);
        for (int i = 0; i < 40; i++)
        {
            float angle = i * 2.399963f;
            float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
            float travel = (70 + (i % 7) * 26) * ease;
            float x = cx + dx * (art.W * 0.3f + travel);
            float y = cy + dy * (art.H * 0.3f + travel) + age * age * 55;
            float edge = Math.Clamp(MathF.Min(MathF.Min(x - stage.X, stage.Right - x), MathF.Min(y - stage.Y, stage.Bottom - y)) / 32, 0, 1);
            float alpha = MathF.Pow(1 - age, 1.3f) * edge;
            float size = (4 + i % 4 * 1.5f) * (1 - age * 0.4f);
            uint col = i % 3 == 0 ? 0xFFFFDA7A : Gfx.Mix(Th.Brand, Th.Text, 0.65f);
            float px = 0, py = 0;
            for (int point = 0; point <= 10; point++)
            {
                float a = point * MathF.PI / 5 + age * (i % 2 == 0 ? 3 : -3);
                float radius = (point % 2 == 0 ? size : size * 0.42f);
                float sx = x + MathF.Sin(a) * radius, sy = y - MathF.Cos(a) * radius;
                if (point > 0)
                {
                    Gfx.Line(px, py, sx, sy, Gfx.Fade(col, alpha * 0.1f), 5);
                    Gfx.Line(px, py, sx, sy, Gfx.Fade(col, alpha), 1.5f);
                }
                px = sx; py = sy;
            }
        }
        Gfx.PopClip();
        UI.Animate();
    }

    public static double? GetPeakTime(string id, string? url, double duration)
    {
        if (App.Test) return null;
        if (!Analyses.TryGetValue(id, out var job) && !string.IsNullOrWhiteSpace(url) && duration > 1)
        {
            job = Analyze(id, url, duration);
            Analyses[id] = job;
        }
        return job is { IsCompletedSuccessfully: true } ? job.Result?.Time : null;
    }

    private static async Task<Peak?> Analyze(string id, string url, double duration)
    {
        // Decode the whole source, never a partial waveform or a guessed future peak.
        await DecodeSlot.WaitAsync().ConfigureAwait(false);
        Process? proc = null;
        try
        {
            string dir = Path.Combine(Paths.Data, "track-peaks-v1");
            Directory.CreateDirectory(dir);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
            string cache = Path.Combine(dir, key + ".txt");
            if (File.Exists(cache))
            {
                string[] values = (await File.ReadAllTextAsync(cache).ConfigureAwait(false)).Split('|');
                if (values.Length == 2 && double.TryParse(values[0], CultureInfo.InvariantCulture, out double t) &&
                    double.TryParse(values[1], CultureInfo.InvariantCulture, out double d) && Math.Abs(d - duration) < 0.5 && t >= 0 && t < d)
                    return new Peak(t, d);
            }
            string? ff = Paths.Ffmpeg;
            if (ff == null)
            {
                string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FluxTube", "tools", "ffmpeg.exe");
                if (File.Exists(installed)) ff = installed;
            }
            if (ff == null) return null;
            var start = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", url, "-vn", "-sn", "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1" }) start.ArgumentList.Add(arg);
            proc = Process.Start(start);
            if (proc == null) return null;
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var registration = timeout.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
            var errors = proc.StandardError.ReadToEndAsync(timeout.Token);
            byte[] buffer = new byte[32768];
            double[] windows = new double[10];
            int window = 0, blockSamples = 0, pending = -1, blocks = 0;
            long samples = 0;
            double blockEnergy = 0, rolling = 0, maximum = 0, peakTime = 0;
            while (true)
            {
                int read = await proc.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (pending < 0) { pending = buffer[i]; continue; }
                    short value = (short)(pending | buffer[i] << 8); pending = -1;
                    blockEnergy += (double)value * value;
                    samples++; blockSamples++;
                    if (blockSamples != 480) continue;
                    rolling += blockEnergy - windows[window]; windows[window] = blockEnergy;
                    window = (window + 1) % windows.Length;
                    blockEnergy = 0; blockSamples = 0; blocks++;
                    if (blocks >= 10 && rolling > maximum)
                    {
                        maximum = rolling;
                        peakTime = (samples - 2400) / 48000.0;
                    }
                }
            }
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (proc.ExitCode != 0 || maximum <= 0 || Math.Abs(samples / 48000.0 - duration) > 1) return null;
            var result = new Peak(peakTime, duration);
            await File.WriteAllTextAsync(cache, peakTime.ToString("R", CultureInfo.InvariantCulture) + "|" + duration.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            App.WakeUi();
            return result;
        }
        catch { return null; }
        finally { try { if (proc is { HasExited: false }) proc.Kill(true); } catch { } proc?.Dispose(); DecodeSlot.Release(); }
    }
}
