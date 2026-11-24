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
    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static byte* _base;
    private static long _end;
    private static bool _dirty, _ok;
    private static Timer? _timer;
    private static string PackFile => Path.Combine(Paths.Data, "thumbs.pack");
    private static string IndexFile => Path.Combine(Paths.Data, "thumbs.idx");

    public static long Bytes => _end;
    public static int Count { get { lock (_lock) return _index.Count; } }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, nint inBuf, uint inSize, nint outBuf, uint outSize, out uint returned, nint overlapped);

    public static void Open()
    {
        try
        {
            var fs = new FileStream(PackFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fs.Length != Capacity)
            {
                DeviceIoControl(fs.SafeFileHandle, 0x900C4 /*FSCTL_SET_SPARSE*/, 0, 0, 0, 0, out _, 0);
                fs.SetLength(Capacity);
            }
            _mmf = MemoryMappedFile.CreateFromFile(fs, null, Capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            _view = _mmf.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p;
            LoadIndex();
            _ok = true;
            _timer = new Timer(_ => SaveIndex(), null, 20_000, 20_000);
        }
        catch (Exception e) { Paths.Log("thumb pack: " + e.Message); }
    }

    private static void LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexFile)) return;
            using var r = new BinaryReader(File.OpenRead(IndexFile));
            if (r.ReadUInt32() != Magic) return;
            _end = r.ReadInt64();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var key = r.ReadUInt64();
                var e = new Entry(r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), r.ReadByte(), r.ReadInt32());
                if (e.Offset >= 0 && e.Offset + e.Bytes <= _end && _end <= Capacity) _index[key] = e;
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

    public static bool TryGet(ulong key, out Entry e, out byte* data)
    {
        data = null;
        if (!_ok) { e = default; return false; }
        lock (_lock)
            if (!_index.TryGetValue(key, out e)) return false;
        data = _base + e.Offset;
        return true;
    }

    public static bool Add(ulong key, ReadOnlySpan<byte> bytes, int w, int h, byte format, out Entry e, out byte* data)
    {
        e = default; data = null;
        if (!_ok) return false;
        lock (_lock)
        {
            if (_end + bytes.Length > Capacity)
            {
                // full: start over. Entries come back from the network as they are needed again.
                _index.Clear();
                _end = 0;
            }
            e = new Entry(_end, w, h, format, bytes.Length);
            _end += (bytes.Length + 15) & ~15;
            _index[key] = e;
            _dirty = true;
        }
        data = _base + e.Offset;
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        return true;
    }

    /// <summary>Stable across runs (string.GetHashCode is not).</summary>
    public static ulong Key(string url, int w, bool round)
    {
        ulong h = 14695981039346656037UL;
        foreach (var c in url) { h ^= c; h *= 1099511628211UL; }
        h ^= (ulong)w; h *= 1099511628211UL;
        h ^= round ? 0x9E37UL : 0; h *= 1099511628211UL;
        return h;
    }
}

public sealed unsafe class Img
{
    public ID2D1Bitmap1? Bmp;
    public byte* Data;          // in the pack, waiting for upload
    public int W, H, Bytes;
    public byte Format;
    public int State;           // 0 queued, 1 in the pack, 2 on GPU, 3 failed, 4 dropped from the queue
    public long WantedFrame;    // last frame something asked to draw it
    public long ReadyTick;
    public int Generation;
    public bool Warm;           // came from the pack: no fade-in
    public required Images.IKey Key;
    public required string Url;
    public bool Round;
}

/// <summary>
/// Thumbnail pipeline: never block the frame, anything seen before appears in the very frame it is asked for (see ThumbPack),
/// new ones are fetched newest-request-first, converted off-thread, uploaded a bounded number per frame, and video memory is bounded.
/// </summary>
public static class Images
{
    private const long GpuBudget = 40L * 1024 * 1024; // BC1: room for some 600 thumbnails
    private const int MaxParallel = 6;

    public readonly record struct IKey(string Url, int W, bool Round);
    private static readonly Dictionary<IKey, Img> _map = new();
    private static readonly ConcurrentStack<Img> _queue = new();
    private static readonly ConcurrentQueue<Img> _ready = new();
    private static int _running;
    private static long _gpuBytes;
    private static int _syncThisFrame;
    private static long _syncFrame;
    private static bool _bc1Works = true;
    private static readonly HttpClient _http = MakeClient();
    [ThreadStatic] private static IWICImagingFactory? t_wic;

