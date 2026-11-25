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

    private static ID2D1SolidColorBrush? _scratchBrush;
    private static ID2D1Bitmap1? _grainBmp;
    private static ID2D1BitmapBrush? _grainBrush;
    private sealed class RadialSlot
    {
        public ID2D1GradientStopCollection? Stops;
        public ID2D1RadialGradientBrush? Brush;
        public float R, G, B, A;
        public void Dispose() { Brush?.Dispose(); Stops?.Dispose(); Brush = null; Stops = null; A = -1; }
    }
    private static readonly RadialSlot[] _radials = [new(), new(), new(), new(), new(), new(), new(), new()];
    private static readonly GradientStop[] _radialStopsBuf = new GradientStop[6];

    public static void Init(nint hwnd)
    {
        _hwnd = hwnd;
        _dw = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using (var fonts = _dw.GetSystemFontCollection(false))
            if (!fonts.FindFamilyName(IconFont, out _)) IconFont = "Segoe MDL2 Assets";
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
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            Scaling = Scaling.None, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        _swap = factory.CreateSwapChainForHwnd(_dev, _hwnd, desc);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
        _d2dDev = _d2dFactory.CreateDevice(dxgiDev);
        DC = _d2dDev.CreateDeviceContext(DeviceContextOptions.None);
        DC.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _scratchBrush = DC.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        CreateGrainTexture();
        CreateTarget();
        DeviceGeneration++;
        Occluded = false;
    }

    /// <summary>Builds a seamless 256×256 fine sandblasted-glass micro-grain texture on the GPU (zero per-frame CPU cost).</summary>
    private static void CreateGrainTexture()
    {
        _grainBrush?.Dispose();
        _grainBmp?.Dispose();
        const int sz = 256;
        var px = new byte[sz * sz * 4];
        uint rng = 0x9E3779B9u;
        for (int i = 0; i < sz * sz; i++)
        {
            // Fast xorshift + triangular-ish frosted glass micro-speckle distribution
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            uint r1 = rng & 255;
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            uint r2 = rng & 255;
            int grain = (int)((r1 + r2) >> 1); // 0..255 centered around 128
            // Highlight micro-facets (bright white specks) & subtle etched pits (dark specks) in premultiplied BGRA
            byte a, v;
            if (grain > 134)
            {
                a = (byte)Math.Min(255, (grain - 134) * 2);
                v = a; // premultiplied white micro-crystal
            }
            else if (grain < 120)
            {
                a = (byte)Math.Min(255, (120 - grain) * 2);
                v = 0; // premultiplied dark etched pit
            }
            else
            {
                a = 0; v = 0;
            }
            int o = i * 4;
            px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = a;
        }
        fixed (byte* p = px)
        {
            _grainBmp = DC.CreateBitmap(new SizeI(sz, sz), (nint)p, sz * 4,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
        _grainBrush = DC.CreateBitmapBrush(_grainBmp, new BitmapBrushProperties(ExtendMode.Wrap, ExtendMode.Wrap, BitmapInterpolationMode.NearestNeighbor));
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
        _grainBrush?.Dispose();
        _grainBrush = null;
        _grainBmp?.Dispose();
        _grainBmp = null;
        foreach (var s in _radials) s.Dispose();
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

    public static void Begin(uint clear)
    {
        Frame++;
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

    /// <summary>Zero-allocation reusable brush for immediate Draw/Fill/Stroke calls.</summary>
    private static ID2D1SolidColorBrush Scratch(uint argb)
    {
        var b = _scratchBrush ??= DC.CreateSolidColorBrush(ToColor(argb));
        b.Color = ToColor(argb);
        return b;
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
        if (radius <= 0) { Fill(r, color); return; }
        var rr = new RoundedRectangle { Rect = Raw(r), RadiusX = radius, RadiusY = radius };
        DC.FillRoundedRectangle(rr, Scratch(color));
    }

    public static void Stroke(R r, uint color, float radius, float width = 1)
    {
        var rr = new RoundedRectangle { Rect = Raw(r.Inset(width / 2)), RadiusX = radius, RadiusY = radius };
        DC.DrawRoundedRectangle(rr, Scratch(color), width);
    }

    public static void Circle(float cx, float cy, float radius, uint color)
        => DC.FillEllipse(new Ellipse(new Vector2(cx, cy), radius, radius), Scratch(color));

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
    /// Draws a lush frosted / matte textured glass backdrop behind the entire UI:
    /// deeply diffused ambient light blooms + fine 1:1 physical-pixel sandblasted glass grain.
    /// </summary>
    public static void DrawFrostedBackdrop(float w, float h)
    {
        var mode = Services.AppSettings.Current.FrostedGlass;
        if (mode == "off") return;
        bool strong = mode != "soft";
        float mul = strong ? 1.0f : 0.55f;

        // 1. Soft deeply diffused frosted light washes behind the glass (adapts automatically to any theme!)
        float br = ((Th.Brand >> 16) & 255) / 255f;
        float bg = ((Th.Brand >> 8) & 255) / 255f;
        float bb = (Th.Brand & 255) / 255f;
        // Cool complementary crystal sheen
        float cr = MathF.Min(1f, br * 0.35f + (Th.Light ? 0.45f : 0.32f));
        float cg = MathF.Min(1f, bg * 0.40f + (Th.Light ? 0.52f : 0.46f));
        float cb = MathF.Min(1f, bb * 0.35f + (Th.Light ? 0.70f : 0.68f));

        float a1 = (Th.Light ? 0.085f : 0.115f) * mul;
        float a2 = (Th.Light ? 0.070f : 0.085f) * mul;
        float a3 = (Th.Light ? 0.060f : 0.070f) * mul;
        RadialGlow(w * 0.16f, h * 0.10f, w * 0.52f, h * 0.48f, br, bg, bb, a1, slot: 5);
        RadialGlow(w * 0.86f, h * 0.22f, w * 0.50f, h * 0.52f, cr, cg, cb, a2, slot: 6);
        RadialGlow(w * 0.52f, h * 0.94f, w * 0.60f, h * 0.44f, (br + cr) * 0.5f, (bg + cg) * 0.5f, (bb + cb) * 0.5f, a3, slot: 7);

        // 2. Tactile sandblasted matte glass micro-grain at 1:1 physical pixel crispness
        if (_grainBrush != null)
        {
            _grainBrush.Transform = Matrix3x2.CreateScale(1f / MathF.Max(0.5f, Scale));
            _grainBrush.Opacity = (Th.Light ? 0.055f : 0.062f) * mul;
            DC.FillRectangle(new Vortice.RawRectF(0, 0, w, h), _grainBrush);
        }
    }

    /// <summary>Draws a frosted matte glass panel surface with translucent tint, fine tactile grain, and luminous 1px bevel.</summary>
    public static void FillFrosted(R r, uint baseColor, float radius = 0, float opacity = 0.80f, bool border = false)
    {
        var mode = Services.AppSettings.Current.FrostedGlass;
        if (mode == "off")
        {
            Fill(r, baseColor, radius);
            if (border) Stroke(r, Th.Line, radius);
            return;
        }
        bool strong = mode != "soft";
        float effOp = strong ? opacity : MathF.Min(0.92f, opacity + 0.08f);
        Fill(r, Fade(baseColor, effOp), radius);
        if (_grainBrush != null)
        {
            _grainBrush.Transform = Matrix3x2.CreateScale(1f / MathF.Max(0.5f, Scale));
            _grainBrush.Opacity = (Th.Light ? 0.045f : 0.055f) * (strong ? 1f : 0.55f);
            if (radius > 0)
                DC.FillRoundedRectangle(new RoundedRectangle { Rect = Raw(r), RadiusX = radius, RadiusY = radius }, _grainBrush);
            else
                DC.FillRectangle(Raw(r), _grainBrush);
        }
        if (border)
            Stroke(r, Th.A(Th.Light ? 0.14f : 0.13f), radius, 1f);
    }

    public static void Image(ID2D1Bitmap bmp, R dest, float opacity = 1)
        => DC.DrawBitmap(bmp, (Vortice.RawRectF?)Raw(dest), opacity, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);

    public static void ImageRegion(ID2D1Bitmap bmp, R dest, R src, float opacity = 1)
        => DC.DrawBitmap(bmp, (Vortice.RawRectF?)Raw(dest), opacity, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, (Vortice.RawRectF?)Raw(src), null);

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

    private readonly record struct FKey(int Size10, int Weight, bool Icon);
    private readonly record struct TKey(string Text, int Size10, int Weight, int MaxW, int Lines, bool Icon);

    private static readonly Dictionary<FKey, (IDWriteTextFormat F, IDWriteInlineObject Ellipsis, float LineH)> _formats = new();
    private static Dictionary<TKey, TextRun> _cur = new(), _prev = new();

    private static (IDWriteTextFormat F, IDWriteInlineObject Ellipsis, float LineH) FormatFor(FKey k)
    {
        lock (_formats) return FormatLocked(k);
    }

    private static (IDWriteTextFormat F, IDWriteInlineObject Ellipsis, float LineH) FormatLocked(FKey k)
    {
        if (_formats.TryGetValue(k, out var f)) return f;
        var fmt = _dw.CreateTextFormat(k.Icon ? IconFont : Font, null, (FontWeight)k.Weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, k.Size10 / 10f, "ru-ru");
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
        var (fmt, ellipsis, lineH) = FormatFor(new FKey(key.Size10, key.Weight, key.Icon));
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
            var lineH = FormatFor(new FKey(key.Size10, weight, false)).LineH;
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

    /// <summary>Icon glyph centred in the box.</summary>
    public static void Icon(string glyph, R box, float size, uint color)
    {
        var run = Text(glyph, size, 400, 0, 1, icon: true);
        Draw(run, Snap(box.X + (box.W - run.W) / 2), Snap(box.Y + (box.H - run.H) / 2), color);
    }

    public static float LineHeight(float size, int weight = 400) => FormatFor(new FKey((int)(size * 10), weight, false)).LineH;

    private static void TrimTextCache()
    {
        if (_cur.Count < 900) return;
        foreach (var r in _prev.Values) r.Layout?.Dispose();
        _prev = _cur;
        _cur = new Dictionary<TKey, TextRun>(256);
    }

    /// <summary>Nothing is moving: layouts not drawn since the last trim are let go (they are rebuilt on demand).</summary>
    public static void DropIdleText()
    {
        if (_prev.Count == 0) return;
        foreach (var r in _prev.Values) r.Layout?.Dispose();
        _prev = new Dictionary<TKey, TextRun>();
    }

    private static void ClearTextCache()
    {
        foreach (var r in _prev.Values) r.Layout?.Dispose();
        foreach (var r in _cur.Values) r.Layout?.Dispose();
        _prev.Clear(); _cur.Clear();
    }
}
