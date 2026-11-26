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
/// Hardware Direct3D 11 HLSL Multi-FX Shader Pack (zero CPU-to-GPU copy via IDXGISurface -> Direct2D!):
/// - "water": 2D Wave Equation (384x216) + audio droplets + 3D refraction &amp; luminous caustics
/// - "ferro": Raymarched 3D Liquid Chrome / Ferrofluid Metaballs + Chromatic Fresnel reflections (1024x576)
/// - "physarum": 16,384-agent GPU Slime Mold / Neural Web with pheromone trail diffusion (512x288)
/// - "rd": Gray-Scott Reaction-Diffusion organic coral/plasma growth (512x288, 6 sub-steps/frame)
/// </summary>
public static unsafe partial class GpuEffectsPack
{
    private const int OutW = 1024, OutH = 576;
    private const int WaterW = 384, WaterH = 216;
    private const int SimW = 512, SimH = 288;
    private const int AgentSide = 128, AgentCount = AgentSide * AgentSide; // 16,384 GPU agents!

    [StructLayout(LayoutKind.Sequential)]
    private struct FxCB
    {
        public float Time, Dt, Bass, Mid;
        public float High, Hit, Pan, Calm;
        public float R0, G0, B0, Param0;
        public float R1, G1, B1, Param1;
        public float TexelX, TexelY, Aspect, Beat;
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
    float2 texel; float aspect, beat;
};

Texture2D tex0 : register(t0);
Texture2D tex1 : register(t1);
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

// ==================== 1. WATER RIPPLES + CAUSTICS (2D Wave Equation) ====================
float4 PS_WaterStep(VSOut i) : SV_Target
{
    float2 c = tex0.Sample(linClamp, i.uv).rg; // r = curr, g = prev
    float l = tex0.Sample(linClamp, i.uv - float2(texel.x, 0)).r;
    float r = tex0.Sample(linClamp, i.uv + float2(texel.x, 0)).r;
    float t = tex0.Sample(linClamp, i.uv - float2(0, texel.y)).r;
    float b = tex0.Sample(linClamp, i.uv + float2(0, texel.y)).r;

    float h = ((l + r + t + b) * 0.5 - c.g) * (0.972 + 0.014 * calm);

    // Continuous orbiting harmonic droplets + bass/beat splashes!
    float2 p0 = float2(0.5 + 0.34 * sin(time * 0.85) + pan * 0.10, 0.5 + 0.28 * cos(time * 1.12));
    float2 p1 = float2(0.5 - 0.36 * cos(time * 0.72 + 1.3), 0.5 + 0.26 * sin(time * 0.94));
    float2 d0 = (i.uv - p0) * float2(aspect, 1.0);
    float2 d1 = (i.uv - p1) * float2(aspect, 1.0);
    h += exp(-dot(d0, d0) / 0.0012) * (0.14 + bass * 0.42 + hit * 0.35) * calm * sin(time * 18.0);
    h += exp(-dot(d1, d1) / 0.0010) * (0.10 + mid * 0.36 + high * 0.25) * calm * cos(time * 15.0);

    if (beat > 0.5)
    {
        float2 pb = float2(param0, param1);
        float2 db = (i.uv - pb) * float2(aspect, 1.0);
        h += exp(-dot(db, db) / 0.0028) * (0.55 + bass * 0.65) * calm;
    }

    h = clamp(h, -1.8, 1.8);
    return float4(h, c.r, 0, 1);
}

float4 PS_WaterDisplay(VSOut i) : SV_Target
{
    float l = tex0.Sample(linClamp, i.uv - float2(texel.x, 0)).r;
    float r = tex0.Sample(linClamp, i.uv + float2(texel.x, 0)).r;
    float t = tex0.Sample(linClamp, i.uv - float2(0, texel.y)).r;
    float b = tex0.Sample(linClamp, i.uv + float2(0, texel.y)).r;
    float h = tex0.Sample(linClamp, i.uv).r;

    float3 n = normalize(float3((l - r) * 3.8, (t - b) * 3.8, 1.0));
    float3 light = normalize(float3(-0.35, -0.55, 0.75));
    float ndl = max(0.0, dot(n, light));
    float spec = pow(ndl, 28.0) * 1.15;
    float caustic = pow(clamp(1.0 - abs(n.x) - abs(n.y), 0.0, 1.0), 4.0) * abs(h) * 2.2;
    float fresnel = pow(1.0 - n.z, 2.5);

    float mixT = clamp(0.5 + n.x * 1.4 + n.y * 0.8, 0.0, 1.0);
    float3 waterCol = lerp(col0, col1, mixT);
    float3 c = waterCol * (caustic * 0.85 + fresnel * 1.35 + abs(h) * 0.55) + spec * lerp(waterCol, float3(1, 1, 1), 0.75);
    c = c / (1.0 + 0.35 * c);

    float2 edge = smoothstep(0.0, 0.06, i.uv) * smoothstep(0.0, 0.06, 1.0 - i.uv);
    float a = clamp(max(c.r, max(c.g, c.b)) * 1.15, 0.0, 0.88) * edge.x * edge.y;
    return float4(c * a, a);
}

// ==================== 2. FERROFLUID / LIQUID CHROME METABALLS (3D Raymarching) ====================
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
        float3 c = float3(
            cos(ang) * rad * 1.35 + pan * 0.25,
            sin(ang * 1.2 + fk) * rad * 0.72,
            sin(ang * 0.8 - fk * 1.3) * 0.45
        );
        float r = (0.24 + 0.08 * sin(time * 1.7 + fk * 3.1)) * pulse;
        d = smin(d, length(p - c) - r, 0.38);
    }
    // Magnetic spikes reacting to treble & bass!
    float spike = sin(p.x * 9.0 + time * 3.0) * sin(p.y * 9.0 - time * 2.4) * sin(p.z * 9.0) * (0.025 + high * 0.065 + bass * 0.045) * calm;
    return d + spike;
}

