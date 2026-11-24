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
        public long Chunk = 10 * 1024 * 1024;
        public bool Fragile;       // no retries on refusal
        public int Retries = 3;    // 0 until a link from a quick request has proved itself on the first bytes
        // token-less link from the quick player request: serves the beginning of the stream at once, then answers 403
        public string? StarterUrl;
        public Dictionary<string, string> StarterHeaders = new();
        public bool StarterDead;
        public DateTime Touched = DateTime.UtcNow;
        public DateTime RefreshedAt;
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
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(10),
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

    /// <summary>Does this look like the start of an mp4 or a webm?</summary>
    private static bool PlausibleStart(ReadOnlySpan<byte> b)
        => b.Length >= 12 && ((b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p')
                              || (b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3));

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
        lock (e) e.HeadTask ??= Task.Run(async () =>
        {
            // a connection that stalls must not hold the click (or a prefetch slot) forever
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
                    e.Cache.Write(have, await resp.Content.ReadAsByteArrayAsync(limit.Token));
                }
                else
                {
                    using var resp = await FetchAsync(e, 0, want - 1, limit.Token, starterOnly: e.Url == null);
                    e.Head = await resp.Content.ReadAsByteArrayAsync(limit.Token);
                }
            }
            catch (Exception ex) { if (!e.Fragile && e.Url != null && e.Retries > 0) Paths.Log("prefill: " + ex.Message); }
        });
        return e.HeadTask;
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
        foreach (var kv in _entries.OrderByDescending(k => k.Value.Touched).Skip(MaxEntries))
        {
            // never what is playing or being read right now
            if (pinned != null && kv.Key.StartsWith(pinned, StringComparison.Ordinal)) continue;
            if (kv.Value.Cache is { } cs && Volatile.Read(ref cs.Users) > 0) continue;
            _entries.TryRemove(kv.Key, out _);
        }
    }

    private async Task ProbeLengthAsync(Entry e, CancellationToken ct = default)
    {
        using var own = ct.CanBeCanceled ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await FetchAsync(e, 0, 0, own?.Token ?? ct);
        if (resp.Content.Headers.ContentRange?.Length is { } len) e.Length = len;
    }

    /// <summary>Sends and waits for the response headers, 15 s at most (the body is read by the caller).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(15_000);
        try { return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("googlevideo did not answer"); }
    }

    /// <summary>
    /// The link stopped working in the middle of a video (they live about six hours): ask for a new one for the same
    /// formats. The answer lands in the same entries, so the stream carries on where it was.
    /// </summary>
    private static async Task<bool> RefreshLinkAsync(Entry e)
    {
        var dot = e.Key.IndexOf('.');
        if (dot != 11 || DateTime.UtcNow - e.RefreshedAt < TimeSpan.FromMinutes(2)) return false;
        e.RefreshedAt = DateTime.UtcNow;
        var id = e.Key[..dot];
        if (Db.CacheVideo(id) is not { } known) return false;
        var old = e.Url;
        try
        {
            QuickStream.Forget(id);
            var s = await QuickStream.GetExactAsync(id, known.Vitag, known.Aitag).WaitAsync(TimeSpan.FromSeconds(15));
            Paths.Log($"proxy {e.Key}: link expired, {(s != null && e.Url != old ? "got a new one" : "no new link")}");
            return s != null && e.Url != old;
        }
        catch (Exception ex) { Paths.Log($"proxy {e.Key}: link refresh failed: {ex.Message}"); return false; }
    }

    private async Task<HttpResponseMessage> FetchAsync(Entry e, long from, long to, CancellationToken ct, bool starterOnly = false, bool mayRefresh = false)
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
            var resp = await SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return resp;
            var code = (int)resp.StatusCode;
            resp.Dispose();
            if (e.Fragile || attempt >= e.Retries || code is not (403 or 429 or 500 or 503))
            {
                if (code == 403 && mayRefresh && !e.Fragile && await RefreshLinkAsync(e)) { attempt = -1; continue; }
                throw new HttpRequestException($"googlevideo {code}");
            }
            await Task.Delay(400 * (attempt + 1), ct);
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
                // 2. the network, in bounded chunks, up to where the disk takes over again; stored as it passes through
                var size = first && pos == 0 ? Math.Min(e.Chunk, 2 * 1024 * 1024) : e.Chunk;
                first = false;
                var to = Math.Min(end, pos + size - 1);
                if (cache != null) to = Math.Min(to, cache.NextStart(pos) - 1);
                using var resp = await FetchAsync(e, pos, to, cts.Token, mayRefresh: true);
                // an answer that ignores the range would put the wrong bytes at this position, in the file and in the player
                if (resp.StatusCode != HttpStatusCode.PartialContent && pos != 0) throw new HttpRequestException("range ignored");
                await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
                var before = pos;
                while (pos <= to)
                {
                    int n;
                    // the read may stall (a dead connection); writing to the player may rightly wait (it is paused): only the read is timed
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                    {
                        idle.CancelAfter(20_000);
                        try { n = await body.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, to - pos + 1)), idle.Token); }
                        catch (OperationCanceledException) when (!cts.IsCancellationRequested) { n = -1; }
                    }
                    if (n == -1) break; // stalled: ask again from here
                    if (n <= 0) break;
                    cache?.Write(pos, buf.AsSpan(0, n));
                    pos += n;
                    await net.WriteAsync(buf.AsMemory(0, n), cts.Token);
                }
                if (pos > before) stalls = 0;
                else if (++stalls > 3) throw new HttpRequestException("upstream gives no data"); // no progress: do not spin
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // mpv closed the connection (seek or stop)
        }
        catch (HttpRequestException) { /* upstream refused (a preview ran out, or the link died): the stream just ends here */ }
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
        while (true)
        {
            var n = await net.ReadAsync(buf.AsMemory(len));
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
