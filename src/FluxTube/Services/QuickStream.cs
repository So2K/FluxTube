using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluxTube.Player;

namespace FluxTube.Services;

/// <summary>
/// Stream links from one direct player request, no yt-dlp on the way:
///  1. as the Vision Pro client: plain links to every format that serve the whole stream at full speed, with no
///     JavaScript challenge, no token and no pre-roll ad delay. One request, well under a second. This is the normal path.
///  2. as the Quest client, when the first refuses a video: links that serve only the beginning of each stream
///     (4-20 MB). Playback starts on them, and the proper link for the same format from yt-dlp continues the same file.
/// Both are the same client identities yt-dlp itself uses. yt-dlp stays as the last resort.
/// </summary>
public static class QuickStream
{
    private sealed record Client(string Name, int Id, string Version, string UA, string DeviceMake, string DeviceModel, string OsName, string OsVersion, int? AndroidSdk, bool WholeStream);

    private static readonly Client Vision = new("VISIONOS", 101, "1.02",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
        "Apple", "RealityDevice17,1", "visionOS", "26.5.23O471", null, WholeStream: true);

    private static readonly Client Quest = new("ANDROID_VR", 28, "1.65.10",
        "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
        "Oculus", "Quest 3", "Android", "12L", 32, WholeStream: false);

    private static readonly HttpClient Http = Make();
    private static readonly ConcurrentDictionary<string, Task<StreamInfo?>> _cache = new();
    private static readonly object _visitorLock = new();
    private static Task<string?>? _visitor;
    private static DateTime _visitorAt;