float3 ferroNormal(float3 p)
{
    float2 e = float2(0.008, 0.0);
    return normalize(float3(
        ferroMap(p + e.xyy) - ferroMap(p - e.xyy),
        ferroMap(p + e.yxy) - ferroMap(p - e.yxy),
        ferroMap(p + e.yyx) - ferroMap(p - e.yyx)
    ));
}

float4 PS_Ferro(VSOut i) : SV_Target
{
    float2 uv = (i.uv - 0.5) * float2(aspect, 1.0) * 2.0;
    float3 ro = float3(0.0, 0.0, -2.85);
    float3 rd = normalize(float3(uv, 1.65));

    float tRay = 0.0;
    bool hitSurf = false;
    float minDist = 10.0;
    for (int s = 0; s < 36; s++)
    {
        float3 p = ro + rd * tRay;
        float d = ferroMap(p);
        minDist = min(minDist, d);
        if (d < 0.006) { hitSurf = true; break; }
        tRay += d * 0.85;
        if (tRay > 5.5) break;
    }

    float3 c = 0;
    float a = 0;
    float aaEdge = smoothstep(0.022, 0.002, minDist); // silky analytical SDF anti-aliasing!
    if (hitSurf || aaEdge > 0.01)
    {
        float3 p = ro + rd * tRay;
        float3 n = ferroNormal(p);
        float3 ref = reflect(rd, n);
        float fres = pow(1.0 - max(0.0, dot(n, -rd)), 2.8);

        // Studio chrome reflections tinted with cover palette + chromatic Fresnel rim!
        float envT = clamp(ref.y * 0.5 + 0.5, 0.0, 1.0);
        float3 env = lerp(col0 * 0.35, col1 * 1.15, envT);
        float spec1 = pow(max(0.0, dot(ref, normalize(float3(-0.5, 0.7, -0.5)))), 24.0);
        float spec2 = pow(max(0.0, dot(ref, normalize(float3(0.6, -0.4, -0.6)))), 14.0);

        float3 chromaRim = float3(
            lerp(col0.r, 1.0, fres),
            lerp(col1.g, 0.9, fres * 0.85),
            lerp(col1.b, 1.0, fres * 1.1)
        ) * fres * (1.20 + bass * 0.60);

        float3 surfCol = env * (0.55 + 0.45 * calm) + chromaRim + spec1 * 1.25 + spec2 * col0 * 0.85;
        surfCol = surfCol / (1.0 + 0.32 * surfCol);
        float halo = exp(-max(0.0, minDist) * 5.8) * (0.30 + bass * 0.32) * calm;
        float3 haloCol = lerp(col0, col1, i.uv.x) * halo;
        float cov = hitSurf ? 1.0 : aaEdge;
        c = lerp(haloCol, surfCol, cov);
        a = lerp(clamp(halo * 1.1, 0.0, 0.55), 0.88, cov);
    }
    else
    {
        // Soft magnetic aura glow right around the chrome blobs
        float halo = exp(-max(0.0, minDist) * 5.8) * (0.30 + bass * 0.32) * calm;
        c = lerp(col0, col1, i.uv.x) * halo;
        a = clamp(halo * 1.1, 0.0, 0.55);
    }

    float2 edge = smoothstep(0.0, 0.05, i.uv) * smoothstep(0.0, 0.05, 1.0 - i.uv);
    a *= edge.x * edge.y;
    return float4(c * a, a);
}

