using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxTube.Services;

/// <summary>
/// Zero-config Lossless FLAC resolver for FluxTube:
///  1. Maintains an auto-updated pool of verified public lossless sources (Monochrome direct FLAC + hifi-api Tidal proxies)
///     seeded from kipavy/spotube-plugin-lossless-sources/sources.json (cached 6h on disk + built-in fallback).
///  2. Gives top priority to an optional user self-hosted hifi-api URL (AppSettings.CustomHifiApiUrl).
///  3. Cleans YouTube video titles/channels, scores candidate matches by title + artist + duration,
///     and verifies stream availability (HEAD for Monochrome, BTS manifest decode for hifi-api) before handing off to StreamProxy.
/// </summary>
public static partial class LosslessResolver
{
    public const int LosslessItag = 9999;

    public sealed record LosslessMatch(
        string VideoId,
        string Url,
        long ContentLength,
        string Mime,
        string SourceLabel,
        string MatchedTitle,
        string MatchedArtist,
        double DurationSec,
        double DurationDiffSec,
        bool ExactTiming,
        int SampleRate,
        byte[]? InitialHead = null);

    private sealed record SourceHost(string Type, string BaseUrl);

    private sealed record CandidateTrack(
        string SourceType,
        string BaseUrl,
        string RefId,
        string Title,
        string[] Artists,
        double DurationSec);

    private const string SourcesRegistryUrl = "https://raw.githubusercontent.com/kipavy/spotube-plugin-lossless-sources/main/sources.json";
    private static readonly SourceHost[] BuiltinSources = [new("monochrome", "https://tracks.monochrome.st")];

    private static readonly HttpClient Http = MakeClient();
    private static readonly ConcurrentDictionary<string, LosslessMatch?> _matchCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _deadHostsUntil = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _sourcesLock = new();
    private static List<SourceHost>? _cachedSources;
    private static DateTime _sourcesFetchedAt;

