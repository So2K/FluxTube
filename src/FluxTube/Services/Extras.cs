using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxTube.Services;

/// <summary>SponsorBlock + Return YouTube Dislike, the same community APIs YTLitePlus uses.</summary>
public static class Extras
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public sealed record Segment(double Start, double End, string Category);

    public static async Task<List<Segment>> SponsorSegmentsAsync(string videoId)
    {
        var list = new List<Segment>();
        try
        {
            // k-anonymity: only the first 4 hex chars of the hash leave the machine
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId))).ToLowerInvariant()[..4];
            var cats = Uri.EscapeDataString("[" + string.Join(',', AppSettings.Current.SponsorCategories.Select(c => $"\"{c}\"")) + "]");
            var resp = await Http.GetAsync($"https://sponsor.ajay.app/api/skipSegments/{hash}?categories={cats}");
            if (!resp.IsSuccessStatusCode) return list;
            var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonArray;
            var mine = arr?.FirstOrDefault(v => v.S("videoID") == videoId);
            if (mine?["segments"] is JsonArray segs)
                foreach (var s in segs)
                {
                    var seg = s?["segment"] as JsonArray;
                    if (seg == null || seg.Count < 2) continue;
                    // "mute" segments cover real content with the sound cut: only "skip" ones are jumped over
                    if ((s.S("actionType") ?? "skip") != "skip") continue;
                    list.Add(new Segment(seg[0]!.GetValue<double>(), seg[1]!.GetValue<double>(), s.S("category") ?? "sponsor"));
                }
        }
        catch (Exception e) { Paths.Log("sponsorblock: " + e.Message); }
        return list;
    }

    public static string CategoryName(string c) => c switch
    {
        "sponsor" => "реклама спонсора",
        "selfpromo" => "самореклама",
        "interaction" => "просьба подписаться",
        "intro" => "заставка",
        "outro" => "концовка",
        "music_offtopic" => "не-музыка",
        "preview" => "превью",
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
        >= 1_000_000_000 => $"{n / 1e9:0.#} млрд",
        >= 1_000_000 => $"{n / 1e6:0.#} млн",
        >= 1_000 => $"{n / 1e3:0.#} тыс.",
        _ => n.ToString(),
    };
}
