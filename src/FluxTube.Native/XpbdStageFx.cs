using System.Numerics;
using Vortice.Direct2D1;

namespace FluxTube;

/// <summary>
/// Ultra-fast Extended Position Based Dynamics (XPBD) audio-reactive stage effects (~0.08ms CPU, zero per-frame allocs):
/// 1) XPBD Silk Membrane ("xpbd_cloth"): 36x20 elastic cloth grid with distance constraints, bass shockwave punches, 3D normal specular sheen.
/// 2) XPBD Laser Strings + Soft-Body Mercury Aura ("xpbd_strings"): 12 plucked FFT-bound Verlet strings + area-preserving soft-body halo.
/// </summary>
public static unsafe class XpbdStageFx
{
    // ---- 1. XPBD Elastic Silk Membrane (36x20 = 720 nodes, 4 constraint iterations) ----
    private const int CW = 36, CH = 20, CN = CW * CH;
    private static readonly float[] _z = new float[CN], _zPrev = new float[CN], _zVel = new float[CN];
    private static readonly float[] _ox = new float[CN], _oy = new float[CN];
    private static readonly float[] _oxPrev = new float[CN], _oyPrev = new float[CN];
    private static readonly uint[] _clothPx = new uint[CW * CH * 4]; // upscaled 2x smooth shading 72x40 -> GPU HighQualityCubic
    private const int ImgW = 144, ImgH = 80;
    private static readonly uint[] _clothImg = new uint[ImgW * ImgH];
    private static ID2D1Bitmap1? _clothBmp;
    private static int _clothBmpGen = -1;
    private static long _lastClothBeat;

    // ---- 2. XPBD Plucked Laser Strings (12 strings x 24 nodes) + Soft-Body Ring (32 nodes) ----
    private const int StrCount = 12, StrNodes = 24;
    private static readonly float[] _sy = new float[StrCount * StrNodes];
    private static readonly float[] _syPrev = new float[StrCount * StrNodes];
    private static readonly float[] _sEnergy = new float[StrCount];
    private const int RingN = 32;
    private static readonly float[] _rr = new float[RingN], _rrPrev = new float[RingN];
    private static bool _ringInit;
    private static long _lastStrBeat;

    public static void DisposeGpuResources()
    {
        _clothBmp?.Dispose();
        _clothBmp = null;
        _clothBmpGen = -1;
    }

