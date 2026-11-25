using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Runtime.InteropServices;
using FluxTube.Services;
using Microsoft.Win32.SafeHandles;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FluxTube;

/// <summary>
/// Every thumbnail the app has ever shown, in one memory-mapped file, already in the form the GPU takes:
/// BC1 blocks at display size (avatars: raw pixels with the circle baked in). Showing one again is a lookup and an upload
/// straight from the mapped pages: no file open, no JPEG decode, no managed memory. The file is sparse, so it takes
/// the space of what is in it, and when it fills up it simply starts over.
/// </summary>
public static unsafe class ThumbPack
{
    public const byte Bgra = 0, Bc1Blocks = 1;
    private const long Capacity = 512L * 1024 * 1024;
    private const uint Magic = 0x4B504C46; // "FLPK"

    public readonly record struct Entry(long Offset, int W, int H, byte Format, int Bytes);

    private static readonly Dictionary<ulong, Entry> _index = new();
    private static readonly object _lock = new();
    private static readonly object _saveLock = new();
    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static byte* _base;
    private static long _end;
    private static volatile int _packGen;
    private static bool _dirty, _ok;
    private static Timer? _timer;
    private static string PackFile => Path.Combine(Paths.Data, "thumbs.pack");
    private static string IndexFile => Path.Combine(Paths.Data, "thumbs.idx");

    public static long Bytes => _end;
    public static int PackGeneration => _packGen;
    public static int Count { get { lock (_lock) return _index.Count; } }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, nint inBuf, uint inSize, nint outBuf, uint outSize, out uint returned, nint overlapped);

    public static void Open()
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(PackFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fs.Length != Capacity)
            {
                DeviceIoControl(fs.SafeFileHandle, 0x900C4 /*FSCTL_SET_SPARSE*/, 0, 0, 0, 0, out _, 0);
                fs.SetLength(Capacity);
            }
            _mmf = MemoryMappedFile.CreateFromFile(fs, null, Capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            fs = null; // owned by _mmf now
            _view = _mmf.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p;
            LoadIndex();
            _ok = true;
            _timer = new Timer(_ => SaveIndex(), null, 20_000, 20_000);
        }
        catch (Exception e) { fs?.Dispose(); Paths.Log("thumb pack: " + e.Message); }
    }

    public static void Close()
    {
        _timer?.Dispose();
        _timer = null;
        SaveIndex();
    }

    private static void LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexFile)) return;
            using var r = new BinaryReader(File.OpenRead(IndexFile));
            if (r.ReadUInt32() != Magic) return;
            var end = r.ReadInt64();
            int n = r.ReadInt32();
            if (end < 0 || end > Capacity || n < 0 || n > 500_000) { _end = 0; return; }
            _end = end;
            for (int i = 0; i < n; i++)
            {
                var key = r.ReadUInt64();
                var e = new Entry(r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), r.ReadByte(), r.ReadInt32());
                if (e.W > 0 && e.H > 0 && e.Bytes > 0 && e.Offset >= 0 && e.Offset + e.Bytes <= _end) _index[key] = e;
            }
        }
        catch (Exception e)
        {
            Paths.Log("thumb index: " + e.Message);
            _index.Clear();
            _end = 0;
        }
    }

    public static void SaveIndex()
    {
        if (!_dirty || !_ok) return;
        lock (_saveLock)
        {
            if (!_dirty || !_ok) return;
            try
            {
                KeyValuePair<ulong, Entry>[] snap;
                long end;
                lock (_lock) { snap = [.. _index]; end = _end; _dirty = false; }
                var tmp = IndexFile + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(Magic); w.Write(end); w.Write(snap.Length);
                    foreach (var (k, e) in snap) { w.Write(k); w.Write(e.Offset); w.Write(e.W); w.Write(e.H); w.Write(e.Format); w.Write(e.Bytes); }
                }
                File.Move(tmp, IndexFile, overwrite: true);
            }
            catch (Exception e) { Paths.Log("thumb index save: " + e.Message); }
        }
    }

    public static bool TryGet(ulong key, out Entry e, out byte* data, out int gen)
    {
        data = null; gen = _packGen;
        if (!_ok) { e = default; return false; }
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out e)) return false;
            gen = _packGen;
        }
        data = _base + e.Offset;
        return true;
    }

    public static bool Add(ulong key, ReadOnlySpan<byte> bytes, int w, int h, byte format, out Entry e, out byte* data, out int gen)
    {
        e = default; data = null; gen = 0;
        if (!_ok || bytes.Length <= 0 || bytes.Length > Capacity) return false;
        long off;
        lock (_lock)
        {
            if (_end + bytes.Length > Capacity)
            {
                // full: start over. Bump pack generation so any stale Img pointers are discarded cleanly.
                _index.Clear();
                _end = 0;
                _packGen++;
            }
            off = _end;
            gen = _packGen;
            _end += (bytes.Length + 15) & ~15;
            e = new Entry(off, w, h, format, bytes.Length);
        }
        // Write pixels into our exclusively reserved slice BEFORE publishing into _index (no uninitialized-memory race!)
        data = _base + off;
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        lock (_lock)
        {
            if (gen != _packGen) return false; // wrapped while copying
            _index[key] = e;
            _dirty = true;
        }
        return true;
    }

    /// <summary>Stable across runs (string.GetHashCode is not).</summary>
    public static ulong Key(string url, int w, int h, bool round)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var c in url) { hash ^= c; hash *= 1099511628211UL; }
        hash ^= (ulong)w; hash *= 1099511628211UL;
        hash ^= (ulong)h; hash *= 1099511628211UL;
        hash ^= round ? 0x9E37UL : 0; hash *= 1099511628211UL;
        return hash;
    }
}

