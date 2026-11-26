using System.Runtime.InteropServices;
using System.Text;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlpha = Vortice.DCommon.AlphaMode;

namespace FluxTube;

/// <summary>
/// Hardware Direct3D 11 HLSL Multi-FX &amp; 2000s Classic Visualizer Pack (1024x576 zero-copy IDXGISurface -> Direct2D):
/// - "milkdrop": Winamp MilkDrop / Geiss GPU feedback spiral warp + neon audio ring
/// - "plasma": Windows Media Player 2000s Alchemy / Plasma harmonic ribbons
/// - "tunnel": Demoparty / Winamp 3D Neon Hyper-Tunnel
/// - "kaleid": iTunes / Geiss 8-ray Fractal Kaleidoscope
/// - "synthwave": Outrun 3D Perspective Grid + Audio Mountains
/// - "water": 2D Wave Equation + caustics
/// - "ferro": Raymarched 3D Liquid Chrome with analytical SDF anti-aliasing
/// - "physarum": 3D Electric Neural Synapse Web with audio pulses
/// - "rd": Clean translucent Kaleidoscopic Cellular Automaton
/// - "fire": 1024x576 5-octave Domain-Warped Multi-Gradient GPU Fire + Volumetric Smoke Haze
/// </summary>
public static unsafe partial class GpuEffectsPack
{
    private const int OutW = 1024, OutH = 576;
    private const int WaterW = 384, WaterH = 216;
    private const int SimW = 512, SimH = 288;

    [StructLayout(LayoutKind.Sequential)]
    private struct FxCB
    {
        public float Time, Dt, Bass, Mid;
        public float High, Hit, Pan, Calm;
        public float R0, G0, B0, Param0;
        public float R1, G1, B1, Param1;
        public float R2, G2, B2, Beat;
        public float TexelX, TexelY, Aspect, Extra;
    }

    private sealed class RtPair : IDisposable
    {
        public ID3D11Texture2D TexA, TexB;
        public ID3D11RenderTargetView RtvA, RtvB;
        public ID3D11ShaderResourceView SrvA, SrvB;
        public int W, H;

        public RtPair(ID3D11Device dev, int w, int h, Format fmt)
        {
            W = w; H = h;
            var d = new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = fmt, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            TexA = dev.CreateTexture2D(d); TexB = dev.CreateTexture2D(d);
            RtvA = dev.CreateRenderTargetView(TexA); RtvB = dev.CreateRenderTargetView(TexB);
            SrvA = dev.CreateShaderResourceView(TexA); SrvB = dev.CreateShaderResourceView(TexB);
        }

        public void Swap()
        {
            (TexA, TexB) = (TexB, TexA);
            (RtvA, RtvB) = (RtvB, RtvA);
            (SrvA, SrvB) = (SrvB, SrvA);
        }

        public void Dispose()
        {
            SrvA.Dispose(); SrvB.Dispose(); RtvA.Dispose(); RtvB.Dispose(); TexA.Dispose(); TexB.Dispose();
        }
    }

    [LibraryImport("d3dcompiler_47.dll")]
    private static partial int D3DCompile(
        byte* pSrcData, nuint srcDataSize, byte* pSourceName,
        nint pDefines, nint pInclude, byte* pEntrypoint, byte* pTarget,
        uint flags1, uint flags2, out nint ppCode, out nint ppErrorMsgs);

    private static byte[] CompileHlsl(string source, string entry, string target)
    {
        var srcBytes = Encoding.UTF8.GetBytes(source);
        var entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
        var targetBytes = Encoding.ASCII.GetBytes(target + "\0");
        fixed (byte* pSrc = srcBytes, pEntry = entryBytes, pTgt = targetBytes)
        {
            int hr = D3DCompile(pSrc, (nuint)srcBytes.Length, null, 0, 0, pEntry, pTgt, 1 << 15, 0, out nint codeBlob, out nint errBlob);
            if (hr < 0 || codeBlob == 0)
            {
                string err = "";
                if (errBlob != 0)
                {
                    var vtbl = *(void***)errBlob;
                    var ptr = ((delegate* unmanaged[Stdcall]<nint, nint>)vtbl[3])(errBlob);
                    var sz = ((delegate* unmanaged[Stdcall]<nint, nuint>)vtbl[4])(errBlob);
                    err = Marshal.PtrToStringAnsi(ptr, (int)sz) ?? "";
                    ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(errBlob);
                }
                if (codeBlob != 0) { var cv = *(void***)codeBlob; ((delegate* unmanaged[Stdcall]<nint, uint>)cv[2])(codeBlob); }
                throw new InvalidOperationException($"GpuEffectsPack HLSL {entry}/{target} failed (0x{hr:X}): {err}");
            }
            try
            {
                var vtbl = *(void***)codeBlob;
                var ptr = ((delegate* unmanaged[Stdcall]<nint, nint>)vtbl[3])(codeBlob);
                var sz = (int)((delegate* unmanaged[Stdcall]<nint, nuint>)vtbl[4])(codeBlob);
                var data = new byte[sz];
                Marshal.Copy(ptr, data, 0, sz);
                return data;
            }
            finally { var vtbl = *(void***)codeBlob; ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(codeBlob); }
        }
    }

