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
    private static CancellationTokenSource? _activeCts;
    private static string? _current;
    private static long _currentSetAt;
    private static double _previousPos;
    private static long _burstAt;

    /// <summary>0..1 intensity curve during the track's strongest decoded RMS peak moment.</summary>
    public static float ActiveIntensity
    {
        get
        {
            if (_burstAt == 0) return 0f;
            float age = (UI.Now - _burstAt) / 2100f;
            if (age < 0f || age >= 1f) return 0f;
            return MathF.Pow(1f - age, 1.4f);
        }
    }

    public static void Draw(R stage, R art, string id, string? url, double pos, double duration, bool playing)
    {
        if (App.Test) return;
        if (_current != id)
        {
            _current = id;
            _currentSetAt = UI.Now;
            _previousPos = pos;
            _burstAt = 0;
            try { _activeCts?.Cancel(); } catch { }
        }
        // Delay heavy ffmpeg peak analysis by 1.4s after switching videos so video switching has zero CPU lag!
        double? peakTime = UI.Now - _currentSetAt > 1400 ? GetPeakTime(id, url, duration) : null;
        if (playing && peakTime is { } peak)
        {
            // Allow re-triggering if the user intentionally seeks back before the drop
            if (pos < peak - 3.0) Fired.Remove(id);
            bool continuous = pos >= _previousPos && pos - _previousPos < 0.75;
            if (!Fired.Contains(id) && continuous && _previousPos <= peak && pos >= peak && pos - peak < 0.35)
            {
                Fired.Add(id);
                _burstAt = UI.Now;
            }
        }
        _previousPos = pos;
        if (!playing) { _burstAt = 0; return; }
        float age = (UI.Now - _burstAt) / 2600f;
        if (_burstAt == 0 || age >= 1) return;
        Gfx.PushClip(stage);
        float cx = art.X + art.W / 2, cy = art.Y + art.H / 2;
        uint gold = 0xFFFFE066;
        uint cyan = Th.Brand2 != 0 ? Th.Brand2 : 0xFF6CE8FF;
        uint brandHi = Gfx.Mix(Th.Brand, 0xFFFFFFFF, 0.55f);

        // 1. Initial MEGA-HYPE flash bloom behind the cover (0..0.22)
        if (age < 0.22f)
        {
            float fa = (1f - age / 0.22f);
            fa *= fa;
            Gfx.RadialGlow(cx, cy, art.W * (1.25f + age * 2.5f), art.H * (1.35f + age * 2.5f),
                1f, 0.90f, 0.55f, fa * 0.36f, slot: 11);
        }

        // 2. Twin expanding shockwave rings (gold primary + brand secondary)
        for (int ring = 0; ring < 2; ring++)
        {
            float rAge = (age - ring * 0.06f) / 0.42f;
            if (rAge <= 0f || rAge >= 1f) continue;
            float rEase = 1f - MathF.Pow(1f - rAge, 3f);
            float ra = (1f - rAge) * (1f - rAge);
            float maxR = MathF.Max(stage.W, stage.H) * 0.58f;
            float rx = art.W * 0.45f + rEase * maxR;
            float ry = art.H * 0.45f + rEase * maxR * 0.72f;
            uint rCol = ring == 0 ? gold : brandHi;
            const int segs = 48;
            float rpx = cx + rx, rpy = cy;
            for (int s = 1; s <= segs; s++)
            {
                float a = s * MathF.Tau / segs;
                float x = cx + MathF.Cos(a) * rx, y = cy + MathF.Sin(a) * ry;
                Gfx.Line(rpx, rpy, x, y, Gfx.Fade(rCol, ra * 0.18f), 7f);
                Gfx.Line(rpx, rpy, x, y, Gfx.Fade(0xFFFFFFFF, ra * 0.75f), 1.8f);
                rpx = x; rpy = y;
            }
        }

        // 3. High-speed radial burst rays on drop impact (0..0.32)
        if (age < 0.32f)
        {
            float rayT = age / 0.32f;
            float rayA = (1f - rayT) * (1f - rayT);
            float rEase = 1f - MathF.Pow(1f - rayT, 2.5f);
            const int rayCount = 24;
            for (int i = 0; i < rayCount; i++)
            {
                float angle = i * (MathF.Tau / rayCount) + (i % 3) * 0.06f;
                float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
                float d0 = art.W * 0.32f + rEase * (75f + (i % 4) * 35f);
                float d1 = d0 + (48f + (i % 5) * 26f) * (1f - rayT * 0.5f);
                uint col = (i % 3) switch { 0 => gold, 1 => cyan, _ => brandHi };
                Gfx.Line(cx + dx * d0, cy + dy * d0, cx + dx * d1, cy + dy * d1, Gfx.Fade(col, rayA * 0.72f), 2.2f);
            }
        }

        // 4. 56 MEGA-HYPE stars: comet trails + twinkling 4-point supernovas + spinning 5-point stars!
        float ease = 1f - MathF.Pow(1f - age, 2.8f);
        float prevEase = 1f - MathF.Pow(1f - MathF.Max(0f, age - 0.045f), 2.8f);
        for (int i = 0; i < 56; i++)
        {
            float angle = i * 2.399963f + (i % 5) * 0.11f;
            float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
            float speed = 95f + (i % 9) * 36f;
            float grav = 68f + (i % 4) * 18f;
            float travel = speed * ease;
            float prevTravel = speed * prevEase;
            float x = cx + dx * (art.W * 0.28f + travel);
            float y = cy + dy * (art.H * 0.28f + travel) + age * age * grav;
            float tx = cx + dx * (art.W * 0.28f + prevTravel);
            float ty = cy + dy * (art.H * 0.28f + prevTravel) + MathF.Max(0f, age - 0.045f) * MathF.Max(0f, age - 0.045f) * grav;

            float edge = Math.Clamp(MathF.Min(MathF.Min(x - stage.X, stage.Right - x), MathF.Min(y - stage.Y, stage.Bottom - y)) / 28f, 0f, 1f);
            if (edge <= 0.01f) continue;

            float twinkle = 0.72f + 0.28f * MathF.Sin(age * 28f + i * 1.9f);
            float alpha = MathF.Pow(1f - age, 1.25f) * edge * twinkle;
            float size = (5.5f + (i % 5) * 2.1f) * (1f - age * 0.35f);
            uint col = (i % 4) switch { 0 => gold, 1 => 0xFFFFFFFF, 2 => cyan, _ => brandHi };

            // Glowing comet trail behind fast stars
            if (age < 0.75f)
                Gfx.Line(tx, ty, x, y, Gfx.Fade(col, alpha * 0.35f), MathF.Max(1.4f, size * 0.28f));

            if ((i & 1) == 0)
            {
                // Crisp 4-point supernova sparkle (✦) with neon halo + white-hot core
                float rot = age * (i % 4 == 0 ? 2.4f : -2.4f) + i * 0.4f;
                float c = MathF.Cos(rot), s = MathF.Sin(rot);
                float r1 = size * 1.35f, r2 = size * 0.95f;
                Gfx.Circle(x, y, size * 0.65f, Gfx.Fade(col, alpha * 0.22f));
                Gfx.Line(x - c * r1, y - s * r1, x + c * r1, y + s * r1, Gfx.Fade(col, alpha * 0.95f), 2.1f);
                Gfx.Line(x + s * r2, y - c * r2, x - s * r2, y + c * r2, Gfx.Fade(col, alpha * 0.95f), 2.1f);
                Gfx.Circle(x, y, MathF.Max(1.4f, size * 0.26f), Gfx.Fade(0xFFFFFFFF, alpha));
            }
            else
            {
                // Spinning 5-point hype star (★) with bright rim
                float px = 0, py = 0;
                for (int point = 0; point <= 10; point++)
                {
                    float a = point * MathF.PI / 5f + age * (i % 3 == 0 ? 4.2f : -4.2f);
                    float radius = point % 2 == 0 ? size : size * 0.42f;
                    float sx = x + MathF.Sin(a) * radius, sy = y - MathF.Cos(a) * radius;
                    if (point > 0)
                    {
                        Gfx.Line(px, py, sx, sy, Gfx.Fade(col, alpha * 0.16f), 4.8f);
                        Gfx.Line(px, py, sx, sy, Gfx.Fade(col, alpha), 1.6f);
                    }
                    px = sx; py = sy;
                }
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
        var myCts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _activeCts, myCts);
        try { prev?.Cancel(); } catch { }
        await DecodeSlot.WaitAsync().ConfigureAwait(false);
        Process? proc = null;
        try
        {
            if (myCts.IsCancellationRequested || _current != id) return null;
            string dir = Path.Combine(Paths.Data, "track-peaks-v2");
            Directory.CreateDirectory(dir);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
            string cache = Path.Combine(dir, key + ".txt");
            if (File.Exists(cache))
            {
                string[] values = (await File.ReadAllTextAsync(cache).ConfigureAwait(false)).Split('|');
                if (values.Length == 2 && double.TryParse(values[0], CultureInfo.InvariantCulture, out double t) &&
                    double.TryParse(values[1], CultureInfo.InvariantCulture, out double d) && Math.Abs(d - duration) < 1.5 && t >= 0 && t < d)
                    return new Peak(t, d);
            }
            string? ff = Paths.Ffmpeg;
            if (ff == null)
            {
                string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FluxTube", "tools", "ffmpeg.exe");
                if (File.Exists(installed)) ff = installed;
            }
            if (ff == null) return null;
            // Decode at 8 kHz mono (6x faster & lighter than 48 kHz!) and cap at 20 min for long mixes
            const int sampleRate = 8000;
            const int blockSize = 800; // 100 ms per block
            var start = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", url, "-t", "1200", "-vn", "-sn", "-ac", "1", "-ar", "8000", "-f", "s16le", "pipe:1" }) start.ArgumentList.Add(arg);
            proc = Process.Start(start);
            if (proc == null) return null;
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(myCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var registration = timeout.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
            var errors = proc.StandardError.ReadToEndAsync(timeout.Token);

            // Collect 100ms blocks of combined full-band + low-pass bass energy (180 Hz 1-pole LPF)
            byte[] buffer = new byte[16384];
            var blockEnergies = new List<double>(2048);
            int blockSamples = 0, pending = -1;
            double fullSum = 0, bassSum = 0, lp = 0;
            const double lpAlpha = 0.132; // ~180 Hz cutoff at 8000 Hz sample rate

            while (true)
            {
                int read = await proc.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (pending < 0) { pending = buffer[i]; continue; }
                    double s = (short)(pending | (buffer[i] << 8)) / 32768.0;
                    pending = -1;
                    lp += (s - lp) * lpAlpha;
                    fullSum += s * s;
                    bassSum += lp * lp;
                    blockSamples++;
                    if (blockSamples == blockSize)
                    {
                        double fullRms = Math.Sqrt(fullSum / blockSize);
                        double bassRms = Math.Sqrt(bassSum / blockSize);
                        // Weighted loudness favoring real bass+full-band punch over thin treble spikes
                        blockEnergies.Add(fullRms * 0.55 + bassRms * 0.75);
                        fullSum = 0; bassSum = 0; blockSamples = 0;
                    }
                }
            }
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (proc.ExitCode != 0 || blockEnergies.Count < 30) return null;

            int n = blockEnergies.Count;
            double[] prefix = new double[n + 1];
            for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + blockEnergies[i];
            double Avg(int from, int to)
            {
                from = Math.Clamp(from, 0, n);
                to = Math.Clamp(to, from + 1, n);
                return (prefix[to] - prefix[from]) / (to - from);
            }

            // Skip intro (first ~12% or up to 8s) and outro (last 5%) so intro pops / fade-outs never win
            int startIdx = n > 180 ? Math.Min(80, (int)(n * 0.12)) : Math.Min(15, n / 6);
            int endIdx = Math.Max(startIdx + 1, n - Math.Max(25, (int)(n * 0.05)));

            double bestScore = 0;
            int bestIdx = startIdx;
            for (int i = startIdx; i < endIdx; i++)
            {
                // Sustained loud drop energy (2.5s after i) vs buildup contrast (3.0s before i) + immediate 0.4s downbeat hit
                double after = Avg(i, i + 25);
                double before = Avg(i - 30, i);
                double hit = Avg(i, i + 4);
                double contrast = Math.Clamp((after - before) / (after + 0.02), 0.0, 1.0);
                double attack = Math.Clamp(hit / (after + 0.02), 0.5, 1.4);
                double score = after * (0.42 + 0.58 * contrast) * (0.68 + 0.32 * attack);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            if (bestScore <= 0.01) return null;
            double peakTime = bestIdx * (blockSize / (double)sampleRate);
            var result = new Peak(peakTime, duration);
            await File.WriteAllTextAsync(cache, peakTime.ToString("R", CultureInfo.InvariantCulture) + "|" + duration.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            App.WakeUi();
            return result;
        }
        catch { return null; }
        finally { try { if (proc is { HasExited: false }) proc.Kill(true); } catch { } proc?.Dispose(); DecodeSlot.Release(); }
    }
}