public sealed unsafe class Img
{
    public ID2D1Bitmap1? Bmp;
    public byte* Data;          // in the pack, waiting for upload
    public int W, H, Bytes, GpuBytes, PackGen;
    public byte Format;
    public int State;           // 0 queued, 1 in the pack, 2 on GPU, 3 failed, 4 dropped from the queue, 5 warming
    public long WantedFrame;    // last frame something asked to draw it
    public long ReadyTick, FailedAt;
    public int FailCount;
    public int Generation;
    public bool Warm;           // came from the pack: no fade-in
    public required Images.IKey Key;
    public required string Url;
    public bool Round;
}

/// <summary>Shared thread-safe per-thread WIC factory so Images, Ambient, and Storyboard never allocate 3 separate COM factories per ThreadPool thread.</summary>
internal static class Wic
{
    [ThreadStatic] private static IWICImagingFactory? t_wic;
    public static IWICImagingFactory Factory
    {
        get
        {
            if (t_wic == null)
            {
                W.CoInitializeEx(0, 0);
                t_wic = new IWICImagingFactory();
            }
            return t_wic;
        }
    }
}

/// <summary>
/// Thumbnail pipeline: never block the frame, anything seen before appears in the very frame it is asked for (see ThumbPack),
/// new ones are fetched newest-request-first, converted off-thread, uploaded a bounded number per frame, and video memory is bounded.
/// </summary>
public static class Images
{
    private const long GpuBudget = 28L * 1024 * 1024; // BC1: ~420 thumbnails on GPU, instant restore from ThumbPack MMF
    private const int MaxParallel = 6;
    private static readonly List<Img> _evictBuf = new(256);
    private static readonly List<IKey> _staleKeyBuf = new(256);

    public readonly record struct IKey(string Url, int W, int H, bool Round);
    private static readonly Dictionary<IKey, Img> _map = new();
    private static readonly ConcurrentStack<Img> _queue = new();
    private static readonly ConcurrentQueue<Img> _ready = new();
    private static int _running;
    private static long _gpuBytes;
    private static int _syncThisFrame;
    private static long _syncFrame;
    private static bool _bc1Works = true;
    private static HttpClient _http = MakeClient();
    private static string _httpProxy = AppSettings.Current.Proxy ?? "";

    static Images()
    {
        Gfx.DeviceLost += OnDeviceLost;
    }

    private static HttpClient Client()
    {
        var cur = AppSettings.Current.Proxy ?? "";
        if (cur != _httpProxy)
        {
            _httpProxy = cur;
            _http = MakeClient();
        }
        return _http;
    }

    public static unsafe void OnDeviceLost()
    {
        foreach (var img in _map.Values)
        {
            if (img.Bmp != null) { img.Bmp.Dispose(); img.Bmp = null; img.GpuBytes = 0; }
            if (Volatile.Read(ref img.State) == 2) Volatile.Write(ref img.State, img.PackGen == ThumbPack.PackGeneration && img.Data != null ? 1 : 4);
        }
        _gpuBytes = 0;
    }