// ==================== 3. PHYSARUM / NEURAL SLIME MOLD (16,384 GPU Agents) ====================
float4 PS_AgentInit(VSOut i) : SV_Target
{
    float h1 = hash21(i.uv * 17.3 + 0.13);
    float h2 = hash21(i.uv * 31.7 + 0.71);
    float h3 = hash21(i.uv * 53.1 + 0.37);
    float ang = h3 * 6.2831853;
    float rad = 0.08 + 0.38 * sqrt(h1);
    float2 pos = float2(0.5 + cos(ang) * rad / aspect, 0.5 + sin(ang) * rad);
    return float4(pos, ang + 3.14159, h2);
}

float4 PS_AgentStep(VSOut i) : SV_Target
{
    float4 ag = tex0.Sample(linClamp, i.uv); // xy = pos, z = angle, w = seed
    float2 pos = ag.xy;
    float ang = ag.z;

    float sensorDist = 0.018 + bass * 0.012;
    float sensorAng = 0.48;
    float turnSpd = (3.8 + high * 3.5 + bass * 2.2) * dt;

    float2 dF = float2(cos(ang) / aspect, sin(ang)) * sensorDist;
    float2 dL = float2(cos(ang - sensorAng) / aspect, sin(ang - sensorAng)) * sensorDist;
    float2 dR = float2(cos(ang + sensorAng) / aspect, sin(ang + sensorAng)) * sensorDist;

    float wF = tex1.Sample(linClamp, pos + dF).r;
    float wL = tex1.Sample(linClamp, pos + dL).r;
    float wR = tex1.Sample(linClamp, pos + dR).r;

    float rnd = hash21(pos * 97.1 + time + ag.w);
    if (wF > wL && wF > wR) { /* straight */ }
    else if (wF < wL && wF < wR) ang += (rnd - 0.5) * 2.0 * turnSpd;
    else if (wL > wR) ang -= turnSpd;
    else if (wR > wL) ang += turnSpd;

    if (beat > 0.5) ang += (rnd - 0.5) * 0.85 * bass;

    float spd = (0.09 + bass * 0.16 + hit * 0.12) * calm * dt;
    pos += float2(cos(ang) / aspect, sin(ang)) * spd;

    if (pos.x < 0.02 || pos.x > 0.98) { pos.x = clamp(pos.x, 0.02, 0.98); ang = 3.1415926 - ang; }
    if (pos.y < 0.02 || pos.y > 0.98) { pos.y = clamp(pos.y, 0.02, 0.98); ang = -ang; }
    return float4(pos, ang, ag.w);
}

struct PVSOut { float4 pos : SV_Position; float energy : TEXCOORD0; };

PVSOut VS_AgentDeposit(uint id : SV_VertexID)
{
    uint ax = id & 127;
    uint ay = (id >> 7) & 127;
    float2 auv = (float2(ax, ay) + 0.5) / 128.0;
    float4 ag = tex0.SampleLevel(linClamp, auv, 0);
    PVSOut o;
    o.pos = float4(ag.x * 2.0 - 1.0, 1.0 - ag.y * 2.0, 0.0, 1.0);
    o.energy = ag.w;
    return o;
}

