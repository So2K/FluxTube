using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluxTube.Services;

namespace FluxTube.Player;

/// <summary>
/// Local HTTP server between mpv and googlevideo. It does what yt-dlp's downloader does:
///  - waits for the link's available_at (before it YouTube answers 403);
///  - asks only for bounded ranges (open-ended requests are throttled to ~0.1 MB/s, 10 MB ranges come at 30+ MB/s);
/// and adds a disk cache: every byte that passes through is stored (see MediaCache), later reads come from disk,
/// and a stream whose beginning is on disk starts playing before its fresh link has even been resolved.
/// </summary>
public sealed class StreamProxy
{
    public static StreamProxy Instance { get; } = new();

    // an entry is a link and a few fields; prepared starts (QuickStream keeps up to 300 videos) point at them,
    // so the table must hold at least as many, or a click lands on a forgotten address
    private const int MaxEntries = 700;

    private sealed class Entry
    {
        public required string Key;
        public string? Url;
        public TaskCompletionSource<bool> UrlReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime AvailableAt;
        public Dictionary<string, string> Headers = new();
        public long Length;
        public string Mime = "application/octet-stream";
        public CachedStream? Cache;
        public int HeadBytes;
        public byte[]? Head;       // only for streams that are not cached on disk
        public Task? HeadTask;
        public long Chunk = 3 * 1024 * 1024;
        public bool Fragile;       // no retries on refusal
        public bool External;      // non-YouTube stream (e.g. Monochrome / hifi-api FLAC): never refresh via QuickStream
        public int Retries = 3;    // 0 until a link from a quick request has proved itself on the first bytes
        // token-less link from the quick player request: serves the beginning of the stream at once, then answers 403
        public string? StarterUrl;
        public Dictionary<string, string> StarterHeaders = new();
        public bool StarterDead;
        public DateTime Touched = DateTime.UtcNow;
        public DateTime RefreshedAt;
        public int AuthFails;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly HttpClient _http;
    private TcpListener? _listener;
    private int _port;

    private StreamProxy()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(6),
        };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { handler.Proxy = new WebProxy(AppSettings.Current.Proxy); handler.UseProxy = true; }
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Start()
    {
        if (_listener != null) return;
        lock (this)
        {
            if (_listener != null) return;
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            _port = ((IPEndPoint)l.LocalEndpoint).Port;
            _listener = l;
            _ = Task.Run(AcceptLoop);
        }
    }

    private string Local(string key) => $"http://127.0.0.1:{_port}/{key}";

    public static int ItagOf(string url)
    {
        var i = url.IndexOf("itag=", StringComparison.Ordinal);
        if (i < 0) return 0;
        i += 5;
        int n = 0;
        while (i < url.Length && char.IsAsciiDigit(url[i])) n = n * 10 + (url[i++] - '0');
        return n;
    }

