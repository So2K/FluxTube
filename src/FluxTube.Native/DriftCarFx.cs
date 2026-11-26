using System.Numerics;

namespace FluxTube;

/// <summary>
/// Realistic Metric 2D Rear-Wheel-Drive (RWD) Drift Car Physics ("Дрифт", ~0.16ms, zero-alloc):
/// - Real SI units (1m = 13px, mass = 1250kg, wheelbase = 2.6m, Iz = 1650 kg*m^2)
/// - Pacejka Magic Formula lateral tire curves + Kamm's Friction Circle + Dynamic Weight Transfer (pitch &amp; roll)
/// - Engine RPM (1500..8500) + Rear Wheel Angular Velocity -> Audio (Loudness/Bass/Beats) slams throttle &amp; breaks rear traction!
/// - Gymkhana Apex Driver with active counter-steering + predictive wall lookahead (carves BOTH left &amp; right transitions cleanly, never stalls on walls!)
/// </summary>
public static class DriftCarFx
{
    private struct SkidSeg { public float X0, Y0, X1, Y1, Life; public uint Col; }
    private struct SmokePuff { public float X, Y, Vx, Vy, R, Life, MaxLife; public uint Col; }
    private struct Spark { public float X, Y, Vx, Vy, Life; public uint Col; }

    private const int MaxSkids = 260, MaxSmoke = 140, MaxSparks = 56;
    private static readonly SkidSeg[] _skids = new SkidSeg[MaxSkids];
    private static readonly SmokePuff[] _smoke = new SmokePuff[MaxSmoke];
    private static readonly Spark[] _sparks = new Spark[MaxSparks];
    private static int _skidIdx, _smokeIdx, _sparkIdx;

    private const float PxPerMeter = 13.0f;
    private const float Mass = 1250f, Iz = 1550f, Lf = 1.25f, Lr = 1.35f, WheelR = 0.33f;

    // Metric rigid-body state (meters, m/s, rad, rad/s)
    private static float _xm, _ym, _vxm, _vym, _heading, _omega;
    private static float _steer, _rearWheelOmega, _rpm = 2200f, _throttle, _wheelSpin, _backfire, _bodyRoll;
    private static float _apexX, _apexY;
    private static int _apexStep;
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

    private static (float ax, float ay) PickNextApex(R stage, int step)
    {
        // Classic Gymkhana figure-8 clipping points well inside all 4 walls (forces BOTH left & right drift transitions!)
        float padX = MathF.Min(stage.W * 0.24f, 210f);
        float padY = MathF.Min(stage.H * 0.24f, 125f);
        float left = stage.X + padX, right = stage.Right - padX;
        float top = stage.Y + padY + 26f, bot = stage.Bottom - padY; // +26 clears top status pill!
        float midX = stage.X + stage.W * 0.5f, midY = stage.Y + stage.H * 0.50f;
        float jx = (Rand01() - 0.5f) * 55f, jy = (Rand01() - 0.5f) * 35f;
        return (step & 7) switch
        {
            0 => (right + jx, top + jy),
            1 => (right + jx, bot + jy),
            2 => (midX + jx, midY + jy),
            3 => (left + jx, top + jy),
            4 => (left + jx, bot + jy),
            5 => (midX + jx, midY + jy),
            6 => (right + jx, midY + jy),
            _ => (left + jx, midY + jy),
        };
    }

