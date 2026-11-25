using System.Collections.Concurrent;
using System.Net.Http;
using FluxTube.Player;
using FluxTube.Services;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FluxTube;

/// <summary>
/// Cinema-grade GPU Ambient Mode: extracts a 5-zone color palette (4 corners + center)
/// live from the playing video's storyboard frames (with instant thumbnail fallback for Shorts
/// and un-storyboarded videos), smoothly eases colors over time, and renders 5 hardware-accelerated
/// Direct2D radial Gaussian light fields around the video at < 0.1 ms GPU cost.
/// </summary>
public static class Ambient
{
    private readonly record struct Palette(
        (float R, float G, float B) TL,
        (float R, float G, float B) TR,
        (float R, float G, float B) BL,
        (float R, float G, float B) BR,
        (float R, float G, float B) C);

    private static readonly ConcurrentDictionary<string, Palette> _thumbPalettes = new();
    private static readonly ConcurrentDictionary<string, byte> _thumbLoading = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    [ThreadStatic] private static IWICImagingFactory? t_wic;

    private static string? _curId;
    private static long _lastSampleAt;
    private static Palette _target = DefaultPalette();
    private static Palette _cur = DefaultPalette();

    private static Palette DefaultPalette() => new(
        (0.24f, 0.26f, 0.34f), (0.26f, 0.22f, 0.34f),
        (0.20f, 0.24f, 0.32f), (0.24f, 0.20f, 0.30f),
        (0.28f, 0.26f, 0.36f));

    private static (float R, float G, float B) Enhance((float R, float G, float B) c)
    {
        float avg = (c.R + c.G + c.B) / 3f;
        // Vibrant saturation boost (+45%) so the glow feels rich and cinematic
        float r = Math.Clamp(avg + (c.R - avg) * 1.45f, 0f, 1f);
        float g = Math.Clamp(avg + (c.G - avg) * 1.45f, 0f, 1f);
        float b = Math.Clamp(avg + (c.B - avg) * 1.45f, 0f, 1f);
        // Gentle luminance floor so dark scenes still cast a subtle moody halo
        float max = MathF.Max(r, MathF.Max(g, b));
        if (max < 0.18f && max > 0.01f)
        {
            float k = 0.18f / max;
            r = MathF.Min(1f, r * k); g = MathF.Min(1f, g * k); b = MathF.Min(1f, b * k);
        }
        return (r, g, b);
    }

    private static Palette EnhanceAll(Palette p) => new(Enhance(p.TL), Enhance(p.TR), Enhance(p.BL), Enhance(p.BR), Enhance(p.C));