    private const string HlslSource = @"
cbuffer FxParams : register(b0)
{
    float time, dt, bass, mid;
    float high, hit, pan, calm;
    float3 col0; float param0;
    float3 col1; float param1;
    float3 col2; float beat;
    float2 texel; float aspect, extra;
};

Texture2D tex0 : register(t0);
SamplerState linClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS_Quad(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float2 hash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

float vnoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + float2(1, 0));
    float c = hash21(i + float2(0, 1));
    float d = hash21(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float fbm(float2 p)
{
    float s = 0.0, a = 0.5;
    float2x2 rot = float2x2(0.80, -0.60, 0.60, 0.80);
    for (int k = 0; k < 5; k++)
    {
        s += a * vnoise(p);
        p = mul(rot, p) * 2.04 + float2(1.7, 9.2);
        a *= 0.5;
    }
    return s;
}

float edgeFade(float2 uv)
{
    float2 e = smoothstep(0.0, 0.06, uv) * smoothstep(0.0, 0.06, 1.0 - uv);
    return e.x * e.y;
}

// ==================== 1. WINAMP MILKDROP / GEISS (GPU Feedback Spiral Warp!) ====================
float4 PS_MilkdropStep(VSOut i) : SV_Target
{
    float2 p = i.uv - 0.5;
    float rad = length(p);
    // Classic MilkDrop zoom-in + subtle spiral twist + harmonic ripple warp!
    float zoom = 0.980 - bass * 0.016 - hit * 0.010;
    float ang = (0.012 + pan * 0.018 + sin(time * 0.7) * 0.010) * calm;
    float cs = cos(ang), sn = sin(ang);
    float2 wp = float2(p.x * cs - p.y * sn, p.x * sn + p.y * cs) * zoom;
    wp += float2(sin(p.y * 8.0 + time * 2.4), cos(p.x * 8.0 - time * 2.1)) * (0.0022 + mid * 0.0042) * calm;
    float2 sampleUv = wp + 0.5;

    // Subtle RGB chromatic trail dispersion (iconic MilkDrop look!)
    float2 shift = normalize(p + 1e-4) * (0.0025 + bass * 0.004);
    float3 prev;
    prev.r = tex0.Sample(linClamp, sampleUv + shift).r;
    prev.g = tex0.Sample(linClamp, sampleUv).g;
    prev.b = tex0.Sample(linClamp, sampleUv - shift).b;
    prev *= (0.935 + 0.032 * calm);

    // Glowing audio-reactive waveform ring + rotating energy flares in center
    float2 ap = p * float2(aspect, 1.0);
    float r = length(ap);
    float a = atan2(ap.y, ap.x);
    float wave = sin(a * 6.0 + time * 4.5) * (0.025 + bass * 0.075)
               + cos(a * 12.0 - time * 7.2) * (0.012 + high * 0.045)
               + sin(a * 3.0 + time * 2.2) * (0.02 + mid * 0.04);
    float ringR = 0.24 + bass * 0.09 + hit * 0.05 + wave;
    float dRing = abs(r - ringR);
    float glow = exp(-dRing * 48.0) * (0.65 + bass * 0.95 + hit * 0.75) * calm;
    float core = exp(-dRing * 160.0) * (0.55 + high * 0.85) * calm;

    float cMix = 0.5 + 0.5 * sin(a * 2.0 + time * 1.4);
    float3 ringCol = lerp(lerp(col0, col1, cMix), col2, 0.5 + 0.5 * cos(a * 3.0 - time * 1.1));
    float3 addCol = ringCol * glow + lerp(ringCol, float3(1, 1, 1), 0.7) * core;

    // Beat burst stars along the ring
    if (beat > 0.5)
    {
        float spokes = pow(abs(sin(a * 4.0 + time * 3.0)), 12.0) * exp(-abs(r - ringR) * 18.0);
        addCol += lerp(col1, float3(1, 1, 1), 0.5) * spokes * (0.6 + bass * 0.8);
    }

    float3 outC = min(prev + addCol * 0.62, float3(2.2, 2.2, 2.2));
    return float4(outC, 1.0);
}

float4 PS_MilkdropDisplay(VSOut i) : SV_Target
{
    float3 c = tex0.Sample(linClamp, i.uv).rgb;
    c = c / (1.0 + 0.35 * c);
    float a = clamp(max(c.r, max(c.g, c.b)) * 1.12, 0.0, 0.92) * edgeFade(i.uv);
    return float4(c * a, a);
}

// ==================== 2. WMP 2000s ALCHEMY / PLASMA WAVES ====================
float4 PS_Plasma(VSOut i) : SV_Target
{
    float2 p = (i.uv - 0.5) * float2(aspect, 1.0) * 2.4;
    float t = time * (0.65 + bass * 0.35);
    float v1 = sin(p.x * 2.2 + t * 1.3 + sin(p.y * 1.8 - t));
    float v2 = sin(p.y * 2.5 - t * 1.1 + cos(p.x * 2.1 + t * 0.8));
    float v3 = sin(length(p - float2(pan * 0.6, sin(t * 0.5) * 0.3)) * (4.2 + bass * 2.2) - t * 2.6);
    float v4 = cos((p.x + p.y) * 2.0 + t * 1.7 + high * 2.0);
    float v = (v1 + v2 + v3 + v4) * 0.25;

    // WMP Alchemy luminous silk ribbon contours!
    float rib1 = exp(-abs(v) * (9.5 - bass * 3.5));
    float rib2 = exp(-abs(v - 0.38) * 11.0);
    float rib3 = exp(-abs(v + 0.38) * 11.0);

    float3 c = col0 * rib1 * (1.1 + bass * 0.7)
             + col1 * rib2 * (0.95 + mid * 0.6)
             + col2 * rib3 * (0.90 + high * 0.7);
    c += (col0 * 0.25 + col1 * 0.25) * (0.5 + 0.5 * v) * (0.35 + bass * 0.35);
    c = c / (1.0 + 0.32 * c);

    float a = clamp((rib1 * 0.78 + rib2 * 0.62 + rib3 * 0.58 + 0.14) * calm, 0.0, 0.88) * edgeFade(i.uv);
    return float4(c * a, a);
}

// ==================== 3. 2000s 3D NEON HYPER-TUNNEL ====================
float4 PS_Tunnel(VSOut i) : SV_Target
{
    float2 p = (i.uv - 0.5) * float2(aspect, 1.0) * 2.0;
    p += float2(sin(time * 0.7) * 0.18 + pan * 0.22, cos(time * 0.55) * 0.14);
    float r = max(length(p), 0.04);
    float ang = atan2(p.y, p.x);

    float depth = 1.0 / r;
    float travel = time * (2.2 + bass * 1.8) + hit * 0.6;
    float twist = ang + depth * (0.28 + sin(time * 0.4) * 0.16);

    // Glowing rings + longitudinal neon rails
    float ringGrid = pow(0.5 + 0.5 * sin(depth * 4.5 - travel * 3.0), 8.0);
    float railGrid = pow(0.5 + 0.5 * sin(twist * 8.0), 10.0);
    float pulseWave = pow(0.5 + 0.5 * sin(depth * 2.0 - travel * 5.0), 4.0) * (0.4 + bass * 1.2);
    float fog = smoothstep(0.04, 0.42, r);

    float3 c = col0 * ringGrid * (1.1 + bass * 0.8)
             + col1 * railGrid * (0.95 + high * 0.85)
             + col2 * pulseWave * 0.65;
    // Central warp core glow
    c += lerp(col1, float3(1, 1, 1), 0.55) * exp(-r * 4.2) * (0.55 + bass * 0.75);
    c *= fog * calm;
    c = c / (1.0 + 0.32 * c);

    float a = clamp(max(c.r, max(c.g, c.b)) * 1.15, 0.0, 0.90) * edgeFade(i.uv);
    return float4(c * a, a);
}

// ==================== 4. ITUNES / GEISS 8-RAY FRACTAL KALEIDOSCOPE ====================
float4 PS_Kaleidoscope(VSOut i) : SV_Target
{
    float2 p = (i.uv - 0.5) * float2(aspect, 1.0) * 2.2;
    float ang = atan2(p.y, p.x) + time * (0.22 + pan * 0.18);
    float r = length(p);
    // 8-fold mirror symmetry!
    float sector = 3.14159265 / 4.0;
    ang = abs(fmod(abs(ang), sector * 2.0) - sector);
    float2 kp = float2(cos(ang), sin(ang)) * r;

    float3 acc = 0;
    for (int k = 0; k < 4; k++)
    {
        float fk = (float)k;
        kp = abs(kp) / max(dot(kp, kp), 0.18) - float2(0.85 + bass * 0.28, 0.65 + mid * 0.25);
        float rot = time * 0.35 + fk * 0.7;
        float cs = cos(rot), sn = sin(rot);
        kp = float2(kp.x * cs - kp.y * sn, kp.x * sn + kp.y * cs);
        float d = exp(-abs(length(kp) - 0.45) * (5.5 - high * 1.8));
        float3 kc = (k == 0) ? col0 : ((k == 1) ? col1 : ((k == 2) ? col2 : lerp(col0, col2, 0.5)));
        acc += kc * d * (0.45 + bass * 0.35);
    }
    acc *= smoothstep(1.9, 0.2, r) * calm;
    acc = acc / (1.0 + 0.38 * acc);
    float a = clamp(max(acc.r, max(acc.g, acc.b)) * 1.05, 0.0, 0.88) * edgeFade(i.uv);
    return float4(acc * a, a);
}

// ==================== 5. OUTRUN / SYNTHWAVE 3D PERSPECTIVE GRID ====================
float4 PS_Synthwave(VSOut i) : SV_Target
{
    float2 uv = i.uv;
    float horizon = 0.48 + sin(time * 0.6) * 0.02;
    float3 c = 0;
    float a = 0;

    if (uv.y > horizon)
    {
        // Perspective 3D ground plane with audio-reactive bass mountain elevation!
        float dy = max(uv.y - horizon, 0.004);
        float z = 0.35 / dy;
        float x = (uv.x - 0.5) * aspect * z;
        float scrollZ = z + time * (2.6 + bass * 2.4);

        float gx = abs(frac(x * 0.75 + 0.5) - 0.5) / max(0.015, fwidth(x * 0.75) * 1.2 + 0.012);
        float gz = abs(frac(scrollZ * 0.55) - 0.5) / max(0.015, fwidth(scrollZ * 0.55) * 1.2 + 0.012);
        float lineX = exp(-gx * 2.2);
        float lineZ = exp(-gz * 2.2);
        float grid = max(lineX, lineZ);
        float distFade = exp(-z * 0.14) * smoothstep(0.0, 0.05, dy);

        // Side audio-reactive mountain glow
        float mtn = smoothstep(0.6, 2.4, abs(x)) * (0.35 + bass * 0.95);
        c = lerp(col0, col1, clamp(z * 0.12, 0.0, 1.0)) * grid * (1.25 + bass * 0.85) * distFade;
        c += col2 * mtn * grid * 0.45 * distFade;
        a = clamp((grid * 0.85 + 0.08) * distFade * calm, 0.0, 0.90);
    }
    else
    {
        // Retro sliced sun + glowing horizon laser line!
        float2 sp = (uv - float2(0.5 + pan * 0.06, horizon - 0.12)) * float2(aspect, 1.0);
        float sr = length(sp);
        float sunR = 0.21 + bass * 0.032;
        float sunMask = smoothstep(sunR + 0.015, sunR - 0.008, sr);
        float stripes = smoothstep(0.15, 0.45, frac((uv.y - horizon) * 28.0 + time * 0.8) + (horizon - uv.y) * 2.2);
        float sun = sunMask * stripes;
        float halo = exp(-sr * 4.8) * (0.48 + bass * 0.45);
        float horLine = exp(-abs(uv.y - horizon) * 65.0) * (0.9 + hit * 0.9);
        float3 sunCol = lerp(col1, col0, clamp((horizon - uv.y) * 3.5, 0.0, 1.0));
        c = sunCol * (sun * 1.25 + halo) + lerp(col2, float3(1, 1, 1), 0.5) * horLine;
        a = clamp((sun * 0.88 + halo * 0.65 + horLine * 0.85) * calm, 0.0, 0.90);
    }

    c = c / (1.0 + 0.30 * c);
    a *= edgeFade(uv);
    return float4(c * a, a);
}

// ==================== 6. WATER RIPPLES + CAUSTICS ====================
float4 PS_WaterStep(VSOut i) : SV_Target
{
    float2 c = tex0.Sample(linClamp, i.uv).rg;
    float l = tex0.Sample(linClamp, i.uv - float2(texel.x, 0)).r;
    float r = tex0.Sample(linClamp, i.uv + float2(texel.x, 0)).r;
    float t = tex0.Sample(linClamp, i.uv - float2(0, texel.y)).r;
    float b = tex0.Sample(linClamp, i.uv + float2(0, texel.y)).r;

    float h = ((l + r + t + b) * 0.5 - c.g) * (0.972 + 0.014 * calm);
    float2 p0 = float2(0.5 + 0.34 * sin(time * 0.85) + pan * 0.10, 0.5 + 0.28 * cos(time * 1.12));
    float2 p1 = float2(0.5 - 0.36 * cos(time * 0.72 + 1.3), 0.5 + 0.26 * sin(time * 0.94));
    float2 d0 = (i.uv - p0) * float2(aspect, 1.0);
    float2 d1 = (i.uv - p1) * float2(aspect, 1.0);
    h += exp(-dot(d0, d0) / 0.0012) * (0.14 + bass * 0.42 + hit * 0.35) * calm * sin(time * 18.0);
    h += exp(-dot(d1, d1) / 0.0010) * (0.10 + mid * 0.36 + high * 0.25) * calm * cos(time * 15.0);
    if (beat > 0.5)
    {
        float2 db = (i.uv - float2(param0, param1)) * float2(aspect, 1.0);
        h += exp(-dot(db, db) / 0.0028) * (0.55 + bass * 0.65) * calm;
    }
    return float4(clamp(h, -1.8, 1.8), c.r, 0, 1);
}

float4 PS_WaterDisplay(VSOut i) : SV_Target
{
    float l = tex0.Sample(linClamp, i.uv - float2(texel.x, 0)).r;
    float r = tex0.Sample(linClamp, i.uv + float2(texel.x, 0)).r;
    float t = tex0.Sample(linClamp, i.uv - float2(0, texel.y)).r;
    float b = tex0.Sample(linClamp, i.uv + float2(0, texel.y)).r;
    float h = tex0.Sample(linClamp, i.uv).r;
    float3 n = normalize(float3((l - r) * 3.8, (t - b) * 3.8, 1.0));
    float ndl = max(0.0, dot(n, normalize(float3(-0.35, -0.55, 0.75))));
    float spec = pow(ndl, 28.0) * 1.15;
    float caustic = pow(clamp(1.0 - abs(n.x) - abs(n.y), 0.0, 1.0), 4.0) * abs(h) * 2.2;
    float fresnel = pow(1.0 - n.z, 2.5);
    float3 waterCol = lerp(col0, col1, clamp(0.5 + n.x * 1.4 + n.y * 0.8, 0.0, 1.0));
    float3 c = waterCol * (caustic * 0.85 + fresnel * 1.35 + abs(h) * 0.55) + spec * lerp(waterCol, float3(1, 1, 1), 0.75);
    c = c / (1.0 + 0.35 * c);
    float a = clamp(max(c.r, max(c.g, c.b)) * 1.15, 0.0, 0.88) * edgeFade(i.uv);
    return float4(c * a, a);
}

// ==================== 7. FERROFLUID / LIQUID CHROME (Analytical SDF Anti-Aliasing) ====================
float smin(float a, float b, float k)
{
    float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return lerp(b, a, h) - k * h * (1.0 - h);
}

float ferroMap(float3 p)
{
    float d = 10.0;
    float pulse = 1.0 + bass * 0.35 + hit * 0.22;
    for (int k = 0; k < 8; k++)
    {
        float fk = (float)k;
        float ang = time * (0.45 + fk * 0.09) + fk * 1.9;
        float rad = (0.55 + 0.38 * sin(time * 0.7 + fk * 2.1)) * (0.85 + bass * 0.32);
        float3 c = float3(cos(ang) * rad * 1.35 + pan * 0.25, sin(ang * 1.2 + fk) * rad * 0.72, sin(ang * 0.8 - fk * 1.3) * 0.45);
        float r = (0.24 + 0.08 * sin(time * 1.7 + fk * 3.1)) * pulse;
        d = smin(d, length(p - c) - r, 0.38);
    }
    float spike = sin(p.x * 9.0 + time * 3.0) * sin(p.y * 9.0 - time * 2.4) * sin(p.z * 9.0) * (0.025 + high * 0.065 + bass * 0.045) * calm;
    return d + spike;
}

float3 ferroNormal(float3 p)
{
    float2 e = float2(0.008, 0.0);
    return normalize(float3(
        ferroMap(p + e.xyy) - ferroMap(p - e.xyy),
        ferroMap(p + e.yxy) - ferroMap(p - e.yxy),
        ferroMap(p + e.yyx) - ferroMap(p - e.yyx)));
}

float4 PS_Ferro(VSOut i) : SV_Target
{
    float2 uv = (i.uv - 0.5) * float2(aspect, 1.0) * 2.0;
    float3 ro = float3(0.0, 0.0, -2.85);
    float3 rd = normalize(float3(uv, 1.65));
    float tRay = 0.0, minDist = 10.0;
    bool hitSurf = false;
    for (int s = 0; s < 36; s++)
    {
        float d = ferroMap(ro + rd * tRay);
        minDist = min(minDist, d);
        if (d < 0.006) { hitSurf = true; break; }
        tRay += d * 0.85;
        if (tRay > 5.5) break;
    }
    float3 c = 0; float a = 0;
    float aaEdge = smoothstep(0.022, 0.002, minDist);
    float halo = exp(-max(0.0, minDist) * 5.8) * (0.30 + bass * 0.32) * calm;
    float3 haloCol = lerp(col0, col1, i.uv.x) * halo;
    if (hitSurf || aaEdge > 0.01)
    {
        float3 p = ro + rd * tRay;
        float3 n = ferroNormal(p);
        float3 ref = reflect(rd, n);
        float fres = pow(1.0 - max(0.0, dot(n, -rd)), 2.8);
        float3 env = lerp(col0 * 0.35, col1 * 1.15, clamp(ref.y * 0.5 + 0.5, 0.0, 1.0));
        float spec1 = pow(max(0.0, dot(ref, normalize(float3(-0.5, 0.7, -0.5)))), 24.0);
        float spec2 = pow(max(0.0, dot(ref, normalize(float3(0.6, -0.4, -0.6)))), 14.0);
        float3 chromaRim = float3(lerp(col0.r, 1.0, fres), lerp(col1.g, 0.9, fres * 0.85), lerp(col1.b, 1.0, fres * 1.1)) * fres * (1.20 + bass * 0.60);
        float3 surfCol = env * (0.55 + 0.45 * calm) + chromaRim + spec1 * 1.25 + spec2 * col0 * 0.85;
        surfCol = surfCol / (1.0 + 0.32 * surfCol);
        float cov = hitSurf ? 1.0 : aaEdge;
        c = lerp(haloCol, surfCol, cov);
        a = lerp(clamp(halo * 1.1, 0.0, 0.55), 0.88, cov);
    }
    else { c = haloCol; a = clamp(halo * 1.1, 0.0, 0.55); }
    a *= edgeFade(i.uv);
    return float4(c * a, a);
}

// ==================== 8. ELECTRIC 3D NEURAL SYNAPSE WEB (Guaranteed Vivid 1024p!) ====================
float segDist(float2 p, float2 a, float2 b)
{
    float2 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-5), 0.0, 1.0);
    return length(pa - ba * h);
}