    public static int ActiveSourceCount
    {
        get
        {
            lock (_sourcesLock)
                return (_cachedSources?.Count ?? BuiltinSources.Length) + (string.IsNullOrWhiteSpace(AppSettings.Current.CustomHifiApiUrl) ? 0 : 1);
        }
    }

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.Proxy))
        {
            h.Proxy = new WebProxy(AppSettings.Current.Proxy);
            h.UseProxy = true;
        }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "FluxTube/9.2 (+https://github.com/FluxTube)");
        return c;
    }

    public static void Warm() => _ = GetSourcesAsync(CancellationToken.None);

    public static void Forget(string videoId) => _matchCache.TryRemove(videoId, out _);

    public static bool TryGetCached(string videoId, out LosslessMatch? match) => _matchCache.TryGetValue(videoId, out match);

    /// <summary>
    /// Resolves a lossless FLAC stream matching a YouTube video's metadata.
    /// Returns null if the video does not look like a matchable track or no healthy source has it.
    /// </summary>
    public static async Task<LosslessMatch?> ResolveAsync(
        string videoId,
        string? rawTitle,
        string? rawChannel,
        double ytDurationSec,
        bool manualRequest = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(rawTitle)) return null;
        if (!manualRequest && _matchCache.TryGetValue(videoId, out var prev)) return prev;

        // Skip obvious non-music long videos / clips when running automatically
        if (!manualRequest && ytDurationSec > 0 && (ytDurationSec < 35 || ytDurationSec > 900))
            return null;

        var (artist, trackTitle, query) = ParseTrackQuery(rawTitle, rawChannel ?? "");
        if (string.IsNullOrWhiteSpace(trackTitle) || trackTitle.Length < 2) return null;

        if (!manualRequest && LooksNonMusic(rawTitle))
            return null;

        var sources = await GetSourcesAsync(ct).ConfigureAwait(false);
        if (sources.Count == 0) return null;

        var nowTick = Environment.TickCount64;
        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested) return null;
            if (_deadHostsUntil.TryGetValue(src.BaseUrl, out var until) && nowTick < until) continue;

            try
            {
                var candidates = src.Type == "monochrome"
                    ? await SearchMonochromeAsync(src.BaseUrl, query, ct).ConfigureAwait(false)
                    : await SearchHifiApiAsync(src.BaseUrl, query, ct).ConfigureAwait(false);

                var best = PickBestCandidate(candidates, artist, trackTitle, ytDurationSec);

                // If artist+title search yielded nothing or weak score, retry with title alone (helps with transliterated artist/channel names)
                if ((best == null || best.Value.Score < 0.55) && !string.Equals(query, trackTitle, StringComparison.OrdinalIgnoreCase))
                {
                    var more = src.Type == "monochrome"
                        ? await SearchMonochromeAsync(src.BaseUrl, trackTitle, ct).ConfigureAwait(false)
                        : await SearchHifiApiAsync(src.BaseUrl, trackTitle, ct).ConfigureAwait(false);
                    if (more.Count > 0)
                    {
                        var best2 = PickBestCandidate(more, artist, trackTitle, ytDurationSec);
                        if (best2 != null && (best == null || best2.Value.Score > best.Value.Score))
                            best = best2;
                    }
                }

                if (best == null) continue;

                var (cand, score, diffSec) = best.Value;
                if (score < 0.48) continue;
                if (ytDurationSec > 0 && cand.DurationSec > 0 && diffSec > 14.0) continue;

                var resolved = cand.SourceType == "monochrome"
                    ? await ResolveMonochromeStreamAsync(videoId, cand, diffSec, ytDurationSec, ct).ConfigureAwait(false)
                    : await ResolveHifiApiStreamAsync(videoId, cand, diffSec, ytDurationSec, ct).ConfigureAwait(false);

                if (resolved != null)
                {
                    CacheResult(videoId, resolved);
                    Paths.Log($"lossless {videoId}: matched [{resolved.SourceLabel}] '{resolved.MatchedArtist} - {resolved.MatchedTitle}' (score={score:0.00}, diff={diffSec:0.1}s, len={resolved.ContentLength / 1024}KB)");
                    return resolved;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                Paths.Log($"lossless {src.BaseUrl}: {ex.Message}");
                _deadHostsUntil[src.BaseUrl] = Environment.TickCount64 + 5 * 60_000L;
            }
        }

        if (!manualRequest) CacheResult(videoId, null);
        return null;
    }

    private static void CacheResult(string videoId, LosslessMatch? match)
    {
        if (_matchCache.Count > 256)
        {
            foreach (var k in _matchCache.Keys.Take(64)) _matchCache.TryRemove(k, out _);
        }
        _matchCache[videoId] = match;
    }

    // ---------------- Source Registry (6h disk + memory cache) ----------------

    private static async Task<List<SourceHost>> GetSourcesAsync(CancellationToken ct)
    {
        var result = new List<SourceHost>();
        var custom = AppSettings.Current.CustomHifiApiUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(custom) && (custom.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || custom.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            result.Add(new SourceHost("hifi-api", custom));

        List<SourceHost>? pub;
        bool needRefresh;
        lock (_sourcesLock)
        {
            pub = _cachedSources;
            needRefresh = pub == null || (DateTime.UtcNow - _sourcesFetchedAt).TotalHours >= 6;
        }

        if (!needRefresh && pub != null)
        {
            result.AddRange(pub);
            return result;
        }

        var cachePath = Path.Combine(Paths.Data, "lossless-sources.json");
        // Try fresh disk cache (< 6h) first so app startup costs zero network requests
        if (pub == null && File.Exists(cachePath))
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
                var parsed = ParseSourcesJson(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false));
                if (parsed.Count > 0)
                {
                    lock (_sourcesLock)
                    {
                        _cachedSources = parsed;
                        _sourcesFetchedAt = age.TotalHours < 6 ? File.GetLastWriteTimeUtc(cachePath) : DateTime.UtcNow.AddHours(-5.5);
                    }
                    if (age.TotalHours < 6)
                    {
                        result.AddRange(parsed);
                        return result;
                    }
                }
            }
            catch { }
        }

        // Fetch live probed sources.json from GitHub
        try
        {
            using var resp = await Http.GetAsync(SourcesRegistryUrl, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsed = ParseSourcesJson(text);
                if (parsed.Count > 0)
                {
                    lock (_sourcesLock)
                    {
                        _cachedSources = parsed;
                        _sourcesFetchedAt = DateTime.UtcNow;
                    }
                    try
                    {
                        Directory.CreateDirectory(Paths.Data);
                        await File.WriteAllTextAsync(cachePath, text, ct).ConfigureAwait(false);
                    }
                    catch { }
                    result.AddRange(parsed);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Paths.Log("lossless sources fetch: " + ex.Message);
        }

        lock (_sourcesLock)
        {
            _cachedSources ??= [.. BuiltinSources];
            _sourcesFetchedAt = DateTime.UtcNow.AddHours(-5); // retry sooner
            result.AddRange(_cachedSources);
        }
        return result;
    }

    private static List<SourceHost> ParseSourcesJson(string json)
    {
        var list = new List<SourceHost>();
        var root = JsonNode.Parse(json);
        if (root?["sources"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var type = item.S("type")?.Trim().ToLowerInvariant();
                var baseUrl = item.S("base")?.Trim().TrimEnd('/');
                if (type is "monochrome" or "hifi-api" && !string.IsNullOrEmpty(baseUrl))
                    list.Add(new SourceHost(type, baseUrl));
            }
        }
        return list;
    }

    // ---------------- Monochrome Protocol ----------------

    private static async Task<List<CandidateTrack>> SearchMonochromeAsync(string baseUrl, string query, CancellationToken ct)
    {
        var url = $"{baseUrl}/search/tracks?q={Uri.EscapeDataString(query)}&limit=12";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode >= 500) _deadHostsUntil[baseUrl] = Environment.TickCount64 + 2 * 60_000L;
            return [];
        }
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (root?["tracks"] is not JsonArray tracks) return [];

        var list = new List<CandidateTrack>(tracks.Count);
        foreach (var t in tracks)
        {
            if (t?["playable"] is JsonValue pv && pv.TryGetValue<bool>(out var playable) && !playable) continue;
            var id = IdString(t?["id"]);
            if (string.IsNullOrEmpty(id)) continue;
            var title = t.S("title") ?? "";
            var artists = new List<string>();
            if (t?["artistNames"] is JsonArray an)
            {
                foreach (var a in an)
                    if (a?.GetValue<string>() is { Length: > 0 } s) artists.Add(s);
            }
            double durMs = NumValue(t?["duration"]);
            list.Add(new CandidateTrack("monochrome", baseUrl, id, title, [.. artists], durMs > 0 ? durMs / 1000.0 : 0));
        }
        return list;
    }

    private static async Task<LosslessMatch?> ResolveMonochromeStreamAsync(
        string videoId, CandidateTrack cand, double diffSec, double ytDurSec, CancellationToken ct)
    {
        var url = $"{cand.BaseUrl}/track/{cand.RefId}";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            // Single GET Range 0..128KB verifies stream, gets total Content-Range length, AND prefetches FLAC header+seektable in 1 round-trip!
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 128 * 1024 - 1);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is 200 or 206)
            {
                long len = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? 0;
                var ctMime = resp.Content.Headers.ContentType?.MediaType;
                string mime = !string.IsNullOrWhiteSpace(ctMime) && ctMime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    ? ctMime
                    : "audio/flac";
                byte[]? head = null;
                if (code == 206 || (len > 0 && len <= 256 * 1024))
                {
                    try { head = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false); }
                    catch { }
                }
                int sr = 44_100;
                if (len > 0 && cand.DurationSec > 0)
                {
                    double bps = len * 8.0 / cand.DurationSec;
                    if (bps > 2_116_800) sr = 96_000;
                }
                bool exact = ytDurSec <= 0 || cand.DurationSec <= 0 || diffSec <= 3.0;
                return new LosslessMatch(
                    videoId, url, len, mime, "Monochrome",
                    cand.Title, string.Join(", ", cand.Artists),
                    cand.DurationSec, diffSec, exact, sr, head);
            }
            if (code >= 500 && attempt == 0)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            break;
        }
        return null;
    }

    // ---------------- HiFi-API Protocol (Tidal proxies / self-hosted) ----------------

    private static async Task<List<CandidateTrack>> SearchHifiApiAsync(string baseUrl, string query, CancellationToken ct)
    {
        var url = $"{baseUrl}/search/?s={Uri.EscapeDataString(query)}";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode is 403 or 429 or >= 500)
                _deadHostsUntil[baseUrl] = Environment.TickCount64 + 5 * 60_000L;
            return [];
        }
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var items = (root?["data"]?["items"] ?? root?["items"]) as JsonArray;
        if (items == null) return [];

        var list = new List<CandidateTrack>(items.Count);
        foreach (var item in items)
        {
            var id = IdString(item?["id"]);
            if (string.IsNullOrEmpty(id)) continue;
            var title = item.S("title") ?? "";
            var artists = new List<string>();
            var mainArtist = item.S("artist", "name");
            if (!string.IsNullOrWhiteSpace(mainArtist)) artists.Add(mainArtist);
            if (item?["artists"] is JsonArray aa)
            {
                foreach (var a in aa)
                {
                    var n = a.S("name");
                    if (!string.IsNullOrWhiteSpace(n) && !artists.Contains(n, StringComparer.OrdinalIgnoreCase))
                        artists.Add(n);
                }
            }
            double durSec = NumValue(item?["duration"]);
            list.Add(new CandidateTrack("hifi-api", baseUrl, id, title, [.. artists], durSec));
        }
        return list;
    }

    private static async Task<LosslessMatch?> ResolveHifiApiStreamAsync(
        string videoId, CandidateTrack cand, double diffSec, double ytDurSec, CancellationToken ct)
    {
        foreach (var quality in new[] { "HI_RES_LOSSLESS", "LOSSLESS" })
        {
            var reqUrl = $"{cand.BaseUrl}/track/?id={Uri.EscapeDataString(cand.RefId)}&quality={quality}";
            using var resp = await Http.GetAsync(reqUrl, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 202)
            {
                await Task.Delay(900, ct).ConfigureAwait(false);
                using var retry = await Http.GetAsync(reqUrl, ct).ConfigureAwait(false);
                if (!retry.IsSuccessStatusCode) continue;
                var m2 = ParseHifiManifest(videoId, cand, diffSec, ytDurSec, await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (m2 != null) return m2;
                continue;
            }
            if (!resp.IsSuccessStatusCode) continue;
            var match = ParseHifiManifest(videoId, cand, diffSec, ytDurSec, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (match != null) return match;
        }
        return null;
    }

    private static LosslessMatch? ParseHifiManifest(string videoId, CandidateTrack cand, double diffSec, double ytDurSec, string body)
    {
        var root = JsonNode.Parse(body);
        if (root?["detail"] != null) return null;
        var data = root?["data"];
        if (data == null) return null;
        if (!string.Equals(data.S("manifestMimeType"), "application/vnd.tidal.bts", StringComparison.OrdinalIgnoreCase))
            return null;

        var rawManifest = data.S("manifest");
        if (string.IsNullOrWhiteSpace(rawManifest)) return null;

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(rawManifest)); }
        catch { return null; }

        var manifest = JsonNode.Parse(decoded);
        var streamUrl = manifest?["urls"] is JsonArray urls && urls.Count > 0 ? urls[0]?.GetValue<string>() : null;
        if (string.IsNullOrWhiteSpace(streamUrl)) return null;

        var codecs = (manifest.S("codecs") ?? "").ToLowerInvariant();
        if (!codecs.Contains("flac")) return null; // only upgrade to true Lossless FLAC

        int sr = (int)NumValue(data["sampleRate"]);
        if (sr <= 0) sr = 44_100;
        bool exact = ytDurSec <= 0 || cand.DurationSec <= 0 || diffSec <= 3.0;
        return new LosslessMatch(
            videoId, streamUrl, 0, "audio/flac", "HiFi-API",
            cand.Title, string.Join(", ", cand.Artists),
            cand.DurationSec, diffSec, exact, sr);
    }

    // ---------------- Metadata Cleaning & Strict Matching ----------------

    private static (string Artist, string Title, string Query) ParseTrackQuery(string rawTitle, string rawChannel)
    {
        string cleanChannel = Regex.Replace(rawChannel, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase).Trim();
        cleanChannel = Regex.Replace(cleanChannel, @"\s*(VEVO|Official(\s+Channel|\s+Music)?)$", "", RegexOptions.IgnoreCase).Trim();

        string cleaned = CleanNoise(rawTitle);
        string artist = "";
        string title = cleaned;

        // Split "Artist - Title" (or em/en dash)
        var sep = DashSepRegex().Match(cleaned);
        if (sep.Success)
        {
            artist = cleaned[..sep.Index].Trim();
            title = cleaned[(sep.Index + sep.Length)..].Trim();
        }
        else if (!string.IsNullOrWhiteSpace(cleanChannel))
        {
            artist = cleanChannel;
            title = cleaned;
        }

        artist = StripFeat(artist);
        title = StripFeat(title);

        string query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
        return (artist, title, query.Trim());
    }

    private static string CleanNoise(string s)
    {
        string r = BracketNoiseRegex().Replace(s, " ");
        r = SuffixNoiseRegex().Replace(r, " ");
        r = MultiSpaceRegex().Replace(r, " ").Trim(' ', '-', '–', '—', '|');
        return r;
    }

    private static string StripFeat(string s) =>
        MultiSpaceRegex().Replace(FeatRegex().Replace(s, " "), " ").Trim(' ', '-', ',', '&');

    private static bool LooksNonMusic(string title)
    {
        var t = title.ToLowerInvariant();
        return t.Contains("подкаст") || t.Contains("podcast") || t.Contains("обзор") || t.Contains("review")
            || t.Contains("гайд") || t.Contains("tutorial") || t.Contains("прохождение") || t.Contains("walkthrough")
            || t.Contains("интервью") || t.Contains("interview") || t.Contains("стрим") || t.Contains("gameplay")
            || t.Contains("новости") || t.Contains("unboxing") || t.Contains("распаковка") || t.Contains("реакция");
    }

    private static (CandidateTrack Cand, double Score, double DiffSec)? PickBestCandidate(
        List<CandidateTrack> candidates, string artist, string title, double ytDurationSec)
    {
        var titleToks = Tokenize(title);
        var artistToks = Tokenize(artist);
        if (titleToks.Count == 0) return null;

        CandidateTrack? best = null;
        double bestScore = -1;
        double bestDiff = 999;

        foreach (var c in candidates)
        {
            var cTitleToks = Tokenize(StripFeat(CleanNoise(c.Title)));
            var cArtistToks = Tokenize(string.Join(' ', c.Artists));

            double titleSim = Jaccard(titleToks, cTitleToks);
            if (titleSim < 0.35) continue;

            double artistSim = artistToks.Count > 0 ? Overlap(artistToks, cArtistToks) : 0.5;
            // Also allow artist tokens to appear in title/artist combined (common on compilations)
            if (artistSim < 0.3 && artistToks.Count > 0)
            {
                var all = new HashSet<string>(cArtistToks);
                foreach (var tk in cTitleToks) all.Add(tk);
                artistSim = Math.Max(artistSim, Overlap(artistToks, all) * 0.8);
            }

            double diffSec = (ytDurationSec > 0 && c.DurationSec > 0) ? Math.Abs(ytDurationSec - c.DurationSec) : 0;
            double durScore = (ytDurationSec <= 0 || c.DurationSec <= 0) ? 0.8
                : diffSec <= 2.5 ? 1.0
                : diffSec <= 5.0 ? 0.75
                : diffSec <= 12.0 ? 0.35
                : 0.0;

            double score = titleSim * 0.52 + artistSim * 0.30 + durScore * 0.18;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
                bestDiff = diffSec;
            }
        }

        return best != null ? (best, bestScore, bestDiff) : null;
    }

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(s)) return set;
        var norm = TransliterateToLatin(s.Normalize(NormalizationForm.FormKD));
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0)
            {
                var w = sb.ToString();
                if (w.Length >= 2) set.Add(w);
                sb.Clear();
            }
        }
        if (sb.Length >= 2) set.Add(sb.ToString());
        return set;
    }

    /// <summary>
    /// Normalizes Cyrillic (Russian/Ukrainian) into canonical Latin so YouTube transliterated titles
    /// ("PAPIN TANK", "MORGENSHTERN") match catalog Cyrillic titles ("ПАПИН ТАНК", "МОРГЕНШТЕРН") seamlessly.
    /// </summary>
    private static string TransliterateToLatin(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var raw in s)
        {
            char c = char.ToLowerInvariant(raw);
            switch (c)
            {
                case 'а': sb.Append('a'); break;
                case 'б': sb.Append('b'); break;
                case 'в': sb.Append('v'); break;
                case 'г': sb.Append('g'); break;
                case 'д': sb.Append('d'); break;
                case 'е': case 'ё': case 'э': sb.Append('e'); break;
                case 'ж': sb.Append("zh"); break;
                case 'з': sb.Append('z'); break;
                case 'и': case 'й': case 'і': case 'ї': sb.Append('i'); break;
                case 'к': sb.Append('k'); break;
                case 'л': sb.Append('l'); break;
                case 'м': sb.Append('m'); break;
                case 'н': sb.Append('n'); break;
                case 'о': sb.Append('o'); break;
                case 'п': sb.Append('p'); break;
                case 'р': sb.Append('r'); break;
                case 'с': sb.Append('s'); break;
                case 'т': sb.Append('t'); break;
                case 'у': sb.Append('u'); break;
                case 'ф': sb.Append('f'); break;
                case 'х': sb.Append('h'); break;
                case 'ц': sb.Append("ts"); break;
                case 'ч': sb.Append("ch"); break;
                case 'ш': sb.Append("sh"); break;
                case 'щ': sb.Append("shch"); break;
                case 'ы': sb.Append('y'); break;
                case 'ю': sb.Append("yu"); break;
                case 'я': sb.Append("ya"); break;
                case 'ъ': case 'ь': break;
                default: sb.Append(c); break;
            }
        }
        // Unify common transliteration variants (kh->h, yo->e, j->i, y->i in endings)
        return sb.ToString()
            .Replace("shch", "sh")
            .Replace("sch", "sh")
            .Replace("kh", "h")
            .Replace("yo", "e")
            .Replace("ia", "ya")
            .Replace("iu", "yu");
    }

    private static bool TokensMatch(string a, string b)
    {
        if (a == b) return true;
        if (Math.Abs(a.Length - b.Length) > 1 || Math.Min(a.Length, b.Length) < 4) return false;
        // Edit distance <= 1 for words >= 4 chars (handles "morgenstern" vs "morgenshtern" / minor typos)
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (b.Length > a.Length) j++;
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = 0;
        foreach (var x in a)
            if (b.Contains(x) || b.Any(y => TokensMatch(x, y))) inter++;
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static double Overlap(HashSet<string> needle, HashSet<string> haystack)
    {
        if (needle.Count == 0 || haystack.Count == 0) return 0;
        int inter = 0;
        foreach (var x in needle)
            if (haystack.Contains(x) || haystack.Any(y => TokensMatch(x, y))) inter++;
        return (double)inter / Math.Min(needle.Count, haystack.Count);
    }

    private static string? IdString(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static double NumValue(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, CultureInfo.InvariantCulture, out var pd)) return pd;
        return 0;
    }

    [GeneratedRegex(@"\s+[-–—]\s+")]
    private static partial Regex DashSepRegex();

    [GeneratedRegex(@"[\(\[\{]\s*(official(\s+music|\s+lyric|\s+audio|\s+hd|\s+4k)?\s*(video|audio|mv|visualizer|clip)?|lyrics?|audio|hd|4k|uhd|remaster(ed)?(\s+\d{4})?|explicit|clean|music\s+video|mv|hq|visualizer|full\s+song|prod\.?[^\)\]\}]*)\s*[\)\]\}]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketNoiseRegex();

    [GeneratedRegex(@"\b(official\s+(music\s+)?video|official\s+audio|lyric\s+video|4k\s+remaster)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixNoiseRegex();

    [GeneratedRegex(@"\b(feat\.?|ft\.?|featuring)\s+[^-\(\[\|]+", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
