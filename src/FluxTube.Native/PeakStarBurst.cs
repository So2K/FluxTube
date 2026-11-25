namespace FluxTube;

/// <summary>
/// One star burst at the strongest 100 ms RMS window of the track.
/// Reuses SoundCloudBar's unified single-pass background decode (ZERO second ffmpeg process, ZERO stream contention!).
/// </summary>
public static class PeakStarBurst
{
    private static readonly HashSet<string> Fired = new(StringComparer.Ordinal);
    private static string? _current;
    private static double _previousPos;
    private static long _burstAt;

    public static void Draw(R stage, R art, string id, string? url, double pos, double duration, bool playing)
    {
        if (App.Test) return;
        if (_current != id) { _current = id; _previousPos = pos; _burstAt = 0; }
        if (!playing) { _burstAt = 0; return; }
        double? peakTime = GetPeakTime(id, url, duration);
        if (!Fired.Contains(id) && peakTime is { } peak)
        {
            bool continuous = pos >= _previousPos && pos - _previousPos < 0.75;
            if (continuous && _previousPos <= peak && pos >= peak && pos - peak < 0.25)
            {
                if (Fired.Count > 256) Fired.Clear();
                Fired.Add(id);
                _burstAt = UI.Now;
            }
        }
        _previousPos = pos;
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
        if (App.Test || duration <= 1) return null;
        var t = SoundCloudBar.TryGetPeakTime(id, url);
        return t is { } pt && pt >= 0 && pt < duration ? pt : null;
    }
}
