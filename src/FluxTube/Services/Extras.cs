using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxTube.Services;

/// <summary>SponsorBlock + Return YouTube Dislike, the same community APIs YTLitePlus uses.</summary>
public static class Extras
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public sealed record Segment(double Start, double End, string Category, string ActionType = "skip");

    public static Task<List<Segment>> SponsorSegmentsAsync(string videoId)
        => FetchSegmentsAsync(videoId, AppSettings.Current.SponsorCategories, includeFull: true);

    public static Task<List<Segment>> ShortsSponsorSegmentsAsync(string videoId)
        => FetchSegmentsAsync(videoId,
            ["sponsor", "selfpromo", "interaction", "exclusive_access", "intro", "outro", "preview", "filler"],
            includeFull: true);

    private static async Task<List<Segment>> FetchSegmentsAsync(string videoId, IEnumerable<string> categories, bool includeFull)
    {
        var list = new List<Segment>();
        try
        {
            // k-anonymity: only the first 4 hex chars of the hash leave the machine
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId))).ToLowerInvariant()[..4];
            var catArr = categories.Distinct().ToArray();
            if (catArr.Length == 0) return list;
            var cats = Uri.EscapeDataString("[" + string.Join(',', catArr.Select(c => $"\"{c}\"")) + "]");
            var actions = includeFull ? "&actionTypes=%5B%22skip%22%2C%22full%22%5D" : "";
            var resp = await Http.GetAsync($"https://sponsor.ajay.app/api/skipSegments/{hash}?categories={cats}{actions}");
            if (!resp.IsSuccessStatusCode) return list;
            var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonArray;
            var mine = arr?.FirstOrDefault(v => v.S("videoID") == videoId);
            if (mine?["segments"] is JsonArray segs)
                foreach (var s in segs)
                {
                    var act = s.S("actionType") ?? "skip";
                    if (act != "skip" && act != "full") continue;
                    var seg = s?["segment"] as JsonArray;
                    double start = seg != null && seg.Count >= 1 ? seg[0]!.GetValue<double>() : 0;
                    double end = seg != null && seg.Count >= 2 ? seg[1]!.GetValue<double>() : 0;
                    if (act == "skip" && end <= start) continue;
                    list.Add(new Segment(start, end, s.S("category") ?? "sponsor", act));
                }
        }
        catch (Exception e) { Paths.Log("sponsorblock: " + e.Message); }
        return list;
    }

    public static string CategoryName(string c) => c switch
    {
        "sponsor" => L.T("реклама спонсора", "sponsor"),
        "selfpromo" => L.T("самореклама", "self-promotion"),
        "interaction" => L.T("просьба подписаться", "interaction reminder"),
        "exclusive_access" => L.T("спонсорский обзор", "exclusive access"),
        "intro" => L.T("заставка", "intro"),
        "outro" => L.T("концовка", "outro"),
        "music_offtopic" => L.T("не-музыка", "non-music"),
        "preview" => L.T("превью", "preview"),
        "filler" => L.T("филлер", "filler"),
        _ => c,
    };

    public sealed record Votes(long Likes, long Dislikes);

    public static async Task<Votes?> DislikesAsync(string videoId)
    {
        try
        {
            var n = JsonNode.Parse(await Http.GetStringAsync($"https://returnyoutubedislikeapi.com/votes?videoId={videoId}"));
            if (n == null) return null;
            return new Votes(n["likes"]?.GetValue<long>() ?? 0, n["dislikes"]?.GetValue<long>() ?? 0);
        }
        catch { return null; }
    }

    public static string Compact(long n) => n switch
    {
        >= 1_000_000_000 => L.En ? $"{n / 1e9:0.#}B" : $"{n / 1e9:0.#} млрд",
        >= 1_000_000 => L.En ? $"{n / 1e6:0.#}M" : $"{n / 1e6:0.#} млн",
        >= 1_000 => L.En ? $"{n / 1e3:0.#}K" : $"{n / 1e3:0.#} тыс.",
        _ => n.ToString(),
    };
}
