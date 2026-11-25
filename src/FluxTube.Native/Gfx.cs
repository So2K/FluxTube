using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlpha = Vortice.DCommon.AlphaMode;
using Win = FluxTube.W;

namespace FluxTube;

public readonly record struct R(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;
    public R Inset(float dx, float dy) => new(X + dx, Y + dy, W - 2 * dx, H - 2 * dy);
    public R Inset(float d) => Inset(d, d);
    public R Intersect(R o)
    {
        float x = MathF.Max(X, o.X), y = MathF.Max(Y, o.Y);
        float r = MathF.Min(Right, o.Right), b = MathF.Min(Bottom, o.Bottom);
        return new(x, y, MathF.Max(0, r - x), MathF.Max(0, b - y));
    }
}

/// <summary>Shaped, measured text ready to draw. Cached, so a steady frame shapes nothing.</summary>
public sealed class TextRun
{
    public IDWriteTextLayout? Layout;   // null: shaping deferred to a later frame, W/H are estimates
    public float W, H;
    public int Lines;
    public bool Color;                  // has emoji: only then pay for colour-glyph analysis when drawing
}

/// <summary>
/// Direct2D on a flip swap chain. Everything is drawn in one pass, only when something changed.
/// Coordinates are DIPs; the device context carries the monitor DPI.
/// </summary>
public static unsafe class Gfx
{
    private static ID3D11Device _dev = null!;
    private static IDXGISwapChain1 _swap = null!;
    private static ID2D1Factory1 _d2dFactory = null!;
    private static ID2D1Device _d2dDev = null!;
    private static ID2D1Bitmap1? _target;
    private static IDWriteFactory _dw = null!;
    private static nint _hwnd;

    public static ID2D1DeviceContext DC { get; private set; } = null!;
    public static float Scale { get; private set; } = 1;
    public static int PxW { get; private set; }
    public static int PxH { get; private set; }
    public static float W => PxW / Scale;
    public static float H => PxH / Scale;
    public static long Frame { get; private set; }
    /// <summary>CPU time Direct2D needed to turn the last frame's draw calls into GPU commands.</summary>
    public static double FlushMs { get; private set; }
    public static int TextCreated, TextDraws;
    public static double TextDrawMs;
    /// <summary>Bumped when the GPU device was recreated: every device bitmap made before is dead.</summary>
    public static int DeviceGeneration { get; private set; }
    /// <summary>True when DXGI reported DXGI_STATUS_OCCLUDED (covered/locked window); pauses continuous animation spinning.</summary>
    public static bool Occluded { get; private set; }
    public static event Action? DeviceLost;

    public const string Font = "Segoe UI";
    public static string IconFont = "Segoe Fluent Icons";
    private static IDWriteFontCollection? _lucideFonts;
    private static readonly Dictionary<string, string> LucideGlyphs = new()
    {
        [Th.Back] = "\ue04c", [Th.Home] = "\ue0f8", [Th.Subs] = "\ue472", [Th.Later] = "\ue24f",
        [Th.Like] = "\ue189", [Th.Dislike] = "\ue188", [Th.History] = "\ue1f4", [Th.Eye] = "\ue0be",
        [Th.Download] = "\ue0b6", [Th.TikTok] = "\ue29a", [Th.Settings] = "\ue157", [Th.Refresh] = "\ue148",
        [Th.Search] = "\ue154", [Th.Account] = "\ue46c", [Th.Play] = "\ue13f", [Th.Pause] = "\ue131",
        [Th.Close] = "\ue1b1", [Th.More] = "\ue0ba", [Th.Folder] = "\ue0dc", [Th.Delete] = "\ue18d",
        [Th.Expand] = "\ue115", [Th.Rewind] = "\ue14b", [Th.Forward] = "\ue14c", [Th.Check] = "\ue070",
        [Th.Link] = "\ue107", [Th.Share] = "\ue159", [Th.Hamburger] = "\ue118", [Th.Add] = "\ue140",
        [Th.Volume] = "\ue1aa", [Th.Mute] = "\ue1ab", [Th.Copy] = "\ue0a2", [Th.Globe] = "\ue0eb",
        [Th.Comment] = "\ue119", [Th.Headphones] = "\ue0f4", [Th.Info] = "\ue0fe",
        [Th.ChevronRight] = "\ue073", [Th.ChevronUp] = "\ue074", [Th.ChevronDown] = "\ue071",
        [Th.OpenNew] = "\ue0bd", [Th.Restart] = "\ue14b",
    };

    private static ID2D1SolidColorBrush? _scratchBrush;
    private static ID2D1Bitmap1? _grainBmp;
    private static ID2D1BitmapBrush? _grainBrush;
    private static ID2D1Bitmap1? _sheenBmp;
    private static ID2D1BitmapBrush? _sheenBrush;
    private static ID2D1Bitmap1? _halftoneBmp;
    private static ID2D1BitmapBrush? _halftoneBrush;
    private static ID2D1Bitmap1? _sketchBmp;
    private static ID2D1BitmapBrush? _sketchBrush;
    private static ID2D1Bitmap1? _backdropBmp;
    private static uint _backdropBg, _backdropBrand, _backdropBrand2;
    private static bool _backdropLight;
    private static string _backdropMode = "";
    private static long _backdropBakedAt;
    private static ID2D1StrokeStyle? _dashStyle;
    private static ID2D1BitmapBrush? _imgBrush;
    // Hardware Direct2D 1.1 Stencil-Masked Adaptive Frosted Glass & GPU Stylization Shader resources
    private static ID2D1Bitmap1? _sceneBmp;
    private static ID2D1Bitmap1? _activeTarget;
    private static ID2D1Bitmap1? _glassDownBmp;
    private static ID2D1Bitmap1? _glassBlurBmp;
    private static ID2D1BitmapBrush? _glassBrush;
    private static ID2D1Effect? _glassBlurFx;
    private static ID2D1Effect? _glassSatFx;
    private static ID2D1Effect? _glassPostFx;
    private static ID2D1Effect? _glassEdgeFx;
    private static ID2D1Effect? _glassTurbFx;
    private static ID2D1Effect? _glassDispFx;
    private static int _sceneW, _sceneH, _glassW, _glassH;
    private static float _sceneScale;
    private static bool _glassReady;
    // Hardware-accelerated Direct2D DirectionalBlur resources (lazily allocated only when UI Motion Blur triggers)
    private static ID2D1Bitmap1? _mbBmp;
    private static ID2D1Effect? _mbEffect;
    private static int _mbBmpW, _mbBmpH;
    private static float _mbBmpScale;
    private static long _mbLastUsedAt;
    private static int _mbDepth, _mbSavedClips;
    private static R _mbRegion;
    private static float _mbStdDev, _mbAngle;
    private sealed class RadialSlot
    {
        public ID2D1GradientStopCollection? Stops;
        public ID2D1RadialGradientBrush? Brush;
        public float R, G, B, A;
        public void Dispose() { Brush?.Dispose(); Stops?.Dispose(); Brush = null; Stops = null; A = -1; }
    }
    private sealed class LinearSlot
    {
        public ID2D1GradientStopCollection? Stops;
        public ID2D1LinearGradientBrush? Brush;
        public uint Rgb;
        public float A0, A1, A2, Mid;
        public void Dispose() { Brush?.Dispose(); Stops?.Dispose(); Brush = null; Stops = null; A0 = -1; }
    }
    private static readonly RadialSlot[] _radials = [new(), new(), new(), new(), new(), new(), new(), new(), new()];
    private static readonly LinearSlot[] _linears = [new(), new(), new()];
    private static readonly GradientStop[] _radialStopsBuf = new GradientStop[6];
    private static readonly GradientStop[] _linearStopsBuf = new GradientStop[3];
    private static int _linearCursor;

