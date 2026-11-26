using System.Numerics;

namespace FluxTube;

/// <summary>
/// Fourier Epicycle Beat-Locked JDM Drift Car ("Дрифт", ~0.16ms, zero-alloc):
/// - Analytical C-infinity 3-harmonic Fourier trajectory driven by smoothed live FFT bands:
///   * 1st harmonic (Bass/Loudness): wide Gymkhana oval
///   * 2nd harmonic (Mids): Figure-8 transition bends (crisp left &lt;-&gt; right whip transitions!)
///   * 3rd harmonic (Treble): sharp hairpin loops on energetic drops
/// - Exact analytical signed curvature kappa = (x'y'' - y'x'') / (x'^2 + y'^2)^(3/2) drives smooth slip angle &amp; counter-steer!
/// - Beat-locked "clutch kick" impulses amplify slip angle, tire smoke, and blue nitro backfire right on the kick!
/// - Draws a subtle glowing future racing-line ribbon ahead of the car showing the live Fourier curve!
/// </summary>
public static class DriftCarFx
{
    private struct SkidSeg { public float X0, Y0, X1, Y1, Life; public uint Col; }
    private struct SmokePuff { public float X, Y, Vx, Vy, R, Life, MaxLife; public uint Col; }
    private struct Spark { public float X, Y, Vx, Vy, Life; public uint Col; }

    private const int MaxSkids = 220, MaxSmoke = 120, MaxSparks = 56;
    private static readonly SkidSeg[] _skids = new SkidSeg[MaxSkids];
    private static readonly SmokePuff[] _smoke = new SmokePuff[MaxSmoke];
    private static readonly Spark[] _sparks = new Spark[MaxSparks];
    private static int _skidIdx, _smokeIdx, _sparkIdx;

    private static float _theta, _cx, _cy, _heading, _slipAngle, _steerWheel, _clutchKick, _backfire;
    private static float _a1 = 0.85f, _a2 = 0.25f, _a3 = 0.0f, _phi2, _tilt;
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

    /// <summary>
    /// Exact analytical 3-harmonic Fourier Epicycle position (x, y), velocity (dx, dy), and signed curvature kappa.
    /// Zero numerical jitter — guarantees silky left &amp; right drift transitions through inflection points!
    /// </summary>
    private static void EvalFourier(float th, float midX, float midY, float rx, float ry,
        out float x, out float y, out float dx, out float dy, out float kappa)
    {
        float c1 = MathF.Cos(th), s1 = MathF.Sin(th);
        float t2 = 2f * th + _phi2;
        float c2 = MathF.Cos(t2), s2 = MathF.Sin(t2);
        float t3 = 3f * th;
        float c3 = MathF.Cos(t3), s3 = MathF.Sin(t3);

        // X uses cos(th) + figure-8 harmonic + hairpin harmonic; Y uses sin(th) + figure-8 sin(2th) for classic Gymkhana S-bends!
        float lx = _a1 * c1 + _a2 * 0.45f * c2 + _a3 * c3;
        float ly = _a1 * 0.72f * s1 + _a2 * 0.92f * s2 + _a3 * 0.75f * s3;

        float ldx = -_a1 * s1 - _a2 * 0.90f * s2 - _a3 * 3f * s3;
        float ldy =  _a1 * 0.72f * c1 + _a2 * 1.84f * c2 + _a3 * 2.25f * c3;

        float lddx = -_a1 * c1 - _a2 * 1.80f * c2 - _a3 * 9f * c3;
        float lddy = -_a1 * 0.72f * s1 - _a2 * 3.68f * s2 - _a3 * 6.75f * s3;

        // Apply gentle StereoPan rotation to the whole Fourier arena
        float ct = MathF.Cos(_tilt), st = MathF.Sin(_tilt);
        float sx = lx * rx, sy = ly * ry;
        float sdx = ldx * rx, sdy = ldy * ry;
        float sddx = lddx * rx, sddy = lddy * ry;

        x = midX + sx * ct - sy * st;
        y = midY + sx * st + sy * ct;
        dx = sdx * ct - sdy * st;
        dy = sdx * st + sdy * ct;
        float ddx = sddx * ct - sddy * st;
        float ddy = sddx * st + sddy * ct;

        float spd2 = dx * dx + dy * dy + 1e-3f;
        float spd3 = spd2 * MathF.Sqrt(spd2);
        kappa = (dx * ddy - dy * ddx) / spd3; // exact signed curvature (rad/px)
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
        float calm = Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.85f, 0.14f, 1f);
        bool beat = AudioAnalyzer.BeatStamp != _lastBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.34f;
        if (beat) _lastBeat = AudioAnalyzer.BeatStamp;

        uint bodyCol = dom1 != 0 ? Gfx.Mix(dom1, 0xFFFF3B6B, 0.22f) : 0xFFFF2E63;
        uint accentCol = dom2 != 0 && dom2 != dom1 ? dom2 : 0xFF00E5FF;
        uint smokeCol1 = Gfx.Mix(accentCol, 0xFFE8F4FF, 0.62f);
        uint smokeCol2 = Gfx.Mix(bodyCol, 0xFFFFEEF4, 0.55f);

