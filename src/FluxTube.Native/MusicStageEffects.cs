using FluxTube.Services;

namespace FluxTube;

/// <summary>Bounded, audio-driven drop effects. All animation remains inside the music stage.</summary>
public static class MusicStageEffects
{
    private static float _average, _intensity, _flash;
    private static long _lastFrame = -1, _updatedAt, _lastHit, _lastBurstAt;
    private static readonly long[] _bursts = new long[1];
    private static readonly float[] _strength = new float[1];
    public static float Intensity => UI.Now - _updatedAt < 150 ? _intensity : 0;

    public static void Update(bool enabled)
    {
        if (_lastFrame == Gfx.Frame) return;
        _lastFrame = Gfx.Frame;
        if (UI.Now - _updatedAt > 200) { _average = _intensity = _flash = 0; Array.Clear(_bursts); }
        _updatedAt = UI.Now;
        if (!enabled)
        {
            _average = _intensity = _flash = 0;
            Array.Clear(_bursts);
            return;
        }
        float dt = (float)Math.Clamp(UI.Dt, 0.003, 0.05);
        float energy = AudioAnalyzer.BassEnergy * 0.65f + AudioAnalyzer.MidEnergy * 0.25f + AudioAnalyzer.HighEnergy * 0.1f;
        _average += (energy - _average) * (1 - MathF.Exp(-dt * 1.5f));
        float target = Math.Clamp((energy - 0.18f) * 2.2f + MathF.Max(0, energy - _average) * 2, 0, 1);
        _intensity += (target - _intensity) * (1 - MathF.Exp(-dt * (target > _intensity ? 12 : 2.5f)));
        _flash *= MathF.Exp(-dt * 7);
        if (AudioAnalyzer.BeatStamp != _lastHit && UI.Now - AudioAnalyzer.BeatStamp >= 25)
        {
            _lastHit = AudioAnalyzer.BeatStamp;
            // Judge the original attack, not its already-decayed render envelope.
            // Consume every event once so cooldown never replays an old hit late.
            if (UI.Now - _lastHit < 80 && AudioAnalyzer.BeatAttackStrength > 0.7f && UI.Now - _lastBurstAt > 1800)
            {
                _lastBurstAt = UI.Now;
                _bursts[0] = UI.Now;
                _strength[0] = AudioAnalyzer.BeatAttackStrength;
                _flash = MathF.Max(_flash, AudioAnalyzer.BeatMotionStrength);
            }
        }
        if (_intensity > 0.01f || _flash > 0.01f) UI.Animate();
    }

    public static void Draw(R stage, R art)
    {
        if (!AppSettings.Current.MusicImpactEffects || Intensity < 0.005f) return;
        Gfx.PushClip(stage);
        float cx = art.X + art.W / 2, cy = art.Y + art.H / 2;
        uint ice = Gfx.Mix(Th.Brand, 0xFF73D8EB, 0.55f);
        float red = ((ice >> 16) & 255) / 255f, green = ((ice >> 8) & 255) / 255f, blue = (ice & 255) / 255f;
        Gfx.RadialGlow(cx, cy, art.W * (0.9f + _intensity), art.H * (1.4f + _intensity),
            red, green, blue, Math.Clamp(AudioAnalyzer.BassPressure * 0.055f + _intensity * 0.025f + _flash * 0.10f + AudioAnalyzer.BeatMotionStrength * 0.09f, 0, 0.20f), slot: 8);
        for (int b = 0; b < _bursts.Length; b++)
        {
            if (_bursts[b] == 0) continue;
            float age = (UI.Now - _bursts[b]) / 800f;
            if (age < 0 || age >= 1) continue;
            float ease = 1 - MathF.Pow(1 - age, 2);
            float alpha = (1 - age) * (1 - age) * _strength[b];
            float maxRx = MathF.Max(1, MathF.Min(cx - stage.X, stage.Right - cx) - 14);
            float maxRy = MathF.Max(1, MathF.Min(cy - stage.Y, stage.Bottom - cy) - 14);
            float startRx = MathF.Min(art.W * 0.67f, maxRx * 0.72f);
            float startRy = MathF.Min(art.H * 0.8f, maxRy * 0.72f);
            float rx = startRx + ease * (maxRx - startRx);
            float ry = startRy + ease * (maxRy - startRy);
            float px = cx + rx, py = cy;
            for (int i = 1; i <= 72; i++)
            {
                float angle = i * MathF.Tau / 72;
                float x = cx + MathF.Cos(angle) * rx, y = cy + MathF.Sin(angle) * ry;
                Gfx.Line(px, py, x, y, Gfx.Fade(ice, alpha * 0.09f), 7);
                Gfx.Line(px, py, x, y, Gfx.Fade(ice, alpha * 0.48f), 1.5f);
                px = x; py = y;
            }
            UI.Animate();
        }
        Gfx.PopClip();
    }
}