    public static void Init(nint hwnd)
    {
        _hwnd = hwnd;
        _dw = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using (var fonts = _dw.GetSystemFontCollection(false))
            if (!fonts.FindFamilyName(IconFont, out _)) IconFont = "Segoe MDL2 Assets";
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "lucide.ttf");
            if (File.Exists(file))
            {
                using var factory = _dw.QueryInterface<IDWriteFactory5>();
                using var fontFile = _dw.CreateFontFileReference(file);
                using var builder = factory.CreateFontSetBuilder();
                builder.AddFontFile(fontFile);
                using var fontSet = builder.CreateFontSet();
                _lucideFonts = factory.CreateFontCollectionFromFontSet(fontSet);
                if (!_lucideFonts.FindFamilyName("lucide", out _)) throw new InvalidOperationException("Lucide font family missing");
                FluxTube.Services.Paths.Log("Lucide icons loaded");
            }
        }
        catch (Exception e) { _lucideFonts = null; FluxTube.Services.Paths.Log("Lucide icons unavailable: " + e.Message); }
        CreateDevice();
    }

    private static void CreateDevice()
    {
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_0], out var dev);
        if (hr.Failure || dev == null)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, [Vortice.Direct3D.FeatureLevel.Level_11_0], out dev).CheckError();
        _dev = dev!;
        using var dxgiDev = _dev.QueryInterface<IDXGIDevice>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        Win.RECT rc;
        Win.GetClientRect(_hwnd, &rc);
        PxW = Math.Max(1, rc.R - rc.L);
        PxH = Math.Max(1, rc.B - rc.T);
        var desc = new SwapChainDescription1
        {
            Width = (uint)PxW, Height = (uint)PxH, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 3,
            Scaling = Scaling.None, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        _swap = factory.CreateSwapChainForHwnd(_dev, _hwnd, desc);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
        _d2dDev = _d2dFactory.CreateDevice(dxgiDev);
        DC = _d2dDev.CreateDeviceContext(DeviceContextOptions.None);
        DC.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        // Ultra-crisp sub-pixel vector glyph rendering (enhanced contrast, smooth at any zoom & bass-shake transform)
        try
        {
            using var trp = _dw.CreateCustomRenderingParams(1.8f, 0.45f, 0.0f, PixelGeometry.Flat, Vortice.DirectWrite.RenderingMode.NaturalSymmetric);
            DC.TextRenderingParams = trp;
        }
        catch { }
        _scratchBrush = DC.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        CreateGrainTexture();
        CreateSheenTexture();
        CreateHalftoneTexture();
        CreateSketchTexture();
        CreateTarget();
        DeviceGeneration++;
        Occluded = false;
    }

    /// <summary>
    /// Pre-bakes a seamless 256×256 velvety bead-blasted micro-facet frost texture on GPU (zero per-frame CPU cost).
    /// Two periodic 3×3 high-pass passes remove low-frequency blotches (blue-noise spectrum), then top-left studio
    /// directional micro-facet shading gives tactile satin glass instead of raw white noise.
    /// </summary>
    private static void CreateGrainTexture()
    {
        _grainBrush?.Dispose();
        _grainBmp?.Dispose();
        const int sz = 256;
        const int mask = sz - 1;
        var h0 = new float[sz * sz];
        var h1 = new float[sz * sz];
        uint rng = 0x9E3779B9u;
        for (int i = 0; i < h0.Length; i++)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            h0[i] = (rng & 0xFFFF) * (1f / 65535f);
        }
        // Two periodic high-pass passes -> silky blue-noise satin frost (no gritty clumping)
        for (int pass = 0; pass < 2; pass++)
        {
            var src = pass == 0 ? h0 : h1;
            var dst = pass == 0 ? h1 : h0;
            for (int y = 0; y < sz; y++)
            {
                int ym = ((y - 1) & mask) * sz, y0 = y * sz, yp = ((y + 1) & mask) * sz;
                for (int x = 0; x < sz; x++)
                {
                    int xm = (x - 1) & mask, xp = (x + 1) & mask;
                    float avg = (src[ym + xm] + src[ym + x] + src[ym + xp]
                               + src[y0 + xm] + src[y0 + x] + src[y0 + xp]
                               + src[yp + xm] + src[yp + x] + src[yp + xp]) * (1f / 9f);
                    dst[y0 + x] = src[y0 + x] - avg;
                }
            }
        }
        // Directional top-left studio micro-facet lighting on the blue-noise frost heightfield
        var px = new byte[sz * sz * 4];
        for (int y = 0; y < sz; y++)
        {
            int ym = ((y - 1) & mask) * sz, y0 = y * sz, yp = ((y + 1) & mask) * sz;
            for (int x = 0; x < sz; x++)
            {
                int xm = (x - 1) & mask, xp = (x + 1) & mask;
                float c = h0[y0 + x];
                float dx = h0[y0 + xp] - h0[y0 + xm];
                float dy = h0[yp + x] - h0[ym + x];
                // Studio softbox grazing from upper-left (-0.7, -0.7) + crystal sparkle
                float facet = c * 1.45f - (dx + dy) * 0.62f;
                int o = (y0 + x) * 4;
                if (facet > 0.03f)
                {
                    float a = Math.Clamp((facet - 0.03f) * 2.4f, 0f, 1f);
                    // Warm pearl-white lit micro-beads (premultiplied BGRA)
                    px[o] = (byte)(a * 244f);
                    px[o + 1] = (byte)(a * 250f);
                    px[o + 2] = (byte)(a * 255f);
                    px[o + 3] = (byte)(a * 255f);
                }
                else if (facet < -0.03f)
                {
                    float a = Math.Clamp((-facet - 0.03f) * 2.1f, 0f, 1f);
                    // Deep velvety etched micro-pits (premultiplied BGRA)
                    px[o] = (byte)(a * 10f);
                    px[o + 1] = (byte)(a * 4f);
                    px[o + 2] = (byte)(a * 2f);
                    px[o + 3] = (byte)(a * 255f);
                }
            }
        }
        fixed (byte* p = px)
        {
            _grainBmp = DC.CreateBitmap(new SizeI(sz, sz), (nint)p, sz * 4,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
        _grainBrush = DC.CreateBitmapBrush(_grainBmp, new BitmapBrushProperties(ExtendMode.Wrap, ExtendMode.Wrap, BitmapInterpolationMode.NearestNeighbor));
    }

    /// <summary>Pre-bakes a 1×128 vertical Fresnel studio sheen brush for frosted glass panels (zero per-frame allocs).</summary>
    private static void CreateSheenTexture()
    {
        _sheenBrush?.Dispose();
        _sheenBmp?.Dispose();
        const int h = 128;
        var px = new byte[h * 4];
        for (int y = 0; y < h; y++)
        {
            float t = y / (float)(h - 1);
            int o = y * 4;
            if (t < 0.28f)
            {
                // Silky top overhead softbox catch-light
                float k = 1f - t / 0.28f;
                float a = MathF.Pow(k, 2.3f) * 0.095f;
                px[o] = (byte)(a * 250f);
                px[o + 1] = (byte)(a * 252f);
                px[o + 2] = (byte)(a * 255f);
                px[o + 3] = (byte)(a * 255f);
            }
            else if (t > 0.66f)
            {
                // Soft velvety lower contact depth
                float k = (t - 0.66f) / 0.34f;
                float a = MathF.Pow(k, 1.9f) * 0.075f;
                px[o] = (byte)(a * 8f);
                px[o + 1] = (byte)(a * 4f);
                px[o + 2] = (byte)(a * 2f);
                px[o + 3] = (byte)(a * 255f);
            }
        }
        fixed (byte* p = px)
        {
            _sheenBmp = DC.CreateBitmap(new SizeI(1, h), (nint)p, 4,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
        _sheenBrush = DC.CreateBitmapBrush(_sheenBmp, new BitmapBrushProperties(ExtendMode.Wrap, ExtendMode.Clamp, BitmapInterpolationMode.Linear));
    }

    /// <summary>Pre-bakes a crisp 16×16 manga/anime Ben-Day halftone dot screen tile for Toon glass surfaces.</summary>
    private static void CreateHalftoneTexture()
    {
        _halftoneBrush?.Dispose();
        _halftoneBmp?.Dispose();
        const int sz = 16;
        var px = new byte[sz * sz * 4];
        for (int y = 0; y < sz; y++)
        {
            for (int x = 0; x < sz; x++)
            {
                // Staggered 45° halftone dot lattice
                float dx1 = (x + 0.5f) - 4f, dy1 = (y + 0.5f) - 4f;
                float dx2 = (x + 0.5f) - 12f, dy2 = (y + 0.5f) - 12f;
                float d = MathF.Min(MathF.Sqrt(dx1 * dx1 + dy1 * dy1), MathF.Sqrt(dx2 * dx2 + dy2 * dy2));
                float a = Math.Clamp(1.65f - d, 0f, 1f);
                if (a > 0.01f)
                {
                    int o = (y * sz + x) * 4;
                    byte v = (byte)(a * 255f);
                    px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = v;
                }
            }
        }
        fixed (byte* p = px)
        {
            _halftoneBmp = DC.CreateBitmap(new SizeI(sz, sz), (nint)p, sz * 4,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
        _halftoneBrush = DC.CreateBitmapBrush(_halftoneBmp, new BitmapBrushProperties(ExtendMode.Wrap, ExtendMode.Wrap, BitmapInterpolationMode.Linear));
    }

    /// <summary>Pre-bakes a seamless 128×128 hand-drawn cross-hatch + watercolor paper tooth texture for Sketch shader.</summary>
    private static void CreateSketchTexture()
    {
        _sketchBrush?.Dispose();
        _sketchBmp?.Dispose();
        const int sz = 128;
        var px = new byte[sz * sz * 4];
        uint rng = 0x517CC1B7u;
        for (int y = 0; y < sz; y++)
        {
            for (int x = 0; x < sz; x++)
            {
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float n = ((rng & 0xFFFF) * (1f / 65535f) - 0.5f) * 2f;
                // Diagonal graphite pencil hatch strokes + subtle counter-hatch + organic paper fiber
                float d1 = MathF.Abs(((x + y) % 8) - 3.5f);
                float h1 = Math.Clamp(1.1f - d1, 0f, 1f) * (0.55f + 0.45f * MathF.Sin(x * 0.19f - y * 0.13f));
                float d2 = MathF.Abs((((x - y) & 15) - 7.5f));
                float h2 = Math.Clamp(0.95f - d2, 0f, 1f) * 0.48f * (0.5f + 0.5f * MathF.Cos(x * 0.11f + y * 0.23f));
                float paper = n * 0.35f;
                float val = Math.Clamp(h1 * 0.55f + h2 * 0.42f + MathF.Max(0f, paper) * 0.45f, 0f, 1f);
                if (val > 0.02f)
                {
                    int o = (y * sz + x) * 4;
                    byte r = (byte)(val * 245f);
                    byte g = (byte)(val * 240f);
                    byte b = (byte)(val * 232f);
                    byte a = (byte)(val * 255f);
                    px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = a;
                }
            }
        }
        fixed (byte* p = px)
        {
            _sketchBmp = DC.CreateBitmap(new SizeI(sz, sz), (nint)p, sz * 4,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
        _sketchBrush = DC.CreateBitmapBrush(_sketchBmp, new BitmapBrushProperties(ExtendMode.Wrap, ExtendMode.Wrap, BitmapInterpolationMode.Linear));
    }

    public static string ActiveFontName() => Services.AppSettings.Current.UiFont switch
    {
        "serif" => "Georgia",
        "tech" => "Bahnschrift",
        "mono" => "Consolas",
        "soft" => "Trebuchet MS",
        "comic" => "Comic Sans MS",
        _ => Font,
    };

    public static float Corner(float baseRadius) => Services.AppSettings.Current.UiCornerStyle switch
    {
        "sharp" => 0f,
        "round" => baseRadius * 1.45f,
        _ => baseRadius,
    };

    /// <summary>Clamps any corner radius to <= min(W, H)/2 so Direct2D ALWAYS draws true circular arcs, never squashed ovals.</summary>
    private static float ClampRad(R r, float radius)
        => radius <= 0f ? 0f : MathF.Min(radius, MathF.Min(MathF.Max(0f, r.W), MathF.Max(0f, r.H)) * 0.5f);

    private static ID2D1StrokeStyle DashStyle()
        => _dashStyle ??= _d2dFactory.CreateStrokeStyle(new StrokeStyleProperties
        {
            DashStyle = Vortice.Direct2D1.DashStyle.Custom,
            DashCap = CapStyle.Round,
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        }, [4.4f, 3.8f]);

    /// <summary>Pre-bakes a 384×240 linear-light studio backdrop ONCE per theme/mode change (~2ms CPU once, 0.04ms GPU/frame).</summary>
    private static void EnsureBackdropBaked(string mode)
    {
        uint bgQ = Th.Bg & 0x00FEFEFEu;
        uint brQ = Th.Brand & 0x00FEFEFEu;
        uint br2Q = Th.Brand2 & 0x00FEFEFEu;
        if (_backdropBmp != null && _backdropBg == bgQ && _backdropBrand == brQ && _backdropBrand2 == br2Q && _backdropLight == Th.Light && _backdropMode == mode)
            return;
        long now = Environment.TickCount64;
        if (_backdropBmp != null && now - _backdropBakedAt < 85 && _backdropLight == Th.Light && _backdropMode == mode)
            return;

        _backdropBakedAt = now;
        _backdropBg = bgQ;
        _backdropBrand = brQ;
        _backdropBrand2 = br2Q;
        _backdropLight = Th.Light;
        _backdropMode = mode;
        _backdropBmp?.Dispose();

        const int bw = 384, bh = 240;
        var px = new byte[bw * bh * 4];

        float bgr = ((Th.Bg >> 16) & 255) / 255f;
        float bgg = ((Th.Bg >> 8) & 255) / 255f;
        float bgb = (Th.Bg & 255) / 255f;
        float lBgR = bgr * bgr, lBgG = bgg * bgg, lBgB = bgb * bgb;

        float ar = ((Th.Brand >> 16) & 255) / 255f;
        float ag = ((Th.Brand >> 8) & 255) / 255f;
        float ab = (Th.Brand & 255) / 255f;
        uint b2 = Th.Brand2 != 0 ? Th.Brand2 : Th.Brand;
        float b2r = ((b2 >> 16) & 255) / 255f;
        float b2g = ((b2 >> 8) & 255) / 255f;
        float b2b = (b2 & 255) / 255f;

        float keyR = ar * ar * 0.62f + 0.28f, keyG = ag * ag * 0.62f + 0.27f, keyB = ab * ab * 0.62f + 0.30f;
        float rimR = b2r * b2r * 0.56f + 0.22f, rimG = b2g * b2g * 0.56f + 0.26f, rimB = b2b * b2b * 0.56f + 0.34f;
        float silkR = (ar * ar + b2r * b2r) * 0.26f + 0.32f;
        float silkG = (ag * ag + b2g * b2g) * 0.26f + 0.30f;
        float silkB = (ab * ab + b2b * b2b) * 0.26f + 0.38f;

        bool strong = mode != "soft";
        float mul = strong ? 1.0f : 0.52f;
        bool light = Th.Light;
        uint rng = 0xA3C59AC3u;

        for (int y = 0; y < bh; y++)
        {
            float v = y / (float)(bh - 1);
            float topGrazing = MathF.Exp(-v * 5.2f);
            for (int x = 0; x < bw; x++)
            {
                float u = x / (float)(bw - 1);
                float r, g, b;

                if (mode == "orbital")
                {
                    // Alien: Isolation deep-space planetary limb silhouette + atmospheric golden-white corona + pinpoint stars
                    float aspect = bw / (float)bh;
                    float pxU = (u - (-0.34f)) * aspect;
                    float pyV = (v - 0.54f);
                    float dist = MathF.Sqrt(pxU * pxU + pyV * pyV);
                    const float planetR = 1.18f;
                    float edge = dist - planetR;
                    if (edge < 0f)
                    {
                        // Inside planet disc: dark terminator with warm sunlit upper-right crescent atmosphere
                        float rim = MathF.Exp(edge * 16.0f);
                        float mid = MathF.Exp(edge * 4.5f);
                        float sunAngle = Math.Clamp(1.05f - MathF.Abs(v - 0.44f) * 1.45f, 0.15f, 1.0f);
                        float band = 0.85f + 0.15f * MathF.Sin((v * 18f + u * 5f));
                        r = lBgR * 0.45f + (ar * ar * 0.55f + 0.42f) * rim * 0.88f * sunAngle + (ar * ar * 0.32f + 0.12f) * mid * 0.34f * band * sunAngle;
                        g = lBgG * 0.45f + (ag * ag * 0.52f + 0.38f) * rim * 0.84f * sunAngle + (ag * ag * 0.26f + 0.09f) * mid * 0.28f * band * sunAngle;
                        b = lBgB * 0.45f + (ab * ab * 0.40f + 0.32f) * rim * 0.72f * sunAngle + (ab * ab * 0.16f + 0.06f) * mid * 0.20f * band * sunAngle;
                    }
                    else
                    {
                        // Outer space: crisp atmospheric scatter falloff + tiny distant star dust
                        float halo = MathF.Exp(-edge * 14.0f) * 0.58f + MathF.Exp(-edge * 4.2f) * 0.14f;
                        float sunAngle = Math.Clamp(1.05f - MathF.Abs(v - 0.44f) * 1.45f, 0.15f, 1.0f);
                        r = lBgR * 0.75f + (b2r * b2r * 0.45f + 0.38f) * halo * sunAngle;
                        g = lBgG * 0.75f + (b2g * b2g * 0.48f + 0.38f) * halo * sunAngle;
                        b = lBgB * 0.75f + (b2b * b2b * 0.56f + 0.48f) * halo * sunAngle;
                        // Deterministic crisp starfield pixels in deep space
                        uint sh = (uint)(x * 73856093 ^ y * 19349663) * 2654435761u;
                        if ((sh & 0x3FFu) == 0u && edge > 0.06f)
                        {
                            float sb = 0.22f + ((sh >> 10) & 255) / 255f * 0.55f;
                            r += sb; g += sb; b += sb * 1.08f;
                        }
                    }
                }
                else if (mode == "noir")
                {
                    // Resident Evil Requiem / Village moody foggy monochrome spotlight + atmospheric smoke vignette
                    float dx1 = (u - 0.36f) * 1.25f, dy1 = (v - 0.32f) * 1.45f;
                    float spot = MathF.Exp(-(dx1 * dx1 + dy1 * dy1) * 2.2f);
                    float fog = 0.5f + 0.5f * MathF.Sin(u * 4.7f + MathF.Sin(v * 3.1f)) * MathF.Cos(v * 3.8f - u * 1.7f);
                    float vx = (u - 0.5f) * 1.85f, vy = (v - 0.5f) * 1.85f;
                    float vig = MathF.Max(0.28f, 1f - 0.42f * (vx * vx + vy * vy));
                    float lift = (spot * 0.052f + fog * 0.020f) * vig;
                    r = lBgR * vig + lift * (0.95f + ar * 0.08f);
                    g = lBgG * vig + lift * (0.95f + ag * 0.05f);
                    b = lBgB * vig + lift * (0.98f + ab * 0.06f);
                }
                else if (mode == "neon")
                {
                    // Ultra Neon / Cyberpunk vivid dual-lobe cyan/magenta plasma edge glow on deep obsidian
                    float dx1 = (u - 0.08f) * 1.25f, dy1 = (v - 0.12f) * 1.35f;
                    float l1 = MathF.Exp(-(dx1 * dx1 + dy1 * dy1) * 2.4f);
                    float dx2 = (u - 0.92f) * 1.25f, dy2 = (v - 0.86f) * 1.35f;
                    float l2 = MathF.Exp(-(dx2 * dx2 + dy2 * dy2) * 2.3f);
                    float waveY = 0.45f + 0.18f * MathF.Sin(u * 3.4f - 0.6f);
                    float ribbon = MathF.Exp(-(v - waveY) * (v - waveY) * 24f) * 0.55f;
                    float vx = (u - 0.5f) * 1.6f, vy = (v - 0.5f) * 1.6f;
                    float vig = 1f - 0.28f * MathF.Min(1.2f, vx * vx + vy * vy);
                    r = lBgR * vig + ar * ar * (0.14f * l1 + 0.045f * ribbon) + b2r * b2r * (0.12f * l2 + 0.03f * ribbon);
                    g = lBgG * vig + ag * ag * (0.14f * l1 + 0.045f * ribbon) + b2g * b2g * (0.12f * l2 + 0.03f * ribbon);
                    b = lBgB * vig + ab * ab * (0.14f * l1 + 0.045f * ribbon) + b2b * b2b * (0.12f * l2 + 0.03f * ribbon);
                }
                else if (mode == "kawaii")
                {
                    if (light)
                    {
                        // Dreamy cotton-candy / strawberry-milk pastel clouds on light kawaii
                        float l1 = MathF.Exp(-((u - 0.15f) * (u - 0.15f) * 2.2f + (v - 0.15f) * (v - 0.15f) * 2.6f));
                        float l2 = MathF.Exp(-((u - 0.86f) * (u - 0.86f) * 2.0f + (v - 0.25f) * (v - 0.25f) * 2.4f));
                        float l3 = MathF.Exp(-((u - 0.55f) * (u - 0.55f) * 1.9f + (v - 0.92f) * (v - 0.92f) * 2.5f));
                        float k1 = 0.09f * l1, k2 = 0.085f * l2, k3 = 0.07f * l3;
                        r = lBgR * (1f - k1 - k2 - k3) + (ar * ar * 0.45f + 0.55f) * k1 + (b2r * b2r * 0.45f + 0.55f) * k2 + (ar * ar * 0.3f + 0.7f) * k3;
                        g = lBgG * (1f - k1 - k2 - k3) + (ag * ag * 0.45f + 0.55f) * k1 + (b2g * b2g * 0.45f + 0.55f) * k2 + (ag * ag * 0.3f + 0.7f) * k3;
                        b = lBgB * (1f - k1 - k2 - k3) + (ab * ab * 0.45f + 0.55f) * k1 + (b2b * b2b * 0.45f + 0.55f) * k2 + (ab * ab * 0.3f + 0.7f) * k3;
                    }
                    else
                    {
                        // Ultra-black obsidian dark kawaii: keeps center deep pitch black with subtle neon edge wisps (no muddy purple wash!)
                        float bgLum = lBgR * 0.299f + lBgG * 0.587f + lBgB * 0.114f;
                        bool pitchBlack = bgLum < 0.004f;
                        float spread = pitchBlack ? 5.6f : 3.0f;
                        float l1 = MathF.Exp(-((u - 0.04f) * (u - 0.04f) * spread + (v - 0.06f) * (v - 0.06f) * spread));
                        float l2 = MathF.Exp(-((u - 0.96f) * (u - 0.96f) * spread + (v - 0.14f) * (v - 0.14f) * spread));
                        float l3 = MathF.Exp(-((u - 0.52f) * (u - 0.52f) * (spread * 0.85f) + (v - 1.02f) * (v - 1.02f) * spread));
                        float k = pitchBlack ? 0.015f : 0.055f;
                        r = lBgR + ar * ar * (k * l1 + k * 0.65f * l3) + b2r * b2r * (k * 0.85f * l2);
                        g = lBgG + ag * ag * (k * l1 + k * 0.65f * l3) + b2g * b2g * (k * 0.85f * l2);
                        b = lBgB + ab * ab * (k * l1 + k * 0.65f * l3) + b2b * b2b * (k * 0.85f * l2);
                    }
                }
                else if (mode == "mesh")
                {
                    // Rich 4-corner liquid gradient mesh derived from Bg + Brand + Brand2
                    float wTL = (1f - u) * (1f - v), wTR = u * (1f - v), wBL = (1f - u) * v, wBR = u * v;
                    float k = light ? 0.10f : 0.11f;
                    r = lBgR * (1f - k * (wTL + wBR) * 0.4f) + ar * ar * k * (wTL * 0.9f + wBL * 0.35f) + b2r * b2r * k * (wBR * 0.9f + wTR * 0.45f);
                    g = lBgG * (1f - k * (wTL + wBR) * 0.4f) + ag * ag * k * (wTL * 0.9f + wBL * 0.35f) + b2g * b2g * k * (wBR * 0.9f + wTR * 0.45f);
                    b = lBgB * (1f - k * (wTL + wBR) * 0.4f) + ab * ab * k * (wTL * 0.9f + wBL * 0.35f) + b2b * b2b * k * (wBR * 0.9f + wTR * 0.45f);
                }
                else
                {
                    // Classic soft / matte frosted studio path-traced GI lightfield
                    float dx1 = (u - 0.13f) * 1.15f, dy1 = (v + 0.04f) * 1.35f;
                    float d1 = dx1 * dx1 + dy1 * dy1;
                    float l1 = MathF.Exp(-d1 * 2.5f) * 0.68f + 0.32f / (1f + d1 * 4.2f);
                    float dx2 = (u - 0.90f) * 1.10f, dy2 = (v - 0.16f) * 1.25f;
                    float d2 = dx2 * dx2 + dy2 * dy2;
                    float l2 = MathF.Exp(-d2 * 2.3f) * 0.65f + 0.35f / (1f + d2 * 3.8f);
                    float waveY = 0.30f + 0.16f * MathF.Sin(u * 2.65f - 0.45f) + 0.08f * u;
                    float dyC = v - waveY;
                    float caustic = MathF.Exp(-dyC * dyC * 16.0f) * (0.45f + 0.55f * MathF.Sin(u * MathF.PI));
                    float dx3 = (u - 0.48f) * 0.95f, dy3 = (v - 1.06f) * 1.55f;
                    float l3 = MathF.Exp(-(dx3 * dx3 + dy3 * dy3) * 2.4f);
                    float vx = (u - 0.5f) * 1.65f, vy = (v - 0.46f) * 1.55f;
                    float r2 = vx * vx + vy * vy;
                    float vig = light ? (1f - 0.045f * r2) : (1f - 0.22f * MathF.Min(1.2f, r2));

                    if (light)
                    {
                        float s1 = 0.055f * mul * l1, s2 = 0.045f * mul * l2, sc = 0.030f * mul * caustic, s3 = 0.030f * mul * l3;
                        r = lBgR * vig + keyR * s1 + rimR * s2 + silkR * sc + keyR * s3 + 0.025f * mul * topGrazing;
                        g = lBgG * vig + keyG * s1 + rimG * s2 + silkG * sc + keyG * s3 + 0.025f * mul * topGrazing;
                        b = lBgB * vig + keyB * s1 + rimB * s2 + silkB * sc + keyB * s3 + 0.028f * mul * topGrazing;
                    }
                    else
                    {
                        float bgLum = lBgR * 0.299f + lBgG * 0.587f + lBgB * 0.114f;
                        float darkScale = bgLum < 0.004f ? 0.32f : 1.0f;
                        float s1 = 0.046f * mul * darkScale * l1, s2 = 0.036f * mul * darkScale * l2, sc = 0.017f * mul * darkScale * caustic, s3 = 0.022f * mul * darkScale * l3;
                        float top = 0.012f * mul * darkScale * topGrazing;
                        r = lBgR * vig + keyR * s1 + rimR * s2 + silkR * sc + (keyR + rimR) * 0.5f * s3 + top;
                        g = lBgG * vig + keyG * s1 + rimG * s2 + silkG * sc + (keyG + rimG) * 0.5f * s3 + top;
                        b = lBgB * vig + keyB * s1 + rimB * s2 + silkB * sc + (keyB + rimB) * 0.5f * s3 + top * 1.15f;
                    }
                }

                r = r / (1f + 0.20f * MathF.Max(0f, r));
                g = g / (1f + 0.20f * MathF.Max(0f, g));
                b = b / (1f + 0.20f * MathF.Max(0f, b));

                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float d1n = (rng & 255) * (1f / 255f);
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float d2n = (rng & 255) * (1f / 255f);
                float dither = (d1n - d2n) * (0.75f / 255f);

                int o = (y * bw + x) * 4;
                px[o]     = (byte)(Math.Clamp(MathF.Sqrt(MathF.Max(0f, b)) + dither, 0f, 1f) * 255f);
                px[o + 1] = (byte)(Math.Clamp(MathF.Sqrt(MathF.Max(0f, g)) + dither, 0f, 1f) * 255f);
                px[o + 2] = (byte)(Math.Clamp(MathF.Sqrt(MathF.Max(0f, r)) + dither, 0f, 1f) * 255f);
                px[o + 3] = 255;
            }
        }
        fixed (byte* p = px)
        {
            _backdropBmp = DC.CreateBitmap(new SizeI(bw, bh), (nint)p, bw * 4,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
    }

    private static bool IsDeviceLost(int code)
        => code == unchecked((int)0x887A0005) // DXGI_ERROR_DEVICE_REMOVED
        || code == unchecked((int)0x887A0007) // DXGI_ERROR_DEVICE_RESET
        || code == unchecked((int)0x8899000C) // D2DERR_RECREATE_TARGET
        || code == unchecked((int)0x88990015); // D2DERR_WRONG_RESOURCE_DOMAIN

    private static void RecreateDevice()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
        _scratchBrush?.Dispose();
        _scratchBrush = null;
        _lastScratchArgb = 0;
        _grainBrush?.Dispose();
        _grainBrush = null;
        _grainBmp?.Dispose();
        _grainBmp = null;
        _sheenBrush?.Dispose();
        _sheenBrush = null;
        _sheenBmp?.Dispose();
        _sheenBmp = null;
        _halftoneBrush?.Dispose();
        _halftoneBrush = null;
        _halftoneBmp?.Dispose();
        _halftoneBmp = null;
        _sketchBrush?.Dispose();
        _sketchBrush = null;
        _sketchBmp?.Dispose();
        _sketchBmp = null;
        _backdropBmp?.Dispose();
        _backdropBmp = null;
        _backdropBg = 0;
        _dashStyle?.Dispose();
        _dashStyle = null;
        _imgBrush?.Dispose();
        _imgBrush = null;
        DisposeDanceAtlas();
        BgEngine.DisposeGpuResources();
        MusicArt.OnDeviceLost();
        DisposeMotionBlurResources();
        DisposeGlassResources();
        foreach (var s in _radials) s.Dispose();
        foreach (var l in _linears) l.Dispose();
        ClearTextCache();
        while (_prewarmed.TryDequeue(out var p)) p.Run.Layout?.Dispose();
        try { DeviceLost?.Invoke(); } catch { }
        DC.Target = null;
        _target?.Dispose();
        _target = null;
        DC.Dispose();
        _d2dDev.Dispose();
        _swap.Dispose();
        _dev.Dispose();
        CreateDevice();
    }

    public static bool TestOccluded()
    {
        if (_swap == null) return false;
        var hr = _swap.Present(0, PresentFlags.Test);
        Occluded = hr.Code == 0x087A0001; // DXGI_STATUS_OCCLUDED
        return Occluded;
    }

    private static void DisposeMotionBlurResources()
    {
        _mbEffect?.Dispose();
        _mbEffect = null;
        _mbBmp?.Dispose();
        _mbBmp = null;
        _mbBmpW = _mbBmpH = 0;
        _mbBmpScale = 0;
        _mbDepth = 0;
    }

    private static float _mbResScale = 1f;

    private static void EnsureMotionBlurResources()
    {
        if (PxW <= 0 || PxH <= 0) return;
        // Above 1080p (e.g. 2694x1824 / 4K), render scroll motion blur at half resolution -> 4x less VRAM & 4x faster GPU DirectionalBlur!
        float resScale = (long)PxW * PxH > 2_000_000L ? 0.5f : 1f;
        int tw = Math.Max(1, (int)(PxW * resScale));
        int th = Math.Max(1, (int)(PxH * resScale));
        float targetDpi = 96f * Scale * resScale;
        if (_mbBmp == null || _mbBmpW != tw || _mbBmpH != th || MathF.Abs(_mbBmpScale - targetDpi) > 0.05f)
        {
            _mbBmp?.Dispose();
            _mbBmp = DC.CreateBitmap(new SizeI(tw, th),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), targetDpi, targetDpi, BitmapOptions.Target));
            _mbBmpW = tw;
            _mbBmpH = th;
            _mbBmpScale = targetDpi;
            _mbResScale = resScale;
        }
        if (_mbEffect == null)
        {
            var ptr = DC.CreateEffect(EffectGuids.DirectionalBlur);
            if (ptr != 0) _mbEffect = new ID2D1Effect(ptr);
        }
    }

    /// <summary>
    /// Redirects drawing inside <paramref name="region"/> to an offscreen GPU target and composites it back
    /// through Direct2D's hardware DirectionalBlur shader oriented along the exact motion vector (vx, vy) in DIPs/sec.
    /// Zero overhead when stationary or disabled (returns false without touching DC.Target).
    /// </summary>
    public static bool PushMotionBlur(R region, float vx, float vy)
    {
        var s = Services.AppSettings.Current;
        if (!s.UiMotionBlur || _target == null || _mbDepth > 0 || MusicBounce.Active || region.W < 4 || region.H < 4)
            return false;

        float speed = MathF.Sqrt(vx * vx + vy * vy);
        if (speed < 240f) return false;

        float mult = s.UiMotionBlurStrength switch
        {
            "soft" => 0.00055f,
            "cinema" => 0.00165f,
            _ => 0.00095f, // "medium"
        };
        float maxDev = s.UiMotionBlurStrength switch
        {
            "soft" => 2.0f,
            "cinema" => 6.2f,
            _ => 3.6f,
        };
        float stdDev = Math.Clamp((speed - 240f) * mult, 0f, maxDev);
        if (stdDev < 0.25f) return false;

        var effRegion = Clip.Intersect(region);
        if (effRegion.W < 4 || effRegion.H < 4) return false;

        try
        {
            EnsureMotionBlurResources();
            if (_mbBmp == null || _mbEffect == null) return false;
            _mbLastUsedAt = Environment.TickCount64;

            int savedClips = _clipDepth;
            for (int i = savedClips - 1; i >= 0; i--) DC.PopAxisAlignedClip();

            DC.Target = _mbBmp;
            DC.SetDpi(96f * Scale * _mbResScale, 96f * Scale * _mbResScale);
            float pad = MathF.Ceiling(stdDev * 3.2f) + 6f;
            var clearR = new R(0, 0, W, H).Intersect(effRegion.Inset(-pad));
            DC.PushAxisAlignedClip(Raw(clearR), AntialiasMode.Aliased);
            DC.Clear(new Color4(0, 0, 0, 0));
            DC.PopAxisAlignedClip();

            for (int i = 0; i < savedClips; i++)
                DC.PushAxisAlignedClip(Raw(_clips[i]), AntialiasMode.Aliased);
            DC.PushAxisAlignedClip(Raw(effRegion), AntialiasMode.Aliased);

            _mbDepth = 1;
            _mbSavedClips = savedClips;
            _mbRegion = effRegion;
            _mbStdDev = stdDev;
            // Direct2D DirectionalBlur Angle expects [0, 360]; negative angles clamp to 0 (horizontal)!
            float ang = (MathF.Atan2(vy, vx) * (180f / MathF.PI)) % 180f;
            if (ang < 0f) ang += 180f;
            _mbAngle = ang;
            return true;
        }
        catch
        {
            DC.Target = _activeTarget ?? _target;
            DC.SetDpi(96f * Scale, 96f * Scale);
            _mbDepth = 0;
            return false;
        }
    }

    public static void PopMotionBlur(bool active)
    {
        if (!active || _mbDepth == 0) return;
        _mbDepth = 0;
        if (_target == null || _mbBmp == null || _mbEffect == null) return;

        DC.PopAxisAlignedClip();
        for (int i = _mbSavedClips - 1; i >= 0; i--) DC.PopAxisAlignedClip();

        DC.Target = _activeTarget ?? _target;
        DC.SetDpi(96f * Scale, 96f * Scale);

        for (int i = 0; i < _mbSavedClips; i++)
            DC.PushAxisAlignedClip(Raw(_clips[i]), AntialiasMode.Aliased);

        _mbEffect.SetInput(0, _mbBmp, true);
        float dev = _mbStdDev;
        float ang = _mbAngle;
        uint opt = (uint)DirectionalBlurOptimization.Speed;
        uint bm = (uint)BorderMode.Hard;
        _mbEffect.SetValue((uint)DirectionalBlurProperties.StandardDeviation, PropertyType.Float, &dev, sizeof(float));
        _mbEffect.SetValue((uint)DirectionalBlurProperties.Angle, PropertyType.Float, &ang, sizeof(float));
        _mbEffect.SetValue((uint)DirectionalBlurProperties.Optimization, PropertyType.Enum, &opt, sizeof(uint));
        _mbEffect.SetValue((uint)DirectionalBlurProperties.BorderMode, PropertyType.Enum, &bm, sizeof(uint));

        DC.PushAxisAlignedClip(Raw(_mbRegion), AntialiasMode.Aliased);
        using var outImg = _mbEffect.Output;
        DC.DrawImage(outImg, new Vector2(_mbRegion.X, _mbRegion.Y), Raw(_mbRegion),
            Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
        DC.PopAxisAlignedClip();
    }

    private static float TargetScale() => (Win.GetDpiForWindow(_hwnd) / 96f) * Math.Clamp(Services.AppSettings.Current.UiScale, 0.7f, 1.5f);

    private static void CreateTarget()
    {
        Scale = TargetScale();
        using var surface = _swap.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Ignore), 96 * Scale, 96 * Scale,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = DC.CreateBitmapFromDxgiSurface(surface, props);
        DC.Target = _target;
        DC.SetDpi(96 * Scale, 96 * Scale);
    }

    public static void ApplyScale()
    {
        if (DC == null || _hwnd == 0) return;
        var next = TargetScale();
        if (MathF.Abs(next - Scale) < 0.001f) return;
        DC.Target = null;
        _target?.Dispose();
        _target = null;
        _mbBmp?.Dispose();
        _mbBmp = null;
        DisposeGlassTargets();
        ClearTextCache();
        CreateTarget();
        App.Invalidate();
    }

    public static void Resize()
    {
        Win.RECT rc;
        Win.GetClientRect(_hwnd, &rc);
        int w = Math.Max(1, rc.R - rc.L), h = Math.Max(1, rc.B - rc.T);
        var scale = TargetScale();
        if (w == PxW && h == PxH && MathF.Abs(scale - Scale) < 0.001f) return;
        PxW = w; PxH = h;
        DC.Target = null;
        _target?.Dispose();
        _target = null;
        _mbBmp?.Dispose();
        _mbBmp = null;
        DisposeGlassTargets();
        var hr = _swap.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None);
        if (hr.Failure && IsDeviceLost(hr.Code))
        {
            Services.Paths.Log($"gpu device lost on resize 0x{hr.Code:X}, recreating");
            RecreateDevice();
            return;
        }
        hr.CheckError();
        if (MathF.Abs(scale - Scale) >= 0.001f) ClearTextCache();
        CreateTarget();
    }

    private static bool NeedsSceneBuffer()
    {
        var sh = Services.AppSettings.Current.UiGlassShader;
        return !string.IsNullOrEmpty(sh) && sh != "off";
    }

    private static void DisposeGlassTargets()
    {
        _activeTarget = null;
        _glassReady = false;
        _glassBrush?.Dispose(); _glassBrush = null;
        _glassBlurBmp?.Dispose(); _glassBlurBmp = null;
        _glassDownBmp?.Dispose(); _glassDownBmp = null;
        _sceneBmp?.Dispose(); _sceneBmp = null;
        _sceneW = _sceneH = _glassW = _glassH = 0;
        _sceneScale = 0;
    }

    private static void DisposeGlassResources()
    {
        DisposeGlassTargets();
        _glassBlurFx?.Dispose(); _glassBlurFx = null;
        _glassSatFx?.Dispose(); _glassSatFx = null;
        _glassPostFx?.Dispose(); _glassPostFx = null;
        _glassEdgeFx?.Dispose(); _glassEdgeFx = null;
        _glassTurbFx?.Dispose(); _glassTurbFx = null;
        _glassDispFx?.Dispose(); _glassDispFx = null;
    }

    private static void EnsureGlassResources()
    {
        if (PxW <= 0 || PxH <= 0) return;
        if (_sceneBmp == null || _sceneW != PxW || _sceneH != PxH || MathF.Abs(_sceneScale - Scale) > 0.001f)
        {
            DisposeGlassTargets();
            _sceneBmp = DC.CreateBitmap(new SizeI(PxW, PxH),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96 * Scale, 96 * Scale, BitmapOptions.Target));
            _sceneW = PxW; _sceneH = PxH; _sceneScale = Scale;
            _glassW = Math.Max(64, PxW / 4);
            _glassH = Math.Max(64, PxH / 4);
            _glassDownBmp = DC.CreateBitmap(new SizeI(_glassW, _glassH),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.Target));
            _glassBlurBmp = DC.CreateBitmap(new SizeI(_glassW, _glassH),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.Target));
            _glassBrush = DC.CreateBitmapBrush(_glassBlurBmp, new BitmapBrushProperties(ExtendMode.Clamp, ExtendMode.Clamp, BitmapInterpolationMode.Linear));
        }
        if (_glassBlurFx == null) { var p = DC.CreateEffect(EffectGuids.GaussianBlur); if (p != 0) _glassBlurFx = new ID2D1Effect(p); }
        if (_glassSatFx == null) { var p = DC.CreateEffect(EffectGuids.Saturation); if (p != 0) _glassSatFx = new ID2D1Effect(p); }
    }

    private static ID2D1Effect? EnsureEffect(ref ID2D1Effect? slot, Guid guid)
    {
        if (slot != null) return slot;
        var p = DC.CreateEffect(guid);
        if (p != 0) slot = new ID2D1Effect(p);
        return slot;
    }

    /// <summary>
    /// Applies real hardware Direct2D GPU stylization shaders (Toon Cel + Sobel Ink, Pop Posterize, or Graphite Sketch)
    /// directly onto the rendered backdrop layer inside _sceneBmp BEFORE sharp vector UI text is drawn on top.
    /// </summary>
    public static void StylizeBackdropIfNeeded()
    {
        if (_activeTarget != _sceneBmp || _sceneBmp == null || _glassDownBmp == null || _mbDepth > 0) return;
        var sh = Services.AppSettings.Current.UiGlassShader;
        if (sh is not ("toon" or "poster" or "sketch")) return;

        var savedTf = DC.Transform;
        int savedClips = _clipDepth;
        for (int i = savedClips - 1; i >= 0; i--) DC.PopAxisAlignedClip();
        try
        {
            DC.Transform = Matrix3x2.Identity;
            DC.SetDpi(96, 96);
            // Copy backdrop into quarter-res scratch so shader cost is ~0.08ms and never reads/writes the same target
            DC.Target = _glassDownBmp;
            DC.DrawBitmap(_sceneBmp, new Vortice.RawRectF(0, 0, _glassW, _glassH), 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);

            if (sh == "toon")
            {
                var post = EnsureEffect(ref _glassPostFx, EffectGuids.Posterize);
                var edge = EnsureEffect(ref _glassEdgeFx, EffectGuids.EdgeDetection);
                if (post != null)
                {
                    uint levels = 6;
                    post.SetInput(0, _glassDownBmp, true);
                    post.SetValue((uint)PosterizeProperties.RedValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    post.SetValue((uint)PosterizeProperties.GreenValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    post.SetValue((uint)PosterizeProperties.BlueValueCount, PropertyType.UInt32, &levels, sizeof(uint));

                    DC.Target = _sceneBmp;
                    DC.Transform = Matrix3x2.CreateScale(PxW / (float)_glassW, PxH / (float)_glassH);
                    using var postOut = post.Output;
                    DC.DrawImage(postOut, Vector2.Zero, new Vortice.RawRectF(0, 0, _glassW, _glassH),
                        Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);

                    if (edge != null)
                    {
                        float strength = 0.55f, br = 0.5f;
                        uint mode = 0, overlay = 1, alphaMode = 0;
                        edge.SetInputEffect(0, post, true);
                        edge.SetValue((uint)EdgeDetectionProperties.Strength, PropertyType.Float, &strength, sizeof(float));
                        edge.SetValue((uint)EdgeDetectionProperties.BlurRadius, PropertyType.Float, &br, sizeof(float));
                        edge.SetValue((uint)EdgeDetectionProperties.Mode, PropertyType.Enum, &mode, sizeof(uint));
                        edge.SetValue((uint)EdgeDetectionProperties.OverlayEdges, PropertyType.Bool, &overlay, sizeof(uint));
                        edge.SetValue((uint)EdgeDetectionProperties.AlphaMode, PropertyType.Enum, &alphaMode, sizeof(uint));
                        using var edgeOut = edge.Output;
                        DC.DrawImage(edgeOut, Vector2.Zero, new Vortice.RawRectF(0, 0, _glassW, _glassH),
                            Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
                    }
                }
            }
            else if (sh == "poster")
            {
                var post = EnsureEffect(ref _glassPostFx, EffectGuids.Posterize);
                if (post != null && _glassSatFx != null)
                {
                    uint levels = 5;
                    post.SetInput(0, _glassDownBmp, true);
                    post.SetValue((uint)PosterizeProperties.RedValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    post.SetValue((uint)PosterizeProperties.GreenValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    post.SetValue((uint)PosterizeProperties.BlueValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    float sat = 1.38f;
                    _glassSatFx.SetInputEffect(0, post, true);
                    _glassSatFx.SetValue((uint)SaturationProperties.Saturation, PropertyType.Float, &sat, sizeof(float));

                    DC.Target = _sceneBmp;
                    DC.Transform = Matrix3x2.CreateScale(PxW / (float)_glassW, PxH / (float)_glassH);
                    using var satOut = _glassSatFx.Output;
                    DC.DrawImage(satOut, Vector2.Zero, new Vortice.RawRectF(0, 0, _glassW, _glassH),
                        Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
                }
            }
            else if (sh == "sketch")
            {
                var edge = EnsureEffect(ref _glassEdgeFx, EffectGuids.EdgeDetection);
                if (edge != null)
                {
                    float strength = 0.65f, br = 0.35f;
                    uint mode = 1 /*Prewitt*/, overlay = 1, alphaMode = 0;
                    edge.SetInput(0, _glassDownBmp, true);
                    edge.SetValue((uint)EdgeDetectionProperties.Strength, PropertyType.Float, &strength, sizeof(float));
                    edge.SetValue((uint)EdgeDetectionProperties.BlurRadius, PropertyType.Float, &br, sizeof(float));
                    edge.SetValue((uint)EdgeDetectionProperties.Mode, PropertyType.Enum, &mode, sizeof(uint));
                    edge.SetValue((uint)EdgeDetectionProperties.OverlayEdges, PropertyType.Bool, &overlay, sizeof(uint));
                    edge.SetValue((uint)EdgeDetectionProperties.AlphaMode, PropertyType.Enum, &alphaMode, sizeof(uint));

                    DC.Target = _sceneBmp;
                    DC.Transform = Matrix3x2.CreateScale(PxW / (float)_glassW, PxH / (float)_glassH);
                    using var edgeOut = edge.Output;
                    DC.DrawImage(edgeOut, Vector2.Zero, new Vortice.RawRectF(0, 0, _glassW, _glassH),
                        Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
                }
            }
        }
        catch { }
        finally
        {
            DC.Target = _activeTarget;
            DC.SetDpi(96 * Scale, 96 * Scale);
            DC.Transform = savedTf;
            for (int i = 0; i < savedClips; i++) DC.PushAxisAlignedClip(Raw(_clips[i]), AntialiasMode.Aliased);
        }

        // Subtle graphic texture finish on backdrop (manga Ben-Day dots for Toon, pencil cross-hatch for Sketch)
        if (sh == "toon" && _halftoneBrush != null)
        {
            _halftoneBrush.Transform = Matrix3x2.CreateScale(0.65f / MathF.Max(0.5f, Scale));
            _halftoneBrush.Opacity = Th.Light ? 0.045f : 0.032f;
            DC.FillRectangle(new Vortice.RawRectF(0, 0, W, H), _halftoneBrush);
        }
        else if (sh == "sketch" && _sketchBrush != null)
        {
            _sketchBrush.Transform = Matrix3x2.CreateScale(0.85f / MathF.Max(0.5f, Scale));
            _sketchBrush.Opacity = Th.Light ? 0.085f : 0.062f;
            DC.FillRectangle(new Vortice.RawRectF(0, 0, W, H), _sketchBrush);
        }
    }

    /// <summary>
    /// Captures the live rendered scene behind upcoming glass chrome/modals into a quarter-res hardware GPU blurred &amp;
    /// shader-stylized surface (~0.12 ms GPU). Subsequent FillFrosted() calls stencil-mask this texture strictly inside
    /// their exact rounded geometry so thumbnails, artwork, and aurora bleed &amp; adapt dynamically under every glass panel!
    /// </summary>
    public static void CaptureGlassLayer()
    {
        if (_activeTarget != _sceneBmp || _sceneBmp == null || _glassDownBmp == null || _glassBlurBmp == null || _glassBlurFx == null || _mbDepth > 0)
            return;

        var sh = Services.AppSettings.Current.UiGlassShader;
        if (string.IsNullOrEmpty(sh) || sh == "off") { _glassReady = false; return; }

        var savedTf = DC.Transform;
        int savedClips = _clipDepth;
        for (int i = savedClips - 1; i >= 0; i--) DC.PopAxisAlignedClip();
        try
        {
            DC.Transform = Matrix3x2.Identity;
            DC.SetDpi(96, 96);

            // 1. Quarter-res downsample of current scene (16x fewer pixels -> blistering fast GPU blur!)
            DC.Target = _glassDownBmp;
            DC.DrawBitmap(_sceneBmp, new Vortice.RawRectF(0, 0, _glassW, _glassH), 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);

            // 2. Hardware GPU Blur + active glass shader graph into _glassBlurBmp
            DC.Target = _glassBlurBmp;
            float stdDev = sh switch
            {
                "liquid" => 3.2f,
                "toon" => 2.8f,
                "poster" => 3.4f,
                "sketch" => 3.8f,
                _ => 5.4f, // "acrylic": silky ~22px full-res frosted glass blur!
            };
            uint opt = (uint)GaussianBlurOptimization.Speed;
            uint bm = (uint)BorderMode.Hard;
            _glassBlurFx.SetInput(0, _glassDownBmp, true);
            _glassBlurFx.SetValue((uint)GaussianBlurProperties.StandardDeviation, PropertyType.Float, &stdDev, sizeof(float));
            _glassBlurFx.SetValue((uint)GaussianBlurProperties.Optimization, PropertyType.Enum, &opt, sizeof(uint));
            _glassBlurFx.SetValue((uint)GaussianBlurProperties.BorderMode, PropertyType.Enum, &bm, sizeof(uint));

            ID2D1Effect tail = _glassBlurFx;

            if (_glassSatFx != null)
            {
                float sat = sh switch { "liquid" => 1.44f, "toon" or "poster" => 1.48f, "sketch" => 0.72f, _ => 1.32f };
                _glassSatFx.SetInputEffect(0, tail, true);
                _glassSatFx.SetValue((uint)SaturationProperties.Saturation, PropertyType.Float, &sat, sizeof(float));
                tail = _glassSatFx;
            }

            if (sh == "liquid")
            {
                var turb = EnsureEffect(ref _glassTurbFx, EffectGuids.Turbulence);
                var disp = EnsureEffect(ref _glassDispFx, EffectGuids.DisplacementMap);
                if (turb != null && disp != null)
                {
                    var freq = new Vector2(0.022f, 0.028f);
                    var sz = new Vector2(_glassW, _glassH);
                    uint oct = 2, noise = 0;
                    int seed = 7;
                    turb.SetValue((uint)TurbulenceProperties.BaseFrequency, PropertyType.Vector2, &freq, (uint)sizeof(Vector2));
                    turb.SetValue((uint)TurbulenceProperties.Size, PropertyType.Vector2, &sz, (uint)sizeof(Vector2));
                    turb.SetValue((uint)TurbulenceProperties.NumOctaves, PropertyType.UInt32, &oct, sizeof(uint));
                    turb.SetValue((uint)TurbulenceProperties.Seed, PropertyType.Int32, &seed, sizeof(int));
                    turb.SetValue((uint)TurbulenceProperties.Noise, PropertyType.Enum, &noise, sizeof(uint));

                    float scale = 12.5f;
                    uint xc = 0, yc = 1;
                    disp.SetInputEffect(0, tail, true);
                    disp.SetInputEffect(1, turb, true);
                    disp.SetValue((uint)DisplacementMapProperties.Scale, PropertyType.Float, &scale, sizeof(float));
                    disp.SetValue((uint)DisplacementMapProperties.XChannelSelect, PropertyType.Enum, &xc, sizeof(uint));
                    disp.SetValue((uint)DisplacementMapProperties.YChannelSelect, PropertyType.Enum, &yc, sizeof(uint));
                    tail = disp;
                }
            }
            else if (sh is "toon" or "poster")
            {
                var post = EnsureEffect(ref _glassPostFx, EffectGuids.Posterize);
                if (post != null)
                {
                    uint levels = sh == "poster" ? 4u : 5u;
                    post.SetInputEffect(0, tail, true);
                    post.SetValue((uint)PosterizeProperties.RedValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    post.SetValue((uint)PosterizeProperties.GreenValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    post.SetValue((uint)PosterizeProperties.BlueValueCount, PropertyType.UInt32, &levels, sizeof(uint));
                    tail = post;
                }
            }

            using var outImg = tail.Output;
            DC.DrawImage(outImg, Vector2.Zero, new Vortice.RawRectF(0, 0, _glassW, _glassH),
                Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
            _glassReady = true;
        }
        catch
        {
            _glassReady = false;
        }
        finally
        {
            DC.Target = _activeTarget;
            DC.SetDpi(96 * Scale, 96 * Scale);
            DC.Transform = savedTf;
            for (int i = 0; i < savedClips; i++) DC.PushAxisAlignedClip(Raw(_clips[i]), AntialiasMode.Aliased);
        }
    }

    public static void Begin(uint clear)
    {
        Frame++;
        _glassReady = false;
        if (NeedsSceneBuffer())
        {
            EnsureGlassResources();
            _activeTarget = _sceneBmp ?? _target;
        }
        else
        {
            if (_sceneBmp != null) DisposeGlassTargets();
            _activeTarget = _target;
        }
        _linearCursor = 0;
        DC.Target = _activeTarget;
        DC.BeginDraw();
        DC.Clear(ToColor(clear));
        _clipDepth = 0;
        _shapeMs = 0;
        while (_prewarmed.TryDequeue(out var p))
        {
            if (_cur.ContainsKey(p.Key) || _prev.ContainsKey(p.Key)) p.Run.Layout?.Dispose();
            else _cur[p.Key] = p.Run;
        }
    }

    /// <summary>False when the device was lost and recreated; the caller redraws.</summary>
    public static bool End()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_activeTarget == _sceneBmp && _sceneBmp != null && _target != null)
        {
            for (int i = _clipDepth - 1; i >= 0; i--) DC.PopAxisAlignedClip();
            _clipDepth = 0;
            DC.Transform = Matrix3x2.Identity;
            DC.Target = _target;
            // Bit-exact 1:1 physical pixel blit preserves razor-sharp DirectWrite vector text!
            DC.DrawBitmap(_sceneBmp, new Vortice.RawRectF(0, 0, W, H), 1f, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
        }
        var hr = DC.EndDraw();
        FlushMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (hr.Success) hr = _swap.Present(1, PresentFlags.None);
        Occluded = hr.Code == 0x087A0001; // DXGI_STATUS_OCCLUDED
        if (hr.Failure && IsDeviceLost(hr.Code))
        {
            Services.Paths.Log($"gpu device lost 0x{hr.Code:X}, recreating");
            RecreateDevice();
            return false;
        }
        TrimTextCache();
        return true;
    }

    // ---------------- colours and shapes ----------------

    private static readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    private static Color4 ToColor(uint argb)
        => new(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, (argb >> 24) / 255f);

    private static uint _lastScratchArgb;

    /// <summary>Zero-allocation reusable brush for immediate Draw/Fill/Stroke calls (skips COM SetColor when color repeats).</summary>
    private static ID2D1SolidColorBrush Scratch(uint argb)
    {
        if (_scratchBrush == null)
        {
            _scratchBrush = DC.CreateSolidColorBrush(ToColor(argb));
            _lastScratchArgb = argb;
            return _scratchBrush;
        }
        if (_lastScratchArgb != argb)
        {
            _scratchBrush.Color = ToColor(argb);
            _lastScratchArgb = argb;
        }
        return _scratchBrush;
    }

    /// <summary>Persistent solid brush (required for IDWriteTextLayout.SetDrawingEffect which stores a COM ref).</summary>
    public static ID2D1SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var b)) return b;
        if (_brushes.Count > 512)
        {
            foreach (var old in _brushes.Values) old.Dispose();
            _brushes.Clear();
        }
        return _brushes[argb] = DC.CreateSolidColorBrush(ToColor(argb));
    }

    /// <summary>Same colour with alpha scaled by a (0..1).</summary>
    public static uint Fade(uint argb, float a)
        => (argb & 0x00FFFFFF) | ((uint)Math.Clamp((argb >> 24) * a, 0, 255) << 24);

    public static float Snap(float v) => MathF.Round(v * Scale) / Scale;

    private static Vortice.RawRectF Raw(R r) => new(r.X, r.Y, r.Right, r.Bottom);

    public static void Fill(R r, uint color) => DC.FillRectangle(Raw(r), Scratch(color));

    public static void Fill(R r, uint color, float radius)
    {
        float cr = ClampRad(r, radius);
        if (cr <= 0) { Fill(r, color); return; }
        var rr = new RoundedRectangle { Rect = Raw(r), RadiusX = cr, RadiusY = cr };
        DC.FillRoundedRectangle(rr, Scratch(color));
    }

    public static void Stroke(R r, uint color, float radius, float width = 1)
    {
        var ir = r.Inset(width * 0.5f);
        float cr = ClampRad(ir, radius - width * 0.5f);
        var rr = new RoundedRectangle { Rect = Raw(ir), RadiusX = cr, RadiusY = cr };
        DC.DrawRoundedRectangle(rr, Scratch(color), width);
    }

    public static void StrokeDashed(R r, uint color, float radius, float width = 1.25f)
    {
        var ir = r.Inset(width * 0.5f);
        float cr = ClampRad(ir, radius - width * 0.5f);
        var rr = new RoundedRectangle { Rect = Raw(ir), RadiusX = cr, RadiusY = cr };
        DC.DrawRoundedRectangle(rr, Scratch(color), width, DashStyle());
    }

    /// <summary>Ultra-cheap 2-stroke GPU neon bloom halo (~0.01ms, zero blur pass).</summary>
    public static void NeonGlow(R r, float radius, uint color, float strength = 1f)
    {
        float cr = ClampRad(r, radius);
        var haloR = r.Inset(-1.2f);
        float hcr = ClampRad(haloR, cr + 1.2f);
        var halo = new RoundedRectangle { Rect = Raw(haloR), RadiusX = hcr, RadiusY = hcr };
        DC.DrawRoundedRectangle(halo, Scratch(Fade(color, 0.22f * strength)), 4.2f);
        var rimR = r.Inset(0.5f);
        float rcr = ClampRad(rimR, cr - 0.5f);
        var rim = new RoundedRectangle { Rect = Raw(rimR), RadiusX = rcr, RadiusY = rcr };
        DC.DrawRoundedRectangle(rim, Scratch(Fade(color, 0.82f * strength)), 1.25f);
    }

    /// <summary>Draws modular video/content card frame matching UiCardStyle + active GPU Glass/Graphics Shader.</summary>
    public static void DrawCardFrame(R box, float radius, bool hovered, out uint surfaceBehind)
    {
        float cr = ClampRad(box, Corner(radius));
        var s = Services.AppSettings.Current;
        var style = s.UiCardStyle;
        var sh = s.UiGlassShader;
        bool hasGlass = _glassReady && _glassBrush != null && sh != "off";

        switch (style)
        {
            case "dashed":
                if (hasGlass) FillFrosted(box, Th.Card, radius, hovered ? 0.78f : 0.62f, border: false);
                else Fill(box, Fade(Th.Card, hovered ? 0.96f : 0.86f), cr);
                StrokeDashed(box, Fade(Th.Brand, hovered ? 0.78f : 0.42f), cr, 1.3f);
                if (hovered && s.UiNeonGlow) NeonGlow(box, cr, Th.Brand, 0.48f);
                surfaceBehind = Th.Card;
                break;
            case "brutal":
                Fill(new R(box.X + 4, box.Y + 4, box.W, box.H), Fade(Th.Ink, hovered ? 0.28f : 0.14f), cr);
                Fill(box, Th.Card, cr);
                Stroke(box, hovered ? Th.Brand : Fade(Th.Ink, 0.72f), cr, hovered ? 2.0f : 1.5f);
                surfaceBehind = Th.Card;
                break;
            case "framed":
                if (hasGlass) FillFrosted(box, Th.Card, radius, hovered ? 0.78f : 0.56f, border: false);
                else Fill(box, Fade(Th.Card, hovered ? 0.86f : 0.50f), cr);
                Stroke(box, hovered ? Fade(Th.Text, 0.68f) : Th.Line, cr, 1f);
                if (hovered) Fill(new R(box.X + 1, box.Y + 1, box.W - 2, 1.2f), Fade(Th.Brand, 0.78f));
                surfaceBehind = Mix(Th.Bg, Th.Card, hovered ? 0.86f : 0.50f);
                break;
            case "neon":
                if (hasGlass) FillFrosted(box, Th.Card, radius, hovered ? 0.78f : 0.56f, border: false);
                else Fill(box, Fade(Th.Card, hovered ? 0.86f : 0.58f), cr);
                uint rimCol = hovered ? Th.Brand : (Th.Brand2 != 0 ? Th.Brand2 : Th.Brand);
                if (hovered || s.UiNeonGlow) NeonGlow(box, cr, rimCol, hovered ? 0.82f : 0.32f);
                else Stroke(box, Fade(rimCol, 0.38f), cr, 1f);
                surfaceBehind = Mix(Th.Bg, Th.Card, hovered ? 0.86f : 0.58f);
                break;
            default:
                if (sh is "toon" or "sketch")
                {
                    if (sh == "toon")
                    {
                        Fill(new R(box.X + 3, box.Y + 3, box.W, box.H), Fade(Th.Light ? 0xFF000000 : Th.Brand, hovered ? 0.42f : 0.20f), cr);
                        FillFrosted(box, Th.Card, radius, hovered ? 0.76f : 0.56f, border: false);
                        Stroke(box, Fade(hovered ? Th.Brand : Th.Ink, hovered ? 0.90f : 0.35f), cr, hovered ? 1.8f : 1.3f);
                    }
                    else
                    {
                        FillFrosted(box, Th.Card, radius, hovered ? 0.74f : 0.54f, border: false);
                        Stroke(box, Fade(Th.Ink, hovered ? 0.55f : 0.25f), cr, 1.1f);
                        Stroke(box.Inset(1.5f), Fade(Th.Brand, hovered ? 0.36f : 0.14f), MathF.Max(0, cr - 1.5f), 0.9f);
                    }
                    surfaceBehind = Th.Card;
                }
                else
                {
                    surfaceBehind = Th.Bg;
                }
                break;
        }
    }

    public static void Circle(float cx, float cy, float radius, uint color)
        => DC.FillEllipse(new Ellipse(new Vector2(cx, cy), radius, radius), Scratch(color));

    public static void Line(float x1, float y1, float x2, float y2, uint color, float width = 1.5f)
        => DC.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), Scratch(color), width);

    /// <summary>Hardware-accelerated Direct2D radial Gaussian-like light glow with per-zone COM brush caching (zero per-frame allocs when steady).</summary>
    public static void RadialGlow(float cx, float cy, float rx, float ry, float r, float g, float b, float alpha, int slot = 0)
    {
        if (alpha <= 0.004f || rx <= 1 || ry <= 1) return;
        var s = _radials[(uint)slot % (uint)_radials.Length];
        if (s.Brush == null || MathF.Abs(s.R - r) > 0.004f || MathF.Abs(s.G - g) > 0.004f || MathF.Abs(s.B - b) > 0.004f || MathF.Abs(s.A - alpha) > 0.003f)
        {
            s.Dispose();
            _radialStopsBuf[0] = new(0.00f, new Color4(r, g, b, alpha));
            _radialStopsBuf[1] = new(0.20f, new Color4(r, g, b, alpha * 0.64f));
            _radialStopsBuf[2] = new(0.42f, new Color4(r, g, b, alpha * 0.28f));
            _radialStopsBuf[3] = new(0.65f, new Color4(r, g, b, alpha * 0.08f));
            _radialStopsBuf[4] = new(0.84f, new Color4(r, g, b, alpha * 0.015f));
            _radialStopsBuf[5] = new(1.00f, new Color4(r, g, b, 0f));
            s.Stops = DC.CreateGradientStopCollection(_radialStopsBuf, Gamma.StandardRgb, ExtendMode.Clamp);
            s.Brush = DC.CreateRadialGradientBrush(new RadialGradientBrushProperties(new Vector2(cx, cy), Vector2.Zero, rx, ry), s.Stops);
            s.R = r; s.G = g; s.B = b; s.A = alpha;
        }
        else
        {
            s.Brush.Center = new Vector2(cx, cy);
            s.Brush.RadiusX = rx;
            s.Brush.RadiusY = ry;
        }
        DC.FillEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), s.Brush);
    }

    /// <summary>
    /// Draws the complete zero-cost GPU backdrop stack:
    /// 1) Pre-baked backdrop lighting (soft/matte/mesh/kawaii/neon/noir/orbital — 1 DrawBitmap, 0.04ms)
    /// 2) Optional custom user wallpaper image (pre-scaled &amp; pre-blurred once — 1 DrawBitmap, 0.04ms)
    /// 3) Optional seamless SDF background pattern (Cat Paws, Lines, Cyber Grid, Circuit, Sakura, Noir Board... — 1 FillRectangle, 0.02ms)
    /// 4) Tactile 1:1 physical-pixel frost / film grain when glass or noir is enabled.
    /// </summary>
    public static void DrawFrostedBackdrop(float w, float h)
    {
        var mode = Services.AppSettings.Current.FrostedGlass;
        if (mode != "off")
        {
            EnsureBackdropBaked(mode);
            if (_backdropBmp != null)
                DC.DrawBitmap(_backdropBmp, new Vortice.RawRectF(0, 0, w, h), 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        }

        // Custom wallpaper image (works WITH glass or INSTEAD of glass!)
        BgEngine.DrawWallpaper(DC, w, h);

        // Bass-reactive club/studio aurora glow across the entire backdrop in Audio-Only Music Mode (~0.02 ms GPU!)
        var s = Services.AppSettings.Current;
        if (s.AudioOnlyMode && s.MusicBassShake && Shell.Current is not Pages.ShortsPage && Player.MpvPlayer.Instance.HasMedia && !Player.MpvPlayer.Instance.Paused && !Player.MpvPlayer.Instance.Muted && !App.Test)
        {
            AudioAnalyzer.RequestFrame(force: true);
            float bass = AudioAnalyzer.BassEnergy;
            float mid = AudioAnalyzer.MidEnergy;
            float hi = AudioAnalyzer.HighEnergy;
            if (bass > 0.02f || mid > 0.02f)
            {
                float r1 = ((Th.Brand >> 16) & 255) / 255f, g1 = ((Th.Brand >> 8) & 255) / 255f, b1 = (Th.Brand & 255) / 255f;
                uint c2 = Th.Brand2 != 0 ? Th.Brand2 : Th.Brand;
                float r2 = ((c2 >> 16) & 255) / 255f, g2 = ((c2 >> 8) & 255) / 255f, b2 = (c2 & 255) / 255f;
                float scaleMul = Th.Light ? 0.55f : 1.0f;
                RadialGlow(w * 0.22f, h * 0.26f, w * (0.34f + bass * 0.26f), h * (0.36f + bass * 0.28f), r1, g1, b1, Math.Clamp(bass * 0.30f * scaleMul, 0f, 0.36f), slot: 5);
                RadialGlow(w * 0.78f, h * 0.72f, w * (0.32f + mid * 0.24f), h * (0.34f + hi * 0.26f), r2, g2, b2, Math.Clamp((mid * 0.20f + hi * 0.14f) * scaleMul, 0f, 0.30f), slot: 6);
                RadialGlow(w * 0.50f, h * 0.48f, w * (0.28f + bass * 0.30f), h * (0.26f + bass * 0.28f), (r1 + r2) * 0.5f, (g1 + g2) * 0.5f, (b1 + b2) * 0.5f, Math.Clamp(bass * bass * 0.22f * scaleMul, 0f, 0.26f), slot: 7);
                UI.Animate();
            }
        }

        // Seamless GPU SDF pattern overlay (pulses with bass in Music Mode!)
        BgEngine.DrawPattern(DC, w, h);

        // 1:1 physical-pixel bead-blasted velvety satin frost / noir film grain
        if (mode != "off" && _grainBrush != null)
        {
            float mul = mode switch { "soft" => 0.55f, "noir" => 1.35f, "orbital" => 0.35f, "kawaii" => 0.38f, _ => 0.95f };
            _grainBrush.Transform = Matrix3x2.CreateScale(1f / MathF.Max(0.5f, Scale));
            _grainBrush.Opacity = (Th.Light ? 0.044f : 0.058f) * mul;
            DC.FillRectangle(new Vortice.RawRectF(0, 0, w, h), _grainBrush);
        }
    }

    /// <summary>
    /// Draws a true hardware stencil-masked adaptive frosted glass surface (real-time GPU blurred/refracted/stylized
    /// live backdrop strictly clipped to the panel's exact rounded geometry + Fresnel sheen + tactile micro-texture + modular rim).
    /// </summary>
    public static void FillFrosted(R r, uint baseColor, float radius = 0, float opacity = 0.80f, bool border = false)
    {
        if (r.W <= 0 || r.H <= 0) return;
        var s = Services.AppSettings.Current;
        var mode = s.FrostedGlass;
        var sh = s.UiGlassShader;
        float cr = ClampRad(r, radius > 0 ? Corner(radius) : 0f);
        bool hasWallOrPat = !string.IsNullOrEmpty(s.BgImagePath) || (s.BgPattern != "none" && !string.IsNullOrEmpty(s.BgPattern));
        bool realGlass = _glassReady && _glassBrush != null && !string.IsNullOrEmpty(sh) && sh != "off";

        if (!realGlass && mode == "off" && !hasWallOrPat)
        {
            Fill(r, baseColor, cr);
            if (border)
            {
                if (s.UiCardStyle == "dashed") StrokeDashed(r, Fade(Th.Brand, 0.38f), cr, 1.25f);
                else if (s.UiCardStyle == "brutal") Stroke(r, Fade(Th.Ink, 0.72f), cr, 1.5f);
                else Stroke(r, Th.Line, cr);
            }
            return;
        }

        // 0. True Hardware Stencil-Masked Adaptive Blurred/Refracted/Stylized Backdrop strictly inside panel shape!
        if (realGlass && _glassBrush != null && _glassW > 0 && _glassH > 0)
        {
            float baseAlpha = (baseColor >> 24) / 255f;
            _glassBrush.Transform = Matrix3x2.CreateScale(W / _glassW, H / _glassH);
            _glassBrush.Opacity = Math.Clamp(baseAlpha, 0f, 1f);
            if (cr > 0)
                DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(r), RadiusX = cr, RadiusY = cr }, _glassBrush);
            else
                DC.FillRectangle(Raw(r), _glassBrush);
        }

        bool strong = mode != "soft" && mode != "off";
        // Let live blurred/refracted colors breathe naturally through glass panels while keeping small labels effortlessly legible even over bright thumbnails!
        float effOp = realGlass
            ? (sh == "liquid" ? Math.Clamp(opacity * 0.72f, 0.48f, 0.78f)
             : sh == "toon"   ? Math.Clamp(opacity * 0.76f, 0.50f, 0.80f)
             : Math.Clamp(opacity * 0.78f, 0.52f, 0.82f))
            : (strong ? MathF.Max(0.52f, opacity * 0.92f) : MathF.Min(0.92f, opacity + 0.05f));
        Fill(r, Fade(baseColor, effOp), cr);

        // 1. Pre-baked vertical Fresnel studio catch-light sheen
        if ((mode != "off" || realGlass) && _sheenBrush != null && r.H > 2)
        {
            _sheenBrush.Transform = Matrix3x2.CreateScale(1f, r.H / 128f) * Matrix3x2.CreateTranslation(0f, r.Y);
            _sheenBrush.Opacity = sh == "liquid" ? 1.0f : (strong ? (Th.Light ? 0.82f : 0.95f) : 0.55f);
            if (cr > 0)
                DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(r), RadiusX = cr, RadiusY = cr }, _sheenBrush);
            else
                DC.FillRectangle(Raw(r), _sheenBrush);
        }

        // 2. Surface micro-texture matched to active GPU shader (satin frost / manga halftone / pencil cross-hatch)
        if (sh == "toon" && _halftoneBrush != null)
        {
            _halftoneBrush.Transform = Matrix3x2.CreateScale(0.65f / MathF.Max(0.5f, Scale));
            _halftoneBrush.Opacity = Th.Light ? 0.065f : 0.042f;
            if (cr > 0) DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(r), RadiusX = cr, RadiusY = cr }, _halftoneBrush);
            else DC.FillRectangle(Raw(r), _halftoneBrush);
        }
        else if (sh == "sketch" && _sketchBrush != null)
        {
            _sketchBrush.Transform = Matrix3x2.CreateScale(0.8f / MathF.Max(0.5f, Scale));
            _sketchBrush.Opacity = Th.Light ? 0.11f : 0.075f;
            if (cr > 0) DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(r), RadiusX = cr, RadiusY = cr }, _sketchBrush);
            else DC.FillRectangle(Raw(r), _sketchBrush);
        }
        else if ((mode != "off" || realGlass) && _grainBrush != null)
        {
            _grainBrush.Transform = Matrix3x2.CreateScale(1f / MathF.Max(0.5f, Scale));
            _grainBrush.Opacity = (Th.Light ? 0.036f : 0.046f) * (strong || realGlass ? 1f : 0.55f);
            if (cr > 0)
                DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(r), RadiusX = cr, RadiusY = cr }, _grainBrush);
            else
                DC.FillRectangle(Raw(r), _grainBrush);
        }

        // 2b. Subtle liquid-glass top specular bevel highlight when rounded & real glass is active
        if (realGlass && cr >= 6f && r.W > cr * 2 + 12)
        {
            float specA = sh == "liquid" ? (Th.Light ? 0.42f : 0.24f) : (Th.Light ? 0.24f : 0.13f);
            Fill(new R(r.X + cr * 0.78f, r.Y + 0.6f, r.W - cr * 1.56f, 1.0f), Th.A(specA));
        }

        // 3. Modular border style (dashed kawaii / brutalist ink / neon rim / toon cel ink / sketch double-line / glass rim)
        if (border)
        {
            if (s.UiCardStyle == "dashed")
                StrokeDashed(r, Fade(Th.Brand, 0.40f), cr, 1.25f);
            else if (s.UiCardStyle == "brutal")
                Stroke(r, Fade(Th.Ink, 0.72f), cr, 1.5f);
            else if (sh == "toon")
                Stroke(r, Fade(Th.Light ? 0xFF000000 : Th.Brand, 0.62f), cr, 1.6f);
            else if (sh == "sketch")
            {
                Stroke(r, Fade(Th.Ink, 0.34f), cr, 1.1f);
                Stroke(r.Inset(1.5f), Fade(Th.Brand, 0.20f), MathF.Max(0, cr - 1.5f), 0.9f);
            }
            else if (s.UiCardStyle == "neon" && s.UiNeonGlow)
                Stroke(r, Fade(Th.Brand2 != 0 ? Th.Brand2 : Th.Brand, 0.38f), cr, 1.1f);
            else
            {
                Stroke(r, Th.A(sh == "liquid" ? 0.24f : (Th.Light ? 0.16f : 0.15f)), cr, 1f);
                float inset = MathF.Max(cr * 0.72f, 6f);
                if (r.W > inset * 2 + 8)
                    Fill(new R(r.X + inset, r.Y + 0.5f, r.W - inset * 2, 1f), Th.A(Th.Light ? 0.24f : 0.18f));
            }
        }
    }

    public static void Image(ID2D1Bitmap bmp, R dest, float opacity = 1)
        => DC.DrawBitmap(bmp, (Vortice.RawRectF?)Raw(dest), opacity, Vortice.Direct2D1.InterpolationMode.Linear, null, null);

    /// <summary>Hardware anti-aliased rounded image using a single reusable GPU bitmap brush — zero corner halo artifacts on any backdrop, pattern, or dashed border!</summary>
    public static void ImageRounded(ID2D1Bitmap bmp, R dest, float radius, float opacity = 1f)
    {
        float cr = ClampRad(dest, radius);
        if (cr <= 0.5f) { Image(bmp, dest, opacity); return; }
        var sz = bmp.Size;
        if (sz.Width <= 0 || sz.Height <= 0 || dest.W <= 0 || dest.H <= 0) return;
        var b = _imgBrush ??= DC.CreateBitmapBrush(bmp, new BitmapBrushProperties(ExtendMode.Clamp, ExtendMode.Clamp, Vortice.Direct2D1.BitmapInterpolationMode.Linear));
        b.Bitmap = bmp;
        b.Transform = Matrix3x2.CreateScale(dest.W / sz.Width, dest.H / sz.Height) * Matrix3x2.CreateTranslation(dest.X, dest.Y);
        b.Opacity = opacity;
        DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(dest), RadiusX = cr, RadiusY = cr }, b);
        b.Bitmap = null;
    }

    /// <summary>Hardware anti-aliased rounded cover-crop image (fills dest completely preserving exact aspect ratio, optional zoom &amp; drift).</summary>
    public static void ImageRoundedCover(ID2D1Bitmap bmp, R dest, float radius, float opacity = 1f, float zoom = 1f, float panX = 0f, float panY = 0f)
    {
        var sz = bmp.Size;
        if (sz.Width <= 0 || sz.Height <= 0 || dest.W <= 0 || dest.H <= 0 || opacity <= 0.002f) return;
        float sc = MathF.Max(dest.W / sz.Width, dest.H / sz.Height) * MathF.Max(1f, zoom);
        float dw = sz.Width * sc, dh = sz.Height * sc;
        float ox = dest.X + (dest.W - dw) * 0.5f + panX;
        float oy = dest.Y + (dest.H - dh) * 0.5f + panY;
        var b = _imgBrush ??= DC.CreateBitmapBrush(bmp, new BitmapBrushProperties(ExtendMode.Clamp, ExtendMode.Clamp, Vortice.Direct2D1.BitmapInterpolationMode.Linear));
        b.Bitmap = bmp;
        b.Transform = Matrix3x2.CreateScale(sc, sc) * Matrix3x2.CreateTranslation(ox, oy);
        b.Opacity = opacity;
        float cr = ClampRad(dest, radius);
        if (cr > 0.5f) DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(dest), RadiusX = cr, RadiusY = cr }, b);
        else DC.FillRectangle(Raw(dest), b);
        b.Bitmap = null;
    }

    /// <summary>Draws a circular cover-cropped rotating album label (used for the spinning DJ vinyl turntable platter).</summary>
    public static void ImageCircleRotated(ID2D1Bitmap bmp, float cx, float cy, float radius, float angleRad, float opacity = 1f)
    {
        var sz = bmp.Size;
        if (sz.Width <= 0 || sz.Height <= 0 || radius <= 1f || opacity <= 0.002f) return;
        float d = radius * 2f;
        float sc = MathF.Max(d / sz.Width, d / sz.Height);
        var b = _imgBrush ??= DC.CreateBitmapBrush(bmp, new BitmapBrushProperties(ExtendMode.Clamp, ExtendMode.Clamp, Vortice.Direct2D1.BitmapInterpolationMode.Linear));
        b.Bitmap = bmp;
        b.Transform = Matrix3x2.CreateTranslation(-sz.Width * 0.5f, -sz.Height * 0.5f)
            * Matrix3x2.CreateScale(sc, sc)
            * Matrix3x2.CreateRotation(angleRad)
            * Matrix3x2.CreateTranslation(cx, cy);
        b.Opacity = opacity;
        DC.FillEllipse(new Ellipse(new Vector2(cx, cy), radius, radius), b);
        b.Bitmap = null;
    }

    /// <summary>Zero-allocation cached vertical scrim gradient (eliminates 288+ COM GPU allocations/sec in Music Mode).</summary>
    public static void FillVerticalFade(R r, uint rgbColor, float topAlpha, float midAlpha, float botAlpha, float midStop = 0.52f)
    {
        if (r.W <= 0 || r.H <= 0) return;
        float ms = Math.Clamp(midStop, 0.1f, 0.9f);
        uint rgb = rgbColor & 0x00FFFFFFu;
        var s = _linears[(uint)(_linearCursor++) % (uint)_linears.Length];
        if (s.Brush == null || s.Rgb != rgb || MathF.Abs(s.A0 - topAlpha) > 0.003f || MathF.Abs(s.A1 - midAlpha) > 0.003f || MathF.Abs(s.A2 - botAlpha) > 0.003f || MathF.Abs(s.Mid - ms) > 0.003f)
        {
            s.Dispose();
            float rf = ((rgb >> 16) & 255) / 255f;
            float gf = ((rgb >> 8) & 255) / 255f;
            float bf = (rgb & 255) / 255f;
            _linearStopsBuf[0] = new GradientStop(0f, new Color4(rf, gf, bf, topAlpha));
            _linearStopsBuf[1] = new GradientStop(ms, new Color4(rf, gf, bf, midAlpha));
            _linearStopsBuf[2] = new GradientStop(1f, new Color4(rf, gf, bf, botAlpha));
            s.Stops = DC.CreateGradientStopCollection(_linearStopsBuf, Gamma.StandardRgb, ExtendMode.Clamp);
            s.Brush = DC.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(r.X, r.Y), new Vector2(r.X, r.Bottom)), s.Stops);
            s.Rgb = rgb; s.A0 = topAlpha; s.A1 = midAlpha; s.A2 = botAlpha; s.Mid = ms;
        }
        else
        {
            s.Brush.StartPoint = new Vector2(r.X, r.Y);
            s.Brush.EndPoint = new Vector2(r.X, r.Bottom);
        }
        DC.FillRectangle(Raw(r), s.Brush);
    }

    public static void ImageRegion(ID2D1Bitmap bmp, R dest, R src, float opacity = 1)
        => DC.DrawBitmap(bmp, (Vortice.RawRectF?)Raw(dest), opacity, Vortice.Direct2D1.InterpolationMode.Linear, (Vortice.RawRectF?)Raw(src), null);

    /// <summary>GPU bitmap straight from memory: raw premultiplied BGRA, or BC1 blocks (an eighth of the video memory).</summary>
    public static ID2D1Bitmap1 CreateBitmap(byte* data, int w, int h, bool bc1)
        => DC.CreateBitmap(new SizeI(w, h), (nint)data, (uint)(bc1 ? w / 4 * 8 : w * 4),
            new BitmapProperties1(new PixelFormat(bc1 ? Format.BC1_UNorm : Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));

    /// <summary>Linear blend of two colours, t = 0 gives a.</summary>
    public static uint Mix(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        uint C(int s) => (uint)(((a >> s) & 255) + (((b >> s) & 255) - (float)((a >> s) & 255)) * t + 0.5f) & 255;
        return C(24) << 24 | C(16) << 16 | C(8) << 8 | C(0);
    }

    /// <summary>
    /// Rounds the corners of something already drawn as a plain rectangle (a block-compressed thumbnail has no alpha):
    /// a band in the colour of what is behind it, hugging the outside of the rounded outline.
    /// </summary>
    public static void RoundOff(R r, float radius, uint behind)
    {
        if (radius <= 0) return;
        float s = radius * 0.5f + 0.75f;
        var rr = new RoundedRectangle { Rect = Raw(r.Inset(-s / 2)), RadiusX = radius + s / 2, RadiusY = radius + s / 2 };
        DC.DrawRoundedRectangle(rr, Scratch(behind), s);
    }

    // ---------------- clipping (drawing and hit-testing share it) ----------------

    private static readonly R[] _clips = new R[16];
    private static int _clipDepth;

    public static R Clip => _clipDepth > 0 ? _clips[_clipDepth - 1] : new R(0, 0, W, H);

    public static void PushClip(R r)
    {
        var c = Clip.Intersect(r);
        _clips[_clipDepth++] = c;
        DC.PushAxisAlignedClip(Raw(c), AntialiasMode.Aliased);
    }

    public static void PopClip()
    {
        _clipDepth--;
        DC.PopAxisAlignedClip();
    }

    // ---------------- text ----------------

    private readonly record struct FKey(string FontName, int Size10, int Weight, bool Icon, bool Lucide = false);
    private readonly record struct TKey(string Text, int Size10, int Weight, int MaxW, int Lines, bool Icon, bool Lucide = false);

    private static readonly Dictionary<FKey, (IDWriteTextFormat F, IDWriteInlineObject Ellipsis, float LineH)> _formats = new();
    private static Dictionary<TKey, TextRun> _cur = new(), _prev = new();

    private static (IDWriteTextFormat F, IDWriteInlineObject Ellipsis, float LineH) FormatFor(FKey k)
    {
        lock (_formats) return FormatLocked(k);
    }

    private static (IDWriteTextFormat F, IDWriteInlineObject Ellipsis, float LineH) FormatLocked(FKey k)
    {
        if (_formats.TryGetValue(k, out var f)) return f;
        var fmt = _dw.CreateTextFormat(k.Lucide ? "lucide" : k.Icon ? IconFont : k.FontName, k.Lucide ? _lucideFonts : null, (FontWeight)k.Weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, k.Size10 / 10f, Services.L.En ? "en-us" : "ru-ru");
        using var probe = _dw.CreateTextLayout("Ag", fmt, 1000, 1000);
        return _formats[k] = (fmt, _dw.CreateEllipsisTrimmingSign(fmt), probe.Metrics.Height);
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<(TKey Key, TextRun Run)> _prewarmed = new();
    private static double _shapeMs;
    private const double ShapeBudgetMs = 2.0;

    // width quantised to 8 DIPs: a window resize re-shapes text only every few pixels
    private static TKey Key(string text, float size, int weight, float maxW, int lines, bool icon)
        => new(text, (int)(size * 10), weight, maxW <= 0 ? 0 : Math.Max(8, (int)maxW / 8 * 8), lines, icon);

    private static TextRun Shape(TKey key)
    {
        var (fmt, ellipsis, lineH) = FormatFor(new FKey(ActiveFontName(), key.Size10, key.Weight, key.Icon, key.Lucide));
        var width = key.MaxW == 0 ? 100000f : key.MaxW;
        var layout = _dw.CreateTextLayout(key.Text, fmt, width, key.Lines > 0 ? lineH * key.Lines + 1 : 100000f);
        layout.WordWrapping = key.Lines == 1 ? WordWrapping.NoWrap : WordWrapping.Wrap;
        if (key.Lines > 0 && key.MaxW > 0)
            layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
        var m = layout.Metrics;
        return new TextRun
        {
            Layout = layout, W = MathF.Min(m.Width, width), H = key.Lines > 0 ? MathF.Min(m.Height, lineH * key.Lines) : m.Height,
            Lines = (int)m.LineCount, Color = !key.Icon && HasEmoji(key.Text),
        };
    }

    private static bool HasEmoji(string s)
    {
        foreach (var ch in s)
            if (char.IsSurrogate(ch) || ch is >= (char)0x2190 and <= (char)0x2BFF || ch is (char)0x00A9 or (char)0x00AE or (char)0x203C or (char)0x2049 or (char)0x2122 or (char)0x2139 or (char)0x3030 or (char)0x303D or (char)0xFE0F)
                return true;
        return false;
    }

    /// <summary>Shapes text on a worker before it scrolls into view (DirectWrite shaping costs up to a millisecond per string).</summary>
    public static void Prewarm(List<(string Text, float Size, int Weight, float MaxW, int Lines)> items)
    {
        if (items.Count == 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Text)) continue;
                try { var k = Key(it.Text, it.Size, it.Weight, it.MaxW, it.Lines, false); _prewarmed.Enqueue((k, Shape(k))); }
                catch { }
            }
            App.WakeUi();
        });
    }

    public static void ResetShapeBudget() => _shapeMs = 0;

    /// <param name="lines">1 = one line with ellipsis; n = wrap to n lines with ellipsis; 0 = wrap without limit.</param>
    public static TextRun Text(string text, float size, int weight = 400, float maxW = 0, int lines = 1, bool icon = false)
    {
        var key = Key(text, size, weight, maxW, lines, icon);
        if (_cur.TryGetValue(key, out var run)) return run;
        if (_prev.Remove(key, out run)) { _cur[key] = run; return run; }

        if (_shapeMs > ShapeBudgetMs && !icon && text.Length > 3)
        {
            // over this frame's budget: reserve the space now, shape on the next frame
            var lineH = FormatFor(new FKey(ActiveFontName(), key.Size10, weight, false)).LineH;
            var estW = text.Length * size * 0.52f;
            var n = lines == 1 || key.MaxW == 0 ? 1 : Math.Max(1, (int)MathF.Ceiling(estW / key.MaxW));
            if (lines > 1) n = Math.Min(n, lines);
            UI.Animate();
            return new TextRun { W = key.MaxW == 0 ? estW : MathF.Min(estW, key.MaxW), H = lineH * n, Lines = n };
        }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        TextCreated++;
        run = Shape(key);
        _shapeMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return _cur[key] = run;
    }
    public static void Draw(TextRun run, float x, float y, uint color)
    {
        if (run.Layout == null) return;
        var t0 = App.Bench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        DC.DrawTextLayout(new Vector2(x, y), run.Layout, Scratch(color), run.Color ? DrawTextOptions.EnableColorFont : DrawTextOptions.None);
        if (App.Bench) { TextDrawMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; TextDraws++; }
    }

    public static TextRun Draw(string text, float x, float y, float size, uint color, int weight = 400, float maxW = 0, int lines = 1)
    {
        var run = Text(text, size, weight, maxW, lines);
        if (text.Length > 0) Draw(run, x, y, color);
        return run;
    }

    private readonly record struct DanceGlyphKey(char Ch, int Size10, int Weight, string Font);
    private readonly record struct DanceSprite(float SrcX, float SrcY, float SrcW, float SrcH, float DipW, float DipH, float Pad);
    private readonly record struct GlyphSlot(char Ch, float Ox, float Oy, float W, float H, int Idx);
    private sealed class GlyphRunEntry { public required GlyphSlot[] Slots; public int TextGen; }
    private static readonly Dictionary<TKey, GlyphRunEntry> _glyphCache = new();
    private static readonly Dictionary<DanceGlyphKey, DanceSprite> _danceSprites = new();
    private static ID2D1Bitmap1? _danceAtlas;
    private static int _dancePenX, _dancePenY, _danceRowH;
    private const int DanceAtlasW = 1024, DanceAtlasH = 512;
    private const float DanceAtlasScale = 2f; // 2x supersampled crisp GPU sprites -> ZERO D2D glyph-atlas thrashing!
    private static int _textCacheGen;

    private static void DisposeDanceAtlas()
    {
        _danceAtlas?.Dispose();
        _danceAtlas = null;
        _danceSprites.Clear();
        _dancePenX = _dancePenY = _danceRowH = 0;
    }

    private static bool TryGetDanceSprite(char ch, float size, int weight, out DanceSprite sprite)
    {
        var font = ActiveFontName();
        var gk = new DanceGlyphKey(ch, (int)(size * 10), weight, font);
        if (_danceAtlas != null && _danceSprites.TryGetValue(gk, out sprite))
            return true;

        sprite = default;
        if (_activeTarget == null || _mbDepth > 0) return false;

        try
        {
            if (_danceAtlas == null)
            {
                _danceAtlas = DC.CreateBitmap(new SizeI(DanceAtlasW, DanceAtlasH),
                    new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.Target));
                _dancePenX = 2; _dancePenY = 2; _danceRowH = 0;
                _danceSprites.Clear();
                // Clear newly created atlas once
                var savedTf0 = DC.Transform;
                int savedClips0 = _clipDepth;
                for (int i = savedClips0 - 1; i >= 0; i--) DC.PopAxisAlignedClip();
                DC.Target = _danceAtlas;
                DC.SetDpi(96, 96);
                DC.Transform = Matrix3x2.Identity;
                DC.Clear(new Color4(0, 0, 0, 0));
                DC.Target = _activeTarget;
                DC.SetDpi(96 * Scale, 96 * Scale);
                DC.Transform = savedTf0;
                for (int i = 0; i < savedClips0; i++) DC.PushAxisAlignedClip(Raw(_clips[i]), AntialiasMode.Aliased);
            }

            var chRun = Text(ch.ToString(), size, weight, 0, 1);
            if (chRun.Layout == null) return false;
            const float pad = 3f;
            float dipW = MathF.Ceiling(MathF.Max(4f, chRun.W)) + pad * 2f;
            float dipH = MathF.Ceiling(MathF.Max(size, chRun.H)) + pad * 2f;
            int pxW = (int)MathF.Ceiling(dipW * DanceAtlasScale);
            int pxH = (int)MathF.Ceiling(dipH * DanceAtlasScale);
            if (pxW <= 0 || pxH <= 0 || pxW >= DanceAtlasW - 4 || pxH >= DanceAtlasH - 4) return false;

            // Shelf-pack into 1024x512 atlas; if full, reset cleanly (rare — holds ~400+ distinct glyphs!)
            if (_dancePenX + pxW + 2 > DanceAtlasW)
            {
                _dancePenX = 2;
                _dancePenY += _danceRowH + 2;
                _danceRowH = 0;
            }
            if (_dancePenY + pxH + 2 > DanceAtlasH)
            {
                _danceSprites.Clear();
                _dancePenX = 2; _dancePenY = 2; _danceRowH = 0;
                var savedTfR = DC.Transform;
                int savedClipsR = _clipDepth;
                for (int i = savedClipsR - 1; i >= 0; i--) DC.PopAxisAlignedClip();
                DC.Target = _danceAtlas;
                DC.SetDpi(96, 96);
                DC.Transform = Matrix3x2.Identity;
                DC.Clear(new Color4(0, 0, 0, 0));
                DC.Target = _activeTarget;
                DC.SetDpi(96 * Scale, 96 * Scale);
                DC.Transform = savedTfR;
                for (int i = 0; i < savedClipsR; i++) DC.PushAxisAlignedClip(Raw(_clips[i]), AntialiasMode.Aliased);
            }

            int sx = _dancePenX, sy = _dancePenY;
            _dancePenX += pxW + 2;
            if (pxH > _danceRowH) _danceRowH = pxH;

            // Rasterize glyph ONCE in pure white into _danceAtlas at 2x scale!
            var savedTf = DC.Transform;
            var savedTextAA = DC.TextAntialiasMode;
            int savedClips = _clipDepth;
            for (int i = savedClips - 1; i >= 0; i--) DC.PopAxisAlignedClip();
            try
            {
                DC.Target = _danceAtlas;
                DC.SetDpi(96, 96);
                DC.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
                DC.Transform = Matrix3x2.CreateScale(DanceAtlasScale) * Matrix3x2.CreateTranslation(sx, sy);
                DC.DrawTextLayout(new Vector2(pad, pad), chRun.Layout, Scratch(0xFFFFFFFF), DrawTextOptions.None);
            }
            finally
            {
                DC.TextAntialiasMode = savedTextAA;
                DC.Target = _activeTarget;
                DC.SetDpi(96 * Scale, 96 * Scale);
                DC.Transform = savedTf;
                for (int i = 0; i < savedClips; i++) DC.PushAxisAlignedClip(Raw(_clips[i]), AntialiasMode.Aliased);
            }

            sprite = new DanceSprite(sx, sy, pxW, pxH, dipW, dipH, pad);
            _danceSprites[gk] = sprite;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Per-character 3-band Fourier liquid-wave dancing text rendered from a 2x supersampled GPU opacity-mask sprite atlas:
    /// ZERO per-frame DirectWrite rasterization, ZERO D2D glyph-atlas stalls, silky 144/240 FPS rotation &amp; squash-stretch!
    /// </summary>
    public static TextRun DrawDancingText(string text, float x, float y, float size, uint color, int weight = 600,
        float maxW = 0, int lines = 2, bool shadow = false, float amp = 1.0f, uint accentMixColor = 0)
    {
        var run = Text(text, size, weight, maxW, lines);
        if (string.IsNullOrEmpty(text) || run.Layout == null) return run;
        if (!MusicBounce.Active || text.Length > 140)
        {
            if (shadow) Draw(run, x + 1f, y + 1.5f, Fade(0xFF000000, 0.58f));
            Draw(run, x, y, color);
            return run;
        }

        var key = Key(text, size, weight, maxW, lines, false);
        if (!_glyphCache.TryGetValue(key, out var entry) || entry.TextGen != _textCacheGen)
        {
            if (_glyphCache.Count > 64) _glyphCache.Clear();
            var list = new List<GlyphSlot>(text.Length);
            float lineH = LineHeight(size, weight);
            float maxY = lines > 0 ? lineH * lines + 1f : 100000f;
            int visIdx = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsSurrogate(ch))
                {
                    if (char.IsHighSurrogate(ch) && i + 1 < text.Length) i++;
                    continue;
                }
                if (char.IsWhiteSpace(ch)) continue;
                run.Layout.HitTestTextPosition((uint)i, false, out float gx, out float gy, out var m);
                if (gy >= maxY - 1f || (maxW > 0 && gx + m.Width > maxW + 4f)) continue;
                list.Add(new GlyphSlot(ch, gx, gy, MathF.Max(4f, m.Width), MathF.Max(size, m.Height), visIdx++));
            }
            entry = new GlyphRunEntry { Slots = list.ToArray(), TextGen = _textCacheGen };
            _glyphCache[key] = entry;
        }

        var slots = entry.Slots;
        if (slots.Length == 0)
        {
            if (shadow) Draw(run, x + 1f, y + 1.5f, Fade(0xFF000000, 0.58f));
            Draw(run, x, y, color);
            return run;
        }

        // Pre-warm any unseen glyphs into the GPU sprite atlas BEFORE switching AntialiasMode
        bool atlasOk = _activeTarget != null && _mbDepth == 0;
        if (atlasOk)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (!TryGetDanceSprite(slots[i].Ch, size, weight, out _)) { atlasOk = false; break; }
            }
        }

        if (!atlasOk || _danceAtlas == null)
        {
            // Safe fallback (translation-only, zero glyph-atlas thrashing)
            if (shadow) Draw(run, x + 1f, y + 1.5f, Fade(0xFF000000, 0.58f));
            Draw(run, x, y, color);
            return run;
        }

        var prevAA = DC.AntialiasMode;
        DC.AntialiasMode = AntialiasMode.Aliased; // required by Direct2D FillOpacityMask; atlas glyphs are 2x supersampled!
        var font = ActiveFontName();
        int size10 = (int)(size * 10);
        try
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var g = slots[i];
                if (!_danceSprites.TryGetValue(new DanceGlyphKey(g.Ch, size10, weight, font), out var sp)) continue;
                float gx = x + g.Ox, gy = y + g.Oy;
                var tf = MusicBounce.PushGlyph(new R(gx, gy, g.W, g.H), g.Idx, amp, out float energy);
                uint col = accentMixColor != 0 && energy > 0.35f
                    ? Mix(color, accentMixColor, Math.Clamp((energy - 0.35f) * 0.55f, 0f, 0.48f))
                    : color;
                var srcR = new Vortice.RawRectF(sp.SrcX, sp.SrcY, sp.SrcX + sp.SrcW, sp.SrcY + sp.SrcH);
                if (shadow)
                {
                    var shR = new Vortice.RawRectF(gx - sp.Pad + 1f, gy - sp.Pad + 1.5f, gx - sp.Pad + 1f + sp.DipW, gy - sp.Pad + 1.5f + sp.DipH);
                    DC.FillOpacityMask(_danceAtlas, Scratch(Fade(0xFF000000, 0.58f)), OpacityMaskContent.Graphics, shR, srcR);
                }
                var dstR = new Vortice.RawRectF(gx - sp.Pad, gy - sp.Pad, gx - sp.Pad + sp.DipW, gy - sp.Pad + sp.DipH);
                DC.FillOpacityMask(_danceAtlas, Scratch(col), OpacityMaskContent.Graphics, dstR, srcR);
                MusicBounce.Pop(tf);
            }
        }
        finally
        {
            DC.AntialiasMode = prevAA;
        }
        return run;
    }

    /// <summary>Icon glyph centred in the box.</summary>
    public static void Icon(string glyph, R box, float size, uint color)
    {
        if (_lucideFonts != null && LucideGlyphs.TryGetValue(glyph, out var lucide))
        {
            var key = Key(lucide, size, 400, 0, 1, true) with { Lucide = true };
            if (!_cur.TryGetValue(key, out var runLucide))
            {
                if (_prev.Remove(key, out runLucide)) _cur[key] = runLucide;
                else _cur[key] = runLucide = Shape(key);
            }
            Draw(runLucide, Snap(box.X + (box.W - runLucide.W) / 2), Snap(box.Y + (box.H - runLucide.H) / 2), color);
            return;
        }
        var run = Text(glyph, size, 400, 0, 1, icon: true);
        Draw(run, Snap(box.X + (box.W - run.W) / 2), Snap(box.Y + (box.H - run.H) / 2), color);
    }

    public static float LineHeight(float size, int weight = 400) => FormatFor(new FKey(ActiveFontName(), (int)(size * 10), weight, false)).LineH;

    private static void TrimTextCache()
    {
        if (_cur.Count < 700) return;
        foreach (var r in _prev.Values) r.Layout?.Dispose();
        _prev = _cur;
        _cur = new Dictionary<TKey, TextRun>(256);
        _textCacheGen++;
    }

    /// <summary>Nothing is moving: layouts not drawn since the last trim are let go (they are rebuilt on demand).</summary>
    public static void DropIdleText()
    {
        if (_prev.Count > 0)
        {
            foreach (var r in _prev.Values) r.Layout?.Dispose();
            _prev = new Dictionary<TKey, TextRun>();
            _textCacheGen++;
        }
    }

    /// <summary>Reclaims idle offscreen GPU targets (~16-33 MB VRAM for motion blur &amp; glass when minimized/idle).</summary>
    public static void TrimMemory(bool minimized)
    {
        long now = Environment.TickCount64;
        if (_mbBmp != null && (minimized || now - _mbLastUsedAt > 3500))
        {
            _mbBmp.Dispose();
            _mbBmp = null;
            _mbBmpW = _mbBmpH = 0;
        }
        if (minimized)
        {
            DisposeGlassTargets();
            DisposeDanceAtlas();
            DropIdleText();
            _glyphCache.Clear();
        }
    }

    public static void ClearTextCache()
    {
        foreach (var r in _prev.Values) r.Layout?.Dispose();
        foreach (var r in _cur.Values) r.Layout?.Dispose();
        _prev.Clear(); _cur.Clear();
        _glyphCache.Clear();
        DisposeDanceAtlas();
        _textCacheGen++;
        while (_prewarmed.TryDequeue(out var p)) p.Run.Layout?.Dispose();
    }
}