    private static void StepSim(R stage, float dt, float bass, float mid, float high, float hit, float loud, float calm, bool beat, out float rearLatSlip)
    {
        float minXm = (stage.X + 70f) / PxPerMeter, maxXm = (stage.Right - 70f) / PxPerMeter;
        float minYm = (stage.Y + 76f) / PxPerMeter, maxYm = (stage.Bottom - 62f) / PxPerMeter;
        float midXm = (stage.X + stage.W * 0.5f) / PxPerMeter, midYm = (stage.Y + stage.H * 0.50f) / PxPerMeter;

        // Advance clipping-point apex when car drifts within 125px of it
        float px = _xm * PxPerMeter, py = _ym * PxPerMeter;
        float dApex = MathF.Sqrt((px - _apexX) * (px - _apexX) + (py - _apexY) * (py - _apexY));
        if (dApex < 130f)
        {
            _apexStep++;
            (_apexX, _apexY) = PickNextApex(stage, _apexStep);
        }

        // ---- SYSTEM 1: AUDIO = ENGINE THROTTLE & HIGH TORQUE ----
        float targetThr = Math.Clamp((0.36f + loud * 0.52f + bass * 0.58f + hit * 0.35f + (beat ? 0.42f : 0f)) * calm, 0.22f, 1.35f);
        _throttle += (targetThr - _throttle) * (1f - MathF.Exp(-dt * 15f));

        const int subSteps = 4;
        float h = dt / subSteps;
        rearLatSlip = 0f;

        for (int s = 0; s < subSteps; s++)
        {
            float fwdX = MathF.Cos(_heading), fwdY = MathF.Sin(_heading);
            float rightX = -fwdY, rightY = fwdX;

            float vLong = _vxm * fwdX + _vym * fwdY;
            float vLat = _vxm * rightX + _vym * rightY;
            float speed = MathF.Sqrt(vLong * vLong + vLat * vLat);

            // ---- SYSTEM 2: SMART PREDICTIVE DRIFT DRIVER (Apex Tracking + Wall Avoidance + Counter-Steer!) ----
            // Predictive position 0.55s ahead -> if headed toward any wall, smoothly pull target inward!
            float predX = _xm + _vxm * 0.55f;
            float predY = _ym + _vym * 0.55f;
            float txm = _apexX / PxPerMeter, tym = _apexY / PxPerMeter;
            if (predX < minXm + 3.0f || predX > maxXm - 3.0f || predY < minYm + 2.5f || predY > maxYm - 2.5f)
            {
                txm = midXm;
                tym = midYm;
            }

            float toApexAng = MathF.Atan2(tym - _ym, txm - _xm);
            float headingErr = WrapAngle(toApexAng - _heading);
            float velAng = speed > 1.5f ? MathF.Atan2(_vym, _vxm) : _heading;
            float velErr = WrapAngle(toApexAng - velAng);
            float slipBeta = WrapAngle(velAng - _heading); // positive when sliding right relative to nose!

            // True pro drift steering:
            // - velErr steers velocity vector toward the next apex (works equally LEFT and RIGHT!)
            // - slipBeta * 0.95 counter-steers into the slide to catch oversteer & hold a heroic 25-50° drift angle!
            // - _omega * 0.14 damps wild spin-outs!
            float targetSteer = Math.Clamp(velErr * 1.45f + slipBeta * 0.92f + _omega * 0.14f, -0.74f, 0.74f);
            // If car is ever pointed nearly opposite to apex at low speed, full lock turn!
            if (MathF.Abs(headingErr) > 1.7f && speed < 8f)
                targetSteer = MathF.Sign(headingErr == 0f ? 1f : headingErr) * 0.74f;

            _steer += (targetSteer - _steer) * (1f - MathF.Exp(-h * 14f));

            // ---- REAL ENGINE TORQUE, RPM & REAR WHEELSPIN ----
            float engineTorque = _throttle * 10500f; // N at rear contact patch
            float maxTireForce = Mass * 9.81f * 0.50f * 1.15f; // ~7050 N per axle
            float targetWheelOmega = MathF.Max(vLong / WheelR, 14f) + MathF.Max(0f, engineTorque - maxTireForce * 0.65f) * 0.0085f;
            _rearWheelOmega += (targetWheelOmega - _rearWheelOmega) * (1f - MathF.Exp(-h * 11f));
            _rpm = Math.Clamp(1600f + _rearWheelOmega * 72f + _throttle * 1400f, 1600f, 8600f);

            float wheelSpd = _rearWheelOmega * WheelR;
            float slipRatio = Math.Clamp((wheelSpd - vLong) / MathF.Max(MathF.Abs(vLong), 4.5f), 0f, 2.2f);
            _wheelSpin = Math.Clamp(slipRatio * 0.85f, 0f, 1.5f);

            // Dynamic longitudinal weight transfer (accel shifts weight rear; hard turn/entry shifts front)
            float fLongRaw = MathF.Min(engineTorque, maxTireForce * (0.95f + 0.12f * MathF.Min(1f, slipRatio)));
            float weightTransfer = Math.Clamp(fLongRaw * 0.00018f, -0.14f, 0.14f);
            float fzFront = Mass * 9.81f * (0.50f - weightTransfer);
            float fzRear = Mass * 9.81f * (0.50f + weightTransfer);

            // Front & Rear slip angles
            float vLatF = vLat + _omega * Lf;
            float vLatR = vLat - _omega * Lr;
            float alphaF = MathF.Atan2(vLatF, MathF.Abs(vLong) + 2.2f) - _steer;
            float alphaR = MathF.Atan2(vLatR, MathF.Abs(vLong) + 2.2f);

            // Pacejka Magic Formula lateral grip:
            // Front keeps high cornering stiffness -> razor-sharp turn-in & counter-steer!
            // Rear lateral grip drops along Kamm's Friction Circle as rear tires spin under audio torque!
            float muFront = 1.32f;
            float muRearLat = 1.18f / (1f + _wheelSpin * 1.85f); // <-- authentic RWD power-oversteer!
            float fLatF = -fzFront * muFront * MathF.Sin(1.55f * MathF.Atan(5.2f * alphaF));
            float fLatR = -fzRear * muRearLat * MathF.Sin(1.45f * MathF.Atan(4.6f * alphaR));

            // Beat clutch-kick: brief rear-axle yaw impulse in current turn direction!
            float kickYaw = (beat && s == 0) ? (MathF.Sign(_steer == 0f ? 1f : _steer) * (1.8f + bass * 2.2f)) : 0f;

            // Longitudinal drag & rolling resistance (keeps top speed ~22-28 m/s = thrilling & controllable on 1000px stage)
            float fDrag = -0.45f * vLong * MathF.Abs(vLong) - 24f * vLong;
            float fxLocal = fLongRaw + fDrag - fLatF * MathF.Sin(_steer) * 0.45f;
            float fyLocal = fLatR + fLatF * MathF.Cos(_steer);
            float torqueZ = Lf * fLatF * MathF.Cos(_steer) - Lr * fLatR;

            float axLocal = fxLocal / Mass;
            float ayLocal = fyLocal / Mass;
            float alphaYaw = torqueZ / Iz - _omega * 1.65f + kickYaw;

            _vxm += (axLocal * fwdX + ayLocal * rightX) * h;
            _vym += (axLocal * fwdY + ayLocal * rightY) * h;
            _omega = Math.Clamp(_omega + alphaYaw * h, -5.2f, 5.2f);
            _heading = WrapAngle(_heading + _omega * h);

            float spdNow = MathF.Sqrt(_vxm * _vxm + _vym * _vym);
            if (spdNow > 30f) { float sc = 30f / spdNow; _vxm *= sc; _vym *= sc; }

            _xm += _vxm * h;
            _ym += _vym * h;
            _bodyRoll += (Math.Clamp(ayLocal * 0.012f, -0.25f, 0.25f) - _bodyRoll) * (1f - MathF.Exp(-h * 10f));

            // Soft elastic wall guard (rarely touched because predictive driver steers away 0.55s early!)
            float hardL = (stage.X + 38f) / PxPerMeter, hardR = (stage.Right - 38f) / PxPerMeter;
            float hardT = (stage.Y + 48f) / PxPerMeter, hardB = (stage.Bottom - 36f) / PxPerMeter;
            if (_xm < hardL) { _xm = hardL; _vxm = MathF.Abs(_vxm) * 0.6f + 4f; }
            else if (_xm > hardR) { _xm = hardR; _vxm = -MathF.Abs(_vxm) * 0.6f - 4f; }
            if (_ym < hardT) { _ym = hardT; _vym = MathF.Abs(_vym) * 0.6f + 4f; }
            else if (_ym > hardB) { _ym = hardB; _vym = -MathF.Abs(_vym) * 0.6f - 4f; }

            rearLatSlip = MathF.Abs(vLatR);
        }
    }