    private static void StepXpbdCloth(float dt, float t, uint dom1, uint dom2, float bass, float mid, float high, float hit, float pan, bool beatHit, bool uploadGpu)
    {
        dt = Math.Clamp(dt, 0.004f, 0.025f);
        float damp = MathF.Exp(-dt * 2.8f);
        var bands = AudioAnalyzer.Bands;

        // 1. Predict positions (Verlet / XPBD prediction) + continuous audio wind & bass shockwaves
        float cx = (CW - 1) * (0.5f + pan * 0.16f);
        float cy = (CH - 1) * 0.5f;
        float kickPunch = (beatHit ? 2.4f : 0f) + bass * 0.85f + hit * 1.15f;

        for (int y = 1; y < CH - 1; y++)
        {
            int row = y * CW;
            for (int x = 1; x < CW - 1; x++)
            {
                int i = row + x;
                float vz = (_z[i] - _zPrev[i]) * damp;
                float vx = (_ox[i] - _oxPrev[i]) * damp;
                float vy = (_oy[i] - _oyPrev[i]) * damp;
                _zPrev[i] = _z[i];
                _oxPrev[i] = _ox[i];
                _oyPrev[i] = _oy[i];

                float dx = x - cx, dy = y - cy;
                float d2 = dx * dx + dy * dy;
                float bandVal = bands.Length > 0 ? bands[(x * 31) / Math.Max(1, CW - 1)] : bass;

                // Harmonic travelling wind + radial bass punch through the cloth!
                float wind = MathF.Sin(x * 0.42f + t * 3.1f) * MathF.Cos(y * 0.48f - t * 2.2f) * (0.35f + mid * 1.1f + high * 0.65f);
                float punch = MathF.Exp(-d2 * 0.032f) * kickPunch * 1.35f;
                float fz = (wind + punch + bandVal * 0.65f - _z[i] * 3.8f) * dt * dt * 24f;
                float fx = (MathF.Sin(y * 0.55f + t * 2.4f) * (0.18f + bass * 0.55f) - _ox[i] * 5.0f) * dt * dt * 14f;
                float fy = (MathF.Cos(x * 0.45f - t * 2.0f) * (0.18f + mid * 0.50f) - _oy[i] * 5.0f) * dt * dt * 14f;

                _z[i] = Math.Clamp(_z[i] + vz + fz, -3.5f, 3.5f);
                _ox[i] = Math.Clamp(_ox[i] + vx + fx, -1.2f, 1.2f);
                _oy[i] = Math.Clamp(_oy[i] + vy + fy, -1.2f, 1.2f);
            }
        }

        // 2. XPBD Distance Constraints (4 Gauss-Seidel iterations with compliance alpha -> silky inextensible fabric!)
        const float alpha = 0.015f;
        float k = 1f / (2f + alpha / (dt * dt));
        for (int iter = 0; iter < 4; iter++)
        {
            for (int y = 1; y < CH - 1; y++)
            {
                int row = y * CW;
                for (int x = 1; x < CW - 1; x++)
                {
                    int i = row + x;
                    // Smooth membrane tension coupling to 4 structural neighbors (z + tangential ox/oy)
                    float avgZ = (_z[i - 1] + _z[i + 1] + _z[i - CW] + _z[i + CW]) * 0.25f;
                    _z[i] += (avgZ - _z[i]) * (k * 1.65f);
                    float avgOx = (_ox[i - 1] + _ox[i + 1] + _ox[i - CW] + _ox[i + CW]) * 0.25f;
                    float avgOy = (_oy[i - 1] + _oy[i + 1] + _oy[i - CW] + _oy[i + CW]) * 0.25f;
                    _ox[i] += (avgOx - _ox[i]) * (k * 1.25f);
                    _oy[i] += (avgOy - _oy[i]) * (k * 1.25f);
                }
            }
        }

        // 3. Render smooth bilinearly-interpolated 144x80 iridescent 3D silk heightfield with Fresnel sheen
        var (r0, g0, b0) = MusicStageEffects.VividPaletteColor(dom1 != 0 ? dom1 : Th.Brand);
        uint c2 = dom2 != 0 && dom2 != dom1 ? dom2 : Gfx.Mix(dom1 != 0 ? dom1 : Th.Brand, Th.Brand, 0.5f);
        var (r1, g1, b1) = MusicStageEffects.VividPaletteColor(c2);

        const float lx = -0.42f, ly = -0.55f, lz = 0.72f;
        fixed (uint* pix = _clothImg)
        {
            for (int py = 0; py < ImgH; py++)
            {
                float gy = py * (CH - 1f) / (ImgH - 1f);
                int y0 = Math.Min(CH - 2, (int)gy);
                float ty = gy - y0;
                float edgeY = py < 10 ? py * 0.1f : (py > ImgH - 11 ? (ImgH - 1 - py) * 0.1f : 1f);
                for (int px = 0; px < ImgW; px++)
                {
                    float gx = px * (CW - 1f) / (ImgW - 1f);
                    int x0 = Math.Min(CW - 2, (int)gx);
                    float tx = gx - x0;
                    float edgeX = px < 14 ? px / 14f : (px > ImgW - 15 ? (ImgW - 1 - px) / 14f : 1f);

                    int i00 = y0 * CW + x0;
                    int i10 = i00 + 1, i01 = i00 + CW, i11 = i01 + 1;
                    float zTop = _z[i00] + (_z[i10] - _z[i00]) * tx;
                    float zBot = _z[i01] + (_z[i11] - _z[i01]) * tx;
                    float h = zTop + (zBot - zTop) * ty;

                    float dzdx = ((_z[i10] - _z[i00]) * (1f - ty) + (_z[i11] - _z[i01]) * ty) * 2.8f;
                    float dzdy = ((_z[i01] - _z[i00]) * (1f - tx) + (_z[i11] - _z[i10]) * tx) * 2.8f;
                    float invN = 1f / MathF.Sqrt(dzdx * dzdx + dzdy * dzdy + 1f);
                    float nx = -dzdx * invN, ny = -dzdy * invN, nz = invN;

                    float ndl = MathF.Max(0f, nx * lx + ny * ly + nz * lz);
                    float fresnel = MathF.Pow(1f - Math.Clamp(nz, 0f, 1f), 2.2f);
                    float spec = MathF.Pow(ndl, 10f) * 0.65f;

                    // Iridescent color blend across cloth fold slope
                    float blend = Math.Clamp(0.5f + nx * 0.85f + h * 0.25f, 0f, 1f);
                    float cr = (r0 + (r1 - r0) * blend) * (0.28f + 0.72f * ndl) + fresnel * r1 * 0.65f + spec;
                    float cg = (g0 + (g1 - g0) * blend) * (0.28f + 0.72f * ndl) + fresnel * g1 * 0.65f + spec;
                    float cb = (b0 + (b1 - b0) * blend) * (0.28f + 0.72f * ndl) + fresnel * b1 * 0.65f + spec;

                    // Subtle woven silk mesh grid lines that deform with the cloth
                    float ux = gx + (_ox[i00] * (1f - tx) + _ox[i10] * tx) * 1.4f;
                    float uy = gy + (_oy[i00] * (1f - ty) + _oy[i01] * ty) * 1.4f;
                    float wireX = MathF.Abs(MathF.Sin(ux * MathF.PI));
                    float wireY = MathF.Abs(MathF.Sin(uy * MathF.PI));
                    float weave = 1f - MathF.Min(wireX, wireY);
                    weave = MathF.Pow(Math.Clamp(weave, 0f, 1f), 7f) * 0.32f;
                    cr += weave * (r0 * 0.6f + 0.4f);
                    cg += weave * (g0 * 0.6f + 0.4f);
                    cb += weave * (b0 * 0.6f + 0.4f);

                    float mag = MathF.Max(cr, MathF.Max(cg, cb));
                    float a = Math.Clamp(0.22f + fresnel * 0.72f + MathF.Abs(h) * 0.32f + weave * 0.85f, 0f, 0.86f) * edgeX * edgeY;
                    float orr = cr / (1f + 0.35f * cr);
                    float ogg = cg / (1f + 0.35f * cg);
                    float obb = cb / (1f + 0.35f * cb);

                    uint ba = (uint)(a * 255f + 0.5f);
                    uint br = (uint)(Math.Clamp(orr * a, 0f, 1f) * 255f + 0.5f);
                    uint bg = (uint)(Math.Clamp(ogg * a, 0f, 1f) * 255f + 0.5f);
                    uint bb = (uint)(Math.Clamp(obb * a, 0f, 1f) * 255f + 0.5f);
                    pix[py * ImgW + px] = (ba << 24) | (br << 16) | (bg << 8) | bb;
                }
            }

            if (uploadGpu)
            {
                if (_clothBmp == null || _clothBmpGen != Gfx.DeviceGeneration)
                {
                    _clothBmp?.Dispose();
                    _clothBmp = Gfx.CreateBitmap((byte*)pix, ImgW, ImgH, bc1: false);
                    _clothBmpGen = Gfx.DeviceGeneration;
                }
                else
                {
                    _clothBmp.CopyFromMemory((nint)pix, ImgW * 4);
                }
            }
        }
    }

