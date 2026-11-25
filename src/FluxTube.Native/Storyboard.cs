using System.Collections.Concurrent;
using FluxTube.Player;
using FluxTube.Services;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FluxTube;

/// <summary>
/// Frame previews on the seek bar. YouTube ships them ready: a few dozen frames packed into one small JPEG
/// ("storyboard"), one sheet per 25 frames, about 60 KB each. We take the sheet the player response points at,
/// cut the frame under the pointer, scale it and hand the raw pixels to mpv, which draws it over the video.
/// Nothing is decoded twice: a sheet stays unpacked (~1.5 MB) while the pointer is on the bar.
/// </summary>
public static class Storyboard
{
    private sealed class Book
    {
        public required string Url;     // with $M for the sheet number
        public int TileW, TileH, Cols, Rows, Total;
        public double Interval = 2;
        public readonly ConcurrentDictionary<int, byte[]?> Sheets = new(); // decoded BGRA, null while loading
        public readonly ConcurrentDictionary<int, (int W, int H)> Size = new();
        public readonly List<int> Order = [];
    }

    private static readonly ConcurrentDictionary<string, Book?> _books = new();
    private static readonly HttpClient _http = MakeClient();
    private static readonly byte[] FailedSheet = []; // negative cache sentinel to avoid HTTP retry storms

    private static string _lastKey = "";
    private static string _lastFile = "";
    private static int _lastW, _lastH, _slot;
    private static long _lastUse;
    private static double _lastSeconds;
    private static volatile bool _hasSheets;

    // Single reusable GPU sheet bitmap for YouTube-style hover preview on Home/Feed cards
    private static Vortice.Direct2D1.ID2D1Bitmap1? _cardBmp;
    private static string _cardKey = "";
    private static int _cardBmpGen;
    private static long _cardNextFrameAt;

    static Storyboard()
    {
        Gfx.DeviceLost += DropCardBitmap;
    }

    private static void DropCardBitmap()
    {
        _cardBmp?.Dispose();
        _cardBmp = null;
        _cardKey = "";
    }

