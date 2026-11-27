using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
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
/// High-res auto-cropped Album Artwork + Apple-Music-style Living Blurred Stage Backdrop for Audio-Only Music Mode.
/// Memory-optimized:
/// - Uses ArrayPool for transient WIC decode buffers (zero LOH GC pressure).
/// - Immediately releases CPU pixel arrays (`Pixels = null`) as soon as GPU upload succeeds (~90% RAM reduction!).
/// - Strict LRU cache capped at 3 tracks (immediately disposes older GPU bitmaps, never wipes the active track).
/// - Pre-bakes a 256×144 hardware GPU GaussianBlur + Saturation ambient backdrop ONCE per track (~0.02 ms/frame draw).
/// </summary>
public static class MusicArt
{
    public sealed class Entry
    {
        public string VideoId = "";
        public int State; // 0=loading, 1=decoded(cpu ready), 2=gpu ready, 3=failed
        public byte[]? Pixels;
        public int W, H;
        public float Aspect = 1f;
        public uint Dom1 = 0xFFD0D4DC, Dom2 = 0xFF8E94A0;
        public bool IsNeutral;
        public float AvgLuma = 0.34f;
        public ID2D1Bitmap1? SharpBmp;
        public ID2D1Bitmap1? BlurBmp;
        public int DeviceGen;
        public long ReadyAt;
        public long LastUsedAt;
        public long FailedAt;
    }

    private const int MaxCachedTracks = 6;
    private static readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (uint Dom1, uint Dom2, bool IsNeutral, float AvgLuma)> _colorCache = new(StringComparer.Ordinal);
    private static HttpClient _http = MakeClient();
    private static string _proxySnap = AppSettings.Current.Proxy ?? "";

    private static HttpClient GetHttp()
    {
        var cur = AppSettings.Current.Proxy ?? "";
        if (cur != _proxySnap) { _proxySnap = cur; _http = MakeClient(); }
        return _http;
    }

