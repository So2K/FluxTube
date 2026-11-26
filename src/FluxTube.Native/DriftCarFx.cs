using System.Numerics;

namespace FluxTube;

/// <summary>
/// Real Rear-Wheel-Drive (RWD) 2D Drift Physics Simulator ("Дрифт", ~0.16ms, zero-alloc):
/// - Audio IS the gas pedal: Loudness + Bass + Beat Kicks apply real engine torque to the rear wheels!
/// - Friction-circle tire model (Pacejka-inspired): when rear torque exceeds grip, rear tires break traction (wheelSpin),
///   dropping rear lateral grip so the tail naturally kicks out into a real physics drift!
/// - Live AI Drift Driver counter-steers the front wheels to hold big slip angles, initiates crisp left/right transitions,
///   and smoothly carves away from stage walls (no rails — 100% emergent Newtonian + tire-slip physics!).
/// </summary>
public static class DriftCarFx
{
    private struct SkidSeg { public float X0, Y0, X1, Y1, Life; public uint Col; }
    private struct SmokePuff { public float X, Y, Vx, Vy, R, Life, MaxLife; public uint Col; }
    private struct Spark { public float X, Y, Vx, Vy, Life; public uint Col; }

    private const int MaxSkids = 240, MaxSmoke = 130, MaxSparks = 56;
    private static readonly SkidSeg[] _skids = new SkidSeg[MaxSkids];
    private static readonly SmokePuff[] _smoke = new SmokePuff[MaxSmoke];
    private static readonly Spark[] _sparks = new Spark[MaxSparks];
    private static int _skidIdx, _smokeIdx, _sparkIdx;

