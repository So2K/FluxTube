using System.Numerics;

namespace FluxTube;

/// <summary>
/// Bidirectional top-down audio-reactive JDM Drift Car simulation for Music Stage (~0.15ms, zero-alloc):
/// - Audio + Perlin/Lissajous racing trajectory: Loudness integrates speed, StereoPan pulls L/R, SpectralCentroid pulls Up/Down!
/// - Dynamic left &amp; right drift transitions (slip angle follows real turn curvature -> crisp S-bend whip transitions!)
/// - Visible counter-steering front wheels, soft 3-layer headlight beams, fluffy 2-layer tire smoke, skid marks &amp; exhaust backfire!
/// </summary>
public static class DriftCarFx
{
    private struct SkidSeg { public float X0, Y0, X1, Y1, Life; public uint Col; }
    private struct SmokePuff { public float X, Y, Vx, Vy, R, Life, MaxLife; public uint Col; }
    private struct Spark { public float X, Y, Vx, Vy, Life; public uint Col; }

    private const int MaxSkids = 200, MaxSmoke = 110, MaxSparks = 48;
    private static readonly SkidSeg[] _skids = new SkidSeg[MaxSkids];
    private static readonly SmokePuff[] _smoke = new SmokePuff[MaxSmoke];
    private static readonly Spark[] _sparks = new Spark[MaxSparks];
    private static int _skidIdx, _smokeIdx, _sparkIdx;

    private static float _cx, _cy, _vx, _vy, _heading, _angVel, _steerWheel, _pathS, _backfire;
    private static float _smoothPan, _smoothVert, _driftBias;
    private static float _prevRLx, _prevRLy, _prevRRx, _prevRRy;
    private static bool _inited;
    private static long _lastBeat;
    private static uint _rng = 0xA341316Cu;

