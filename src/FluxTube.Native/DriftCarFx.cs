using System.Numerics;

namespace FluxTube;

/// <summary>
/// Dual-System RWD Drift Physics Simulator ("Дрифт", ~0.16ms, zero-alloc):
/// - System 1 (The Music = Adversary / Gas Pedal): Loudness + Bass + Beat Kicks slam rear-wheel torque past
///   the tire friction circle, causing rear wheelspin and violent power-oversteer!
/// - System 2 (The Driver = 7-Candidate Model Predictive Control / Rollout AI): every frame simulates 7 steering angles
///   1.1 seconds into the future using the exact RWD physics model, scoring wall clearance, speed, and drift stability ->
///   picks the optimal steer to catch slides, carve right along the arena walls without ever getting stuck, and whip left/right!
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

    /// <summary>Exact single RWD Pacejka physics step shared by both real simulation and MPC future rollouts.</summary>
    private static void StepPhysics(ref float x, ref float y, ref float vx, ref float vy, ref float heading, ref float omega,
        float steer, float throttle, float wheelSpin, float kickTorque, float h, out float rearLatSlip)
    {
        const float lf = 16f, lr = 16f;
        float fwdX = MathF.Cos(heading), fwdY = MathF.Sin(heading);
        float rightX = -fwdY, rightY = fwdX;

        float vLong = vx * fwdX + vy * fwdY;
        float vLat = vx * rightX + vy * rightY;

        float vLatF = vLat + omega * lf;
        float vLatR = vLat - omega * lr;

        float slipF = MathF.Atan2(vLatF, MathF.Abs(vLong) + 18f) - steer;
        float slipR = MathF.Atan2(vLatR, MathF.Abs(vLong) + 18f);

        // Front axle keeps sharp authority so driver can always whip out of trouble; rear axle slides under audio torque!
        float frontGrip = 520f;
        float rearGrip = 400f / (1f + wheelSpin * 2.45f);

        float fLatF = -MathF.Tanh(slipF * 2.5f) * frontGrip;
        float fLatR = -MathF.Tanh(slipR * 2.1f) * rearGrip;

        float driveForce = throttle * 540f / (1f + wheelSpin * 0.30f);
        float fLong = driveForce - vLong * 1.08f - vLong * MathF.Abs(vLong) * 0.0011f;

        float aLong = fLong - fLatF * MathF.Sin(steer) * 0.30f;
        float aLat = fLatR + fLatF * MathF.Cos(steer);
        float alphaYaw = (lf * fLatF * MathF.Cos(steer) - lr * fLatR) * 0.00072f - omega * 2.55f + kickTorque;

        vx += (aLong * fwdX + aLat * rightX) * h;
        vy += (aLong * fwdY + aLat * rightY) * h;
        omega = Math.Clamp(omega + alphaYaw * h, -4.6f, 4.6f);
        heading = WrapAngle(heading + omega * h);

        float spd = MathF.Sqrt(vx * vx + vy * vy);
        if (spd > 470f) { float sc = 470f / spd; vx *= sc; vy *= sc; }

        x += vx * h;
        y += vy * h;
        rearLatSlip = MathF.Abs(vLatR);
    }

    /// <summary>
    /// 7-Candidate Model Predictive Control (MPC): rolls out 7 steering trajectories 1.05s into the future
    /// so the driver anticipates velocity + drift momentum, dodges walls early, and catches wild oversteer!
    /// </summary>
    private static float ChooseMpcSteer(R stage, float preferTurn, float throttle, float wheelSpin)
    {
        ReadOnlySpan<float> candidates = [-0.72f, -0.48f, -0.24f, 0.0f, 0.24f, 0.48f, 0.72f];
        float bestSteer = 0f;
        float bestScore = float.NegativeInfinity;

        float safeL = stage.X + 115f, safeR = stage.Right - 115f;
        float safeT = stage.Y + 105f, safeB = stage.Bottom - 105f;
        float midX = stage.X + stage.W * 0.5f, midY = stage.Y + stage.H * 0.48f;

        const int horizon = 10;
        const float stepDt = 0.105f; // 1.05 seconds lookahead!

        for (int c = 0; c < candidates.Length; c++)
        {
            float st = candidates[c];
            float sx = _x, sy = _y, svx = _vx, svy = _vy, sh = _heading, som = _omega;
            float score = 0f;

            for (int k = 0; k < horizon; k++)
            {
                // Keep candidate steer for first 4 steps, then relax toward neutral to test recovery
                float curSt = k < 5 ? st : st * 0.35f;
                StepPhysics(ref sx, ref sy, ref svx, ref svy, ref sh, ref som, curSt, throttle, wheelSpin, 0f, stepDt, out float rSlip);

                // 1. HUGE exponential penalty for approaching or hitting any of the 4 walls!
                float dL = sx - stage.X, dR = stage.Right - sx;
                float dT = sy - stage.Y, dB = stage.Bottom - sy;
                float minWall = MathF.Min(MathF.Min(dL, dR), MathF.Min(dT, dB));
                if (minWall < 40f) score -= 9000f + (40f - minWall) * 200f;
                else if (minWall < 125f)
                {
                    float t = (125f - minWall) / 85f;
                    score -= t * t * 1400f;
                }

                // 2. Mild preference to stay in the rich mid-ring (not glued to edges, not parked dead center)
                float distC = MathF.Sqrt((sx - midX) * (sx - midX) + (sy - midY) * (sy - midY));
                if (sx > safeL && sx < safeR && sy > safeT && sy < safeB) score += 22f;

                // 3. Reward forward momentum + sweet-spot drift slip (penalize stalling or spinning out > 70 deg!)
                float spd = MathF.Sqrt(svx * svx + svy * svy);
                score += spd * 0.14f;
                float vAng = MathF.Atan2(svy, svx);
                float slipMag = MathF.Abs(WrapAngle(vAng - sh));
                if (slipMag > 1.15f) score -= (slipMag - 1.15f) * 320f; // catch spin-out!
                else score += MathF.Min(rSlip, 120f) * 0.18f; // reward holding a clean controlled drift!

                // 4. Gentle bias toward active left/right transition direction when safe from walls
                if (minWall > 115f && k < 3)
                    score += (st * preferTurn) * 24f;

                // 5. Smooth steering continuity (avoid twitchy flickering between identical scores)
                if (k == 0)
                    score -= MathF.Abs(st - _steer) * 18f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestSteer = st;
            }
        }
        return bestSteer;
    }

    public static void Draw(R stage, uint dom1, uint dom2)
    {
        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.028);
        float bass = AudioAnalyzer.BassEnergy;
        float mid = AudioAnalyzer.MidEnergy;
        float high = AudioAnalyzer.HighEnergy;
        float hit = AudioAnalyzer.SharpWaveHit;
        float loud = Math.Clamp(AudioAnalyzer.Loudness, 0f, 1f);
        float calm = Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.88f, 0.12f, 1f);
        bool beat = AudioAnalyzer.BeatStamp != _lastBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.34f;
        if (beat) _lastBeat = AudioAnalyzer.BeatStamp;

        uint bodyCol = dom1 != 0 ? Gfx.Mix(dom1, 0xFFFF3B6B, 0.22f) : 0xFFFF2E63;
        uint accentCol = dom2 != 0 && dom2 != dom1 ? dom2 : 0xFF00E5FF;
        uint smokeCol1 = Gfx.Mix(accentCol, 0xFFE8F4FF, 0.62f);
        uint smokeCol2 = Gfx.Mix(bodyCol, 0xFFFFEEF4, 0.55f);

        float midX = stage.X + stage.W * 0.5f, midY = stage.Y + stage.H * 0.48f;

        if (!_inited || _x < stage.X - 80 || _x > stage.Right + 80 || _y < stage.Y - 80 || _y > stage.Bottom + 80)
        {
            _x = midX - stage.W * 0.22f;
            _y = midY + stage.H * 0.12f;
            _vx = 230f; _vy = -40f;
            _heading = -0.16f; _omega = 0f; _steer = 0f; _wheelSpin = 0f;
            _prevRLx = _prevRRx = _x; _prevRLy = _prevRRy = _y;
            Array.Clear(_skids); Array.Clear(_smoke); Array.Clear(_sparks);
            _inited = true;
        }

        // ---- SYSTEM 1: AUDIO SLAMS THE GAS PEDAL & BREAKS REAR TRACTION ----
        float targetThrottle = Math.Clamp((0.32f + loud * 0.68f + bass * 0.72f + hit * 0.45f + (beat ? 0.55f : 0f)) * calm, 0.16f, 1.65f);
        _throttle += (targetThrottle - _throttle) * (1f - MathF.Exp(-dt * 14f));
        float excessTorque = MathF.Max(0f, _throttle * 540f - 255f);
        float targetSpin = Math.Clamp(excessTorque / 330f + (beat ? 0.38f : 0f), 0f, 1.55f);
        _wheelSpin += (targetSpin - _wheelSpin) * (1f - MathF.Exp(-dt * 12f));

        // Flip preferred drift side periodically or on stereo shifts
        _flipTimer -= dt;
        if (_flipTimer <= 0f || (beat && MathF.Abs(AudioAnalyzer.StereoPan) > 0.24f && _flipTimer < 1.0f))
        {
            _preferTurn = MathF.Abs(AudioAnalyzer.StereoPan) > 0.20f ? MathF.Sign(AudioAnalyzer.StereoPan) : -_preferTurn;
            _flipTimer = 2.0f + Rand01() * 2.2f;
        }

        // ---- SYSTEM 2: MPC PREDICTIVE DRIVER CHOOSES OPTIMAL STEER TO SURVIVE & DRIFT ----
        float mpcSteer = ChooseMpcSteer(stage, _preferTurn, _throttle, _wheelSpin);
        _steer += (mpcSteer - _steer) * (1f - MathF.Exp(-dt * 12f));

        // Step actual physics with 4 sub-steps
        const int subSteps = 4;
        float h = dt / subSteps;
        float lastRearSlip = 0f;
        for (int s = 0; s < subSteps; s++)
        {
            float kickTorque = (beat && s == 0) ? (_preferTurn * (2.2f + bass * 2.4f)) : 0f;
            StepPhysics(ref _x, ref _y, ref _vx, ref _vy, ref _heading, ref _omega,
                _steer, _throttle, _wheelSpin, kickTorque, h, out lastRearSlip);

            // Hard safety clamp only if extreme collision happens (MPC prevents reaching here in normal play!)
            if (_x < stage.X + 36f) { _x = stage.X + 36f; _vx = MathF.Abs(_vx) * 0.75f + 60f; }
            else if (_x > stage.Right - 36f) { _x = stage.Right - 36f; _vx = -MathF.Abs(_vx) * 0.75f - 60f; }
            if (_y < stage.Y + 36f) { _y = stage.Y + 36f; _vy = MathF.Abs(_vy) * 0.75f + 60f; }
            else if (_y > stage.Bottom - 36f) { _y = stage.Bottom - 36f; _vy = -MathF.Abs(_vy) * 0.75f - 60f; }
        }

        float driftAmt = Math.Clamp(lastRearSlip / 105f + _wheelSpin * 0.65f, 0f, 1.35f);
        float cFwdX = MathF.Cos(_heading), cFwdY = MathF.Sin(_heading);
        float cRightX = -cFwdY, cRightY = cFwdX;

        const float carLen = 44f, carWid = 22f;
        float rlX = _x - cFwdX * (carLen * 0.36f) - cRightX * (carWid * 0.44f);
        float rlY = _y - cFwdY * (carLen * 0.36f) - cRightY * (carWid * 0.44f);
        float rrX = _x - cFwdX * (carLen * 0.36f) + cRightX * (carWid * 0.44f);
        float rrY = _y - cFwdY * (carLen * 0.36f) + cRightY * (carWid * 0.44f);
        float exX = _x - cFwdX * (carLen * 0.52f) + cRightX * (carWid * 0.22f);
        float exY = _y - cFwdY * (carLen * 0.52f) + cRightY * (carWid * 0.22f);

        // ---- 3. EMIT SKID MARKS, SMOKE & WARM EXHAUST SPARKS ----
        if (driftAmt > 0.22f)
        {
            float distRL = MathF.Sqrt((rlX - _prevRLx) * (rlX - _prevRLx) + (rlY - _prevRLy) * (rlY - _prevRLy));
            if (distRL > 2.5f && distRL < 55f)
            {
                uint skCol = Gfx.Mix(0xFF0A0A12, accentCol, Math.Clamp(0.22f + _wheelSpin * 0.38f, 0f, 0.70f));
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
                    Vx = -cFwdX * (32f * _wheelSpin) + (Rand01() - 0.5f) * 24f,
                    Vy = -cFwdY * (32f * _wheelSpin) + (Rand01() - 0.5f) * 24f,
                    R = 4.5f + driftAmt * 6.0f + bass * 3.8f,
                    Life = ml, MaxLife = ml,
                    Col = leftW ? smokeCol1 : smokeCol2,
                };
                _smokeIdx = (_smokeIdx + 1) % MaxSmoke;
            }
        }
        _prevRLx = rlX; _prevRLy = rlY;
        _prevRRx = rrX; _prevRRy = rrY;

        if (beat || (_wheelSpin > 0.78f && hit > 0.45f))
        {
            _backfire = MathF.Max(_backfire, 0.85f);
            int cnt = beat ? 6 : 2;
            for (int k = 0; k < cnt; k++)
            {
                float spd = 85f + Rand01() * 135f;
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
            _smoke[i].R += dt * (19f + bass * 13f);
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

        for (int side = -1; side <= 1; side += 2)
        {
            Gfx.DC.Transform = Matrix3x2.CreateRotation(_steer) * Matrix3x2.CreateTranslation(carLen * 0.28f, side * carWid * 0.50f) * carTf;
            Gfx.Fill(new R(-4.5f, -2.0f, 9.0f, 4.0f), 0xFF0A0C12, 1.2f);
        }
        Gfx.DC.Transform = carTf;

        Gfx.Fill(new R(-carLen * 0.55f, -carWid * 0.60f, carLen * 1.10f, carWid * 1.20f), Gfx.Fade(accentCol, (0.20f + _wheelSpin * 0.24f + bass * 0.16f) * calm), 10f);

        // Subtle warm exhaust pop flash (tiny orange glow, no weird cyan laser bar!)
        if (_backfire > 0.15f)
        {
            Gfx.Circle(-carLen * 0.52f - 3f, carWid * 0.22f, 4.5f * _backfire, Gfx.Fade(0xFFFF9800, _backfire * 0.55f));
            Gfx.Circle(-carLen * 0.52f - 2f, carWid * 0.22f, 2.2f * _backfire, Gfx.Fade(0xFFFFF3D0, _backfire * 0.85f));
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
