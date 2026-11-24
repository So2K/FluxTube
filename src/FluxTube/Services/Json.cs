using System.Text.Json.Nodes;

namespace FluxTube.Services;

/// <summary>Small helpers for walking InnerTube's deeply nested JSON without caring about the exact layout.</summary>
public static class J
{
    public static JsonNode? P(this JsonNode? n, params object[] path)
    {
        foreach (var key in path)
        {
            if (n == null) return null;
            n = key switch
            {
                string s when n is JsonObject o => o.TryGetPropertyValue(s, out var v) ? v : null,
                int i when n is JsonArray a => i < a.Count ? a[i] : null,
                _ => null,
            };
        }
        return n;
    }

    public static string? S(this JsonNode? n, params object[] path)
    {
        var v = n.P(path);
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return null;
    }

    /// <summary>Text from {simpleText} | {runs:[{text}]} | {content}.</summary>
    public static string? Text(this JsonNode? n)
    {
        if (n == null) return null;
        if (n is JsonValue) return n.S();
        var st = n.S("simpleText") ?? n.S("content");
        if (st != null) return st;
        if (n.P("runs") is JsonArray runs)
            return string.Concat(runs.Select(r => r.S("text")));
        return null;
    }

    /// <summary>Depth-first walk over every object; return false from visit to skip descending into it.</summary>
    public static void Walk(JsonNode? node, Func<string?, JsonObject, bool> visit, string? key = null)
    {
        switch (node)
        {
            case JsonObject o:
                if (!visit(key, o)) return;
                foreach (var kv in o) Walk(kv.Value, visit, kv.Key);
                break;
            case JsonArray a:
                foreach (var item in a) Walk(item, visit, key);
                break;
        }
    }

    public static JsonObject? FindFirst(JsonNode? node, string key)
    {
        JsonObject? found = null;
        Walk(node, (k, o) =>
        {
            if (found != null) return false;
            if (o.TryGetPropertyValue(key, out var v) && v is JsonObject vo) { found = vo; return false; }
            return true;
        });
        return found;
    }

    /// <summary>Extracts a balanced JSON object that starts at the first '{' after marker.</summary>
    public static string? ExtractObject(string text, string marker)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        i = text.IndexOf('{', i + marker.Length);
        if (i < 0) return null;
        int depth = 0; bool inStr = false; bool esc = false;
        for (int j = i; j < text.Length; j++)
        {
            var c = text[j];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text.Substring(i, j - i + 1);
        }
        return null;
    }
}