float4 PS_AgentDeposit(PVSOut i) : SV_Target
{
    return float4(0.68 + bass * 0.45, 0, 0, 1);
}

float4 PS_TrailDiffuse(VSOut i) : SV_Target
{
    float sum = tex0.Sample(linClamp, i.uv).r * 3.0;
    sum += tex0.Sample(linClamp, i.uv - float2(texel.x, 0)).r;
    sum += tex0.Sample(linClamp, i.uv + float2(texel.x, 0)).r;
    sum += tex0.Sample(linClamp, i.uv - float2(0, texel.y)).r;
    sum += tex0.Sample(linClamp, i.uv + float2(0, texel.y)).r;
    sum += tex0.Sample(linClamp, i.uv + float2(-texel.x, -texel.y)).r * 0.5;
    sum += tex0.Sample(linClamp, i.uv + float2( texel.x, -texel.y)).r * 0.5;
    sum += tex0.Sample(linClamp, i.uv + float2(-texel.x,  texel.y)).r * 0.5;
    sum += tex0.Sample(linClamp, i.uv + float2( texel.x,  texel.y)).r * 0.5;
    float decay = 0.935 + 0.042 * calm;
    return float4(min(2.6, (sum / 9.0) * decay), 0, 0, 1);
}

float4 PS_PhysarumDisplay(VSOut i) : SV_Target
{
    float v = tex0.Sample(linClamp, i.uv).r;
    float glow = pow(clamp(v * 1.25, 0.0, 2.0), 1.15);
    float3 c = lerp(col0 * 0.75, col1 * 1.35, clamp(v * 0.75, 0.0, 1.0)) * glow;
    c += pow(clamp(v - 0.45, 0.0, 1.6), 1.8) * float3(1.0, 0.96, 1.0) * (0.55 + bass * 0.55);
    c = c / (1.0 + 0.32 * c);
    float2 edge = smoothstep(0.0, 0.05, i.uv) * smoothstep(0.0, 0.05, 1.0 - i.uv);
    float a = clamp(v * 1.35, 0.0, 0.90) * edge.x * edge.y;
    return float4(c * a, a);
}

// ==================== 4. AUDIO-REACTIVE GPU CELLULAR AUTOMATON (SmoothLife / Cyclic Pattern Waves) ====================
float4 PS_RdInit(VSOut i) : SV_Target
{
    float2 p = (i.uv - 0.5) * float2(aspect, 1.0);
    float ang = atan2(p.y, p.x);
    float rad = length(p);
    float mandala = sin(rad * 28.0) * cos(ang * 6.0);
    float h = hash21(floor(i.uv * 96.0));
    float alive = (mandala > 0.55 || h > 0.88) ? 1.0 : 0.0;
    return float4(alive, alive * 0.5, 0, 1);
}