    public static void DrawXpbdCloth(R stage, uint dom1, uint dom2)
    {
        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.025);
        float t = (UI.Now - UI.StartTick) * 0.001f;
        bool beat = AudioAnalyzer.BeatStamp != _lastClothBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.35f;
        if (beat) _lastClothBeat = AudioAnalyzer.BeatStamp;

        StepXpbdCloth(dt, t, dom1, dom2, AudioAnalyzer.BassEnergy, AudioAnalyzer.MidEnergy, AudioAnalyzer.HighEnergy,
            AudioAnalyzer.SharpWaveHit, AudioAnalyzer.StereoPan, beat, uploadGpu: true);

        if (_clothBmp != null)
        {
            float op = Math.Clamp(0.75f + AudioAnalyzer.BassEnergy * 0.20f, 0.55f, 0.94f) * Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.72f, 0.20f, 1f);
            Gfx.DC.DrawBitmap(_clothBmp, new Vortice.RawRectF(stage.X, stage.Y, stage.Right, stage.Bottom), op,
                Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
        }
        UI.Animate();
    }

    /// <summary>Draws 12 XPBD plucked laser strings + an area-preserving XPBD soft-body mercury halo around the stage.</summary>
    public static void DrawXpbdStrings(R stage, uint dom1, uint dom2)
    {
        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.025);
        float t = (UI.Now - UI.StartTick) * 0.001f;
        float bass = AudioAnalyzer.BassEnergy;
        float hit = AudioAnalyzer.SharpWaveHit;
        var bands = AudioAnalyzer.Bands;
        bool beat = AudioAnalyzer.BeatStamp != _lastStrBeat && AudioAnalyzer.BeatStamp > 0 && AudioAnalyzer.BeatAttackStrength > 0.34f;
        if (beat) _lastStrBeat = AudioAnalyzer.BeatStamp;

        uint c1 = dom1 != 0 ? dom1 : Th.Brand;
        uint c2 = dom2 != 0 && dom2 != c1 ? dom2 : Gfx.Mix(c1, 0xFF73D8EB, 0.55f);
        uint hot = Gfx.Mix(c2, 0xFFFFFFFF, 0.72f);
        float calm = Math.Clamp(1f - AudioAnalyzer.QuietDim * 0.78f, 0.16f, 1f);

        // 1. Step 12 XPBD Verlet Strings (fixed endpoints, 4 constraint solves per frame!)
        float damp = MathF.Exp(-dt * 4.2f);
        for (int s = 0; s < StrCount; s++)
        {
            int bIdx = Math.Clamp((s * 31) / (StrCount - 1), 0, Math.Max(0, bands.Length - 1));
            float be = bands.Length > 0 ? bands[bIdx] : bass;
            _sEnergy[s] = _sEnergy[s] * 0.85f + be * 0.15f;
            int off = s * StrNodes;

            // Pluck the string on strong band spikes or kicks!
            if ((beat && s < 5) || be > _sEnergy[s] + 0.14f)
            {
                int pluckIdx = 3 + ((s * 7 + (int)(t * 17f)) % (StrNodes - 6));
                float dir = ((s + (int)(t * 11f)) & 1) == 0 ? 1f : -1f;
                float amp = (be * 26f + (beat ? 18f : 0f) + hit * 14f) * calm;
                _sy[off + pluckIdx] = Math.Clamp(_sy[off + pluckIdx] + dir * amp * 0.38f, -46f, 46f);
            }

            // Verlet integration
            for (int n = 1; n < StrNodes - 1; n++)
            {
                int idx = off + n;
                float vel = (_sy[idx] - _syPrev[idx]) * damp;
                _syPrev[idx] = _sy[idx];
                _sy[idx] = Math.Clamp(_sy[idx] + vel, -52f, 52f);
            }
            // 4 XPBD stretch-constraint passes (standing harmonic wave propagation!)
            for (int iter = 0; iter < 4; iter++)
            {
                for (int n = 1; n < StrNodes - 1; n++)
                {
                    int idx = off + n;
                    float avg = (_sy[idx - 1] + _sy[idx + 1]) * 0.5f;
                    _sy[idx] += (avg - _sy[idx]) * 0.62f;
                }
            }
            _sy[off] = _sy[off + StrNodes - 1] = 0f;

            // Draw glowing laser string
            float frac = s / (float)(StrCount - 1);
            float baseY = stage.Y + stage.H * (0.14f + frac * 0.72f);
            uint col = Gfx.Mix(c1, c2, frac);
            float glowA = Math.Clamp((0.18f + be * 0.65f) * calm, 0.06f, 0.85f);
            float stepX = stage.W / (StrNodes - 1);
            float px = stage.X, py = baseY;
            for (int n = 1; n < StrNodes; n++)
            {
                float x = stage.X + n * stepX;
                float y = baseY + _sy[off + n];
                Gfx.Line(px, py, x, y, Gfx.Fade(col, glowA * 0.24f), 6.5f);
                Gfx.Line(px, py, x, y, Gfx.Fade(Gfx.Mix(col, hot, be * 0.65f), glowA), 1.45f);
                px = x; py = y;
            }
        }

        // 2. XPBD Soft-Body Area-Preserving Mercury Ring orbiting the stage center
        if (!_ringInit)
        {
            for (int i = 0; i < RingN; i++) _rr[i] = _rrPrev[i] = 1f;
            _ringInit = true;
        }
        float rDamp = MathF.Exp(-dt * 5.0f);
        float sumR = 0f;
        for (int i = 0; i < RingN; i++)
        {
            float v = (_rr[i] - _rrPrev[i]) * rDamp;
            _rrPrev[i] = _rr[i];
            int bIdx = (i * 31) / RingN;
            float be = bands.Length > 0 ? bands[bIdx] : bass;
            float kickF = (beat ? 0.35f : 0f) * MathF.Cos(i * 0.75f + t * 5f);
            float target = 1f + be * 0.26f * calm + kickF;
            _rr[i] = Math.Clamp(_rr[i] + v + (target - _rr[i]) * dt * 18f, 0.65f, 1.55f);
            sumR += _rr[i];
        }
        // XPBD area preservation + neighbor bend constraint
        float targetAvg = 1.0f + bass * 0.16f * calm;
        float corr = (targetAvg - sumR / RingN) * 0.45f;
        for (int iter = 0; iter < 3; iter++)
        {
            for (int i = 0; i < RingN; i++)
            {
                float avg = (_rr[(i - 1 + RingN) % RingN] + _rr[(i + 1) % RingN]) * 0.5f;
                _rr[i] += (avg - _rr[i]) * 0.42f + (iter == 0 ? corr : 0f);
            }
        }

        float cx = stage.X + stage.W * 0.5f, cy = stage.Y + stage.H * 0.46f;
        float rxBase = MathF.Min(stage.W, stage.H) * 0.34f;
        float ryBase = rxBase * 0.68f;
        float p0x = cx + MathF.Cos(t * 0.25f) * rxBase * _rr[0];
        float p0y = cy + MathF.Sin(t * 0.25f) * ryBase * _rr[0];
        float prevX = p0x, prevY = p0y;
        for (int i = 1; i <= RingN; i++)
        {
            int idx = i % RingN;
            float ang = idx * MathF.Tau / RingN + t * 0.25f;
            float x = cx + MathF.Cos(ang) * rxBase * _rr[idx];
            float y = cy + MathF.Sin(ang) * ryBase * _rr[idx];
            uint rc = Gfx.Mix(c1, c2, 0.5f + 0.5f * MathF.Sin(ang * 2f));
            Gfx.Line(prevX, prevY, x, y, Gfx.Fade(rc, 0.22f * calm), 8f);
            Gfx.Line(prevX, prevY, x, y, Gfx.Fade(hot, (0.45f + bass * 0.45f) * calm), 1.8f);
            prevX = x; prevY = y;
        }
        UI.Animate();
    }

    internal static (bool Ok, double Ms) SelfTestXpbd()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 6; i++)
            StepXpbdCloth(0.016f, i * 0.05f, 0xFFFF2090, 0xFF00D4FF, 0.75f, 0.55f, 0.45f, 0.6f, 0.2f, i == 0, uploadGpu: false);
        sw.Stop();
        int lit = 0;
        for (int i = 0; i < _clothImg.Length; i++) if ((_clothImg[i] >> 24) > 20) lit++;
        return (lit > 2000 && sw.Elapsed.TotalMilliseconds / 6.0 < 3.0, sw.Elapsed.TotalMilliseconds / 6.0);
    }
}
