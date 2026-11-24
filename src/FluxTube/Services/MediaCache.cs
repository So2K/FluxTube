using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluxTube.Services;

/// <summary>One stream (a video or audio track of one video) stored as a sparse file plus the byte ranges it holds.</summary>
public sealed class CachedStream
{
    public required string VideoId { get; init; }
    public required int Itag { get; init; }
    public required long Length { get; init; }
    public required string Mime { get; init; }
    public string Path => System.IO.Path.Combine(MediaCache.Dir, $"{VideoId}.{Itag}.bin");

    private readonly List<(long From, long To)> _ranges = []; // sorted, merged, To exclusive
    private SafeFileHandle? _h;
    private readonly object _lock = new();
    internal bool Dirty;
    internal int Users; // connections reading or writing right now

    public long Bytes { get { lock (_lock) return _ranges.Sum(r => r.To - r.From); } }
    public bool Complete { get { lock (_lock) return Length > 0 && _ranges.Count == 1 && _ranges[0].From == 0 && _ranges[0].To >= Length; } }

    internal string RangesText { get { lock (_lock) return string.Join(';', _ranges.Select(r => $"{r.From}-{r.To}")); } }

    internal void LoadRanges(string text)
    {
        lock (_lock)
        {
            _ranges.Clear();
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = part.IndexOf('-');
                if (i > 0 && long.TryParse(part[..i], out var a) && long.TryParse(part[(i + 1)..], out var b) && b > a) _ranges.Add((a, b));
            }
        }
    }

    /// <summary>How many bytes are stored contiguously starting at pos (0 = not cached there).</summary>
    public long Covered(long pos)
    {
        lock (_lock)
            foreach (var (from, to) in _ranges)
            {
                if (pos < from) return 0;
                if (pos < to) return to - pos;
            }
        return 0;
    }

    /// <summary>Where the next stored range after pos begins, so a download stops where the cache takes over.</summary>
    public long NextStart(long pos)
    {
        lock (_lock)
            foreach (var (from, _) in _ranges)
                if (from > pos) return from;
        return long.MaxValue;
    }

    private SafeFileHandle Handle()
    {
        if (_h is { IsInvalid: false, IsClosed: false }) return _h;
        lock (_lock)
        {
            if (_h is { IsInvalid: false, IsClosed: false }) return _h;
            Directory.CreateDirectory(MediaCache.Dir);
            var isNew = !File.Exists(Path);
            _h = File.OpenHandle(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            if (isNew)
            {
                // sparse: the file has the stream's full length but only written ranges take disk space
                DeviceIoControl(_h, 0x900C4 /*FSCTL_SET_SPARSE*/, 0, 0, 0, 0, out _, 0);
                RandomAccess.SetLength(_h, Length);
                _ranges.Clear();
            }
            return _h;
        }
    }

    public int Read(long pos, Span<byte> buffer) => RandomAccess.Read(Handle(), buffer, pos);

    public void Write(long pos, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        RandomAccess.Write(Handle(), data, pos);
        lock (_lock)
        {
            long from = pos, to = pos + data.Length;
            int i = 0;
            while (i < _ranges.Count && _ranges[i].To < from) i++;
            while (i < _ranges.Count && _ranges[i].From <= to)
            {
                from = Math.Min(from, _ranges[i].From);
                to = Math.Max(to, _ranges[i].To);
                _ranges.RemoveAt(i);
            }
            _ranges.Insert(i, (from, to));
            Dirty = true;
        }
    }

    /// <summary>Forgets what is stored: the bytes are refetched. Used when the file turns out to be unusable.</summary>
    public void Invalidate()
    {
        lock (_lock) { _ranges.Clear(); Dirty = true; }
    }

    internal void Close()
    {
        lock (_lock) { _h?.Dispose(); _h = null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, nint inBuf, uint inSize, nint outBuf, uint outSize, out uint returned, nint overlapped);
}

public sealed record CachedVideo(string VideoId, string Quality, CachedStream Video, CachedStream? Audio)
{
    public bool Complete => Video.Complete && (Audio?.Complete ?? true);
}

/// <summary>
/// What was played stays on disk, so playing it again starts at once and needs no network.
/// It is a bounded cache, not a download folder:
///  - a size limit (1 GB by default);
///  - things played often (music on repeat) are kept; things watched once go after a week, a few times after a month;
///  - when the limit is hit, the least valuable entries go first: rarely played and not opened for the longest time.
/// Streams are stored exactly as YouTube sends them (AV1/VP9 + Opus): already compressed as far as it goes.
/// </summary>
public static class MediaCache
{
    public static string Dir => Path.Combine(Paths.Data, "media");
    private static readonly ConcurrentDictionary<(string, int), CachedStream> _open = new();
    private static Timer? _timer;
    private static long _lastEnforce;
    /// <summary>The video being played: never evicted.</summary>
    public static string? Pinned;

    public static bool Enabled => AppSettings.Current.CacheLimitMb > 0;
    private static long Limit => AppSettings.Current.CacheLimitMb * 1024L * 1024L;

    /// <summary>The cache file for a stream, created on first use. Null when caching is off or the length is unknown.</summary>
    public static CachedStream? Open(string videoId, int itag, long length, string mime)
    {
        if (!Enabled || length <= 0 || itag <= 0) return null;
        _timer ??= new Timer(_ => Flush(), null, 5000, 5000);
        return _open.GetOrAdd((videoId, itag), _ =>
        {
            var cs = new CachedStream { VideoId = videoId, Itag = itag, Length = length, Mime = mime };
            var row = Db.CacheStream(videoId, itag);
            if (row != null && row.Value.Length == length && File.Exists(cs.Path)) cs.LoadRanges(row.Value.Ranges);
            else
            {
                try { if (File.Exists(cs.Path)) File.Delete(cs.Path); } catch { }
                cs.Dirty = true;
            }
            return cs;
        });
    }

    /// <summary>A previously played video at this quality whose beginning is on disk (enough to start instantly).</summary>
    public static CachedVideo? Find(string videoId, string quality)
    {
        if (!Enabled) return null;
        var v = Db.CacheVideo(videoId);
        if (v == null || v.Value.Quality != quality) return null;
        CachedStream? Load(int itag)
        {
            var row = Db.CacheStream(videoId, itag);
            return row == null ? null : Open(videoId, itag, row.Value.Length, row.Value.Mime);
        }
        var video = Load(v.Value.Vitag);
        if (video == null || video.Covered(0) < Math.Min(video.Length, 256 * 1024)) return null;
        CachedStream? audio = null;
        if (v.Value.Aitag > 0)
        {
            audio = Load(v.Value.Aitag);
            if (audio == null || audio.Covered(0) < Math.Min(audio.Length, 64 * 1024)) return null;
        }
        return new CachedVideo(videoId, quality, video, audio);
    }

    /// <summary>Counts a play: frequency and recency decide what survives.</summary>
    public static void NotePlay(string videoId, string quality, int vitag, int aitag)
    {
        if (!Enabled || vitag <= 0) return;
        Db.CacheTouch(videoId, quality, vitag, aitag, addPlay: true);
    }

    /// <summary>A prefetched beginning (card hovered, not yet played): remembered with zero plays so it is cleaned up first.</summary>
    public static void NoteSeen(string videoId, string quality, int vitag, int aitag)
    {
        if (!Enabled || vitag <= 0) return;
        Db.CacheTouch(videoId, quality, vitag, aitag, addPlay: false);
    }

    private static void Flush()
    {
        try
        {
            foreach (var cs in _open.Values)
            {
                if (!cs.Dirty) continue;
                cs.Dirty = false;
                Db.CacheSaveStream(cs.VideoId, cs.Itag, cs.Length, cs.Mime, cs.RangesText, cs.Bytes);
            }
            if (Environment.TickCount64 - _lastEnforce > 30_000) Enforce();
        }
        catch (Exception e) { Paths.Log("cache flush: " + e.Message); }
    }

    public static long TotalBytes() => Db.CacheTotalBytes();

    private static int Days(int plays) => plays >= 5 ? 180 : plays >= 2 ? 30 : 7;

    /// <summary>Expiry by age, then eviction by value while over the limit.</summary>
    public static void Enforce()
    {
        _lastEnforce = Environment.TickCount64;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var videos = Db.CacheVideos();
        long total = 0;
        var keep = new List<(string Id, int Plays, long Last, long Bytes)>();
        foreach (var v in videos)
        {
            var age = now - v.Last;
            var expired = v.Plays == 0 ? age > 40 * 60 : age > Days(v.Plays) * 86400L; // prefetched beginnings nobody opened go within the hour
            if (expired && v.Id != Pinned) Delete(v.Id);
            else { keep.Add(v); total += v.Bytes; }
        }
        if (total <= Limit) return;
        // least valuable first: never-played prefetches, then by tier (favourites last), oldest within a tier
        foreach (var v in keep.OrderBy(v => v.Plays == 0 ? 0 : v.Plays >= 5 ? 3 : v.Plays >= 2 ? 2 : 1).ThenBy(v => v.Last))
        {
            if (total <= Limit * 9 / 10) break;
            if (v.Id == Pinned) continue;
            if (Delete(v.Id)) total -= v.Bytes;
        }
    }

    /// <summary>Throws away everything cached for a video, even while it is open (bad data, or the user asked).</summary>
    public static void Drop(string videoId)
    {
        if (Pinned == videoId) Pinned = null;
        Delete(videoId, force: true);
    }

    private static bool Delete(string videoId, bool force = false)
    {
        var keys = _open.Keys.Where(k => k.Item1 == videoId).ToList();
        // all or nothing: one stream of the video being read right now keeps the whole video
        if (!force && keys.Any(k => _open.TryGetValue(k, out var u) && Volatile.Read(ref u.Users) > 0)) return false;
        foreach (var key in keys)
            if (_open.TryRemove(key, out var cs)) cs.Close();
        // whoever still holds links to these files (the proxy, prepared starts) must ask again, not read a deleted file
        FluxTube.Player.StreamProxy.Instance.Forget(videoId);
        QuickStream.Forget(videoId);
        Resolver.Instance.Forget(videoId);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, videoId + ".*.bin")) File.Delete(f);
        }
        catch (Exception e) { Paths.Log("cache delete: " + e.Message); return false; }
        Db.CacheDelete(videoId);
        return true;
    }

    public static void Clear()
    {
        foreach (var v in Db.CacheVideos()) if (v.Id != Pinned) Delete(v.Id);
        try
        {
            // files without a row (crash leftovers)
            if (Directory.Exists(Dir))
                foreach (var f in Directory.EnumerateFiles(Dir, "*.bin"))
                {
                    var id = Path.GetFileName(f).Split('.')[0];
                    if (id != Pinned && Db.CacheVideo(id) == null) File.Delete(f);
                }
        }
        catch { }
    }

    public static void Shutdown()
    {
        Flush();
        foreach (var cs in _open.Values) cs.Close();
    }
}
