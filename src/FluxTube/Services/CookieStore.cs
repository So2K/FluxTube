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
    private static volatile bool _isLoggedIn;
    private static volatile bool _initialized;
    private static readonly object Lock = new();

    public static event Action? Changed;

    /// <summary>Zero-syscall cached login state (safe to read inside every UI paint frame).</summary>
    public static bool IsLoggedIn
    {
        get
        {
            if (!_initialized) All();
            return _isLoggedIn;
        }
    }

    private static bool ComputeLoggedIn(List<Cookie> list)
        => list.Any(c => (c.Name is "SAPISID" or "__Secure-3PAPISID") && c.Domain.TrimStart('.').EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<Cookie> All()
    {
        lock (Lock)
        {
            var path = Paths.Cookies;
            if (!File.Exists(path)) { _cache = []; _mtime = default; _isLoggedIn = false; _initialized = true; return _cache; }
            DateTime mt;
            try { mt = File.GetLastWriteTimeUtc(path); }
            catch { return _cache; }
            if (_initialized && mt == _mtime) return _cache;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    var lines = new List<string>();
                    string? l;
                    while ((l = sr.ReadLine()) != null) lines.Add(l);
                    _cache = Parse(lines);
                    _mtime = mt;
                    _isLoggedIn = ComputeLoggedIn(_cache);
                    _initialized = true;
                    return _cache;
                }
                catch (IOException) when (attempt < 2) { Thread.Sleep(15); }
                catch (Exception e) { Paths.Log("cookies read: " + e.Message); _initialized = true; return _cache; }
            }
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
            var tmp = Paths.Cookies + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, Paths.Cookies, overwrite: true);
            _mtime = default;
            _initialized = false;
        }
        All();
        Changed?.Invoke();
    }

    /// <summary>cookies.txt was rewritten by another process (the login helper).</summary>
    public static void NotifyExternalChange()
    {
        lock (Lock) { _mtime = default; _initialized = false; }
        All();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Lock)
        {
            if (File.Exists(Paths.Cookies)) File.Delete(Paths.Cookies);
            _cache = []; _mtime = default; _isLoggedIn = false; _initialized = true;
        }
        Changed?.Invoke();
    }

    private static List<Cookie> Parse(IEnumerable<string> lines)
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