float4 PS_RdStep(VSOut i) : SV_Target
{
    float2 cur = tex0.Sample(linClamp, i.uv).rg; // r = state (0..1), g = glowing trail age
    // 8-neighbor Moore neighborhood average (inner ring) + 4-neighbor outer ring (SmoothLife / Lenia kernel!)
    float inner = 0.0;
    inner += tex0.Sample(linClamp, i.uv + float2(-texel.x,  0)).r;
    inner += tex0.Sample(linClamp, i.uv + float2( texel.x,  0)).r;
    inner += tex0.Sample(linClamp, i.uv + float2( 0, -texel.y)).r;
    inner += tex0.Sample(linClamp, i.uv + float2( 0,  texel.y)).r;
    inner += tex0.Sample(linClamp, i.uv + float2(-texel.x, -texel.y)).r;
    inner += tex0.Sample(linClamp, i.uv + float2( texel.x, -texel.y)).r;
    inner += tex0.Sample(linClamp, i.uv + float2(-texel.x,  texel.y)).r;
    inner += tex0.Sample(linClamp, i.uv + float2( texel.x,  texel.y)).r;
    inner *= 0.125;

    float2 t2 = texel * 2.5;
    float outer = 0.0;
    outer += tex0.Sample(linClamp, i.uv + float2(-t2.x, 0)).r;
    outer += tex0.Sample(linClamp, i.uv + float2( t2.x, 0)).r;
    outer += tex0.Sample(linClamp, i.uv + float2(0, -t2.y)).r;
    outer += tex0.Sample(linClamp, i.uv + float2(0,  t2.y)).r;
    outer *= 0.25;

    // Lenia / SmoothLife growth function modulated LIVE by Bass, Mid, and Treble!
    float mu = 0.28 + mid * 0.14 - bass * 0.06;
    float sigma = 0.095 + high * 0.045;
    float kernel = inner * 0.68 + outer * 0.32;
    float diff = (kernel - mu) / max(sigma, 0.02);
    float growth = exp(-0.5 * diff * diff) * 2.0 - 1.0;

    float stepRate = (0.16 + bass * 0.26 + hit * 0.18) * calm;
    float nextState = clamp(cur.r + growth * stepRate, 0.0, 1.0);

    // Symmetrical kaleidoscopic beat seeds so patterns constantly blossom into geometric mandalas!
    if (beat > 0.5)
    {
        float2 p0 = float2(param0, param1);
        float2 p1 = float2(1.0 - param0, param1);
        float2 d0 = (i.uv - p0) * float2(aspect, 1.0);
        float2 d1 = (i.uv - p1) * float2(aspect, 1.0);
        float seed = exp(-dot(d0, d0) / 0.0014) + exp(-dot(d1, d1) / 0.0014);
        nextState = clamp(nextState + seed * (0.65 + bass * 0.5), 0.0, 1.0);
    }

    float trail = max(nextState, cur.g * (0.89 + 0.05 * calm));
    return float4(nextState, trail, 0, 1);
}