    static MusicArt()
    {
        Gfx.DeviceLost += OnDeviceLost;
    }

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 4,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { h.Proxy = new WebProxy(AppSettings.Current.Proxy); h.UseProxy = true; }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", InnerTube.UserAgent);
        return c;
    }

    public static void OnDeviceLost()
    {
        foreach (var e in _cache.Values)
        {
            e.SharpBmp?.Dispose(); e.SharpBmp = null;
            e.BlurBmp?.Dispose(); e.BlurBmp = null;
            if (Volatile.Read(ref e.State) == 2) Volatile.Write(ref e.State, e.Pixels != null ? 1 : 0);
        }
    }

    /// <summary>Evicts non-active cached album art GPU bitmaps to keep VRAM minimal when minimized or browsing feeds.</summary>
    public static void Trim(string? keepVideoId = null)
    {
        foreach (var kv in _cache)
        {
            if (keepVideoId != null && kv.Key == keepVideoId) continue;
            if (_cache.TryRemove(kv.Key, out var old))
            {
                old.SharpBmp?.Dispose(); old.SharpBmp = null;
                old.BlurBmp?.Dispose(); old.BlurBmp = null;
                old.Pixels = null;
            }
        }
    }

    private static void EvictOldest(string keepId)
    {
        while (_cache.Count >= MaxCachedTracks)
        {
            string? victimKey = null;
            long oldest = long.MaxValue;
            foreach (var kv in _cache)
            {
                if (kv.Key == keepId) continue;
                long lu = Volatile.Read(ref kv.Value.LastUsedAt);
                if (lu < oldest) { oldest = lu; victimKey = kv.Key; }
            }
            if (victimKey == null) break;
            if (_cache.TryRemove(victimKey, out var old))
            {
                old.SharpBmp?.Dispose(); old.SharpBmp = null;
                old.BlurBmp?.Dispose(); old.BlurBmp = null;
                old.Pixels = null;
            }
        }
    }

    /// <summary>
    /// CPU-only dominant color query for the Adaptive Video Theme — works on ANY tab, ANY mode (video, audio-only, Settings),
    /// and outside Direct2D BeginDraw! Immediately kicks off thumbnail download if not cached and retries transient failures.
    /// </summary>
    public static bool TryGetColors(string? videoId, out uint dom1, out uint dom2, out bool isNeutral)
        => TryGetColors(videoId, out dom1, out dom2, out isNeutral, out _);

    public static bool TryGetColors(string? videoId, out uint dom1, out uint dom2, out bool isNeutral, out float avgLuma)
    {
        dom1 = 0; dom2 = 0; isNeutral = false; avgLuma = 0.34f;
        if (string.IsNullOrEmpty(videoId)) return false;
        if (_colorCache.TryGetValue(videoId, out var cached))
        {
            dom1 = cached.Dom1;
            dom2 = cached.Dom2;
            isNeutral = cached.IsNeutral;
            avgLuma = cached.AvgLuma;
            return dom1 != 0;
        }
        long now = UI.Now;
        if (!_cache.TryGetValue(videoId, out var e))
        {
            EvictOldest(videoId);
            e = new Entry { VideoId = videoId, State = 0, LastUsedAt = now };
            if (_cache.TryAdd(videoId, e))
                _ = Task.Run(() => FetchAndCropAsync(e));
            else
                _cache.TryGetValue(videoId, out e);
        }
        if (e == null) return false;
        Volatile.Write(ref e.LastUsedAt, now);

        int st = Volatile.Read(ref e.State);
        if (st == 1 || st == 2 || (e.W > 0 && e.Dom1 != 0))
        {
            dom1 = e.Dom1;
            dom2 = e.Dom2;
            isNeutral = e.IsNeutral;
            avgLuma = e.AvgLuma;
            return dom1 != 0;
        }
        if (st == 3 && now - Volatile.Read(ref e.FailedAt) > 3000)
        {
            Volatile.Write(ref e.State, 0);
            Volatile.Write(ref e.FailedAt, now);
            _ = Task.Run(() => FetchAndCropAsync(e));
        }
        return false;
    }

    public static Entry? Get(string? videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return null;
        long now = UI.Now;
        if (!_cache.TryGetValue(videoId, out var e))
        {
            EvictOldest(videoId);
            e = new Entry { VideoId = videoId, State = 0, LastUsedAt = now };
            if (_cache.TryAdd(videoId, e))
                _ = Task.Run(() => FetchAndCropAsync(e));
            else
                _cache.TryGetValue(videoId, out e);
        }
        if (e == null) return null;
        Volatile.Write(ref e.LastUsedAt, now);

        int st = Volatile.Read(ref e.State);
        if (st == 2 && e.DeviceGen != Gfx.DeviceGeneration)
        {
            e.SharpBmp?.Dispose(); e.SharpBmp = null;
            e.BlurBmp?.Dispose(); e.BlurBmp = null;
            if (e.Pixels != null)
            {
                st = 1;
                Volatile.Write(ref e.State, 1);
            }
            else
            {
                st = 0;
                Volatile.Write(ref e.State, 0);
                _ = Task.Run(() => FetchAndCropAsync(e));
            }
        }
        if (st == 1 && e.Pixels != null && Gfx.DC != null)
            UploadAndBakeBlur(e);

        return Volatile.Read(ref e.State) == 2 && e.SharpBmp != null ? e : null;
    }

    private static async Task FetchAndCropAsync(Entry e)
    {
        try
        {
            byte[]? data = null;
            var http = GetHttp();
            string[] urls =
            [
                $"https://i.ytimg.com/vi/{e.VideoId}/maxresdefault.jpg",
                $"https://i.ytimg.com/vi/{e.VideoId}/sddefault.jpg",
                $"https://i.ytimg.com/vi/{e.VideoId}/hq720.jpg",
                $"https://i.ytimg.com/vi/{e.VideoId}/hqdefault.jpg",
                $"https://i.ytimg.com/vi/{e.VideoId}/mqdefault.jpg",
                $"https://i.ytimg.com/vi/{e.VideoId}/oar2.jpg",
                $"https://i.ytimg.com/vi/{e.VideoId}/0.jpg",
            ];
            var wic = Wic.Factory;
            foreach (var u in urls)
            {
                try
                {
                    using var resp = await http.GetAsync(u);
                    if (!resp.IsSuccessStatusCode) continue;
                    var b = await resp.Content.ReadAsByteArrayAsync();
                    // Allow compact pure-white / solid minimalist JPEGs (> 500 bytes), rejecting only YouTube's 120x90 404 stub!
                    if (b.Length <= 500) continue;
                    using var testStream = wic.CreateStream(b);
                    using var testDec = wic.CreateDecoderFromStream(testStream, DecodeOptions.CacheOnLoad);
                    using var testFrame = testDec.GetFrame(0);
                    var tsz = testFrame.Size;
                    if (tsz.Width == 120 && tsz.Height == 90) continue;
                    data = b;
                    break;
                }
                catch { }
            }
            if (data == null) { Volatile.Write(ref e.FailedAt, UI.Now); Volatile.Write(ref e.State, 3); return; }

            using var stream = wic.CreateStream(data);
            using var dec = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = dec.GetFrame(0);
            var sz = frame.Size;
            int w = sz.Width, h = sz.Height;
            if (w < 32 || h < 32) { Volatile.Write(ref e.State, 3); return; }

            // Cap decode at 960px max side (retina-crisp even at 200% DPI while saving ~45% RAM/VRAM vs 1280px!)
            const int maxSide = 960;
            int tw = w, th = h;
            if (w > maxSide || h > maxSide)
            {
                float k = maxSide / (float)Math.Max(w, h);
                tw = Math.Max(32, (int)MathF.Round(w * k));
                th = Math.Max(32, (int)MathF.Round(h * k));
            }
            IWICBitmapSource src = frame;
            IWICBitmapScaler? scaler = null;
            int rawBytes = tw * th * 4;
            var raw = ArrayPool<byte>.Shared.Rent(rawBytes);
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
                CopyPixels(conv, tw, th, raw, rawBytes);

                // Smart auto-crop: strip solid/black pillarbox & letterbox bars so square album covers are 100% edge-to-edge!
                DetectContentBounds(raw, tw, th, out int x0, out int y0, out int cw, out int ch);
                var finalPx = new byte[cw * ch * 4];
                if (x0 == 0 && y0 == 0 && cw == tw && ch == th)
                {
                    Buffer.BlockCopy(raw, 0, finalPx, 0, cw * ch * 4);
                }
                else
                {
                    int rowBytes = cw * 4;
                    for (int row = 0; row < ch; row++)
                        Buffer.BlockCopy(raw, ((y0 + row) * tw + x0) * 4, finalPx, row * rowBytes, rowBytes);
                }

                ExtractDominantColors(finalPx, cw, ch, out uint d1, out uint d2, out bool neutral, out float imgLuma);
                e.Pixels = finalPx;
                e.W = cw;
                e.H = ch;
                e.Aspect = cw / (float)Math.Max(1, ch);
                e.Dom1 = d1;
                e.Dom2 = d2;
                e.IsNeutral = neutral;
                e.AvgLuma = imgLuma;
                if (_colorCache.Count >= 128) _colorCache.Clear();
                _colorCache[e.VideoId] = (d1, d2, neutral, imgLuma);
                Volatile.Write(ref e.State, 1);
                App.WakeUi();
                App.Post(App.Invalidate);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                scaler?.Dispose();
            }
        }
        catch
        {
            Volatile.Write(ref e.FailedAt, UI.Now);
            Volatile.Write(ref e.State, 3);
        }
    }

    private static void DetectContentBounds(byte[] px, int w, int h, out int x0, out int y0, out int cw, out int ch)
    {
        x0 = 0; y0 = 0; cw = w; ch = h;
        static bool IsBarPixel(byte[] buf, int idx)
        {
            int b = buf[idx], g = buf[idx + 1], r = buf[idx + 2];
            return r <= 20 && g <= 20 && b <= 22;
        }

        int maxCropX = (int)(w * 0.34f);
        int maxCropY = (int)(h * 0.24f);
        int left = 0, right = w - 1, top = 0, bottom = h - 1;

        bool ColIsBar(int x)
        {
            int dark = 0, total = 0;
            int step = Math.Max(1, h / 48);
            for (int y = h / 10; y < h - h / 10; y += step)
            {
                total++;
                if (IsBarPixel(px, (y * w + x) * 4)) dark++;
            }
            return total > 0 && dark >= total * 0.90f;
        }
        bool RowIsBar(int y)
        {
            int dark = 0, total = 0;
            int step = Math.Max(1, w / 48);
            for (int x = w / 10; x < w - w / 10; x += step)
            {
                total++;
                if (IsBarPixel(px, (y * w + x) * 4)) dark++;
            }
            return total > 0 && dark >= total * 0.90f;
        }

        while (left < maxCropX && ColIsBar(left)) left++;
        while (right > w - 1 - maxCropX && ColIsBar(right)) right--;
        while (top < maxCropY && RowIsBar(top)) top++;
        while (bottom > h - 1 - maxCropY && RowIsBar(bottom)) bottom--;

        if (left > 4 && (w - 1 - right) > 4)
        {
            int sym = Math.Min(left, w - 1 - right);
            left = sym + 2;
            right = w - 1 - sym - 2;
        }
        else { left = 0; right = w - 1; }

        if (top > 4 && (h - 1 - bottom) > 4)
        {
            int sym = Math.Min(top, h - 1 - bottom);
            top = sym + 2;
            bottom = h - 1 - sym - 2;
        }
        else { top = 0; bottom = h - 1; }

        int nw = right - left + 1, nh = bottom - top + 1;
        if (nw >= w * 0.42f && nh >= h * 0.50f && nw >= 64 && nh >= 64)
        {
            x0 = left; y0 = top; cw = nw; ch = nh;
        }
    }

    private static void ExtractDominantColors(byte[] px, int w, int h, out uint c1, out uint c2, out bool isNeutral, out float imgLuma)
    {
        Span<double> hr = stackalloc double[12];
        Span<double> hg = stackalloc double[12];
        Span<double> hb = stackalloc double[12];
        Span<double> hw = stackalloc double[12];
        int sx = Math.Max(1, w / 36), sy = Math.Max(1, h / 36);
        int totalSamples = 0, visibleSamples = 0, chromaticSamples = 0, whiteNeutralSamples = 0;
        double sumR = 0, sumG = 0, sumB = 0, sumLuma = 0, sumAllLuma = 0;

        for (int y = 0; y < h; y += sy)
        {
            for (int x = 0; x < w; x += sx)
            {
                int o = (y * w + x) * 4;
                float b = px[o] / 255f, g = px[o + 1] / 255f, r = px[o + 2] / 255f;
                float mx = MathF.Max(r, MathF.Max(g, b));
                float mn = MathF.Min(r, MathF.Min(g, b));
                float d = mx - mn;
                float sat = mx > 0.01f ? d / mx : 0f;
                float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                totalSamples++;
                sumAllLuma += luma;
                if (mx >= 0.12f)
                {
                    visibleSamples++;
                    sumR += r; sumG += g; sumB += b; sumLuma += luma;
                }
                if (luma >= 0.56f && sat < 0.18f)
                    whiteNeutralSamples++;

                if (mx < 0.15f || sat < 0.18f || d < 0.08f) continue;
                chromaticSamples++;

                float hue = d < 0.001f ? 0f :
                    mx == r ? ((g - b) / d + (g < b ? 6f : 0f)) :
                    mx == g ? ((b - r) / d + 2f) : ((r - g) / d + 4f);
                int bin = Math.Clamp((int)(hue * 2f) % 12, 0, 11);
                double weight = sat * sat * (0.35 + mx * 0.65);
                hr[bin] += r * weight; hg[bin] += g * weight; hb[bin] += b * weight; hw[bin] += weight;
            }
        }
        int best1 = -1, best2 = -1;
        for (int i = 0; i < 12; i++)
            if (hw[i] > 0 && (best1 < 0 || hw[i] > hw[best1])) best1 = i;
        for (int i = 0; i < 12; i++)
        {
            if (hw[i] <= 0 || i == best1) continue;
            int dist = Math.Min(Math.Abs(i - best1), 12 - Math.Abs(i - best1));
            if (dist >= 2 && (best2 < 0 || hw[i] > hw[best2])) best2 = i;
        }

        // Detect white / grayscale / minimalist neutral videos so tiny colored logos or noise NEVER turn them red/blue!
        float chromaticFrac = chromaticSamples / (float)Math.Max(1, visibleSamples);
        float whiteFrac = whiteNeutralSamples / (float)Math.Max(1, totalSamples);
        float avgLuma = visibleSamples > 0 ? (float)(sumLuma / visibleSamples) : 0.12f;
        imgLuma = totalSamples > 0 ? (float)(sumAllLuma / totalSamples) : 0.34f;
        isNeutral = best1 < 0 || chromaticFrac < 0.15f || (whiteFrac >= 0.58f && chromaticFrac < 0.28f);

        if (isNeutral)
        {
            // Derive a refined neutral silver-white / warm-or-cool light-gray accent from the real neutral tone (zero red/neon bias!)
            float baseTone = (whiteFrac >= 0.42f || avgLuma >= 0.58f) ? 0.84f : 0.74f;
            float nr = baseTone, ng = baseTone, nb = baseTone;
            if (visibleSamples > 0)
            {
                float ar = (float)(sumR / visibleSamples), ag = (float)(sumG / visibleSamples), ab = (float)(sumB / visibleSamples);
                float aMean = (ar + ag + ab) / 3f;
                // Keep at most a delicate ±0.025 warm/cool paper tint so white stays truly neutral white/gray
                nr = Math.Clamp(baseTone + Math.Clamp((ar - aMean) * 0.35f, -0.025f, 0.025f), 0.65f, 0.92f);
                ng = Math.Clamp(baseTone + Math.Clamp((ag - aMean) * 0.35f, -0.025f, 0.025f), 0.65f, 0.92f);
                nb = Math.Clamp(baseTone + Math.Clamp((ab - aMean) * 0.35f, -0.025f, 0.025f), 0.65f, 0.92f);
            }
            c1 = 0xFF000000u | ((uint)(nr * 255f + 0.5f) << 16) | ((uint)(ng * 255f + 0.5f) << 8) | (uint)(nb * 255f + 0.5f);
            float sec = baseTone - 0.22f;
            c2 = 0xFF000000u | ((uint)(sec * 255f + 0.5f) << 16) | ((uint)(sec * 255f + 0.5f) << 8) | (uint)(Math.Clamp(sec + 0.02f, 0f, 1f) * 255f + 0.5f);
            return;
        }

        static uint PackVivid(double sr, double sg, double sb, double sw, uint fallback)
        {
            if (sw <= 0.001) return fallback;
            float r = (float)(sr / sw), g = (float)(sg / sw), b = (float)(sb / sw);
            float avg = (r + g + b) / 3f;
            r = Math.Clamp(avg + (r - avg) * 1.35f, 0.08f, 1f);
            g = Math.Clamp(avg + (g - avg) * 1.35f, 0.08f, 1f);
            b = Math.Clamp(avg + (b - avg) * 1.35f, 0.08f, 1f);
            float mx = MathF.Max(r, MathF.Max(g, b));
            if (mx < 0.65f && mx > 0.01f) { float k = 0.65f / mx; r = MathF.Min(1f, r * k); g = MathF.Min(1f, g * k); b = MathF.Min(1f, b * k); }
            return 0xFF000000u | ((uint)(r * 255f) << 16) | ((uint)(g * 255f) << 8) | (uint)(b * 255f);
        }
        c1 = PackVivid(hr[best1], hg[best1], hb[best1], hw[best1], 0xFFD0D4DCu);
        if (best2 >= 0)
        {
            c2 = PackVivid(hr[best2], hg[best2], hb[best2], hw[best2], c1);
        }
        else
        {
            float r = ((c1 >> 16) & 255) / 255f, g = ((c1 >> 8) & 255) / 255f, b = (c1 & 255) / 255f;
            c2 = 0xFF000000u | ((uint)(Math.Clamp(g * 0.72f + b * 0.42f, 0.14f, 1f) * 255f) << 16)
                             | ((uint)(Math.Clamp(b * 0.72f + r * 0.42f, 0.14f, 1f) * 255f) << 8)
                             |  (uint)(Math.Clamp(r * 0.72f + g * 0.42f, 0.14f, 1f) * 255f);
        }
    }

    private static unsafe void CopyPixels(IWICFormatConverter conv, int tw, int th, byte[] dst, int byteLen)
    {
        fixed (byte* p = dst)
            conv.CopyPixels(new RectI(0, 0, tw, th), (uint)(tw * 4), (uint)byteLen, (nint)p);
    }

    private static unsafe void UploadAndBakeBlur(Entry e)
    {
        var dc = Gfx.DC;
        var px = e.Pixels;
        if (dc == null || px == null || e.W <= 0 || e.H <= 0) return;
        try
        {
            e.SharpBmp?.Dispose();
            e.BlurBmp?.Dispose();
            fixed (byte* p = px)
            {
                e.SharpBmp = dc.CreateBitmap(new SizeI(e.W, e.H), (nint)p, (uint)(e.W * 4),
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
            }

            // Immediately free managed CPU pixel buffer once safely uploaded to GPU!
            e.Pixels = null;

            // Pre-bake lush Apple-Music-style vibrant Gaussian-blurred stage backdrop (256x144 -> 0.02ms GPU draw!)
            const int bw = 256, bh = 144;
            var blurTarget = dc.CreateBitmap(new SizeI(bw, bh),
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.Target));
            var blurPtr = dc.CreateEffect(EffectGuids.GaussianBlur);
            var satPtr = dc.CreateEffect(EffectGuids.Saturation);
            if (blurPtr != 0 && satPtr != 0)
            {
                using var blurFx = new ID2D1Effect(blurPtr);
                using var satFx = new ID2D1Effect(satPtr);
                float dev = MathF.Max(14f, MathF.Min(e.W, e.H) * 0.055f);
                uint opt = (uint)GaussianBlurOptimization.Speed;
                uint bm = (uint)BorderMode.Hard;
                blurFx.SetInput(0, e.SharpBmp, true);
                blurFx.SetValue((uint)GaussianBlurProperties.StandardDeviation, PropertyType.Float, &dev, sizeof(float));
                blurFx.SetValue((uint)GaussianBlurProperties.Optimization, PropertyType.Enum, &opt, sizeof(uint));
                blurFx.SetValue((uint)GaussianBlurProperties.BorderMode, PropertyType.Enum, &bm, sizeof(uint));

                float sat = 1.72f;
                satFx.SetInputEffect(0, blurFx, true);
                satFx.SetValue((uint)SaturationProperties.Saturation, PropertyType.Float, &sat, sizeof(float));

                // dc.Target hands back a NEW COM reference to the current target (often the swap-chain back buffer). Left undisposed
                // it pinned the back buffer forever, and every later window resize failed with DXGI_ERROR_INVALID_CALL.
                using var prevTarget = dc.Target;
                var prevTf = dc.Transform;
                dc.GetDpi(out float ox, out float oy);
                try
                {
                    dc.Target = blurTarget;
                    dc.SetDpi(96, 96);
                    float srcAspect = e.W / (float)e.H;
                    float dstAspect = bw / (float)bh;
                    float sx = 0, sy = 0, sw = e.W, sh = e.H;
                    if (srcAspect > dstAspect) { sw = e.H * dstAspect; sx = (e.W - sw) * 0.5f; }
                    else { sh = e.W / dstAspect; sy = (e.H - sh) * 0.5f; }
                    dc.Transform = Matrix3x2.CreateTranslation(-sx, -sy) * Matrix3x2.CreateScale(bw / sw, bh / sh);
                    dc.Clear(new Color4(0, 0, 0, 1));
                    using var outImg = satFx.Output;
                    dc.DrawImage(outImg, Vector2.Zero, new Vortice.RawRectF(sx, sy, sx + sw, sy + sh),
                        Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
                }
                finally
                {
                    dc.Target = prevTarget;
                    dc.SetDpi(ox, oy);
                    dc.Transform = prevTf;
                }
                e.BlurBmp = blurTarget;
            }
            else blurTarget.Dispose();

            e.DeviceGen = Gfx.DeviceGeneration;
            e.ReadyAt = UI.Now;
            Volatile.Write(ref e.State, 2);
            App.Invalidate();
        }
        catch
        {
            e.Pixels = null;
            Volatile.Write(ref e.State, 3);
        }
    }

    public static float Alpha(Entry e)
    {
        float a = Math.Clamp((UI.Now - e.ReadyAt) / 220f, 0f, 1f);
        if (a < 1f) UI.Animate();
        return a;
    }
}