/// <summary>
/// Ultra-cheap per-element GPU bass/spectrum bounce for Audio-Only Music Mode (~0.0005ms per element, zero alloc).
/// Pure v9.2.2 FFT transient physics (silky beat-locked hop, scale &amp; tilt per band) + optional Drive boost
/// and per-glyph scatter dance for individual title/like characters.
/// </summary>
public static class MusicBounce
{
    public static bool Active { get; private set; }
    public static float Kick { get; private set; }
    public static float Bass { get; private set; }
    public static float Mid { get; private set; }
    public static float High { get; private set; }
    public static float LoudnessGain { get; private set; }

    private static float _bassAvg, _phase, _lowPhase, _midPhase, _highPhase;
    private static readonly float[] _bandAvg = new float[32];
    private static readonly float[] _bandPulse = new float[32];

    public static void Update(bool enabledPlaying, float dt)
    {
        if (!enabledPlaying)
        {
            if (Active)
            {
                Active = false;
                Kick = Bass = Mid = High = LoudnessGain = _bassAvg = 0f;
                Array.Clear(_bandAvg);
                Array.Clear(_bandPulse);
            }
            return;
        }

        AudioAnalyzer.RequestFrame(force: true);
        dt = Math.Clamp(dt, 0.003f, 0.04f);
        float bass = AudioAnalyzer.BassEnergy;
        float mid = AudioAnalyzer.MidEnergy;
        float high = AudioAnalyzer.HighEnergy;
        float loud = AudioAnalyzer.LoudnessGain;
        float dyn = AudioAnalyzer.TrackDynamics;
        Bass = bass; Mid = mid; High = high; LoudnessGain = loud;

        // Track-adaptive loudness & dynamic drop physics: quiet intros breathe softly, big drops slam hard!
        float avgAlpha = 1f - MathF.Exp(-dt * 6.5f);
        _bassAvg += (bass - _bassAvg) * avgAlpha;
        float kickRaw = (MathF.Max(0f, bass - _bassAvg * 0.82f) * 2.95f + MathF.Max(0f, bass - 0.22f) * 0.90f) * loud;
        float kickDecay = 12.5f + (1f - Math.Clamp(dyn - 0.6f, 0f, 1f)) * 4.5f;
        Kick = MathF.Max(Kick * MathF.Exp(-dt * kickDecay), Math.Clamp(kickRaw, 0f, 1.65f));

        var bands = AudioAnalyzer.Bands;
        float decay = MathF.Exp(-dt * 13.5f);
        float bandAvgA = 1f - MathF.Exp(-dt * 5.5f);
        float maxE = Kick;
        for (int i = 0; i < 32 && i < bands.Length; i++)
        {
            float v = bands[i];
            _bandAvg[i] += (v - _bandAvg[i]) * bandAvgA;
            float trans = (MathF.Max(0f, v - _bandAvg[i] * 0.78f) * 2.40f + v * 0.64f) * loud;
            float next = MathF.Max(_bandPulse[i] * decay, Math.Clamp(trans, 0f, 1.65f));
            _bandPulse[i] = next;
            if (next > maxE) maxE = next;
        }

        // 3 independent Fourier water-wave phase velocities: slow deep bass swell, flowing mid wave, rapid treble ripples!
        _phase += dt * (10.5f + Kick * 17.5f);
        _lowPhase += dt * (3.2f + Bass * 4.8f + Kick * 5.5f);
        _midPhase += dt * (6.4f + Mid * 8.2f);
        _highPhase += dt * (12.5f + High * 14.0f);
        Active = maxE > 0.012f;
        if (Active) UI.Animate();
    }