    /// <summary>
    /// Registers (or refreshes the link of) a stream and returns the local URL mpv opens.
    /// With videoId the stream is cached on disk under videoId+itag, so a new link for the same stream continues the same file.
    /// </summary>
    public string Register(string url, long availableAtUnix, Dictionary<string, string> headers, int headBytes, string? videoId, bool fragile = false, bool unproven = false)
    {
        Start();
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        long.TryParse(q["clen"], out var clen);
        var itag = ItagOf(url);
        var key = videoId != null && itag > 0 && !fragile
            ? $"{videoId}.{itag}"
            : Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20].ToLowerInvariant();
        var e = _entries.GetOrAdd(key, k => new Entry { Key = k });
        lock (e)
        {
            e.Url = url;
            e.AvailableAt = availableAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(availableAtUnix).UtcDateTime : DateTime.UtcNow;
            e.Headers = headers;
            e.HeadBytes = headBytes;
            e.Fragile = fragile;
            e.Retries = unproven ? 0 : 3;
            if (e.HeadTask is { IsCompleted: true } && !HeadReadyLocked(e)) e.HeadTask = null; // an earlier attempt failed: allow another
            if (fragile) e.Chunk = 1024 * 1024;
            if (clen > 0) e.Length = clen;
            if (q["mime"] is { } mime) e.Mime = mime;
            if (e.Cache == null && videoId != null && !fragile) e.Cache = MediaCache.Open(videoId, itag, e.Length, e.Mime);
            e.Touched = DateTime.UtcNow;
        }
        e.UrlReady.TrySetResult(true);
        Trim();
        return Local(key);
    }

    /// <summary>
    /// The quick link for a stream: good for its beginning only. The proper link for the same videoId+itag,
    /// registered later with Register, takes over in the same entry and the same cache file.
    /// </summary>
    public string RegisterStarter(string url, Dictionary<string, string> headers, int headBytes, string videoId)
    {
        Start();
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        long.TryParse(q["clen"], out var clen);
        var itag = ItagOf(url);
        var key = $"{videoId}.{itag}";
        var e = _entries.GetOrAdd(key, k => new Entry { Key = k });
        lock (e)
        {
            e.StarterUrl = url;
            e.StarterHeaders = headers;
            e.StarterDead = false;
            if (e.HeadBytes == 0) e.HeadBytes = headBytes;
            if (e.Length <= 0 && clen > 0) e.Length = clen;
            if (q["mime"] is { } mime && e.Mime == "application/octet-stream") e.Mime = mime;
            if (e.Cache == null) e.Cache = MediaCache.Open(videoId, itag, e.Length, e.Mime);
            if (e.HeadTask is { IsCompleted: true } && !HeadReadyLocked(e)) e.HeadTask = null; // an earlier attempt failed: allow another
            e.Touched = DateTime.UtcNow;
        }
        Trim();
        return Local(key);
    }

    /// <summary>Drops everything known about a video: the next play starts from fresh links.</summary>
    public void Forget(string videoId)
    {
        foreach (var k in _entries.Keys.Where(k => k.StartsWith(videoId + ".", StringComparison.Ordinal)).ToList())
            _entries.TryRemove(k, out _);
    }

    /// <summary>Invalidates one stale itag entry (e.g. when YouTube re-encoded the stream with a new byte length).</summary>
    public void InvalidateItag(string videoId, int itag)
    {
        if (itag <= 0) return;
        if (_entries.TryRemove($"{videoId}.{itag}", out var e))
            e.Cache?.Invalidate();
    }

    /// <summary>
    /// Registers an external lossless FLAC stream (Monochrome / hifi-api) under the dedicated virtual itag (9999)
    /// so it streams through the local proxy and caches seamlessly to MediaCache on disk.
    /// </summary>
    public string RegisterLossless(string url, string videoId, long knownLength = 0, string mime = "audio/flac", Dictionary<string, string>? headers = null)
    {
        Start();
        const int itag = LosslessResolver.LosslessItag;
        var key = $"{videoId}.{itag}";
        var e = _entries.GetOrAdd(key, k => new Entry { Key = k });
        lock (e)
        {
            e.Url = url;
            e.AvailableAt = DateTime.UtcNow;
            e.Headers = headers ?? new Dictionary<string, string> { ["User-Agent"] = "FluxTube/9.2" };
            e.HeadBytes = 128 * 1024; // fast ~100ms header + seektable prefill before mpv attaches
            e.Fragile = false;
            e.External = true;
            e.Retries = 4;
            e.Chunk = 2 * 1024 * 1024; // snappy 2 MB chunks: fast first-byte & seek response on Cloudflare
            if (knownLength > 0) e.Length = knownLength;
            e.Mime = string.IsNullOrWhiteSpace(mime) ? "audio/flac" : mime;
            if (e.Cache == null && e.Length > 0) e.Cache = MediaCache.Open(videoId, itag, e.Length, e.Mime);
            e.Touched = DateTime.UtcNow;
        }
        e.UrlReady.TrySetResult(true);
        Trim();
        return Local(key);
    }

    /// <summary>
    /// Progressively caches an active Lossless FLAC stream to MediaCache in the background after a short grace delay
    /// so it never competes with mpv's initial seek/playback buffer, then makes all future seeks instant from SSD.
    /// </summary>
    public void StartBackgroundDownload(string localUrl)
    {
        var key = localUrl[(localUrl.LastIndexOf('/') + 1)..];
        if (!_entries.TryGetValue(key, out var e) || !e.External) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                await Task.Delay(1800, cts.Token).ConfigureAwait(false);
                if (e.Length <= 0) await ProbeLengthAsync(e, cts.Token).ConfigureAwait(false);
                var cache = e.Cache;
                if (cache == null || e.Length <= 0 || cache.Complete) return;
                Interlocked.Increment(ref cache.Users);
                try
                {
                    long pos = 0;
                    const long chunk = 2 * 1024 * 1024;
                    while (pos < e.Length && !cts.IsCancellationRequested)
                    {
                        var cov = cache.Covered(pos);
                        if (cov > 0) { pos += cov; continue; }
                        long to = Math.Min(e.Length - 1, pos + chunk - 1);
                        to = Math.Min(to, cache.NextStart(pos) - 1);
                        if (to < pos) { pos++; continue; }
                        using var resp = await FetchAsync(e, pos, to, cts.Token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) break;
                        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                        if (bytes.Length == 0) break;
                        cache.Write(pos, bytes);
                        pos += bytes.Length;
                        await Task.Delay(120, cts.Token).ConfigureAwait(false); // yield bandwidth to active mpv reads
                    }
                }
                finally { Interlocked.Decrement(ref cache.Users); }
            }
            catch { }
        });
    }

    /// <summary>Does this look like the start of an mp4, webm, flac, wav, ogg or id3 media file?</summary>
    private static bool PlausibleStart(ReadOnlySpan<byte> b)
        => b.Length >= 12 && ((b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p')
                              || (b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
                              || (b[0] == (byte)'f' && b[1] == (byte)'L' && b[2] == (byte)'a' && b[3] == (byte)'C')
                              || (b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F')
                              || (b[0] == (byte)'O' && b[1] == (byte)'g' && b[2] == (byte)'g' && b[3] == (byte)'S')
                              || (b[0] == (byte)'I' && b[1] == (byte)'D' && b[2] == (byte)'3'));

    /// <summary>Length of a registered stream, 0 when unknown: lets the caller check a new link is for the same bytes.</summary>
    public long LengthOf(string videoId, int itag) => _entries.TryGetValue($"{videoId}.{itag}", out var e) ? e.Length : 0;

    private static bool HeadReadyLocked(Entry e)
        => e.Head != null || (e.Cache != null && e.Cache.Covered(0) >= Math.Min(Math.Max(1, e.HeadBytes), Math.Max(1, e.Length)));

    /// <summary>A stream that is (partly) on disk: playable now, its link can be supplied later with Register.</summary>
    public string RegisterCached(CachedStream cs)
    {
        Start();
        var key = $"{cs.VideoId}.{cs.Itag}";
        var e = _entries.GetOrAdd(key, k => new Entry { Key = k });
        lock (e)
        {
            e.Cache ??= cs;
            e.Length = cs.Length;
            e.Mime = cs.Mime;
            e.Touched = DateTime.UtcNow;
        }
        return Local(key);
    }

    /// <summary>Downloads the first bytes ahead of the click (to disk when cached, else to memory).</summary>
    public Task PrefillAsync(string localUrl)
    {
        var key = localUrl[(localUrl.LastIndexOf('/') + 1)..];
        if (!_entries.TryGetValue(key, out var e)) return Task.CompletedTask;
        Task ht;
        lock (e)
        {
            ht = e.HeadTask ??= Task.Run(async () =>
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (e.Length <= 0) await ProbeLengthAsync(e, limit.Token);
                    var want = Math.Min(e.HeadBytes, e.Length > 0 ? e.Length : e.HeadBytes);
                    if (e.Cache != null)
                    {
                        var have = e.Cache.Covered(0);
                        if (have >= want) return;
                        using var resp = await FetchAsync(e, have, want - 1, limit.Token, starterOnly: e.Url == null);
                        if (resp.StatusCode != HttpStatusCode.PartialContent && have > 0) return;
                        var bytes = await resp.Content.ReadAsByteArrayAsync(limit.Token);
                        if (bytes.Length <= want - have + 4096) e.Cache.Write(have, bytes);
                    }
                    else
                    {
                        using var resp = await FetchAsync(e, 0, want - 1, limit.Token, starterOnly: e.Url == null);
                        if (resp.StatusCode != HttpStatusCode.PartialContent) return;
                        var bytes = await resp.Content.ReadAsByteArrayAsync(limit.Token);
                        if (bytes.Length <= want + 4096) e.Head = bytes;
                    }
                }
                catch (Exception ex) { if (!e.Fragile && e.Url != null && e.Retries > 0) Paths.Log("prefill: " + ex.Message); }
            });
        }
        return ht;
    }

    /// <summary>The link served its first bytes: from now on a refusal is worth retrying.</summary>
    public void Confirm(string localUrl)
    {
        if (_entries.TryGetValue(localUrl[(localUrl.LastIndexOf('/') + 1)..], out var e)) e.Retries = 3;
    }

    /// <summary>True when the first bytes are already here (disk or memory): a click will start without a network round trip.</summary>
    public bool HeadReady(string localUrl)
    {
        var key = localUrl[(localUrl.LastIndexOf('/') + 1)..];
        if (!_entries.TryGetValue(key, out var e)) return false;
        return e.Head != null || (e.Cache != null && e.Cache.Covered(0) >= Math.Min(Math.Max(1, e.HeadBytes), Math.Max(1, e.Length)));
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries) return;
        var pinned = MediaCache.Pinned;
        var snap = _entries.Select(kv => (kv.Key, kv.Value, Touched: kv.Value.Touched)).OrderByDescending(x => x.Touched).Skip(MaxEntries);
        foreach (var (key, val, _) in snap)
        {
            // never what is playing, in background tabs, or being read right now
            var dot = key.IndexOf('.');
            var vid = dot > 0 ? key[..dot] : key;
            if ((pinned != null && vid == pinned) || (MediaCache.IsActive?.Invoke(vid) ?? false)) continue;
            if (val.Cache is { } cs && Volatile.Read(ref cs.Users) > 0) continue;
            _entries.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { }
    }

    private async Task ProbeLengthAsync(Entry e, CancellationToken ct = default)
    {
        using var own = ct.CanBeCanceled ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await FetchAsync(e, 0, 0, own?.Token ?? ct);
        long len = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? 0;
        if (len > 0)
        {
            e.Length = len;
            if (e.External && e.Cache == null)
            {
                var dot = e.Key.IndexOf('.');
                if (dot > 0 && int.TryParse(e.Key[(dot + 1)..], out var itag))
                    e.Cache = MediaCache.Open(e.Key[..dot], itag, len, e.Mime);
            }
        }
    }

    /// <summary>Sends and waits for the response headers, 10 s at most (the body is read by the caller).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(10_000);
        try { return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("googlevideo did not answer"); }
    }

    /// <summary>
    /// The link stopped working or stalled in the middle of a video: ask for a fresh link for the same
    /// formats (QuickStream first, yt-dlp Resolver fallback). The answer lands in the same entries, so the stream carries on seamlessly.
    /// </summary>
    private static async Task<bool> RefreshLinkAsync(Entry e, bool force = false, bool preferResolver = false)
    {
        if (e.External) return false;
        var dot = e.Key.IndexOf('.');
        var cooldown = force ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(25);
        if (dot != 11 || DateTime.UtcNow - e.RefreshedAt < cooldown) return false;
        e.RefreshedAt = DateTime.UtcNow;
        var id = e.Key[..dot];
        var known = Db.CacheVideo(id);
        int vitag = known?.Vitag ?? (int.TryParse(e.Key[(dot + 1)..], out var parsedItag) ? parsedItag : 0);
        int aitag = known?.Aitag ?? 0;
        if (vitag <= 0) return false;
        var old = e.Url;
        try
        {
            QuickStream.Forget(id);
            StreamInfo? s = null;
            if (!preferResolver && e.AuthFails < 2)
            {
                try { s = await QuickStream.GetExactAsync(id, vitag, aitag).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { }
            }
            if (s == null || e.Url == old)
            {
                try { s = await Resolver.Instance.GetExactAsync(id, vitag, aitag).WaitAsync(TimeSpan.FromSeconds(18)); }
                catch { }
            }
            bool got = s != null && e.Url != old;
            Paths.Log($"proxy {e.Key}: link refreshed -> {(got ? "new link ok" : "no new link")}");
            return got;
        }
        catch (Exception ex) { Paths.Log($"proxy {e.Key}: link refresh failed: {ex.Message}"); return false; }
    }

    private async Task<HttpResponseMessage> FetchAsync(Entry e, long from, long to, CancellationToken ct, bool starterOnly = false, bool mayRefresh = false, bool forceNewConnection = false)
    {
        // until the proper link exists and is allowed to be used, the quick link serves the beginning
        if ((e.Url == null || DateTime.UtcNow < e.AvailableAt) && e.StarterUrl != null && !e.StarterDead)
        {
            var sreq = new HttpRequestMessage(HttpMethod.Get, e.StarterUrl);
            foreach (var (k, v) in e.StarterHeaders) sreq.Headers.TryAddWithoutValidation(k, v);
            sreq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            var sresp = await SendAsync(sreq, ct);
            if (sresp.IsSuccessStatusCode) return sresp;
            sresp.Dispose();
            e.StarterDead = true; // past the free beginning: from here on only the proper link works
        }
        if (e.Url == null && starterOnly) throw new HttpRequestException("quick link refused");
        // a stream may be playing (from the quick link or from disk) before its proper link exists
        if (e.Url == null) await e.UrlReady.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        var wait = e.AvailableAt - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(150), ct);
        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, e.Url);
            foreach (var (k, v) in e.Headers) req.Headers.TryAddWithoutValidation(k, v);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            if (forceNewConnection || attempt > 0) req.Headers.ConnectionClose = true;
            var resp = await SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) { e.AuthFails = 0; return resp; }
            var code = (int)resp.StatusCode;
            resp.Dispose();
            if (code is 401 or 403) e.AuthFails++;
            if (e.Fragile || attempt >= e.Retries || code is not (401 or 403 or 404 or 410 or 429 or 500 or 502 or 503))
            {
                if (code is (401 or 403 or 404 or 410 or 429 or 503) && mayRefresh && !e.Fragile && await RefreshLinkAsync(e, force: code == 401, preferResolver: code == 401 && e.AuthFails >= 2)) { attempt = -1; continue; }
                throw new HttpRequestException($"googlevideo {code}");
            }
            if (code is (401 or 403 or 410) && mayRefresh && !e.Fragile && (code == 401 || attempt == 1) && await RefreshLinkAsync(e, force: code == 401, preferResolver: code == 401 && e.AuthFails >= 2)) { attempt = -1; continue; }
            await Task.Delay(300 * (attempt + 1), ct);
        }
    }

    // ---------------- tiny HTTP/1.1 server for mpv ----------------

    private async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _listener!.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(c));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var net = client.GetStream();
        using var cts = new CancellationTokenSource();
        CachedStream? user = null;
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            var (method, path, range) = await ReadRequestAsync(net);
            var key = path.TrimStart('/');
            if (!_entries.TryGetValue(key, out var e)) { await WriteAsync(net, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
            e.Touched = DateTime.UtcNow;
            if (e.Length <= 0) await ProbeLengthAsync(e);

            long start = 0, end = e.Length - 1;
            if (range is { } r)
            {
                start = r.from;
                if (r.to is { } t && t < end) end = t;
            }
            if (start >= e.Length)
            {
                await WriteAsync(net, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{e.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            var status = range != null ? "206 Partial Content" : "200 OK";
            var head = $"HTTP/1.1 {status}\r\nContent-Type: {e.Mime}\r\nAccept-Ranges: bytes\r\nContent-Length: {end - start + 1}\r\n" +
                       (range != null ? $"Content-Range: bytes {start}-{end}/{e.Length}\r\n" : "") + "Connection: close\r\n\r\n";
            await WriteAsync(net, head);
            if (method == "HEAD") return;

            var cache = e.Cache;
            if (cache != null) { Interlocked.Increment(ref cache.Users); user = cache; }
            // a cached file that does not begin like a media file would make the player say "unrecognized file format"
            if (start == 0 && cache != null && cache.Covered(0) >= 16)
            {
                Span<byte> hdr = stackalloc byte[16];
                if (cache.Read(0, hdr) == 16 && !PlausibleStart(hdr))
                {
                    Paths.Log($"proxy {key}: cached start is not media data, fetching again");
                    cache.Invalidate();
                }
            }
            var pos = start;

            // a prefill in flight covers the beginning: joining it beats a second request
            if (pos < e.HeadBytes && e.HeadTask is { IsCompleted: false } ht && (cache == null ? e.Head == null : cache.Covered(pos) == 0))
            {
                try { await ht.WaitAsync(TimeSpan.FromSeconds(12), cts.Token); }
                catch (TimeoutException) { }
            }

            if (cache == null && e.Head is { } mem && pos < mem.Length)
            {
                var n = (int)Math.Min(mem.Length - pos, end - pos + 1);
                await net.WriteAsync(mem.AsMemory((int)pos, n), cts.Token);
                pos += n;
            }

            var first = true;
            var stalls = 0;
            var upstreamErrors = 0;
            while (pos <= end)
            {
                // 1. whatever the disk has
                var have = cache?.Covered(pos) ?? 0;
                if (have > 0)
                {
                    var n = (int)Math.Min(Math.Min(have, end - pos + 1), buf.Length);
                    n = cache!.Read(pos, buf.AsSpan(0, n));
                    if (n <= 0) break;
                    await net.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    pos += n;
                    continue;
                }
                // 2. the network, in bounded 3 MB chunks (fast, resists mid-request DPI/ISP throttling); stored as it passes through
                var size = first && pos == 0 ? Math.Min(e.Chunk, 1024 * 1024) : e.Chunk;
                first = false;
                var to = Math.Min(end, pos + size - 1);
                if (cache != null) to = Math.Min(to, cache.NextStart(pos) - 1);

                var before = pos;
                bool upstreamFailed = false;
                try
                {
                    using var resp = await FetchAsync(e, pos, to, cts.Token, mayRefresh: true, forceNewConnection: upstreamErrors > 0);
                    // an answer that ignores the range would put the wrong bytes at this position, in the file and in the player
                    if (resp.StatusCode != HttpStatusCode.PartialContent && pos != 0) throw new HttpRequestException($"range ignored ({(int)resp.StatusCode})");
                    await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    while (pos <= to)
                    {
                        int n;
                        idle.CancelAfter(e.External ? 14_000 : 6_000); // generous timeout for Hi-Res FLAC CDNs, snappy for googlevideo
                        try { n = await body.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, to - pos + 1)), idle.Token); }
                        catch (OperationCanceledException) when (!cts.IsCancellationRequested) { upstreamFailed = true; break; }
                        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException)
                        {
                            // Upstream TCP reset/drop MUST be retried here — never let it kill mpv's local socket!
                            Paths.Log($"proxy {key}: upstream read reset at {pos}, retrying ({ex.Message})");
                            upstreamFailed = true;
                            break;
                        }
                        if (n <= 0) break;
                        try { cache?.Write(pos, buf.AsSpan(0, n)); }
                        catch (IOException) { /* disk full or evicted: keep streaming cleanly without caching */ }
                        pos += n;
                        // Downstream write to mpv: IOException here means mpv closed the socket (seek/stop/switch Short) —
                        // exit immediately instead of falling into the upstream retry catch!
                        try { await net.WriteAsync(buf.AsMemory(0, n), cts.Token); }
                        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex) when (!cts.IsCancellationRequested && ex is HttpRequestException or IOException or SocketException or TimeoutException)
                {
                    if (upstreamErrors < 3) Paths.Log($"proxy {key}: upstream error at {pos}, retrying ({ex.Message})");
                    upstreamFailed = true;
                }

                if (pos > before)
                {
                    stalls = 0;
                    upstreamErrors = 0;
                }
                else
                {
                    upstreamErrors++;
                    stalls++;
                    if (stalls >= 2 && !e.Fragile)
                    {
                        bool refreshed = await RefreshLinkAsync(e, force: stalls >= 3 || e.AuthFails >= 2, preferResolver: e.AuthFails >= 2);
                        // Stop hammering a dead 401/403 link when refresh failed — let PlaybackTracker switch to full yt-dlp cleanly!
                        if (!refreshed && e.AuthFails >= 3)
                            throw new HttpRequestException("upstream auth failed after refresh");
                    }
                    if (stalls > 5) throw new HttpRequestException("upstream gives no data after retries");
                    if (upstreamFailed || stalls > 1)
                        await Task.Delay(Math.Min(1200, 250 * stalls), cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // mpv closed the connection (seek or stop)
        }
        catch (HttpRequestException ex) { Paths.Log("proxy request: " + ex.Message); }
        catch (Exception ex) { Paths.Log("proxy: " + ex.Message); }
        finally
        {
            cts.Cancel();
            if (user != null) Interlocked.Decrement(ref user.Users);
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async Task<(string method, string path, (long from, long? to)? range)> ReadRequestAsync(NetworkStream net)
    {
        var buf = new byte[8192];
        int len = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var n = await net.ReadAsync(buf.AsMemory(len), timeout.Token);
            if (n == 0) throw new IOException("closed");
            len += n;
            var text = Encoding.ASCII.GetString(buf, 0, len);
            var endIdx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (endIdx < 0) { if (len == buf.Length) throw new IOException("header too big"); continue; }
            var lines = text[..endIdx].Split("\r\n");
            var first = lines[0].Split(' ');
            (long, long?)? range = null;
            foreach (var l in lines.Skip(1))
            {
                if (!l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                var v = l[6..].Trim();
                if (!v.StartsWith("bytes=")) continue;
                var parts = v[6..].Split('-');
                if (long.TryParse(parts[0], out var a))
                    range = (a, parts.Length > 1 && long.TryParse(parts[1], out var b) ? b : null);
            }
            return (first[0], first.Length > 1 ? first[1] : "/", range);
        }
    }

    private static Task WriteAsync(NetworkStream net, string s) => net.WriteAsync(Encoding.ASCII.GetBytes(s)).AsTask();
}