float2 neuralNode(float2 cell, float t)
{
    float2 h = hash22(cell);
    return cell + 0.5 + 0.36 * sin(t * (0.65 + h * 0.7) + h * 6.2831);
}

float4 PS_NeuralWeb(VSOut i) : SV_Target
{
    float2 uv = (i.uv - 0.5) * float2(aspect, 1.0);
    float3 total = 0;

    // 2 parallax depth layers of live firing neurons & synaptic arcs!
    for (int layer = 0; layer < 2; layer++)
    {
        float scale = (layer == 0) ? 4.2 : 7.0;
        float t = time * (layer == 0 ? 0.85 : 1.25) + layer * 13.7;
        float2 p = uv * scale + float2(pan * 0.45, time * 0.14);
        float2 id = floor(p);
        float2 centerPt = neuralNode(id, t);

        float layerGlow = 0.0, nodeGlow = 0.0, pulseGlow = 0.0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                float2 nbId = id + float2(dx, dy);
                float2 nbPt = neuralNode(nbId, t);
                float dNode = length(p - nbPt);
                float fire = 0.5 + 0.5 * sin(t * 2.5 + hash21(nbId) * 12.0);
                nodeGlow += exp(-dNode * 16.0) * (0.45 + fire * (0.65 + bass * 0.85));

                if (dx != 0 || dy != 0)
                {
                    float len = length(nbPt - centerPt);
                    if (len < 1.35)
                    {
                        float dSeg = segDist(p, centerPt, nbPt);
                        float fadeLen = smoothstep(1.35, 0.55, len);
                        layerGlow += exp(-dSeg * 32.0) * fadeLen * (0.35 + mid * 0.65);

                        // Travelling synaptic electric spark along the axon!
                        float sparkT = frac(time * (0.65 + bass * 0.85) + hash21(nbId + id));
                        float2 sparkPos = lerp(centerPt, nbPt, sparkT);
                        pulseGlow += exp(-length(p - sparkPos) * 18.0) * fadeLen * (0.65 + high * 1.1 + hit * 0.8);
                    }
                }
            }
        }
        float w = (layer == 0) ? 1.0 : 0.55;
        float3 lc = (layer == 0) ? col0 : col1;
        total += (lc * layerGlow + lerp(lc, col2, 0.5) * nodeGlow + lerp(col2, float3(1, 1, 1), 0.7) * pulseGlow) * w;
    }

    total *= calm;
    total = total / (1.0 + 0.35 * total);
    float a = clamp(max(total.r, max(total.g, total.b)) * 1.15, 0.0, 0.90) * edgeFade(i.uv);
    return float4(total * a, a);
}