    private static HttpClient Make()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All, UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(4), EnableMultipleHttp2Connections = true,
        };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy)) { h.Proxy = new WebProxy(AppSettings.Current.Proxy); h.UseProxy = true; }
        return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8), DefaultRequestVersion = HttpVersion.Version20 };
    }

    /// <summary>Gets the anonymous visitor id and opens the connection ahead of the first click.</summary>
    public static void Warm() => _ = VisitorAsync();

    private static Task<string?> VisitorAsync()
    {
        lock (_visitorLock)
        {
            if (_visitor != null && (DateTime.UtcNow - _visitorAt).TotalHours < 5 && !_visitor.IsFaulted && !(_visitor.IsCompleted && _visitor.Result == null))
                return _visitor;
            _visitorAt = DateTime.UtcNow;
            return _visitor = Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/");
                    req.Headers.TryAddWithoutValidation("User-Agent", InnerTube.UserAgent);
                    req.Headers.TryAddWithoutValidation("Accept-Language", "ru,en;q=0.8");
                    req.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI");
                    using var resp = await Http.SendAsync(req);
                    var html = await resp.Content.ReadAsStringAsync();
                    const string mark = "\"VISITOR_DATA\":\"";
                    var i = html.IndexOf(mark, StringComparison.Ordinal);
                    if (i < 0) { Paths.Log($"quick visitor: no VISITOR_DATA in {html.Length} chars, http {(int)resp.StatusCode}"); return null; }
                    i += mark.Length;
                    var end = html.IndexOf('"', i);
                    return end > i ? html[i..end] : null;
                }
                catch (Exception e) { Paths.Log("quick visitor: " + e.Message); return (string?)null; }
            });
        }
    }

    private static bool Good(Task<StreamInfo?> t) => !(t.IsCompleted && (t.IsFaulted || t.IsCanceled || t.Result == null || (DateTime.UtcNow - t.Result.ResolvedAt).TotalHours >= 4));

    public static StreamInfo? TryGet(string videoId)
        => _cache.TryGetValue(videoId, out var t) && t.IsCompletedSuccessfully && Good(t) ? t.Result : null;

    private static readonly SemaphoreSlim Gate = new(3, 3);
    private static readonly ConcurrentDictionary<string, byte> Waiting = new();

    private static void TrimCache<T>(ConcurrentDictionary<string, T> dict, int cap)
    {
        if (dict.Count <= cap) return;
        int remove = dict.Count - cap * 3 / 4;
        foreach (var k in dict.Keys)
        {
            if (remove-- <= 0) break;
            dict.TryRemove(k, out _);
        }
    }

    /// <summary>Background hint (a card in view, a card under the pointer). Bounded concurrency so scrolling never storms.</summary>
    public static void Prefetch(string videoId)
    {
        if (!AppSettings.Current.QuickStart || videoId.Length != 11) return;
        if (Waiting.Count >= 6) return;
        if (_cache.TryGetValue(videoId, out var t) && Good(t)) return;
        TrimCache(_cache, 240);
        if (!Waiting.TryAdd(videoId, 0)) return;
        _cache[videoId] = Task.Run(async () =>
        {
            await Gate.WaitAsync();
            try
            {
                Waiting.TryRemove(videoId, out _);
                return await ResolveAsync(videoId, 0, 0);
            }
            finally { Gate.Release(); }
        });
    }

    public static void Invalidate() => _cache.Clear(); // quality changed

    // ---- subtitles and dubbed audio: listed in the player answer we already have ----
    public sealed record CaptionTrack(string Lang, string Name, bool Auto, string Url);
    public sealed record AudioTrack(string Id, string Lang, string Name, bool IsDefault, string Url);
    public sealed class Extras
    {
        public List<CaptionTrack> Captions { get; } = [];
        public List<AudioTrack> Audio { get; } = [];
        public string UA = "";
    }

    private static readonly ConcurrentDictionary<string, Extras> _extras = new();

    public static Extras? ExtrasOf(string videoId) => _extras.TryGetValue(videoId, out var e) ? e : null;

    private static void Remember(Client c, string id, JsonNode n)
    {
        if (!c.WholeStream) return; // the other client's links stop after the beginning and it lists no dubs
        var x = new Extras { UA = c.UA };
        if (n.P("captions", "playerCaptionsTracklistRenderer", "captionTracks") is JsonArray caps)
            foreach (var t in caps)
                if (t.S("baseUrl") is { } url)
                    x.Captions.Add(new CaptionTrack(t.S("languageCode") ?? "", t.P("name").Text() ?? t.S("languageCode") ?? "?", t.S("kind") == "asr", url));
        if (n.P("streamingData", "adaptiveFormats") is JsonArray formats)
        {
            // one entry per language: Opus when there is one, otherwise the best AAC
            foreach (var g in formats.Where(f => IsAudio(f) && f?["audioTrack"] != null && f.S("url") != null && f?["isDrc"] == null).GroupBy(f => f.S("audioTrack", "id") ?? ""))
            {
                var best = g.OrderByDescending(f => Num(f, "itag") == 251 ? 2 : Num(f, "itag") == 140 ? 1 : 0).ThenByDescending(f => Num(f, "bitrate")).First();
                var isDef = best.P("audioTrack", "audioIsDefault") is JsonValue dv && dv.TryGetValue<bool>(out var d) && d;
                x.Audio.Add(new AudioTrack(g.Key, g.Key.Split('.')[0], best.S("audioTrack", "displayName") ?? g.Key, isDef, best.S("url")!));
            }
            x.Audio.Sort((a, b) => a.IsDefault != b.IsDefault ? (a.IsDefault ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        }
        if (_extras.Count > 200) _extras.Clear();
        _extras[id] = x;
    }

    /// <summary>A dubbed audio track as a local address the player can add (not kept on disk: it shares its format number with the original).</summary>
    public static string AudioUrl(Extras x, AudioTrack t)
        => StreamProxy.Instance.Register(t.Url, 0, new Dictionary<string, string> { ["User-Agent"] = x.UA }, 128 * 1024, null);

    /// <summary>
    /// Downloads one subtitle track (optionally machine-translated by YouTube) and writes it as an .srt file.
    /// YouTube's own formats carry rolling duplicates for automatic captions; plain timed lines are what a player wants.
    /// </summary>
    public static async Task<string?> CaptionFileAsync(string videoId, Extras x, CaptionTrack t, string? translateTo)
    {
        var dir = Path.Combine(Paths.Data, "subs");
        var file = Path.Combine(dir, $"{videoId}.{t.Lang}{(t.Auto ? ".auto" : "")}{(translateTo != null ? "." + translateTo : "")}.srt");
        if (File.Exists(file) && new FileInfo(file).Length > 0) return file;

        async Task<string?> Get(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", x.UA);
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode != 429) Paths.Log($"captions {videoId}: http {(int)resp.StatusCode}");
            return null;
        }

        List<(double Start, double End, string Text)>? cues = null;
        // YouTube's own translation first: it answers only now and then (it wants a token we do not have)
        if (translateTo != null && await Get(t.Url + "&fmt=srv1&tlang=" + translateTo) is { } done) cues = ParseCues(done);
        if (cues == null || cues.Count == 0)
        {
            var xml = await Get(t.Url + "&fmt=srv1");
            if (xml == null) return null;
            cues = ParseCues(xml);
            if (cues.Count == 0) { Paths.Log($"captions {videoId}: nothing in {xml.Length} chars"); return null; }
            if (t.Auto) cues = Merge(cues);
            if (translateTo != null && !await TranslateAsync(cues, translateTo)) { Paths.Log($"captions {videoId}: translation failed"); return null; }
        }
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(file, WriteSrt(cues), Encoding.UTF8);
        // a bounded folder: the oldest files go once there are many
        try { foreach (var old in new DirectoryInfo(dir).GetFiles("*.srt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(60)) old.Delete(); } catch { }
        return file;
    }

    /// <summary>Automatic captions come two or three words at a time: joined into lines that read (and translate) as phrases.</summary>
    private static List<(double Start, double End, string Text)> Merge(List<(double Start, double End, string Text)> cues)
    {
        var res = new List<(double, double, string)>();
        int i = 0;
        while (i < cues.Count)
        {
            var (start, end, text) = cues[i++];
            while (i < cues.Count && text.Length + cues[i].Text.Length < 84 && cues[i].Start - start < 5.5 && cues[i].Start - end < 1.2)
            {
                text += " " + cues[i].Text;
                end = cues[i].End;
                i++;
            }
            res.Add((start, end, text.Replace('\n', ' ')));
        }
        return res;
    }

    /// <summary>
    /// The lines through Google Translate's public endpoint, a couple of thousand characters at a time, one line per
    /// line: the answer keeps the line breaks, so each translated line goes back to its own time.
    /// </summary>
    private static async Task<bool> TranslateAsync(List<(double Start, double End, string Text)> cues, string to)
    {
        int at = 0;
        while (at < cues.Count)
        {
            int n = 0, chars = 0;
            while (at + n < cues.Count && (n == 0 || chars + cues[at + n].Text.Length < 1800)) { chars += cues[at + n].Text.Length + 1; n++; }
            var q = string.Join('\n', cues.Skip(at).Take(n).Select(c => c.Text.Replace('\n', ' ')));
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://translate.googleapis.com/translate_a/single")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client"] = "gtx", ["sl"] = "auto", ["tl"] = to, ["dt"] = "t", ["q"] = q }),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", InnerTube.UserAgent);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;
            var d = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            if (d?[0] is not JsonArray segs) return false;
            var sb = new StringBuilder();
            foreach (var s in segs) if (s?[0] is JsonValue v && v.TryGetValue<string>(out var piece)) sb.Append(piece);
            var lines = sb.ToString().Split('\n');
            for (int k = 0; k < n && k < lines.Length; k++)
                if (lines[k].Trim().Length > 0) cues[at + k] = (cues[at + k].Start, cues[at + k].End, lines[k].Trim());
            at += n;
        }
        return true;
    }

    /// <summary>Both shapes YouTube answers with: &lt;text start dur&gt; in seconds, or &lt;p t d&gt; in milliseconds.</summary>
    private static List<(double Start, double End, string Text)> ParseCues(string xml)
    {
        var cues = new List<(double Start, double End, string Text)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml,
                     @"<(text|p)\b([^>]*)>(.*?)</\1>", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var attrs = m.Groups[2].Value;
            double A(string name)
            {
                var a = System.Text.RegularExpressions.Regex.Match(attrs, "\\b" + name + "=\"([0-9.]+)\"");
                return a.Success && double.TryParse(a.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
            }
            double start, dur;
            if (m.Groups[1].Value == "text") { start = A("start"); dur = A("dur"); }
            else { start = A("t") / 1000; dur = A("d") / 1000; }
            if (start < 0) continue;
            var text = System.Text.RegularExpressions.Regex.Replace(m.Groups[3].Value, "<[^>]+>", "");
            text = WebUtility.HtmlDecode(WebUtility.HtmlDecode(text)).Replace("\r", "").Trim();
            if (text.Length == 0) continue;
            cues.Add((start, start + (dur > 0 ? dur : 3), text));
        }
        return cues;
    }

    private static string WriteSrt(List<(double Start, double End, string Text)> cues)
    {
        var sb = new StringBuilder();
        static string T(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}"; }
        for (int i = 0; i < cues.Count; i++)
        {
            // automatic captions overlap the next line: each one ends where the next begins
            var end = i + 1 < cues.Count ? Math.Min(cues[i].End, Math.Max(cues[i].Start + 0.3, cues[i + 1].Start)) : cues[i].End;
            sb.Append(i + 1).Append('\n').Append(T(cues[i].Start)).Append(" --> ").Append(T(end)).Append('\n').Append(cues[i].Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static void Forget(string videoId)
    {
        _cache.TryRemove(videoId, out _);
        _specs.TryRemove(videoId, out _);
        _extras.TryRemove(videoId, out _);
    }

    // the player response also carries the seek-bar frame previews ("storyboards")
    private static readonly ConcurrentDictionary<string, string> _specs = new();

    public static string? StoryboardSpec(string videoId) => _specs.TryGetValue(videoId, out var s) && s.Length > 0 ? s : null;

    private static readonly ConcurrentDictionary<string, Task> _specTasks = new();

    /// <summary>
    /// Makes sure we know where the seek-bar frame previews are. A video played from the disk cache may never
    /// have needed a player request, so this asks for one (half a second) and keeps only the storyboard line.
    /// </summary>
    public static Task EnsureSpecAsync(string videoId)
    {
        if (_specs.ContainsKey(videoId)) return Task.CompletedTask;
        return _specTasks.GetOrAdd(videoId, id => Task.Run(async () =>
        {
            try
            {
                var visitor = await VisitorAsync();
                if (visitor == null) return;
                var n = await PlayerAsync(Vision, id, visitor);
                if (n != null && n.S("playabilityStatus", "status") == "OK")
                {
                    Remember(Vision, id, n);
                    TrimCache(_specs, 180);
                    _specs[id] = n.S("storyboards", "playerStoryboardSpecRenderer", "spec") ?? "";
                }
            }
            catch (Exception e) { Paths.Log($"storyboard spec {id}: {e.Message}"); }
            finally { _specTasks.TryRemove(id, out _); }
        }));
    }

    /// <summary>Known to have no previews (asked and answered), as opposed to not asked yet.</summary>
    public static bool SpecKnown(string videoId) => _specs.ContainsKey(videoId);

    /// <summary>The user clicked: answered at once, never queued behind background hints.</summary>
    public static Task<StreamInfo?> GetAsync(string videoId)
    {
        // a hint that is still waiting for its turn does not count: do it now
        if (_cache.TryGetValue(videoId, out var t) && Good(t) && !Waiting.ContainsKey(videoId)) return t;
        TrimCache(_cache, 240);
        return _cache[videoId] = Task.Run(() => ResolveAsync(videoId, 0, 0));
    }

    /// <summary>
    /// A link for exactly these formats, for a stream that is partly in the disk cache: the same videoId+itag continues the same file.
    /// </summary>
    public static Task<StreamInfo?> GetExactAsync(string videoId, int vitag, int aitag) => Task.Run(() => ResolveAsync(videoId, vitag, aitag));

    private static long Num(JsonNode? f, string k)
    {
        try
        {
            var v = f?[k];
            if (v == null) return 0;
            return v.GetValueKind() == System.Text.Json.JsonValueKind.String ? long.Parse(v.GetValue<string>()) : v.GetValue<long>();
        }
        catch { return 0; }
    }

    // the order yt-dlp itself prefers, so that a later yt-dlp link is usually for the very same format
    private static int CodecRank(JsonNode? f)
    {
        var m = f.S("mimeType") ?? "";
        return m.Contains("av01") ? 3 : m.Contains("vp09") || m.Contains("vp9") ? 2 : 1;
    }

    private static bool IsAudio(JsonNode? f) => f.S("mimeType")?.StartsWith("audio/") == true;

    private static async Task<JsonNode?> PlayerAsync(Client c, string id, string visitor)
    {
        var client = new JsonObject
        {
            ["clientName"] = c.Name, ["clientVersion"] = c.Version, ["deviceMake"] = c.DeviceMake, ["deviceModel"] = c.DeviceModel,
            ["userAgent"] = c.UA, ["osName"] = c.OsName, ["osVersion"] = c.OsVersion,
            ["hl"] = AppSettings.Current.Hl, ["timeZone"] = "UTC", ["utcOffsetMinutes"] = 0, ["visitorData"] = visitor,
        };
        if (c.AndroidSdk is { } sdk) client["androidSdkVersion"] = sdk;
        var body = new JsonObject { ["videoId"] = id, ["contentCheckOk"] = true, ["racyCheckOk"] = true, ["context"] = new JsonObject { ["client"] = client } };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
        req.Headers.TryAddWithoutValidation("User-Agent", c.UA);
        req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", c.Id.ToString());
        req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", c.Version);
        req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<StreamInfo?> ResolveAsync(string id, int wantV, int wantA)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var visitor = await VisitorAsync();
            if (visitor == null) { Paths.Log($"quick {id}: no visitor id"); return null; }
            // the whole-stream client first; the beginning-only one is asked only when it refuses
            foreach (var client in wantV == 0 ? new[] { Vision, Quest } : [Vision])
            {
                JsonNode? n;
                try { n = await PlayerAsync(client, id, visitor); } catch (Exception e) { Paths.Log($"quick {id} {client.Name}: {e.Message}"); continue; }
                if (n == null) continue;
                var apiMs = sw.ElapsedMilliseconds;
                var s = await FromResponse(client, id, n, wantV, wantA);
                if (s != null)
                {
                    Paths.Log($"quick {id} ok {sw.ElapsedMilliseconds} ms ({client.Name} api {apiMs} ms, itags {s.Vitag}+{s.Aitag}{(s.Preview ? ", beginning only" : "")})");
                    return s;
                }
            }
            Paths.Log($"quick {id}: no client gave a playable stream ({sw.ElapsedMilliseconds} ms)");
            return null;
        }
        catch (Exception e)
        {
            Paths.Log($"quick {id}: {e.Message}");
            return null;
        }
    }

    private static async Task<StreamInfo?> FromResponse(Client c, string id, JsonNode n, int wantV, int wantA)
    {
        if (n.S("playabilityStatus", "status") != "OK") { Paths.Log($"quick {id} {c.Name}: {n.S("playabilityStatus", "status")} {n.S("playabilityStatus", "reason")}"); return null; }
        if (n.S("storyboards", "playerStoryboardSpecRenderer", "spec") is { Length: > 0 } spec)
        {
            if (_specs.Count > 200) _specs.Clear();
            _specs[id] = spec;
        }
        Remember(c, id, n);
        if (n.P("streamingData", "adaptiveFormats") is not JsonArray formats) return null;
        double.TryParse(n.S("videoDetails", "lengthSeconds"), out var dur);
        var title = n.S("videoDetails", "title");
        var headers = new Dictionary<string, string> { ["User-Agent"] = c.UA };

        var quality = AppSettings.Current.PlayerQuality;
        var cap = int.TryParse(quality, out var q) ? q : 100000;
        var usable = formats.Where(f => f.S("url") != null && Num(f, "contentLength") > 0 && f?["isDrc"] == null).ToList();
        List<JsonNode?> videos, audios;
        if (wantV > 0)
        {
            videos = wantA > 0 ? usable.Where(f => Num(f, "itag") == wantV).Take(1).ToList() : [];
            var prefLangExact = AppSettings.Current.AudioLanguage;
            audios = usable.Where(f => Num(f, "itag") == (wantA > 0 ? wantA : wantV))
                .OrderByDescending(f =>
                {
                    if (!string.IsNullOrEmpty(prefLangExact))
                    {
                        var lang = (f.S("audioTrack", "id") ?? "").Split('.')[0];
                        if (lang.Equals(prefLangExact, StringComparison.OrdinalIgnoreCase)
                            || lang.StartsWith(prefLangExact + "-", StringComparison.OrdinalIgnoreCase))
                            return 2;
                    }
                    return f?["audioTrack"] == null || (f.P("audioTrack", "audioIsDefault") is JsonValue ov && ov.TryGetValue<bool>(out var od) && od) ? 1 : 0;
                }).Take(1).ToList();
            if (audios.Count == 0 || (wantA > 0 && videos.Count == 0)) return null;
        }
        else
        {
            videos = usable.Where(f => !IsAudio(f) && Math.Min(Num(f, "height"), Num(f, "width")) <= cap && f.S("qualityLabel")?.Contains("HDR") != true)
                .OrderByDescending(f => Num(f, "height")).ThenByDescending(f => Num(f, "fps")).ThenByDescending(CodecRank).ThenByDescending(f => Num(f, "bitrate"))
                .Take(3).ToList();
            var prefLang = AppSettings.Current.AudioLanguage;
            var allAudio = usable.Where(IsAudio).ToList();
            List<JsonNode?> preferred = [];
            if (!string.IsNullOrEmpty(prefLang))
            {
                preferred = allAudio.Where(f =>
                {
                    var tid = f.S("audioTrack", "id") ?? "";
                    var lang = tid.Split('.')[0];
                    return lang.Equals(prefLang, StringComparison.OrdinalIgnoreCase)
                        || lang.StartsWith(prefLang + "-", StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }
            if (preferred.Count == 0)
                preferred = allAudio.Where(f => f?["audioTrack"] == null || (f.P("audioTrack", "audioIsDefault") is JsonValue dv && dv.TryGetValue<bool>(out var isDef) && isDef)).ToList();
            if (preferred.Count == 0) preferred = allAudio;
            audios = preferred
                .OrderByDescending(f => Num(f, "itag") == 251 ? 2 : Num(f, "itag") == 140 ? 1 : 0).ThenByDescending(f => Num(f, "bitrate"))
                .Take(2).ToList();
            if (quality == "audio") videos.Clear();
        }
        var audioOnly = videos.Count == 0;
        if (audios.Count == 0 || (audioOnly && quality != "audio" && wantV == 0)) return null;

        var proxy = StreamProxy.Instance;
        // the first candidate whose beginning YouTube actually lets through
        async Task<(string Local, int Itag)?> Start(List<JsonNode?> candidates, int head)
        {
            foreach (var f in candidates)
            {
                var url = f.S("url")!;
                var itag = (int)Num(f, "itag");
                // a stream already known under this itag must be the same bytes, or the link cannot continue it
                if (proxy.LengthOf(id, itag) is > 0 and var have && have != Num(f, "contentLength")) continue;
                var local = c.WholeStream ? proxy.Register(url, 0, headers, head, id, unproven: true) : proxy.RegisterStarter(url, headers, head, id);
                await proxy.PrefillAsync(local);
                if (proxy.HeadReady(local)) { proxy.Confirm(local); return (local, itag); }
            }
            return null;
        }
        // enough for the headers and the first second or two; the rest streams at 25 MB/s once it plays
        var audioTask = Start(audios, 128 * 1024);
        var videoTask = audioOnly ? Task.FromResult<(string Local, int Itag)?>(null) : Start(videos, 512 * 1024);
        await Task.WhenAll(audioTask, videoTask);
        var a = audioTask.Result;
        var v = videoTask.Result;
        if (a == null || (v == null && !audioOnly)) return null;

        int vitag = v?.Itag ?? a.Value.Itag, aitag = v != null ? a.Value.Itag : 0;
        if (Db.CacheVideo(id) is not { } known || known.Vitag != vitag || known.Aitag != aitag) MediaCache.NoteSeen(id, quality, vitag, aitag);
        return new StreamInfo(id, v?.Local ?? a.Value.Local, v != null ? a.Value.Local : null, headers, title, dur, DateTime.UtcNow)
            { Vitag = vitag, Aitag = aitag, Preview = !c.WholeStream };
    }
}
