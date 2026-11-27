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
/// Full hardware-accelerated Direct3D 11 HLSL port of Pavel Dobryakov's WebGL-Fluid-Simulation:
/// - 256x144 R16G16F Velocity &amp; R16F Curl/Divergence/Pressure with 20 Jacobi pressure iterations
/// - 1024x576 R16G16B16A16F HDR Dye texture advected by hardware bilinear sampling (razor-sharp 1024p silk!)
/// - 4-level GPU Bloom pyramid + 3D normal-gradient specular shading + linearToGamma
/// - Zero CPU-to-GPU upload: final B8G8R8A8_UNorm texture is shared directly with Direct2D via IDXGISurface!
/// </summary>
public static unsafe partial class GpuFluidEngine
{
    private const int SimW = 256, SimH = 144;
    private const int DyeW = 1024, DyeH = 576;
    private const int PressureIterations = 20;
    private const int BloomLevels = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FluidCB
    {
        public float TexelX, TexelY, DyeTexelX, DyeTexelY;
        public float Dt, Dissipation, Curl, Aspect;
        public float PointX, PointY, Radius, Param;
        public float ColorR, ColorG, ColorB, ColorA;
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
            var desc = new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = fmt, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            TexA = dev.CreateTexture2D(desc);
            TexB = dev.CreateTexture2D(desc);
            RtvA = dev.CreateRenderTargetView(TexA);
            RtvB = dev.CreateRenderTargetView(TexB);
            SrvA = dev.CreateShaderResourceView(TexA);
            SrvB = dev.CreateShaderResourceView(TexB);
        }

        public void Swap()
        {
            (TexA, TexB) = (TexB, TexA);
            (RtvA, RtvB) = (RtvB, RtvA);
            (SrvA, SrvB) = (SrvB, SrvA);
        }

        public void Dispose()
        {
            SrvA.Dispose(); SrvB.Dispose();
            RtvA.Dispose(); RtvB.Dispose();
            TexA.Dispose(); TexB.Dispose();
        }
    }

    private sealed class RtSingle : IDisposable
    {
        public ID3D11Texture2D Tex;
        public ID3D11RenderTargetView Rtv;
        public ID3D11ShaderResourceView Srv;
        public int W, H;

        public RtSingle(ID3D11Device dev, int w, int h, Format fmt)
        {
            W = w; H = h;
            var desc = new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = fmt, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            Tex = dev.CreateTexture2D(desc);
            Rtv = dev.CreateRenderTargetView(Tex);
            Srv = dev.CreateShaderResourceView(Tex);
        }

        public void Dispose()
        {
            Srv.Dispose();
            Rtv.Dispose();
            Tex.Dispose();
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
            int hr = D3DCompile(pSrc, (nuint)srcBytes.Length, null, 0, 0, pEntry, pTgt, 1 << 15 /*O3*/, 0, out nint codeBlob, out nint errBlob);
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
                if (codeBlob != 0)
                {
                    var cv = *(void***)codeBlob;
                    ((delegate* unmanaged[Stdcall]<nint, uint>)cv[2])(codeBlob);
                }
                throw new InvalidOperationException($"HLSL compile {entry}/{target} failed (0x{hr:X}): {err}");
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
            finally
            {
                var vtbl = *(void***)codeBlob;
                ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(codeBlob);
            }
        }
    }

    private const string HlslSource = @"
cbuffer Params : register(b0)
{
    float2 texelSize;
    float2 dyeTexelSize;
    float  dt;
    float  dissipation;
    float  curlStrength;
    float  aspectRatio;
    float2 splatPoint;
    float  splatRadius;
    float  param;
    float4 splatColor;
};

