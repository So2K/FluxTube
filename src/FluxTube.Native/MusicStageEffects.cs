using System.Numerics;
using FluxTube.Services;
using Vortice;
using Vortice.Direct2D1;

namespace FluxTube;

/// <summary>Bounded, audio-driven drop effects, quiet fireflies, and ultra-lightweight GPU cellular fire texture.</summary>
public static unsafe class MusicStageEffects
{
    private static float _average, _intensity, _flash, _fireIntensity;
    private static long _lastFrame = -1, _updatedAt, _lastHit, _lastBurstAt;
    private static long _fireStartedAt, _fireCooldownUntil, _lastDropSeen, _lastQuietExitSeen;
    private static readonly long[] _bursts = new long[1];
    private static readonly float[] _strength = new float[1];
    public static float Intensity => UI.Now - _updatedAt < 150 ? _intensity : 0;

    // ---- Ultra-Crisp 192x96 Unsafe-Pointer 2D Stable Fluids + Vorticity Confinement Engine (~0.3ms CPU, 2 GPU DrawBitmaps!) ----
    private const int FireW = 192, FireH = 96, FireN = FireW * FireH;
    private static readonly float[] _u = new float[FireN], _u0 = new float[FireN];
    private static readonly float[] _v = new float[FireN], _v0 = new float[FireN];
    private static readonly float[] _d = new float[FireN], _d0 = new float[FireN];
    private static readonly float[] _div = new float[FireN], _p = new float[FireN], _curl = new float[FireN];
    private static readonly uint[] _firePixels = new uint[FireN];
    private static readonly uint[] _firePal = new uint[256];
    private static uint _palCol1, _palCol2;
    private static ID2D1Bitmap1? _fireBmp;
    private static int _fireBmpGen = -1;
    private static long _lastFireSimMs;
    private static uint _rng = 0x9E3779B9u;

    // ---- Pavel Dobryakov-Style Ultra-Crisp 320x180 MacCormack 3-Channel RGB Curl-Vorticity Silk Fluid (~0.7ms CPU via 4-stripe Parallel.For!) ----
    private const int FlW = 320, FlH = 180, FlN = FlW * FlH;
    private static readonly float[] _fu = new float[FlN], _fu0 = new float[FlN];
    private static readonly float[] _fv = new float[FlN], _fv0 = new float[FlN];
    private static readonly float[] _fr = new float[FlN], _fr0 = new float[FlN], _fr1 = new float[FlN];
    private static readonly float[] _fg = new float[FlN], _fg0 = new float[FlN], _fg1 = new float[FlN];
    private static readonly float[] _fb = new float[FlN], _fb0 = new float[FlN], _fb1 = new float[FlN];
    private static readonly float[] _fdiv = new float[FlN], _fp = new float[FlN], _fcurl = new float[FlN];
    private static readonly uint[] _fluidPixels = new uint[FlN];
    private static ID2D1Bitmap1? _fluidBmp;
    private static int _fluidBmpGen = -1;
    private static long _lastFluidSimMs, _lastFluidBeat;
    private static float _fluidBurstPower;
    private static int _fluidSplatSeed;

    private static void ClearRgbFluid()
    {
        Array.Clear(_fu); Array.Clear(_fv); Array.Clear(_fu0); Array.Clear(_fv0);
        Array.Clear(_fr); Array.Clear(_fg); Array.Clear(_fb);
        Array.Clear(_fr0); Array.Clear(_fg0); Array.Clear(_fb0);
        Array.Clear(_fr1); Array.Clear(_fg1); Array.Clear(_fb1);
        Array.Clear(_fp);
    }

    /// <summary>Rodrigues RGB hue rotation around the (1,1,1) gray axis (pure branchless trig, preserves vividness).</summary>
    private static (float r, float g, float b) RotateHue(float r, float g, float b, float rad)
    {
        float cosA = MathF.Cos(rad), sinA = MathF.Sin(rad);
        const float k = 1f / 3f;
        float sq = MathF.Sqrt(k);
        float m00 = cosA + (1f - cosA) * k;
        float m01 = k * (1f - cosA) - sq * sinA;
        float m02 = k * (1f - cosA) + sq * sinA;
        float rr = Math.Clamp(r * m00 + g * m01 + b * m02, 0.02f, 1f);
        float gg = Math.Clamp(r * m02 + g * m00 + b * m01, 0.02f, 1f);
        float bb = Math.Clamp(r * m01 + g * m02 + b * m00, 0.02f, 1f);
        return (rr, gg, bb);
    }

    private static (float r, float g, float b) HyperVivid(uint argb, uint fallback)
    {
        var (r, g, b) = VividColor(argb);
        float mx = MathF.Max(r, MathF.Max(g, b));
        float mn = MathF.Min(r, MathF.Min(g, b));
        // If cover is monochrome/B&W (like checkerboard), pull vivid neon energy from theme Brand instead of gray!
        if (mx - mn < 0.22f)
        {
            var (fr, fg, fb) = VividColor(fallback);
            r = r * 0.18f + fr * 0.82f;
            g = g * 0.18f + fg * 0.82f;
            b = b * 0.18f + fb * 0.82f;
        }
        float avg = (r + g + b) * (1f / 3f);
        r = Math.Clamp(avg + (r - avg) * 1.45f, 0.02f, 1f);
        g = Math.Clamp(avg + (g - avg) * 1.45f, 0.02f, 1f);
        b = Math.Clamp(avg + (b - avg) * 1.45f, 0.02f, 1f);
        // Normalize peak channel toward 1.0 so ribbons always glow brightly
        float peak = MathF.Max(0.25f, MathF.Max(r, MathF.Max(g, b)));
        float lift = MathF.Min(1.35f, 0.98f / peak);
        return (Math.Clamp(r * lift, 0.02f, 1f), Math.Clamp(g * lift, 0.02f, 1f), Math.Clamp(b * lift, 0.02f, 1f));
    }

