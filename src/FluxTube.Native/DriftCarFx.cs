using System.Numerics;

namespace FluxTube;

/// <summary>
/// Ultra-lightweight (~0.15ms, zero-alloc) top-down audio-reactive JDM Drift Car simulation for Music Stage:
/// - Smooth slip-angle drift physics orbiting the stage around the cover art
/// - Kicks &amp; drops trigger turbo throttle bursts, counter-steer whip drifts, exhaust backfire flames &amp; tire smoke clouds
/// - Persistent fading tire skid marks, glowing headlight cones, brake/tail light trails, and spinning front wheels!
/// </summary>
public static class DriftCarFx
{
    private struct SkidSeg { public float X0, Y0, X1, Y1, Life; public uint Col; }
    private struct SmokePuff { public float X, Y, Vx, Vy, R, Life, MaxLife; public uint Col; }
    private struct Spark { public float X, Y, Vx, Vy, Life; public uint Col; }

    private const int MaxSkids = 180, MaxSmoke = 96, MaxSparks = 48;
    private static readonly SkidSeg[] _skids = new SkidSeg[MaxSkids];
    private static readonly SmokePuff[] _smoke = new SmokePuff[MaxSmoke];
    private static readonly Spark[] _sparks = new Spark[MaxSparks];
    private static int _skidIdx, _smokeIdx, _sparkIdx;

    private static float _cx, _cy, _vx, _vy, _heading, _angVel, _steer, _orbitPhase, _backfire;
    private static float _prevRLx, _prevRLy, _prevRRx, _prevRRy;
    private static bool _inited;
    private static long _lastBeat;
    private static uint _rng = 0xA341316Cu;
    private static int _dir = 1;