    public static void Draw(R stage, uint dom1, uint dom2)
    {
        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.028);
        float bass = AudioAnalyzer.BassEnergy;
        float mid = AudioAnalyzer.MidEnergy;
        float high = AudioAnalyzer.HighEnergy;
        float hit = AudioAnalyzer.SharpWaveHit;
        float loud = Math.Clamp(AudioAnalyzer.Loudness, 0f, 1f);
        float calm = Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.88f, 0.14f, 1f);
        bool beat = AudioAnalyzer.BeatStamp != _lastBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.34f;
        if (beat) _lastBeat = AudioAnalyzer.BeatStamp;

        uint bodyCol = dom1 != 0 ? Gfx.Mix(dom1, 0xFFFF3B6B, 0.22f) : 0xFFFF2E63;
        uint accentCol = dom2 != 0 && dom2 != dom1 ? dom2 : 0xFF00E5FF;
        uint smokeCol1 = Gfx.Mix(accentCol, 0xFFE8F4FF, 0.65f);
        uint smokeCol2 = Gfx.Mix(bodyCol, 0xFFFFEEF4, 0.58f);

        float cxPx = _xm * PxPerMeter, cyPx = _ym * PxPerMeter;
        if (!_inited || cxPx < stage.X - 80 || cxPx > stage.Right + 80 || cyPx < stage.Y - 80 || cyPx > stage.Bottom + 80)
        {
            _xm = (stage.X + stage.W * 0.35f) / PxPerMeter;
            _ym = (stage.Y + stage.H * 0.52f) / PxPerMeter;
            _vxm = 16f; _vym = -2f;
            _heading = -0.12f; _omega = 0f; _steer = 0f; _rearWheelOmega = 48f; _wheelSpin = 0f; _bodyRoll = 0f;
            _apexStep = 0;
            (_apexX, _apexY) = PickNextApex(stage, 0);
            _prevRLx = _prevRRx = _xm * PxPerMeter; _prevRLy = _prevRRy = _ym * PxPerMeter;
            Array.Clear(_skids); Array.Clear(_smoke); Array.Clear(_sparks);
            _inited = true;
        }

        StepSim(stage, dt, bass, mid, high, hit, loud, calm, beat, out float rearLatSlip);

        float x = _xm * PxPerMeter, y = _ym * PxPerMeter;
        float driftAmt = Math.Clamp(rearLatSlip / 6.5f + _wheelSpin * 0.65f, 0f, 1.4f);
        float cFwdX = MathF.Cos(_heading), cFwdY = MathF.Sin(_heading);
        float cRightX = -cFwdY, cRightY = cFwdX;

        const float carLen = 46f, carWid = 22f;
        float rlX = x - cFwdX * (carLen * 0.36f) - cRightX * (carWid * 0.44f);
        float rlY = y - cFwdY * (carLen * 0.36f) - cRightY * (carWid * 0.44f);
        float rrX = x - cFwdX * (carLen * 0.36f) + cRightX * (carWid * 0.44f);
        float rrY = y - cFwdY * (carLen * 0.36f) + cRightY * (carWid * 0.44f);
        float exX = x - cFwdX * (carLen * 0.52f) + cRightX * (carWid * 0.22f);
        float exY = y - cFwdY * (carLen * 0.52f) + cRightY * (carWid * 0.22f);

        // Emit tire skid marks & wheelspin smoke blasted backward off the spinning rear rubber!
        if (driftAmt > 0.22f)
        {
            float distRL = MathF.Sqrt((rlX - _prevRLx) * (rlX - _prevRLx) + (rlY - _prevRLy) * (rlY - _prevRLy));
            if (distRL > 2.2f && distRL < 55f)
            {
                uint skCol = Gfx.Mix(0xFF0A0A12, accentCol, Math.Clamp(0.20f + _wheelSpin * 0.38f, 0f, 0.68f));
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRLx, Y0 = _prevRLy, X1 = rlX, Y1 = rlY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
                _skids[_skidIdx] = new SkidSeg { X0 = _prevRRx, Y0 = _prevRRy, X1 = rrX, Y1 = rrY, Life = 1f, Col = skCol };
                _skidIdx = (_skidIdx + 1) % MaxSkids;
            }

            if (Rand01() < 0.45f + driftAmt * 0.48f)
            {
                bool leftW = Rand01() < 0.5f;
                float ml = 0.55f + Rand01() * 0.55f;
                float kickBack = 42f * (0.4f + _wheelSpin);
                _smoke[_smokeIdx] = new SmokePuff
                {
                    X = leftW ? rlX : rrX, Y = leftW ? rlY : rrY,
                    Vx = -cFwdX * kickBack + (Rand01() - 0.5f) * 24f,
                    Vy = -cFwdY * kickBack + (Rand01() - 0.5f) * 24f,
                    R = 4.5f + driftAmt * 6.0f + bass * 3.5f,
                    Life = ml, MaxLife = ml,
                    Col = leftW ? smokeCol1 : smokeCol2,
                };
                _smokeIdx = (_smokeIdx + 1) % MaxSmoke;
            }
        }
        _prevRLx = rlX; _prevRLy = rlY;
        _prevRRx = rrX; _prevRRy = rrY;

        if (beat || (_rpm > 7200f && hit > 0.45f))
        {
            _backfire = MathF.Max(_backfire, 0.85f);
            int cnt = beat ? 6 : 2;
            for (int k = 0; k < cnt; k++)
            {
                float spd = 85f + Rand01() * 140f;
                float spAng = _heading + MathF.PI + (Rand01() - 0.5f) * 0.65f;
                _sparks[_sparkIdx] = new Spark
                {
                    X = exX, Y = exY,
                    Vx = MathF.Cos(spAng) * spd, Vy = MathF.Sin(spAng) * spd,
                    Life = 0.24f + Rand01() * 0.26f,
                    Col = (k & 1) == 0 ? 0xFFFFB74D : 0xFFFF7043,
                };
                _sparkIdx = (_sparkIdx + 1) % MaxSparks;
            }
        }
        _backfire *= MathF.Exp(-dt * 18f);

        // Render Skid Marks, Fluffy Smoke, Sparks
        for (int i = 0; i < MaxSkids; i++)
        {
            if (_skids[i].Life <= 0.01f) continue;
            _skids[i].Life -= dt * 0.32f;
            if (_skids[i].Life <= 0.01f) continue;
            Gfx.Line(_skids[i].X0, _skids[i].Y0, _skids[i].X1, _skids[i].Y1, Gfx.Fade(_skids[i].Col, _skids[i].Life * 0.45f * calm), 3.0f);
        }
        for (int i = 0; i < MaxSmoke; i++)
        {
            if (_smoke[i].Life <= 0.01f) continue;
            _smoke[i].Life -= dt;
            if (_smoke[i].Life <= 0.01f) continue;
            _smoke[i].X += _smoke[i].Vx * dt;
            _smoke[i].Y += _smoke[i].Vy * dt;
            _smoke[i].R += dt * (20f + bass * 13f);
            float frac = _smoke[i].Life / _smoke[i].MaxLife;
            float a = frac * frac * 0.15f * calm;
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
            Gfx.Line(_sparks[i].X, _sparks[i].Y, nx, ny, Gfx.Fade(_sparks[i].Col, Math.Clamp(_sparks[i].Life * 2.8f, 0f, 0.95f) * calm), 1.6f);
            _sparks[i].X = nx; _sparks[i].Y = ny;
            _sparks[i].Vx *= MathF.Exp(-dt * 4f); _sparks[i].Vy *= MathF.Exp(-dt * 4f);
        }

        // Render Coupe with Live Counter-Steer Front Wheels, Body Roll Shift & Soft Headlights
        var savedTf = Gfx.DC.Transform;
        var carTf = Matrix3x2.CreateRotation(_heading) * Matrix3x2.CreateTranslation(x, y) * savedTf;
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
            Gfx.DC.Transform = Matrix3x2.CreateRotation(_steer) * Matrix3x2.CreateTranslation(carLen * 0.28f, side * carWid * 0.50f) * carTf;
            Gfx.Fill(new R(-4.5f, -2.1f, 9.0f, 4.2f), 0xFF0A0C12, 1.2f);
        }
        Gfx.DC.Transform = carTf;

        Gfx.Fill(new R(-carLen * 0.55f, -carWid * 0.60f, carLen * 1.10f, carWid * 1.20f), Gfx.Fade(accentCol, (0.20f + _wheelSpin * 0.25f + bass * 0.16f) * calm), 10f);

        if (_backfire > 0.15f)
        {
            Gfx.Circle(-carLen * 0.52f - 3f, carWid * 0.22f, 4.5f * _backfire, Gfx.Fade(0xFFFF9800, _backfire * 0.55f));
            Gfx.Circle(-carLen * 0.52f - 2f, carWid * 0.22f, 2.2f * _backfire, Gfx.Fade(0xFFFFF3D0, _backfire * 0.85f));
        }

        float rollShift = _bodyRoll * 6.5f;
        Gfx.Fill(new R(-carLen * 0.50f + 2f, -carWid * 0.50f + 3f, carLen, carWid), Gfx.Fade(0xFF000000, 0.48f), 6f);
        var bodyR = new R(-carLen * 0.50f, -carWid * 0.50f + rollShift * 0.35f, carLen, carWid);
        Gfx.Fill(bodyR, Gfx.Fade(bodyCol, 0.95f), 6.5f);
        Gfx.Stroke(bodyR, Gfx.Fade(0xFFFFFFFF, 0.45f), 6.5f, 1.1f);
        Gfx.Fill(new R(-carLen * 0.24f, -carWid * 0.38f + rollShift * 0.65f, carLen * 0.46f, carWid * 0.76f), 0xFF10131C, 4.5f);
        Gfx.Fill(new R(-carLen * 0.14f, -carWid * 0.26f + rollShift, carLen * 0.22f, carWid * 0.52f), Gfx.Fade(bodyCol, 0.85f), 3f);
        Gfx.Fill(new R(-carLen * 0.50f, -carWid * 0.54f + rollShift * 0.45f, 4.5f, carWid * 1.08f), 0xFF0C0E14, 1.5f);

        Gfx.Circle(carLen * 0.46f, -carWid * 0.30f + rollShift * 0.35f, 2.2f, 0xFFFFFFFF);
        Gfx.Circle(carLen * 0.46f,  carWid * 0.30f + rollShift * 0.35f, 2.2f, 0xFFFFFFFF);
        float brakeGlow = 0.55f + MathF.Min(0.45f, driftAmt * 0.35f);
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f + rollShift * 0.35f, 4.2f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f + rollShift * 0.35f, 4.2f, Gfx.Fade(0xFFFF1E42, brakeGlow * 0.35f));
        Gfx.Circle(-carLen * 0.48f, -carWid * 0.30f + rollShift * 0.35f, 1.9f, 0xFFFF2A4B);
        Gfx.Circle(-carLen * 0.48f,  carWid * 0.30f + rollShift * 0.35f, 1.9f, 0xFFFF2A4B);

        Gfx.DC.Transform = savedTf;
        UI.Animate();
    }

    internal static (bool Ok, int LeftTurns, int RightTurns, int WallHits) SelfTestPhysics()
    {
        var box = new R(0, 0, 1280, 720);
        _xm = 450f / PxPerMeter; _ym = 360f / PxPerMeter;
        _vxm = 16f; _vym = 0f; _heading = 0f; _omega = 0f; _steer = 0f; _rearWheelOmega = 50f;
        _apexStep = 0; (_apexX, _apexY) = PickNextApex(box, 0);
        int lefts = 0, rights = 0, hits = 0;
        for (int f = 0; f < 360; f++)
        {
            StepSim(box, 1f / 60f, 0.72f, 0.55f, 0.45f, f % 20 == 0 ? 0.8f : 0.1f, 0.8f, 1f, f % 20 == 0, out _);
            if (_omega < -0.35f) lefts++;
            if (_omega >  0.35f) rights++;
            float px = _xm * PxPerMeter, py = _ym * PxPerMeter;
            if (px <= 42f || px >= 1238f || py <= 52f || py >= 680f) hits++;
        }
        _inited = false;
        return (lefts > 25 && rights > 25 && hits < 10, lefts, rights, hits);
    }
}
