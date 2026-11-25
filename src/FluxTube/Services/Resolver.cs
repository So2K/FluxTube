using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxTube.Services;

public sealed record StreamInfo(string VideoId, string Video, string? Audio, Dictionary<string, string> Headers,
    string? Title, double Duration, DateTime ResolvedAt)
{
    public int Vitag { get; init; }
    public int Aitag { get; init; }
    /// <summary>Quick-start stand-in: good for the first seconds only.</summary>
    public bool Preview { get; init; }
    // googlevideo links live ~6 h; refresh well before that
    public bool Fresh => DateTime.UtcNow - ResolvedAt < TimeSpan.FromHours(4);
}

/// <summary>One warm Python yt-dlp process answering JSON lines (Assets/ytsrv.py). Leaves after two idle minutes.</summary>
internal sealed class PyServer(string name, int threads, int idleSeconds)
{
    private Process? _proc;
    private StreamWriter? _in;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _waiting = new();
    private long _lastUse;
    private Timer? _idle;
    private bool _failed;
    private int _seq;

    public bool Ensure()
    {
        lock (_lock)
        {
            _lastUse = Environment.TickCount64;
            _idle ??= new Timer(_ => IdleCheck(), null, 30_000, 30_000);
            if (_failed) return false;
            if (_proc is { HasExited: false }) return true;
            var script = Path.Combine(AppContext.BaseDirectory, "Assets", "ytsrv.py");
            if (!File.Exists(Paths.PyExe) || !File.Exists(script)) return false;
            try
            {
                var psi = new ProcessStartInfo(Paths.PyExe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                foreach (var a in new[] { "-I", "-u", script, Paths.YtCache, Paths.Deno ?? "", threads.ToString() }) psi.ArgumentList.Add(a);
                _proc = Process.Start(psi)!;
                _in = _proc.StandardInput;
                _in.AutoFlush = true;
                _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Paths.Log($"ytsrv {name}: {e.Data}"); };
                _proc.BeginErrorReadLine();
                var proc = _proc;
                _ = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    {
                        try
                        {
                            var n = JsonNode.Parse(line)!;
                            if (n.S("id") is { } rid && _waiting.TryRemove(rid, out var w)) w.TrySetResult(n);
                        }
                        catch { }
                    }
                    foreach (var w in _waiting.Values) w.TrySetException(new IOException("resolver exited"));
                    _waiting.Clear();
                });
                return true;
            }
            catch (Exception e)
            {
                Paths.Log($"ytsrv {name} start: {e.Message}");
                _failed = true;
                return false;
            }
        }
    }

    /// <summary>Sends a job and waits for its answer. tag only makes the request id readable in logs.</summary>
    public async Task<JsonNode> Ask(JsonObject req, string tag, int timeoutSeconds = 40)
    {
        var rid = tag + "#" + name + Interlocked.Increment(ref _seq);
        req["id"] = rid;
        var w = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[rid] = w;
        try
        {
            lock (_lock) { _lastUse = Environment.TickCount64; _in!.WriteLine(req.ToJsonString()); }
            return await w.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        }
        finally { _waiting.TryRemove(rid, out _); }
    }

    public void Tell(JsonObject req)
    {
        req["id"] = "tell#" + Interlocked.Increment(ref _seq);
        lock (_lock) { _lastUse = Environment.TickCount64; _in?.WriteLine(req.ToJsonString()); }
    }

    private void IdleCheck()
    {
        lock (_lock)
        {
            if (_proc is not { HasExited: false }) return;
            if (Environment.TickCount64 - _lastUse < idleSeconds * 1000L || !_waiting.IsEmpty) return;
            try { _proc.Kill(true); } catch { }
            _proc = null;
            Paths.Log($"resolver {name} idle: stopped");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        }
    }
}

/// <summary>
/// Turns a video id into full-quality stream links.
/// Two yt-dlp processes: one reserved for what the user just clicked (it never waits behind anything),
/// one for background prefetch (visible cards, related videos). Separate processes, because parallel
/// extractions inside one Python process slow each other down several times over.
/// </summary>
public sealed class Resolver
{
    public static Resolver Instance { get; } = new();

    private readonly ConcurrentDictionary<string, Task<StreamInfo?>> _cache = new();
    private readonly ConcurrentDictionary<string, string> _failures = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StreamInfo?>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _onClickServer = new();
    private readonly LinkedList<string> _queue = new(); // front = most urgent
    private readonly object _qLock = new();
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly PyServer _click = new("click", 2, 3 * 60);
    private readonly PyServer _bg = new("bg", 2, 2 * 60);
    private bool _clickWarmed;