Texture2D tex0 : register(t0);
Texture2D tex1 : register(t1);
SamplerState linClamp : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VS_Quad(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float4 PS_Splat(VSOut i) : SV_Target
{
    float2 p = i.uv - splatPoint;
    p.x *= aspectRatio;
    float3 splat = exp(-dot(p, p) / max(splatRadius, 1e-6)) * splatColor.rgb;
    float3 base = tex0.Sample(linClamp, i.uv).rgb;
    return float4(base + splat, 1.0);
}

float4 PS_Curl(VSOut i) : SV_Target
{
    float L = tex0.Sample(linClamp, i.uv - float2(texelSize.x, 0)).y;
    float R = tex0.Sample(linClamp, i.uv + float2(texelSize.x, 0)).y;
    float T = tex0.Sample(linClamp, i.uv - float2(0, texelSize.y)).x;
    float B = tex0.Sample(linClamp, i.uv + float2(0, texelSize.y)).x;
    float vorticity = R - L - B + T;
    return float4(0.5 * vorticity, 0, 0, 1);
}

float4 PS_Vorticity(VSOut i) : SV_Target
{
    float L = tex1.Sample(linClamp, i.uv - float2(texelSize.x, 0)).x;
    float R = tex1.Sample(linClamp, i.uv + float2(texelSize.x, 0)).x;
    float T = tex1.Sample(linClamp, i.uv - float2(0, texelSize.y)).x;
    float B = tex1.Sample(linClamp, i.uv + float2(0, texelSize.y)).x;
    float C = tex1.Sample(linClamp, i.uv).x;

    float2 force = 0.5 * float2(abs(B) - abs(T), abs(L) - abs(R));
    force /= (length(force) + 0.0001);
    force *= curlStrength * C;
    force.y *= -1.0;

    float2 vel = tex0.Sample(linClamp, i.uv).xy;
    vel += force * dt;
    vel = clamp(vel, -1000.0, 1000.0);
    return float4(vel, 0, 1);
}

float4 PS_Divergence(VSOut i) : SV_Target
{
    float2 C = tex0.Sample(linClamp, i.uv).xy;
    float2 uvL = i.uv - float2(texelSize.x, 0);
    float2 uvR = i.uv + float2(texelSize.x, 0);
    float2 uvT = i.uv - float2(0, texelSize.y);
    float2 uvB = i.uv + float2(0, texelSize.y);

    float L = tex0.Sample(linClamp, uvL).x;
    float R = tex0.Sample(linClamp, uvR).x;
    float T = tex0.Sample(linClamp, uvT).y;
    float B = tex0.Sample(linClamp, uvB).y;

    if (uvL.x < 0.0) L = -C.x;
    if (uvR.x > 1.0) R = -C.x;
    if (uvT.y < 0.0) T = -C.y;
    if (uvB.y > 1.0) B = -C.y;

    float div = 0.5 * (R - L + B - T);
    return float4(div, 0, 0, 1);
}

float4 PS_Clear(VSOut i) : SV_Target
{
    return param * tex0.Sample(linClamp, i.uv);
}

float4 PS_Pressure(VSOut i) : SV_Target
{
    float L = tex0.Sample(linClamp, i.uv - float2(texelSize.x, 0)).x;
    float R = tex0.Sample(linClamp, i.uv + float2(texelSize.x, 0)).x;
    float T = tex0.Sample(linClamp, i.uv - float2(0, texelSize.y)).x;
    float B = tex0.Sample(linClamp, i.uv + float2(0, texelSize.y)).x;
    float div = tex1.Sample(linClamp, i.uv).x;
    float pressure = (L + R + B + T - div) * 0.25;
    return float4(pressure, 0, 0, 1);
}

float4 PS_GradSub(VSOut i) : SV_Target
{
    float L = tex0.Sample(linClamp, i.uv - float2(texelSize.x, 0)).x;
    float R = tex0.Sample(linClamp, i.uv + float2(texelSize.x, 0)).x;
    float T = tex0.Sample(linClamp, i.uv - float2(0, texelSize.y)).x;
    float B = tex0.Sample(linClamp, i.uv + float2(0, texelSize.y)).x;
    float2 vel = tex1.Sample(linClamp, i.uv).xy;
    vel -= float2(R - L, B - T);
    return float4(vel, 0, 1);
}

float4 PS_Advect(VSOut i) : SV_Target
{
    float2 coord = i.uv - dt * tex0.Sample(linClamp, i.uv).xy * texelSize;
    float4 result = tex1.Sample(linClamp, coord);
    float decay = 1.0 + dissipation * dt;
    return result / decay;
}

float4 PS_BloomPrefilter(VSOut i) : SV_Target
{
    float3 c = tex0.Sample(linClamp, i.uv).rgb;
    float br = max(c.r, max(c.g, c.b));
    float threshold = 0.55;
    float knee = 0.40;
    float rq = clamp(br - threshold + knee, 0.0, 2.0 * knee);
    rq = (rq * rq) / (4.0 * knee + 1e-5);
    float contrib = max(rq, br - threshold) / max(br, 1e-5);
    return float4(c * contrib, 1.0);
}

float4 PS_BloomBlur(VSOut i) : SV_Target
{
    float3 sum = 0;
    sum += tex0.Sample(linClamp, i.uv - float2(texelSize.x, 0)).rgb;
    sum += tex0.Sample(linClamp, i.uv + float2(texelSize.x, 0)).rgb;
    sum += tex0.Sample(linClamp, i.uv - float2(0, texelSize.y)).rgb;
    sum += tex0.Sample(linClamp, i.uv + float2(0, texelSize.y)).rgb;
    return float4(sum * 0.25, 1.0);
}

float3 LinearToGamma(float3 c)
{
    c = max(c, 0.0);
    return max(1.055 * pow(c, 0.416666667) - 0.055, 0.0);
}

float4 PS_Display(VSOut i) : SV_Target
{
    float3 c = tex0.Sample(linClamp, i.uv).rgb;

    // Pavel's exact 3D surface-gradient shading on the 1024x576 HDR dye buffer!
    float3 lc = tex0.Sample(linClamp, i.uv - float2(dyeTexelSize.x, 0)).rgb;
    float3 rc = tex0.Sample(linClamp, i.uv + float2(dyeTexelSize.x, 0)).rgb;
    float3 tc = tex0.Sample(linClamp, i.uv - float2(0, dyeTexelSize.y)).rgb;
    float3 bc = tex0.Sample(linClamp, i.uv + float2(0, dyeTexelSize.y)).rgb;
    float dx = length(rc) - length(lc);
    float dy = length(bc) - length(tc);
    float3 n = normalize(float3(dx, dy, length(dyeTexelSize)));
    float3 l = float3(0.0, 0.0, 1.0);
    float diffuse = clamp(dot(n, l) + 0.70, 0.70, 1.0);
    c *= diffuse;

    // Add Pavel's luminous GPU Bloom
    float3 bloom = tex1.Sample(linClamp, i.uv).rgb * 0.62;
    c += bloom;

    c = LinearToGamma(c);
    // Soft edge feathering so plumes melt seamlessly into the Music Stage border
    float2 edge = smoothstep(0.0, 0.06, i.uv) * smoothstep(0.0, 0.06, 1.0 - i.uv);
    float mask = edge.x * edge.y;
    float a = clamp(max(c.r, max(c.g, c.b)), 0.0, 0.94) * mask;
    return float4(c * a, a);
}

// ---- 16,384 GPU Flow-Field Spark Particles riding the EXACT same fluid velocity field! ----
float pHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float4 PS_SparkInit(VSOut i) : SV_Target
{
    return float4(pHash(i.uv * 19.1 + 0.17), pHash(i.uv * 41.3 + 0.53), pHash(i.uv * 67.7 + 0.31), pHash(i.uv * 91.9 + 0.89));
}

float4 PS_SparkStep(VSOut i) : SV_Target
{
    float4 p = tex0.Sample(linClamp, i.uv); // xy = pos, z = life, w = colorSeed
    float2 vel = tex1.Sample(linClamp, p.xy).xy;
    p.xy += vel * texelSize * dt * 1.45;
    p.z -= dt * (0.28 + 0.20 * p.w);
    if (p.z <= 0.0 || p.x < 0.01 || p.x > 0.99 || p.y < 0.01 || p.y > 0.99)
    {
        float h1 = pHash(i.uv * 23.7 + param);
        float h2 = pHash(i.uv * 57.3 + param * 1.37);
        p.xy = float2(0.08 + h1 * 0.84, 0.08 + h2 * 0.84);
        p.z = 0.65 + 0.35 * pHash(i.uv * 83.1 + param);
    }
    return p;
}

struct SparkVSOut { float4 pos : SV_Position; float4 col : COLOR0; };

SparkVSOut VS_SparkDraw(uint id : SV_VertexID)
{
    uint ax = id & 127;
    uint ay = (id >> 7) & 127;
    float2 auv = (float2(ax, ay) + 0.5) / 128.0;
    float4 p = tex0.SampleLevel(linClamp, auv, 0);
    float3 dye = tex1.SampleLevel(linClamp, p.xy, 0).rgb;
    float speedGlow = clamp(length(dye) * 1.35 + 0.18, 0.0, 1.0);
    float fade = sin(clamp(p.z, 0.0, 1.0) * 3.14159) * speedGlow;
    SparkVSOut o;
    o.pos = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
    float3 sc = lerp(splatColor.rgb, float3(1.0, 1.0, 1.0), 0.55) * fade * 0.85;
    o.col = float4(sc, fade * 0.75);
    return o;
}

float4 PS_SparkDraw(SparkVSOut i) : SV_Target { return i.col; }
";

    private static int _devGen = -1;
    private static bool _failed;
    private static ID3D11VertexShader? _vs, _vsSparkDraw;
    private static ID3D11PixelShader? _psSplat, _psCurl, _psVort, _psDiv, _psClear, _psPress, _psGrad, _psAdvect;
    private static ID3D11PixelShader? _psBloomPre, _psBloomBlur, _psDisplay;
    private static ID3D11PixelShader? _psSparkInit, _psSparkStep, _psSparkDraw;
    private static ID3D11Buffer? _cb;
    private static ID3D11SamplerState? _sampler;
    private static ID3D11RasterizerState? _rs;
    private static ID3D11BlendState? _bsOpaque, _bsAdd;

    private static RtPair? _vel, _dye, _press, _sparks;
    private static bool _sparksInit;
    private static RtSingle? _curl, _div, _outRt;
    private static RtSingle[]? _bloom;
    private static ID2D1Bitmap1? _d2dBmp;

    // Pending frame request (set during DrawBackdropEffects, executed cleanly BEFORE DC.BeginDraw()!)
    private static bool _pendingStep, _reqSparks;
    private static uint _reqDom1, _reqDom2;
    private static float _reqBass, _reqMid, _reqHigh, _reqHit, _reqPan, _reqQuiet, _reqBurst;
    private static bool _reqBeat;
    private static long _lastStepMs;
    private static int _splatSeed;
    private static float _prevE0x = 0.5f, _prevE0y = 0.5f, _prevE1x = 0.5f, _prevE1y = 0.5f, _prevE2x = 0.5f, _prevE2y = 0.5f;
    private static bool _hasPrevPos;

    public static ID2D1Bitmap1? OutputBitmap => _d2dBmp;

    /// <summary>Width / height of the stage the output is stretched over. The render texture is a fixed 16:9, so the shaders must
    /// use the STAGE aspect (not the texture's) or circles turn into ellipses on a wide or tall stage.</summary>
    private static float _reqAspect = 16f / 9f;
    public static void RequestStep(uint dom1, uint dom2, float bass, float mid, float high, float hit, float pan, float quietDim, bool beatHit, float burstPower, bool withSparks = false, float stageAspect = 0f)
    {
        if (stageAspect > 0.01f) _reqAspect = Math.Clamp(stageAspect, 0.4f, 6f);
        _reqDom1 = dom1; _reqDom2 = dom2;
        _reqBass = bass; _reqMid = mid; _reqHigh = high;
        _reqHit = hit; _reqPan = pan; _reqQuiet = quietDim;
        _reqSparks = withSparks;
        if (beatHit) _reqBeat = true;
        if (burstPower > _reqBurst) _reqBurst = burstPower;
        _pendingStep = true;
    }

    public static void DisposeResources()
    {
        _d2dBmp?.Dispose(); _d2dBmp = null;
        _outRt?.Dispose(); _outRt = null;
        _vel?.Dispose(); _vel = null;
        _dye?.Dispose(); _dye = null;
        _press?.Dispose(); _press = null;
        _sparks?.Dispose(); _sparks = null;
        _sparksInit = false;
        _curl?.Dispose(); _curl = null;
        _div?.Dispose(); _div = null;
        if (_bloom != null) { foreach (var b in _bloom) b?.Dispose(); _bloom = null; }
        _vs?.Dispose(); _vs = null;
        _vsSparkDraw?.Dispose(); _vsSparkDraw = null;
        _psSplat?.Dispose(); _psSplat = null;
        _psCurl?.Dispose(); _psCurl = null;
        _psVort?.Dispose(); _psVort = null;
        _psDiv?.Dispose(); _psDiv = null;
        _psClear?.Dispose(); _psClear = null;
        _psPress?.Dispose(); _psPress = null;
        _psGrad?.Dispose(); _psGrad = null;
        _psAdvect?.Dispose(); _psAdvect = null;
        _psBloomPre?.Dispose(); _psBloomPre = null;
        _psBloomBlur?.Dispose(); _psBloomBlur = null;
        _psDisplay?.Dispose(); _psDisplay = null;
        _psSparkInit?.Dispose(); _psSparkInit = null;
        _psSparkStep?.Dispose(); _psSparkStep = null;
        _psSparkDraw?.Dispose(); _psSparkDraw = null;
        _cb?.Dispose(); _cb = null;
        _sampler?.Dispose(); _sampler = null;
        _rs?.Dispose(); _rs = null;
        _bsOpaque?.Dispose(); _bsOpaque = null;
        _bsAdd?.Dispose(); _bsAdd = null;
        _devGen = -1;
        _failed = false;
        _hasPrevPos = false;
    }

    private static bool EnsureInit()
    {
        if (_failed) return false;
        if (_d2dBmp != null && _devGen == Gfx.DeviceGeneration) return true;
        DisposeResources();
        try
        {
            var dev = Gfx.D3DDevice;
            _vs = dev.CreateVertexShader(CompileHlsl(HlslSource, "VS_Quad", "vs_5_0"));
            _vsSparkDraw = dev.CreateVertexShader(CompileHlsl(HlslSource, "VS_SparkDraw", "vs_5_0"));
            _psSplat = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Splat", "ps_5_0"));
            _psCurl = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Curl", "ps_5_0"));
            _psVort = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Vorticity", "ps_5_0"));
            _psDiv = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Divergence", "ps_5_0"));
            _psClear = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Clear", "ps_5_0"));
            _psPress = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Pressure", "ps_5_0"));
            _psGrad = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_GradSub", "ps_5_0"));
            _psAdvect = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Advect", "ps_5_0"));
            _psBloomPre = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_BloomPrefilter", "ps_5_0"));
            _psBloomBlur = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_BloomBlur", "ps_5_0"));
            _psDisplay = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_Display", "ps_5_0"));
            _psSparkInit = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_SparkInit", "ps_5_0"));
            _psSparkStep = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_SparkStep", "ps_5_0"));
            _psSparkDraw = dev.CreatePixelShader(CompileHlsl(HlslSource, "PS_SparkDraw", "ps_5_0"));

            _cb = dev.CreateBuffer(new BufferDescription((uint)sizeof(FluidCB), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _sampler = dev.CreateSamplerState(new SamplerDescription(Vortice.Direct3D11.Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            _rs = dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid));
            _bsOpaque = dev.CreateBlendState(new Vortice.Direct3D11.BlendDescription(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.Zero));
            var addDesc = new Vortice.Direct3D11.BlendDescription(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.One);
            _bsAdd = dev.CreateBlendState(addDesc);

            _vel = new RtPair(dev, SimW, SimH, Format.R16G16_Float);
            _press = new RtPair(dev, SimW, SimH, Format.R16_Float);
            _curl = new RtSingle(dev, SimW, SimH, Format.R16_Float);
            _div = new RtSingle(dev, SimW, SimH, Format.R16_Float);
            _dye = new RtPair(dev, DyeW, DyeH, Format.R16G16B16A16_Float);
            _outRt = new RtSingle(dev, DyeW, DyeH, Format.B8G8R8A8_UNorm);

            _bloom = new RtSingle[BloomLevels];
            int bw = 256, bh = 144;
            for (int i = 0; i < BloomLevels; i++)
            {
                _bloom[i] = new RtSingle(dev, bw, bh, Format.R16G16B16A16_Float);
                bw = Math.Max(16, bw / 2);
                bh = Math.Max(9, bh / 2);
            }

            // Clear all render targets to zero once
            var ctx = dev.ImmediateContext;
            var zero = new Color4(0, 0, 0, 0);
            ctx.ClearRenderTargetView(_vel.RtvA, zero); ctx.ClearRenderTargetView(_vel.RtvB, zero);
            ctx.ClearRenderTargetView(_press.RtvA, zero); ctx.ClearRenderTargetView(_press.RtvB, zero);
            ctx.ClearRenderTargetView(_dye.RtvA, zero); ctx.ClearRenderTargetView(_dye.RtvB, zero);
            ctx.ClearRenderTargetView(_outRt.Rtv, zero);

            using var surf = _outRt.Tex.QueryInterface<IDXGISurface>();
            _d2dBmp = Gfx.DC.CreateBitmapFromDxgiSurface(surf,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
            _devGen = Gfx.DeviceGeneration;
            return true;
        }
        catch (Exception e)
        {
            Services.Paths.Log("GpuFluidEngine init failed: " + e.Message);
            DisposeResources();
            _failed = true;
            return false;
        }
    }

    private static void UpdateCB(ID3D11DeviceContext ctx, in FluidCB data)
    {
        var map = ctx.Map(_cb!, 0, MapMode.WriteDiscard);
        *(FluidCB*)map.DataPointer = data;
        ctx.Unmap(_cb!, 0);
    }

    private static void Pass(ID3D11DeviceContext ctx, ID3D11PixelShader ps, ID3D11RenderTargetView rtv, int w, int h,
        ID3D11ShaderResourceView? s0, ID3D11ShaderResourceView? s1, in FluidCB cb, bool additive = false)
    {
        UpdateCB(ctx, cb);
        ctx.OMSetBlendState(additive ? _bsAdd : _bsOpaque);
        ctx.RSSetViewport(0, 0, w, h);
        ctx.OMSetRenderTargets(rtv);
        ctx.PSSetShader(ps);
        ctx.PSSetShaderResource(0, s0!);
        ctx.PSSetShaderResource(1, s1!);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
    }

    private static void Splat(ID3D11DeviceContext ctx, float x, float y, float dx, float dy, float cr, float cg, float cbCol, float radius)
    {
        var cb = new FluidCB
        {
            Aspect = _reqAspect,
            PointX = x, PointY = y,
            Radius = radius,
            ColorR = dx, ColorG = dy, ColorB = 0f, ColorA = 1f,
        };
        Pass(ctx, _psSplat!, _vel!.RtvB, SimW, SimH, _vel.SrvA, null, cb);
        _vel.Swap();

        cb.ColorR = cr; cb.ColorG = cg; cb.ColorB = cbCol;
        Pass(ctx, _psSplat!, _dye!.RtvB, DyeW, DyeH, _dye.SrvA, null, cb);
        _dye.Swap();
    }

    private static (float r, float g, float b) RotateHue(float r, float g, float b, float rad)
    {
        float cosA = MathF.Cos(rad), sinA = MathF.Sin(rad);
        const float k = 1f / 3f;
        float sq = MathF.Sqrt(k);
        float m00 = cosA + (1f - cosA) * k;
        float m01 = k * (1f - cosA) - sq * sinA;
        float m02 = k * (1f - cosA) + sq * sinA;
        return (Math.Clamp(r * m00 + g * m01 + b * m02, 0.02f, 1f),
                Math.Clamp(r * m02 + g * m00 + b * m01, 0.02f, 1f),
                Math.Clamp(r * m01 + g * m02 + b * m00, 0.02f, 1f));
    }

    /// <summary>Called in Gfx.Begin() strictly BEFORE DC.BeginDraw() so D3D11 &amp; Direct2D never contend.</summary>
    public static void StepBeforeBeginDraw()
    {
        if (!_pendingStep) return;
        _pendingStep = false;
        long now = UI.Now;
        if (now - _lastStepMs < 14) return; // up to ~65-72Hz silky GPU simulation cadence
        float dt = _lastStepMs == 0 ? 0.016f : Math.Clamp((now - _lastStepMs) * 0.001f, 0.008f, 0.025f);
        _lastStepMs = now;

        bool beat = _reqBeat; _reqBeat = false;
        float burst = _reqBurst; _reqBurst = 0f;
        ExecuteGpuStep((now - UI.StartTick) * 0.001f, dt, _reqDom1, _reqDom2, _reqBass, _reqMid, _reqHigh, _reqHit, _reqPan, _reqQuiet, beat, burst, _reqSparks);
    }

    private static void ExecuteGpuStep(float t, float dt, uint dom1, uint dom2,
        float bass, float mid, float high, float hit, float pan, float quietDim, bool beatHit, float burstPower, bool withSparks = false)
    {
        if (!EnsureInit()) return;
        var ctx = Gfx.D3DDevice.ImmediateContext;

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(null);
        ctx.VSSetShader(_vs);
        ctx.RSSetState(_rs);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.PSSetSampler(0, _sampler);

        // Rich evolving multi-color triad anchored to cover art (always vivid violet/cyan/gold/cover harmony — never flat monochrome!)
        var (baseR, baseG, baseB) = MusicStageEffects.VividPaletteColor(dom1 != 0 ? dom1 : Th.Brand);
        var (r0, g0, b0) = RotateHue(baseR, baseG, baseB, MathF.Sin(t * 0.14f) * 0.65f);
        var (r1, g1, b1) = RotateHue(baseR, baseG, baseB, 2.15f + MathF.Cos(t * 0.19f) * 0.55f); // rich complementary violet/magenta
        var (r2, g2, b2) = RotateHue(baseR, baseG, baseB, -2.10f + MathF.Sin(t * 0.23f) * 0.55f); // electric cyan/gold accent

        float calm = Math.Clamp(1f - quietDim * 0.92f, 0.05f, 1f);

        // 1. Three smooth Pavel-style sweeping splat emitters driven by live Bass / Mid / Treble + StereoPan
        float e0x = 0.50f + 0.34f * MathF.Sin(t * 0.58f) + pan * 0.08f;
        float e0y = 0.52f + 0.25f * MathF.Sin(t * 1.16f);
        float e1x = 0.50f - 0.35f * MathF.Cos(t * 0.50f + 1.1f) - pan * 0.07f;
        float e1y = 0.48f + 0.27f * MathF.Sin(t * 0.84f + 0.6f);
        float e2x = 0.50f + 0.37f * MathF.Sin(t * 0.90f + 2.4f);
        float e2y = 0.50f - 0.28f * MathF.Cos(t * 1.32f + 1.8f);

        if (!_hasPrevPos)
        {
            _prevE0x = e0x; _prevE0y = e0y;
            _prevE1x = e1x; _prevE1y = e1y;
            _prevE2x = e2x; _prevE2y = e2y;
            _hasPrevPos = true;
        }

        float velGain = (3400f + bass * 3800f + hit * 2400f) * calm;
        float d0 = (0.14f + bass * 0.34f + hit * 0.16f) * calm;
        Splat(ctx, e0x, e0y, (e0x - _prevE0x) * velGain, (e0y - _prevE0y) * velGain, r0 * d0, g0 * d0, b0 * d0, 0.0022f + bass * 0.0014f);

        float d1 = (0.12f + mid * 0.32f + bass * 0.12f) * calm;
        Splat(ctx, e1x, e1y, (e1x - _prevE1x) * velGain, (e1y - _prevE1y) * velGain, r1 * d1, g1 * d1, b1 * d1, 0.0020f + mid * 0.0012f);

        float d2 = (0.10f + high * 0.30f + hit * 0.20f) * calm;
        Splat(ctx, e2x, e2y, (e2x - _prevE2x) * velGain, (e2y - _prevE2y) * velGain, r2 * d2, g2 * d2, b2 * d2, 0.0016f + high * 0.0010f);

        _prevE0x = e0x; _prevE0y = e0y;
        _prevE1x = e1x; _prevE1y = e1y;
        _prevE2x = e2x; _prevE2y = e2y;

        // Beat & Drop opposing colliding splats (exact Pavel multi-splat curl bursts!)
        if ((beatHit || burstPower > 0.05f) && calm > 0.2f)
        {
            int count = burstPower > 0.45f ? 5 : 2;
            float pwr = MathF.Max(0.6f, MathF.Max(bass * 1.1f + hit, burstPower));
            for (int s = 0; s < count; s++)
            {
                int idx = ++_splatSeed;
                float ang = (idx * 2.399963f + t * 0.9f) % MathF.Tau;
                float sx = 0.50f + 0.33f * MathF.Cos(ang);
                float sy = 0.50f + 0.30f * MathF.Sin(ang);
                float tang = ang + 2.45f;
                float spd = (260f + 240f * pwr) * calm;
                float cr = (s % 3) == 0 ? r0 : ((s % 3) == 1 ? r1 : r2);
                float cg = (s % 3) == 0 ? g0 : ((s % 3) == 1 ? g1 : g2);
                float cbC = (s % 3) == 0 ? b0 : ((s % 3) == 1 ? b1 : b2);
                float boost = burstPower > 0.45f ? 0.52f : 0.34f;
                Splat(ctx, sx, sy, MathF.Cos(tang) * spd, MathF.Sin(tang) * spd, cr * boost, cg * boost, cbC * boost, 0.0024f + pwr * 0.0012f);
            }
        }

        // 2. Curl -> Vorticity Confinement (Pavel default CURL = 30!)
        var simCb = new FluidCB
        {
            TexelX = 1f / SimW, TexelY = 1f / SimH,
            DyeTexelX = 1f / DyeW, DyeTexelY = 1f / DyeH,
            Dt = dt, Curl = 30f + bass * 12f, Param = 0.8f,
        };
        Pass(ctx, _psCurl!, _curl!.Rtv, SimW, SimH, _vel!.SrvA, null, simCb);
        Pass(ctx, _psVort!, _vel.RtvB, SimW, SimH, _vel.SrvA, _curl.Srv, simCb);
        _vel.Swap();

        // 3. Divergence -> Clear Pressure (0.8) -> 20 Jacobi Pressure Iterations -> Gradient Subtract
        Pass(ctx, _psDiv!, _div!.Rtv, SimW, SimH, _vel.SrvA, null, simCb);
        Pass(ctx, _psClear!, _press!.RtvB, SimW, SimH, _press.SrvA, null, simCb);
        _press.Swap();
        for (int k = 0; k < PressureIterations; k++)
        {
            Pass(ctx, _psPress!, _press.RtvB, SimW, SimH, _press.SrvA, _div.Srv, simCb);
            _press.Swap();
        }
        Pass(ctx, _psGrad!, _vel.RtvB, SimW, SimH, _press.SrvA, _vel.SrvA, simCb);
        _vel.Swap();

        // 4. Advect Velocity (256x144) & Advect HDR Dye (1024x576!)
        simCb.Dissipation = 0.22f;
        Pass(ctx, _psAdvect!, _vel.RtvB, SimW, SimH, _vel.SrvA, _vel.SrvA, simCb);
        _vel.Swap();

        simCb.Dissipation = 0.95f + (1f - calm) * 1.8f;
        Pass(ctx, _psAdvect!, _dye!.RtvB, DyeW, DyeH, _vel.SrvA, _dye.SrvA, simCb);
        _dye.Swap();

        // 5. 4-Level GPU Bloom Pyramid (Prefilter -> Downsample -> Additive Upsample)
        var bl0 = _bloom![0];
        var blCb = new FluidCB { TexelX = 1f / DyeW, TexelY = 1f / DyeH };
        Pass(ctx, _psBloomPre!, bl0.Rtv, bl0.W, bl0.H, _dye.SrvA, null, blCb);
        for (int i = 1; i < BloomLevels; i++)
        {
            var prev = _bloom[i - 1];
            var cur = _bloom[i];
            blCb.TexelX = 1f / prev.W; blCb.TexelY = 1f / prev.H;
            Pass(ctx, _psBloomBlur!, cur.Rtv, cur.W, cur.H, prev.Srv, null, blCb);
        }
        for (int i = BloomLevels - 1; i > 0; i--)
        {
            var src = _bloom[i];
            var dst = _bloom[i - 1];
            blCb.TexelX = 1f / src.W; blCb.TexelY = 1f / src.H;
            Pass(ctx, _psBloomBlur!, dst.Rtv, dst.W, dst.H, src.Srv, null, blCb, additive: true);
        }

        // 6. Final 1024x576 Display Pass (3D Normal Shading + Bloom + LinearToGamma -> Direct2D Shared Surface!)
        Pass(ctx, _psDisplay!, _outRt!.Rtv, DyeW, DyeH, _dye.SrvA, bl0.Srv, simCb);

        // 7. Optional 16,384 GPU Flow-Field Spark Particles riding _vel directly onto _outRt!
        if (withSparks)
        {
            if (_sparks == null)
            {
                _sparks = new RtPair(Gfx.D3DDevice, 128, 128, Format.R32G32B32A32_Float);
                _sparksInit = false;
            }
            var spCb = new FluidCB
            {
                TexelX = 1f / SimW, TexelY = 1f / SimH,
                Dt = dt, Param = t,
                ColorR = r0, ColorG = g0, ColorB = b0, ColorA = 1f,
            };
            if (!_sparksInit)
            {
                Pass(ctx, _psSparkInit!, _sparks.RtvA, 128, 128, null, null, spCb);
                _sparksInit = true;
            }
            Pass(ctx, _psSparkStep!, _sparks.RtvB, 128, 128, _sparks.SrvA, _vel.SrvA, spCb);
            _sparks.Swap();

            UpdateCB(ctx, spCb);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.PointList);
            ctx.VSSetShader(_vsSparkDraw);
            ctx.VSSetConstantBuffer(0, _cb);
            ctx.VSSetSampler(0, _sampler);
            ctx.VSSetShaderResource(0, _sparks.SrvA);
            ctx.VSSetShaderResource(1, _dye.SrvA);
            ctx.OMSetBlendState(_bsAdd);
            ctx.RSSetViewport(0, 0, DyeW, DyeH);
            ctx.OMSetRenderTargets(_outRt.Rtv);
            ctx.PSSetShader(_psSparkDraw);
            ctx.Draw(128 * 128, 0);
            ctx.VSSetShaderResource(0, null!);
            ctx.VSSetShaderResource(1, null!);
        }

        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        ctx.OMSetBlendState(null);
    }

    internal static (bool Ok, double MsPerStep, int LitPx) SelfTestGpuFluid()
    {
        DisposeResources();
        if (!EnsureInit()) return (false, 999, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int steps = 10;
        for (int i = 0; i < steps; i++)
            ExecuteGpuStep(i * 0.05f, 0.016f, 0xFFFF2090, 0xFF00D4FF, 0.78f, 0.62f, 0.50f, i == 3 ? 0.9f : 0.2f, 0.2f, 0f, i == 0 || i == 5, i == 0 ? 1.0f : 0f);
        var dev = Gfx.D3DDevice;
        using var stage = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = DyeW, Height = DyeH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        dev.ImmediateContext.CopyResource(stage, _outRt!.Tex);
        var map = dev.ImmediateContext.Map(stage, 0, MapMode.Read);
        int lit = 0;
        try
        {
            for (int y = 0; y < DyeH; y += 2)
            {
                var row = (byte*)map.DataPointer + y * map.RowPitch;
                for (int x = 0; x < DyeW; x += 2)
                    if (row[x * 4 + 3] > 20) lit++;
            }
        }
        finally { dev.ImmediateContext.Unmap(stage, 0); }
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds / steps;
        DisposeResources();
        return (lit > 1000 && ms < 25.0, ms, lit);
    }
}