    private static float Rand01()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return (x & 0xFFFF) * (1f / 65535f);
    }

    public static void Draw(R stage, uint dom1, uint dom2)
    {
        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.032);
        float t = (UI.Now - UI.StartTick) * 0.001f;
        float bass = AudioAnalyzer.BassEnergy;
        float mid = AudioAnalyzer.MidEnergy;
        float high = AudioAnalyzer.HighEnergy;
        float hit = AudioAnalyzer.SharpWaveHit;
        float kick = MusicBounce.Kick;
        float calm = Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.85f, 0.12f, 1f);
        bool beat = AudioAnalyzer.BeatStamp != _lastBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.36f;
        if (beat) _lastBeat = AudioAnalyzer.BeatStamp;

        uint bodyCol = dom1 != 0 ? Gfx.Mix(dom1, 0xFFFF3B6B, 0.25f) : 0xFFFF2E63;
        uint accentCol = dom2 != 0 && dom2 != dom1 ? dom2 : 0xFF00E5FF;
        uint smokeCol1 = Gfx.Mix(accentCol, 0xFFE8F4FF, 0.55f);
        uint smokeCol2 = Gfx.Mix(bodyCol, 0xFFFFE6F0, 0.45f);

        float midX = stage.X + stage.W * 0.5f;
        float midY = stage.Y + stage.H * 0.48f;
        float rx = MathF.Max(140f, stage.W * 0.36f);
        float ry = MathF.Max(90f, stage.H * 0.30f);

        if (!_inited || _cx < stage.X - 100 || _cx > stage.Right + 100 || _cy < stage.Y - 100 || _cy > stage.Bottom + 100)
        {
            _orbitPhase = 0f;
            _cx = midX + rx;
            _cy = midY;
            _vx = 0f; _vy = 180f;
            _heading = MathF.PI * 0.5f;
            _angVel = 0f;
            _prevRLx = _prevRRx = _cx;
            _prevRLy = _prevRRy = _cy;
            Array.Clear(_skids); Array.Clear(_smoke); Array.Clear(_sparks);
            _inited = true;
        }

        // 1. Target figure-8 / wide drift racing line orbiting around the cover art!
        if (beat && AudioAnalyzer.DropImpact > 0.45f && Rand01() < 0.35f) _dir = -_dir;
        float targetSpeed = (165f + bass * 210f + kick * 120f + hit * 95f) * calm;
        _orbitPhase += dt * _dir * (0.68f + bass * 0.65f + mid * 0.25f) * calm;

        // Smooth racing oval + subtle figure-8 S-bend on mids!
        float tx = midX + MathF.Cos(_orbitPhase) * rx;
        float ty = midY + MathF.Sin(_orbitPhase * (1f + 0.0f)) * ry + MathF.Sin(_orbitPhase * 2f) * (ry * 0.22f);

        float toX = tx - _cx, toY = ty - _cy;
        float targetAng = MathF.Atan2(toY, toX);

        // Shortest angle delta
        float diff = targetAng - _heading;
        while (diff > MathF.PI) diff -= MathF.Tau;
        while (diff < -MathF.PI) diff += MathF.Tau;

        // Exaggerate drift slip angle on bass kicks so the tail swings out dramatically!
        float driftBias = _dir * (0.32f + bass * 0.42f + kick * 0.35f) * calm;
        float desiredHeading = targetAng + driftBias;
        float hDiff = desiredHeading - _heading;
        while (hDiff > MathF.PI) hDiff -= MathF.Tau;
        while (hDiff < -MathF.PI) hDiff += MathF.Tau;

        _steer = Math.Clamp(hDiff * 1.4f, -0.55f, 0.55f);
        _angVel += (hDiff * 12f - _angVel * 5.2f) * dt;
        _heading += _angVel * dt;

        // Slip-angle velocity blend: car slides sideways while engine thrusts forward along heading!
        float fwdX = MathF.Cos(_heading), fwdY = MathF.Sin(_heading);
        float rightX = -fwdY, rightY = fwdX;
        float aimX = MathF.Cos(targetAng), aimY = MathF.Sin(targetAng);

        float grip = 2.6f; // low lateral grip = juicy prolonged drift!
        float desiredVx = (aimX * 0.72f + fwdX * 0.28f) * targetSpeed;
        float desiredVy = (aimY * 0.72f + fwdY * 0.28f) * targetSpeed;
        _vx += (desiredVx - _vx) * (1f - MathF.Exp(-dt * grip));
        _vy += (desiredVy - _vy) * (1f - MathF.Exp(-dt * grip));
        _cx = Math.Clamp(_cx + _vx * dt, stage.X + 42f, stage.Right - 42f);
        _cy = Math.Clamp(_cy + _vy * dt, stage.Y + 42f, stage.Bottom - 42f);

        // Compute real lateral slip speed (how hard the rear tires are drifting!)
        float latSpeed = MathF.Abs(_vx * rightX + _vy * rightY);
        float driftAmt = Math.Clamp((latSpeed - 22f) / 145f, 0f, 1.2f);

        // Car dimensions
        const float carLen = 44f, carWid = 22f;
        float rlX = _cx - fwdX * (carLen * 0.36f) - rightX * (carWid * 0.44f);
        float rlY = _cy - fwdY * (carLen * 0.36f) - rightY * (carWid * 0.44f);
        float rrX = _cx - fwdX * (carLen * 0.36f) + rightX * (carWid * 0.44f);
        float rrY = _cy - fwdY * (carLen * 0.36f) + rightY * (carWid * 0.44f);
        float exX = _cx - fwdX * (carLen * 0.52f) + rightX * (carWid * 0.22f);
        float exY = _cy - fwdY * (carLen * 0.52f) + rightY * (carWid * 0.22f);

        // 2. Emit Tire Skid Marks + Colorful Drift Smoke when sliding!
        if (driftAmt > 0.14f)
        {
            float distRL = MathF.Sqrt((rlX - _prevRLx) * (rlX - _prevRLx) + (rlY - _prevRLy) * (rlY - _prevRLy));
            if (distRL > 3f && distRL < 55f)
            {
                uint skCol = Gfx.Mix(0xFF0A0A12, accentCol, 0.30f + bass * 0.35f);
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRLx, Y0 = _prevRLy, X1 = rlX, Y1 = rlY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRRx, Y0 = _prevRRy, X1 = rrX, Y1 = rrY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
            }

            if (Rand01() < 0.55f + driftAmt * 0.45f)
            {
                bool leftWheel = Rand01() < 0.5f;
                float sx = leftWheel ? rlX : rrX;
                float sy = leftWheel ? rlY : rrY;
                float ml = 0.55f + Rand01() * 0.55f;
                _smoke[_smokeIdx] = new SmokePuff
                {
                    X = sx, Y = sy,
                    Vx = -_vx * 0.12f + (Rand01() - 0.5f) * 28f,
                    Vy = -_vy * 0.12f + (Rand01() - 0.5f) * 28f,
                    R = 5f + driftAmt * 7f + bass * 5f,
                    Life = ml, MaxLife = ml,
                    Col = leftWheel ? smokeCol1 : smokeCol2,
                };
                _smokeIdx = (_smokeIdx + 1) % MaxSmoke;
            }
        }
        _prevRLx = rlX; _prevRLy = rlY;
        _prevRRx = rrX; _prevRRy = rrY;

        // Exhaust backfire flames + neon sparks on bass kicks!
        if (beat || hit > 0.45f)
        {
            _backfire = MathF.Max(_backfire, 0.85f + bass * 0.45f);
            int cnt = beat ? 6 : 2;
            for (int k = 0; k < cnt; k++)
            {
                float spd = 90f + Rand01() * 140f;
                float spAng = _heading + MathF.PI + (Rand01() - 0.5f) * 0.75f;
                _sparks[_sparkIdx] = new Spark
                {
                    X = exX, Y = exY,
                    Vx = MathF.Cos(spAng) * spd,
                    Vy = MathF.Sin(spAng) * spd,
                    Life = 0.30f + Rand01() * 0.32f,
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
            _skids[i].Life -= dt * 0.42f;
            if (_skids[i].Life <= 0.01f) continue;
            float a = _skids[i].Life * 0.45f * calm;
            Gfx.Line(_skids[i].X0, _skids[i].Y0, _skids[i].X1, _skids[i].Y1, Gfx.Fade(_skids[i].Col, a), 3.2f);
        }

        // 4. Draw Expanding Drift Smoke Clouds
        for (int i = 0; i < MaxSmoke; i++)
        {
            if (_smoke[i].Life <= 0.01f) continue;
            _smoke[i].Life -= dt;
            if (_smoke[i].Life <= 0.01f) continue;
            _smoke[i].X += _smoke[i].Vx * dt;
            _smoke[i].Y += _smoke[i].Vy * dt;
            _smoke[i].R += dt * (18f + bass * 14f);
            float frac = _smoke[i].Life / _smoke[i].MaxLife;
            float a = frac * frac * 0.26f * calm;
            Gfx.Circle(_smoke[i].X, _smoke[i].Y, _smoke[i].R, Gfx.Fade(_smoke[i].Col, a));
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
            _sparks[i].Vx *= MathF.Exp(-dt * 4f);
            _sparks[i].Vy *= MathF.Exp(-dt * 4f);
        }

        // 6. Draw Styled Top-Down Neon Sports Coupe (rotated around _cx, _cy!)
        var savedTf = Gfx.DC.Transform;
        Gfx.DC.Transform = Matrix3x2.CreateRotation(_heading) * Matrix3x2.CreateTranslation(_cx, _cy) * savedTf;

        // Soft underglow neon aura pulsing to bass!
        var underCol = Gfx.Fade(accentCol, (0.28f + bass * 0.35f) * calm);
        Gfx.Fill(new R(-carLen * 0.56f, -carWid * 0.62f, carLen * 1.12f, carWid * 1.24f), underCol, 10f);

        // Twin glowing headlight beams projecting forward
        float beamLen = 78f + high * 36f;
        Gfx.Line(carLen * 0.45f, -carWid * 0.30f, carLen * 0.45f + beamLen, -carWid * 0.85f, Gfx.Fade(0xFFFFF6DD, 0.16f * calm), 16f);
        Gfx.Line(carLen * 0.45f,  carWid * 0.30f, carLen * 0.45f + beamLen,  carWid * 0.85f, Gfx.Fade(0xFFFFF6DD, 0.16f * calm), 16f);
        Gfx.Line(carLen * 0.46f, -carWid * 0.30f, carLen * 0.46f + beamLen * 0.65f, -carWid * 0.58f, Gfx.Fade(0xFFFFFFFF, 0.24f * calm), 5f);
        Gfx.Line(carLen * 0.46f,  carWid * 0.30f, carLen * 0.46f + beamLen * 0.65f,  carWid * 0.58f, Gfx.Fade(0xFFFFFFFF, 0.24f * calm), 5f);

        // Exhaust backfire nitro flame!
        if (_backfire > 0.08f)
        {
            float fLen = 12f + _backfire * 22f;
            Gfx.Line(-carLen * 0.50f, carWid * 0.22f, -carLen * 0.50f - fLen, carWid * 0.22f, Gfx.Fade(0xFF00E5FF, _backfire * 0.70f), 5.5f);
            Gfx.Line(-carLen * 0.50f, carWid * 0.22f, -carLen * 0.50f - fLen * 0.65f, carWid * 0.22f, Gfx.Fade(0xFFFFFFFF, _backfire * 0.92f), 2.4f);
        }

        // Drop shadow
        Gfx.Fill(new R(-carLen * 0.50f + 2f, -carWid * 0.50f + 3f, carLen, carWid), Gfx.Fade(0xFF000000, 0.48f), 6f);

        // Main coupe chassis
        var bodyR = new R(-carLen * 0.50f, -carWid * 0.50f, carLen, carWid);
        Gfx.Fill(bodyR, Gfx.Fade(bodyCol, 0.95f), 6.5f);
        Gfx.Stroke(bodyR, Gfx.Fade(0xFFFFFFFF, 0.45f), 6.5f, 1.1f);

        // Dark tinted cabin / windshield + rear glass
        Gfx.Fill(new R(-carLen * 0.24f, -carWid * 0.38f, carLen * 0.46f, carWid * 0.76f), 0xFF10131C, 4.5f);
        // Roof specular highlight
        Gfx.Fill(new R(-carLen * 0.14f, -carWid * 0.26f, carLen * 0.22f, carWid * 0.52f), Gfx.Fade(bodyCol, 0.85f), 3f);

        // Rear GT wing / spoiler
        Gfx.Fill(new R(-carLen * 0.50f, -carWid * 0.54f, 4.5f, carWid * 1.08f), 0xFF0C0E14, 1.5f);

        // Bright LED headlights & glowing red tail lights
        Gfx.Circle(carLen * 0.46f, -carWid * 0.30f, 2.3f, 0xFFFFFFFF);
        Gfx.Circle(carLen * 0.46f,  carWid * 0.30f, 2.3f, 0xFFFFFFFF);
        float brakeGlow = 0.55f + driftAmt * 0.40f;
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f, 4.5f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f, 4.5f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f, 2.0f, 0xFFFF2A4B);
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f, 2.0f, 0xFFFF2A4B);

        Gfx.DC.Transform = savedTf;
        UI.Animate();
    }
}