    public static float BandEnergy(int band) => Active ? _bandPulse[band & 31] : 0f;

    /// <summary>
    /// Pushes an independent per-element GPU hop/scale/tilt transform centered on rect. Zero heap alloc.
    /// Pass textSafe: true for text-bearing widgets (pure translation -> ZERO Direct2D glyph-atlas re-rasterization!).
    /// </summary>
    public static Matrix3x2 Push(R rect, int band, float amp = 1.0f, float rotAmp = 1.0f, bool textSafe = true)
    {
        var prev = Gfx.DC.Transform;
        if (!Active) return prev;
        int b = band & 31;
        float kickShare = b < 8 ? 0.52f : b < 18 ? 0.25f : 0.12f;
        float drive = Services.AppSettings.Current.MusicImpactEffects ? 1.32f : 1.0f;
        float e = Math.Clamp(_bandPulse[b] * 0.78f + Kick * kickShare, 0f, 1.70f) * amp * drive;
        if (e <= 0.008f) return prev;

        // Distinct phase per band so neighbouring items ripple & hop independently like v9.2.2!
        float wave = MathF.Cos(_phase * 1.15f + b * 1.29f);
        float dy = Math.Clamp((-0.55f + 0.45f * wave) * e * 4.8f, -9.0f, 5.8f);
        float dx = Math.Clamp(MathF.Sin(_phase * 0.94f + b * 1.73f) * e * 2.0f, -5.5f, 5.5f);

        if (textSafe)
        {
            // Pure translation preserves Direct2D's glyph atlas 100% (0 CPU re-rasterization at 144/240 FPS!)
            Gfx.DC.Transform = Matrix3x2.CreateTranslation(dx, dy) * prev;
            return prev;
        }

        float sc = 1f + Math.Clamp(e * 0.042f, 0f, 0.092f);
        float ang = Math.Clamp(MathF.Sin(_phase * 1.07f + b * 2.31f) * e * 0.025f * rotAmp, -0.056f, 0.056f);

        var center = new Vector2(rect.X + rect.W * 0.5f, rect.Y + rect.H * 0.5f);
        var local = Matrix3x2.CreateScale(sc, sc, center)
            * Matrix3x2.CreateRotation(ang, center)
            * Matrix3x2.CreateTranslation(dx, dy);
        Gfx.DC.Transform = local * prev;
        return prev;
    }

