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
    [ThreadStatic] private static IWICImagingFactory? t_wic;

    private static string _lastKey = "";
    private static string _lastFile = "";
    private static int _lastW, _lastH, _slot;
    private static long _lastUse;
    private static double _lastSeconds;

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, MaxConnectionsPerServer = 4 };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { h.Proxy = new System.Net.WebProxy(AppSettings.Current.Proxy); h.UseProxy = true; }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", InnerTube.UserAgent);
        return c;
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
            _ = Task.Run(() => LoadSheet(videoId, book, sheet));
            return;
        }
        if (px == null) return; // on its way; the pointer will ask again
        if (!book.Size.TryGetValue(sheet, out var size)) return;
        var (sw, sh) = size;
        int sx = cell % book.Cols * book.TileW, sy = cell / book.Cols * book.TileH;
        if (sx + book.TileW > sw || sy + book.TileH > sh) return; // last sheet, missing cell

        int tw = (int)Math.Round(180 * Gfx.Scale / 2) * 2;
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
        _lastKey = ""; _lastFile = "";
    }

    /// <summary>Drops the unpacked sheets once the pointer has been away from the bar for a while.</summary>
    public static void Idle()
    {
        if (_lastFile.Length == 0 || Environment.TickCount64 - _lastUse < 20_000) return;
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
        _lastKey = ""; _lastFile = "";
    }

    /// <summary>spec: "baseUrl|W#H#frames#cols#rows#intervalMs#name#sigh|…" (one part per level, biggest last).</summary>
    private static Book? Parse(string spec)
    {
        try
        {
            var parts = spec.Split('|');
            if (parts.Length < 2) return null;
            int lvl = parts.Length - 2;
            var f = parts[1 + lvl].Split('#');
            if (f.Length < 8) return null;
            var url = parts[0].Replace("$L", lvl.ToString()).Replace("$N", f[6]) + "&sigh=" + f[7];
            return new Book
            {
                Url = url,
                TileW = int.Parse(f[0]), TileH = int.Parse(f[1]), Total = int.Parse(f[2]),
                Cols = int.Parse(f[3]), Rows = int.Parse(f[4]),
                Interval = Math.Max(0.5, int.Parse(f[5]) / 1000.0),
            };
        }
        catch (Exception e)
        {
            Paths.Log("storyboard spec: " + e.Message);
            return null;
        }
    }

    private static async Task LoadSheet(string videoId, Book book, int sheet)
    {
        try
        {
            var data = await _http.GetByteArrayAsync(book.Url.Replace("$M", sheet.ToString()));
            if (t_wic == null) W.CoInitializeEx(0, 0);
            var wic = t_wic ??= new IWICImagingFactory();
            using var stream = wic.CreateStream(data);
            using var decoder = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            var size = frame.Size;
            using var conv = wic.CreateFormatConverter();
            conv.Initialize(frame, PixelFormat.Format32bppBGRA);
            var px = Copy(conv, size.Width, size.Height);
            lock (book.Order)
            {
                book.Size[sheet] = (size.Width, size.Height); // before the pixels: a reader that sees the sheet finds its size
                book.Sheets[sheet] = px;
                book.Order.Add(sheet);
                while (book.Order.Count > 4)
                {
                    var old = book.Order[0];
                    book.Order.RemoveAt(0);
                    book.Sheets.TryRemove(old, out _);
                    book.Size.TryRemove(old, out _);
                }
            }
            App.Post(() => Request(videoId, _lastSeconds)); // the sheet the pointer was waiting for is here
            App.WakeUi();
        }
        catch (Exception e)
        {
            book.Sheets.TryRemove(sheet, out _);
            Paths.Log($"storyboard sheet {sheet}: {e.Message}");
        }
    }

    private static unsafe byte[] Copy(IWICBitmapSource src, int w, int h)
    {
        var px = new byte[w * h * 4];
        fixed (byte* p = px) src.CopyPixels(new RectI(0, 0, w, h), (uint)(w * 4), (uint)px.Length, (nint)p);
        return px;
    }

    /// <summary>Bilinear copy of one tile out of the sheet into an opaque BGRA image.</summary>
    private static void Resample(byte[] src, int srcW, int sx, int sy, int tileW, int tileH, byte[] dst, int w, int h)
    {
        int stride = srcW * 4;
        for (int y = 0; y < h; y++)
        {
            float fy = (y + 0.5f) * tileH / h - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, tileH - 1), y1 = Math.Min(y0 + 1, tileH - 1);
            float wy = Math.Clamp(fy - y0, 0, 1);
            int row0 = (sy + y0) * stride, row1 = (sy + y1) * stride, drow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) * tileW / w - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, tileW - 1), x1 = Math.Min(x0 + 1, tileW - 1);
                float wx = Math.Clamp(fx - x0, 0, 1);
                int a = row0 + (sx + x0) * 4, b = row0 + (sx + x1) * 4, c = row1 + (sx + x0) * 4, d = row1 + (sx + x1) * 4;
                int o = drow + x * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = src[a + ch] + (src[b + ch] - src[a + ch]) * wx;
                    float bot = src[c + ch] + (src[d + ch] - src[c + ch]) * wx;
                    dst[o + ch] = (byte)(top + (bot - top) * wy + 0.5f);
                }
                dst[o + 3] = 255;
            }
        }
    }
}