// ==================== 9. CLEAN KALEIDOSCOPIC CELLULAR AUTOMATON (Transparent Glowing Waves!) ====================
float4 PS_CaInit(VSOut i) : SV_Target
{
    float2 p = (i.uv - 0.5) * float2(aspect, 1.0);
    float d = length(p);
    float init = (abs(d - 0.20) < 0.018) ? 1.0 : 0.0;
    return float4(init, 0.0, 0.0, 1.0);
}

float4 PS_CaStep(VSOut i) : SV_Target
{
    // Brian's Brain / Excitable Medium (3-state: r = firing 1.0, g = refractory 0..1) -> crisp propagating geometric waves on dark background!
    float2 st = tex0.Sample(linClamp, i.uv).rg;
    float firing = st.r;
    float refrac = st.g;

    float nb = 0.0;
    nb += tex0.Sample(linClamp, i.uv + float2(-texel.x,  0)).r;
    nb += tex0.Sample(linClamp, i.uv + float2( texel.x,  0)).r;
    nb += tex0.Sample(linClamp, i.uv + float2( 0, -texel.y)).r;
    nb += tex0.Sample(linClamp, i.uv + float2( 0,  texel.y)).r;
    nb += tex0.Sample(linClamp, i.uv + float2(-texel.x, -texel.y)).r * 0.7;
    nb += tex0.Sample(linClamp, i.uv + float2( texel.x, -texel.y)).r * 0.7;
    nb += tex0.Sample(linClamp, i.uv + float2(-texel.x,  texel.y)).r * 0.7;
    nb += tex0.Sample(linClamp, i.uv + float2( texel.x,  texel.y)).r * 0.7;

    float nextFire = 0.0;
    float nextRefrac = refrac * (0.80 + 0.08 * calm);

    if (firing > 0.5)
    {
        nextFire = 0.0;
        nextRefrac = 1.0;
    }
    else if (refrac < 0.14)
    {
        // Clean propagating wave rings modulated by music!
        float lo = 0.85 - bass * 0.25;
        float hi = 2.45 + high * 0.55;
        if (nb > lo && nb < hi) nextFire = 1.0;
    }

    // Symmetrical 4-way kaleidoscopic beat seeds so clean rings & mandalas constantly blossom!
    if (beat > 0.5 || (bass > 0.45 && frac(time * 2.2) < 0.12))
    {
        float2 s0 = float2(param0, param1);
        float2 s1 = float2(1.0 - param0, param1);
        float2 s2 = float2(param0, 1.0 - param1);
        float2 s3 = float2(1.0 - param0, 1.0 - param1);
        float dMin = min(min(length((i.uv - s0) * float2(aspect, 1.0)), length((i.uv - s1) * float2(aspect, 1.0))),
                         min(length((i.uv - s2) * float2(aspect, 1.0)), length((i.uv - s3) * float2(aspect, 1.0))));
        if (dMin < 0.016 + bass * 0.010) nextFire = 1.0;
    }
    return float4(nextFire, nextRefrac, 0.0, 1.0);
}

