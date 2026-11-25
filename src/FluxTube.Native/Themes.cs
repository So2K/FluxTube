using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FluxTube.Services;

namespace FluxTube;

/// <summary>One colour theme: a handful of base colours; everything else (hover, lines, fields, menus) is derived.</summary>
public sealed record Theme(string Name, string Group, bool Light, uint Bg, uint Panel, uint Card, uint Text, uint Sub, uint Dim, uint Brand, uint OnBrand = 0xFFFFFFFF);

/// <summary>
/// Built-in & user-authored custom code themes (stored in %USERPROFILE%\.fluxtube\themes\*.json).
/// Switching eases every colour smoothly over 260 ms.
/// </summary>
public static class Themes
{
    public static readonly Theme[] BuiltIn =
    [
        new("FluxTube", "Свои", false, 0xFF0E0E11, 0xFF151519, 0xFF1C1C22, 0xFFF2F2F4, 0xFFA6A6B2, 0xFF6C6C78, 0xFFFF3355),
        new("Новогодняя", "Свои", false, 0xFF0A1220, 0xFF101C30, 0xFF172740, 0xFFF3F7FF, 0xFF9FB5D1, 0xFF647B99, 0xFFE63946),
        new("Чёрно-оранжевая", "Свои", false, 0xFF000000, 0xFF121212, 0xFF1E1E1E, 0xFFFFFFFF, 0xFFC6C6C6, 0xFF8A8A8A, 0xFFFF9000, 0xFF000000),
        new("AMOLED", "Свои", false, 0xFF000000, 0xFF0A0A0A, 0xFF151515, 0xFFEDEDED, 0xFF9A9A9A, 0xFF5E5E5E, 0xFFFF3355),

        new("YouTube тёмная", "YouTube", false, 0xFF0F0F0F, 0xFF212121, 0xFF272727, 0xFFF1F1F1, 0xFFAAAAAA, 0xFF717171, 0xFFFF0033),
        new("YouTube светлая", "YouTube", true, 0xFFFFFFFF, 0xFFF9F9F9, 0xFFF2F2F2, 0xFF0F0F0F, 0xFF606060, 0xFF909090, 0xFFFF0033),
        new("Старый YouTube", "YouTube", true, 0xFFF1F1F1, 0xFFFBFBFB, 0xFFE3E3E3, 0xFF222222, 0xFF666666, 0xFF999999, 0xFFCC181E),

        new("One Dark", "Редакторы", false, 0xFF282C33, 0xFF2F343E, 0xFF3B414D, 0xFFDCE0E5, 0xFFA9AFBC, 0xFF878A98, 0xFF74ADE8, 0xFF1B1F27),
        new("One Light", "Редакторы", true, 0xFFFAFAFA, 0xFFEBEBEC, 0xFFE5E5E6, 0xFF242529, 0xFF58585A, 0xFFA1A1A3, 0xFF5C78E2),
        new("Ayu Dark", "Редакторы", false, 0xFF0D1017, 0xFF131721, 0xFF1C212B, 0xFFBFBDB6, 0xFF8A8986, 0xFF5C6773, 0xFFE6B450, 0xFF0D1017),
        new("Gruvbox", "Редакторы", false, 0xFF282828, 0xFF32302F, 0xFF3C3836, 0xFFEBDBB2, 0xFFBDAE93, 0xFF928374, 0xFFFE8019, 0xFF282828),
        new("Catppuccin Mocha", "Редакторы", false, 0xFF1E1E2E, 0xFF181825, 0xFF313244, 0xFFCDD6F4, 0xFFA6ADC8, 0xFF7F849C, 0xFFCBA6F7, 0xFF1E1E2E),
        new("Catppuccin Latte", "Редакторы", true, 0xFFEFF1F5, 0xFFE6E9EF, 0xFFDCE0E8, 0xFF4C4F69, 0xFF6C6F85, 0xFF9CA0B0, 0xFF8839EF),
        new("Dracula", "Редакторы", false, 0xFF282A36, 0xFF21222C, 0xFF44475A, 0xFFF8F8F2, 0xFFBDC0D0, 0xFF6272A4, 0xFFFF79C6, 0xFF282A36),
        new("Nord", "Редакторы", false, 0xFF2E3440, 0xFF3B4252, 0xFF434C5E, 0xFFECEFF4, 0xFFAEB7C6, 0xFF7B88A1, 0xFF88C0D0, 0xFF2E3440),
        new("Tokyo Night", "Редакторы", false, 0xFF1A1B26, 0xFF16161E, 0xFF24283B, 0xFFC0CAF5, 0xFFA9B1D6, 0xFF565F89, 0xFF7AA2F7, 0xFF1A1B26),
        new("Rosé Pine", "Редакторы", false, 0xFF191724, 0xFF1F1D2E, 0xFF26233A, 0xFFE0DEF4, 0xFF908CAA, 0xFF6E6A86, 0xFFEBBCBA, 0xFF191724),
        new("Solarized Light", "Редакторы", true, 0xFFFDF6E3, 0xFFEEE8D5, 0xFFEEE8D5, 0xFF073642, 0xFF586E75, 0xFF93A1A1, 0xFFCB4B16),
        new("VS Code", "Редакторы", false, 0xFF1F1F1F, 0xFF181818, 0xFF2B2B2B, 0xFFCCCCCC, 0xFF9D9D9D, 0xFF6E7681, 0xFF0078D4),
        new("Monokai", "Редакторы", false, 0xFF272822, 0xFF1E1F1C, 0xFF3E3D32, 0xFFF8F8F2, 0xFFCFCFC2, 0xFF75715E, 0xFFF92672),
        new("GitHub Dark", "Редакторы", false, 0xFF0D1117, 0xFF161B22, 0xFF21262D, 0xFFE6EDF3, 0xFF8B949E, 0xFF6E7681, 0xFF2F81F7),
    ];