    private static void SplatFluid(float* u, float* v, float* r, float* g, float* b,
        float cx, float cy, float vx, float vy, float cr, float cg, float cb, float radPx)
    {
        int x0 = Math.Clamp((int)(cx - radPx * 2.1f), 1, FlW - 2);
        int x1 = Math.Clamp((int)(cx + radPx * 2.1f), 1, FlW - 2);
        int y0 = Math.Clamp((int)(cy - radPx * 2.1f), 1, FlH - 2);
        int y1 = Math.Clamp((int)(cy + radPx * 2.1f), 1, FlH - 2);
        float inv2R2 = 1f / MathF.Max(1f, 2f * radPx * radPx);
        for (int y = y0; y <= y1; y++)
        {
            float dy = y - cy;
            float dy2 = dy * dy;
            int row = y * FlW;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx;
                float w = MathF.Exp(-(dx * dx + dy2) * inv2R2);
                int i = row + x;
                u[i] += vx * w;
                v[i] += vy * w;
                r[i] = MathF.Min(3.2f, r[i] + cr * w);
                g[i] = MathF.Min(3.2f, g[i] + cg * w);
                b[i] = MathF.Min(3.2f, b[i] + cb * w);
            }
        }
    }

    private static void AdvectFluidBand(float* dst, float* src, float* u, float* v, float dt, float dissipation, int yStart, int yEnd)
    {
        const float maxX = FlW - 1.5f, maxY = FlH - 1.5f;
        for (int y = yStart; y < yEnd; y++)
        {
            int row = y * FlW;
            for (int x = 1; x < FlW - 1; x++)
            {
                int i = row + x;
                float px = Math.Clamp(x - dt * u[i], 0.5f, maxX);
                float py = Math.Clamp(y - dt * v[i], 0.5f, maxY);
                int x0 = (int)px, y0 = (int)py;
                float tx = px - x0, ty = py - y0;
                int r0 = y0 * FlW + x0;
                int r1 = r0 + FlW;
                float top = src[r0] + (src[r0 + 1] - src[r0]) * tx;
                float bot = src[r1] + (src[r1 + 1] - src[r1]) * tx;
                dst[i] = (top + (bot - top) * ty) * dissipation;
            }
        }
    }

    /// <summary>2nd-order MacCormack advection with min/max limiter: keeps thin curling silk filaments RAZOR-SHARP (zero blurry cloud diffusion!).</summary>
    private static void AdvectDyeMacCormack(float* src, float* phiHat, float* dst, float* u, float* v, float dt, float dissipation)
    {
        // Pass 1: forward semi-Lagrangian into phiHat
        Parallel.For(0, 4, stripe =>
        {
            int y0 = 1 + stripe * (FlH - 2) / 4;
            int y1 = 1 + (stripe + 1) * (FlH - 2) / 4;
            AdvectFluidBand(phiHat, src, u, v, dt, 1f, y0, y1);
        });
        // Pass 2: backward error estimate + clamped 2nd-order correction into dst
        Parallel.For(0, 4, stripe =>
        {
            const float maxX = FlW - 1.5f, maxY = FlH - 1.5f;
            int y0 = 1 + stripe * (FlH - 2) / 4;
            int y1 = 1 + (stripe + 1) * (FlH - 2) / 4;
            for (int y = y0; y < y1; y++)
            {
                int row = y * FlW;
                for (int x = 1; x < FlW - 1; x++)
                {
                    int i = row + x;
                    // Sample phiHat forward (+dt) to estimate advection error
                    float fx = Math.Clamp(x + dt * u[i], 0.5f, maxX);
                    float fy = Math.Clamp(y + dt * v[i], 0.5f, maxY);
                    int fx0 = (int)fx, fy0 = (int)fy;
                    float ftx = fx - fx0, fty = fy - fy0;
                    int fr0 = fy0 * FlW + fx0, fr1 = fr0 + FlW;
                    float phiRev = (phiHat[fr0] + (phiHat[fr0 + 1] - phiHat[fr0]) * ftx) * (1f - fty)
                                 + (phiHat[fr1] + (phiHat[fr1 + 1] - phiHat[fr1]) * ftx) * fty;

                    // Min/max clamp from the 4 backward source neighbors guarantees 100% stability (zero overshoot/ringing!)
                    float bx = Math.Clamp(x - dt * u[i], 0.5f, maxX);
                    float by = Math.Clamp(y - dt * v[i], 0.5f, maxY);
                    int bx0 = (int)bx, by0 = (int)by;
                    int br0 = by0 * FlW + bx0, br1 = br0 + FlW;
                    float s00 = src[br0], s10 = src[br0 + 1], s01 = src[br1], s11 = src[br1 + 1];
                    float lo = MathF.Min(MathF.Min(s00, s10), MathF.Min(s01, s11));
                    float hi = MathF.Max(MathF.Max(s00, s10), MathF.Max(s01, s11));

                    float corr = phiHat[i] + 0.5f * (src[i] - phiRev);
                    dst[i] = Math.Clamp(corr, lo, hi) * dissipation;
                }
            }
        });
    }

    private static void ProjectFluid(float* u, float* v, float* div, float* p)
    {
        Parallel.For(0, 4, stripe =>
        {
            int y0 = 1 + stripe * (FlH - 2) / 4;
            int y1 = 1 + (stripe + 1) * (FlH - 2) / 4;
            for (int y = y0; y < y1; y++)
            {
                int row = y * FlW;
                for (int x = 1; x < FlW - 1; x++)
                {
                    int i = row + x;
                    div[i] = -0.5f * (u[i + 1] - u[i - 1] + v[i + FlW] - v[i - FlW]);
                    p[i] *= 0.68f;
                }
            }
        });
        for (int k = 0; k < 4; k++)
        {
            Parallel.For(0, 4, stripe =>
            {
                int y0 = 1 + stripe * (FlH - 2) / 4;
                int y1 = 1 + (stripe + 1) * (FlH - 2) / 4;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * FlW;
                    for (int x = 1; x < FlW - 1; x++)
                    {
                        int i = row + x;
                        p[i] = (div[i] + p[i - 1] + p[i + 1] + p[i - FlW] + p[i + FlW]) * 0.25f;
                    }
                }
            });
        }
        Parallel.For(0, 4, stripe =>
        {
            int y0 = 1 + stripe * (FlH - 2) / 4;
            int y1 = 1 + (stripe + 1) * (FlH - 2) / 4;
            for (int y = y0; y < y1; y++)
            {
                int row = y * FlW;
                for (int x = 1; x < FlW - 1; x++)
                {
                    int i = row + x;
                    u[i] -= 0.5f * (p[i + 1] - p[i - 1]);
                    v[i] -= 0.5f * (p[i + FlW] - p[i - FlW]);
                }
            }
        });
    }

    private static void StepRgbFluidSimulation(float t, uint dom1, uint dom2, float bass, float mid, float high,
        float hit, float pan, float quietDim, bool beatHit, float burstPower, bool uploadGpu)
    {
        uint brand = Th.Brand != 0 ? Th.Brand : 0xFFFF2E93;
        uint c1 = dom1 != 0 ? dom1 : brand;
        uint c2 = dom2 != 0 && dom2 != c1 ? dom2 : Gfx.Mix(c1, brand, 0.5f);
        var (r0, g0, b0) = HyperVivid(c1, brand);
        var (r1, g1, b1) = HyperVivid(c2, Gfx.Mix(brand, 0xFF00E5FF, 0.55f));
        // Guarantee rich 3-color harmonic silk separation (magenta/cyan/gold or cover-matched triad!)
        float colorDist = MathF.Abs(r0 - r1) + MathF.Abs(g0 - g1) + MathF.Abs(b0 - b1);
        if (colorDist < 0.45f) (r1, g1, b1) = RotateHue(r0, g0, b0, 1.85f);
        var (r2, g2, b2) = RotateHue(r0, g0, b0, -1.95f);

        float kick = MusicBounce.Kick;
        float domAng = AudioAnalyzer.DominantAngle;
        float energy = Math.Clamp(0.35f + bass * 1.15f + mid * 0.55f + high * 0.45f + hit * 0.85f + kick * 0.55f, 0.20f, 2.4f);
        float calm = Math.Clamp(1f - quietDim * 0.90f, 0.08f, 1f);

        fixed (float* fu = _fu, fu0 = _fu0, fv = _fv, fv0 = _fv0,
               fr = _fr, fr0 = _fr0, fr1 = _fr1,
               fg = _fg, fg0 = _fg0, fg1 = _fg1,
               fb = _fb, fb0 = _fb0, fb1 = _fb1,
               fdiv = _fdiv, fp = _fp, fcurl = _fcurl)
        fixed (uint* fpix = _fluidPixels)
        {
            StepRgbFluidCore(fu, fu0, fv, fv0, fr, fr0, fr1, fg, fg0, fg1, fb, fb0, fb1, fdiv, fp, fcurl, fpix,
                t, r0, g0, b0, r1, g1, b1, r2, g2, b2, bass, mid, high, hit, kick, pan, domAng, energy, calm, beatHit, burstPower, uploadGpu);
        }
    }

    private static void StepRgbFluidCore(
        float* u, float* u0, float* v, float* v0,
        float* r, float* r0p, float* r1p,
        float* g, float* g0p, float* g1p,
        float* b, float* b0p, float* b1p,
        float* div, float* p, float* curl, uint* pix,
        float t, float r0, float g0, float b0, float r1, float g1, float b1, float r2, float g2, float b2,
        float bass, float mid, float high, float hit, float kick, float pan, float domAng, float energy, float calm,
        bool beatHit, float burstPower, bool uploadGpu)
    {
        {
            // 1. FIVE full-stage interlocking silk vortex jets (sweeping center, wings, & diagonals -> rich full-canvas saturation!)
            float spdScale = (1.55f + bass * 1.35f + kick * 0.85f) * calm;

            // Jet 0 (Bass Core Sweeper): wide figure-8 across the stage behind the cover art
            float e0x = FlW * (0.50f + 0.36f * MathF.Sin(t * 0.72f) + pan * 0.10f);
            float e0y = FlH * (0.52f + 0.26f * MathF.Sin(t * 1.44f));
            float d0s = (0.68f + bass * 1.10f + kick * 0.55f) * calm;
            SplatFluid(u, v, r, g, b, e0x, e0y,
                MathF.Cos(t * 0.72f) * 62f * spdScale, MathF.Cos(t * 1.44f) * 48f * spdScale,
                r0 * d0s, g0 * d0s, b0 * d0s, 7.2f + bass * 4.5f);

            // Jet 1 (Mid Counter-Swirl): counter-orbiting silk ribbon
            float e1x = FlW * (0.50f - 0.38f * MathF.Cos(t * 0.64f + 1.1f) - pan * 0.09f);
            float e1y = FlH * (0.48f + 0.30f * MathF.Sin(t * 0.98f + 0.7f));
            float d1s = (0.62f + mid * 1.05f + bass * 0.42f) * calm;
            SplatFluid(u, v, r, g, b, e1x, e1y,
                MathF.Sin(t * 0.64f + 1.1f) * 58f * spdScale, MathF.Cos(t * 0.98f + 0.7f) * 52f * spdScale,
                r1 * d1s, g1 * d1s, b1 * d1s, 6.8f + mid * 4.0f);

            // Jet 2 (Treble Whip Ribbon): fast electric accent slicing diagonally
            float e2x = FlW * (0.50f + 0.40f * MathF.Sin(t * 1.12f + 2.4f));
            float e2y = FlH * (0.50f - 0.32f * MathF.Cos(t * 1.56f + 1.9f));
            float d2s = (0.52f + high * 1.00f + hit * 0.72f) * calm;
            SplatFluid(u, v, r, g, b, e2x, e2y,
                MathF.Cos(t * 1.12f + 2.4f) * 66f * spdScale, MathF.Sin(t * 1.56f + 1.9f) * 54f * spdScale,
                r2 * d2s, g2 * d2s, b2 * d2s, 5.8f + high * 3.6f);

            // Jet 3 & 4 (Opposing Left & Right Wing Jets): collide in the center into massive curling mushroom spirals!
            float wingY = FlH * (0.50f + 0.24f * MathF.Sin(t * 0.85f));
            float wingD = (0.55f + bass * 0.95f + hit * 0.55f) * calm;
            SplatFluid(u, v, r, g, b, FlW * 0.08f, wingY,
                (52f + bass * 78f + kick * 55f) * calm, MathF.Cos(t * 1.3f) * 28f * calm,
                r0 * wingD, g0 * wingD, b0 * wingD, 6.4f + bass * 3.5f);
            SplatFluid(u, v, r, g, b, FlW * 0.92f, FlH - wingY,
                -(52f + bass * 78f + kick * 55f) * calm, -MathF.Cos(t * 1.3f) * 28f * calm,
                r1 * wingD, g1 * wingD, b1 * wingD, 6.4f + bass * 3.5f);

            // 2. Beat & Drop Opposing Colliding Vortex Bursts
            if ((beatHit || burstPower > 0.05f) && calm > 0.18f)
            {
                int splats = burstPower > 0.45f ? 5 : 3;
                float pwr = MathF.Max(0.75f, MathF.Max(bass * 1.35f + kick * 0.95f + hit, burstPower));
                for (int s = 0; s < splats; s++)
                {
                    int idx = ++_fluidSplatSeed;
                    float ang = (idx * 2.399963f + t * 1.1f) % MathF.Tau;
                    float sx = FlW * (0.50f + 0.34f * MathF.Cos(ang));
                    float sy = FlH * (0.50f + 0.30f * MathF.Sin(ang));
                    float tang = ang + 2.38f;
                    float spd = (95f + 85f * pwr) * calm;
                    float cr = (s % 3) == 0 ? r0 : ((s % 3) == 1 ? r1 : r2);
                    float cg = (s % 3) == 0 ? g0 : ((s % 3) == 1 ? g1 : g2);
                    float cb = (s % 3) == 0 ? b0 : ((s % 3) == 1 ? b1 : b2);
                    float boost = burstPower > 0.45f ? 1.55f : 1.12f;
                    SplatFluid(u, v, r, g, b, sx, sy, MathF.Cos(tang) * spd, MathF.Sin(tang) * spd,
                        cr * boost, cg * boost, cb * boost, 7.2f + pwr * 3.6f);
                }
            }

            // 3. BASS-REACTIVE RADIAL SHOCKWAVE & GLOBAL KINETIC FLUID SHAKE (plumes physically jolt, ripple & whip to every kick!)
            float shakePwr = (bass * 0.85f + kick * 1.35f + hit * 0.90f) * calm;
            if (shakePwr > 0.10f || beatHit)
            {
                float radialBlast = (beatHit ? 42f : 0f) + kick * 26f + bass * 12f;
                float swirlTorque = (pan * 24f + MathF.Sin(domAng) * 18f) * shakePwr;
                float joltX = (MathF.Cos(domAng) * 16f + pan * 22f) * shakePwr;
                float joltY = (-14f * kick + MathF.Sin(domAng) * 12f) * shakePwr;
                const float midX = FlW * 0.5f, midY = FlH * 0.5f;
                float invRad2 = 1f / (FlW * FlW * 0.16f);
                Parallel.For(0, 4, stripe =>
                {
                    int y0 = 2 + stripe * (FlH - 4) / 4;
                    int y1 = 2 + (stripe + 1) * (FlH - 4) / 4;
                    for (int y = y0; y < y1; y++)
                    {
                        float dy = y - midY;
                        int row = y * FlW;
                        for (int x = 2; x < FlW - 2; x++)
                        {
                            float dx = x - midX;
                            float d2 = dx * dx + dy * dy + 16f;
                            float invD = 1f / MathF.Sqrt(d2);
                            float falloff = MathF.Exp(-d2 * invRad2);
                            float nx = dx * invD, ny = dy * invD;
                            int i = row + x;
                            // Radial bass shockwave + rotational swirl + global directional kick jolt!
                            u[i] += (nx * radialBlast - ny * swirlTorque) * falloff + joltX * 0.22f;
                            v[i] += (ny * radialBlast + nx * swirlTorque) * falloff + joltY * 0.22f;
                        }
                    }
                });
            }

            // 4. High-Curl Vorticity Confinement (tight curling liquid-silk spirals!)
            const float dt = 0.036f;
            Parallel.For(0, 4, stripe =>
            {
                int y0 = 1 + stripe * (FlH - 2) / 4;
                int y1 = 1 + (stripe + 1) * (FlH - 2) / 4;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * FlW;
                    for (int x = 1; x < FlW - 1; x++)
                    {
                        int i = row + x;
                        curl[i] = (v[i + 1] - v[i - 1] - (u[i + FlW] - u[i - FlW])) * 0.5f;
                    }
                }
            });
            float vortDt = (18.5f + energy * 6.5f) * dt;
            Parallel.For(0, 4, stripe =>
            {
                int y0 = 2 + stripe * (FlH - 4) / 4;
                int y1 = 2 + (stripe + 1) * (FlH - 4) / 4;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * FlW;
                    for (int x = 2; x < FlW - 2; x++)
                    {
                        int i = row + x;
                        float dwDx = MathF.Abs(curl[i + 1]) - MathF.Abs(curl[i - 1]);
                        float dwDy = MathF.Abs(curl[i + FlW]) - MathF.Abs(curl[i - FlW]);
                        float invLen = 1f / (MathF.Sqrt(dwDx * dwDx + dwDy * dwDy) + 1e-4f);
                        float c = curl[i] * vortDt;
                        u[i] += dwDy * invLen * c;
                        v[i] -= dwDx * invLen * c;
                    }
                }
            });

            // 5. Incompressible Projection -> Velocity Advection -> Projection
            ProjectFluid(u, v, div, p);
            Parallel.For(0, 4, stripe =>
            {
                int y0 = 1 + stripe * (FlH - 2) / 4;
                int y1 = 1 + (stripe + 1) * (FlH - 2) / 4;
                AdvectFluidBand(u0, u, u, v, dt, 0.976f, y0, y1);
                AdvectFluidBand(v0, v, u, v, dt, 0.976f, y0, y1);
            });
            Buffer.MemoryCopy(u0, u, FlN * sizeof(float), FlN * sizeof(float));
            Buffer.MemoryCopy(v0, v, FlN * sizeof(float), FlN * sizeof(float));
            ProjectFluid(u, v, div, p);

            // 6. RAZOR-SHARP 2nd-Order MacCormack RGB Dye Advection (zero cloud blur!)
            float dyeDiss = 0.946f + 0.034f * calm;
            AdvectDyeMacCormack(r, r0p, r1p, u, v, dt, dyeDiss);
            AdvectDyeMacCormack(g, g0p, g1p, u, v, dt, dyeDiss);
            AdvectDyeMacCormack(b, b0p, b1p, u, v, dt, dyeDiss);
            Buffer.MemoryCopy(r1p, r, FlN * sizeof(float), FlN * sizeof(float));
            Buffer.MemoryCopy(g1p, g, FlN * sizeof(float), FlN * sizeof(float));
            Buffer.MemoryCopy(b1p, b, FlN * sizeof(float), FlN * sizeof(float));

            // 7. Glossy 3D Liquid-Silk Specular Shading + Vivid Neon Bloom -> Premultiplied BGRA
            const float lx = -0.45f, ly = -0.58f, lz = 0.68f;
            float bassGlow = 1.0f + bass * 0.35f + kick * 0.25f;
            Parallel.For(0, 4, stripe =>
            {
                int y0 = stripe * FlH / 4;
                int y1 = (stripe + 1) * FlH / 4;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * FlW;
                    int ym = (y > 0 ? y - 1 : 0) * FlW;
                    int yp = (y < FlH - 1 ? y + 1 : FlH - 1) * FlW;
                    float fy = y < 12 ? y * (1f / 12f) : (y > FlH - 13 ? (FlH - 1 - y) * (1f / 12f) : 1f);
                    for (int x = 0; x < FlW; x++)
                    {
                        int i = row + x;
                        int xm = x > 0 ? x - 1 : 0;
                        int xp = x < FlW - 1 ? x + 1 : FlW - 1;
                        float cr = r[i] * bassGlow, cg = g[i] * bassGlow, cb = b[i] * bassGlow;
                        float maxC = MathF.Max(cr, MathF.Max(cg, cb));
                        float fx = x < 16 ? x * 0.0625f : (x > FlW - 17 ? (FlW - 1 - x) * 0.0625f : 1f);
                        float edgeMask = fx * fy;

                        // Steep screen-space height normal for crisp 3D glossy liquid-silk ridges!
                        float lL = r[row + xm] * 0.299f + g[row + xm] * 0.587f + b[row + xm] * 0.114f;
                        float lR = r[row + xp] * 0.299f + g[row + xp] * 0.587f + b[row + xp] * 0.114f;
                        float lU = r[ym + x] * 0.299f + g[ym + x] * 0.587f + b[ym + x] * 0.114f;
                        float lD = r[yp + x] * 0.299f + g[yp + x] * 0.587f + b[yp + x] * 0.114f;
                        float nx = (lL - lR) * 8.8f;
                        float ny = (lU - lD) * 8.8f;
                        float invN = 1f / MathF.Sqrt(nx * nx + ny * ny + 1f);
                        float ndl = MathF.Max(0f, (nx * lx + ny * ly + lz) * invN);
                        float sp2 = ndl * ndl;
                        float spec = sp2 * sp2 * sp2 * 0.42f * Math.Clamp(maxC * 1.5f, 0f, 1f);
                        float shade = 0.62f + 0.58f * ndl;

                        // Vibrant chroma boost + luminous hot-core neon bloom
                        float lum = (cr + cg + cb) * (1f / 3f);
                        float vr = MathF.Max(0f, lum + (cr - lum) * 1.28f) * shade + spec;
                        float vg = MathF.Max(0f, lum + (cg - lum) * 1.28f) * shade + spec;
                        float vb = MathF.Max(0f, lum + (cb - lum) * 1.28f) * shade + spec;
                        float orr = vr / (1f + 0.30f * vr);
                        float ogg = vg / (1f + 0.30f * vg);
                        float obb = vb / (1f + 0.30f * vb);

                        // Crisp contrast alpha curve: deep dark troughs between ribbons, rich opaque glowing silk cores!
                        float a = Math.Clamp(MathF.Pow(Math.Clamp(maxC * 0.92f, 0f, 1.35f), 1.15f), 0f, 0.92f) * edgeMask;

                        uint ba = (uint)(a * 255f + 0.5f);
                        uint br = (uint)(Math.Clamp(orr * a, 0f, 1f) * 255f + 0.5f);
                        uint bg = (uint)(Math.Clamp(ogg * a, 0f, 1f) * 255f + 0.5f);
                        uint bb = (uint)(Math.Clamp(obb * a, 0f, 1f) * 255f + 0.5f);
                        pix[i] = (ba << 24) | (br << 16) | (bg << 8) | bb;
                    }
                }
            });

            if (uploadGpu)
            {
                if (_fluidBmp == null || _fluidBmpGen != Gfx.DeviceGeneration)
                {
                    _fluidBmp?.Dispose();
                    _fluidBmp = Gfx.CreateBitmap((byte*)pix, FlW, FlH, bc1: false);
                    _fluidBmpGen = Gfx.DeviceGeneration;
                }
                else
                {
                    _fluidBmp.CopyFromMemory((nint)pix, FlW * 4);
                }
            }
        }
    }

    internal static (bool Ok, double MsPerStep, int LitPx) SelfTestFluid()
    {
        ClearRgbFluid();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int steps = 10;
        for (int i = 0; i < steps; i++)
            StepRgbFluidSimulation(i * 0.05f, 0xFFFF2090, 0xFF00D4FF, 0.78f, 0.62f, 0.52f, i == 3 ? 0.90f : 0.25f, 0.25f, 0f, i == 0 || i == 5, i == 0 ? 1.0f : 0f, uploadGpu: false);
        sw.Stop();
        int lit = 0;
        for (int i = 0; i < FlN; i++) if ((_fluidPixels[i] >> 24) > 30) lit++;
        double ms = sw.Elapsed.TotalMilliseconds / steps;
        ClearRgbFluid();
        return (ms < 6.0 && lit > 8000, ms, lit);
    }

    private static uint NextRng()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return _rng = x;
    }

    private static void ClearFluid()
    {
        Array.Clear(_u); Array.Clear(_v); Array.Clear(_d);
        Array.Clear(_u0); Array.Clear(_v0); Array.Clear(_d0);
        Array.Clear(_p);
    }

    /// <summary>Pre-seeds a rich swirling flame wave right on frame-0 of a drop or quiet-exit so flames erupt with ZERO lag!</summary>
    private static void TriggerDropBurst(float strength)
    {
        float s = Math.Clamp(strength, 0.65f, 1.25f);
        fixed (float* d = _d, u = _u, v = _v)
        {
            for (int y = FireH - 26; y < FireH - 1; y++)
            {
                float rowFrac = (y - (FireH - 26)) / 25f; // 0 at tips -> 1 at wall
                int row = y * FireW;
                for (int x = 3; x < FireW - 3; x++)
                {
                    float uNorm = (x + 0.5f) / FireW;
                    float corner = MathF.Pow(MathF.Sin(uNorm * MathF.PI), 0.5f);
                    float wave = 0.45f + 0.55f * MathF.Pow(0.5f + 0.5f * MathF.Sin(uNorm * MathF.PI * 10f), 1.35f);
                    int i = row + x;
                    d[i] = MathF.Max(d[i], rowFrac * rowFrac * wave * corner * 1.35f * s);
                    v[i] -= rowFrac * (28f + wave * 34f) * corner * s;
                    u[i] += MathF.Sin(uNorm * MathF.PI * 14f) * 12f * rowFrac * s;
                }
            }
        }
    }

    /// <summary>Boosts color purity of cover art dominant colors so flames look rich, refined, and vivid — never milky.</summary>
    private static (float r, float g, float b) VividColor(uint argb)
    {
        float r = ((argb >> 16) & 255) / 255f;
        float g = ((argb >> 8) & 255) / 255f;
        float b = (argb & 255) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float avg = (r + g + b) / 3f;
        // If cover color is near-grayscale/white: keep sleek neutral silver-white in Adaptive mode; otherwise blend gently with theme brand
        if (max - min < 0.14f)
        {
            if (Themes.IsAdaptiveVideo)
                return (Math.Clamp(avg, 0.72f, 0.96f), Math.Clamp(avg, 0.72f, 0.96f), Math.Clamp(avg + 0.03f, 0.75f, 1f));
            uint tb = Th.Brand;
            float tr = ((tb >> 16) & 255) / 255f, tg = ((tb >> 8) & 255) / 255f, tbb = (tb & 255) / 255f;
            r = r * 0.35f + tr * 0.65f;
            g = g * 0.35f + tg * 0.65f;
            b = b * 0.35f + tbb * 0.65f;
            avg = (r + g + b) / 3f;
        }
        r = Math.Clamp(avg + (r - avg) * 1.70f, 0.03f, 1f);
        g = Math.Clamp(avg + (g - avg) * 1.70f, 0.03f, 1f);
        b = Math.Clamp(avg + (b - avg) * 1.70f, 0.03f, 1f);
        return (r, g, b);
    }

    private static void EnsureCoverFirePalette(uint dom1, uint dom2)
    {
        uint c1 = dom1 != 0 ? dom1 : Th.Brand;
        uint c2 = dom2 != 0 && dom2 != c1 ? dom2 : Gfx.Mix(c1, Th.Brand, 0.5f);
        if (_palCol1 == c1 && _palCol2 == c2) return;
        _palCol1 = c1;
        _palCol2 = c2;

        var (r1, g1, b1) = VividColor(c1);
        var (r2, g2, b2) = VividColor(c2);

        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            float r, g, b, a;
            if (t < 0.26f)
            {
                // Ultra-clean transparent wispy outer edge
                float k = t / 0.26f;
                r = r1 * 0.35f * k; g = g1 * 0.35f * k; b = b1 * 0.35f * k;
                a = k * k * k * 0.12f;
            }
            else if (t < 0.60f)
            {
                float k = (t - 0.26f) / 0.34f;
                r = r1 * (0.35f + 0.65f * k);
                g = g1 * (0.35f + 0.65f * k);
                b = b1 * (0.35f + 0.65f * k);
                a = 0.12f + 0.56f * k;
            }
            else if (t < 0.89f)
            {
                float k = (t - 0.60f) / 0.29f;
                r = r1 + (r2 - r1) * k;
                g = g1 + (g2 - g1) * k;
                b = b1 + (b2 - b1) * k;
                a = 0.68f + 0.26f * k;
            }
            else
            {
                // Crisp jewel-bright core strictly at the hottest 11%
                float k = (t - 0.89f) / 0.11f;
                r = r2 + (1f - r2) * k * 0.78f;
                g = g2 + (1f - g2) * k * 0.78f;
                b = b2 + (1f - b2) * k * 0.78f;
                a = 0.94f + 0.06f * k;
            }
            uint ba = (uint)Math.Clamp(a * 255f + 0.5f, 0, 255);
            uint br = (uint)Math.Clamp(r * a * 255f + 0.5f, 0, 255);
            uint bg = (uint)Math.Clamp(g * a * 255f + 0.5f, 0, 255);
            uint bb = (uint)Math.Clamp(b * a * 255f + 0.5f, 0, 255);
            _firePal[i] = (ba << 24) | (br << 16) | (bg << 8) | bb;
        }
    }

    private static void Advect(float* dst, float* src, float* u, float* v, float dt, float dissipation)
    {
        const float maxX = FireW - 1.5f, maxY = FireH - 1.5f;
        for (int y = 1; y < FireH - 1; y++)
        {
            int row = y * FireW;
            for (int x = 1; x < FireW - 1; x++)
            {
                int i = row + x;
                float px = x - dt * u[i];
                if (px < 0.5f) px = 0.5f; else if (px > maxX) px = maxX;
                float py = y - dt * v[i];
                if (py < 0.5f) py = 0.5f; else if (py > maxY) py = maxY;
                int x0 = (int)px, y0 = (int)py;
                float tx = px - x0, ty = py - y0;
                int r0 = y0 * FireW + x0;
                int r1 = r0 + FireW;
                float s00 = src[r0], s10 = src[r0 + 1], s01 = src[r1], s11 = src[r1 + 1];
                float top = s00 + (s10 - s00) * tx;
                float bot = s01 + (s11 - s01) * tx;
                dst[i] = (top + (bot - top) * ty) * dissipation;
            }
        }
    }

    private static void Project(float* u, float* v, float* div, float* p)
    {
        for (int y = 1; y < FireH - 1; y++)
        {
            int row = y * FireW;
            for (int x = 1; x < FireW - 1; x++)
            {
                int i = row + x;
                div[i] = -0.5f * (u[i + 1] - u[i - 1] + v[i + FireW] - v[i - FireW]);
                p[i] *= 0.55f; // warm-start pressure -> converges cleanly in 3 iterations!
            }
        }
        for (int k = 0; k < 3; k++)
        {
            for (int y = 1; y < FireH - 1; y++)
            {
                int row = y * FireW;
                for (int x = 1; x < FireW - 1; x++)
                {
                    int i = row + x;
                    p[i] = (div[i] + p[i - 1] + p[i + 1] + p[i - FireW] + p[i + FireW]) * 0.25f;
                }
            }
        }
        for (int y = 1; y < FireH - 1; y++)
        {
            int row = y * FireW;
            for (int x = 1; x < FireW - 1; x++)
            {
                int i = row + x;
                u[i] -= 0.5f * (p[i + 1] - p[i - 1]);
                v[i] -= 0.5f * (p[i + FireW] - p[i - FireW]);
            }
        }
    }

    private static void StepFireSimulation(float fire, float t, uint dom1, uint dom2)
    {
        // 45 Hz crisp fluid cadence (~0.3ms CPU via raw pointers!)
        if (UI.Now - _lastFireSimMs < 22) return;
        _lastFireSimMs = UI.Now;
        EnsureCoverFirePalette(dom1, dom2);

        var bands = AudioAnalyzer.Bands;
        float bass = AudioAnalyzer.BassEnergy;
        float hit = AudioAnalyzer.SharpWaveHit;
        float beatSurge = bass * 0.55f + hit * 1.10f;

        fixed (float* u = _u, u0 = _u0, v = _v, v0 = _v0, d = _d, d0 = _d0, div = _div, p = _p, curl = _curl)
        fixed (uint* pix = _firePixels, pal = _firePal)
        {
            // 1. Sleek audio-reactive fluid nozzles along emitter wall (tapered cleanly at top/bottom corners)
            int emitRow = (FireH - 2) * FireW;
            int emitRow2 = (FireH - 3) * FireW;
            for (int x = 3; x < FireW - 3; x++)
            {
                float uNorm = (x + 0.5f) / FireW;
                float cornerFade = MathF.Pow(MathF.Sin(uNorm * MathF.PI), 0.62f);
                float dist = MathF.Abs(uNorm * 2f - 1f);
                int bIdx = Math.Clamp((int)(MathF.Pow(dist, 0.80f) * (AudioAnalyzer.BandCount - 1)), 0, AudioAnalyzer.BandCount - 1);
                float bandE = bands[bIdx];

                float wave = 0.42f + 0.58f * MathF.Pow(0.5f + 0.5f * MathF.Sin(uNorm * MathF.PI * 10f + MathF.Sin(t * 2.3f + uNorm * 6f) * 1.6f), 1.35f);
                float rnd = (NextRng() & 255) / 255f;
                float fuel = (fire * 0.64f + bandE * 0.92f + beatSurge * 0.78f) * wave * cornerFade * (0.74f + 0.26f * rnd);

                int i = emitRow + x;
                float dn = d[i] * 0.30f + fuel * 1.18f;
                d[i] = dn > 1.65f ? 1.65f : dn;
                float jetVy = -(24f + fire * 34f + bandE * 36f + beatSurge * 44f) * (0.55f + 0.45f * wave) * cornerFade;
                float jetVx = MathF.Sin(t * 3.6f + uNorm * 18f) * (12f + beatSurge * 20f) + (rnd - 0.5f) * 14f;
                v[i] = v[i] * 0.38f + jetVy * 0.62f;
                u[i] = u[i] * 0.5f + jetVx * 0.5f;
                if (hit > 0.20f)
                {
                    if (fuel * 0.88f > d[emitRow2 + x]) d[emitRow2 + x] = fuel * 0.88f;
                    v[emitRow2 + x] -= hit * 22f * cornerFade;
                }
            }

            // 2. Buoyancy + Vorticity Confinement (creates fine, silky swirling tendrils!)
            const float dt = 0.038f;
            float buoy = (26f + beatSurge * 16f) * dt;
            for (int y = 1; y < FireH - 1; y++)
            {
                int row = y * FireW;
                for (int x = 1; x < FireW - 1; x++)
                {
                    int i = row + x;
                    v[i] -= d[i] * buoy;
                    curl[i] = (v[i + 1] - v[i - 1] - (u[i + FireW] - u[i - FireW])) * 0.5f;
                }
            }
            const float vortDt = 6.8f * dt;
            for (int y = 2; y < FireH - 2; y++)
            {
                int row = y * FireW;
                for (int x = 2; x < FireW - 2; x++)
                {
                    int i = row + x;
                    float dwDx = MathF.Abs(curl[i + 1]) - MathF.Abs(curl[i - 1]);
                    float dwDy = MathF.Abs(curl[i + FireW]) - MathF.Abs(curl[i - FireW]);
                    float invLen = 1f / (MathF.Sqrt(dwDx * dwDx + dwDy * dwDy) + 1e-4f);
                    float c = curl[i] * vortDt;
                    u[i] += dwDy * invLen * c;
                    v[i] -= dwDx * invLen * c;
                }
            }

            // 3. Projection -> Semi-Lagrangian Advection -> Projection
            Project(u, v, div, p);
            Advect(u0, u, u, v, dt, 0.95f);
            Advect(v0, v, u, v, dt, 0.95f);
            Buffer.MemoryCopy(u0, u, FireN * sizeof(float), FireN * sizeof(float));
            Buffer.MemoryCopy(v0, v, FireN * sizeof(float), FireN * sizeof(float));
            Project(u, v, div, p);

            // 4. Advect fuel/temperature with fuller longevity so flames billow further cleanly
            float cool = 0.892f + fire * 0.048f + MathF.Min(0.025f, beatSurge * 0.022f);
            Advect(d0, d, u, v, dt, cool);
            Buffer.MemoryCopy(d0, d, FireN * sizeof(float), FireN * sizeof(float));

            // 5. Dual-pass separable Gaussian blur on the render density (zero pixels -> silky volumetric GPU blur!)
            for (int y = 0; y < FireH; y++)
            {
                int row = y * FireW;
                for (int x = 0; x < FireW; x++)
                {
                    int xm2 = x > 1 ? x - 2 : 0, xm1 = x > 0 ? x - 1 : 0;
                    int xp1 = x < FireW - 1 ? x + 1 : FireW - 1, xp2 = x < FireW - 2 ? x + 2 : FireW - 1;
                    d0[row + x] = (d[row + xm2] + 4f * d[row + xm1] + 6f * d[row + x] + 4f * d[row + xp1] + d[row + xp2]) * 0.0625f;
                }
            }
            for (int y = 0; y < FireH; y++)
            {
                int ym2 = (y > 1 ? y - 2 : 0) * FireW, ym1 = (y > 0 ? y - 1 : 0) * FireW, y0r = y * FireW;
                int yp1 = (y < FireH - 1 ? y + 1 : FireH - 1) * FireW, yp2 = (y < FireH - 2 ? y + 2 : FireH - 1) * FireW;
                for (int x = 0; x < FireW; x++)
                    div[y0r + x] = (d0[ym2 + x] + 4f * d0[ym1 + x] + 6f * d0[y0r + x] + 4f * d0[yp1 + x] + d0[yp2 + x]) * 0.0625f;
            }
            // Second Gaussian pass for ultra-smooth zero-pixel volumetric glow
            for (int y = 0; y < FireH; y++)
            {
                int row = y * FireW;
                for (int x = 0; x < FireW; x++)
                {
                    int xm2 = x > 1 ? x - 2 : 0, xm1 = x > 0 ? x - 1 : 0;
                    int xp1 = x < FireW - 1 ? x + 1 : FireW - 1, xp2 = x < FireW - 2 ? x + 2 : FireW - 1;
                    d0[row + x] = (div[row + xm2] + 4f * div[row + xm1] + 6f * div[row + x] + 4f * div[row + xp1] + div[row + xp2]) * 0.0625f;
                }
            }
            const float fadeBand = FireH * 0.42f;
            for (int y = 0; y < FireH; y++)
            {
                int ym2 = (y > 1 ? y - 2 : 0) * FireW, ym1 = (y > 0 ? y - 1 : 0) * FireW, y0r = y * FireW;
                int yp1 = (y < FireH - 1 ? y + 1 : FireH - 1) * FireW, yp2 = (y < FireH - 2 ? y + 2 : FireH - 1) * FireW;
                float tTip = y < fadeBand ? y / fadeBand : 1f;
                float tipMask = tTip * tTip * (3f - 2f * tTip);
                for (int x = 0; x < FireW; x++)
                {
                    float sideMask = x < 10 ? x * 0.1f : (x > FireW - 11 ? (FireW - 1 - x) * 0.1f : 1f);
                    float blurred = (d0[ym2 + x] + 4f * d0[ym1 + x] + 6f * d0[y0r + x] + 4f * d0[yp1 + x] + d0[yp2 + x]) * 0.0625f;
                    float val = blurred * tipMask * sideMask;
                    int idx = (int)(val * 238f);
                    if (idx < 0) idx = 0; else if (idx > 255) idx = 255;
                    pix[y0r + x] = pal[idx];
                }
            }

            // 6. Upload crisp 192x96 texture to reusable GPU bitmap
            if (_fireBmp == null || _fireBmpGen != Gfx.DeviceGeneration)
            {
                _fireBmp?.Dispose();
                _fireBmp = Gfx.CreateBitmap((byte*)pix, FireW, FireH, bc1: false);
                _fireBmpGen = Gfx.DeviceGeneration;
            }
            else
            {
                _fireBmp.CopyFromMemory((nint)pix, FireW * 4);
            }
        }
    }

    public static void Update(bool enabled)
    {
        if (_lastFrame == Gfx.Frame) return;
        _lastFrame = Gfx.Frame;
        if (UI.Now - _updatedAt > 200) { _average = _intensity = _flash = _fireIntensity = 0; Array.Clear(_bursts); }
        _updatedAt = UI.Now;
        if (!enabled)
        {
            _average = _intensity = _flash = _fireIntensity = 0;
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
        // 100% guaranteed fire after quiet breakdown + frame-0 drop-synced fire climax (6.2s blaze, 9s cooldown)!
        float peak = PeakStarBurst.ActiveIntensity;
        long now = UI.Now;
        bool fireActive = _fireStartedAt > 0 && now - _fireStartedAt < 6200;
        if (!fireActive && _fireStartedAt > 0)
        {
            _fireStartedAt = 0;
            _fireCooldownUntil = now + 9000;
        }
        bool freshDrop = AudioAnalyzer.DropOnsetStamp != _lastDropSeen && now - AudioAnalyzer.DropOnsetStamp < 140;
        if (freshDrop) _lastDropSeen = AudioAnalyzer.DropOnsetStamp;
        bool freshQuietExit = AudioAnalyzer.QuietExitStamp != _lastQuietExitSeen && now - AudioAnalyzer.QuietExitStamp < 250;
        if (freshQuietExit) _lastQuietExitSeen = AudioAnalyzer.QuietExitStamp;

        if (freshQuietExit)
        {
            // 100% guaranteed eruption right after a quiet breakdown — bypasses cooldown!
            _fireStartedAt = now;
            fireActive = true;
            _fireIntensity = MathF.Max(_fireIntensity, 0.88f);
            _fluidBurstPower = MathF.Max(_fluidBurstPower, 1.15f);
            TriggerDropBurst(1.10f);
        }
        else if (!fireActive && AudioAnalyzer.QuietDim < 0.1f)
        {
            bool peakTrigger = peak > 0.12f;
            bool dropTrigger = now >= _fireCooldownUntil && (freshDrop || AudioAnalyzer.DropImpact > 0.55f || (AudioAnalyzer.BoomEnergy > 0.50f && AudioAnalyzer.BassEnergy > 0.36f));
            if (peakTrigger || dropTrigger)
            {
                _fireStartedAt = now;
                fireActive = true;
                float str = peakTrigger ? 1.15f : MathF.Max(0.85f, AudioAnalyzer.DropOnsetStrength);
                _fireIntensity = MathF.Max(_fireIntensity, 0.82f);
                _fluidBurstPower = MathF.Max(_fluidBurstPower, str);
                TriggerDropBurst(str);
            }
        }
        float fireTarget = fireActive
            ? Math.Clamp(0.75f + AudioAnalyzer.BassEnergy * 0.25f + AudioAnalyzer.SharpWaveHit * 0.35f + peak * 0.3f, 0f, 1f)
            : 0f;
        if (AudioAnalyzer.QuietDim > 0.18f && !freshQuietExit) { fireTarget = 0f; _fireStartedAt = 0; }
        float fireRate = fireTarget > _fireIntensity ? 12.0f : 2.0f;
        _fireIntensity += (fireTarget - _fireIntensity) * (1f - MathF.Exp(-dt * fireRate));
        if (_fireIntensity < 0.008f) { _fireIntensity = 0f; ClearFluid(); }

        if (_intensity > 0.01f || _flash > 0.01f || _fireIntensity > 0.01f) UI.Animate();
    }

    /// <summary>Draws Pavel-style RGB curl-vorticity silk fluid OR cover-matched side fire + quiet breakdown starlight.</summary>
    public static void DrawBackdropEffects(R stage, uint dom1, uint dom2)
    {
        var sOpt = AppSettings.Current;
        string mode = !sOpt.MusicImpactEffects ? "off" : (sOpt.MusicStageEffect ?? "fluid");
        if (mode == "off") return;

        bool fluidMode = !string.Equals(mode, "fire", StringComparison.OrdinalIgnoreCase);
        float fire = _fireIntensity;
        float qDim = AudioAnalyzer.QuietDim;
        if (!fluidMode && fire <= 0.01f && qDim <= 0.04f) return;

        Gfx.PushClip(stage);
        float t = (UI.Now - UI.StartTick) * 0.001f;

        // ---- 1. QUIET BREAKDOWN: Floating Firefly Starlight & Gentle Drifting Sparkles ----
        if (qDim > 0.04f)
        {
            uint starCol = Gfx.Mix(dom1, 0xFFEAF6FF, 0.72f);
            const int motes = 20;
            for (int i = 0; i < motes; i++)
            {
                float seed = i * 1.6180339f;
                float u = ((seed * 7.31f) % 1f + 1f) % 1f;
                float speed = 0.032f + (i % 5) * 0.011f;
                float v = 1f - ((t * speed + (seed * 3.17f % 1f)) % 1f); // slowly floats upward
                float sway = MathF.Sin(t * (0.9f + (i % 4) * 0.35f) + seed * 6f) * 16f;
                float x = stage.X + 24f + u * (stage.W - 48f) + sway;
                float y = stage.Y + 24f + v * (stage.H - 48f);
                float twinkle = 0.45f + 0.55f * MathF.Sin(t * (2.6f + (i % 5) * 0.7f) + i * 2.1f);
                float edgeFade = MathF.Sin(v * MathF.PI);
                float a = qDim * twinkle * edgeFade * 0.72f;
                if (a <= 0.02f) continue;

                float r = 1.4f + (i % 3) * 0.85f;
                Gfx.Circle(x, y, r * 2.6f, Gfx.Fade(starCol, a * 0.16f));
                Gfx.Circle(x, y, r, Gfx.Fade(0xFFFFFFFF, a));
                if ((i % 4) == 0)
                {
                    // Tiny delicate 4-point star sparkle in silence
                    float sp = r * 2.8f;
                    Gfx.Line(x - sp, y, x + sp, y, Gfx.Fade(starCol, a * 0.65f), 1.0f);
                    Gfx.Line(x, y - sp, x, y + sp, Gfx.Fade(starCol, a * 0.65f), 1.0f);
                }
            }
        }

        // ---- 2A. PAVEL-STYLE THEME-ADAPTIVE 320x180 MACCORMACK RGB CURL-VORTICITY SILK FLUID + BASS SHAKE ----
        if (fluidMode)
        {
            if (UI.Now - _lastFluidSimMs >= 16)
            {
                _lastFluidSimMs = UI.Now;
                bool beatHit = AudioAnalyzer.BeatStamp != _lastFluidBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.30f;
                if (beatHit) _lastFluidBeat = AudioAnalyzer.BeatStamp;
                float burst = _fluidBurstPower;
                _fluidBurstPower = 0f;
                StepRgbFluidSimulation(t, dom1, dom2,
                    AudioAnalyzer.BassEnergy, AudioAnalyzer.MidEnergy, AudioAnalyzer.HighEnergy,
                    AudioAnalyzer.SharpWaveHit, AudioAnalyzer.StereoPan, qDim, beatHit, burst, uploadGpu: true);
            }
            if (_fluidBmp != null)
            {
                float bass = AudioAnalyzer.BassEnergy;
                float kick = MusicBounce.Kick;
                float fluidOp = Math.Clamp(0.76f + bass * 0.20f + AudioAnalyzer.DropImpact * 0.14f, 0.55f, 0.96f) * Math.Clamp(1f - qDim * 0.72f, 0.20f, 1f);
                // Live GPU Bass-Pump & Coupled Shake Transform on the fluid layer so ribbons visibly thump & sway!
                var savedTf = Gfx.DC.Transform;
                float pump = 1.035f + bass * 0.065f + kick * 0.048f;
                float tilt = AudioAnalyzer.StereoPan * 0.022f + MathF.Sin(t * 2.1f) * kick * 0.015f;
                var center = new Vector2(stage.X + stage.W * 0.5f, stage.Y + stage.H * 0.5f);
                var tfFluid = MusicBounce.Push(stage, 2, amp: 0.75f, rotAmp: 0.45f, textSafe: false);
                Gfx.DC.Transform = Matrix3x2.CreateScale(pump, pump, center) * Matrix3x2.CreateRotation(tilt, center) * Gfx.DC.Transform;
                Gfx.DC.DrawBitmap(_fluidBmp, new RawRectF(stage.X, stage.Y, stage.Right, stage.Bottom), fluidOp,
                    Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
                MusicBounce.Pop(tfFluid);
                Gfx.DC.Transform = savedTf;
            }
            UI.Animate();
        }
        // ---- 2B. COVER-MATCHED FLUID SIDE FIRE CLIMAX (selectable "fire" mode) ----
        else if (fire > 0.01f)
        {
            float bass = AudioAnalyzer.BassEnergy;
            StepFireSimulation(fire, t, dom1, dom2);

            // Cover-matched vivid side glows (cached slots 2 & 3 — 0 GPU allocs)
            uint fc1 = dom1 != 0 ? dom1 : Th.Brand;
            uint fc2 = dom2 != 0 && dom2 != fc1 ? dom2 : Gfx.Mix(fc1, Th.Brand, 0.5f);
            var (tr, tg, tb) = VividColor(fc1);
            float glowA = Math.Clamp(fire * (0.28f + bass * 0.12f), 0f, 0.40f);
            float glowRx = stage.W * (0.16f + fire * 0.12f);
            float glowRy = stage.H * 0.62f;
            Gfx.RadialGlow(stage.X - 6f, stage.Y + stage.H * 0.5f, glowRx, glowRy, tr, tg, tb, glowA, slot: 3);
            Gfx.RadialGlow(stage.Right + 6f, stage.Y + stage.H * 0.5f, glowRx, glowRy, tr, tg, tb, glowA, slot: 2);

            // Draw fuller 192x96 cover-colored swirling flames inward from left & right (zero hard border!)
            if (_fireBmp != null)
            {
                float hitLift = AudioAnalyzer.SharpWaveHit * 0.08f + bass * 0.04f;
                float sideDepth = stage.W * Math.Clamp(0.18f + fire * 0.14f + hitLift, 0.14f, 0.34f);
                float op = Math.Clamp(fire * 0.86f, 0f, 0.86f);
                var srcRect = new RawRectF(0f, 0f, stage.H, sideDepth);
                var savedTf = Gfx.DC.Transform;

                Gfx.DC.Transform = new Matrix3x2(0f, 1f, -1f, 0f, stage.X + sideDepth, stage.Y) * savedTf;
                Gfx.DC.DrawBitmap(_fireBmp, srcRect, op, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);

                Gfx.DC.Transform = new Matrix3x2(0f, 1f, 1f, 0f, stage.Right - sideDepth, stage.Y) * savedTf;
                Gfx.DC.DrawBitmap(_fireBmp, srcRect, op, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);

                Gfx.DC.Transform = savedTf;
            }

            // 28 Crisp Cover-Colored Sparks launching inward from left & right edges
            const int embers = 28;
            for (int i = 0; i < embers; i++)
            {
                bool fromLeft = (i & 1) == 0;
                float seed = i * 2.399963f;
                float speed = 0.28f + (i % 6) * 0.08f;
                float life = (t * speed + (seed % 1f)) % 1f;
                float v = ((seed * 3.71f) % 1f + 1f) % 1f;
                float reach = life * stage.W * (0.19f + fire * 0.16f);
                float x = fromLeft ? (stage.X + reach) : (stage.Right - reach);
                float y = stage.Y + 28f + v * (stage.H - 56f) - life * 44f + MathF.Sin(t * (3.2f + (i % 4)) + seed * 4f) * 12f;
                float fade = MathF.Sin(life * MathF.PI) * (1f - life * 0.42f);
                float a = fire * fade * 0.84f;
                if (a <= 0.02f) continue;

                float sz = (1.3f + (i % 3) * 0.75f) * (1f - life * 0.35f);
                uint ec = (i % 3) switch { 0 => 0xFFFFFFFF, 1 => fc2, _ => fc1 };
                Gfx.Circle(x, y, sz * 2.0f, Gfx.Fade(ec, a * 0.22f));
                Gfx.Circle(x, y, sz, Gfx.Fade(0xFFFFFFFF, a * 0.88f));
            }
        }

        Gfx.PopClip();
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