float4 PS_CaDisplay(VSOut i) : SV_Target
{
    float2 st = tex0.Sample(linClamp, i.uv).rg;
    float f = st.r;
    float r = st.g;
    // Soft bloom of neighbors
    float glow = (tex0.Sample(linClamp, i.uv + float2(texel.x * 2, 0)).g
                + tex0.Sample(linClamp, i.uv - float2(texel.x * 2, 0)).g
                + tex0.Sample(linClamp, i.uv + float2(0, texel.y * 2)).g
                + tex0.Sample(linClamp, i.uv - float2(0, texel.y * 2)).g) * 0.25;

    float3 c = col0 * r * 0.75 + col1 * glow * 0.55 + lerp(col2, float3(1, 1, 1), 0.72) * f * (1.1 + bass * 0.5);
    c = c / (1.0 + 0.35 * c);
    float a = clamp((f * 0.85 + r * 0.48 + glow * 0.30) * calm, 0.0, 0.84) * edgeFade(i.uv);
    return float4(c * a, a);
}

// ==================== 10. VOLUMETRIC MULTI-GRADIENT 5-OCTAVE GPU FIRE + SMOKE HAZE ====================
float4 PS_GpuFire(VSOut i) : SV_Target
{
    float2 uv = i.uv;
    float t = time * (1.35 + bass * 0.95 + hit * 0.65);

    // Distance from left & right walls + bottom edge -> flames & translucent smoke curl inward and upward!
    float sideL = clamp(1.0 - uv.x / (0.34 + bass * 0.14 + hit * 0.08), 0.0, 1.0);
    float sideR = clamp(1.0 - (1.0 - uv.x) / (0.34 + bass * 0.14 + hit * 0.08), 0.0, 1.0);
    float bot = clamp(1.0 - (1.0 - uv.y) / (0.28 + bass * 0.12), 0.0, 1.0);

    // 5-octave domain-warped FBM turbulence for razor-sharp wispy flames + filling atmospheric haze!
    float2 p = float2(uv.x * aspect * 3.2, uv.y * 3.2 + t * 0.95);
    float2 warp = float2(fbm(p + float2(0.0, t * 0.4)), fbm(p + float2(5.2, 1.3 + t * 0.55)));
    float n1 = fbm(p + warp * (1.55 + bass * 0.65));
    float n2 = fbm(p * 1.85 - warp * 1.2 + float2(0.0, t * 1.15));

    // Left flame tinted with col0->col2, Right flame tinted with col1->col2, seamlessly blending in the middle!
    float flameL = pow(sideL, 1.35) * (0.45 + n1 * 1.25) * (0.65 + 0.35 * sin(uv.y * 3.14159));
    float flameR = pow(sideR, 1.35) * (0.45 + n2 * 1.25) * (0.65 + 0.35 * sin(uv.y * 3.14159));
    float flameB = pow(bot, 1.8) * (0.35 + n1 * 0.95) * (0.55 + bass * 0.75);
    // Soft filling ambient smoke haze drifting across the whole stage
    float haze = smoothstep(0.32, 0.85, n1 * 0.65 + n2 * 0.35) * (0.16 + bass * 0.16) * calm;

    float dL = flameL * (0.75 + bass * 0.65 + hit * 0.45) * calm;
    float dR = flameR * (0.75 + mid * 0.65 + hit * 0.45) * calm;
    float dB = flameB * (0.65 + bass * 0.55) * calm;

    // Rich 3-color gradient extracted from the video/cover palette!
    float3 cL = lerp(col0, col2, clamp(dL * 0.85, 0.0, 1.0));
    float3 cR = lerp(col1, col2, clamp(dR * 0.85, 0.0, 1.0));
    float3 cB = lerp(col0, col1, uv.x);
    float3 cHaze = lerp(col0, col1, uv.x) * 0.65 + col2 * 0.35;

    float3 c = cL * dL + cR * dR + cB * dB + cHaze * haze;
    float density = dL + dR + dB + haze;
    // Subtle luminous ember core only in thin high-turbulence filaments (ZERO flat white clipping!)
    float hotCore = smoothstep(0.75, 1.55, density * n2 * 1.45);
    c += lerp(col2, float3(1.0, 0.96, 0.88), 0.55) * hotCore * 0.45;
    c = c / (1.0 + 0.45 * c);

    float a = clamp(density / (1.0 + 0.55 * density), 0.0, 0.84) * edgeFade(uv);
    return float4(c * a, a);
}
";

    private static int _devGen = -1;
    private static bool _failed;
    private static ID3D11VertexShader? _vsQuad;
    private static ID3D11PixelShader? _psMilkStep, _psMilkDisp, _psPlasma, _psTunnel, _psKaleid, _psSynth;
    private static ID3D11PixelShader? _psWaterStep, _psWaterDisp, _psFerro, _psNeural;
    private static ID3D11PixelShader? _psCaInit, _psCaStep, _psCaDisp, _psGpuFire;
    private static ID3D11Buffer? _cb;
    private static ID3D11SamplerState? _sampler;
    private static ID3D11RasterizerState? _rs;
    private static ID3D11BlendState? _bsOpaque;

    private static ID3D11Texture2D? _outTex;
    private static ID3D11RenderTargetView? _outRtv;
    private static ID2D1Bitmap1? _d2dBmp;

    private static RtPair? _waterRt, _milkRt, _caRt;
    private static bool _caReady;

    private static string? _reqMode;
    private static uint _reqDom1, _reqDom2;
    private static float _reqBass, _reqMid, _reqHigh, _reqHit, _reqPan, _reqQuiet;
    private static bool _reqBeat;
    private static long _lastStepMs;
    private static int _seed;

    public static ID2D1Bitmap1? OutputBitmap => _d2dBmp;

    public static void RequestStep(string mode, uint dom1, uint dom2, float bass, float mid, float high, float hit, float pan, float quietDim, bool beatHit)
    {
        _reqMode = mode;
        _reqDom1 = dom1; _reqDom2 = dom2;
        _reqBass = bass; _reqMid = mid; _reqHigh = high;
        _reqHit = hit; _reqPan = pan; _reqQuiet = quietDim;
        if (beatHit) _reqBeat = true;
    }

    public static void DisposeResources()
    {
        _d2dBmp?.Dispose(); _d2dBmp = null;
        _outRtv?.Dispose(); _outRtv = null;
        _outTex?.Dispose(); _outTex = null;
        _waterRt?.Dispose(); _waterRt = null;
        _milkRt?.Dispose(); _milkRt = null;
        _caRt?.Dispose(); _caRt = null;
        _caReady = false;
        _vsQuad?.Dispose(); _vsQuad = null;
        _psMilkStep?.Dispose(); _psMilkStep = null;
        _psMilkDisp?.Dispose(); _psMilkDisp = null;
        _psPlasma?.Dispose(); _psPlasma = null;
        _psTunnel?.Dispose(); _psTunnel = null;
        _psKaleid?.Dispose(); _psKaleid = null;
        _psSynth?.Dispose(); _psSynth = null;
        _psWaterStep?.Dispose(); _psWaterStep = null;
        _psWaterDisp?.Dispose(); _psWaterDisp = null;
        _psFerro?.Dispose(); _psFerro = null;
        _psNeural?.Dispose(); _psNeural = null;
        _psCaInit?.Dispose(); _psCaInit = null;
        _psCaStep?.Dispose(); _psCaStep = null;
        _psCaDisp?.Dispose(); _psCaDisp = null;
        _psGpuFire?.Dispose(); _psGpuFire = null;
        _cb?.Dispose(); _cb = null;
        _sampler?.Dispose(); _sampler = null;
        _rs?.Dispose(); _rs = null;
        _bsOpaque?.Dispose(); _bsOpaque = null;
        _devGen = -1;
        _failed = false;
    }

    private static bool EnsureInit()
    {
        if (_failed) return false;
        if (_d2dBmp != null && _devGen == Gfx.DeviceGeneration) return true;
        DisposeResources();
        try
        {
            var dev = Gfx.D3DDevice;
            _vsQuad = dev.CreateVertexShader(CompileHlsl(HlslSource, "VS_Quad", "vs_5_0"));
            _psMilkStep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_MilkdropStep", "ps_5_0"));
            _psMilkDisp = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_MilkdropDisplay", "ps_5_0"));
            _psPlasma = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Plasma", "ps_5_0"));
            _psTunnel = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Tunnel", "ps_5_0"));
            _psKaleid = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Kaleidoscope", "ps_5_0"));
            _psSynth = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Synthwave", "ps_5_0"));
            _psWaterStep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_WaterStep", "ps_5_0"));
            _psWaterDisp = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_WaterDisplay", "ps_5_0"));
            _psFerro = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Ferro", "ps_5_0"));
            _psNeural = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_NeuralWeb", "ps_5_0"));
            _psCaInit = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_CaInit", "ps_5_0"));
            _psCaStep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_CaStep", "ps_5_0"));
            _psCaDisp = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_CaDisplay", "ps_5_0"));
            _psGpuFire = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_GpuFire", "ps_5_0"));

            _cb = dev.CreateBuffer(new BufferDescription((uint)sizeof(FxCB), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _sampler = dev.CreateSamplerState(new SamplerDescription(Vortice.Direct3D11.Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _rs = dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid));
            _bsOpaque = dev.CreateBlendState(new Vortice.Direct3D11.BlendDescription(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.Zero));

            _outTex = dev.CreateTexture2D(new Texture2DDescription
            {
                Width = OutW, Height = OutH, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _outRtv = dev.CreateRenderTargetView(_outTex);
            dev.ImmediateContext.ClearRenderTargetView(_outRtv, new Color4(0, 0, 0, 0));

            using var surf = _outTex.QueryInterface<IDXGISurface>();
            _d2dBmp = Gfx.DC.CreateBitmapFromDxgiSurface(surf,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
            _devGen = Gfx.DeviceGeneration;
            return true;
        }
        catch (Exception e)
        {
            Services.Paths.Log("GpuEffectsPack init failed: " + e.Message);
            DisposeResources();
            _failed = true;
            return false;
        }
    }

    private static (float r, float g, float b) RotateHue(float r, float g, float b, float rad)
    {
        float cosA = MathF.Cos(rad), sinA = MathF.Sin(rad);
        const float k = 1f / 3f;
        float sq = MathF.Sqrt(k);
        float m00 = cosA + (1f - cosA) * k;
        float m01 = k * (1f - cosA) - sq * sinA;
        float m02 = k * (1f - cosA) + sq * sinA;
        return (Math.Clamp(r * m00 + g * m01 + b * m02, 0.03f, 1f),
                Math.Clamp(r * m02 + g * m00 + b * m01, 0.03f, 1f),
                Math.Clamp(r * m01 + g * m02 + b * m00, 0.03f, 1f));
    }

    private static void QuadPass(ID3D11DeviceContext ctx, ID3D11PixelShader ps, ID3D11RenderTargetView rtv, int w, int h,
        ID3D11ShaderResourceView? s0, in FxCB cb)
    {
        var map = ctx.Map(_cb!, 0, MapMode.WriteDiscard);
        *(FxCB*)map.DataPointer = cb;
        ctx.Unmap(_cb!, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vsQuad);
        ctx.OMSetBlendState(_bsOpaque);
        ctx.RSSetViewport(0, 0, w, h);
        ctx.OMSetRenderTargets(rtv);
        ctx.PSSetShader(ps);
        ctx.PSSetShaderResource(0, s0!);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null!);
    }

    public static void StepBeforeBeginDraw()
    {
        var mode = _reqMode;
        if (mode == null) return;
        _reqMode = null;
        long now = UI.Now;
        if (now - _lastStepMs < 14) return;
        float dt = _lastStepMs == 0 ? 0.016f : Math.Clamp((now - _lastStepMs) * 0.001f, 0.008f, 0.025f);
        _lastStepMs = now;
        bool beat = _reqBeat; _reqBeat = false;
        ExecuteStep(mode, (now - UI.StartTick) * 0.001f, dt, _reqDom1, _reqDom2, _reqBass, _reqMid, _reqHigh, _reqHit, _reqPan, _reqQuiet, beat);
    }

    private static void ExecuteStep(string mode, float t, float dt, uint dom1, uint dom2,
        float bass, float mid, float high, float hit, float pan, float quietDim, bool beatHit)
    {
        if (!EnsureInit()) return;
        var dev = Gfx.D3DDevice;
        var ctx = dev.ImmediateContext;
        ctx.IASetInputLayout(null);
        ctx.RSSetState(_rs);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.PSSetSampler(0, _sampler);

        // Rich evolving 3-color triad anchored to cover art (always multi-hue violet/cyan/gold/cover gradient — never flat monochrome!)
        uint c1 = dom1 != 0 ? dom1 : Th.Brand;
        var (br, bg, bb) = MusicStageEffects.VividPaletteColor(c1);
        var (r0, g0, b0) = RotateHue(br, bg, bb, MathF.Sin(t * 0.15f) * 0.55f);
        var (r1, g1, b1) = RotateHue(br, bg, bb, 2.15f + MathF.Cos(t * 0.20f) * 0.55f);
        var (r2, g2, b2) = RotateHue(br, bg, bb, -2.10f + MathF.Sin(t * 0.24f) * 0.55f);
        float calm = Math.Clamp(1f - quietDim * 0.88f, 0.08f, 1f);

        int sd = beatHit ? ++_seed : _seed;
        float bx = 0.22f + ((sd * 0.371f) % 0.56f);
        float by = 0.22f + ((sd * 0.619f) % 0.56f);

        var cb = new FxCB
        {
            Time = t, Dt = dt, Bass = bass, Mid = mid,
            High = high, Hit = hit, Pan = pan, Calm = calm,
            R0 = r0, G0 = g0, B0 = b0, Param0 = bx,
            R1 = r1, G1 = g1, B1 = b1, Param1 = by,
            R2 = r2, G2 = g2, B2 = b2, Beat = beatHit ? 1f : 0f,
            TexelX = 1f / OutW, TexelY = 1f / OutH, Aspect = (float)OutW / OutH,
        };

        switch (mode)
        {
            case "milkdrop":
                if (_milkRt == null)
                {
                    _milkRt = new RtPair(dev, OutW, OutH, Format.R16G16B16A16_Float);
                    ctx.ClearRenderTargetView(_milkRt.RtvA, new Color4(0, 0, 0, 0));
                    ctx.ClearRenderTargetView(_milkRt.RtvB, new Color4(0, 0, 0, 0));
                }
                QuadPass(ctx, _psMilkStep!, _milkRt.RtvB, OutW, OutH, _milkRt.SrvA, cb);
                _milkRt.Swap();
                QuadPass(ctx, _psMilkDisp!, _outRtv!, OutW, OutH, _milkRt.SrvA, cb);
                break;
            case "plasma":
                QuadPass(ctx, _psPlasma!, _outRtv!, OutW, OutH, null, cb);
                break;
            case "tunnel":
                QuadPass(ctx, _psTunnel!, _outRtv!, OutW, OutH, null, cb);
                break;
            case "kaleid":
                QuadPass(ctx, _psKaleid!, _outRtv!, OutW, OutH, null, cb);
                break;
            case "synthwave":
                QuadPass(ctx, _psSynth!, _outRtv!, OutW, OutH, null, cb);
                break;
            case "water":
                if (_waterRt == null)
                {
                    _waterRt = new RtPair(dev, WaterW, WaterH, Format.R16G16_Float);
                    ctx.ClearRenderTargetView(_waterRt.RtvA, new Color4(0, 0, 0, 0));
                    ctx.ClearRenderTargetView(_waterRt.RtvB, new Color4(0, 0, 0, 0));
                }
                cb.TexelX = 1f / WaterW; cb.TexelY = 1f / WaterH;
                QuadPass(ctx, _psWaterStep!, _waterRt.RtvB, WaterW, WaterH, _waterRt.SrvA, cb);
                _waterRt.Swap();
                QuadPass(ctx, _psWaterDisp!, _outRtv!, OutW, OutH, _waterRt.SrvA, cb);
                break;
            case "ferro":
                QuadPass(ctx, _psFerro!, _outRtv!, OutW, OutH, null, cb);
                break;
            case "physarum":
                QuadPass(ctx, _psNeural!, _outRtv!, OutW, OutH, null, cb);
                break;
            case "rd":
                if (_caRt == null)
                {
                    _caRt = new RtPair(dev, SimW, SimH, Format.R16G16_Float);
                    _caReady = false;
                }
                cb.TexelX = 1f / SimW; cb.TexelY = 1f / SimH;
                if (!_caReady)
                {
                    QuadPass(ctx, _psCaInit!, _caRt.RtvA, SimW, SimH, null, cb);
                    _caReady = true;
                }
                QuadPass(ctx, _psCaStep!, _caRt.RtvB, SimW, SimH, _caRt.SrvA, cb);
                _caRt.Swap();
                QuadPass(ctx, _psCaDisp!, _outRtv!, OutW, OutH, _caRt.SrvA, cb);
                break;
            case "fire":
                QuadPass(ctx, _psGpuFire!, _outRtv!, OutW, OutH, null, cb);
                break;
        }

        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        ctx.OMSetBlendState(null);
    }

    internal static (bool Ok, string Summary) SelfTestAllModes()
    {
        DisposeResources();
        if (!EnsureInit()) return (false, "init failed");
        var dev = Gfx.D3DDevice;
        using var stage = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = OutW, Height = OutH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        string[] modes = ["milkdrop", "plasma", "tunnel", "kaleid", "synthwave", "water", "ferro", "physarum", "rd", "fire"];
        var parts = new List<string>(modes.Length);
        bool allOk = true;
        foreach (var m in modes)
        {
            for (int i = 0; i < 4; i++)
                ExecuteStep(m, i * 0.1f + 0.5f, 0.016f, 0xFFFF2090, 0xFF00D4FF, 0.78f, 0.62f, 0.50f, 0.65f, 0.2f, 0f, i == 0);
            dev.ImmediateContext.CopyResource(stage, _outTex!);
            var map = dev.ImmediateContext.Map(stage, 0, MapMode.Read);
            int lit = 0;
            try
            {
                for (int y = 0; y < OutH; y += 4)
                {
                    var row = (byte*)map.DataPointer + y * map.RowPitch;
                    for (int x = 0; x < OutW; x += 4)
                        if (row[x * 4 + 3] > 10) lit++;
                }
            }
            finally { dev.ImmediateContext.Unmap(stage, 0); }
            if (lit < 50) allOk = false;
            parts.Add($"{m}:{lit}");
        }
        DisposeResources();
        return (allOk, string.Join(" ", parts));
    }
}