    private static float Rand01()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return (x & 0xFFFF) * (1f / 65535f);
    }

    private static float Hash1(int n)
    {
        uint x = (uint)(n * 374761393 + 668265263);
        x = (x ^ (x >> 13)) * 1274126177u;
        return ((x ^ (x >> 16)) & 0xFFFF) * (1f / 65535f) - 0.5f;
    }

    /// <summary>Smooth 1D Perlin-like cubic value noise for organic unpredictable drift corners.</summary>
    private static float Noise1D(float x)
    {
        int i = (int)MathF.Floor(x);
        float f = x - i;
        f = f * f * (3f - 2f * f);
        return Hash1(i) + (Hash1(i + 1) - Hash1(i)) * f;
    }

    private static (float x, float y) TrackPoint(float s, float midX, float midY, float rx, float ry, float pan, float vert)
    {
        // Figure-8 / Lissajous + Perlin meanders + live Stereo L/R & Treble/Bass audio pull -> drifts BOTH left & right!
        float nx = Noise1D(s * 0.55f + 1.7f) * 0.48f;
        float ny = Noise1D(s * 0.55f + 19.3f) * 0.48f;
        float ux = 0.72f * MathF.Sin(s * 1.00f) + 0.28f * MathF.Sin(s * 2.13f + 0.6f) + nx + pan * 0.36f;
        float uy = 0.68f * MathF.Sin(s * 1.618f + 0.4f) + 0.32f * MathF.Cos(s * 0.77f) + ny + vert * 0.38f;
        return (midX + Math.Clamp(ux, -1.05f, 1.05f) * rx, midY + Math.Clamp(uy, -1.05f, 1.05f) * ry);
    }

    public static void Draw(R stage, uint dom1, uint dom2)
    {
        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.032);
        float bass = AudioAnalyzer.BassEnergy;
        float mid = AudioAnalyzer.MidEnergy;
        float high = AudioAnalyzer.HighEnergy;
        float hit = AudioAnalyzer.SharpWaveHit;
        float kick = MusicBounce.Kick;
        float loud = Math.Clamp(AudioAnalyzer.Loudness, 0f, 1f);
        float calm = Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.85f, 0.12f, 1f);
        bool beat = AudioAnalyzer.BeatStamp != _lastBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.36f;
        if (beat) _lastBeat = AudioAnalyzer.BeatStamp;

        uint bodyCol = dom1 != 0 ? Gfx.Mix(dom1, 0xFFFF3B6B, 0.22f) : 0xFFFF2E63;
        uint accentCol = dom2 != 0 && dom2 != dom1 ? dom2 : 0xFF00E5FF;
        uint smokeCol1 = Gfx.Mix(accentCol, 0xFFE8F4FF, 0.62f);
        uint smokeCol2 = Gfx.Mix(bodyCol, 0xFFFFEEF4, 0.55f);

        float midX = stage.X + stage.W * 0.5f;
        float midY = stage.Y + stage.H * 0.48f;
        float rx = MathF.Max(140f, stage.W * 0.39f);
        float ry = MathF.Max(90f, stage.H * 0.31f);

        float sAlpha = 1f - MathF.Exp(-dt * 4.0f);
        _smoothPan += (Math.Clamp(AudioAnalyzer.StereoPan, -1f, 1f) - _smoothPan) * sAlpha;
        float targetVert = Math.Clamp(-(AudioAnalyzer.SpectralCentroid - 0.36f) * 1.35f + (bass - 0.35f) * 0.55f, -1f, 1f);
        _smoothVert += (targetVert - _smoothVert) * sAlpha;

        if (!_inited || _cx < stage.X - 100 || _cx > stage.Right + 100 || _cy < stage.Y - 100 || _cy > stage.Bottom + 100)
        {
            _pathS = 0f;
            (_cx, _cy) = TrackPoint(0f, midX, midY, rx, ry, 0f, 0f);
            _vx = 180f; _vy = 0f;
            _heading = 0f; _angVel = 0f; _driftBias = 0f;
            _prevRLx = _prevRRx = _cx; _prevRLy = _prevRRy = _cy;
            Array.Clear(_skids); Array.Clear(_smoke); Array.Clear(_sparks);
            _inited = true;
        }

        // 1. C1 loudness-integrated progression along the bidirectional S-bend audio track!
        float speedMul = (0.55f + loud * 0.85f + bass * 0.55f + AudioAnalyzer.DropImpact * 0.55f) * calm;
        _pathS += dt * 0.72f * speedMul;
        var (tx, ty) = TrackPoint(_pathS + 0.32f, midX, midY, rx, ry, _smoothPan, _smoothVert);
        var (tx2, ty2) = TrackPoint(_pathS + 0.62f, midX, midY, rx, ry, _smoothPan, _smoothVert);

        float targetAng = MathF.Atan2(ty - _cy, tx - _cx);
        float aheadAng = MathF.Atan2(ty2 - ty, tx2 - tx);

        // Compute REAL signed turn curvature (negative = left turn, positive = right turn!)
        float turnCurv = aheadAng - targetAng;
        while (turnCurv > MathF.PI) turnCurv -= MathF.Tau;
        while (turnCurv < -MathF.PI) turnCurv += MathF.Tau;

        // Tail swings opposite to turn direction -> true bidirectional left & right whip drifts!
        float targetDrift = Math.Clamp(turnCurv * (0.95f + bass * 0.85f + kick * 0.55f), -0.78f, 0.78f);
        _driftBias += (targetDrift - _driftBias) * (1f - MathF.Exp(-dt * 6.5f));

        float desiredHeading = targetAng + _driftBias;
        float hDiff = desiredHeading - _heading;
        while (hDiff > MathF.PI) hDiff -= MathF.Tau;
        while (hDiff < -MathF.PI) hDiff += MathF.Tau;

        _angVel += (hDiff * 13.5f - _angVel * 5.6f) * dt;
        _heading += _angVel * dt;

        // Front wheels visibly counter-steer into the slide!
        float velAng = MathF.Atan2(_vy, _vx);
        float slipAng = velAng - _heading;
        while (slipAng > MathF.PI) slipAng -= MathF.Tau;
        while (slipAng < -MathF.PI) slipAng += MathF.Tau;
        _steerWheel = Math.Clamp(slipAng * 0.85f, -0.52f, 0.52f);

        float fwdX = MathF.Cos(_heading), fwdY = MathF.Sin(_heading);
        float rightX = -fwdY, rightY = fwdX;
        float aimX = MathF.Cos(targetAng), aimY = MathF.Sin(targetAng);

        float targetSpeed = (165f + loud * 165f + bass * 155f + kick * 95f + hit * 75f) * calm;
        float desiredVx = (aimX * 0.78f + fwdX * 0.22f) * targetSpeed;
        float desiredVy = (aimY * 0.78f + fwdY * 0.22f) * targetSpeed;
        float grip = 2.9f;
        _vx += (desiredVx - _vx) * (1f - MathF.Exp(-dt * grip));
        _vy += (desiredVy - _vy) * (1f - MathF.Exp(-dt * grip));
        _cx = Math.Clamp(_cx + _vx * dt, stage.X + 42f, stage.Right - 42f);
        _cy = Math.Clamp(_cy + _vy * dt, stage.Y + 42f, stage.Bottom - 42f);

        float latSpeed = MathF.Abs(_vx * rightX + _vy * rightY);
        float driftAmt = Math.Clamp((latSpeed - 18f) / 135f, 0f, 1.2f);

        const float carLen = 44f, carWid = 22f;
        float rlX = _cx - fwdX * (carLen * 0.36f) - rightX * (carWid * 0.44f);
        float rlY = _cy - fwdY * (carLen * 0.36f) - rightY * (carWid * 0.44f);
        float rrX = _cx - fwdX * (carLen * 0.36f) + rightX * (carWid * 0.44f);
        float rrY = _cy - fwdY * (carLen * 0.36f) + rightY * (carWid * 0.44f);
        float exX = _cx - fwdX * (carLen * 0.52f) + rightX * (carWid * 0.22f);
        float exY = _cy - fwdY * (carLen * 0.52f) + rightY * (carWid * 0.22f);

        // 2. Emit Skid Marks + Fluffy Smoke when drifting in either direction!
        if (driftAmt > 0.14f)
        {
            float distRL = MathF.Sqrt((rlX - _prevRLx) * (rlX - _prevRLx) + (rlY - _prevRLy) * (rlY - _prevRLy));
            if (distRL > 3f && distRL < 55f)
            {
                uint skCol = Gfx.Mix(0xFF0A0A12, accentCol, 0.28f + bass * 0.35f);
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRLx, Y0 = _prevRLy, X1 = rlX, Y1 = rlY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRRx, Y0 = _prevRRy, X1 = rrX, Y1 = rrY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
            }

            if (Rand01() < 0.58f + driftAmt * 0.40f)
            {
                bool leftWheel = Rand01() < 0.5f;
                float ml = 0.55f + Rand01() * 0.50f;
                _smoke[_smokeIdx] = new SmokePuff
                {
                    X = leftWheel ? rlX : rrX, Y = leftWheel ? rlY : rrY,
                    Vx = -_vx * 0.10f + (Rand01() - 0.5f) * 26f,
                    Vy = -_vy * 0.10f + (Rand01() - 0.5f) * 26f,
                    R = 4.5f + driftAmt * 6.5f + bass * 4.5f,
                    Life = ml, MaxLife = ml,
                    Col = leftWheel ? smokeCol1 : smokeCol2,
                };
                _smokeIdx = (_smokeIdx + 1) % MaxSmoke;
            }
        }
        _prevRLx = rlX; _prevRLy = rlY;
        _prevRRx = rrX; _prevRRy = rrY;

        if (beat || hit > 0.45f)
        {
            _backfire = MathF.Max(_backfire, 0.85f + bass * 0.45f);
            int cnt = beat ? 6 : 2;
            for (int k = 0; k < cnt; k++)
            {
                float spd = 85f + Rand01() * 135f;
                float spAng = _heading + MathF.PI + (Rand01() - 0.5f) * 0.72f;
                _sparks[_sparkIdx] = new Spark
                {
                    X = exX, Y = exY,
                    Vx = MathF.Cos(spAng) * spd, Vy = MathF.Sin(spAng) * spd,
                    Life = 0.28f + Rand01() * 0.30f,
                    Col = (k & 1) == 0 ? 0xFFFFD166 : accentCol,
                };
                _sparkIdx = (_sparkIdx + 1) % MaxSparks;
            }
        }
        _backfire *= MathF.Exp(-dt * 12f);

        // 3. Draw Tire Skid Marks
        for (int i = 0; i < MaxSkids; i++)
        {
            if (_skids[i].Life <= 0.01f) continue;
            _skids[i].Life -= dt * 0.40f;
            if (_skids[i].Life <= 0.01f) continue;
            Gfx.Line(_skids[i].X0, _skids[i].Y0, _skids[i].X1, _skids[i].Y1, Gfx.Fade(_skids[i].Col, _skids[i].Life * 0.42f * calm), 3.0f);
        }

        // 4. Draw Soft 2-Layer Fluffy Drift Smoke
        for (int i = 0; i < MaxSmoke; i++)
        {
            if (_smoke[i].Life <= 0.01f) continue;
            _smoke[i].Life -= dt;
            if (_smoke[i].Life <= 0.01f) continue;
            _smoke[i].X += _smoke[i].Vx * dt;
            _smoke[i].Y += _smoke[i].Vy * dt;
            _smoke[i].R += dt * (20f + bass * 14f);
            float frac = _smoke[i].Life / _smoke[i].MaxLife;
            float a = frac * frac * 0.16f * calm;
            Gfx.Circle(_smoke[i].X, _smoke[i].Y, _smoke[i].R * 1.45f, Gfx.Fade(_smoke[i].Col, a * 0.45f));
            Gfx.Circle(_smoke[i].X, _smoke[i].Y, _smoke[i].R * 0.85f, Gfx.Fade(_smoke[i].Col, a));
        }

        // 5. Draw Exhaust Sparks
        for (int i = 0; i < MaxSparks; i++)
        {
            if (_sparks[i].Life <= 0.01f) continue;
            _sparks[i].Life -= dt;
            if (_sparks[i].Life <= 0.01f) continue;
            float nx = _sparks[i].X + _sparks[i].Vx * dt;
            float ny = _sparks[i].Y + _sparks[i].Vy * dt;
            Gfx.Line(_sparks[i].X, _sparks[i].Y, nx, ny, Gfx.Fade(_sparks[i].Col, Math.Clamp(_sparks[i].Life * 2.5f, 0f, 0.95f) * calm), 1.8f);
            _sparks[i].X = nx; _sparks[i].Y = ny;
            _sparks[i].Vx *= MathF.Exp(-dt * 4f); _sparks[i].Vy *= MathF.Exp(-dt * 4f);
        }

        // 6. Draw Styled Top-Down Coupe with Counter-Steering Front Wheels & Soft Layered Headlight Cones!
        var savedTf = Gfx.DC.Transform;
        var carTf = Matrix3x2.CreateRotation(_heading) * Matrix3x2.CreateTranslation(_cx, _cy) * savedTf;
        Gfx.DC.Transform = carTf;

        // Soft 3-step feathered headlight beams (no hard rectangle edges!)
        float beamLen = 74f + high * 32f;
        for (int side = -1; side <= 1; side += 2)
        {
            float ly = side * carWid * 0.30f;
            float sx = carLen * 0.45f;
            Gfx.Circle(sx + beamLen * 0.55f, ly + side * 9f, 22f, Gfx.Fade(0xFFFFF6DD, 0.055f * calm));
            Gfx.Circle(sx + beamLen * 0.30f, ly + side * 4f, 13f, Gfx.Fade(0xFFFFF6DD, 0.095f * calm));
            Gfx.Circle(sx + beamLen * 0.12f, ly + side * 1.5f, 6.5f, Gfx.Fade(0xFFFFFFFF, 0.16f * calm));
        }

        // Visible counter-steering front wheels sticking slightly out of front fenders during hard drifts!
        for (int side = -1; side <= 1; side += 2)
        {
            Gfx.DC.Transform = Matrix3x2.CreateRotation(_steerWheel) * Matrix3x2.CreateTranslation(carLen * 0.28f, side * carWid * 0.50f) * carTf;
            Gfx.Fill(new R(-4.5f, -2.0f, 9.0f, 4.0f), 0xFF0A0C12, 1.2f);
        }
        Gfx.DC.Transform = carTf;

        // Soft underglow neon aura pulsing to bass
        Gfx.Fill(new R(-carLen * 0.55f, -carWid * 0.60f, carLen * 1.10f, carWid * 1.20f), Gfx.Fade(accentCol, (0.24f + bass * 0.32f) * calm), 10f);

        // Exhaust backfire nitro flame
        if (_backfire > 0.08f)
        {
            float fLen = 10f + _backfire * 20f;
            Gfx.Line(-carLen * 0.50f, carWid * 0.22f, -carLen * 0.50f - fLen, carWid * 0.22f, Gfx.Fade(0xFF00E5FF, _backfire * 0.65f), 5.0f);
            Gfx.Line(-carLen * 0.50f, carWid * 0.22f, -carLen * 0.50f - fLen * 0.65f, carWid * 0.22f, Gfx.Fade(0xFFFFFFFF, _backfire * 0.90f), 2.2f);
        }

        Gfx.Fill(new R(-carLen * 0.50f + 2f, -carWid * 0.50f + 3f, carLen, carWid), Gfx.Fade(0xFF000000, 0.48f), 6f);
        var bodyR = new R(-carLen * 0.50f, -carWid * 0.50f, carLen, carWid);
        Gfx.Fill(bodyR, Gfx.Fade(bodyCol, 0.95f), 6.5f);
        Gfx.Stroke(bodyR, Gfx.Fade(0xFFFFFFFF, 0.45f), 6.5f, 1.1f);
        Gfx.Fill(new R(-carLen * 0.24f, -carWid * 0.38f, carLen * 0.46f, carWid * 0.76f), 0xFF10131C, 4.5f);
        Gfx.Fill(new R(-carLen * 0.14f, -carWid * 0.26f, carLen * 0.22f, carWid * 0.52f), Gfx.Fade(bodyCol, 0.85f), 3f);
        Gfx.Fill(new R(-carLen * 0.50f, -carWid * 0.54f, 4.5f, carWid * 1.08f), 0xFF0C0E14, 1.5f);

        Gfx.Circle(carLen * 0.46f, -carWid * 0.30f, 2.2f, 0xFFFFFFFF);
        Gfx.Circle(carLen * 0.46f,  carWid * 0.30f, 2.2f, 0xFFFFFFFF);
        float brakeGlow = 0.55f + driftAmt * 0.40f;
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f, 4.2f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f, 4.2f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f, 1.9f, 0xFFFF2A4B);
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f, 1.9f, 0xFFFF2A4B);

        Gfx.DC.Transform = savedTf;
        UI.Animate();
    }
}