    public const string CustomGroup = "Мои темы (код)";
    public static string CustomDir
    {
        get
        {
            var d = Path.Combine(Paths.Data, "themes");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static readonly Dictionary<string, string> _customFiles = new(StringComparer.OrdinalIgnoreCase);
    public static Theme[] All { get; private set; } = BuiltIn;
    public static (string Group, Theme[] Items)[] Groups { get; private set; } = BuildGroups(BuiltIn);

    /// <summary>Accent colours offered on top of any theme ("" = the theme's own).</summary>
    public static readonly (string Name, uint Color)[] Accents =
    [
        ("Красный YouTube", 0xFFFF0033), ("Малиновый", 0xFFFF3355), ("Оранжевый", 0xFFFF9000), ("Жёлтый", 0xFFFFC400),
        ("Зелёный", 0xFF2BD576), ("Бирюзовый", 0xFF1ABC9C), ("Голубой", 0xFF3EA6FF), ("Синий", 0xFF2F6BFF),
        ("Фиолетовый", 0xFF9B6CFF), ("Розовый", 0xFFFF5FA2),
    ];

    public static Theme Current { get; private set; } = BuiltIn[0];
    public static bool IsNewYear => Current.Name == "Новогодняя";
    public static bool IsCustom(string name) => _customFiles.ContainsKey(name);

    private static (string Group, Theme[] Items)[] BuildGroups(Theme[] list)
        => list.GroupBy(t => t.Group).Select(g => (g.Key, g.ToArray())).ToArray();

    public static void ReloadCustom()
    {
        _customFiles.Clear();
        var custom = new List<Theme>();
        try
        {
            if (Directory.Exists(CustomDir))
            {
                foreach (var file in Directory.GetFiles(CustomDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var raw = File.ReadAllText(file);
                        if (TryParseCode(raw, out var t, out _) && t != null)
                        {
                            var clean = t with { Group = CustomGroup };
                            if (BuiltIn.Any(b => b.Name.Equals(clean.Name, StringComparison.OrdinalIgnoreCase)))
                                clean = clean with { Name = clean.Name + " (своя)" };
                            if (_customFiles.ContainsKey(clean.Name)) continue;
                            _customFiles[clean.Name] = file;
                            custom.Add(clean);
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception e) { Paths.Log("themes reload: " + e.Message); }

        All = custom.Count > 0 ? [.. custom, .. BuiltIn] : BuiltIn;
        Groups = BuildGroups(All);
    }

    public static string ExportCode(Theme t, string? nameOverride = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"name\": \"{(nameOverride ?? t.Name).Replace("\"", "'")}\",");
        sb.AppendLine($"  \"light\": {(t.Light ? "true" : "false")},");
        sb.AppendLine($"  \"bg\": \"{Hex(t.Bg)}\",");
        sb.AppendLine($"  \"panel\": \"{Hex(t.Panel)}\",");
        sb.AppendLine($"  \"card\": \"{Hex(t.Card)}\",");
        sb.AppendLine($"  \"text\": \"{Hex(t.Text)}\",");
        sb.AppendLine($"  \"sub\": \"{Hex(t.Sub)}\",");
        sb.AppendLine($"  \"dim\": \"{Hex(t.Dim)}\",");
        sb.AppendLine($"  \"brand\": \"{Hex(t.Brand)}\",");
        sb.AppendLine($"  \"onBrand\": \"{Hex(t.OnBrand)}\"");
        sb.Append('}');
        return sb.ToString();
    }

    public static bool TryParseColor(string? s, out uint color)
    {
        color = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Trim('"', '\'', ',', ';');
        if (s.StartsWith('#')) s = s[1..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 3) s = new string([s[0], s[0], s[1], s[1], s[2], s[2]]);
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, null, out var rgb))
        {
            color = 0xFF000000u | rgb;
            return true;
        }
        if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, null, out var argb))
        {
            color = argb | 0xFF000000u;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a custom theme from either JSON ({"name":"Cyber","bg":"#08090F","brand":"#00F5D4",...})
    /// OR relaxed key: value / CSS-like lines (name: Cyber\nbg: #08090F\nbrand: #00F5D4).
    /// Unspecified secondary colours (panel, card, sub, dim, onBrand) are smartly derived automatically!
    /// </summary>
    public static bool TryParseCode(string code, out Theme? theme, out string? error)
    {
        theme = null;
        error = null;
        if (string.IsNullOrWhiteSpace(code)) { error = "Введите JSON или список цветов (например bg: #0D0F18)"; return false; }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = code.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonObject obj)
                {
                    foreach (var kv in obj)
                        if (kv.Value != null) dict[kv.Key] = kv.Value.ToString();
                }
            }
            catch (Exception e) { error = "Ошибка JSON: " + e.Message; return false; }
        }
        else
        {
            foreach (var rawLine in trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#')) continue;
                int sep = line.IndexOfAny([':', '=']);
                if (sep <= 0) continue;
                var k = line[..sep].Trim().TrimStart('-').Trim('"', '\'');
                var v = line[(sep + 1)..].Trim().TrimEnd(',', ';').Trim('"', '\'');
                if (k.Length > 0 && v.Length > 0) dict[k] = v;
            }
        }

        string name = dict.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : "Моя тема";
        if (name.Length > 32) name = name[..32].Trim();

        uint bg = TryGetColor(dict, "bg") ?? TryGetColor(dict, "background") ?? 0;
        if (bg == 0) { error = "Укажите цвет фона \"bg\": \"#RRGGBB\""; return false; }

        bool light = dict.TryGetValue("light", out var ls)
            ? ls.Equals("true", StringComparison.OrdinalIgnoreCase) || ls == "1"
            : Luma(bg) > 0.55f;

        uint ink = light ? 0xFF000000u : 0xFFFFFFFFu;
        uint brand = TryGetColor(dict, "brand") ?? TryGetColor(dict, "accent") ?? 0xFFFF3355u;
        uint panel = TryGetColor(dict, "panel") ?? Gfx.Mix(bg, ink, light ? 0.03f : 0.045f);
        uint card = TryGetColor(dict, "card") ?? Gfx.Mix(bg, ink, light ? 0.06f : 0.085f);
        uint text = TryGetColor(dict, "text") ?? (light ? 0xFF141418u : 0xFFF2F2F6u);
        uint sub = TryGetColor(dict, "sub") ?? Gfx.Mix(text, bg, 0.34f);
        uint dim = TryGetColor(dict, "dim") ?? Gfx.Mix(text, bg, 0.56f);
        uint onBrand = TryGetColor(dict, "onBrand") ?? (Luma(brand) > 0.62f ? 0xFF111111u : 0xFFFFFFFFu);

        theme = new Theme(name, CustomGroup, light, bg, panel, card, text, sub, dim, brand, onBrand);
        return true;
    }

    private static uint? TryGetColor(Dictionary<string, string> dict, string key)
        => dict.TryGetValue(key, out var v) && TryParseColor(v, out var c) ? c : null;

    private static string SafeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ' ? ch : '_');
        var res = sb.ToString().Trim();
        return res.Length > 0 ? res : "custom_theme";
    }

    public static bool SaveCustomFromCode(string code, out string? savedName, out string? error)
    {
        savedName = null;
        if (!TryParseCode(code, out var t, out error) || t == null) return false;
        if (BuiltIn.Any(b => b.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
            t = t with { Name = t.Name + " (своя)" };

        try
        {
            var dest = Path.Combine(CustomDir, SafeFileName(t.Name) + ".json");
            var tmp = dest + ".tmp";
            File.WriteAllText(tmp, ExportCode(t), Encoding.UTF8);
            File.Move(tmp, dest, overwrite: true);
            ReloadCustom();
            savedName = t.Name;
            Apply(theme: savedName, accent: "");
            return true;
        }
        catch (Exception e)
        {
            error = "Ошибка сохранения файла: " + e.Message;
            return false;
        }
    }

    public static bool DeleteCustom(string name)
    {
        if (!_customFiles.TryGetValue(name, out var path)) return false;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            ReloadCustom();
            if (Current.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) Apply(theme: BuiltIn[0].Name);
            else App.Invalidate();
            return true;
        }
        catch (Exception e) { Paths.Log("theme delete: " + e.Message); return false; }
    }

    public static Theme ByName(string? name) => All.FirstOrDefault(t => t.Name == name) ?? BuiltIn[0];

    private static string? _cachedAccentStr;
    private static uint? _cachedAccentVal;

    public static uint? AccentOverride
    {
        get
        {
            var s = AppSettings.Current.Accent;
            if (string.Equals(s, _cachedAccentStr, StringComparison.Ordinal)) return _cachedAccentVal;
            _cachedAccentStr = s;
            _cachedAccentVal = s is { Length: 7 } && s[0] == '#' && uint.TryParse(s[1..], NumberStyles.HexNumber, null, out var c)
                ? 0xFF000000 | c : null;
            return _cachedAccentVal;
        }
    }

    private static Pal _from, _to;
    private static long _startedAt;
    private static bool _moving;

    private struct Pal
    {
        public uint Bg, Panel, Card, Text, Sub, Dim, Brand, OnBrand, Ink, Hover, Press, Line, Thumb, Field, Menu, Icon, Track, Good, Warn;
        public bool Light;
    }

    private static Pal Derive(Theme t, uint? accent)
    {
        var ink = t.Light ? 0xFF000000 : 0xFFFFFFFF;
        uint A(float a) => ((uint)(a * 255 + 0.5f) << 24) | (ink & 0xFFFFFF);
        var brand = accent ?? t.Brand;
        var onBrand = accent != null ? (Luma(brand) > 0.62f ? 0xFF111111 : 0xFFFFFFFF) : t.OnBrand;
        return new Pal
        {
            Bg = t.Bg, Panel = t.Panel, Card = t.Card, Text = t.Text, Sub = t.Sub, Dim = t.Dim, Brand = brand, OnBrand = onBrand, Ink = ink,
            Hover = A(0.08f), Press = A(0.13f), Line = A(t.Light ? 0.1f : 0.094f),
            Thumb = Gfx.Mix(t.Card, ink, 0.03f),
            Field = t.Light ? Gfx.Mix(t.Bg, ink, 0.035f) : Gfx.Mix(t.Bg, t.Card, 0.6f),
            Menu = t.Light ? Gfx.Mix(t.Bg, 0xFFFFFFFF, 0.6f) : Gfx.Mix(t.Card, ink, 0.035f),
            Icon = Gfx.Mix(t.Sub, t.Text, 0.55f),
            Track = Gfx.Mix(t.Card, ink, t.Light ? 0.18f : 0.12f),
            Good = t.Light ? 0xFF1E8E3E : 0xFF7BD88F,
            Warn = t.Light ? 0xFFB26A00 : 0xFFFFC857,
            Light = t.Light,
        };
    }

    private static float Luma(uint c) => (0.2126f * ((c >> 16) & 255) + 0.7152f * ((c >> 8) & 255) + 0.0722f * (c & 255)) / 255f;

    private static void Set(in Pal p)
    {
        Th.Bg = p.Bg; Th.Panel = p.Panel; Th.Card = p.Card; Th.Text = p.Text; Th.Sub = p.Sub; Th.Dim = p.Dim; Th.Brand = p.Brand;
        Th.OnBrand = p.OnBrand; Th.Ink = p.Ink; Th.Hover = p.Hover; Th.Press = p.Press; Th.Line = p.Line; Th.Thumb = p.Thumb;
        Th.Field = p.Field; Th.Menu = p.Menu; Th.Icon = p.Icon; Th.Track = p.Track; Th.Good = p.Good; Th.Warn = p.Warn; Th.Light = p.Light;
    }

    private static Pal Blend(in Pal a, in Pal b, float t)
    {
        static uint M(uint x, uint y, float t) => Gfx.Mix(x, y, t);
        return new Pal
        {
            Bg = M(a.Bg, b.Bg, t), Panel = M(a.Panel, b.Panel, t), Card = M(a.Card, b.Card, t), Text = M(a.Text, b.Text, t),
            Sub = M(a.Sub, b.Sub, t), Dim = M(a.Dim, b.Dim, t), Brand = M(a.Brand, b.Brand, t), OnBrand = M(a.OnBrand, b.OnBrand, t),
            Ink = M(a.Ink, b.Ink, t), Hover = M(a.Hover, b.Hover, t), Press = M(a.Press, b.Press, t), Line = M(a.Line, b.Line, t),
            Thumb = M(a.Thumb, b.Thumb, t), Field = M(a.Field, b.Field, t), Menu = M(a.Menu, b.Menu, t), Icon = M(a.Icon, b.Icon, t),
            Track = M(a.Track, b.Track, t), Good = M(a.Good, b.Good, t), Warn = M(a.Warn, b.Warn, t),
            Light = t < 0.5f ? a.Light : b.Light,
        };
    }

    private static Pal Snapshot() => new()
    {
        Bg = Th.Bg, Panel = Th.Panel, Card = Th.Card, Text = Th.Text, Sub = Th.Sub, Dim = Th.Dim, Brand = Th.Brand, OnBrand = Th.OnBrand,
        Ink = Th.Ink, Hover = Th.Hover, Press = Th.Press, Line = Th.Line, Thumb = Th.Thumb, Field = Th.Field, Menu = Th.Menu,
        Icon = Th.Icon, Track = Th.Track, Good = Th.Good, Warn = Th.Warn, Light = Th.Light,
    };

    /// <summary>At start-up: load custom themes + saved theme, no easing.</summary>
    public static void Load()
    {
        ReloadCustom();
        Current = ByName(AppSettings.Current.Theme);
        _to = Derive(Current, AccentOverride);
        Set(_to);
        Changed?.Invoke();
    }

    /// <summary>Switches theme and/or accent (null keeps the current one) and saves the choice.</summary>
    public static void Apply(string? theme = null, string? accent = null)
    {
        if (theme != null)
        {
            var prev = AppSettings.Current.Theme;
            AppSettings.Current.Theme = theme;
            if (theme == "Новогодняя" && prev != "Новогодняя" && AppSettings.Current.SeasonalEffect == "none")
                AppSettings.Current.SeasonalEffect = "newyear";
        }
        if (accent != null) AppSettings.Current.Accent = accent;
        AppSettings.Current.Save();
        Current = ByName(AppSettings.Current.Theme);
        _from = Snapshot();
        _to = Derive(Current, AccentOverride);
        _startedAt = UI.Now;
        _moving = true;
        Changed?.Invoke();
        App.Invalidate();
    }

    public static event Action? Changed;

    public static void Tick()
    {
        if (!_moving) return;
        var t = Math.Clamp((UI.Now - _startedAt) / 260f, 0, 1);
        var e = t * t * (3 - 2 * t);
        Set(Blend(_from, _to, e));
        if (t >= 1) { _moving = false; Set(_to); Changed?.Invoke(); }
        UI.Animate();
    }

    public static (uint Bg, uint Panel, uint Card, uint Text, uint Sub, uint Brand, uint OnBrand, uint Line) Preview(Theme t, bool useAccentOverride = true)
    {
        var p = Derive(t, useAccentOverride ? AccentOverride : null);
        return (p.Bg, p.Panel, p.Card, p.Text, p.Sub, p.Brand, p.OnBrand, p.Line);
    }

    public static string Hex(uint c) => "#" + (c & 0xFFFFFF).ToString("X6");
}
