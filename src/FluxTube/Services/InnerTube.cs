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
        // the text lives in entity payloads keyed by commentKey; the thread list gives the order
        var entities = new Dictionary<string, JsonNode>();
        if (d.P("frameworkUpdates", "entityBatchUpdate", "mutations") is JsonArray muts)
            foreach (var m in muts)
                if (m.P("payload", "commentEntityPayload") is { } e && m.S("entityKey") is { } k) entities[k] = e;
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
                page.Items.Add(new Comment
                {
                    RepliesToken = repliesToken,
                    IsReply = thread == null,

                    Author = ent.S("author", "displayName") ?? "",
                    Avatar = ent.S("author", "avatarThumbnailUrl"),
                    Text = ent.S("properties", "content", "content") ?? "",
                    When = ent.S("properties", "publishedTime") ?? "",
                    Likes = ent.S("toolbar", "likeCountNotliked") ?? "",
                    Replies = ent.S("toolbar", "replyCount") ?? "",
                    ByOwner = ent.P("author", "isCreator")?.GetValue<bool>() ?? false,
                    Pinned = vm.P("pinnedText") != null,
                });
            }
        }
        return page;
    }

    /// <summary>Posts a top-level comment on a video or Short using the signed-in YouTube account.</summary>
    public async Task<(bool Ok, string? Error)> CreateCommentAsync(string createCommentParams, string text, string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(createCommentParams)) return (false, "Нет токена для комментария");
        if (string.IsNullOrWhiteSpace(text)) return (false, "Пустой комментарий");
        var body = new JsonObject
        {
            ["createCommentParams"] = createCommentParams,
            ["commentText"] = text.Trim(),
        };
        var d = await PostAsync("comment/create_comment", body, $"{Origin}/watch?v={videoId}", ct);
        if (d["error"] != null) return (false, d.S("error", "message") ?? "YouTube отклонил комментарий");
        if (d.P("actionResults") is JsonArray results)
        {
            foreach (var r in results)
            {
                var st = r.S("status");
                if (st == "STATUS_SUCCEEDED") return (true, null);
                if (st != null && st != "STATUS_SUCCEEDED")
                    return (false, r.S("feedbackText") ?? "YouTube не принял комментарий");
            }
        }
        return (true, null);
    }

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
        var body = new JsonObject { ["channelIds"] = new JsonArray(channelId) };
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
        var body = new JsonObject { ["query"] = query, ["params"] = filter ?? SearchParams(0, 0, 1, 0) };
        return ParseFeed(await PostAsync("search", body, $"{Origin}/results?search_query={Uri.EscapeDataString(query)}", ct), playlists: true);
    }

    public async Task<FeedPage> ContinueAsync(string endpoint, string token, CancellationToken ct = default, bool shorts = false)
        => ParseFeed(await PostAsync(endpoint, new JsonObject { ["continuation"] = token }, Origin + "/", ct), playlists: shorts, shorts: shorts);

    /// <summary>
    /// YouTube's Shorts feed: ids in the order YouTube would show them, and the token for the next batch.
    /// Without a token the feed starts over (a fresh selection every time).
    /// </summary>
    public async Task<(List<string> Ids, string? Next)> ShortsFeedAsync(string? token, CancellationToken ct = default)
    {
        var ids = new List<string>();
        if (token == null)
        {
            var first = await PostAsync("reel/reel_item_watch", new JsonObject { ["params"] = "CA8%3D", ["inputType"] = "REEL_WATCH_INPUT_TYPE_SEEDLESS", ["disablePlayerResponse"] = true }, Origin + "/shorts/", ct);
            if (first.S("replacementEndpoint", "reelWatchEndpoint", "videoId") is { } id0) ids.Add(id0);
            token = first.S("sequenceContinuation");
            if (token == null) return (ids, null);
        }
        var d = await PostAsync("reel/reel_watch_sequence", new JsonObject { ["sequenceParams"] = token }, Origin + "/shorts/", ct);
        if (d.P("entries") is JsonArray entries)
            foreach (var e in entries)
                if (e.S("command", "reelWatchEndpoint", "videoId") is { } id && !ids.Contains(id)) ids.Add(id);
        return (ids, d.S("continuationEndpoint", "continuationCommand", "token"));
    }

    /// <summary>Title, channel and like count of one short.</summary>
    public async Task<VideoItem?> ShortInfoAsync(string videoId, CancellationToken ct = default)
    {
        var d = await PostAsync("reel/reel_item_watch", new JsonObject
        {
            ["playerRequest"] = new JsonObject { ["videoId"] = videoId }, ["params"] = "CA8%3D", ["disablePlayerResponse"] = true,
        }, $"{Origin}/shorts/{videoId}", ct);
        var bar = J.FindFirst(d, "reelChannelBarViewModel");
        var title = J.FindFirst(d, "shortsVideoTitleViewModel").S("text", "content");
        string? channelId = null, avatar = null, likes = null;
        J.Walk(bar, (_, x) =>
        {
            channelId ??= x.S("browseEndpoint", "browseId");
            avatar ??= x.S("avatarViewModel", "image", "sources", 0, "url");
            return true;
        });
        likes = J.FindFirst(d, "likeButtonViewModel").S("toggleButtonViewModel", "toggleButtonViewModel", "defaultButtonViewModel", "buttonViewModel", "title");
        if (title == null && bar == null) return null;
        return new VideoItem
        {
            VideoId = videoId, IsShort = true, Title = title ?? "", Channel = bar.S("channelName", "content") ?? "", ChannelId = channelId,
            AvatarUrl = avatar, Meta = likes ?? "",
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

    public async Task<WatchInfo> WatchAsync(string videoId, CancellationToken ct = default, string? playlistId = null)
    {
        var body = new JsonObject { ["videoId"] = videoId, ["racyCheckOk"] = true, ["contentCheckOk"] = true };
        if (playlistId != null) body["playlistId"] = playlistId;
        var data = await PostAsync("next", body, $"{Origin}/watch?v={videoId}" + (playlistId != null ? "&list=" + playlistId : ""), ct);
        var info = new WatchInfo { VideoId = videoId };
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
        J.Walk(primary, (_, o) =>
        {
            if (info.Likes.Length > 0) return false;
            if (o.S("iconName") == "LIKE" && o.S("title") is { } t) { info.Likes = t; return false; }
            return true;
        });
        var secondaryResults = data.P("contents", "twoColumnWatchNextResults", "secondaryResults");
        info.Related = ParseFeed(secondaryResults);
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
        "adSlotRenderer", "promotedSparklesWebRenderer", "displayAdRenderer", "inFeedAdLayoutRenderer",
        "promotedVideoRenderer", "statementBannerRenderer", "searchPyvRenderer", "adsEngagementPanelContentRenderer",
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
        return new VideoItem
        {
            VideoId = id,
            Title = o.P("title").Text() ?? "",
            Channel = byline.Text() ?? "",
            ChannelId = byline.S("runs", 0, "navigationEndpoint", "browseEndpoint", "browseId"),
            Duration = duration ?? "",
            Meta = JoinMeta(views, when),
            AvatarUrl = LastThumb(o.P("channelThumbnailSupportedRenderers", "channelThumbnailWithLinkRenderer", "thumbnail", "thumbnails"))
                        ?? LastThumb(o.P("channelThumbnail", "thumbnails")),
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