    public static long GpuBytes => _gpuBytes;
    public static int Count => _map.Count;
    public static int Uploaded;

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, MaxConnectionsPerServer = 8, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { h.Proxy = new WebProxy(AppSettings.Current.Proxy); h.UseProxy = true; }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15), DefaultRequestVersion = HttpVersion.Version20 };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", InnerTube.UserAgent);
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/jpeg,image/png,image/*;q=0.8");
        return c;
    }

    /// <summary>Bitmap for url at w x h DIPs, or null while it loads. Call every frame it is visible.</summary>
    public static unsafe Img? Get(string? url, float wDip, float hDip, bool round = false)
    {
        if (string.IsNullOrEmpty(url)) return null;
        // quantised so a window resize does not re-convert on every pixel; multiples of 4 for the block format
        int w = Math.Max(16, ((int)MathF.Ceiling(wDip * Gfx.Scale) + 15) / 16 * 16);
        int h = Math.Max(4, ((int)MathF.Round(w * hDip / wDip) + 3) / 4 * 4);
        var key = new IKey(url, w, round);
        if (!_map.TryGetValue(key, out var img))
        {
            img = new Img { Key = key, Url = url, W = w, H = h, Round = round, Generation = Gfx.DeviceGeneration };
            _map[key] = img;
            if (ThumbPack.TryGet(ThumbPack.Key(url, w, round), out var e, out var data) && e.W == w && e.H == h)
            {
                img.Data = data; img.Format = e.Format; img.Bytes = e.Bytes; img.State = 5; img.Warm = true;
                // the pack is a memory-mapped file: its pages may still be on disk, and reading them here would
                // stall the frame for as long as the disk takes. A worker touches them; the upload follows next frame.
                ThreadPool.UnsafeQueueUserWorkItem(static i => Warm(i), img, preferLocal: false);
            }
            else Enqueue(img);
        }
        else if (img.State == 2 && img.Generation != Gfx.DeviceGeneration)
        {
            img.Bmp = null; img.State = 1; // device was recreated: the pack still has the data
            TryUploadNow(img);
        }
        else if (img.State == 1) TryUploadNow(img);
        else if (img.State == 4)
        {
            img.State = 0; // was dropped from the queue while off screen
            Enqueue(img);
        }
        img.WantedFrame = Gfx.Frame;
        return img.State == 2 ? img : null;
    }

    private static unsafe void Warm(Img img)
    {
        byte sum = 0;
        for (int i = 0; i < img.Bytes; i += 4096) sum ^= img.Data[i];
        if (img.Bytes > 0) sum ^= img.Data[img.Bytes - 1];
        GC.KeepAlive(sum);
        img.State = 1;
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
        try
        {
            if (img.Format == ThumbPack.Bc1Blocks && !_bc1Works)
            {
                // this GPU path refuses block-compressed bitmaps: expand in software (still no JPEG decode)
                var px = System.Buffers.ArrayPool<byte>.Shared.Rent(img.W * img.H * 4);
                Bc1.Decode(new ReadOnlySpan<byte>(img.Data, img.Bytes), img.W, img.H, px);
                fixed (byte* p = px) img.Bmp = Gfx.CreateBitmap(p, img.W, img.H, bc1: false);
                System.Buffers.ArrayPool<byte>.Shared.Return(px);
                _gpuBytes += img.W * img.H * 4;
            }
            else
            {
                img.Bmp = Gfx.CreateBitmap(img.Data, img.W, img.H, img.Format == ThumbPack.Bc1Blocks);
                _gpuBytes += img.Bytes;
            }
            img.Generation = Gfx.DeviceGeneration;
            img.ReadyTick = fade ? Environment.TickCount64 : 0;
            img.State = 2;
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
            else img.State = 3;
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

    private static async Task Worker()
    {
        try
        {
            while (_queue.TryPop(out var img))
            {
                // scrolled past long ago: skip, it is re-queued if it ever becomes visible again
                if (Gfx.Frame - Volatile.Read(ref img.WantedFrame) > 40 && _queue.Count > 8) { img.State = 4; continue; }
                try
                {
                    var data = img.Url.StartsWith("file:", StringComparison.Ordinal)
                        ? await File.ReadAllBytesAsync(new Uri(img.Url).LocalPath)
                        : await _http.GetByteArrayAsync(img.Url);
                    Convert(data, img);
                    img.State = 1;
                    _ready.Enqueue(img);
                    App.WakeUi();
                }
                catch (Exception e)
                {
                    img.State = 3;
                    Paths.Log($"image {img.Url}: {e.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            if (!_queue.IsEmpty && Interlocked.Increment(ref _running) <= MaxParallel) _ = Task.Run(Worker);
            else if (!_queue.IsEmpty) Interlocked.Decrement(ref _running);
        }
    }

    /// <summary>JPEG to display-size pixels to GPU blocks, written into the pack. Runs on a worker.</summary>
    private static unsafe void Convert(byte[] data, Img img)
    {
        if (t_wic == null) W.CoInitializeEx(0, 0);
        var wic = t_wic ??= new IWICImagingFactory();
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
            var key = ThumbPack.Key(img.Url, img.W, img.Round);
            ThumbPack.Entry e;
            byte* at;
            bool stored;
            if (img.Round)
            {
                // avatars are tiny and need a soft edge: raw pixels with the circle baked into alpha
                Mask(px, img.W, img.H, Math.Min(img.W, img.H) / 2f);
                stored = ThumbPack.Add(key, px.AsSpan(0, n), img.W, img.H, ThumbPack.Bgra, out e, out at);
            }
            else
            {
                var blocks = System.Buffers.ArrayPool<byte>.Shared.Rent(Bc1.Size(img.W, img.H));
                Bc1.Encode(px.AsSpan(0, n), img.W, img.H, blocks);
                stored = ThumbPack.Add(key, blocks.AsSpan(0, Bc1.Size(img.W, img.H)), img.W, img.H, ThumbPack.Bc1Blocks, out e, out at);
                System.Buffers.ArrayPool<byte>.Shared.Return(blocks);
            }
            if (!stored) throw new IOException("thumbnail pack unavailable");
            img.Data = at; img.Format = e.Format; img.Bytes = e.Bytes;
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
            if (img.State != 1) continue;
            Upload(img, fade: !img.Warm);
            n++;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 1.5) break;
        }
        if (!_ready.IsEmpty) UI.Animate();
        if (_gpuBytes > GpuBudget) Evict();
    }

    private static void Evict()
    {
        var victims = _map.Values.Where(i => i.State == 2 && Gfx.Frame - i.WantedFrame > 2).OrderBy(i => i.WantedFrame).ToList();
        foreach (var v in victims)
        {
            if (_gpuBytes < GpuBudget * 3 / 4) break;
            v.Bmp?.Dispose();
            _gpuBytes -= v.Format == ThumbPack.Bc1Blocks && _bc1Works ? v.Bytes : v.W * v.H * 4;
            _map.Remove(v.Key); // the pack still has it: coming back costs one upload
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