    public static long GpuBytes => _gpuBytes;
    public static int Count => _map.Count;
    public static int Uploaded;

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 12,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(4),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
        };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { h.Proxy = new WebProxy(AppSettings.Current.Proxy); h.UseProxy = true; }
        var c = new HttpClient(h)
        {
            Timeout = TimeSpan.FromSeconds(12),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", InnerTube.UserAgent);
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/jpeg,image/png,image/*;q=0.8");
        return c;
    }

    /// <summary>Bitmap for url at w x h DIPs, or null while it loads. Call every frame it is visible.</summary>
    public static unsafe Img? Get(string? url, float wDip, float hDip, bool round = false)
    {
        if (string.IsNullOrEmpty(url) || wDip <= 0.5f || hDip <= 0.5f) return null;
        // quantised so a window resize does not re-convert on every pixel; multiples of 4 for the block format
        int w = Math.Max(16, ((int)MathF.Ceiling(wDip * Gfx.Scale) + 15) / 16 * 16);
        int h = Math.Max(4, ((int)MathF.Round(w * hDip / wDip) + 3) / 4 * 4);
        var key = new IKey(url, w, h, round);
        if (!_map.TryGetValue(key, out var img))
        {
            img = new Img { Key = key, Url = url, W = w, H = h, Round = round, Generation = Gfx.DeviceGeneration };
            _map[key] = img;
            if (ThumbPack.TryGet(ThumbPack.Key(url, w, h, round), out var e, out var data, out var gen) && e.W == w && e.H == h)
            {
                img.Data = data; img.Format = e.Format; img.Bytes = e.Bytes; img.PackGen = gen; img.Warm = true;
                Volatile.Write(ref img.State, 5);
                // the pack is a memory-mapped file: its pages may still be on disk, and reading them here would
                // stall the frame for as long as the disk takes. A worker touches them; the upload follows next frame.
                ThreadPool.UnsafeQueueUserWorkItem(static i => Warm(i), img, preferLocal: false);
            }
            else Enqueue(img);
        }
        else
        {
            int st = Volatile.Read(ref img.State);
            if (st == 2 && img.Generation != Gfx.DeviceGeneration)
            {
                img.Bmp?.Dispose(); img.Bmp = null;
                _gpuBytes = Math.Max(0, _gpuBytes - img.GpuBytes); img.GpuBytes = 0;
                if (img.Data != null && img.PackGen == ThumbPack.PackGeneration)
                {
                    Volatile.Write(ref img.State, 1);
                    TryUploadNow(img);
                }
                else
                {
                    Volatile.Write(ref img.State, 0);
                    Enqueue(img);
                }
            }
            else if (st == 1) TryUploadNow(img);
            else if (st == 4 || (st == 3 && UI.Now - img.FailedAt > (img.FailCount <= 1 ? 3_500L : img.FailCount == 2 ? 10_000L : 25_000L)))
            {
                Volatile.Write(ref img.State, 0);
                Enqueue(img);
            }
        }
        Volatile.Write(ref img.WantedFrame, Gfx.Frame);
        return Volatile.Read(ref img.State) == 2 ? img : null;
    }

    private static unsafe void Warm(Img img)
    {
        if (img.PackGen != ThumbPack.PackGeneration || img.Data == null)
        {
            Volatile.Write(ref img.State, 4);
            return;
        }
        byte sum = 0;
        for (int i = 0; i < img.Bytes; i += 4096) sum ^= img.Data[i];
        if (img.Bytes > 0) sum ^= img.Data[img.Bytes - 1];
        GC.KeepAlive(sum);
        Volatile.Write(ref img.State, 1);
        _ready.Enqueue(img);
        App.WakeUi();
    }

    /// <summary>Known images go to the GPU in the frame that asks for them, a bounded number per frame.</summary>
    private static void TryUploadNow(Img img)
    {
        if (_syncFrame != Gfx.Frame) { _syncFrame = Gfx.Frame; _syncThisFrame = 0; }
        if (_syncThisFrame >= 8) { UI.Animate(); return; }
        _syncThisFrame++;
        Upload(img, fade: false);
    }

    private static unsafe void Upload(Img img, bool fade)
    {
        if (img.Data == null || img.PackGen != ThumbPack.PackGeneration)
        {
            Volatile.Write(ref img.State, 4);
            return;
        }
        try
        {
            if (img.Bmp != null) { img.Bmp.Dispose(); img.Bmp = null; _gpuBytes = Math.Max(0, _gpuBytes - img.GpuBytes); img.GpuBytes = 0; }
            if (img.Format == ThumbPack.Bc1Blocks && !_bc1Works)
            {
                // this GPU path refuses block-compressed bitmaps: expand in software (still no JPEG decode)
                int size = img.W * img.H * 4;
                var px = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    Bc1.Decode(new ReadOnlySpan<byte>(img.Data, img.Bytes), img.W, img.H, px);
                    fixed (byte* p = px) img.Bmp = Gfx.CreateBitmap(p, img.W, img.H, bc1: false);
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(px); }
                img.GpuBytes = size;
                _gpuBytes += size;
            }
            else
            {
                img.Bmp = Gfx.CreateBitmap(img.Data, img.W, img.H, img.Format == ThumbPack.Bc1Blocks);
                img.GpuBytes = img.Bytes;
                _gpuBytes += img.Bytes;
            }
            img.Generation = Gfx.DeviceGeneration;
            img.ReadyTick = fade ? Environment.TickCount64 : 0;
            Volatile.Write(ref img.State, 2);
            Uploaded++;
        }
        catch (Exception e)
        {
            if (img.Format == ThumbPack.Bc1Blocks && _bc1Works)
            {
                _bc1Works = false;
                Paths.Log("block-compressed bitmaps not accepted, expanding in software: " + e.Message);
                Upload(img, fade);
            }
            else
            {
                img.FailedAt = Environment.TickCount64;
                Volatile.Write(ref img.State, 3);
            }
        }
    }

    /// <summary>0..1 fade-in for an image that just arrived from the network; asks for another frame while it runs.</summary>
    public static float Alpha(Img img)
    {
        if (img.ReadyTick == 0) return 1;
        var t = (Environment.TickCount64 - img.ReadyTick) / 160f;
        if (t >= 1) { img.ReadyTick = 0; return 1; }
        UI.Animate();
        return MathF.Max(0.05f, t * (2 - t));
    }

    private static void Enqueue(Img img)
    {
        _queue.Push(img);
        if (Interlocked.Increment(ref _running) <= MaxParallel) _ = Task.Run(Worker);
        else Interlocked.Decrement(ref _running);
    }

    private static List<string> BuildCandidates(string url)
    {
        var list = new List<string>(4) { url };
        // Handle YouTube thumbnail fallback chain (including ?sqp=... query suffixes and vi_webp/*.webp!)
        if (url.Contains("ytimg.com/", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(url, @"/vi(?:_webp)?/([A-Za-z0-9_-]{11})/([^/?]+)");
            if (m.Success)
            {
                string id = m.Groups[1].Value;
                string file = m.Groups[2].Value.ToLowerInvariant();
                if (file != "hq720.jpg" && (file.Contains("maxres") || file.EndsWith(".webp")))
                    list.Add($"https://i.ytimg.com/vi/{id}/hq720.jpg");
                if (file != "hqdefault.jpg")
                    list.Add($"https://i.ytimg.com/vi/{id}/hqdefault.jpg");
                if (file != "mqdefault.jpg")
                    list.Add($"https://i.ytimg.com/vi/{id}/mqdefault.jpg");
            }
        }
        return list;
    }

    private static async Task Worker()
    {
        try
        {
            while (_queue.TryPop(out var img))
            {
                // scrolled past long ago: skip, it is re-queued if it ever becomes visible again
                if (Gfx.Frame - Volatile.Read(ref img.WantedFrame) > 40 && _queue.Count > 8) { Volatile.Write(ref img.State, 4); continue; }
                try
                {
                    if (img.Url.StartsWith("file:", StringComparison.Ordinal))
                    {
                        var data = await File.ReadAllBytesAsync(new Uri(img.Url).LocalPath);
                        Convert(data, img);
                    }
                    else
                    {
                        var candidates = BuildCandidates(img.Url);
                        Exception? lastErr = null;
                        bool ok = false;
                        var http = Client();
                        for (int ci = 0; ci < candidates.Count && !ok; ci++)
                        {
                            var u = candidates[ci];
                            for (int attempt = 0; attempt < 2 && !ok; attempt++)
                            {
                                try
                                {
                                    using var resp = await http.GetAsync(u, HttpCompletionOption.ResponseContentRead);
                                    if (!resp.IsSuccessStatusCode)
                                    {
                                        int code = (int)resp.StatusCode;
                                        lastErr = new HttpRequestException($"HTTP {code}");
                                        if (code is 404 or 410 or 403) break; // try next fallback URL immediately
                                        if (attempt == 0) await Task.Delay(220);
                                        continue;
                                    }
                                    var data = await resp.Content.ReadAsByteArrayAsync();
                                    // Reject YouTube's tiny ~1097-byte grey 120x90 missing-thumb placeholder when a fallback exists
                                    if (data.Length < 1400 && ci + 1 < candidates.Count && u.Contains("ytimg.com/", StringComparison.OrdinalIgnoreCase))
                                        break;
                                    Convert(data, img);
                                    ok = true;
                                }
                                catch (Exception ex)
                                {
                                    lastErr = ex;
                                    if (ex is not HttpRequestException and not TaskCanceledException and not IOException)
                                        break; // decode error -> jump straight to next fallback URL (e.g. webp -> jpg)
                                    if (attempt == 0) await Task.Delay(220);
                                }
                            }
                        }
                        if (!ok) throw lastErr ?? new IOException("image fetch failed");
                    }
                    img.FailCount = 0;
                    Volatile.Write(ref img.State, 1);
                    _ready.Enqueue(img);
                    App.WakeUi();
                }
                catch (Exception e)
                {
                    img.FailCount++;
                    img.FailedAt = Environment.TickCount64;
                    Volatile.Write(ref img.State, 3);
                    if (img.FailCount <= 2) Paths.Log($"image {img.Url}: {e.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            if (!_queue.IsEmpty)
            {
                if (Interlocked.Increment(ref _running) <= MaxParallel) _ = Task.Run(Worker);
                else Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>JPEG to display-size pixels to GPU blocks, written into the pack. Runs on a worker.</summary>
    private static unsafe void Convert(byte[] data, Img img)
    {
        var wic = Wic.Factory;
        using var stream = wic.CreateStream(data);
        using var decoder = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        // centre-crop to the target aspect (YouTube's hqdefault is 4:3 with black bars)
        int sw = size.Width, sh = size.Height;
        int cw = sw, ch = (int)Math.Round((double)sw * img.H / img.W);
        if (ch > sh) { ch = sh; cw = (int)Math.Round((double)sh * img.W / img.H); }
        using var clip = wic.CreateBitmapClipper();
        clip.Initialize(frame, new RectI((sw - cw) / 2, (sh - ch) / 2, cw, ch));
        using var scaler = wic.CreateBitmapScaler();
        scaler.Initialize(clip, (uint)img.W, (uint)img.H, Vortice.WIC.BitmapInterpolationMode.Fant);
        using var conv = wic.CreateFormatConverter();
        conv.Initialize(scaler, PixelFormat.Format32bppPBGRA);
        int n = img.W * img.H * 4;
        var px = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
        try
        {
            fixed (byte* p = px) conv.CopyPixels(new RectI(0, 0, img.W, img.H), (uint)(img.W * 4), (uint)n, (nint)p);
            var key = ThumbPack.Key(img.Url, img.W, img.H, img.Round);
            ThumbPack.Entry e;
            byte* at;
            int pgen;
            bool stored;
            if (img.Round)
            {
                // avatars are tiny and need a soft edge: raw pixels with the circle baked into alpha
                Mask(px, img.W, img.H, Math.Min(img.W, img.H) / 2f);
                stored = ThumbPack.Add(key, px.AsSpan(0, n), img.W, img.H, ThumbPack.Bgra, out e, out at, out pgen);
            }
            else
            {
                int bsz = Bc1.Size(img.W, img.H);
                var blocks = System.Buffers.ArrayPool<byte>.Shared.Rent(bsz);
                try
                {
                    Bc1.Encode(px.AsSpan(0, n), img.W, img.H, blocks);
                    stored = ThumbPack.Add(key, blocks.AsSpan(0, bsz), img.W, img.H, ThumbPack.Bc1Blocks, out e, out at, out pgen);
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(blocks); }
            }
            if (!stored) throw new IOException("thumbnail pack unavailable");
            img.Data = at; img.Format = e.Format; img.Bytes = e.Bytes; img.PackGen = pgen;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(px); }
    }

    /// <summary>Anti-aliased circle (or rounded corners) baked into premultiplied pixels.</summary>
    private static void Mask(byte[] px, int w, int h, float r)
    {
        if (r < 1) return;
        int n = (int)MathF.Ceiling(r);
        for (int y = 0; y < n && y < h; y++)
            for (int x = 0; x < n && x < w; x++)
            {
                float dx = r - x - 0.5f, dy = r - y - 0.5f;
                float a = Math.Clamp(r - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0, 1);
                if (a >= 1) continue;
                Scale(px, (y * w + x) * 4, a);
                Scale(px, (y * w + (w - 1 - x)) * 4, a);
                Scale(px, ((h - 1 - y) * w + x) * 4, a);
                Scale(px, ((h - 1 - y) * w + (w - 1 - x)) * 4, a);
            }
    }

    private static void Scale(byte[] px, int i, float a)
    {
        px[i] = (byte)(px[i] * a); px[i + 1] = (byte)(px[i + 1] * a); px[i + 2] = (byte)(px[i + 2] * a); px[i + 3] = (byte)(px[i + 3] * a);
    }

    /// <summary>Frame start: images that just arrived from the network go to the GPU, a bounded number per frame.</summary>
    public static void Upload()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int n = 0;
        while (n < 24 && _ready.TryDequeue(out var img))
        {
            if (Volatile.Read(ref img.State) != 1) continue;
            Upload(img, fade: !img.Warm);
            n++;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 1.5) break;
        }
        if (!_ready.IsEmpty) UI.Animate();
        if (_gpuBytes > GpuBudget || _map.Count > 1000) Evict(GpuBudget * 3 / 4);
    }

    /// <summary>Aggressively trims off-screen GPU bitmaps when minimized or idle (ThumbPack MMF restores them in &lt;1ms).</summary>
    public static void TrimMemory(bool minimized)
    {
        Evict(minimized ? 6L * 1024 * 1024 : GpuBudget / 2, keepRecentFrames: minimized ? 0 : 120);
    }

    private static void Evict(long targetBudget, long keepRecentFrames = 2)
    {
        _evictBuf.Clear();
        long frame = Gfx.Frame;
        foreach (var img in _map.Values)
        {
            if (Volatile.Read(ref img.State) == 2 && frame - Volatile.Read(ref img.WantedFrame) > keepRecentFrames)
                _evictBuf.Add(img);
        }
        _evictBuf.Sort(static (a, b) => Volatile.Read(ref a.WantedFrame).CompareTo(Volatile.Read(ref b.WantedFrame)));
        for (int i = 0; i < _evictBuf.Count; i++)
        {
            if (_gpuBytes <= targetBudget) break;
            var v = _evictBuf[i];
            v.Bmp?.Dispose();
            v.Bmp = null;
            _gpuBytes = Math.Max(0, _gpuBytes - v.GpuBytes);
            v.GpuBytes = 0;
            _map.Remove(v.Key); // the pack still has it: coming back costs one upload
        }
        _evictBuf.Clear();

        if (_map.Count > 900 || keepRecentFrames == 0)
        {
            _staleKeyBuf.Clear();
            long age = keepRecentFrames == 0 ? 10 : 240;
            foreach (var img in _map.Values)
            {
                if (Volatile.Read(ref img.State) is 1 or 3 or 4 && frame - Volatile.Read(ref img.WantedFrame) > age)
                    _staleKeyBuf.Add(img.Key);
            }
            for (int i = 0; i < _staleKeyBuf.Count; i++) _map.Remove(_staleKeyBuf[i]);
            _staleKeyBuf.Clear();
        }
    }

    /// <summary>The first builds kept one JPEG file per thumbnail; the pack replaces that folder.</summary>
    public static void CleanDisk()
    {
        try
        {
            var old = Path.Combine(Paths.Data, "thumbs");
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        }
        catch { }
    }
}