    /// <summary>
    /// The first extraction in a fresh process is slow (player script download, new connections).
    /// Spend it on a card the user is looking at, so the reserved process is hot before the first real click.
    /// </summary>
    public void WarmClickWith(string videoId)
    {
        if (_clickWarmed || videoId.Length != 11) return;
        _clickWarmed = true;
        _ = GetAsync(videoId);
    }
    private const int MaxQueued = 16;

    public string Format => AppSettings.Current.PlayerFormat;

    private static bool Usable(Task<StreamInfo?> t) => !t.IsFaulted && !(t.IsCompletedSuccessfully && t.Result is not { Fresh: true });

    /// <summary>Ready stream if it is already resolved, without waiting.</summary>
    public StreamInfo? TryGet(string videoId)
        => _cache.TryGetValue(videoId, out var t) && t.IsCompletedSuccessfully && t.Result is { Fresh: true } s ? s : null;

    public string? FailureFor(string videoId) => _failures.TryGetValue(videoId, out var reason) ? reason : null;

    /// <summary>The user wants this one now: it goes straight to the reserved process, whatever the background is doing.</summary>
    public Task<StreamInfo?> GetAsync(string videoId)
    {
        TaskCompletionSource<StreamInfo?> tcs;
        lock (_qLock)
        {
            if (_cache.TryGetValue(videoId, out var t) && Usable(t))
            {
                if (t.IsCompleted || !_pending.TryGetValue(videoId, out tcs!)) return t;
            }
            else
            {
                if (_cache.Count > 256)
                {
                    foreach (var k in _cache.Keys.Take(64).ToList())
                        if (!_pending.ContainsKey(k)) _cache.TryRemove(k, out _);
                }
                tcs = new TaskCompletionSource<StreamInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _cache[videoId] = tcs.Task;
                _pending[videoId] = tcs;
            }
            _queue.Remove(videoId); // not started in the background yet: no need to do it twice
        }
        if (_onClickServer.TryAdd(videoId, 0))
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                StreamInfo? s = null;
                try { s = _click.Ensure() ? await ViaServerAsync(_click, videoId, null) : await ViaExeAsync(videoId); }
                finally
                {
                    _onClickServer.TryRemove(videoId, out _);
                    Paths.Log($"resolve {videoId} {(s != null ? "ok" : "fail")} {sw.ElapsedMilliseconds} ms (click)");
                    Finish(videoId, tcs, s);
                }
            });
        return tcs.Task;
    }

    /// <summary>Background hint. Most recent hints win; old ones drop off when the queue is full.</summary>
    public void Prefetch(string videoId, bool urgent = false)
    {
        if (string.IsNullOrEmpty(videoId) || videoId.Length != 11) return;
        if (_cache.TryGetValue(videoId, out var t) && Usable(t))
        {
            if (urgent) lock (_qLock) { if (_queue.Remove(videoId)) _queue.AddFirst(videoId); }
            return;
        }
        var tcs = new TaskCompletionSource<StreamInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cache[videoId] = tcs.Task;
        _pending[videoId] = tcs;
        lock (_qLock)
        {
            _queue.Remove(videoId);
            _queue.AddFirst(videoId); // LIFO: the newest hint is what the user looks at now
            while (_queue.Count > MaxQueued)
            {
                var old = _queue.Last!.Value;
                _queue.RemoveLast();
                if (_pending.TryRemove(old, out var dropped)) { _cache.TryRemove(old, out _); dropped.TrySetResult(null); }
            }
        }
        _ = Task.Run(Pump);
    }

    public void Invalidate() { _cache.Clear(); _failures.Clear(); } // quality or cookies changed

    public void Forget(string videoId) { _cache.TryRemove(videoId, out _); _pending.TryRemove(videoId, out _); _failures.TryRemove(videoId, out _); }

    /// <summary>Starts both Python processes early so their import cost is paid before the first click.</summary>
    public void Warm() { _click.Ensure(); _bg.Ensure(); }

    public void Dispose() { _click.Dispose(); _bg.Dispose(); }

    /// <summary>First finished attempt wins; a failure only counts when nobody else is still trying.</summary>
    private void Finish(string id, TaskCompletionSource<StreamInfo?> tcs, StreamInfo? s)
    {
        if (s != null) { _failures.TryRemove(id, out _); tcs.TrySetResult(s); }
        else if (!_onClickServer.ContainsKey(id) && !_running.ContainsKey(id)) tcs.TrySetResult(null);
        if (tcs.Task.IsCompleted)
        {
            _pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<StreamInfo?>>(id, tcs));
            if (tcs.Task.Result == null) _cache.TryRemove(new KeyValuePair<string, Task<StreamInfo?>>(id, tcs.Task));
        }
    }

    private readonly ConcurrentDictionary<string, byte> _running = new();

    private async Task Pump()
    {
        while (true)
        {
            if (!await _slots.WaitAsync(0)) return;
            string? id;
            lock (_qLock)
            {
                id = _queue.First?.Value;
                if (id != null) _queue.RemoveFirst();
            }
            if (id == null || !_pending.TryGetValue(id, out var tcs)) { _slots.Release(); if (id == null) return; continue; }
            _running[id] = 0;
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                StreamInfo? s = null;
                try { s = _bg.Ensure() ? await ViaServerAsync(_bg, id, null) : await ViaExeAsync(id); }
                finally
                {
                    _running.TryRemove(id, out _);
                    Paths.Log($"resolve {id} {(s != null ? "ok" : "fail")} {sw.ElapsedMilliseconds} ms");
                    Finish(id, tcs, s);
                    _slots.Release();
                    _ = Task.Run(Pump);
                }
            });
        }
    }

    /// <summary>Tells YouTube the video was watched (history and recommendations), off the critical path.</summary>
    public void MarkWatched(string videoId)
    {
        if (!AppSettings.Current.MarkWatched || !CookieStore.IsLoggedIn || videoId.Length != 11) return;
        _ = Task.Run(() =>
        {
            if (!_bg.Ensure()) return;
            _bg.Tell(new JsonObject
            {
                ["url"] = "https://www.youtube.com/watch?v=" + videoId, ["format"] = Format, ["cookies"] = Paths.Cookies, ["mark"] = true,
            });
        });
    }

    /// <summary>
    /// A fresh link for exactly these formats: used to continue a stream that is partly in the disk cache
    /// (a new link for the same itag continues the same file).
    /// </summary>
    public Task<StreamInfo?> GetExactAsync(string videoId, int vitag, int aitag)
        => Task.Run(() => _click.Ensure() ? ViaServerAsync(_click, videoId, aitag > 0 ? $"{vitag}+{aitag}" : vitag.ToString()) : Task.FromResult<StreamInfo?>(null));

    // ---------------- backends ----------------

    private async Task<StreamInfo?> ViaServerAsync(PyServer srv, string id, string? format)
    {
        var req = new JsonObject
        {
            ["url"] = "https://www.youtube.com/watch?v=" + id,
            ["format"] = format ?? Format,
            ["cookies"] = CookieStore.IsLoggedIn ? Paths.Cookies : null,
            ["proxy"] = string.IsNullOrWhiteSpace(AppSettings.Current.Proxy) ? null : AppSettings.Current.Proxy,
        };
        try
        {
            var n = await srv.Ask(req, id);
            if (n["ok"]?.GetValue<bool>() != true)
            {
                var error = n.S("error") ?? "YouTube не вернул ссылку на видео";
                _failures[id] = error;
                Paths.Log($"resolve {id}: {error}");
                return null;
            }
            return ToStream(id, n);
        }
        catch (Exception e)
        {
            Paths.Log($"resolve {id}: {e.Message}");
            return format == null ? await ViaExeAsync(id) : null;
        }
    }

    private async Task<StreamInfo?> ViaExeAsync(string id)
    {
        var psi = new ProcessStartInfo(Paths.YtDlp)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--no-warnings", "--no-playlist", "-f", Format, "--extractor-args", "youtube:skip=hls,translated_subs",
                     "--cache-dir", Paths.YtCache, "-j" }) psi.ArgumentList.Add(a);
        foreach (var a in YtDlp.CommonArgs(forPlayer: false).Where(a => a != "--impersonate" && a != "chrome")) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("https://www.youtube.com/watch?v=" + id);
        try
        {
            using var p = Process.Start(psi)!;
            // nobody reads the errors, but an unread pipe fills up and blocks the process; and never wait forever
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string json;
            try
            {
                json = await p.StandardOutput.ReadToEndAsync(limit.Token);
                await p.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                Paths.Log($"resolve {id}: yt-dlp gave no answer in 60 s");
                return null;
            }
            if (p.ExitCode != 0 || json.Length == 0) return null;
            var info = JsonNode.Parse(json)!;
            var n = new JsonObject { ["title"] = info.S("title"), ["duration"] = info["duration"]?.DeepClone() };
            if (info["requested_formats"] is JsonArray rf && rf.Count > 0)
            {
                n["video"] = rf[0].S("url");
                n["audio"] = rf.Count > 1 ? rf[1].S("url") : null;
                n["video_avail"] = rf[0]?["available_at"]?.DeepClone();
                n["audio_avail"] = rf.Count > 1 ? rf[1]?["available_at"]?.DeepClone() : null;
                n["headers"] = rf[0]?["http_headers"]?.DeepClone();
            }
            else
            {
                n["video"] = info.S("url");
                n["headers"] = info["http_headers"]?.DeepClone();
            }
            return ToStream(id, n);
        }
        catch (Exception e)
        {
            Paths.Log("yt-dlp exe resolve: " + e.Message);
            return null;
        }
    }

    private static StreamInfo? ToStream(string id, JsonNode n)
    {
        var video = n.S("video");
        if (video == null) return null;
        var headers = new Dictionary<string, string>();
        if (n["headers"] is JsonObject h)
            foreach (var kv in h) if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s)) headers[kv.Key] = s;
        double dur = 0;
        try { dur = n["duration"]?.GetValue<double>() ?? 0; } catch { }
        // mpv talks to our local proxy, which handles available_at and bounded ranges (see StreamProxy)
        var proxy = Player.StreamProxy.Instance;
        var audio = n.S("audio");
        int vitag = Player.StreamProxy.ItagOf(video), aitag = audio != null ? Player.StreamProxy.ItagOf(audio) : 0;
        static long Clen(string url) => long.TryParse(System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["clen"], out var l) ? l : 0;
        // same itag but another length means another encode: it cannot continue the file that is already playing or cached
        if (proxy.LengthOf(id, vitag) is > 0 and var have && Clen(video) is > 0 and var got && have != got)
        {
            Paths.Log($"resolve {id}: itag {vitag} length changed ({have} -> {got})");
            return null;
        }
        // the cache index knows the video before any of its bytes land on disk (zero plays: cleaned up first)
        if (Db.CacheVideo(id) is not { } known || known.Vitag != vitag || known.Aitag != aitag)
            MediaCache.NoteSeen(id, AppSettings.Current.PlayerQuality, vitag, aitag);
        var localVideo = proxy.Register(video, Long(n, "video_avail"), headers, 1024 * 1024, id);
        var localAudio = audio != null ? proxy.Register(audio, Long(n, "audio_avail"), headers, 256 * 1024, id) : null;
        _ = proxy.PrefillAsync(localVideo);
        if (localAudio != null) _ = proxy.PrefillAsync(localAudio);
        return new StreamInfo(id, localVideo, localAudio, headers, n.S("title"), dur, DateTime.UtcNow) { Vitag = vitag, Aitag = aitag };
    }

    private static long Long(JsonNode n, string key)
    {
        try { return (long)(n[key]?.GetValue<double>() ?? 0); } catch { return 0; }
    }

    // ---------------- python provisioning ----------------

    /// <summary>Creates/updates the private Python env with yt-dlp in the background. Daily pip -U keeps up with YouTube.</summary>
    public static async Task ProvisionAsync()
    {
        try
        {
            var python = Paths.FindOnPath("python.exe");
            if (python == null || python.Contains("WindowsApps")) return;
            if (!File.Exists(Paths.PyExe))
            {
                await Run(python, "-m", "venv", Paths.PyEnv);
                await Run(Paths.PyExe, "-m", "pip", "install", "-q", "--disable-pip-version-check", "yt-dlp[default]");
            }
            else if ((DateTime.UtcNow - AppSettings.Current.LastPyUpdate).TotalHours > 20)
            {
                await Run(Paths.PyExe, "-m", "pip", "install", "-q", "--disable-pip-version-check", "-U", "yt-dlp[default]");
                AppSettings.Current.LastPyUpdate = DateTime.UtcNow;
                AppSettings.Current.Save();
            }
        }
        catch (Exception e) { Paths.Log("python provision: " + e.Message); }
    }

    private static async Task Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var e = (await err).Trim();
        Paths.Log($"{Path.GetFileName(exe)} {string.Join(' ', args)} -> {p.ExitCode} {e[..Math.Min(200, e.Length)]}");
    }
}