        float midX = stage.X + stage.W * 0.5f;
        float midY = stage.Y + stage.H * 0.48f;
        float rx = MathF.Max(140f, stage.W * 0.38f);
        float ry = MathF.Max(85f, stage.H * 0.30f);

        // 1. Smoothly morph Fourier Epicycle harmonics from live FFT spectrum (slow ~1.2s envelope so curve never jerks!)
        float envA = 1f - MathF.Exp(-dt * 1.35f);
        float targetA1 = Math.Clamp(0.76f + bass * 0.16f, 0.65f, 0.92f);
        // Mids & Drops open up the 2nd harmonic -> smooth Figure-8 left/right Gymkhana transitions!
        float targetA2 = Math.Clamp((0.28f + mid * 0.36f + AudioAnalyzer.DropImpact * 0.20f) * calm, 0.10f, 0.52f);
        // Highs & energy add subtle 3rd-harmonic hairpin corners
        float targetA3 = Math.Clamp((high * 0.16f + hit * 0.08f) * calm, 0.0f, 0.16f);
        _a1 += (targetA1 - _a1) * envA;
        _a2 += (targetA2 - _a2) * envA;
        _a3 += (targetA3 - _a3) * envA;
        _phi2 += dt * (0.14f + mid * 0.18f) * calm; // slowly precesses figure-8 so every lap explores new corners!
        _tilt += (Math.Clamp(AudioAnalyzer.StereoPan * 0.24f, -0.26f, 0.26f) - _tilt) * (1f - MathF.Exp(-dt * 2.0f));

        // 2. Arc-length normalized constant-feel speed integrated smoothly from Loudness + Bass!
        EvalFourier(_theta, midX, midY, rx, ry, out float curX, out float curY, out float dx0, out float dy0, out float curKappa);
        float curArcSpd = MathF.Max(60f, MathF.Sqrt(dx0 * dx0 + dy0 * dy0));
        float desiredPxPerSec = (175f + loud * 160f + bass * 140f + AudioAnalyzer.DropImpact * 110f) * calm;
        _theta += (desiredPxPerSec / curArcSpd) * dt;
        if (_theta > MathF.Tau * 100f) _theta -= MathF.Tau * 100f;

        EvalFourier(_theta, midX, midY, rx, ry, out float x, out float y, out float dx, out float dy, out float kappa);
        x = Math.Clamp(x, stage.X + 42f, stage.Right - 42f);
        y = Math.Clamp(y, stage.Y + 42f, stage.Bottom - 42f);

        if (!_inited)
        {
            _cx = x; _cy = y;
            _heading = MathF.Atan2(dy, dx);
            _slipAngle = 0f;
            _prevRLx = _prevRRx = _cx; _prevRLy = _prevRRy = _cy;
            Array.Clear(_skids); Array.Clear(_smoke); Array.Clear(_sparks);
            _inited = true;
        }

        // Smooth position tracking
        float posA = 1f - MathF.Exp(-dt * 18f);
        float vx = (x - _cx) / MathF.Max(dt, 0.004f);
        float vy = (y - _cy) / MathF.Max(dt, 0.004f);
        _cx += (x - _cx) * posA;
        _cy += (y - _cy) * posA;

        // 3. Beat-Locked Clutch Kick + Exact Signed Curvature Drift Angle!
        if (beat) _clutchKick = MathF.Max(_clutchKick, 0.55f + bass * 0.55f);
        _clutchKick *= MathF.Exp(-dt * 6.5f);

        float tangentAng = MathF.Atan2(dy, dx);
        // Curvature kappa directly sets drift direction: left turn -> tail right; right turn -> tail left!
        float normCurv = Math.Clamp(kappa * 145f, -1.0f, 1.0f);
        float targetSlip = normCurv * (0.48f + bass * 0.28f + _clutchKick * 0.36f) * calm;
        _slipAngle += (targetSlip - _slipAngle) * (1f - MathF.Exp(-dt * 8.5f));
        _heading = tangentAng + _slipAngle;
        _steerWheel = Math.Clamp(-_slipAngle * 0.92f, -0.54f, 0.54f); // front wheels counter-steer into the drift!

        float driftAmt = Math.Clamp(MathF.Abs(_slipAngle) / 0.65f, 0f, 1.25f);
        float fwdX = MathF.Cos(_heading), fwdY = MathF.Sin(_heading);
        float rightX = -fwdY, rightY = fwdX;

