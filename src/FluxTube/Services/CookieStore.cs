using System.Security.Cryptography;
using System.Text;

namespace FluxTube.Services;

/// <summary>
/// cookies.txt (Netscape format) is the single source of truth: yt-dlp reads and updates it,
/// our InnerTube client only reads it. That keeps Google's rotating session cookies consistent.
/// </summary>
public static class CookieStore
{
    public sealed record Cookie(string Domain, bool Subdomains, string Path, bool Secure, long Expires, string Name, string Value, bool HttpOnly);

    private static List<Cookie> _cache = [];
    private static DateTime _mtime;
    private static readonly object Lock = new();

    public static event Action? Changed;

    public static bool IsLoggedIn => Get("youtube.com", "SAPISID") != null || Get("youtube.com", "__Secure-3PAPISID") != null;

    public static IReadOnlyList<Cookie> All()
    {
        lock (Lock)
        {
            var path = Paths.Cookies;
            if (!File.Exists(path)) { _cache = []; return _cache; }
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt == _mtime) return _cache;
            _mtime = mt;
            _cache = Parse(File.ReadAllLines(path));
            return _cache;
        }
    }

    public static string? Get(string domainSuffix, string name)
        => All().LastOrDefault(c => c.Name == name && c.Domain.TrimStart('.').EndsWith(domainSuffix, StringComparison.OrdinalIgnoreCase))?.Value;

    public static string HeaderFor(string host)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seen = new HashSet<string>();
        var sb = new StringBuilder();
        foreach (var c in All().Reverse())
        {
            var d = c.Domain.TrimStart('.');
            if (!(host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))) continue;
            if (c.Expires != 0 && c.Expires < now) continue;
            if (!seen.Add(c.Name)) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(c.Name).Append('=').Append(c.Value);
        }
        return sb.ToString();
    }

    /// <summary>Google's SAPISIDHASH auth header, exactly as the web client builds it.</summary>
    public static string? AuthorizationHeader(string origin)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string Hash(string v) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{ts} {v} {origin}"))).ToLowerInvariant();
        var sapisid = Get("youtube.com", "SAPISID") ?? Get("youtube.com", "__Secure-3PAPISID");
        if (sapisid == null) return null;
        var parts = new List<string> { $"SAPISIDHASH {ts}_{Hash(sapisid)}" };
        var p1 = Get("youtube.com", "__Secure-1PAPISID");
        if (p1 != null) parts.Add($"SAPISID1PHASH {ts}_{Hash(p1)}");
        var p3 = Get("youtube.com", "__Secure-3PAPISID");
        if (p3 != null) parts.Add($"SAPISID3PHASH {ts}_{Hash(p3)}");
        return string.Join(' ', parts);
    }

    public static void Save(IEnumerable<Cookie> cookies)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        sb.AppendLine("# Written by FluxTube. Shared with yt-dlp.");
        foreach (var c in cookies)
        {
            var domain = (c.HttpOnly ? "#HttpOnly_" : "") + c.Domain;
            sb.Append(domain).Append('\t')
              .Append(c.Subdomains ? "TRUE" : "FALSE").Append('\t')
              .Append(c.Path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
              .Append(c.Expires).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }
        lock (Lock)
        {
            File.WriteAllText(Paths.Cookies, sb.ToString());
            _mtime = default;
        }
        Changed?.Invoke();
    }

    /// <summary>cookies.txt was rewritten by another process (the login helper).</summary>
    public static void NotifyExternalChange()
    {
        lock (Lock) _mtime = default;
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Lock)
        {
            if (File.Exists(Paths.Cookies)) File.Delete(Paths.Cookies);
            _cache = []; _mtime = default;
        }
        Changed?.Invoke();
    }

    private static List<Cookie> Parse(string[] lines)
    {
        var list = new List<Cookie>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var httpOnly = false;
            if (line.StartsWith("#HttpOnly_")) { httpOnly = true; line = line[10..]; }
            else if (line.StartsWith('#') || line.Length == 0) continue;
            var f = line.Split('\t');
            if (f.Length < 7) continue;
            long.TryParse(f[4], out var exp);
            list.Add(new Cookie(f[0], f[1] == "TRUE", f[2], f[3] == "TRUE", exp, f[5], f[6], httpOnly));
        }
        return list;
    }
}
