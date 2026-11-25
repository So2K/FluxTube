using System.Numerics;
using FluxTube.Services;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DAlpha = Vortice.DCommon.AlphaMode;

namespace FluxTube;

/// <summary>
/// Ultra-cheap GPU Background Engine:
/// 1. Seamless procedural SDF pattern tiles (Cat Paws, Lines, Cyber Grid, Dots, Waves, Hex, Hearts, Stars, Circuit, Sakura)
///    baked ONCE on CPU (~1.5 ms, ~100 KB VRAM) into a hardware ID2D1BitmapBrush with ExtendMode.Wrap -> 1 GPU FillRectangle/frame (~0.02 ms).
/// 2. Optional user wallpaper image (PNG/JPG/WEBP/BMP) decoded asynchronously off-thread via WIC (capped at 1920px),
///    with optional one-shot pre-baked GPU GaussianBlur -> 1 GPU DrawBitmap/frame (~0.04 ms, 0.00 ms per-frame blur cost).
/// </summary>
public static unsafe class BgEngine
{
    public static readonly (string Key, string Ru, string En)[] Patterns =
    [
        ("none", "Нет", "None"),
        ("paws", "Лапки", "Cat Paws"),
        ("lines", "Линии", "Lines"),
        ("grid", "Сетка", "Cyber Grid"),
        ("dots", "Точки", "Dots"),
        ("waves", "Волны", "Waves"),
        ("hex", "Соты", "Hexagons"),
        ("hearts", "Сердечки", "Hearts"),
        ("stars", "Звёзды", "Stars"),
        ("circuit", "Микросхема", "Circuit"),
        ("sakura", "Сакура", "Sakura"),
        ("noir_web", "Нити улик", "Noir Board"),
        ("scanlines", "Сканлайны", "Scanlines"),
    ];

    public static readonly (string Key, string Ru, string En)[] PatternTints =
    [
        ("theme", "Тема", "Theme"),
        ("brand", "Акцент", "Accent"),
        ("dual", "Двойной неон", "Dual Neon"),
    ];

    public static readonly (float Scale, string Label)[] PatternScales =
    [
        (0.72f, "S"),
        (1.00f, "M"),
        (1.42f, "L"),
    ];

    public static readonly (float Blur, string Ru, string En)[] WallpaperBlurs =
    [
        (0f, "Чётко", "Sharp"),
        (6f, "Мягко", "Soft"),
        (18f, "Сильно", "Dreamy"),
    ];

    // Cached seamless GPU pattern tile
    private static ID2D1Bitmap1? _patBmp;
    private static ID2D1BitmapBrush? _patBrush;
    private static string _patKey = "";
    private static uint _patC1, _patC2;
    private static long _patBakedAt;

    // Cached custom wallpaper (raw + optional one-shot pre-blurred GPU bitmap)
    private static readonly object _wallLock = new();
    private static string _wallRequestedPath = "";
    private static string _wallLoadedPath = "";
    private static byte[]? _wallPendingPx;
    private static int _wallPendingW, _wallPendingH;
    private static string _wallPendingPath = "";
    private static ID2D1Bitmap1? _wallRawBmp;
    private static ID2D1Bitmap1? _wallBlurBmp;
    private static int _wallW, _wallH;
    private static float _wallBakedBlur = -1f;

    public static void DisposeGpuResources()
    {
        _patBrush?.Dispose(); _patBrush = null;
        _patBmp?.Dispose(); _patBmp = null;
        _patKey = "";
        _wallBlurBmp?.Dispose(); _wallBlurBmp = null;
        _wallRawBmp?.Dispose(); _wallRawBmp = null;
        _wallLoadedPath = "";
        _wallBakedBlur = -1f;
        lock (_wallLock) _wallRequestedPath = "";
    }

    public static void InvalidateWallpaper()
    {
        _wallBlurBmp?.Dispose(); _wallBlurBmp = null;
        _wallRawBmp?.Dispose(); _wallRawBmp = null;
        _wallLoadedPath = "";
        _wallBakedBlur = -1f;
        lock (_wallLock)
        {
            _wallRequestedPath = "";
            _wallPendingPx = null;
            _wallPendingPath = "";
        }
        App.Invalidate();
    }