    private static void EnsureThumbPalette(string videoId, bool isShort)
    {
        if (_thumbPalettes.ContainsKey(videoId) || !_thumbLoading.TryAdd(videoId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                byte[]? data = null;
                if (isShort)
                {
                    try { data = await _http.GetByteArrayAsync($"https://i.ytimg.com/vi/{videoId}/oar2.jpg"); } catch { }
                }
                data ??= await _http.GetByteArrayAsync($"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg");
                if (t_wic == null) W.CoInitializeEx(0, 0);
                var wic = t_wic ??= new IWICImagingFactory();
                using var stream = wic.CreateStream(data);
                using var dec = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
                using var frame = dec.GetFrame(0);
                var sz = frame.Size;
                using var conv = wic.CreateFormatConverter();
                conv.Initialize(frame, PixelFormat.Format32bppBGRA);
                var px = Copy(conv, sz.Width, sz.Height);
                int w = sz.Width, h = sz.Height;
                var pal = EnhanceAll(new Palette(
                    Avg(px, w, 0, 0, w / 2, h / 2),
                    Avg(px, w, w / 2, 0, w / 2, h / 2),
                    Avg(px, w, 0, h / 2, w / 2, h / 2),
                    Avg(px, w, w / 2, h / 2, w / 2, h / 2),
                    Avg(px, w, w / 4, h / 4, w / 2, h / 2)));
                _thumbPalettes[videoId] = pal;
                if (_thumbPalettes.Count > 64) _thumbPalettes.Clear();
                App.WakeUi();
            }
            catch { }
            finally { _thumbLoading.TryRemove(videoId, out _); }
        });
    }

    private static unsafe byte[] Copy(IWICBitmapSource src, int w, int h)
    {
        var px = new byte[w * h * 4];
        fixed (byte* p = px) src.CopyPixels(new RectI(0, 0, w, h), (uint)(w * 4), (uint)px.Length, (nint)p);
        return px;
    }

    private static (float R, float G, float B) Avg(byte[] px, int w, int x0, int y0, int rw, int rh)
    {
        int sx = Math.Max(1, rw / 8), sy = Math.Max(1, rh / 6);
        double sr = 0, sg = 0, sb = 0; int n = 0;
        for (int y = y0; y < y0 + rh; y += sy)
        {
            int row = y * w * 4;
            for (int x = x0; x < x0 + rw; x += sx)
            {
                int o = row + x * 4;
                sb += px[o]; sg += px[o + 1]; sr += px[o + 2];
                n++;
            }
        }
        return n == 0 ? (0.2f, 0.2f, 0.25f) : ((float)(sr / (n * 255.0)), (float)(sg / (n * 255.0)), (float)(sb / (n * 255.0)));
    }

    private static (float R, float G, float B) Lerp((float R, float G, float B) a, (float R, float G, float B) b, float t)
        => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static float Diff((float R, float G, float B) a, (float R, float G, float B) b)
        => MathF.Abs(a.R - b.R) + MathF.Abs(a.G - b.G) + MathF.Abs(a.B - b.B);

    /// <summary>Draws the soft multi-zone GPU ambient glow behind videoRect.</summary>
    public static void Draw(R videoRect, string videoId, bool isShort = false)
    {
        if (!AppSettings.Current.AmbientMode || string.IsNullOrEmpty(videoId)) return;

        if (_curId != videoId)
        {
            _curId = videoId;
            _lastSampleAt = 0;
            EnsureThumbPalette(videoId, isShort);
        }

        // Update target palette every 600ms from live storyboard frame (or thumbnail fallback)
        if (UI.Now - _lastSampleAt > 600)
        {
            _lastSampleAt = UI.Now;
            var pos = MpvPlayer.Instance.CurrentVideoId == videoId ? MpvPlayer.Instance.TimePos : 0;
            if (Storyboard.TrySampleColors(videoId, pos, out var tl, out var tr, out var bl, out var br, out var c))
                _target = EnhanceAll(new Palette(tl, tr, bl, br, c));
            else if (_thumbPalettes.TryGetValue(videoId, out var tp))
                _target = tp;
            else
                EnsureThumbPalette(videoId, isShort);
        }

        // Smooth cinema color easing
        float dt = (float)UI.Dt;
        float k = 1f - MathF.Exp(-dt * 2.4f);
        _cur = new Palette(
            Lerp(_cur.TL, _target.TL, k),
            Lerp(_cur.TR, _target.TR, k),
            Lerp(_cur.BL, _target.BL, k),
            Lerp(_cur.BR, _target.BR, k),
            Lerp(_cur.C, _target.C, k));

        float delta = Diff(_cur.TL, _target.TL) + Diff(_cur.TR, _target.TR) + Diff(_cur.BL, _target.BL) + Diff(_cur.BR, _target.BR);
        if (delta > 0.008f) UI.Animate();

        // Subtle organic breathing modulation (barely perceptible, feels alive)
        float breath = 1f + 0.04f * MathF.Sin(UI.Now / 2400f);
        float baseAlpha = Th.Light ? 0.14f : (isShort ? 0.34f : 0.28f);
        float spreadX = (isShort ? 150f : 130f) * breath;
        float spreadY = (isShort ? 130f : 120f) * breath;

        float rx = videoRect.W * 0.56f + spreadX;
        float ry = videoRect.H * 0.52f + spreadY;

        // 5 overlapping Gaussian radial light fields around the video edges
        Gfx.RadialGlow(videoRect.X + videoRect.W * 0.18f, videoRect.Y + videoRect.H * 0.18f, rx * 0.85f, ry * 0.85f, _cur.TL.R, _cur.TL.G, _cur.TL.B, baseAlpha);
        Gfx.RadialGlow(videoRect.X + videoRect.W * 0.82f, videoRect.Y + videoRect.H * 0.18f, rx * 0.85f, ry * 0.85f, _cur.TR.R, _cur.TR.G, _cur.TR.B, baseAlpha);
        Gfx.RadialGlow(videoRect.X + videoRect.W * 0.18f, videoRect.Y + videoRect.H * 0.85f, rx * 0.90f, ry * 0.90f, _cur.BL.R, _cur.BL.G, _cur.BL.B, baseAlpha * 1.05f);
        Gfx.RadialGlow(videoRect.X + videoRect.W * 0.82f, videoRect.Y + videoRect.H * 0.85f, rx * 0.90f, ry * 0.90f, _cur.BR.R, _cur.BR.G, _cur.BR.B, baseAlpha * 1.05f);
        Gfx.RadialGlow(videoRect.X + videoRect.W * 0.50f, videoRect.Y + videoRect.H * 0.50f, rx * 1.08f, ry * 1.04f, _cur.C.R, _cur.C.G, _cur.C.B, baseAlpha * 0.65f);
    }
}
