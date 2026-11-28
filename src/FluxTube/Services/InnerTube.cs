using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluxTube.Models;

namespace FluxTube.Services;

/// <summary>
/// YouTube's own web API (the one youtube.com calls), spoken with the same headers, client context,
/// visitor id and auth hash as desktop Chrome. The session is bootstrapped the way a browser does it:
/// load the page, read ytcfg, then call youtubei/v1.
/// </summary>
public sealed class InnerTube
{
    public static InnerTube Instance { get; } = new();

    private sealed record SessionState(JsonObject Context, string ClientVersion, string? VisitorData, string SessionIndex, string? DelegatedSessionId, bool LoggedIn, DateTime BootAt);

    private const string Origin = "https://www.youtube.com";
    private readonly HttpClient _http;
    private volatile SessionState? _session;
    private JsonNode? _bootInitialData;
    private readonly SemaphoreSlim _bootLock = new(1, 1);

    public static int ChromeMajor { get; set; } = 142;
    public static string UserAgent => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromeMajor}.0.0.0 Safari/537.36";

    public bool LoggedIn => _session?.LoggedIn ?? false;

    private InnerTube()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false, // cookies.txt is the source of truth, see CookieStore
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
        };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy))
        {
            handler.Proxy = new WebProxy(AppSettings.Current.Proxy);
            handler.UseProxy = true;
        }
        _http = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromSeconds(25),
        };
        CookieStore.Changed += ResetSession;
    }

    // ---------------- headers ----------------

    private string SecChUa => $"\"Chromium\";v=\"{ChromeMajor}\", \"Google Chrome\";v=\"{ChromeMajor}\", \"Not_A Brand\";v=\"99\"";

    private void BrowserHeaders(HttpRequestMessage req, bool navigation)
    {
        var h = req.Headers;
        h.TryAddWithoutValidation("sec-ch-ua", SecChUa);
        h.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        h.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        if (navigation)
        {
            h.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            h.TryAddWithoutValidation("User-Agent", UserAgent);
            h.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
            h.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            h.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            h.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            h.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        }
        else
        {
            h.TryAddWithoutValidation("User-Agent", UserAgent);
            h.TryAddWithoutValidation("Accept", "*/*");
            h.TryAddWithoutValidation("Origin", Origin);
            h.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            h.TryAddWithoutValidation("Sec-Fetch-Mode", "same-origin");
            h.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        }
        h.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        h.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        var cookie = CookieStore.HeaderFor("www.youtube.com");
        if (string.IsNullOrEmpty(cookie)) cookie = "SOCS=CAI; PREF=hl=" + AppSettings.Current.Hl;
        h.TryAddWithoutValidation("Cookie", cookie);
    }

    private static string AcceptLanguage => AppSettings.Current.Hl switch
    {
        "ru" => "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7",
        var hl => $"{hl},en-US;q=0.9,en;q=0.8",
    };

    // ---------------- bootstrap ----------------

    private async Task<SessionState> EnsureBootAsync(CancellationToken ct)
    {
        var cur = _session;
        if (cur != null && (DateTime.UtcNow - cur.BootAt).TotalMinutes < 30) return cur;
        await _bootLock.WaitAsync(ct);
        try
        {
            cur = _session;
            if (cur != null && (DateTime.UtcNow - cur.BootAt).TotalMinutes < 30) return cur;
            var html = await GetHtmlAsync(Origin + "/", ct);
            return ParseBoot(html);
        }
        finally { _bootLock.Release(); }
    }

    private SessionState ParseBoot(string html)
    {
        var cfgText = J.ExtractObject(html, "ytcfg.set({") is { } t ? t : null;
        // ytcfg.set is called several times; the big one contains INNERTUBE_CONTEXT
        var idx = html.IndexOf("\"INNERTUBE_CONTEXT\"", StringComparison.Ordinal);
        if (idx > 0)
        {
            var start = html.LastIndexOf("ytcfg.set(", idx, StringComparison.Ordinal);
            if (start >= 0) cfgText = J.ExtractObject(html[start..], "ytcfg.set(");
        }
        if (cfgText == null) throw new InvalidOperationException("YouTube page without ytcfg");
        var cfg = JsonNode.Parse(cfgText)!;
        var ctx = cfg["INNERTUBE_CONTEXT"]?.DeepClone() as JsonObject
                  ?? throw new InvalidOperationException("no INNERTUBE_CONTEXT");
        var clientVer = cfg.S("INNERTUBE_CLIENT_VERSION") ?? _session?.ClientVersion ?? "2.20261112.00.00";
        var visitor = cfg.S("VISITOR_DATA") ?? ctx.S("client", "visitorData");
        var sessIdx = cfg.S("SESSION_INDEX") ?? "0";
        var delSess = cfg.S("DELEGATED_SESSION_ID");
        var logged = cfg["LOGGED_IN"]?.GetValue<bool>() ?? false;

        var client = (JsonObject)ctx["client"]!;
        client["hl"] = AppSettings.Current.Hl;
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Gl)) client["gl"] = AppSettings.Current.Gl;
        client["userAgent"] = UserAgent + ",gzip(gfe)";
        client["timeZone"] = IanaTimeZone();
        client["utcOffsetMinutes"] = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
        client["screenWidthPoints"] = 1920;
        client["screenHeightPoints"] = 1080;
        client["screenPixelDensity"] = 1;
        client["userInterfaceTheme"] = "USER_INTERFACE_THEME_DARK";
        ctx.Remove("clickTracking");
        ctx.Remove("adSignalsInfo");

        var dataText = J.ExtractObject(html, "var ytInitialData = ") ?? J.ExtractObject(html, "ytInitialData\"] = ");
        Volatile.Write(ref _bootInitialData, dataText != null ? JsonNode.Parse(dataText) : null);
        var state = new SessionState(ctx, clientVer, visitor, sessIdx, delSess, logged, DateTime.UtcNow);
        _session = state;
        return state;
    }

    private static string IanaTimeZone()
        => TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana! : "UTC";

    private async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        BrowserHeaders(req, navigation: true);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<JsonNode> PostAsync(string endpoint, JsonObject body, string referer, CancellationToken ct)
    {
        var sess = await EnsureBootAsync(ct);
        body["context"] = sess.Context.DeepClone();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Origin}/youtubei/v1/{endpoint}?prettyPrint=false");
        BrowserHeaders(req, navigation: false);
        var h = req.Headers;
        h.TryAddWithoutValidation("Referer", referer);
        h.TryAddWithoutValidation("X-Youtube-Client-Name", "1");
        h.TryAddWithoutValidation("X-Youtube-Client-Version", sess.ClientVersion);
        h.TryAddWithoutValidation("X-Origin", Origin);
        if (sess.VisitorData != null) h.TryAddWithoutValidation("X-Goog-Visitor-Id", sess.VisitorData);
        var auth = CookieStore.AuthorizationHeader(Origin);
        if (auth != null)
        {
            h.TryAddWithoutValidation("Authorization", auth);
            h.TryAddWithoutValidation("X-Goog-AuthUser", sess.SessionIndex);
            if (!string.IsNullOrEmpty(sess.DelegatedSessionId)) h.TryAddWithoutValidation("X-Goog-PageId", sess.DelegatedSessionId);
            h.TryAddWithoutValidation("X-Youtube-Bootstrap-Logged-In", "true");
        }
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Paths.Log($"innertube {endpoint} {(int)resp.StatusCode}: {text[..Math.Min(300, text.Length)]}");
            resp.EnsureSuccessStatusCode();
        }
        return JsonNode.Parse(text)!;
    }

    /// <summary>Forget the session and watch cache (account/language changed): the next request boots cleanly.</summary>
    public void ResetSession()
    {
        _session = null;
        Volatile.Write(ref _bootInitialData, null);
        _watch.Clear();
    }

    /// <summary>Opens the TLS/HTTP2 connection and reads ytcfg before the first page needs it.</summary>
    public async Task WarmAsync()
    {
        try { await EnsureBootAsync(CancellationToken.None); }
        catch (Exception e) { Paths.Log("warm: " + e.Message); }
    }

    /// <summary>Authenticated fallback to fetch a video's storyboard spec when anonymous VisionOS/Quest clients hit LOGIN_REQUIRED.</summary>
    public async Task<string?> StoryboardSpecAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            var d = await PostAsync("player", new JsonObject
            {
                ["videoId"] = videoId,
                ["contentCheckOk"] = true,
                ["racyCheckOk"] = true,
            }, $"{Origin}/watch?v={videoId}", ct);
            return d.S("storyboards", "playerStoryboardSpecRenderer", "spec")
                ?? d.S("storyboards", "playerLiveStoryboardSpecRenderer", "spec");
        }
        catch (Exception e)
        {
            Paths.Log($"innertube storyboard {videoId}: {e.Message}");
            return null;
        }
    }

    // ---------------- feeds ----------------

    public async Task<FeedPage> HomeAsync(CancellationToken ct = default)
    {
        await EnsureBootAsync(ct);
        // Use the exact 1-to-1 ytInitialData from https://www.youtube.com/ (identical ranking, personalization, and chips as desktop YouTube!)
        JsonNode? d = Interlocked.Exchange(ref _bootInitialData, null);
        if (d == null)
        {
            await _bootLock.WaitAsync(ct);
            try
            {
                d = Interlocked.Exchange(ref _bootInitialData, null);
                if (d == null)
                {
                    try
                    {
                        var html = await GetHtmlAsync(Origin + "/", ct);
                        ParseBoot(html);
                        d = Interlocked.Exchange(ref _bootInitialData, null);
                    }
                    catch { }
                }
            }
            finally { _bootLock.Release(); }
        }
        d ??= await PostAsync("browse", new JsonObject { ["browseId"] = "FEwhat_to_watch" }, Origin + "/", ct);
        var page = ParseFeed(d);
        if (page.Items.Count == 0)
        {
            d = await PostAsync("browse", new JsonObject { ["browseId"] = "FEwhat_to_watch" }, Origin + "/", ct);
            page = ParseFeed(d);
        }
        if (J.FindFirst(d, "feedFilterChipBarRenderer")?.P("contents") is JsonArray chips)
        {
            foreach (var ch in chips)
            {
                if (ch.P("chipCloudChipRenderer") is not JsonObject c) continue;
                var title = c.P("text").Text();
                if (string.IsNullOrWhiteSpace(title)) continue;
                var tok = c.S("navigationEndpoint", "continuationCommand", "token") ?? "";
                var sel = c["isSelected"]?.GetValue<bool>() == true;
                page.Chips.Add((title, tok, sel));
            }
        }
        return page;
    }

    /// <summary>
    /// The account's watch history as youtube.com/feed/history lists it, newest day first.
    /// Pass the token of the previous answer for the days after it.
    /// </summary>
    public async Task<(List<HistoryDay> Days, string? Next)> HistoryAsync(string? token, CancellationToken ct = default)
    {
        var d = await PostAsync("browse", token == null ? new JsonObject { ["browseId"] = "FEhistory" } : new JsonObject { ["continuation"] = token }, Origin + "/feed/history", ct);
        var days = new List<HistoryDay>();
        string? next = null;
        J.Walk(d, (key, o) =>
        {
            if (key is "secondaryContents" or "topbar" or "header" or "frameworkUpdates") return false;
            if (key == "continuationItemRenderer")
            {
                next = o.S("continuationEndpoint", "continuationCommand", "token") ?? next;
                return false;
            }
            if (key != "itemSectionRenderer") return true;
            var day = new HistoryDay { Title = o.P("header", "itemSectionHeaderRenderer", "title").Text() ?? "" };
            if (o.P("contents") is JsonArray items)
                foreach (var it in items)
                {
                    if (it.P("reelShelfRenderer", "items") is JsonArray reels)
                    {
                        foreach (var r in reels)
                            if (r.P("shortsLockupViewModel") is JsonObject s && FromShort(s) is { } sv) day.Shorts.Add(sv);
                    }
                    else if (it.P("lockupViewModel") is JsonObject l) { if (FromLockup(l) is { } v) day.Videos.Add(v); }
                    else if (it.P("videoRenderer") is JsonObject vr) { if (FromRenderer(vr, shortsToo: true) is { } v) day.Videos.Add(v); }
                }
            if (day.Shorts.Count + day.Videos.Count > 0) days.Add(day);
            return false;
        });
        return (days, next);
    }

    /// <summary>The untouched answer of a browse request (for looking at a page's shape).</summary>
    public Task<JsonNode> BrowseRawAsync(string browseId, CancellationToken ct = default)
        => PostAsync("browse", new JsonObject { ["browseId"] = browseId }, Origin + "/", ct);

    public async Task<FeedPage> BrowseAsync(string browseId, CancellationToken ct = default)
        => ParseFeed(await PostAsync("browse", new JsonObject { ["browseId"] = browseId }, Origin + "/", ct));

    /// <summary>Channel "Videos" tab.</summary>
    public async Task<FeedPage> ChannelVideosAsync(string channelId, CancellationToken ct = default)
        => ParseFeed(await PostAsync("browse", new JsonObject { ["browseId"] = channelId, ["params"] = "EgZ2aWRlb3PyBgQKAjoA" },
            $"{Origin}/channel/{channelId}/videos", ct));

    /// <summary>A channel page: header plus the home shelves (params null) or one tab's grid (params from Tabs).</summary>
    /// <summary>
    /// Fetches deep professional video analytics directly from YouTube player + next endpoints:
    /// exact views, publish UTC date, category, monetization signals (adPlacements), full adaptiveFormats capabilities
    /// (max res/FPS/HDR/AV1/VP9/H.264/audio tracks/captions), SEO tags, comments count, chapters, and 100-point Most Replayed heatmap!
    /// </summary>
    public async Task<VideoAnalyticsData> VideoAnalyticsAsync(string videoId, CancellationToken ct = default)
    {
        var res = new VideoAnalyticsData { VideoId = videoId };
        try
        {
            var playerTask = PostAsync("player", new JsonObject
            {
                ["videoId"] = videoId,
                ["contentCheckOk"] = true,
                ["racyCheckOk"] = true,
            }, $"{Origin}/watch?v={videoId}", ct);
            var nextTask = PostAsync("next", new JsonObject
            {
                ["videoId"] = videoId,
                ["contentCheckOk"] = true,
                ["racyCheckOk"] = true,
            }, $"{Origin}/watch?v={videoId}", ct);
            await Task.WhenAll(playerTask, nextTask);
            var d = await playerTask;
            var nd = await nextTask;

            var vd = d.P("videoDetails");
            var mf = d.P("microformat", "playerMicroformatRenderer");
            res.Title = vd.S("title") ?? mf.P("title").Text() ?? "";
            res.Channel = vd.S("author") ?? mf.S("ownerChannelName") ?? "";
            res.ChannelId = vd.S("channelId") ?? mf.S("externalChannelId");
            res.Description = vd.S("shortDescription") ?? mf.P("description").Text() ?? "";
            if (long.TryParse(vd.S("viewCount") ?? mf.S("viewCount"), out var vc)) res.Views = vc;
            if (double.TryParse(vd.S("lengthSeconds") ?? mf.S("lengthSeconds"), out var len)) res.DurationSec = len;
            res.PublishDate = mf.S("publishDate") ?? mf.S("uploadDate") ?? "";
            if (DateTime.TryParse(res.PublishDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var pdt))
                res.PublishedUtc = pdt;
            res.Category = mf.S("category") ?? "";
            res.IsLive = vd?["isLiveContent"]?.GetValue<bool>() ?? false;
            res.IsFamilySafe = mf?["isFamilySafe"]?.GetValue<bool>() ?? true;
            res.IsUnlisted = mf?["isUnlisted"]?.GetValue<bool>() ?? false;
            if (d["adPlacements"] is JsonArray adp && adp.Count > 0 || d["playerAds"] is JsonArray pads && pads.Count > 0)
                res.AdsDetected = true;
            else
                res.AdsDetected = false;

            if (vd?["keywords"] is JsonArray kw)
            {
                foreach (var k in kw)
                    if (k?.GetValue<string>() is { Length: > 0 } tag && res.Tags.Count < 30)
                        res.Tags.Add(tag);
            }

            // Full technical format audit from streamingData.adaptiveFormats
            if (d.P("streamingData", "adaptiveFormats") is JsonArray fmts)
            {
                var audioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in fmts)
                {
                    var mime = f.S("mimeType") ?? "";
                    int br = 0;
                    try { br = (int)((f?["bitrate"]?.GetValue<long>() ?? 0) / 1000); } catch { }
                    if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        int h = 0, fps = 0;
                        try { h = f?["height"]?.GetValue<int>() ?? 0; } catch { }
                        try { fps = f?["fps"]?.GetValue<int>() ?? 0; } catch { }
                        if (h > res.MaxHeight) res.MaxHeight = h;
                        if (fps > res.MaxFps) res.MaxFps = fps;
                        if (br > res.MaxVideoBitrateKbps) res.MaxVideoBitrateKbps = br;
                        if (mime.Contains("av01", StringComparison.OrdinalIgnoreCase)) res.HasAv1 = true;
                        else if (mime.Contains("vp9", StringComparison.OrdinalIgnoreCase) || mime.Contains("vp09", StringComparison.OrdinalIgnoreCase)) res.HasVp9 = true;
                        else if (mime.Contains("avc1", StringComparison.OrdinalIgnoreCase)) res.HasH264 = true;
                        if ((f.S("qualityLabel") ?? "").Contains("HDR", StringComparison.OrdinalIgnoreCase)
                            || (f.S("colorInfo", "transferCharacteristics") ?? "").Contains("SMPTEST2084", StringComparison.OrdinalIgnoreCase))
                            res.HasHdr = true;
                    }
                    else if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (br > res.MaxAudioBitrateKbps) res.MaxAudioBitrateKbps = br;
                        var trackId = f.S("audioTrack", "id") ?? "default";
                        audioIds.Add(trackId.Split('.')[0]);
                    }
                }
                res.AudioTrackCount = Math.Max(1, audioIds.Count);
            }
            if (d.P("captions", "playerCaptionsTracklistRenderer", "captionTracks") is JsonArray caps)
            {
                foreach (var c in caps)
                {
                    var code = c.S("languageCode") ?? c.P("name").Text();
                    if (!string.IsNullOrEmpty(code) && !res.CaptionLangs.Contains(code) && res.CaptionLangs.Count < 16)
                        res.CaptionLangs.Add(code.ToUpperInvariant());
                }
            }

            // From `next`: Paid promotion, chapters count, comments count, and 100-point Most Replayed Heatmap!
            res.HasPaidPromotion = HasPaidPromotion(nd.P("playerOverlays")) || HasPaidPromotion(nd.P("contents", "twoColumnWatchNextResults", "results"));
            if (J.FindFirst(nd, "multiMarkersPlayerBarRenderer").P("markersMap") is JsonArray maps)
            {
                foreach (var m in maps)
                    if (m.P("value", "chapters") is JsonArray ch && ch.Count > res.ChaptersCount)
                        res.ChaptersCount = ch.Count;
            }
            if (nd.P("engagementPanels") is JsonArray panels)
            {
                foreach (var p in panels)
                {
                    if (p.S("engagementPanelSectionListRenderer", "panelIdentifier")?.Contains("comment") == true)
                    {
                        var cc = p.P("engagementPanelSectionListRenderer", "header", "engagementPanelTitleHeaderRenderer", "contextualInfo").Text();
                        res.CommentsCount = ParseCompactCount(cc);
                    }
                }
            }

            // Extract YouTube's official 100-point "Most Replayed" viewer retention heatmap!
            J.Walk(nd, (key, o) =>
            {
                if (res.Heatmap.Count > 0) return false;
                if (key == "markersList" && o.S("markerType") == "MARKER_TYPE_HEATMAP" && o["markers"] is JsonArray markers)
                {
                    foreach (var mk in markers)
                    {
                        double st = 0, dr = 0;
                        float inten = 0;
                        try
                        {
                            st = double.Parse(mk.S("startMillis") ?? "0", System.Globalization.CultureInfo.InvariantCulture) / 1000.0;
                            dr = double.Parse(mk.S("durationMillis") ?? "0", System.Globalization.CultureInfo.InvariantCulture) / 1000.0;
                            inten = (float)(mk?["intensityScoreNormalized"]?.GetValue<double>() ?? 0);
                        }
                        catch { }
                        res.Heatmap.Add(new HeatmapPoint(st, dr, Math.Clamp(inten, 0f, 1f)));
                    }
                    return false;
                }
                if (key == "heatMarkerRenderer")
                {
                    double st = 0, dr = 0;
                    float inten = 0;
                    try
                    {
                        st = (o["timeRangeStartMillis"]?.GetValue<double>() ?? 0) / 1000.0;
                        dr = (o["markerDurationMillis"]?.GetValue<double>() ?? 0) / 1000.0;
                        inten = (float)(o["heatMarkerIntensityScoreNormalized"]?.GetValue<double>() ?? 0);
                    }
                    catch { }
                    res.Heatmap.Add(new HeatmapPoint(st, dr, Math.Clamp(inten, 0f, 1f)));
                }
                return true;
            });
        }
        catch (Exception e) { Paths.Log($"video analytics {videoId}: {e.Message}"); }
        return res;
    }

    /// <summary>Parses relative YouTube dates ("2 дня назад", "3 weeks ago", "1 мес. назад") into approximate age in days for cadence estimates.</summary>
    public static double ParseRelativeAgeDays(string? when)
    {
        if (string.IsNullOrWhiteSpace(when)) return -1;
        var s = when.ToLowerInvariant().Trim();
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)");
        double n = m.Success && double.TryParse(m.Groups[1].Value, out var v) ? v : 1;
        if (s.Contains("сек") || s.Contains("sec") || s.Contains("мин") || s.Contains("min")) return 0.02;
        if (s.Contains("час") || s.Contains("hour") || s.Contains("ч.")) return n / 24.0;
        if (s.Contains("дн") || s.Contains("ден") || s.Contains("day") || s.Contains("д.")) return n;
        if (s.Contains("нед") || s.Contains("week")) return n * 7.0;
        if (s.Contains("мес") || s.Contains("month")) return n * 30.0;
        if (s.Contains("год") || s.Contains("лет") || s.Contains("year")) return n * 365.0;
        return -1;
    }

    private static double ParseDurationText(string? dur)
    {
        if (string.IsNullOrWhiteSpace(dur) || !dur.Contains(':')) return 0;
        var p = dur.Split(':');
        double total = 0;
        foreach (var part in p)
        {
            if (!int.TryParse(part.Trim(), out var v)) return 0;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>Parses compact RU/EN YouTube counts ("125 тыс.", "1,4 млн", "84.2K", "3,210,455 просмотров") into a raw number.</summary>
    public static long ParseCompactCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.ToLowerInvariant().Replace('\u00A0', ' ').Trim();
        double mult = 1;
        if (s.Contains("млрд") || s.Contains("billion") || System.Text.RegularExpressions.Regex.IsMatch(s, @"\d\s*b\b")) mult = 1_000_000_000;
        else if (s.Contains("млн") || s.Contains("million") || System.Text.RegularExpressions.Regex.IsMatch(s, @"\d\s*m\b")) mult = 1_000_000;
        else if (s.Contains("тыс") || s.Contains("thousand") || System.Text.RegularExpressions.Regex.IsMatch(s, @"\d\s*k\b")) mult = 1_000;
        if (mult == 1)
        {
            var digits = new string(s.Where(char.IsAsciiDigit).ToArray());
            return long.TryParse(digits, out var exact) ? exact : 0;
        }
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+(?:[.,]\d+)?)");
        if (!m.Success) return 0;
        var numStr = m.Groups[1].Value.Replace(',', '.');
        return double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (long)Math.Round(v * mult) : 0;
    }

    /// <summary>Fetches deep channel analytics: aboutChannel stats (total views, join date, country, hidden channel keywords) + latest 24 videos performance, stability, and upload cadence.</summary>
    public async Task<ChannelAnalyticsData> ChannelAnalyticsAsync(string channelId, CancellationToken ct = default)
    {
        var res = new ChannelAnalyticsData { ChannelId = channelId };
        try
        {
            var aboutTask = PostAsync("browse", new JsonObject { ["browseId"] = channelId, ["params"] = "EgVhYm91dPIGBAoCEgA%3D" }, $"{Origin}/channel/{channelId}/about", ct);
            var vidsTask = ChannelVideosAsync(channelId, ct);
            await Task.WhenAll(aboutTask, vidsTask);
            var d = await aboutTask;
            var vids = await vidsTask;

            var h = d.P("header", "pageHeaderRenderer", "content", "pageHeaderViewModel");
            if (h != null)
            {
                res.Title = h.S("title", "dynamicTextViewModel", "text", "content") ?? d.S("header", "pageHeaderRenderer", "pageTitle") ?? "";
                res.Verified = h.P("title", "dynamicTextViewModel", "text", "attachmentRuns") != null;
                res.Avatar = LastSource(h.P("image", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources"));
            }
            if (d.S("metadata", "channelMetadataRenderer", "keywords") is { Length: > 0 } chKw)
            {
                foreach (System.Text.RegularExpressions.Match km in System.Text.RegularExpressions.Regex.Matches(chKw, "\"([^\"]+)\"|(\\S+)"))
                {
                    var kwVal = (km.Groups[1].Success ? km.Groups[1].Value : km.Groups[2].Value).Trim();
                    if (kwVal.Length > 1 && res.ChannelKeywords.Count < 16) res.ChannelKeywords.Add(kwVal);
                }
            }
            J.Walk(d, (key, o) =>
            {
                if (key == "aboutChannelViewModel")
                {
                    if (o.S("viewCountText") is { Length: > 0 } vt) { res.TotalViewsText = vt; res.TotalViewsNum = ParseCompactCount(vt); }
                    if (o.S("subscriberCountText") is { Length: > 0 } st) { res.SubscribersText = st; res.SubscribersNum = ParseCompactCount(st); }
                    if (o.S("videoCountText") is { Length: > 0 } vct) { res.VideoCountText = vct; res.VideoCountNum = ParseCompactCount(vct); }
                    if (o.P("joinedDateText").Text() is { Length: > 0 } jt) res.JoinedDate = jt;
                    if (o.S("country") is { Length: > 0 } cnt) res.Country = cnt;
                    if (o.S("description") is { Length: > 0 } desc) res.Description = desc;
                    if (o.S("canonicalChannelUrl") is { Length: > 0 } cu && cu.Contains("/@")) res.Handle = "@" + cu.Split("/@")[^1];
                    return false;
                }
                if (key == "channelAboutFullMetadataRenderer")
                {
                    if (o.P("viewCountText").Text() is { Length: > 0 } vt) { res.TotalViewsText = vt; res.TotalViewsNum = ParseCompactCount(vt); }
                    if (o.P("joinedDateText").Text() is { Length: > 0 } jt) res.JoinedDate = jt;
                    if (o.P("country").Text() is { Length: > 0 } cnt) res.Country = cnt;
                    if (o.P("description").Text() is { Length: > 0 } desc) res.Description = desc;
                    return false;
                }
                return true;
            });

            if (res.TotalViewsNum == 0 && string.IsNullOrEmpty(res.JoinedDate))
            {
                string? aboutTok = null;
                J.Walk(d.P("header"), (_, o) =>
                {
                    if (aboutTok != null) return false;
                    if (o.S("showEngagementPanelEndpoint", "engagementPanel", "engagementPanelSectionListRenderer", "content", "sectionListRenderer", "contents", 0, "itemSectionRenderer", "contents", 0, "continuationItemRenderer", "continuationEndpoint", "continuationCommand", "token") is { Length: > 0 } tk)
                    { aboutTok = tk; return false; }
                    if (o.S("continuationCommand", "token") is { Length: > 0 } tk2 && o.ToJsonString().Contains("about", StringComparison.OrdinalIgnoreCase))
                    { aboutTok = tk2; return false; }
                    return true;
                });
                if (aboutTok != null)
                {
                    try
                    {
                        var ad = await PostAsync("browse", new JsonObject { ["continuation"] = aboutTok }, $"{Origin}/channel/{channelId}", ct);
                        J.Walk(ad, (key, o) =>
                        {
                            if (key == "aboutChannelViewModel")
                            {
                                if (o.S("viewCountText") is { Length: > 0 } vt) { res.TotalViewsText = vt; res.TotalViewsNum = ParseCompactCount(vt); }
                                if (o.S("subscriberCountText") is { Length: > 0 } st) { res.SubscribersText = st; res.SubscribersNum = ParseCompactCount(st); }
                                if (o.S("videoCountText") is { Length: > 0 } vct) { res.VideoCountText = vct; res.VideoCountNum = ParseCompactCount(vct); }
                                if (o.P("joinedDateText").Text() is { Length: > 0 } jt) res.JoinedDate = jt;
                                if (o.S("country") is { Length: > 0 } cnt) res.Country = cnt;
                                return false;
                            }
                            return true;
                        });
                    }
                    catch { }
                }
            }

            // Build recent 24 videos statistical breakdown
            var viewList = new List<long>();
            var durList = new List<double>();
            var ageList = new List<double>();
            res.MinViews = long.MaxValue;
            foreach (var v in vids.Items.Take(24))
            {
                var parts = v.Meta.Split('·', StringSplitOptions.TrimEntries);
                long vCount = parts.Length > 0 ? ParseCompactCount(parts[0]) : 0;
                string when = parts.Length > 1 ? parts[1] : "";
                double ageDays = ParseRelativeAgeDays(when);
                res.RecentVideos.Add(new ChannelVideoPoint(v.VideoId, v.Title, vCount, when, v.Duration, ageDays));
                if (vCount > 0)
                {
                    viewList.Add(vCount);
                    if (vCount > res.PeakViews) { res.PeakViews = vCount; res.PeakTitle = v.Title; }
                    if (vCount < res.MinViews) res.MinViews = vCount;
                }
                double ds = ParseDurationText(v.Duration);
                if (ds > 0) durList.Add(ds);
                if (ageDays >= 0) ageList.Add(ageDays);
            }
            if (res.MinViews == long.MaxValue) res.MinViews = 0;
            if (viewList.Count > 0)
            {
                double mean = viewList.Average();
                res.AvgViews = (long)Math.Round(mean);
                var sorted = viewList.OrderBy(x => x).ToList();
                res.MedianViews = sorted[sorted.Count / 2];
                // Consistency score (100 = super steady views, lower = viral spikes / erratic)
                double variance = viewList.Average(x => Math.Pow(x - mean, 2));
                double cv = mean > 0 ? Math.Sqrt(variance) / mean : 1.0;
                res.ConsistencyScore = Math.Clamp(100.0 - cv * 55.0, 12.0, 99.0);
            }
            if (durList.Count > 0) res.AvgDurationSec = durList.Average();
            if (ageList.Count >= 2)
            {
                double spanDays = Math.Max(1.0, ageList.Max() - ageList.Min());
                res.UploadsPerWeek = Math.Round((ageList.Count - 1) / spanDays * 7.0, 1);
                if (res.AvgViews > 0 && res.UploadsPerWeek > 0)
                    res.EstMonthlyViews = (long)Math.Round(res.AvgViews * (res.UploadsPerWeek * 4.33));
            }
        }
        catch (Exception e) { Paths.Log($"channel analytics {channelId}: {e.Message}"); }
        return res;
    }

    public async Task<ChannelInfo> ChannelAsync(string channelId, string? tabParams = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["browseId"] = channelId };
        if (!string.IsNullOrEmpty(tabParams)) body["params"] = Uri.UnescapeDataString(tabParams);
        var d = await PostAsync("browse", body, $"{Origin}/channel/{channelId}", ct);
        var c = new ChannelInfo { Id = channelId };

        var h = d.P("header", "pageHeaderRenderer", "content", "pageHeaderViewModel");
        if (h != null)
        {
            c.Title = h.S("title", "dynamicTextViewModel", "text", "content") ?? d.S("header", "pageHeaderRenderer", "pageTitle") ?? "";
            c.Verified = h.P("title", "dynamicTextViewModel", "text", "attachmentRuns") != null;
            c.Avatar = LastSource(h.P("image", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources"));
            // the banner comes pre-cropped in several widths; ~1700 px is plenty for a 1500-DIP-wide page
            if (h.P("banner", "imageBannerViewModel", "image", "sources") is JsonArray bs && bs.Count > 0)
                c.Banner = (bs.FirstOrDefault(b => (b?["width"]?.GetValue<int>() ?? 0) >= 1700) ?? bs[^1]).S("url");
            if (h.P("metadata", "contentMetadataViewModel", "metadataRows") is JsonArray rows)
            {
                var parts = rows.SelectMany(r => r.P("metadataParts") as JsonArray ?? []).Select(p => p.S("text", "content")).Where(s => !string.IsNullOrEmpty(s)).ToList();
                foreach (var p in parts)
                {
                    if (p!.StartsWith('@')) c.Handle = p;
                    else if (c.Subscribers.Length == 0 && p.Any(char.IsDigit) && !p.Contains("видео") && !p.Contains("video")) c.Subscribers = p;
                    else if (p.Contains("видео") || p.Contains("video")) c.VideoCount = p;
                }
            }
            c.Description = h.S("description", "descriptionPreviewViewModel", "description", "content") ?? "";
        }
        // logged in: the subscribe button carries the state
        J.Walk(d.P("header"), (_, o) =>
        {
            if (c.Subscribed != null) return false;
            if (o["subscribed"] is JsonValue v && v.TryGetValue<bool>(out var b)) { c.Subscribed = b; return false; }
            return true;
        });
        if (c.Subscribed == null && LoggedIn)
            J.Walk(d.P("frameworkUpdates"), (_, o) =>
            {
                if (c.Subscribed != null) return false;
                if (o.P("subscriptionStateEntity") is JsonObject s && s["subscribed"] is JsonValue v && v.TryGetValue<bool>(out var b)) { c.Subscribed = b; return false; }
                return true;
            });

        if (d.P("contents", "twoColumnBrowseResultsRenderer", "tabs") is JsonArray tabs)
        {
            JsonNode? selected = null;
            foreach (var t in tabs)
            {
                var tr = t.P("tabRenderer");
                if (tr == null) continue;
                var title = tr.S("title") ?? "";
                var prm = tr.S("endpoint", "browseEndpoint", "params") ?? "";
                c.Tabs.Add((title, prm));
                if (tr["selected"]?.GetValue<bool>() == true) selected = tr.P("content");
            }
            if (string.IsNullOrEmpty(tabParams)) ParseShelves(selected, c);
            else
            {
                c.Grid = ParseFeed(selected, playlists: true, shorts: true);
                J.Walk(selected, (key, o) =>
                {
                    if (key != "chipViewModel") return true;
                    if (o.S("text") is { } text && o.S("tapCommand", "innertubeCommand", "continuationCommand", "token") is { } tok)
                        c.Chips.Add((text, tok, o["selected"]?.GetValue<bool>() ?? false));
                    return false;
                });
            }
        }
        return c;
    }

    private static void ParseShelves(JsonNode? content, ChannelInfo c)
    {
        if (content.P("sectionListRenderer", "contents") is not JsonArray secs) return;
        foreach (var s in secs)
        {
            if (s.P("itemSectionRenderer", "contents") is not JsonArray items) continue;
            foreach (var it in items)
            {
                var shelf = it.P("shelfRenderer");
                if (shelf != null)
                {
                    var page = ParseFeed(shelf.P("content"), playlists: true);
                    if (page.Items.Count == 0) continue;
                    var sh = new Shelf { Title = shelf.P("title").Text() ?? "" };
                    sh.Items.AddRange(page.Items);
                    sh.BrowseId = shelf.S("endpoint", "browseEndpoint", "browseId") ?? shelf.S("title", "runs", 0, "navigationEndpoint", "browseEndpoint", "browseId");
                    sh.Params = shelf.S("endpoint", "browseEndpoint", "params") ?? shelf.S("title", "runs", 0, "navigationEndpoint", "browseEndpoint", "params");
                    c.Shelves.Add(sh);
                }
                else if (it.P("channelVideoPlayerRenderer") is { } feat && feat.S("videoId") is { } vid)
                {
                    // the channel's featured video
                    var sh = new Shelf { Title = "" };
                    sh.Items.Add(new VideoItem { VideoId = vid, Title = feat.P("title").Text() ?? "", Meta = JoinMeta(feat.P("viewCountText").Text() ?? "", feat.P("publishedTimeText").Text() ?? ""), Channel = c.Title, ChannelId = c.Id });
                    c.Shelves.Add(sh);
                }
            }
        }
    }

    /// <summary>The comment section's first page token sits in the watch page data; this follows it.</summary>
    public async Task<CommentPage> CommentsAsync(string token, CancellationToken ct = default)
    {
        var d = await PostAsync("next", new JsonObject { ["continuation"] = token }, Origin + "/", ct);
        var page = new CommentPage();
        // the text lives in entity payloads keyed by commentKey; toolbar state & like/dislike actions live in sibling mutations
        var entities = new Dictionary<string, JsonNode>();
        var toolbarStates = new Dictionary<string, JsonNode>();
        var toolbarSurfaces = new Dictionary<string, JsonNode>();
        if (d.P("frameworkUpdates", "entityBatchUpdate", "mutations") is JsonArray muts)
            foreach (var m in muts)
            {
                if (m.S("entityKey") is not { } k) continue;
                if (m.P("payload", "commentEntityPayload") is { } e) entities[k] = e;
                else if (m.P("payload", "engagementToolbarStateEntityPayload") is { } ts) toolbarStates[k] = ts;
                else if (m.P("payload", "engagementToolbarSurfaceEntityPayload") is { } surf) toolbarSurfaces[k] = surf;
            }
        if (d.P("onResponseReceivedEndpoints") is not JsonArray eps) return page;
        foreach (var ep in eps)
        {
            var items = ep.P("reloadContinuationItemsCommand", "continuationItems") ?? ep.P("appendContinuationItemsAction", "continuationItems");
            if (items is not JsonArray arr) continue;
            foreach (var it in arr)
            {
                if (it.P("commentsHeaderRenderer") is { } hdr)
                {
                    page.Count = hdr.P("countText").Text();
                    J.Walk(hdr, (_, x) =>
                    {
                        if (page.CreateCommentParams != null) return false;
                        if (x.S("createCommentParams") is { Length: > 0 } cp) { page.CreateCommentParams = cp; return false; }
                        return true;
                    });
                    continue;
                }
                if (it.P("continuationItemRenderer") is { } ci)
                {
                    page.Continuation = ci.S("continuationEndpoint", "continuationCommand", "token")
                                        ?? ci.S("button", "buttonRenderer", "command", "continuationCommand", "token");
                    continue;
                }
                var thread = it.P("commentThreadRenderer");
                var vm = thread.P("commentViewModel", "commentViewModel") ?? it.P("commentViewModel");
                if (vm?.S("commentKey") is not { } key || !entities.TryGetValue(key, out var ent)) continue;
                string? repliesToken = null;
                J.Walk(thread.P("replies"), (_, x) => { repliesToken ??= x.S("continuationCommand", "token"); return repliesToken == null; });
                string? deleteAction = null;
                J.Walk(thread ?? it, (_, x) =>
                {
                    if (deleteAction != null) return false;
                    if (x.S("icon", "iconType") == "DELETE")
                    {
                        J.Walk(x, (_, y) =>
                        {
                            if (deleteAction != null) return false;
                            if (y.S("performCommentActionEndpoint", "action") is { Length: > 0 } act) { deleteAction = act; return false; }
                            return true;
                        });
                    }
                    return deleteAction == null;
                });
                int likeState = 0;
                if (vm.S("toolbarStateKey") is { } tsk && toolbarStates.TryGetValue(tsk, out var tsNode))
                {
                    var ls = tsNode.S("likeState");
                    if (ls == "TOOLBAR_LIKE_STATE_LIKED") likeState = 1;
                    else if (ls == "TOOLBAR_LIKE_STATE_DISLIKED") likeState = -1;
                }
                static string? ExtractCommentAction(JsonNode? cmd)
                {
                    if (cmd == null) return null;
                    var direct = cmd.S("innertubeCommand", "performCommentActionEndpoint", "action")
                              ?? cmd.S("performCommentActionEndpoint", "action");
                    if (!string.IsNullOrEmpty(direct)) return direct;
                    string? found = null;
                    J.Walk(cmd, (_, x) =>
                    {
                        if (found != null) return false;
                        if (x.S("performCommentActionEndpoint", "action") is { Length: > 0 } a) { found = a; return false; }
                        return true;
                    });
                    return found;
                }
                string? likeAct = null, unlikeAct = null, dislikeAct = null, undislikeAct = null;
                if (vm.S("toolbarSurfaceKey") is { } surfKey && toolbarSurfaces.TryGetValue(surfKey, out var surfNode))
                {
                    likeAct = ExtractCommentAction(surfNode.P("likeCommand"));
                    unlikeAct = ExtractCommentAction(surfNode.P("unlikeCommand"));
                    dislikeAct = ExtractCommentAction(surfNode.P("dislikeCommand"));
                    undislikeAct = ExtractCommentAction(surfNode.P("undislikeCommand"));
                }
                page.Items.Add(new Comment
                {
                    RepliesToken = repliesToken,
                    IsReply = thread == null,
                    DeleteAction = deleteAction,
                    IsOwn = deleteAction != null,

                    Author = ent.S("author", "displayName") ?? "",
                    Avatar = ent.S("author", "avatarThumbnailUrl"),
                    Text = ent.S("properties", "content", "content") ?? "",
                    When = ent.S("properties", "publishedTime") ?? "",
                    Likes = ent.S("toolbar", "likeCountNotliked") ?? "",
                    LikesLiked = ent.S("toolbar", "likeCountLiked") ?? "",
                    LikeState = likeState,
                    LikeAction = likeAct,
                    UnlikeAction = unlikeAct,
                    DislikeAction = dislikeAct,
                    UndislikeAction = undislikeAct,
                    Replies = ent.S("toolbar", "replyCount") ?? "",
                    ByOwner = ent.P("author", "isCreator")?.GetValue<bool>() ?? false,
                    Pinned = vm.P("pinnedText") != null,
                });
            }
        }
        return page;
    }

    /// <summary>Posts a top-level comment on a video or Short using the signed-in YouTube account. Returns optional deleteAction token.</summary>
    public async Task<(bool Ok, string? Error, string? DeleteAction)> CreateCommentAsync(string createCommentParams, string text, string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(createCommentParams)) return (false, "Нет токена для комментария", null);
        if (string.IsNullOrWhiteSpace(text)) return (false, "Пустой комментарий", null);
        var body = new JsonObject
        {
            ["createCommentParams"] = createCommentParams,
            ["commentText"] = text.Trim(),
        };
        var d = await PostAsync("comment/create_comment", body, $"{Origin}/watch?v={videoId}", ct);
        if (d["error"] != null) return (false, d.S("error", "message") ?? "YouTube отклонил комментарий", null);
        string? delAction = null;
        J.Walk(d, (_, x) =>
        {
            if (delAction != null) return false;
            if (x.S("icon", "iconType") == "DELETE")
            {
                J.Walk(x, (_, y) =>
                {
                    if (delAction != null) return false;
                    if (y.S("performCommentActionEndpoint", "action") is { Length: > 0 } act) { delAction = act; return false; }
                    return true;
                });
            }
            return delAction == null;
        });
        if (d.P("actionResults") is JsonArray results)
        {
            foreach (var r in results)
            {
                var st = r.S("status");
                if (st == "STATUS_SUCCEEDED") return (true, null, delAction);
                if (st != null && st != "STATUS_SUCCEEDED")
                    return (false, r.S("feedbackText") ?? "YouTube не принял комментарий", null);
            }
        }
        return (true, null, delAction);
    }

    /// <summary>Executes an InnerTube comment/perform_comment_action token (like, unlike, dislike, undislike, delete).</summary>
    public async Task<bool> PerformCommentActionAsync(string action, string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        var body = new JsonObject { ["actions"] = new JsonArray(action) };
        var d = await PostAsync("comment/perform_comment_action", body, $"{Origin}/watch?v={videoId}", ct);
        if (d["error"] != null) return false;
        if (d.P("actionResults") is JsonArray results)
        {
            foreach (var r in results)
            {
                var st = r.S("status");
                if (st != null && st != "STATUS_SUCCEEDED") return false;
            }
        }
        return true;
    }

    /// <summary>Deletes one of the signed-in user's own comments via InnerTube comment/perform_comment_action.</summary>
    public Task<bool> DeleteCommentAsync(string deleteAction, string videoId, CancellationToken ct = default)
        => PerformCommentActionAsync(deleteAction, videoId, ct);

    /// <summary>The signed-in account: name, handle, photo, own channel. Null when not signed in.</summary>
    public async Task<Account?> AccountAsync(CancellationToken ct = default)
    {
        await EnsureBootAsync(ct);
        if (!LoggedIn && CookieStore.AuthorizationHeader(Origin) == null) return null;
        var d = await PostAsync("account/account_menu", new JsonObject(), Origin + "/", ct);
        var h = J.FindFirst(d, "activeAccountHeaderRenderer");
        if (h == null) return null;
        var a = new Account
        {
            Name = h.P("accountName").Text() ?? "",
            Handle = h.P("channelHandle").Text() ?? "",
            Photo = LastThumb(h.P("accountPhoto", "thumbnails")),
        };
        J.Walk(d, (_, o) =>
        {
            if (a.ChannelId != null) return false;
            if (o.S("browseEndpoint", "browseId") is { } b && b.StartsWith("UC")) a.ChannelId = b;
            return a.ChannelId == null;
        });
        return a;
    }

    /// <summary>Like, dislike or take the mark back ("like" / "dislike" / "removelike") on the signed-in account.</summary>
    public async Task<bool> RateAsync(string videoId, string action, CancellationToken ct = default)
    {
        var body = new JsonObject { ["target"] = new JsonObject { ["videoId"] = videoId } };
        var d = await PostAsync("like/" + action, body, $"{Origin}/watch?v={videoId}", ct);
        return d["error"] == null;
    }

    /// <summary>Adds the video to a playlist of the account or takes it out ("WL" is Watch later).</summary>
    public async Task<bool> PlaylistEditAsync(string playlistId, string videoId, bool add, CancellationToken ct = default)
    {
        var action = add
            ? new JsonObject { ["addedVideoId"] = videoId, ["action"] = "ACTION_ADD_VIDEO" }
            : new JsonObject { ["removedVideoId"] = videoId, ["action"] = "ACTION_REMOVE_VIDEO_BY_VIDEO_ID" };
        var d = await PostAsync("browse/edit_playlist", new JsonObject { ["playlistId"] = playlistId, ["actions"] = new JsonArray(action) }, $"{Origin}/watch?v={videoId}", ct);
        return d.S("status") == "STATUS_SUCCEEDED";
    }

    /// <summary>The account's playlists and whether each already holds the video.</summary>
    public async Task<List<PlaylistRef>> PlaylistsForAsync(string videoId, CancellationToken ct = default)
    {
        var d = await PostAsync("playlist/get_add_to_playlist", new JsonObject { ["videoIds"] = new JsonArray(videoId), ["excludeWatchLater"] = false }, $"{Origin}/watch?v={videoId}", ct);
        var list = new List<PlaylistRef>();
        J.Walk(d, (key, o) =>
        {
            if (key != "playlistAddToOptionRenderer") return true;
            if (o.S("playlistId") is { } id)
                list.Add(new PlaylistRef { Id = id, Title = o.P("title").Text() ?? id, Contains = o.S("containsSelectedVideos") == "ALL" });
            return false;
        });
        return list;
    }

    public async Task<bool> SubscribeAsync(string channelId, bool subscribe, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["channelIds"] = new JsonArray(channelId),
            ["params"] = "EgIIAhgA",
        };
        var d = await PostAsync(subscribe ? "subscription/subscribe" : "subscription/unsubscribe", body, $"{Origin}/channel/{channelId}", ct);
        return d["error"] == null;
    }

    private static string? LastSource(JsonNode? sources)
        => sources is JsonArray a && a.Count > 0 ? a[^1].S("url") is { } u ? (u.StartsWith("//") ? "https:" + u : u) : null : null;

    /// <summary>
    /// YouTube's search filter as it travels in the request (a tiny protobuf, base64).
    /// sort: 0 relevance, 1 rating, 2 upload date, 3 views; date: 1 hour, 2 today, 3 week, 4 month, 5 year;
    /// type: 1 video, 2 channel, 3 playlist; duration: 1 under 4 min, 3 4-20 min, 2 over 20 min. 0 = any.
    /// </summary>
    public static string SearchParams(int sort, int date, int type, int duration)
    {
        var f = new List<byte>();
        if (date > 0) { f.Add(0x08); f.Add((byte)date); }
        if (type > 0) { f.Add(0x10); f.Add((byte)type); }
        if (duration > 0) { f.Add(0x18); f.Add((byte)duration); }
        var b = new List<byte>();
        if (sort > 0) { b.Add(0x08); b.Add((byte)sort); }
        if (f.Count > 0) { b.Add(0x12); b.Add((byte)f.Count); b.AddRange(f); }
        return Convert.ToBase64String(b.ToArray());
    }

    public async Task<FeedPage> SearchAsync(string query, string? filter = null, CancellationToken ct = default)
    {
        var sp = filter ?? SearchParams(0, 0, 1, 0);
        var body = new JsonObject { ["query"] = query, ["params"] = sp };
        var resultsUrl = $"{Origin}/results?search_query={Uri.EscapeDataString(query)}";
        try
        {
            return ParseFeed(await PostAsync("search", body, resultsUrl, ct), playlists: true);
        }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            // Google sometimes answers the search API with its "Sorry..." page (403) for an address it distrusts (VPN, burst of
            // requests). The ordinary results page carries the same data as ytInitialData, so read it from there instead.
            Paths.Log($"search api {(int)e.StatusCode!}: falling back to the results page");
            var html = await GetHtmlAsync(resultsUrl + "&sp=" + Uri.EscapeDataString(sp), ct);
            var dataText = J.ExtractObject(html, "var ytInitialData = ") ?? J.ExtractObject(html, "ytInitialData\"] = ");
            if (dataText == null) throw;
            return ParseFeed(JsonNode.Parse(dataText)!, playlists: true);
        }
    }

    public async Task<FeedPage> ContinueAsync(string endpoint, string token, CancellationToken ct = default, bool shorts = false)
        => ParseFeed(await PostAsync(endpoint, new JsonObject { ["continuation"] = token }, Origin + "/", ct), playlists: shorts, shorts: shorts);

    private static readonly HashSet<string> AdIndicatorKeys =
    [
        "adSlotRenderer", "adSlotMetadata", "adLayoutLoggingData",
        "inFeedAdLayoutRenderer", "promotedSparklesWebRenderer",
        "promotedSparklesTextSearchRenderer", "displayAdRenderer", "promotedVideoRenderer",
        "compactPromotedItemRenderer", "compactPromotedVideoRenderer", "adInfoRenderer",
        "adBadgeRenderer", "adBadgeViewModel", "adCtaButtonRenderer", "reelAdOverlayRenderer",
        "shortsAdOverlayRenderer", "adOverlayRenderer", "adActionInterstitialRenderer",
        "brandVideoShelfRenderer", "brandVideoSingletonRenderer", "adModule", "adsEngagementPanelContentRenderer",
        "visitAdvertiser", "sponsoredBadge",
    ];

    private static bool IsIgnoredAdSubtree(string? key) => key is
        "unserializedPrefetchData" or "playerResponse" or "topbar" or "desktopTopbar"
        or "engagementPanels" or "frameworkUpdates" or "secondaryResults";

    private static bool HasAdMarker(JsonNode? node)
    {
        if (node == null) return false;
        bool found = false;
        J.Walk(node, (key, o) =>
        {
            if (found) return false;
            if (IsIgnoredAdSubtree(key)) return false;
            if (key != null && AdIndicatorKeys.Contains(key)) { found = true; return false; }
            foreach (var k in AdIndicatorKeys)
                if (o.ContainsKey(k)) { found = true; return false; }
            if (o["isAd"] is JsonValue v && v.TryGetValue<bool>(out var b) && b) { found = true; return false; }
            if (o.S("badgeStyle") is { } bs && (bs.Contains("AD", StringComparison.OrdinalIgnoreCase) && (bs.Contains("BADGE_STYLE_TYPE_AD") || bs.Contains("SPONSOR"))))
            { found = true; return false; }
            return true;
        });
        return found;
    }

    private static bool HasPaidPromotion(JsonNode? node)
    {
        if (node == null) return false;
        bool found = false;
        J.Walk(node, (key, o) =>
        {
            if (found) return false;
            if (IsIgnoredAdSubtree(key)) return false;
            if (key is "paidContentOverlayRenderer" or "paidContentOverlay" or "paidPromotionOverlayRenderer"
                || o.ContainsKey("paidContentOverlayRenderer") || o.ContainsKey("paidContentOverlay"))
            { found = true; return false; }
            if (o["includesPaidPromotion"] is JsonValue v && v.TryGetValue<bool>(out var b) && b) { found = true; return false; }
            return true;
        });
        return found;
    }

    /// <summary>
    /// YouTube's Shorts feed: ids in the order YouTube would show them, and the token for the next batch.
    /// Automatically strips out all injected YouTube ad &amp; sponsored Shorts entries before they ever enter the player.
    /// </summary>
    public async Task<(List<string> Ids, string? Next)> ShortsFeedAsync(string? token, CancellationToken ct = default)
    {
        var ids = new List<string>();
        bool blockAds = ShortsAdBlocker.Enabled;
        if (token == null)
        {
            var first = await PostAsync("reel/reel_item_watch", new JsonObject { ["params"] = "CA8%3D", ["inputType"] = "REEL_WATCH_INPUT_TYPE_SEEDLESS", ["disablePlayerResponse"] = true }, Origin + "/shorts/", ct);
            if (first.S("replacementEndpoint", "reelWatchEndpoint", "videoId") is { } id0)
            {
                bool seedAd = HasAdMarker(first.P("overlay")) || HasAdMarker(first.P("replacementEndpoint"))
                    || HasPaidPromotion(first.P("overlay")) || HasPaidPromotion(first.P("playerOverlays"));
                if (blockAds && seedAd)
                {
                    ShortsAdBlocker.RecordBlocked();
                    Paths.Log($"shorts-adblock: dropped seed ad {id0}");
                }
                else ids.Add(id0);
            }
            token = first.S("sequenceContinuation");
            if (token == null) return (ids, null);
        }
        var d = await PostAsync("reel/reel_watch_sequence", new JsonObject { ["sequenceParams"] = token }, Origin + "/shorts/", ct);
        if (d.P("entries") is JsonArray entries)
            foreach (var e in entries)
            {
                var id = e.S("command", "reelWatchEndpoint", "videoId");
                if (id == null) continue;
                if (blockAds && (HasAdMarker(e) || HasPaidPromotion(e)))
                {
                    ShortsAdBlocker.RecordBlocked();
                    Paths.Log($"shorts-adblock: dropped feed ad entry {id}");
                    continue;
                }
                if (!ids.Contains(id)) ids.Add(id);
            }
        return (ids, d.S("continuationEndpoint", "continuationCommand", "token"));
    }

    /// <summary>Title, channel, like count, description, and ad/paid-promotion markers of one short.</summary>
    public async Task<VideoItem?> ShortInfoAsync(string videoId, CancellationToken ct = default)
    {
        var d = await PostAsync("reel/reel_item_watch", new JsonObject
        {
            ["playerRequest"] = new JsonObject { ["videoId"] = videoId }, ["params"] = "CA8%3D", ["disablePlayerResponse"] = true,
        }, $"{Origin}/shorts/{videoId}", ct);
        bool isAd = HasAdMarker(d.P("overlay")) || HasAdMarker(d.P("playerOverlays")) || HasAdMarker(d.P("replacementEndpoint"));
        bool paid = HasPaidPromotion(d.P("overlay")) || HasPaidPromotion(d.P("playerOverlays"));
        var bar = J.FindFirst(d, "reelChannelBarViewModel");
        var title = J.FindFirst(d, "shortsVideoTitleViewModel").S("text", "content");
        string? desc = J.FindFirst(d, "attributedDescription").Text()
                    ?? J.FindFirst(d, "shortsDescriptionViewModel").S("text", "content")
                    ?? J.FindFirst(d, "videoDescriptionHeaderRenderer").P("title").Text();
        string? channelId = null, avatar = null, likes = null;
        J.Walk(bar, (_, x) =>
        {
            channelId ??= x.S("browseEndpoint", "browseId");
            avatar ??= x.S("avatarViewModel", "image", "sources", 0, "url");
            return true;
        });
        likes = J.FindFirst(d, "likeButtonViewModel").S("toggleButtonViewModel", "toggleButtonViewModel", "defaultButtonViewModel", "buttonViewModel", "title");
        if (title == null && bar == null && !isAd && !paid) return null;
        return new VideoItem
        {
            VideoId = videoId, IsShort = true, Title = title ?? "", Channel = bar.S("channelName", "content") ?? "", ChannelId = channelId,
            AvatarUrl = avatar, Meta = likes ?? "", IsAd = isAd, HasPaidPromotion = paid, Description = desc ?? "",
        };
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Task<WatchInfo> Task, long At)> _watch = new();

    /// <summary>Watch-page data, shared between a hover prefetch and the page itself; ten minutes fresh.</summary>
    public Task<WatchInfo> WatchCachedAsync(string videoId, string? playlistId = null)
    {
        var now = Environment.TickCount64;
        var key = playlistId == null ? videoId : videoId + "|" + playlistId;
        if (_watch.TryGetValue(key, out var e) && !e.Task.IsFaulted && !e.Task.IsCanceled && now - e.At < 600_000) return e.Task;
        if (_watch.Count > 120) _watch.Clear();
        var t = Task.Run(() => WatchAsync(videoId, default, playlistId));
        _watch[key] = (t, now);
        return t;
    }

    /// <summary>Polls YouTube's live_chat/get_live_chat endpoint for all message types, real Unicode/custom emojis, view selector, pinned banners, deletions, and full restriction details.</summary>
    public async Task<LiveChatPollResult> LiveChatAsync(string continuation, CancellationToken ct = default)
    {
        var msgs = new List<LiveChatMessage>();
        var deleted = new List<string>();
        LiveChatMessage? pinned = null;
        var data = await PostAsync("live_chat/get_live_chat", new JsonObject { ["continuation"] = continuation }, $"{Origin}/live_chat", ct);
        var lc = data.P("continuationContents", "liveChatContinuation");
        if (lc == null) return new LiveChatPollResult(msgs, deleted, null, null, 4000, null, null, null, null, 200, null);

        static string ExtractRunsText(JsonNode? node, List<string>? emojiUrls = null)
        {
            if (node == null) return "";
            if (node["runs"] is JsonArray runs)
            {
                var sb = new StringBuilder();
                foreach (var run in runs)
                {
                    if (run.S("text") is { } txt) sb.Append(txt);
                    else if (run.P("emoji") is { } em)
                    {
                        bool isCustom = em["isCustomEmoji"]?.GetValue<bool>() ?? false;
                        var eid = em.S("emojiId") ?? "";
                        if (!isCustom && eid.Length > 0 && !eid.Contains('/') && eid.Length <= 12)
                        {
                            // Real Unicode emoji (😂, ❤️, 😎, 🔥, etc.) — never ugly :shortcode: text!
                            sb.Append(eid);
                        }
                        else if (LastThumb(em.P("image", "thumbnails")) is { } eurl)
                        {
                            // Custom YouTube / channel emote -> collect actual PNG URL for crisp inline rendering!
                            if (emojiUrls != null && emojiUrls.Count < 8) emojiUrls.Add(eurl);
                        }
                    }
                }
                return sb.ToString().Trim();
            }
            return (node.Text() ?? "").Trim();
        }

        static LiveChatMessage? ParseChatRenderer(JsonNode? item)
        {
            if (item == null) return null;
            if (item.P("liveChatModeChangeMessageRenderer") is { } mc)
            {
                var t = ExtractRunsText(mc["text"]);
                var sub = ExtractRunsText(mc["subtext"]);
                var full = string.IsNullOrEmpty(sub) ? t : (string.IsNullOrEmpty(t) ? sub : $"{t} · {sub}");
                if (string.IsNullOrEmpty(full)) return null;
                return new LiveChatMessage(mc.S("id") ?? ("sys:" + full.GetHashCode()), "", null, full, false, false, false, false, true, null, null, 0);
            }
            if (item.P("liveChatViewerEngagementMessageRenderer") is { } ve)
            {
                var t = ExtractRunsText(ve["message"]);
                if (string.IsNullOrEmpty(t)) return null;
                return new LiveChatMessage(ve.S("id") ?? ("eng:" + t.GetHashCode()), "", null, t, false, false, false, false, true, null, null, 0);
            }
            if (item.P("liveChatMembershipItemRenderer") is { } mem)
            {
                var author = mem.P("authorName").Text() ?? "";
                if (string.IsNullOrEmpty(author)) return null;
                var hdr = ExtractRunsText(mem["headerPrimaryText"]);
                var sub = ExtractRunsText(mem["headerSubtext"]);
                var body = ExtractRunsText(mem["message"]);
                var txt = string.Join(" · ", new[] { hdr, sub, body }.Where(s => !string.IsNullOrEmpty(s)));
                if (string.IsNullOrEmpty(txt)) txt = "Новый спонсор канала";
                return new LiveChatMessage(mem.S("id") ?? "", author, LastThumb(mem.P("authorPhoto", "thumbnails")), txt, false, false, true, false, false, "SPONSOR", "★ Спонсор", 0xFF10B981u);
            }
            if (item.P("liveChatSponsorshipsGiftPurchaseAnnouncementRenderer", "header", "liveChatSponsorshipsHeaderRenderer") is { } gift)
            {
                var author = gift.P("authorName").Text() ?? "";
                var txt = ExtractRunsText(gift["primaryText"]);
                if (string.IsNullOrEmpty(author) || string.IsNullOrEmpty(txt)) return null;
                return new LiveChatMessage(item.S("liveChatSponsorshipsGiftPurchaseAnnouncementRenderer", "id") ?? "", author, LastThumb(gift.P("authorPhoto", "thumbnails")), txt, false, false, true, false, false, "GIFT", "🎁 Подарок", 0xFF10B981u);
            }
            if (item.P("liveChatPaidStickerRenderer") is { } st)
            {
                var author = st.P("authorName").Text() ?? "";
                var amount = st.P("purchaseAmountText").Text() ?? "";
                if (string.IsNullOrEmpty(author)) return null;
                var lbl = st.S("sticker", "accessibility", "accessibilityData", "label") ?? "Super Sticker";
                uint acc = 0xFFE6A82Eu;
                try { var bg = st["backgroundColor"]?.GetValue<long>() ?? 0; if (bg != 0) acc = (uint)bg | 0xFF000000u; } catch { }
                var stickerUrl = LastThumb(st.P("sticker", "thumbnails"));
                return new LiveChatMessage(st.S("id") ?? "", author, LastThumb(st.P("authorPhoto", "thumbnails")), lbl, false, false, false, false, false, null, amount, acc,
                    stickerUrl != null ? [stickerUrl] : null);
            }
            var r = item.P("liveChatTextMessageRenderer") ?? item.P("liveChatPaidMessageRenderer");
            if (r == null) return null;
            var id = r.S("id") ?? "";
            var name = r.P("authorName").Text() ?? "";
            if (string.IsNullOrEmpty(name)) return null;
            var avatar = LastThumb(r.P("authorPhoto", "thumbnails"));
            var emojiUrls = new List<string>();
            var text = ExtractRunsText(r["message"], emojiUrls);
            var paid = r.P("purchaseAmountText").Text();
            if (string.IsNullOrEmpty(text) && emojiUrls.Count == 0 && string.IsNullOrEmpty(paid)) return null;

            bool isOwner = false, isMod = false, isMember = false, isVerified = false;
            string? badgeText = null;
            if (r["authorBadges"] is JsonArray badges)
            {
                foreach (var b in badges)
                {
                    var br = b.P("liveChatAuthorBadgeRenderer");
                    if (br == null) continue;
                    var iconType = br.S("icon", "iconType") ?? "";
                    if (iconType == "OWNER") isOwner = true;
                    else if (iconType == "MODERATOR") isMod = true;
                    else if (iconType == "VERIFIED" || iconType == "CHECK_CIRCLE_THICK") isVerified = true;
                    else if (br["customThumbnail"] != null) isMember = true;
                    var tt = br.S("tooltip") ?? "";
                    if (badgeText == null && tt.StartsWith("#")) badgeText = tt.Split(' ')[0];
                }
            }
            uint accent = 0;
            if (!string.IsNullOrEmpty(paid))
            {
                try { var bg = r["bodyBackgroundColor"]?.GetValue<long>() ?? 0; if (bg != 0) accent = (uint)bg | 0xFF000000u; } catch { }
                if (accent == 0) accent = 0xFFE6A82Eu;
            }
            return new LiveChatMessage(id, name, avatar, text, isOwner, isMod, isMember, isVerified, false, badgeText, paid, accent,
                emojiUrls.Count > 0 ? emojiUrls : null);
        }

        string? sendParams = null;
        string? placeholder = null;
        string? restricted = null;
        string? restrictedDetail = null;
        int maxChars = 200;

        static void ReadActionPanel(JsonNode? ap, ref string? sendParams, ref string? placeholder, ref string? restricted, ref string? restrictedDetail, ref int maxChars)
        {
            if (ap == null) return;
            if (ap.P("liveChatRestrictedParticipationRenderer") is { } rest)
            {
                var m1 = ExtractRunsText(rest["message"]);
                var m2 = ExtractRunsText(rest["subtext"]);
                var clickToast = rest.P("onClickCommand", "liveChatAddToToastAction", "item", "notificationActionRenderer", "responseText").Text();
                restricted = !string.IsNullOrEmpty(m1) ? m1 : (!string.IsNullOrEmpty(m2) ? m2 : "Отправка сообщений в этот чат ограничена");
                if (!string.IsNullOrEmpty(m2) && m2 != restricted) restrictedDetail = m2;
                else if (!string.IsNullOrEmpty(clickToast) && clickToast != restricted) restrictedDetail = clickToast;
            }
            if (ap.P("liveChatMessageInputRenderer") is { } inp)
            {
                var field = inp.P("inputField", "liveChatTextInputFieldRenderer");
                var ph = ExtractRunsText(field?["placeholder"]);
                if (!string.IsNullOrEmpty(ph)) placeholder = ph;
                try { var mc = field?["maxCharacterLimit"]?.GetValue<int>() ?? 0; if (mc > 0) maxChars = mc; } catch { }
            }
            string? foundSp = null;
            J.Walk(ap, (_, x) =>
            {
                if (foundSp != null) return false;
                if (x.S("sendLiveChatMessageEndpoint", "params") is { Length: > 0 } sp) { foundSp = sp; return false; }
                return true;
            });
            if (foundSp != null) { sendParams = foundSp; restricted = null; restrictedDetail = null; }
        }

        if (lc["actions"] is JsonArray actions)
        {
            foreach (var act in actions)
            {
                if (act.P("addChatItemAction", "item") is { } item)
                {
                    if (ParseChatRenderer(item) is { } msg) msgs.Add(msg);
                }
                else if (act.S("markChatItemAsDeletedAction", "targetItemId") is { Length: > 0 } delId)
                {
                    deleted.Add(delId);
                }
                else if (act.P("addBannerToLiveChatCommand", "bannerRenderer", "liveChatBannerRenderer", "contents") is { } ban)
                {
                    pinned = ParseChatRenderer(ban);
                }
                else if (act.P("replaceLiveChatRendererAction", "replacement") is { } rep)
                {
                    ReadActionPanel(rep, ref sendParams, ref placeholder, ref restricted, ref restrictedDetail, ref maxChars);
                }
            }
        }

        string? next = null;
        int timeoutMs = 3000;
        if (lc["continuations"] is JsonArray conts)
        {
            foreach (var c in conts)
            {
                var node = c.P("invalidationContinuationData") ?? c.P("timedContinuationData") ?? c.P("reloadContinuationData");
                if (node == null) continue;
                next = node.S("continuation");
                try { var tm = node["timeoutMs"]?.GetValue<int>() ?? 0; if (tm > 0) timeoutMs = tm; } catch { }
                if (!string.IsNullOrEmpty(next)) break;
            }
        }

        List<LiveChatViewOption>? views = null;
        if (lc.P("header", "liveChatHeaderRenderer", "viewSelector", "sortFilterSubMenuRenderer", "subMenuItems") is JsonArray vm)
        {
            views = [];
            foreach (var it in vm)
            {
                var title = it.S("title");
                var tok = it.S("continuation", "reloadContinuationData", "continuation");
                bool sel = it["selected"]?.GetValue<bool>() ?? false;
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(tok))
                    views.Add(new LiveChatViewOption(title, tok, sel));
            }
            if (views.Count == 0) views = null;
        }

        ReadActionPanel(lc["actionPanel"], ref sendParams, ref placeholder, ref restricted, ref restrictedDetail, ref maxChars);
        return new LiveChatPollResult(msgs, deleted, pinned, next, Math.Clamp(timeoutMs, 1500, 6000), sendParams, placeholder, restricted, restrictedDetail, maxChars, views);
    }

    /// <summary>Sends a live chat message on an active YouTube LIVE broadcast using the signed-in account.</summary>
    public async Task<(bool Ok, string? Error)> SendLiveChatMessageAsync(string sendParams, string text, CancellationToken ct = default)
    {
        try
        {
            var clientMsgId = "flux-" + Guid.NewGuid().ToString("N")[..16];
            var body = new JsonObject
            {
                ["params"] = sendParams,
                ["clientMessageId"] = clientMsgId,
                ["richMessage"] = new JsonObject
                {
                    ["textSegments"] = new JsonArray(new JsonObject { ["text"] = text }),
                },
            };
            var d = await PostAsync("live_chat/send_message", body, $"{Origin}/live_chat", ct);
            if (d["actions"] is JsonArray arr && arr.Count > 0) return (true, null);
            var err = J.FindFirst(d, "liveChatErrorMessageRenderer").P("errorText").Text()
                ?? d.P("errors", 0)?.ToString();
            return (err == null, err);
        }
        catch (Exception e) { Paths.Log("live chat send: " + e.Message); return (false, e.Message); }
    }

    public async Task<WatchInfo> WatchAsync(string videoId, CancellationToken ct = default, string? playlistId = null)
    {
        var body = new JsonObject { ["videoId"] = videoId, ["racyCheckOk"] = true, ["contentCheckOk"] = true };
        if (playlistId != null) body["playlistId"] = playlistId;
        var data = await PostAsync("next", body, $"{Origin}/watch?v={videoId}" + (playlistId != null ? "&list=" + playlistId : ""), ct);
        var info = new WatchInfo { VideoId = videoId, HasPaidPromotion = HasPaidPromotion(data.P("playerOverlays")) || HasPaidPromotion(data.P("contents", "twoColumnWatchNextResults", "results")) };
        // watched inside a playlist or a mix: the list comes with the page
        if (data.P("contents", "twoColumnWatchNextResults", "playlist", "playlist") is { } pl)
        {
            var panel = new PlaylistPanel { Id = pl.S("playlistId") ?? playlistId ?? "", Title = pl.S("title") ?? pl.P("titleText").Text() ?? "Плейлист" };
            panel.Items.AddRange(ParseFeed(pl.P("contents")).Items);
            if (panel.Items.Count > 0 && panel.Id.Length > 0) info.Playlist = panel;
        }
        var primary = J.FindFirst(data, "videoPrimaryInfoRenderer");
        var secondary = J.FindFirst(data, "videoSecondaryInfoRenderer");
        info.Title = primary.P("title").Text() ?? "";
        info.Views = primary.P("viewCount", "videoViewCountRenderer", "viewCount").Text() ?? "";
        info.Date = primary.P("relativeDateText").Text() ?? primary.P("dateText").Text() ?? "";
        var owner = secondary.P("owner", "videoOwnerRenderer");
        info.Channel = owner.P("title").Text() ?? "";
        info.ChannelId = owner.S("navigationEndpoint", "browseEndpoint", "browseId");
        info.Subscribers = owner.P("subscriberCountText").Text() ?? "";
        info.ChannelAvatar = LastThumb(owner.P("thumbnail", "thumbnails"));
        info.Description = secondary.P("attributedDescription").Text() ?? secondary.P("description").Text() ?? "";
        J.Walk(secondary, (_, o) =>
        {
            if (info.Subscribed != null) return false;
            if (o["subscribed"] is JsonValue v && v.TryGetValue<bool>(out var b)) { info.Subscribed = b; return false; }
            return true;
        });
        if (info.Subscribed == null && LoggedIn)
        {
            J.Walk(data.P("frameworkUpdates"), (_, o) =>
            {
                if (info.Subscribed != null) return false;
                if (o.P("subscriptionStateEntity") is JsonObject s && s["subscribed"] is JsonValue v && v.TryGetValue<bool>(out var b)) { info.Subscribed = b; return false; }
                return true;
            });
        }
        J.Walk(primary, (_, o) =>
        {
            if (info.Likes.Length > 0) return false;
            if (o.S("iconName") == "LIKE" && o.S("title") is { } t) { info.Likes = t; return false; }
            return true;
        });
        var secondaryResults = data.P("contents", "twoColumnWatchNextResults", "secondaryResults");
        info.Related = ParseFeed(secondaryResults);
        // Live chat continuation token (for active LIVE broadcasts — searches both conversationBar & engagementPanels!)
        var lcRenderer = data.P("contents", "twoColumnWatchNextResults", "conversationBar", "liveChatRenderer")
            ?? J.FindFirst(data, "liveChatRenderer");
        if (lcRenderer != null)
        {
            if (lcRenderer["continuations"] is JsonArray lcConts)
            {
                foreach (var c in lcConts)
                {
                    info.LiveChatToken = c.S("reloadContinuationData", "continuation")
                        ?? c.S("invalidationContinuationData", "continuation")
                        ?? c.S("timedContinuationData", "continuation");
                    if (!string.IsNullOrEmpty(info.LiveChatToken)) break;
                }
            }
            if (string.IsNullOrEmpty(info.LiveChatToken))
            {
                J.Walk(lcRenderer, (_, x) =>
                {
                    if (!string.IsNullOrEmpty(info.LiveChatToken)) return false;
                    var tok = x.S("reloadContinuationData", "continuation")
                        ?? x.S("invalidationContinuationData", "continuation")
                        ?? x.S("timedContinuationData", "continuation");
                    if (!string.IsNullOrEmpty(tok)) { info.LiveChatToken = tok; return false; }
                    return true;
                });
            }
        }
        J.Walk(data.P("contents", "twoColumnWatchNextResults", "results"), (_, o) =>
        {
            if (info.CommentsToken != null) return false;
            if (o.S("sectionIdentifier") != "comment-item-section") return true;
            J.Walk(o, (_, x) => { info.CommentsToken ??= x.S("continuationCommand", "token"); return info.CommentsToken == null; });
            return false;
        });
        // chapters: the author's (from the description) first, YouTube's automatic ones otherwise
        if (J.FindFirst(data, "multiMarkersPlayerBarRenderer").P("markersMap") is JsonArray maps)
        {
            JsonArray? best = null;
            foreach (var m in maps)
            {
                if (m.P("value", "chapters") is not JsonArray ch || ch.Count == 0) continue;
                if (best == null || m.S("key") == "DESCRIPTION_CHAPTERS") best = ch;
            }
            if (best != null)
                foreach (var ch in best)
                {
                    var r = ch.P("chapterRenderer");
                    var title = r.P("title").Text();
                    if (r?["timeRangeStartMillis"] is JsonValue ms && ms.TryGetValue<double>(out var at) && title != null) info.Chapters.Add((at / 1000, title));
                }
        }
        // YouTube "Most Replayed" viewer retention heatmap (100 normalized peaks for the seekbar mountains!)
        J.Walk(data, (key, o) =>
        {
            if (info.Heatmap.Count > 0) return false;
            if (key == "markersList" && o.S("markerType") == "MARKER_TYPE_HEATMAP" && o["markers"] is JsonArray hmMarkers)
            {
                foreach (var mk in hmMarkers)
                {
                    float inten = 0;
                    try { inten = (float)(mk?["intensityScoreNormalized"]?.GetValue<double>() ?? 0); } catch { }
                    info.Heatmap.Add(Math.Clamp(inten, 0f, 1f));
                }
                return false;
            }
            if (key == "heatMarkerRenderer")
            {
                float inten = 0;
                try { inten = (float)(o["heatMarkerIntensityScoreNormalized"]?.GetValue<double>() ?? 0); } catch { }
                info.Heatmap.Add(Math.Clamp(inten, 0f, 1f));
            }
            return true;
        });
        J.Walk(primary, (_, o) =>
        {
            if (info.LikeStatus != null) return false;
            if (o.S("likeStatus") is { } ls) { info.LikeStatus = ls; return false; }
            return true;
        });
        if (J.FindFirst(data, "sortFilterSubMenuRenderer").P("subMenuItems") is JsonArray sorts)
            foreach (var s in sorts)
                if (s.S("title") is { } st && s.S("serviceEndpoint", "continuationCommand", "token") is { } tk)
                    info.CommentSorts.Add((st, tk, s?["selected"]?.GetValue<bool>() ?? false));
        if (data.P("engagementPanels") is JsonArray panels)
            foreach (var p in panels)
                if (p.S("engagementPanelSectionListRenderer", "panelIdentifier")?.Contains("comment") == true)
                {
                    info.CommentsCount = p.P("engagementPanelSectionListRenderer", "header", "engagementPanelTitleHeaderRenderer", "contextualInfo").Text();
                    J.Walk(p, (_, x) =>
                    {
                        if (info.CreateCommentParams != null) return false;
                        if (x.S("createCommentParams") is { Length: > 0 } cp) { info.CreateCommentParams = cp; return false; }
                        return true;
                    });
                }
        return info;
    }

    public async Task<List<string>> SuggestAsync(string q, CancellationToken ct = default)
    {
        var url = $"https://suggestqueries-clients6.youtube.com/complete/search?client=youtube&ds=yt&hl={AppSettings.Current.Hl}&q={Uri.EscapeDataString(q)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        BrowserHeaders(req, navigation: false);
        req.Headers.Remove("Cookie");
        req.Headers.TryAddWithoutValidation("Referer", Origin + "/");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var a = text.IndexOf('('); var b = text.LastIndexOf(')');
        var list = new List<string>();
        if (a < 0 || b <= a) return list;
        var arr = JsonNode.Parse(text[(a + 1)..b]);
        if (arr.P(1) is JsonArray items)
            foreach (var it in items) if (it.S(0) is { } s) list.Add(s);
        return list;
    }

    // ---------------- parsing ----------------

    private static readonly HashSet<string> SkipKeys =
    [
        "adSlotRenderer", "adSlotMetadata", "adLayoutLoggingData", "promotedSparklesWebRenderer", "promotedSparklesTextSearchRenderer",
        "displayAdRenderer", "inFeedAdLayoutRenderer", "promotedVideoRenderer", "compactPromotedItemRenderer", "compactPromotedVideoRenderer",
        "brandVideoShelfRenderer", "brandVideoSingletonRenderer", "statementBannerRenderer", "searchPyvRenderer",
        "adsEngagementPanelContentRenderer", "reelAdOverlayRenderer", "shortsAdOverlayRenderer", "mealbarPromoRenderer",
        "primetimePromoRenderer", "bannerPromoRenderer", "adActionInterstitialRenderer", "adInfoRenderer", "playerAds", "adPlacements", "adSlots",
        "engagementPanels", "topbar", "header", "playerOverlays", "frameworkUpdates", "menu", "commentsEntryPointHeaderRenderer",
        "itemSectionRendererComments",
    ];

    /// <param name="playlists">Also keep playlist cards (channel pages); feeds skip them.</param>
    /// <param name="shorts">Keep Shorts as items (a channel's Shorts tab); everywhere else they are skipped.</param>
    public static FeedPage ParseFeed(JsonNode? root, bool playlists = false, bool shorts = false)
    {
        var page = new FeedPage();
        var seen = new HashSet<string>();
        var hideShorts = AppSettings.Current.HideShorts;
        J.Walk(root, (key, o) =>
        {
            if (key != null && SkipKeys.Contains(key)) return false;
            if (!shorts && key is "reelShelfRenderer" or "richShelfRenderer" && hideShorts && IsShortsShelf(o)) return false;
            if (key is "shortsLockupViewModel" or "reelItemRenderer" or "shortsLockupViewModelV2")
            {
                if (shorts && FromShort(o) is { } sv && seen.Add(sv.VideoId)) page.Items.Add(sv);
                return false;
            }

            VideoItem? v = key switch
            {
                "videoRenderer" or "compactVideoRenderer" or "gridVideoRenderer" or "videoWithContextRenderer"
                    or "playlistVideoRenderer" or "playlistPanelVideoRenderer" => FromRenderer(o),
                "lockupViewModel" => FromLockup(o) ?? (playlists ? FromPlaylistLockup(o) : null),
                "gridPlaylistRenderer" or "playlistRenderer" when playlists => FromPlaylistRenderer(o),
                "channelRenderer" when playlists => FromChannel(o),
                _ => null,
            };
            if (v != null)
            {
                if (seen.Add(v.IsChannel ? v.ChannelId ?? "" : v.PlaylistId ?? v.VideoId)) page.Items.Add(v);
                return false;
            }
            if (key == "continuationItemRenderer")
            {
                var tok = o.S("continuationEndpoint", "continuationCommand", "token");
                if (tok == null)
                    J.Walk(o, (_, x) => { tok ??= x.S("continuationCommand", "token"); return tok == null; });
                if (tok != null && (o.S("trigger") != "CONTINUATION_TRIGGER_ON_ITEM_SHOWN" || page.Continuation == null || page.Items.Count > 0))
                    page.Continuation = tok;
                return false;
            }
            return true;
        });
        return page;
    }

    private static bool IsShortsShelf(JsonObject o)
    {
        bool found = false;
        J.Walk(o, (k, x) =>
        {
            if (found) return false;
            if (k is "shortsLockupViewModel" or "reelItemRenderer" or "shortsLockupViewModelV2"
                || x.ContainsKey("shortsLockupViewModel") || x.ContainsKey("reelItemRenderer") || x.ContainsKey("isShorts"))
            {
                found = true;
                return false;
            }
            return true;
        });
        return found;
    }

    /// <param name="shortsToo">Keep a Short that is listed as a plain video (the history does that for Shorts opened
    /// on the watch page); everywhere else such rows are dropped.</param>
    private static VideoItem? FromRenderer(JsonObject o, bool shortsToo = false)
    {
        var isShort = false;
        var id = o.S("videoId");
        if (id == null) return null;
        var byline = o.P("ownerText") ?? o.P("shortBylineText") ?? o.P("longBylineText");
        string? duration = o.P("lengthText").Text();
        bool live = false;
        if (o.P("thumbnailOverlays") is JsonArray overlays)
            foreach (var ov in overlays)
            {
                var ts = ov.P("thumbnailOverlayTimeStatusRenderer");
                if (ts == null) continue;
                duration ??= ts.P("text").Text();
                if (ts.S("style") == "LIVE") live = true;
                if (ts.S("style") == "SHORTS") { if (!shortsToo) return null; isShort = true; }
            }
        if (o.P("badges") is JsonArray badges && badges.Any(b => b.S("metadataBadgeRenderer", "style") == "BADGE_STYLE_TYPE_LIVE_NOW")) live = true;
        var views = o.P("shortViewCountText").Text() ?? o.P("viewCountText").Text() ?? "";
        var when = o.P("publishedTimeText").Text() ?? "";
        if (views.Length == 0 && when.Length == 0) views = o.P("videoInfo").Text() ?? ""; // playlist rows
        var avatarUrl = LastThumb(o.P("channelThumbnailSupportedRenderers", "channelThumbnailWithLinkRenderer", "thumbnail", "thumbnails"))
            ?? LastThumb(o.P("channelThumbnail", "thumbnails"))
            ?? LastThumb(o.P("avatar", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources"))
            ?? o.S("avatar", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources", 0, "url");
        if (avatarUrl == null && (o.P("avatar") ?? o.P("channelThumbnailSupportedRenderers")) is { } avNode)
        {
            J.Walk(avNode, (_, x) =>
            {
                if (avatarUrl != null) return false;
                if (x["sources"] is JsonArray src && LastThumb(src) is { } su) { avatarUrl = su; return false; }
                if (x["thumbnails"] is JsonArray th && LastThumb(th) is { } tu) { avatarUrl = tu; return false; }
                return true;
            });
        }
        return new VideoItem
        {
            VideoId = id,
            Title = o.P("title").Text() ?? "",
            Channel = byline.Text() ?? "",
            ChannelId = byline.S("runs", 0, "navigationEndpoint", "browseEndpoint", "browseId"),
            Duration = duration ?? "",
            Meta = JoinMeta(views, when),
            AvatarUrl = avatarUrl,
            IsLive = live,
            IsShort = isShort,
        };
    }

    private static VideoItem? FromLockup(JsonObject o)
    {
        if (o.S("contentType") != "LOCKUP_CONTENT_TYPE_VIDEO") return null;
        var id = o.S("contentId");
        if (id == null) return null;
        var meta = o.P("metadata", "lockupMetadataViewModel");
        var rows = meta.P("metadata", "contentMetadataViewModel", "metadataRows") as JsonArray;
        string channel = "", details = "";
        if (rows != null)
        {
            // rows of parts; the very first part is the channel, whether or not the rest shares its row
            var parts = rows.Select(r => r.P("metadataParts") as JsonArray).Where(a => a != null)
                .SelectMany(a => a!.Select(p => p.P("text").Text())).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (parts.Count > 0) channel = parts[0]!;
            if (parts.Count > 1) details = string.Join(" · ", parts.Skip(1));
        }
        string? duration = null; bool live = false; string? channelId = null;
        J.Walk(o.P("contentImage"), (_, x) =>
        {
            if (x.S("text") is { } t && x.ContainsKey("badgeStyle"))
            {
                if (x.S("badgeStyle")?.Contains("LIVE") == true) live = true;
                else duration ??= t;
            }
            return true;
        });
        J.Walk(meta, (_, x) =>
        {
            channelId ??= x.S("browseEndpoint", "browseId") is { } b && b.StartsWith("UC") ? b : null;
            return channelId == null;
        });
        return new VideoItem
        {
            VideoId = id,
            Title = meta.P("title").Text() ?? "",
            Channel = channel,
            ChannelId = channelId,
            Duration = duration ?? "",
            Meta = details,
            AvatarUrl = meta.S("image", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources", 0, "url"),
            IsLive = live,
        };
    }

    private static VideoItem? FromPlaylistLockup(JsonObject o)
    {
        if (o.S("contentType") is not ("LOCKUP_CONTENT_TYPE_PLAYLIST" or "LOCKUP_CONTENT_TYPE_PODCAST")) return null;
        var id = o.S("contentId");
        var thumb = o.S("contentImage", "collectionThumbnailViewModel", "primaryThumbnail", "thumbnailViewModel", "image", "sources", 0, "url");
        var first = thumb != null ? System.Text.RegularExpressions.Regex.Match(thumb, "/vi/([A-Za-z0-9_-]{11})/").Groups[1].Value : "";
        if (id == null || first.Length != 11) return null;
        string count = "";
        J.Walk(o.P("contentImage"), (_, x) => { if (x.S("text") is { } t && x.ContainsKey("badgeStyle")) count = t; return count.Length == 0; });
        var meta = o.P("metadata", "lockupMetadataViewModel");
        return new VideoItem
        {
            VideoId = first, PlaylistId = id,
            Title = meta.P("title").Text() ?? "",
            Duration = count,
            Meta = "Плейлист",
        };
    }

    private static VideoItem? FromShort(JsonObject o)
    {
        if (HasAdMarker(o)) return null;
        var id = o.S("onTap", "innertubeCommand", "reelWatchEndpoint", "videoId") ?? o.S("videoId");
        if (id == null) return null;
        return new VideoItem
        {
            VideoId = id, IsShort = true,
            Title = o.S("overlayMetadata", "primaryText", "content") ?? o.P("headline").Text() ?? "",
            Meta = o.S("overlayMetadata", "secondaryText", "content") ?? o.P("viewCountText").Text() ?? "",
        };
    }

    private static VideoItem? FromChannel(JsonObject o)
    {
        var id = o.S("channelId");
        if (id == null) return null;
        var name = o.P("title").Text() ?? "";
        // the two count fields are swapped by YouTube: the "subscribers" one carries the @handle
        var handle = o.P("subscriberCountText").Text() ?? "";
        var subs = o.P("videoCountText").Text() ?? "";
        return new VideoItem
        {
            IsChannel = true, ChannelId = id, Title = name, Channel = name,
            Meta = JoinMeta(handle, subs),
            AvatarUrl = LastThumb(o.P("thumbnail", "thumbnails")),
        };
    }

    private static VideoItem? FromPlaylistRenderer(JsonObject o)
    {
        var id = o.S("playlistId");
        var first = o.S("navigationEndpoint", "watchEndpoint", "videoId") ?? o.S("videos", 0, "childVideoRenderer", "videoId");
        if (id == null || first == null) return null;
        return new VideoItem
        {
            VideoId = first, PlaylistId = id,
            Title = o.P("title").Text() ?? "",
            Duration = (o.P("videoCountShortText").Text() ?? o.P("videoCountText").Text() ?? "") is { Length: > 0 } n ? n + (n.Any(char.IsLetter) ? "" : " видео") : "",
            Meta = "Плейлист",
        };
    }

    private static string JoinMeta(string a, string b)
        => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : $"{a} · {b}";

    private static string? LastThumb(JsonNode? thumbs)
        => thumbs is JsonArray a && a.Count > 0 ? a[^1].S("url") is { } u ? (u.StartsWith("//") ? "https:" + u : u) : null : null;
}