float4 PS_RdDisplay(VSOut i) : SV_Target
{
    float2 st = tex0.Sample(linClamp, i.uv).rg;
    float alive = st.r;
    float trail = st.g;

    // Crisp luminous cellular pattern with glowing edges & soft trail aura
    float l = tex0.Sample(linClamp, i.uv - float2(texel.x, 0)).r;
    float r = tex0.Sample(linClamp, i.uv + float2(texel.x, 0)).r;
    float t = tex0.Sample(linClamp, i.uv - float2(0, texel.y)).r;
    float b = tex0.Sample(linClamp, i.uv + float2(0, texel.y)).r;
    float edgeGlow = clamp(length(float2(r - l, b - t)) * 3.8, 0.0, 1.0);

    float3 baseCol = lerp(col0, col1, clamp(alive * 1.15, 0.0, 1.0));
    float3 c = baseCol * (trail * 0.65 + alive * 0.55) + lerp(col1, float3(1.0, 1.0, 1.0), 0.65) * edgeGlow * (0.75 + bass * 0.45);
    c = c / (1.0 + 0.30 * c);

    float2 vig = smoothstep(0.0, 0.06, i.uv) * smoothstep(0.0, 0.06, 1.0 - i.uv);
    float a = clamp((trail * 0.72 + edgeGlow * 0.55) * calm, 0.0, 0.88) * vig.x * vig.y;
    return float4(c * a, a);
}
";

    private static int _devGen = -1;
    private static bool _failed;
    private static ID3D11VertexShader? _vsQuad, _vsAgentDep;
    private static ID3D11PixelShader? _psWaterStep, _psWaterDisp, _psFerro;
    private static ID3D11PixelShader? _psAgentInit, _psAgentStep, _psAgentDep, _psTrailDiff, _psPhysDisp;
    private static ID3D11PixelShader? _psRdInit, _psRdStep, _psRdDisp;
    private static ID3D11Buffer? _cb;
    private static ID3D11SamplerState? _sampler;
    private static ID3D11RasterizerState? _rs;
    private static ID3D11BlendState? _bsOpaque, _bsAdd;

    private static ID3D11Texture2D? _outTex;
    private static ID3D11RenderTargetView? _outRtv;
    private static ID2D1Bitmap1? _d2dBmp;

    private static RtPair? _waterRt, _agentRt, _trailRt, _rdRt;
    private static bool _agentsReady, _rdReady;

    // Pending request (stepped cleanly in Gfx.Begin() BEFORE DC.BeginDraw()!)
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
        _agentRt?.Dispose(); _agentRt = null;
        _trailRt?.Dispose(); _trailRt = null;
        _rdRt?.Dispose(); _rdRt = null;
        _agentsReady = _rdReady = false;
        _vsQuad?.Dispose(); _vsQuad = null;
        _vsAgentDep?.Dispose(); _vsAgentDep = null;
        _psWaterStep?.Dispose(); _psWaterStep = null;
        _psWaterDisp?.Dispose(); _psWaterDisp = null;
        _psFerro?.Dispose(); _psFerro = null;
        _psAgentInit?.Dispose(); _psAgentInit = null;
        _psAgentStep?.Dispose(); _psAgentStep = null;
        _psAgentDep?.Dispose(); _psAgentDep = null;
        _psTrailDiff?.Dispose(); _psTrailDiff = null;
        _psPhysDisp?.Dispose(); _psPhysDisp = null;
        _psRdInit?.Dispose(); _psRdInit = null;
        _psRdStep?.Dispose(); _psRdStep = null;
        _psRdDisp?.Dispose(); _psRdDisp = null;
        _cb?.Dispose(); _cb = null;
        _sampler?.Dispose(); _sampler = null;
        _rs?.Dispose(); _rs = null;
        _bsOpaque?.Dispose(); _bsOpaque = null;
        _bsAdd?.Dispose(); _bsAdd = null;
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
            _vsAgentDep = dev.CreateVertexShader(CompileHlsl(HlslSource, "VS_AgentDeposit", "vs_5_0"));
            _psWaterStep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_WaterStep", "ps_5_0"));
            _psWaterDisp = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_WaterDisplay", "ps_5_0"));
            _psFerro = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Ferro", "ps_5_0"));
            _psAgentInit = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_AgentInit", "ps_5_0"));
            _psAgentStep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_AgentStep", "ps_5_0"));
            _psAgentDep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_AgentDeposit", "ps_5_0"));
            _psTrailDiff = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_TrailDiffuse", "ps_5_0"));
            _psPhysDisp = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_PhysarumDisplay", "ps_5_0"));
            _psRdInit = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_RdInit", "ps_5_0"));
            _psRdStep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_RdStep", "ps_5_0"));
            _psRdDisp = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_RdDisplay", "ps_5_0"));

            _cb = dev.CreateBuffer(new BufferDescription((uint)sizeof(FxCB), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _sampler = dev.CreateSamplerState(new SamplerDescription(Vortice.Direct3D11.Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _rs = dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid));
            _bsOpaque = dev.CreateBlendState(new Vortice.Direct3D11.BlendDescription(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.Zero));
            _bsAdd = dev.CreateBlendState(new Vortice.Direct3D11.BlendDescription(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.One));

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

    private static void UpdateCB(ID3D11DeviceContext ctx, in FxCB data)
    {
        var map = ctx.Map(_cb!, 0, MapMode.WriteDiscard);
        *(FxCB*)map.DataPointer = data;
        ctx.Unmap(_cb!, 0);
    }

    private static void QuadPass(ID3D11DeviceContext ctx, ID3D11PixelShader ps, ID3D11RenderTargetView rtv, int w, int h,
        ID3D11ShaderResourceView? s0, ID3D11ShaderResourceView? s1, in FxCB cb)
    {
        UpdateCB(ctx, cb);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vsQuad);
        ctx.OMSetBlendState(_bsOpaque);
        ctx.RSSetViewport(0, 0, w, h);
        ctx.OMSetRenderTargets(rtv);
        ctx.PSSetShader(ps);
        ctx.PSSetShaderResource(0, s0!);
        ctx.PSSetShaderResource(1, s1!);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
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

        var (r0, g0, b0) = MusicStageEffects.VividPaletteColor(dom1 != 0 ? dom1 : Th.Brand);
        uint c2 = dom2 != 0 && dom2 != dom1 ? dom2 : Gfx.Mix(dom1 != 0 ? dom1 : Th.Brand, 0xFF00E5FF, 0.45f);
        var (r1, g1, b1) = MusicStageEffects.VividPaletteColor(c2);
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
            Aspect = (float)OutW / OutH, Beat = beatHit ? 1f : 0f,
        };

        if (mode == "water")
        {
            if (_waterRt == null)
            {
                _waterRt = new RtPair(dev, WaterW, WaterH, Format.R16G16_Float);
                ctx.ClearRenderTargetView(_waterRt.RtvA, new Color4(0, 0, 0, 0));
                ctx.ClearRenderTargetView(_waterRt.RtvB, new Color4(0, 0, 0, 0));
            }
            cb.TexelX = 1f / WaterW; cb.TexelY = 1f / WaterH;
            QuadPass(ctx, _psWaterStep!, _waterRt.RtvB, WaterW, WaterH, _waterRt.SrvA, null, cb);
            _waterRt.Swap();
            QuadPass(ctx, _psWaterDisp!, _outRtv!, OutW, OutH, _waterRt.SrvA, null, cb);
        }
        else if (mode == "ferro")
        {
            cb.TexelX = 1f / OutW; cb.TexelY = 1f / OutH;
            QuadPass(ctx, _psFerro!, _outRtv!, OutW, OutH, null, null, cb);
        }
        else if (mode == "physarum")
        {
            if (_agentRt == null || _trailRt == null)
            {
                _agentRt = new RtPair(dev, AgentSide, AgentSide, Format.R32G32B32A32_Float);
                _trailRt = new RtPair(dev, SimW, SimH, Format.R16_Float);
                ctx.ClearRenderTargetView(_trailRt.RtvA, new Color4(0, 0, 0, 0));
                ctx.ClearRenderTargetView(_trailRt.RtvB, new Color4(0, 0, 0, 0));
                _agentsReady = false;
            }
            cb.TexelX = 1f / SimW; cb.TexelY = 1f / SimH;
            if (!_agentsReady)
            {
                QuadPass(ctx, _psAgentInit!, _agentRt.RtvA, AgentSide, AgentSide, null, null, cb);
                _agentsReady = true;
            }
            // 1. Step 16,384 agents steering along pheromone trail
            QuadPass(ctx, _psAgentStep!, _agentRt.RtvB, AgentSide, AgentSide, _agentRt.SrvA, _trailRt.SrvA, cb);
            _agentRt.Swap();
            // 2. Deposit 16,384 points additively onto trail texture (bind VS sampler & CB so SampleLevel works!)
            UpdateCB(ctx, cb);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.PointList);
            ctx.VSSetShader(_vsAgentDep);
            ctx.VSSetConstantBuffer(0, _cb);
            ctx.VSSetSampler(0, _sampler);
            ctx.VSSetShaderResource(0, _agentRt.SrvA);
            ctx.OMSetBlendState(_bsAdd);
            ctx.RSSetViewport(0, 0, SimW, SimH);
            ctx.OMSetRenderTargets(_trailRt.RtvA);
            ctx.PSSetShader(_psAgentDep);
            ctx.Draw(AgentCount, 0);
            ctx.VSSetShaderResource(0, null!);
            // 3. Diffuse & decay trail
            QuadPass(ctx, _psTrailDiff!, _trailRt.RtvB, SimW, SimH, _trailRt.SrvA, null, cb);
            _trailRt.Swap();
            // 4. Render neural web
            QuadPass(ctx, _psPhysDisp!, _outRtv!, OutW, OutH, _trailRt.SrvA, null, cb);
        }
        else if (mode == "rd")
        {
            if (_rdRt == null)
            {
                _rdRt = new RtPair(dev, SimW, SimH, Format.R16G16_Float);
                _rdReady = false;
            }
            cb.TexelX = 1f / SimW; cb.TexelY = 1f / SimH;
            if (!_rdReady)
            {
                QuadPass(ctx, _psRdInit!, _rdRt.RtvA, SimW, SimH, null, null, cb);
                _rdReady = true;
            }
            for (int s = 0; s < 2; s++)
            {
                cb.Beat = (s == 0 && beatHit) ? 1f : 0f;
                QuadPass(ctx, _psRdStep!, _rdRt.RtvB, SimW, SimH, _rdRt.SrvA, null, cb);
                _rdRt.Swap();
            }
            QuadPass(ctx, _psRdDisp!, _outRtv!, OutW, OutH, _rdRt.SrvA, null, cb);
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
        string[] modes = ["water", "ferro", "physarum", "rd"];
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