        const float carLen = 44f, carWid = 22f;
        float rlX = _cx - fwdX * (carLen * 0.36f) - rightX * (carWid * 0.44f);
        float rlY = _cy - fwdY * (carLen * 0.36f) - rightY * (carWid * 0.44f);
        float rrX = _cx - fwdX * (carLen * 0.36f) + rightX * (carWid * 0.44f);
        float rrY = _cy - fwdY * (carLen * 0.36f) + rightY * (carWid * 0.44f);
        float exX = _cx - fwdX * (carLen * 0.52f) + rightX * (carWid * 0.22f);
        float exY = _cy - fwdY * (carLen * 0.52f) + rightY * (carWid * 0.22f);

        // 4. Draw Subtle Glowing Future Fourier Racing Line ahead of the car!
        float rpx = _cx, rpy = _cy;
        const int lineSteps = 22;
        for (int s = 1; s <= lineSteps; s++)
        {
            float f = s / (float)lineSteps;
            EvalFourier(_theta + f * 0.95f, midX, midY, rx, ry, out float nx, out float ny, out _, out _, out _);
            nx = Math.Clamp(nx, stage.X + 42f, stage.Right - 42f);
            ny = Math.Clamp(ny, stage.Y + 42f, stage.Bottom - 42f);
            float fade = (1f - f) * (1f - f) * (0.20f + bass * 0.22f) * calm;
            if (fade > 0.01f)
                Gfx.Line(rpx, rpy, nx, ny, Gfx.Fade(Gfx.Mix(accentCol, bodyCol, f), fade), 2.0f);
            rpx = nx; rpy = ny;
        }

        // 5. Emit Skid Marks, Tire Smoke & Beat Backfire Sparks
        if (driftAmt > 0.16f)
        {
            float distRL = MathF.Sqrt((rlX - _prevRLx) * (rlX - _prevRLx) + (rlY - _prevRLy) * (rlY - _prevRLy));
            if (distRL > 2.5f && distRL < 55f)
            {
                uint skCol = Gfx.Mix(0xFF0A0A12, accentCol, 0.30f + bass * 0.35f);
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRLx, Y0 = _prevRLy, X1 = rlX, Y1 = rlY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRRx, Y0 = _prevRRy, X1 = rrX, Y1 = rrY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
            }

            if (Rand01() < 0.55f + driftAmt * 0.42f)
            {
                bool leftW = Rand01() < 0.5f;
                float ml = 0.55f + Rand01() * 0.50f;
                _smoke[_smokeIdx] = new SmokePuff
                {
                    X = leftW ? rlX : rrX, Y = leftW ? rlY : rrY,
                    Vx = -vx * 0.06f + (Rand01() - 0.5f) * 24f,
                    Vy = -vy * 0.06f + (Rand01() - 0.5f) * 24f,
                    R = 4.5f + driftAmt * 6.5f + bass * 4.5f,
                    Life = ml, MaxLife = ml,
                    Col = leftW ? smokeCol1 : smokeCol2,
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

        // 6. Render Skid Marks, Fluffy Smoke, Sparks
        for (int i = 0; i < MaxSkids; i++)
        {
            if (_skids[i].Life <= 0.01f) continue;
            _skids[i].Life -= dt * 0.36f;
            if (_skids[i].Life <= 0.01f) continue;
            Gfx.Line(_skids[i].X0, _skids[i].Y0, _skids[i].X1, _skids[i].Y1, Gfx.Fade(_skids[i].Col, _skids[i].Life * 0.44f * calm), 3.0f);
        }
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

        // 7. Render Top-Down Coupe with Counter-Steering Front Wheels & Soft Layered Headlight Cones
        var savedTf = Gfx.DC.Transform;
        var carTf = Matrix3x2.CreateRotation(_heading) * Matrix3x2.CreateTranslation(_cx, _cy) * savedTf;
        Gfx.DC.Transform = carTf;

        float beamLen = 74f + high * 32f;
        for (int side = -1; side <= 1; side += 2)
        {
            float ly = side * carWid * 0.30f;
            float sx = carLen * 0.45f;
            Gfx.Circle(sx + beamLen * 0.55f, ly + side * 9f, 22f, Gfx.Fade(0xFFFFF6DD, 0.055f * calm));
            Gfx.Circle(sx + beamLen * 0.30f, ly + side * 4f, 13f, Gfx.Fade(0xFFFFF6DD, 0.095f * calm));
            Gfx.Circle(sx + beamLen * 0.12f, ly + side * 1.5f, 6.5f, Gfx.Fade(0xFFFFFFFF, 0.16f * calm));
        }

        for (int side = -1; side <= 1; side += 2)
        {
            Gfx.DC.Transform = Matrix3x2.CreateRotation(_steerWheel) * Matrix3x2.CreateTranslation(carLen * 0.28f, side * carWid * 0.50f) * carTf;
            Gfx.Fill(new R(-4.5f, -2.0f, 9.0f, 4.0f), 0xFF0A0C12, 1.2f);
        }
        Gfx.DC.Transform = carTf;

        Gfx.Fill(new R(-carLen * 0.55f, -carWid * 0.60f, carLen * 1.10f, carWid * 1.20f), Gfx.Fade(accentCol, (0.24f + bass * 0.32f) * calm), 10f);

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