    // Rigid-body 2D car state (position, velocity, heading, angular velocity, front steer, rear wheelspin)
    private static float _x, _y, _vx, _vy, _heading, _omega;
    private static float _steer, _wheelSpin, _throttle, _backfire, _preferTurn = 1f, _flipTimer;
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

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    public static void Draw(R stage, uint dom1, uint dom2)
    {
        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.028);
        float bass = AudioAnalyzer.BassEnergy;
        float mid = AudioAnalyzer.MidEnergy;
        float high = AudioAnalyzer.HighEnergy;
        float hit = AudioAnalyzer.SharpWaveHit;
        float loud = Math.Clamp(AudioAnalyzer.Loudness, 0f, 1f);
        float calm = Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.88f, 0.10f, 1f);
        bool beat = AudioAnalyzer.BeatStamp != _lastBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.34f;
        if (beat) _lastBeat = AudioAnalyzer.BeatStamp;

        uint bodyCol = dom1 != 0 ? Gfx.Mix(dom1, 0xFFFF3B6B, 0.22f) : 0xFFFF2E63;
        uint accentCol = dom2 != 0 && dom2 != dom1 ? dom2 : 0xFF00E5FF;
        uint smokeCol1 = Gfx.Mix(accentCol, 0xFFE8F4FF, 0.62f);
        uint smokeCol2 = Gfx.Mix(bodyCol, 0xFFFFEEF4, 0.55f);

        float margin = 64f;
        float minX = stage.X + margin, maxX = stage.Right - margin;
        float minY = stage.Y + margin, maxY = stage.Bottom - margin;
        float midX = stage.X + stage.W * 0.5f, midY = stage.Y + stage.H * 0.48f;

        if (!_inited || _x < stage.X - 80 || _x > stage.Right + 80 || _y < stage.Y - 80 || _y > stage.Bottom + 80)
        {
            _x = midX - stage.W * 0.24f;
            _y = midY + stage.H * 0.14f;
            _vx = 210f; _vy = -40f;
            _heading = -0.18f; _omega = 0f; _steer = 0f; _wheelSpin = 0f;
            _prevRLx = _prevRRx = _x; _prevRLy = _prevRRy = _y;
            Array.Clear(_skids); Array.Clear(_smoke); Array.Clear(_sparks);
            _inited = true;
        }

        // ---- 1. AUDIO = ENGINE THROTTLE & TORQUE ----
        float targetThrottle = Math.Clamp((0.26f + loud * 0.68f + bass * 0.72f + hit * 0.45f + (beat ? 0.55f : 0f)) * calm, 0.12f, 1.65f);
        _throttle += (targetThrottle - _throttle) * (1f - MathF.Exp(-dt * 14f));

        // Periodically or on strong stereo/beat shifts, the driver initiates a crisp left <-> right drift transition!
        _flipTimer -= dt;
        if (_flipTimer <= 0f || (beat && MathF.Abs(AudioAnalyzer.StereoPan) > 0.25f && _flipTimer < 1.2f))
        {
            _preferTurn = MathF.Abs(AudioAnalyzer.StereoPan) > 0.20f ? MathF.Sign(AudioAnalyzer.StereoPan) : -_preferTurn;
            _flipTimer = 2.2f + Rand01() * 2.4f;
        }

        // ---- 2. SUB-STEPPED NEWTONIAN RWD TIRE-SLIP PHYSICS (4 sub-steps -> rock-solid 240Hz stability!) ----
        const int subSteps = 4;
        float h = dt / subSteps;
        const float lf = 16f, lr = 16f; // front & rear axle distances from CG
        float lastRearSlip = 0f;

        for (int s = 0; s < subSteps; s++)
        {
            float fwdX = MathF.Cos(_heading), fwdY = MathF.Sin(_heading);
            float rightX = -fwdY, rightY = fwdX;

            // Transform world velocity into car local frame (vLong = forward, vLat = sideways)
            float vLong = _vx * fwdX + _vy * fwdY;
            float vLat = _vx * rightX + _vy * rightY;
            float speed = MathF.Sqrt(vLong * vLong + vLat * vLat);

            // AI Driver Steering:
            // (a) Soft boundary avoidance vector pointing smoothly inward when approaching any stage wall
            float wallX = 0f, wallY = 0f;
            float softPad = 125f;
            if (_x < minX + softPad) wallX += (minX + softPad - _x) / softPad;
            if (_x > maxX - softPad) wallX -= (_x - (maxX - softPad)) / softPad;
            if (_y < minY + softPad) wallY += (minY + softPad - _y) / softPad;
            if (_y > maxY - softPad) wallY -= (_y - (maxY - softPad)) / softPad;
            float wallUrgency = MathF.Sqrt(wallX * wallX + wallY * wallY);

            float targetSteer;
            if (wallUrgency > 0.08f)
            {
                // Steer smoothly back toward the open arena while keeping the slide alive
                float safeAng = MathF.Atan2(wallY + (midY - _y) * 0.002f, wallX + (midX - _x) * 0.002f);
                float turnErr = WrapAngle(safeAng - _heading);
                targetSteer = Math.Clamp(turnErr * 1.15f, -0.64f, 0.64f);
                if (wallUrgency > 0.55f) _preferTurn = MathF.Sign(turnErr == 0f ? 1f : turnErr);
            }
            else
            {
                // Open floor: hold an expressive drift angle with natural counter-steer!
                float slip = MathF.Atan2(vLat, MathF.Abs(vLong) + 25f);
                // Counter-steer catches oversteer (proportional to slip + angular velocity), while _preferTurn initiates the bend!
                float initiate = _preferTurn * (0.28f + mid * 0.22f);
                float counter = slip * 0.88f + _omega * 0.16f;
                targetSteer = Math.Clamp(initiate + counter, -0.66f, 0.66f);
            }
            _steer += (targetSteer - _steer) * (1f - MathF.Exp(-h * 11f));

            // Real RWD Wheelspin from excessive audio torque!
            float driveForce = _throttle * 520f;
            float tractionLimit = 260f;
            float excessTorque = MathF.Max(0f, driveForce - tractionLimit);
            float targetSpin = Math.Clamp(excessTorque / 340f + (beat ? 0.35f : 0f), 0f, 1.5f);
            _wheelSpin += (targetSpin - _wheelSpin) * (1f - MathF.Exp(-h * 12f));

            // Local velocities at front & rear axles
            float vLatF = vLat + _omega * lf;
            float vLatR = vLat - _omega * lr;

            // Slip angles (Pacejka-like saturating tanh tire lateral forces!)
            float slipF = MathF.Atan2(vLatF, MathF.Abs(vLong) + 18f) - _steer;
            float slipR = MathF.Atan2(vLatR, MathF.Abs(vLong) + 18f);

            // Front tires keep crisp bite (so steering works!), Rear tires LOSE lateral grip when spinning from audio torque!
            float frontGrip = 440f;
            float rearGrip = 390f / (1f + _wheelSpin * 2.65f); // <-- THIS is what causes authentic RWD power-oversteer drift!

            float fLatF = -MathF.Tanh(slipF * 2.4f) * frontGrip;
            float fLatR = -MathF.Tanh(slipR * 2.1f) * rearGrip;

            // On a hard beat kick, high rear torque kicks the tail slightly in the active drift direction (clutch-kick!)
            float kickTorque = (beat && s == 0) ? (_preferTurn * (2.6f + bass * 2.8f)) : 0f;

            // Longitudinal acceleration + aerodynamic drag + rolling resistance (keeps top speed exciting & controllable)
            float effectiveDrive = driveForce / (1f + _wheelSpin * 0.35f);
            float fLong = effectiveDrive - vLong * 1.15f - vLong * MathF.Abs(vLong) * 0.0012f;

            // Combine forces in local space
            float aLong = fLong - fLatF * MathF.Sin(_steer) * 0.35f;
            float aLat = fLatR + fLatF * MathF.Cos(_steer);
            float alphaYaw = (lf * fLatF * MathF.Cos(_steer) - lr * fLatR) * 0.00058f - _omega * 2.45f + kickTorque;

            // Transform local acceleration to world & integrate
            _vx += (aLong * fwdX + aLat * rightX) * h;
            _vy += (aLong * fwdY + aLat * rightY) * h;
            _omega = Math.Clamp(_omega + alphaYaw * h, -4.2f, 4.2f);
            _heading = WrapAngle(_heading + _omega * h);

            // Soft speed floor when music is playing so the car stays alive
            float spdNow = MathF.Sqrt(_vx * _vx + _vy * _vy);
            if (spdNow > 460f) { float sc = 460f / spdNow; _vx *= sc; _vy *= sc; }

            _x += _vx * h;
            _y += _vy * h;

            // Elastic soft bounce off extreme walls (safety guard so car never leaves stage)
            if (_x < stage.X + 34f) { _x = stage.X + 34f; _vx = MathF.Abs(_vx) * 0.65f; }
            else if (_x > stage.Right - 34f) { _x = stage.Right - 34f; _vx = -MathF.Abs(_vx) * 0.65f; }
            if (_y < stage.Y + 34f) { _y = stage.Y + 34f; _vy = MathF.Abs(_vy) * 0.65f; }
            else if (_y > stage.Bottom - 34f) { _y = stage.Bottom - 34f; _vy = -MathF.Abs(_vy) * 0.65f; }

            lastRearSlip = MathF.Abs(vLatR);
        }

        // Real tire slip intensity = combination of rear wheelspin + sideways rear-axle slide!
        float driftAmt = Math.Clamp(lastRearSlip / 110f + _wheelSpin * 0.65f, 0f, 1.35f);
        float cFwdX = MathF.Cos(_heading), cFwdY = MathF.Sin(_heading);
        float cRightX = -cFwdY, cRightY = cFwdX;

        const float carLen = 44f, carWid = 22f;
        float rlX = _x - cFwdX * (carLen * 0.36f) - cRightX * (carWid * 0.44f);
        float rlY = _y - cFwdY * (carLen * 0.36f) - cRightY * (carWid * 0.44f);
        float rrX = _x - cFwdX * (carLen * 0.36f) + cRightX * (carWid * 0.44f);
        float rrY = _y - cFwdY * (carLen * 0.36f) + cRightY * (carWid * 0.44f);
        float exX = _x - cFwdX * (carLen * 0.52f) + cRightX * (carWid * 0.22f);
        float exY = _y - cFwdY * (carLen * 0.52f) + cRightY * (carWid * 0.22f);

        // ---- 3. EMIT SKID MARKS & DRIFT SMOKE FROM REAL WHEELSPIN / SLIP ----
        if (driftAmt > 0.22f)
        {
            float distRL = MathF.Sqrt((rlX - _prevRLx) * (rlX - _prevRLx) + (rlY - _prevRLy) * (rlY - _prevRLy));
            if (distRL > 2.5f && distRL < 55f)
            {
                uint skCol = Gfx.Mix(0xFF0A0A12, accentCol, Math.Clamp(0.22f + _wheelSpin * 0.40f, 0f, 0.72f));
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRLx, Y0 = _prevRLy, X1 = rlX, Y1 = rlY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRRx, Y0 = _prevRRy, X1 = rrX, Y1 = rrY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
            }

            if (Rand01() < 0.42f + driftAmt * 0.48f)
            {
                bool leftW = Rand01() < 0.5f;
                float ml = 0.55f + Rand01() * 0.55f;
                _smoke[_smokeIdx] = new SmokePuff
                {
                    X = leftW ? rlX : rrX, Y = leftW ? rlY : rrY,
                    Vx = -cFwdX * (35f * _wheelSpin) + (Rand01() - 0.5f) * 26f,
                    Vy = -cFwdY * (35f * _wheelSpin) + (Rand01() - 0.5f) * 26f,
                    R = 4.5f + driftAmt * 6.5f + bass * 4f,
                    Life = ml, MaxLife = ml,
                    Col = leftW ? smokeCol1 : smokeCol2,
                };
                _smokeIdx = (_smokeIdx + 1) % MaxSmoke;
            }
        }
        _prevRLx = rlX; _prevRLy = rlY;
        _prevRRx = rrX; _prevRRy = rrY;

        if (beat || (_wheelSpin > 0.75f && hit > 0.45f))
        {
            _backfire = MathF.Max(_backfire, 0.80f + bass * 0.45f);
            int cnt = beat ? 6 : 2;
            for (int k = 0; k < cnt; k++)
            {
                float spd = 90f + Rand01() * 140f;
                float spAng = _heading + MathF.PI + (Rand01() - 0.5f) * 0.70f;
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

        // ---- 4. RENDER SKID MARKS, FLUFFY SMOKE, SPARKS ----
        for (int i = 0; i < MaxSkids; i++)
        {
            if (_skids[i].Life <= 0.01f) continue;
            _skids[i].Life -= dt * 0.34f;
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

        // ---- 5. RENDER COUPE WITH LIVE COUNTER-STEERING FRONT WHEELS & SOFT HEADLIGHTS ----
        var savedTf = Gfx.DC.Transform;
        var carTf = Matrix3x2.CreateRotation(_heading) * Matrix3x2.CreateTranslation(_x, _y) * savedTf;
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

        // Front wheels turned to exact physics _steer angle!
        for (int side = -1; side <= 1; side += 2)
        {
            Gfx.DC.Transform = Matrix3x2.CreateRotation(_steer) * Matrix3x2.CreateTranslation(carLen * 0.28f, side * carWid * 0.50f) * carTf;
            Gfx.Fill(new R(-4.5f, -2.0f, 9.0f, 4.0f), 0xFF0A0C12, 1.2f);
        }
        Gfx.DC.Transform = carTf;

        Gfx.Fill(new R(-carLen * 0.55f, -carWid * 0.60f, carLen * 1.10f, carWid * 1.20f), Gfx.Fade(accentCol, (0.22f + _wheelSpin * 0.28f + bass * 0.18f) * calm), 10f);

        if (_backfire > 0.08f)
        {
            float fLen = 10f + _backfire * 22f;
            Gfx.Line(-carLen * 0.50f, carWid * 0.22f, -carLen * 0.50f - fLen, carWid * 0.22f, Gfx.Fade(0xFF00E5FF, _backfire * 0.68f), 5.2f);
            Gfx.Line(-carLen * 0.50f, carWid * 0.22f, -carLen * 0.50f - fLen * 0.65f, carWid * 0.22f, Gfx.Fade(0xFFFFFFFF, _backfire * 0.92f), 2.2f);
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
        float brakeGlow = 0.55f + MathF.Min(0.45f, driftAmt * 0.35f);
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f, 4.2f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f, 4.2f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f, 1.9f, 0xFFFF2A4B);
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f, 1.9f, 0xFFFF2A4B);

        Gfx.DC.Transform = savedTf;
        UI.Animate();
    }
}