    /// <summary>
    /// 3-Band Fourier Liquid Water-Wave Typography:
    /// - Big slow ocean swell driven by Sub-Bass / Kick (lows)
    /// - Medium traveling wave driven by Mids (vocals/synths)
    /// - Crisp fine surface ripples driven by Highs (hats/sparkle) + local FFT band
    /// - True water-surface tangent slope rotation so letters flow organically like a wave without crashing into each other!
    /// </summary>
    public static Matrix3x2 PushGlyph(R rect, int charIdx, float amp, out float energy)
    {
        var prev = Gfx.DC.Transform;
        energy = 0f;
        if (!Active) return prev;

        float drive = Services.AppSettings.Current.MusicImpactEffects ? 1.28f : 1.0f;
        float loud = Math.Clamp(LoudnessGain, 0.12f, 1.65f);
        int localBand = (charIdx * 2 + 2) & 31;
        float localE = _bandPulse[localBand];

        // 3 coherent traveling water waves across the word (large swell + medium wave + small surface ripples)
        const float kLow = 0.28f;  // ~22-char wide ocean bass swell
        const float kMid = 0.72f;  // ~9-char medium flowing wave (counter-propagating for rich interference!)
        const float kHi  = 1.68f;  // ~3.7-char sparkling treble surface ripple

        float pLow = _lowPhase - charIdx * kLow;
        float pMid = _midPhase + charIdx * kMid;
        float pHi  = _highPhase - charIdx * kHi;

        float aLow = Math.Clamp((Bass * 0.78f + Kick * 0.68f) * loud, 0f, 1.55f) * 5.8f * amp * drive;
        float aMid = Math.Clamp(Mid * 0.92f * loud, 0f, 1.35f) * 3.2f * amp * drive;
        float aHi  = Math.Clamp((High * 0.85f + localE * 0.28f) * loud, 0f, 1.25f) * 1.75f * amp * drive;

        // Vertical water displacement = superposition of Big + Medium + Small waves
        float yLow = MathF.Sin(pLow) * aLow;
        float yMid = MathF.Sin(pMid) * aMid;
        float yHi  = MathF.Sin(pHi)  * aHi;
        float dy = Math.Clamp(yLow + yMid + yHi - Kick * 1.4f * amp, -11.5f, 9.0f);

        // Gentle orbital water-particle horizontal sway (crests move forward, troughs move back -> keeps kerning clean!)
        float dx = Math.Clamp((MathF.Cos(pLow) * aLow * 0.18f - MathF.Cos(pMid) * aMid * 0.14f), -2.4f, 2.4f);

        // Exact derivative dY/d(charIdx) gives the natural water-surface tangent angle for each floating letter!
        float slope = (-MathF.Cos(pLow) * kLow * aLow)
                    + ( MathF.Cos(pMid) * kMid * aMid)
                    + (-MathF.Cos(pHi)  * kHi  * aHi * 0.45f);
        float ang = Math.Clamp(slope * 0.036f, -0.145f, 0.145f);

        // Subtle organic crest stretch on bass/mid energy
        float crest = Math.Clamp((Bass * 0.45f + Mid * 0.30f + High * 0.25f) * loud * amp * drive, 0f, 1.5f);
        energy = Math.Clamp(crest + Kick * 0.45f, 0f, 1.65f);
        float scY = 1f + Math.Clamp((Kick * 0.065f + MathF.Max(0f, -yLow) * 0.012f) * amp, 0f, 0.15f);
        float scX = 1f + Math.Clamp(Kick * 0.035f * amp, 0f, 0.08f);

        var center = new Vector2(rect.X + rect.W * 0.5f, rect.Y + rect.H * 0.55f);
        var local = Matrix3x2.CreateScale(scX, scY, center)
            * Matrix3x2.CreateRotation(ang, center)
            * Matrix3x2.CreateTranslation(dx, dy);
        Gfx.DC.Transform = local * prev;
        return prev;
    }

    public static void Pop(Matrix3x2 prev) => Gfx.DC.Transform = prev;
}