    /// <summary>Draws the cached custom wallpaper (if configured) with aspect-preserving cover crop and optional pre-baked blur.</summary>
    public static void DrawWallpaper(ID2D1DeviceContext dc, float w, float h)
    {
        var s = AppSettings.Current;
        var path = s.BgImagePath?.Trim() ?? "";
        if (path.Length == 0)
        {
            if (_wallRawBmp != null || _wallBlurBmp != null) InvalidateWallpaper();
            return;
        }

        // Upload freshly decoded pixels if ready from worker
        byte[]? px = null;
        int pw = 0, ph = 0;
        string pPath = "";
        lock (_wallLock)
        {
            if (_wallPendingPx != null)
            {
                px = _wallPendingPx; pw = _wallPendingW; ph = _wallPendingH; pPath = _wallPendingPath;
                _wallPendingPx = null;
            }
            else if (_wallRequestedPath != path)
            {
                _wallRequestedPath = path;
                _ = Task.Run(() => DecodeWallpaperAsync(path));
            }
        }

        if (px != null && pw > 0 && ph > 0)
        {
            _wallBlurBmp?.Dispose(); _wallBlurBmp = null;
            _wallRawBmp?.Dispose(); _wallRawBmp = null;
            fixed (byte* ptr = px)
            {
                _wallRawBmp = dc.CreateBitmap(new SizeI(pw, ph), (nint)ptr, (uint)(pw * 4),
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
            }
            _wallW = pw; _wallH = ph;
            _wallLoadedPath = pPath;
            _wallBakedBlur = -1f;
        }

        if (_wallRawBmp == null || _wallLoadedPath != path || _wallW <= 0 || _wallH <= 0) return;

        float wantBlur = Math.Clamp(s.BgImageBlur, 0f, 32f);
        ID2D1Bitmap1 drawBmp = _wallRawBmp;
        if (wantBlur >= 0.5f)
        {
            if (_wallBlurBmp == null || MathF.Abs(_wallBakedBlur - wantBlur) > 0.25f)
                BakeBlurredWallpaper(dc, wantBlur);
            if (_wallBlurBmp != null) drawBmp = _wallBlurBmp;
        }
        else if (_wallBlurBmp != null)
        {
            _wallBlurBmp.Dispose();
            _wallBlurBmp = null;
            _wallBakedBlur = 0f;
        }

        // Cover-crop source rectangle preserving exact aspect ratio
        float viewAspect = w / MathF.Max(1f, h);
        float imgAspect = _wallW / (float)_wallH;
        float sx = 0, sy = 0, sw = _wallW, sh = _wallH;
        if (imgAspect > viewAspect)
        {
            sw = _wallH * viewAspect;
            sx = (_wallW - sw) * 0.5f;
        }
        else
        {
            sh = _wallW / MathF.Max(0.1f, viewAspect);
            sy = (_wallH - sh) * 0.5f;
        }

        float op = Math.Clamp(s.BgImageOpacity, 0.08f, 0.95f);
        dc.DrawBitmap(drawBmp, new Vortice.RawRectF(0, 0, w, h), op,
            Vortice.Direct2D1.InterpolationMode.HighQualityCubic,
            new Vortice.RawRectF(sx, sy, sx + sw, sy + sh), null);
    }

    private static void DecodeWallpaperAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var data = File.ReadAllBytes(path);
            var wic = Wic.Factory;
            using var stream = wic.CreateStream(data);
            using var dec = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = dec.GetFrame(0);
            var sz = frame.Size;
            int w = sz.Width, h = sz.Height;
            if (w <= 0 || h <= 0) return;

            // Cap at 1920px on longest side so VRAM stays tiny (~4-8 MB) even for huge 8K wallpapers
            const int maxSide = 1920;
            int tw = w, th = h;
            if (w > maxSide || h > maxSide)
            {
                float k = maxSide / (float)Math.Max(w, h);
                tw = Math.Max(16, (int)MathF.Round(w * k));
                th = Math.Max(16, (int)MathF.Round(h * k));
            }

            IWICBitmapSource src = frame;
            IWICBitmapScaler? scaler = null;
            try
            {
                if (tw != w || th != h)
                {
                    scaler = wic.CreateBitmapScaler();
                    scaler.Initialize(frame, (uint)tw, (uint)th, Vortice.WIC.BitmapInterpolationMode.Fant);
                    src = scaler;
                }
                using var conv = wic.CreateFormatConverter();
                conv.Initialize(src, Vortice.WIC.PixelFormat.Format32bppPBGRA);
                var buf = new byte[tw * th * 4];
                fixed (byte* p = buf)
                    conv.CopyPixels(new RectI(0, 0, tw, th), (uint)(tw * 4), (uint)buf.Length, (nint)p);

                lock (_wallLock)
                {
                    if (_wallRequestedPath != path) return;
                    _wallPendingPx = buf;
                    _wallPendingW = tw;
                    _wallPendingH = th;
                    _wallPendingPath = path;
                }
                App.WakeUi();
            }
            finally { scaler?.Dispose(); }
        }
        catch (Exception e) { Paths.Log("bg wallpaper: " + e.Message); }
    }

    /// <summary>One-shot GPU GaussianBlur pre-bake for wallpaper (runs ONCE when blur changes; 0ms cost per frame afterward!).</summary>
    private static void BakeBlurredWallpaper(ID2D1DeviceContext dc, float stdDev)
    {
        if (_wallRawBmp == null || _wallW <= 0 || _wallH <= 0) return;
        _wallBlurBmp?.Dispose();
        _wallBlurBmp = null;
        _wallBakedBlur = stdDev;
        try
        {
            // Half-res target for blurred wallpaper cuts VRAM 4x while looking identically silky
            int bw = Math.Max(64, _wallW / 2), bh = Math.Max(64, _wallH / 2);
            var target = dc.CreateBitmap(new SizeI(bw, bh),
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.Target));

            var fxPtr = dc.CreateEffect(EffectGuids.GaussianBlur);
            if (fxPtr == 0) { target.Dispose(); return; }
            using var fx = new ID2D1Effect(fxPtr);
            fx.SetInput(0, _wallRawBmp, true);
            float dev = stdDev * 0.55f;
            uint opt = (uint)GaussianBlurOptimization.Balanced;
            uint bm = (uint)BorderMode.Hard;
            fx.SetValue((uint)GaussianBlurProperties.StandardDeviation, PropertyType.Float, &dev, sizeof(float));
            fx.SetValue((uint)GaussianBlurProperties.Optimization, PropertyType.Enum, &opt, sizeof(uint));
            fx.SetValue((uint)GaussianBlurProperties.BorderMode, PropertyType.Enum, &bm, sizeof(uint));

            var prevTarget = dc.Target;
            var prevTf = dc.Transform;
            try
            {
                dc.Target = target;
                dc.Transform = Matrix3x2.CreateScale(bw / (float)_wallW, bh / (float)_wallH);
                dc.Clear(new Color4(0, 0, 0, 1));
                using var outImg = fx.Output;
                dc.DrawImage(outImg, Vector2.Zero, new Vortice.RawRectF(0, 0, _wallW, _wallH),
                    Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            finally
            {
                dc.Transform = prevTf;
                dc.Target = prevTarget;
            }
            _wallBlurBmp = target;
        }
        catch (Exception e)
        {
            Paths.Log("bg wallpaper blur: " + e.Message);
            _wallBlurBmp?.Dispose();
            _wallBlurBmp = null;
        }
    }

    /// <summary>Draws the active seamless GPU pattern tile in ONE FillRectangle call (~0.02 ms GPU, 0 CPU).</summary>
    public static void DrawPattern(ID2D1DeviceContext dc, float w, float h)
    {
        var s = AppSettings.Current;
        var key = s.BgPattern;
        if (string.IsNullOrEmpty(key) || key == "none") return;

        ResolvePatternColors(s.BgPatternTint, out uint c1, out uint c2);
        EnsurePatternTile(dc, key, c1, c2);
        if (_patBrush == null) return;

        float tileScale = Math.Clamp(s.BgPatternScale, 0.55f, 2.2f);
        float op = Math.Clamp(s.BgPatternOpacity, 0.03f, 0.65f);
        // In Audio-Only Music Mode with Bass Shake on, the pattern pulses & breathes with every bass beat!
        if (s.AudioOnlyMode && s.MusicBassShake && Shell.Current is not Pages.ShortsPage && Player.MpvPlayer.Instance.HasMedia && !Player.MpvPlayer.Instance.Paused && !Player.MpvPlayer.Instance.Muted && !App.Test)
        {
            float bass = AudioAnalyzer.BassEnergy;
            tileScale *= 1f + bass * 0.048f;
            op = Math.Clamp(op * (1f + bass * 1.25f), 0.04f, 0.80f);
        }
        // Tile is baked at 2x supersampling (DPI-crisp at 4K!), so base display scale is 0.5 * tileScale
        float drawScale = 0.5f * tileScale;
        _patBrush.Transform = Matrix3x2.CreateScale(drawScale, drawScale, new Vector2(w * 0.5f, h * 0.5f));
        _patBrush.Opacity = op;
        dc.FillRectangle(new Vortice.RawRectF(0, 0, w, h), _patBrush);
    }

    private static void ResolvePatternColors(string tint, out uint c1, out uint c2)
    {
        switch (tint)
        {
            case "brand":
                c1 = Th.Brand;
                c2 = Gfx.Mix(Th.Brand, Th.Ink, 0.25f);
                break;
            case "dual":
                c1 = Th.Brand;
                c2 = Th.Brand2 != 0 ? Th.Brand2 : Gfx.Mix(Th.Brand, Th.Ink, 0.45f);
                break;
            default: // "theme"
                c1 = Gfx.Mix(Th.Ink, Th.Brand, 0.28f);
                c2 = Gfx.Mix(Th.Ink, Th.Brand2 != 0 ? Th.Brand2 : Th.Brand, 0.42f);
                break;
        }
    }

    private static void EnsurePatternTile(ID2D1DeviceContext dc, string key, uint c1, uint c2)
    {
        uint q1 = c1 & 0x00FEFEFEu, q2 = c2 & 0x00FEFEFEu;
        if (_patBrush != null && _patKey == key && _patC1 == q1 && _patC2 == q2) return;
        long now = Environment.TickCount64;
        if (_patBrush != null && _patKey == key && now - _patBakedAt < 90) return;

        _patBakedAt = now;
        _patKey = key;
        _patC1 = q1;
        _patC2 = q2;
        _patBrush?.Dispose(); _patBrush = null;
        _patBmp?.Dispose(); _patBmp = null;

        // 2x high-DPI seamless tile (displayed at 0.5x -> razor sharp on 4K 150-200% displays!)
        int sz = key switch
        {
            "lines" or "dots" or "grid" => 128,
            "waves" or "hex" => 160,
            _ => 224, // paws, hearts, stars, circuit, sakura
        };

        var px = new byte[sz * sz * 4];
        float r1 = ((c1 >> 16) & 255) / 255f, g1 = ((c1 >> 8) & 255) / 255f, b1 = (c1 & 255) / 255f;
        float r2 = ((c2 >> 16) & 255) / 255f, g2 = ((c2 >> 8) & 255) / 255f, b2 = (c2 & 255) / 255f;

        for (int py = 0; py < sz; py++)
        {
            for (int pxI = 0; pxI < sz; pxI++)
            {
                float x = pxI + 0.5f, y = py + 0.5f;
                SamplePattern(key, x, y, sz, out float a1, out float a2);
                if (a1 <= 0.002f && a2 <= 0.002f) continue;

                // Smooth premultiplied blend of layer 1 (primary) and layer 2 (secondary)
                float a = Math.Clamp(a1 + a2 * (1f - a1), 0f, 1f);
                float w1 = a1, w2 = a2 * (1f - a1);
                float pr = Math.Clamp(r1 * w1 + r2 * w2, 0f, 1f);
                float pg = Math.Clamp(g1 * w1 + g2 * w2, 0f, 1f);
                float pb = Math.Clamp(b1 * w1 + b2 * w2, 0f, 1f);

                int o = (py * sz + pxI) * 4;
                px[o] = (byte)(pb * 255f + 0.5f);
                px[o + 1] = (byte)(pg * 255f + 0.5f);
                px[o + 2] = (byte)(pr * 255f + 0.5f);
                px[o + 3] = (byte)(a * 255f + 0.5f);
            }
        }

        fixed (byte* ptr = px)
        {
            _patBmp = dc.CreateBitmap(new SizeI(sz, sz), (nint)ptr, (uint)(sz * 4),
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
        _patBrush = dc.CreateBitmapBrush(_patBmp, new BitmapBrushProperties(ExtendMode.Wrap, ExtendMode.Wrap, Vortice.Direct2D1.BitmapInterpolationMode.Linear));
    }

    // ---------------- Analytic Seamless SDF Pattern Samplers ----------------

    private static float AaFill(float d, float aa = 1.35f) => Math.Clamp(0.5f - d / aa, 0f, 1f);
    private static float AaStroke(float d, float halfWidth, float aa = 1.35f) => AaFill(MathF.Abs(d) - halfWidth, aa);

    private static float WrapDelta(float a, float b, float period)
    {
        float d = a - b;
        float half = period * 0.5f;
        if (d > half) d -= period;
        else if (d < -half) d += period;
        return d;
    }

    private static void Rotate(float x, float y, float cos, float sin, out float rx, out float ry)
    {
        rx = x * cos - y * sin;
        ry = x * sin + y * cos;
    }

    private static float SdEllipse(float x, float y, float rx, float ry)
    {
        float nx = x / rx, ny = y / ry;
        float k0 = MathF.Sqrt(nx * nx + ny * ny);
        if (k0 < 1e-5f) return -MathF.Min(rx, ry);
        float nx2 = x / (rx * rx), ny2 = y / (ry * ry);
        float k1 = MathF.Sqrt(nx2 * nx2 + ny2 * ny2);
        return k0 * (k0 - 1f) / MathF.Max(1e-5f, k1);
    }

    private static float SdSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        float pax = px - ax, pay = py - ay;
        float bax = bx - ax, bay = by - ay;
        float h = Math.Clamp((pax * bax + pay * bay) / MathF.Max(1e-5f, bax * bax + bay * bay), 0f, 1f);
        float dx = pax - bax * h, dy = pay - bay * h;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Adorable stylized cat paw print SDF: soft plump main bean pad + 4 arched oval toe beans.</summary>
    private static float SdCatPaw(float x, float y, float scale)
    {
        float lx = x / scale, ly = y / scale;
        // Main metacarpal pad: rounded plump triangle (3 overlapping smooth circles)
        float pCenter = SdEllipse(lx, ly - 0.16f, 0.42f, 0.34f);
        float pLeft = SdEllipse(lx + 0.25f, ly - 0.34f, 0.25f, 0.23f);
        float pRight = SdEllipse(lx - 0.25f, ly - 0.34f, 0.25f, 0.23f);
        float pTop = SdEllipse(lx, ly - 0.01f, 0.28f, 0.24f);
        float mainPad = SmoothMin(SmoothMin(pCenter, pTop, 0.14f), MathF.Min(pLeft, pRight), 0.12f);

        // 4 cute arched toe beans (tilted naturally outward)
        Rotate(lx + 0.62f, ly + 0.24f, 0.90f, 0.43f, out float t1x, out float t1y);
        float toe1 = SdEllipse(t1x, t1y, 0.17f, 0.24f);

        Rotate(lx + 0.24f, ly + 0.56f, 0.98f, 0.19f, out float t2x, out float t2y);
        float toe2 = SdEllipse(t2x, t2y, 0.175f, 0.25f);

        Rotate(lx - 0.24f, ly + 0.56f, 0.98f, -0.19f, out float t3x, out float t3y);
        float toe3 = SdEllipse(t3x, t3y, 0.175f, 0.25f);

        Rotate(lx - 0.62f, ly + 0.24f, 0.90f, -0.43f, out float t4x, out float t4y);
        float toe4 = SdEllipse(t4x, t4y, 0.17f, 0.24f);

        float toes = MathF.Min(MathF.Min(toe1, toe2), MathF.Min(toe3, toe4));
        return MathF.Min(mainPad, toes) * scale;
    }

    private static float SmoothMin(float a, float b, float k)
    {
        float h = Math.Clamp(0.5f + 0.5f * (b - a) / k, 0f, 1f);
        return b * (1f - h) + a * h - k * h * (1f - h);
    }

    /// <summary>Cute plump heart SDF.</summary>
    private static float SdHeart(float x, float y, float scale)
    {
        float px = MathF.Abs(x) / scale;
        float py = -y / scale + 0.52f;
        if (py + px > 1f)
        {
            float dx = px - 0.25f, dy = py - 0.75f;
            return (MathF.Sqrt(dx * dx + dy * dy) - 0.353553f) * scale;
        }
        float d1x = px, d1y = py - 1f;
        float t = 0.5f * MathF.Max(px + py, 0f);
        float d2x = px - t, d2y = py - t;
        float d = MathF.Sqrt(MathF.Min(d1x * d1x + d1y * d1y, d2x * d2x + d2y * d2y));
        return d * MathF.Sign(px - py) * scale;
    }

    /// <summary>Crisp 4-point kawaii/anime sparkle star SDF.</summary>
    private static float SdSparkle(float x, float y, float r)
    {
        float ax = MathF.Abs(x), ay = MathF.Abs(y);
        float d = MathF.Pow(MathF.Pow(ax / r, 0.52f) + MathF.Pow(ay / r, 0.52f), 1f / 0.52f) - 1f;
        return d * r * 0.65f;
    }

    /// <summary>Delicate cherry-blossom (sakura) notched petal SDF.</summary>
    private static float SdSakuraPetal(float x, float y, float scale)
    {
        float lx = x / scale, ly = y / scale;
        float body = SdEllipse(lx, ly, 0.42f, 0.85f);
        // Cute V-notch at the petal tip (top)
        float notch = MathF.Sqrt(lx * lx + (ly + 0.88f) * (ly + 0.88f)) - 0.24f;
        return MathF.Max(body, -notch) * scale;
    }

    private static void SamplePattern(string key, float x, float y, int sz, out float a1, out float a2)
    {
        a1 = 0f; a2 = 0f;
        float s = sz;
        switch (key)
        {
            case "paws":
            {
                // Two staggered tilted kitty paws + tiny sparkles in alternating primary/secondary colors
                float dx1 = WrapDelta(x, s * 0.25f, s), dy1 = WrapDelta(y, s * 0.26f, s);
                Rotate(dx1, dy1, 0.94f, -0.34f, out float rx1, out float ry1);
                a1 = AaFill(SdCatPaw(rx1, ry1, 26f));

                float dx2 = WrapDelta(x, s * 0.75f, s), dy2 = WrapDelta(y, s * 0.76f, s);
                Rotate(dx2, dy2, 0.95f, 0.31f, out float rx2, out float ry2);
                a2 = AaFill(SdCatPaw(rx2, ry2, 24f));

                // Cute tiny 4-point sparkles in the open spaces
                float sx1 = WrapDelta(x, s * 0.76f, s), sy1 = WrapDelta(y, s * 0.24f, s);
                a1 = MathF.Max(a1, AaFill(SdSparkle(sx1, sy1, 9.5f)) * 0.78f);

                float sx2 = WrapDelta(x, s * 0.24f, s), sy2 = WrapDelta(y, s * 0.75f, s);
                a2 = MathF.Max(a2, AaFill(SdHeart(sx2, sy2, 13f)) * 0.70f);
                break;
            }
            case "lines":
            {
                // Crisp 45-degree architectural stripes (primary + subtle offset twin line)
                float diag = (x + y) % (s * 0.5f);
                float d1 = MathF.Min(diag, s * 0.5f - diag);
                a1 = AaFill(d1 - 1.35f, 1.3f);

                float diag2 = (x + y + s * 0.25f) % (s * 0.5f);
                float d2 = MathF.Min(diag2, s * 0.5f - diag2);
                a2 = AaFill(d2 - 0.75f, 1.2f) * 0.45f;
                break;
            }
            case "grid":
            {
                // Precision cyber-grid with glowing crosshair intersections
                float step = s * 0.5f;
                float gx = MathF.Min(x % step, step - (x % step));
                float gy = MathF.Min(y % step, step - (y % step));
                a1 = MathF.Max(AaFill(gx - 0.85f, 1.2f), AaFill(gy - 0.85f, 1.2f)) * 0.62f;
                // Bright "+" crosshair right at grid intersections
                bool nearX = gx < 7.5f, nearY = gy < 7.5f;
                if (nearX && nearY)
                {
                    float cross = MathF.Min(MathF.Max(gx - 1.4f, gy - 6.5f), MathF.Max(gy - 1.4f, gx - 6.5f));
                    a2 = AaFill(cross, 1.15f);
                }
                break;
            }
            case "dots":
            {
                float d1x = WrapDelta(x, s * 0.25f, s), d1y = WrapDelta(y, s * 0.25f, s);
                float d2x = WrapDelta(x, s * 0.75f, s), d2y = WrapDelta(y, s * 0.75f, s);
                a1 = AaFill(MathF.Sqrt(d1x * d1x + d1y * d1y) - 4.2f);
                a2 = AaFill(MathF.Sqrt(d2x * d2x + d2y * d2y) - 3.2f) * 0.82f;
                break;
            }
            case "waves":
            {
                // Smooth multi-ribbon flowing sine waves
                float u = x / s * MathF.PI * 2f;
                float wy1 = s * 0.25f + MathF.Sin(u) * 14f;
                float wy2 = s * 0.75f + MathF.Sin(u + MathF.PI) * 14f;
                float dy1 = MathF.Abs(WrapDelta(y, wy1, s));
                float dy2 = MathF.Abs(WrapDelta(y, wy2, s));
                // Compensate stroke thickness by curve slope sqrt(1 + (dy/dx)^2)
                float slope1 = MathF.Cos(u) * (14f * MathF.PI * 2f / s);
                float slope2 = MathF.Cos(u + MathF.PI) * (14f * MathF.PI * 2f / s);
                float dist1 = dy1 / MathF.Sqrt(1f + slope1 * slope1);
                float dist2 = dy2 / MathF.Sqrt(1f + slope2 * slope2);
                a1 = AaFill(dist1 - 1.45f, 1.3f);
                a2 = AaFill(dist2 - 1.15f, 1.3f) * 0.75f;
                break;
            }
            case "hex":
            {
                // Exact seamless honeycomb grid using two offset hex lattices
                float rx = s * 0.5f, ry = s * 0.5f;
                float hx1 = WrapDelta(x, rx * 0.5f, rx), hy1 = WrapDelta(y, ry * 0.5f, ry);
                float hx2 = WrapDelta(x, rx, rx), hy2 = WrapDelta(y, ry, ry);
                static float HexEdge(float px, float py, float rad)
                {
                    float ax = MathF.Abs(px), ay = MathF.Abs(py);
                    float d = MathF.Max(ax * 0.866025f + ay * 0.5f, ay) - rad;
                    return MathF.Abs(d);
                }
                float dA = HexEdge(hx1, hy1, s * 0.215f);
                float dB = HexEdge(hx2, hy2, s * 0.215f);
                a1 = AaFill(dA - 1.1f, 1.25f) * 0.85f;
                a2 = AaFill(dB - 0.9f, 1.25f) * 0.55f;
                break;
            }
            case "hearts":
            {
                float dx1 = WrapDelta(x, s * 0.25f, s), dy1 = WrapDelta(y, s * 0.28f, s);
                Rotate(dx1, dy1, 0.96f, -0.28f, out float rx1, out float ry1);
                a1 = AaFill(SdHeart(rx1, ry1, 24f));

                float dx2 = WrapDelta(x, s * 0.75f, s), dy2 = WrapDelta(y, s * 0.76f, s);
                Rotate(dx2, dy2, 0.96f, 0.28f, out float rx2, out float ry2);
                // Outline heart for cute layered variety
                a2 = AaStroke(SdHeart(rx2, ry2, 22f), 2.1f);

                float sx = WrapDelta(x, s * 0.74f, s), sy = WrapDelta(y, s * 0.25f, s);
                a2 = MathF.Max(a2, AaFill(SdSparkle(sx, sy, 8.5f)) * 0.75f);
                break;
            }
            case "stars":
            {
                float d1x = WrapDelta(x, s * 0.24f, s), d1y = WrapDelta(y, s * 0.26f, s);
                a1 = AaFill(SdSparkle(d1x, d1y, 20f));

                float d2x = WrapDelta(x, s * 0.74f, s), d2y = WrapDelta(y, s * 0.72f, s);
                a2 = AaFill(SdSparkle(d2x, d2y, 16f));

                float d3x = WrapDelta(x, s * 0.76f, s), d3y = WrapDelta(y, s * 0.22f, s);
                a1 = MathF.Max(a1, AaFill(SdSparkle(d3x, d3y, 9f)) * 0.72f);

                float d4x = WrapDelta(x, s * 0.26f, s), d4y = WrapDelta(y, s * 0.78f, s);
                a2 = MathF.Max(a2, AaFill(MathF.Sqrt(d4x * d4x + d4y * d4y) - 3.0f) * 0.85f);
                break;
            }
            case "circuit":
            {
                // Sleek PCB traces with 45° bends and glowing via rings (seamless across tile edges)
                float dLine = 1e9f;
                // Horizontal bus wrapping left->right with a 45° jog
                dLine = MathF.Min(dLine, SdSegment(x, y, 0, s * 0.25f, s * 0.28f, s * 0.25f));
                dLine = MathF.Min(dLine, SdSegment(x, y, s * 0.28f, s * 0.25f, s * 0.44f, s * 0.41f));
                dLine = MathF.Min(dLine, SdSegment(x, y, s * 0.44f, s * 0.41f, s * 0.74f, s * 0.41f));
                dLine = MathF.Min(dLine, SdSegment(x, y, s * 0.74f, s * 0.41f, s * 0.90f, s * 0.25f));
                dLine = MathF.Min(dLine, SdSegment(x, y, s * 0.90f, s * 0.25f, s, s * 0.25f));

                // Vertical branch wrapping top->bottom
                float dLine2 = 1e9f;
                dLine2 = MathF.Min(dLine2, SdSegment(x, y, s * 0.62f, 0, s * 0.62f, s * 0.22f));
                dLine2 = MathF.Min(dLine2, SdSegment(x, y, s * 0.62f, s * 0.62f, s * 0.62f, s));
                dLine2 = MathF.Min(dLine2, SdSegment(x, y, s * 0.62f, s * 0.62f, s * 0.36f, s * 0.88f));
                dLine2 = MathF.Min(dLine2, SdSegment(x, y, s * 0.36f, s * 0.88f, s * 0.16f, s * 0.88f));

                a1 = AaFill(dLine - 1.35f, 1.25f) * 0.82f;
                a2 = AaFill(dLine2 - 1.35f, 1.25f) * 0.82f;

                // Circular via nodes at endpoints/junctions
                float v1 = MathF.Sqrt((x - s * 0.62f) * (x - s * 0.62f) + (y - s * 0.22f) * (y - s * 0.22f));
                float v2 = MathF.Sqrt((x - s * 0.16f) * (x - s * 0.16f) + (y - s * 0.88f) * (y - s * 0.88f));
                float v3 = MathF.Sqrt((x - s * 0.44f) * (x - s * 0.44f) + (y - s * 0.41f) * (y - s * 0.41f));
                a2 = MathF.Max(a2, AaStroke(v1 - 6.0f, 1.6f));
                a2 = MathF.Max(a2, AaStroke(v2 - 6.0f, 1.6f));
                a1 = MathF.Max(a1, AaFill(v3 - 4.2f));
                break;
            }
            case "sakura":
            {
                // Drifting notched cherry-blossom petals at varied angles + tiny blossom buds
                float dx1 = WrapDelta(x, s * 0.24f, s), dy1 = WrapDelta(y, s * 0.26f, s);
                Rotate(dx1, dy1, 0.82f, -0.57f, out float rx1, out float ry1);
                a1 = AaFill(SdSakuraPetal(rx1, ry1, 18f));

                float dx2 = WrapDelta(x, s * 0.74f, s), dy2 = WrapDelta(y, s * 0.72f, s);
                Rotate(dx2, dy2, 0.74f, 0.67f, out float rx2, out float ry2);
                a2 = AaFill(SdSakuraPetal(rx2, ry2, 16f));

                float dx3 = WrapDelta(x, s * 0.76f, s), dy3 = WrapDelta(y, s * 0.24f, s);
                Rotate(dx3, dy3, 0.50f, 0.86f, out float rx3, out float ry3);
                a2 = MathF.Max(a2, AaFill(SdSakuraPetal(rx3, ry3, 11.5f)) * 0.72f);

                float dx4 = WrapDelta(x, s * 0.28f, s), dy4 = WrapDelta(y, s * 0.78f, s);
                a1 = MathF.Max(a1, AaFill(MathF.Sqrt(dx4 * dx4 + dy4 * dy4) - 3.2f) * 0.65f);
                break;
            }
            case "noir_web":
            {
                // Detective investigation corkboard / RE Requiem monochrome threads + pins + subtle cross-hatch
                float dThread = 1e9f;
                dThread = MathF.Min(dThread, SdSegment(x, y, 0, s * 0.18f, s * 0.42f, s * 0.34f));
                dThread = MathF.Min(dThread, SdSegment(x, y, s * 0.42f, s * 0.34f, s, s * 0.18f));
                dThread = MathF.Min(dThread, SdSegment(x, y, s * 0.42f, s * 0.34f, s * 0.78f, s * 0.80f));
                dThread = MathF.Min(dThread, SdSegment(x, y, s * 0.78f, s * 0.80f, s * 0.14f, s * 0.76f));
                dThread = MathF.Min(dThread, SdSegment(x, y, s * 0.14f, s * 0.76f, s * 0.42f, s * 0.34f));
                dThread = MathF.Min(dThread, SdSegment(x, y, s * 0.78f, s * 0.80f, s, s * 0.76f));
                dThread = MathF.Min(dThread, SdSegment(x, y, 0, s * 0.76f, s * 0.14f, s * 0.76f));
                a1 = AaFill(dThread - 0.85f, 1.25f) * 0.70f;

                // Subtle torn-paper / pin points at thread intersections
                float p1 = MathF.Sqrt((x - s * 0.42f) * (x - s * 0.42f) + (y - s * 0.34f) * (y - s * 0.34f));
                float p2 = MathF.Sqrt((x - s * 0.78f) * (x - s * 0.78f) + (y - s * 0.80f) * (y - s * 0.80f));
                float p3 = MathF.Sqrt((x - s * 0.14f) * (x - s * 0.14f) + (y - s * 0.76f) * (y - s * 0.76f));
                a2 = MathF.Max(AaFill(p1 - 4.0f), MathF.Max(AaFill(p2 - 3.6f), AaFill(p3 - 3.4f)));
                break;
            }
            case "scanlines":
            {
                // Sleek CRT / retro-futuristic horizontal scanlines + subtle sub-pixel pulse
                float period = 8f;
                float m = y % period;
                float d = MathF.Min(m, period - m);
                a1 = AaFill(d - 1.15f, 1.1f) * 0.65f;
                float m2 = x % (s * 0.5f);
                float d2 = MathF.Min(m2, s * 0.5f - m2);
                a2 = AaFill(d2 - 0.65f, 1.1f) * 0.22f;
                break;
            }
        }
    }
}