    /// <summary>
    /// Draws an animated storyboard preview inside a hovered video card thumbnail (like YouTube Home hover preview).
    /// Returns true once frames are actively drawing.
    /// </summary>
    public static unsafe bool TryDrawCardPreview(string videoId, R dest, float radius, uint behind, double hoverSec)
    {
        if (string.IsNullOrEmpty(videoId) || hoverSec < 0.35) return false;
        _lastUse = Environment.TickCount64;
        var book = BookFor(videoId);
        if (book == null || book.Total <= 0 || book.TileW < 120) return false;

        int per = Math.Max(1, book.Cols * book.Rows);
        int maxFrames = Math.Min(book.Total, per * 2); // cycle through up to 2 sheets for rich variety
        int step = (int)((hoverSec - 0.35) * 2.4);
        int idx = maxFrames > 0 ? step % maxFrames : 0;
        int sheet = idx / per, cell = idx % per;

        if (!book.Sheets.TryGetValue(sheet, out var px))
        {
            book.Sheets[sheet] = null;
            _ = Task.Run(() => LoadSheet(videoId, book, sheet, notifySeekBar: false));
            // Fall back to sheet 0 if already loaded so playback never stalls
            if (sheet > 0 && book.Sheets.TryGetValue(0, out var px0) && px0 is { Length: > 0 })
            {
                sheet = 0; cell = idx % per; px = px0;
            }
            else return false;
        }
        if (px == null || px.Length == 0 || !book.Size.TryGetValue(sheet, out var size)) return false;
        var (sw, sh) = size;
        int sx = cell % book.Cols * book.TileW, sy = cell / book.Cols * book.TileH;
        if (sx + book.TileW > sw || sy + book.TileH > sh) return false;

        var key = videoId + "#" + sheet;
        if (_cardKey != key || _cardBmp == null || _cardBmpGen != Gfx.DeviceGeneration)
        {
            _cardBmp?.Dispose();
            fixed (byte* p = px) _cardBmp = Gfx.CreateBitmap(p, sw, sh, bc1: false);
            _cardKey = key;
            _cardBmpGen = Gfx.DeviceGeneration;
        }

        float fade = Math.Clamp((float)((hoverSec - 0.35) / 0.22), 0f, 1f);
        Gfx.ImageRegion(_cardBmp, dest, new R(sx, sy, book.TileW, book.TileH), fade);
        Gfx.RoundOff(dest, radius, behind);

        // Sleek bottom preview progress bar + live timestamp pill
        double progress = maxFrames > 1 ? (double)idx / (maxFrames - 1) : 0;
        UI.Progress(new R(dest.X + radius * 0.7f, dest.Bottom - 4, dest.W - radius * 1.4f, 3), progress, Th.A(0.32f));
        var ts = Fmt.Time(idx * book.Interval);
        var tr = Gfx.Text(ts, 11.5f, 600);
        var badgeR = new R(dest.Right - tr.W - 15, dest.Bottom - tr.H - 10, tr.W + 9, tr.H + 3);
        Gfx.Fill(badgeR, Gfx.Fade(0xFF0A0A0E, 0.86f * fade), 4);
        Gfx.Draw(tr, badgeR.X + 4.5f, badgeR.Y + 1, Gfx.Fade(0xFFFFFFFF, fade));

        // 60 FPS only during the 220ms fade-in; after that, wake at the 2.4 FPS frame cadence instead of spinning!
        if (fade < 1f) UI.Animate();
        else if (UI.Now >= _cardNextFrameAt)
        {
            _cardNextFrameAt = UI.Now + 210;
            App.After(210, App.Invalidate);
        }
        return true;
    }

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, MaxConnectionsPerServer = 4 };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { h.Proxy = new System.Net.WebProxy(AppSettings.Current.Proxy); h.UseProxy = true; }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", InnerTube.UserAgent);
        return c;
    }

    /// <summary>Samples 5 smooth RGB zone colors (TL, TR, BL, BR, Center) from the storyboard frame at this timestamp for Ambient Mode.</summary>
    public static bool TrySampleColors(string videoId, double seconds,
        out (float R, float G, float B) tl, out (float R, float G, float B) tr,
        out (float R, float G, float B) bl, out (float R, float G, float B) br,
        out (float R, float G, float B) center)
    {
        tl = tr = bl = br = center = default;
        if (string.IsNullOrEmpty(videoId)) return false;
        _lastUse = Environment.TickCount64;
        var book = BookFor(videoId);
        if (book == null) return false;
        int idx = Math.Clamp((int)(seconds / book.Interval), 0, Math.Max(0, book.Total - 1));
        int per = Math.Max(1, book.Cols * book.Rows), sheet = idx / per, cell = idx % per;
        if (!book.Sheets.TryGetValue(sheet, out var px))
        {
            book.Sheets[sheet] = null;
            _ = Task.Run(() => LoadSheet(videoId, book, sheet, notifySeekBar: false));
            return false;
        }
        if (px == null || px.Length == 0 || !book.Size.TryGetValue(sheet, out var size)) return false;
        var (sw, sh) = size;
        int sx = cell % book.Cols * book.TileW, sy = cell / book.Cols * book.TileH;
        if (sx + book.TileW > sw || sy + book.TileH > sh) return false;

        tl = AvgRegion(px, sw, sx, sy, book.TileW / 2, book.TileH / 2);
        tr = AvgRegion(px, sw, sx + book.TileW / 2, sy, book.TileW / 2, book.TileH / 2);
        bl = AvgRegion(px, sw, sx, sy + book.TileH / 2, book.TileW / 2, book.TileH / 2);
        br = AvgRegion(px, sw, sx + book.TileW / 2, sy + book.TileH / 2, book.TileW / 2, book.TileH / 2);
        center = AvgRegion(px, sw, sx + book.TileW / 4, sy + book.TileH / 4, book.TileW / 2, book.TileH / 2);
        return true;
    }

    private static (float R, float G, float B) AvgRegion(byte[] px, int strideW, int x0, int y0, int w, int h)
    {
        int stepX = Math.Max(1, w / 6), stepY = Math.Max(1, h / 4);
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int y = y0; y < y0 + h; y += stepY)
        {
            int row = y * strideW * 4;
            for (int x = x0; x < x0 + w; x += stepX)
            {
                int o = row + x * 4;
                sb += px[o]; sg += px[o + 1]; sr += px[o + 2];
                n++;
            }
        }
        if (n == 0) return (0.2f, 0.2f, 0.25f);
        return ((float)(sr / (n * 255.0)), (float)(sg / (n * 255.0)), (float)(sb / (n * 255.0)));
    }

    /// <summary>The player script asks for the frame at this moment; the answer comes back as a script message.</summary>
    public static void Request(string? videoId, double seconds)
    {
        if (string.IsNullOrEmpty(videoId)) { Send(null, 0, 0); return; }
        _lastUse = Environment.TickCount64;
        _lastSeconds = seconds;
        var book = BookFor(videoId);
        if (book == null) { Send(null, 0, 0); return; }

        int idx = Math.Clamp((int)(seconds / book.Interval), 0, Math.Max(0, book.Total - 1));
        var key = videoId + "#" + idx;
        if (key == _lastKey && _lastFile.Length > 0) { Send(_lastFile, _lastW, _lastH); return; }

        int per = Math.Max(1, book.Cols * book.Rows), sheet = idx / per, cell = idx % per;
        if (!book.Sheets.TryGetValue(sheet, out var px))
        {
            book.Sheets[sheet] = null;
            _ = Task.Run(() => LoadSheet(videoId, book, sheet, notifySeekBar: true));
            return;
        }
        if (px == null || px.Length == 0) return; // on its way or permanently unavailable
        if (!book.Size.TryGetValue(sheet, out var size)) return;
        var (sw, sh) = size;
        int sx = cell % book.Cols * book.TileW, sy = cell / book.Cols * book.TileH;
        if (sx + book.TileW > sw || sy + book.TileH > sh) return; // last sheet, missing cell

        int tw = (int)Math.Round(240 * Gfx.Scale / 2) * 2;
        int th = (int)Math.Round((double)tw * book.TileH / book.TileW / 2) * 2;
        var dst = new byte[tw * th * 4];
        Resample(px, sw, sx, sy, book.TileW, book.TileH, dst, tw, th);

        // mpv keeps the picture file mapped while it shows it: rotate through several files, so the one being
        // written is never the one on screen (writing over it would flash black)
        _slot = (_slot + 1) % 8;
        var file = Path.Combine(Paths.Data, "preview" + _slot + ".bgra");
        try { File.WriteAllBytes(file, dst); }
        catch (Exception e) { Paths.Log("preview write: " + e.Message); return; }
        if (_lastFile.Length == 0) Paths.Log($"preview ready: {tw}x{th} px");
        _lastKey = key; _lastFile = file; _lastW = tw; _lastH = th;
        Send(file, tw, th);
    }

    private static void Send(string? file, int w, int h)
        => MpvPlayer.Instance.Command("script-message-to", "fluxosc", "flux-thumb", file ?? "none", w.ToString(), h.ToString());

    private static Book? BookFor(string videoId)
    {
        if (_books.TryGetValue(videoId, out var b)) return b;
        var spec = QuickStream.StoryboardSpec(videoId);
        if (spec == null)
        {
            // nobody asked YouTube for this video's player data yet (it came from the disk cache): ask now
            if (!QuickStream.SpecKnown(videoId)) _ = QuickStream.EnsureSpecAsync(videoId).ContinueWith(_ => App.WakeUi());
            else _books[videoId] = null; // asked, there are none
            return null;
        }
        var book = Parse(spec);
        Paths.Log(book == null ? $"storyboard {videoId}: spec not understood"
                               : $"storyboard {videoId}: {book.Total} frames, tile {book.TileW}x{book.TileH}, every {book.Interval}s");
        _books[videoId] = book;
        if (_books.Count > 12) Trim(videoId);
        return book;
    }

    /// <summary>Keeps only the video being watched: the unpacked sheets are the only real memory here.</summary>
    private static void Trim(string keep)
    {
        foreach (var k in _books.Keys.Where(k => k != keep).ToList()) _books.TryRemove(k, out _);
    }

    public static void Forget()
    {
        _books.Clear();
        _hasSheets = false;
        DropCardBitmap();
        _lastKey = ""; _lastFile = "";
    }

    /// <summary>Drops the unpacked sheets and card preview GPU bitmap once idle for 20s.</summary>
    public static void Idle()
    {
        if ((!_hasSheets && _lastFile.Length == 0 && _cardBmp == null) || Environment.TickCount64 - _lastUse < 20_000) return;
        foreach (var b in _books.Values)
        {
            if (b == null) continue;
            lock (b.Order)
            {
                b.Sheets.Clear();
                b.Size.Clear();
                b.Order.Clear();
            }
        }
        _hasSheets = false;
        DropCardBitmap();
        _lastKey = ""; _lastFile = "";
    }

    /// <summary>spec: "baseUrl|W#H#frames#cols#rows#intervalMs#name#sigh|…" — picks the highest-resolution valid level.</summary>
    private static Book? Parse(string spec)
    {
        try
        {
            var parts = spec.Split('|');
            if (parts.Length < 2) return null;
            Book? best = null;
            int bestArea = 0;
            for (int lvl = 0; lvl < parts.Length - 1; lvl++)
            {
                var f = parts[1 + lvl].Split('#');
                if (f.Length < 8) continue;
                if (!int.TryParse(f[0], out int w) || !int.TryParse(f[1], out int h)
                    || !int.TryParse(f[2], out int total) || !int.TryParse(f[3], out int cols)
                    || !int.TryParse(f[4], out int rows) || !int.TryParse(f[5], out int intervalMs))
                    continue;
                if (w <= 0 || h <= 0 || total <= 0 || cols <= 0 || rows <= 0) continue;
                int area = w * h;
                if (area >= bestArea)
                {
                    bestArea = area;
                    var url = parts[0].Replace("$L", lvl.ToString()).Replace("$N", f[6]) + "&sigh=" + f[7];
                    best = new Book
                    {
                        Url = url,
                        TileW = w, TileH = h, Total = total, Cols = cols, Rows = rows,
                        Interval = intervalMs > 0 ? Math.Max(0.5, intervalMs / 1000.0) : 2.0,
                    };
                }
            }
            return best;
        }
        catch (Exception e)
        {
            Paths.Log("storyboard spec: " + e.Message);
            return null;
        }
    }

    private static async Task LoadSheet(string videoId, Book book, int sheet, bool notifySeekBar)
    {
        try
        {
            var data = await _http.GetByteArrayAsync(book.Url.Replace("$M", sheet.ToString()));
            var wic = Wic.Factory;
            using var stream = wic.CreateStream(data);
            using var decoder = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            var size = frame.Size;
            using var conv = wic.CreateFormatConverter();
            conv.Initialize(frame, PixelFormat.Format32bppBGRA);
            var px = Copy(conv, size.Width, size.Height);
            SharpenMild(px, size.Width, size.Height);
            lock (book.Order)
            {
                book.Size[sheet] = (size.Width, size.Height); // before the pixels: a reader that sees the sheet finds its size
                book.Sheets[sheet] = px;
                book.Order.Add(sheet);
                while (book.Order.Count > 3)
                {
                    var old = book.Order[0];
                    book.Order.RemoveAt(0);
                    book.Sheets.TryRemove(old, out _);
                    book.Size.TryRemove(old, out _);
                }
            }
            _hasSheets = true;
            if (notifySeekBar) App.Post(() => { if (MpvPlayer.Instance.CurrentVideoId == videoId) Request(videoId, _lastSeconds); });
            App.WakeUi();
        }
        catch (Exception e)
        {
            book.Sheets[sheet] = FailedSheet; // negative-cache so we never storm HTTP at 60 req/s
            Paths.Log($"storyboard sheet {sheet}: {e.Message}");
        }
    }

    private static unsafe byte[] Copy(IWICBitmapSource src, int w, int h)
    {
        var px = new byte[w * h * 4];
        fixed (byte* p = px) src.CopyPixels(new RectI(0, 0, w, h), (uint)(w * 4), (uint)px.Length, (nint)p);
        return px;
    }

    /// <summary>Gentle 3x3 unsharp mask applied once off-thread on decoded storyboard sheets so hover previews stay crisp.</summary>
    private static void SharpenMild(byte[] px, int w, int h)
    {
        if (w < 3 || h < 3) return;
        var src = (byte[])px.Clone();
        int stride = w * 4;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * stride;
            for (int x = 1; x < w - 1; x++)
            {
                int o = row + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int center = src[o + c];
                    int avg = (src[o - 4 + c] + src[o + 4 + c] + src[o - stride + c] + src[o + stride + c]) >> 2;
                    int v = center + ((center - avg) * 3 >> 3); // ~0.375 sharpen strength
                    px[o + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }
    }

    /// <summary>Catmull-Rom bicubic copy of one tile out of the sheet into a crisp opaque BGRA image.</summary>
    private static void Resample(byte[] src, int srcW, int sx, int sy, int tileW, int tileH, byte[] dst, int w, int h)
    {
        static float Cubic(float t)
        {
            t = MathF.Abs(t);
            if (t < 1f) return 1.5f * t * t * t - 2.5f * t * t + 1f;
            if (t < 2f) return -0.5f * t * t * t + 2.5f * t * t - 4f * t + 2f;
            return 0f;
        }

        int stride = srcW * 4;
        Span<int> ix = stackalloc int[4];
        Span<float> wx = stackalloc float[4];
        Span<int> iy = stackalloc int[4];
        Span<float> wy = stackalloc float[4];
        for (int y = 0; y < h; y++)
        {
            float fy = (y + 0.5f) * tileH / h - 0.5f;
            int yBase = (int)MathF.Floor(fy);
            float ty = fy - yBase;
            for (int k = 0; k < 4; k++)
            {
                iy[k] = (sy + Math.Clamp(yBase + k - 1, 0, tileH - 1)) * stride;
                wy[k] = Cubic(ty - (k - 1));
            }
            int drow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) * tileW / w - 0.5f;
                int xBase = (int)MathF.Floor(fx);
                float tx = fx - xBase;
                for (int k = 0; k < 4; k++)
                {
                    ix[k] = (sx + Math.Clamp(xBase + k - 1, 0, tileW - 1)) * 4;
                    wx[k] = Cubic(tx - (k - 1));
                }
                float b = 0, g = 0, r = 0;
                for (int j = 0; j < 4; j++)
                {
                    float wj = wy[j];
                    int rowOff = iy[j];
                    for (int i = 0; i < 4; i++)
                    {
                        float ww = wj * wx[i];
                        int o = rowOff + ix[i];
                        b += src[o] * ww;
                        g += src[o + 1] * ww;
                        r += src[o + 2] * ww;
                    }
                }
                int dstOff = drow + x * 4;
                dst[dstOff] = (byte)Math.Clamp((int)(b + 0.5f), 0, 255);
                dst[dstOff + 1] = (byte)Math.Clamp((int)(g + 0.5f), 0, 255);
                dst[dstOff + 2] = (byte)Math.Clamp((int)(r + 0.5f), 0, 255);
                dst[dstOff + 3] = 255;
            }
        }
    }
}
