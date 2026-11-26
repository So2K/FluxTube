using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FluxTube.Services;

namespace FluxTube;

/// <summary>One colour theme: base palette + optional secondary accent &amp; full modular style preset (backdrop, pattern, corners, card frame, font, neon glow).</summary>
public sealed record Theme(
    string Name, string Group, bool Light,
    uint Bg, uint Panel, uint Card, uint Text, uint Sub, uint Dim, uint Brand,
    uint OnBrand = 0xFFFFFFFF,
    uint Brand2 = 0,
    string? DefaultBackdrop = null,
    string? DefaultPattern = null,
    bool DefaultNeonGlow = false,
    string? DefaultTint = null,
    string? DefaultCorners = null,
    string? DefaultCardStyle = null,
    string? DefaultFont = null,
    string? DefaultEffect = null);

/// <summary>
/// Built-in &amp; user-authored custom code themes (stored in %USERPROFILE%\.fluxtube\themes\*.json).
/// Switching eases every colour smoothly over 260 ms.
/// </summary>
public static class Themes
{
    public const string AdaptiveVideoName = "Адаптивная (видео)";

    public static readonly Theme[] BuiltIn =
    [
        new("FluxTube", "Свои", false, 0xFF0E0E11, 0xFF151519, 0xFF1C1C22, 0xFFF2F2F4, 0xFFA6A6B2, 0xFF6C6C78, 0xFFFF3355, 0xFFFFFFFF, 0xFF7B61FF),
        new(AdaptiveVideoName, "Свои", false, 0xFF0E0E13, 0xFF15151C, 0xFF1D1D26, 0xFFF3F3F7, 0xFFA6A7B5, 0xFF6B6C7A, 0xFF7B61FF, 0xFFFFFFFF, 0xFFFF4DA6, DefaultBackdrop: "mesh", DefaultNeonGlow: true),
        new("Новогодняя", "Свои", false, 0xFF0A1220, 0xFF101C30, 0xFF172740, 0xFFF3F7FF, 0xFF9FB5D1, 0xFF647B99, 0xFFE63946, 0xFFFFFFFF, 0xFF56CFE1, DefaultEffect: "newyear"),
        new("Чёрно-оранжевая", "Свои", false, 0xFF000000, 0xFF121212, 0xFF1E1E1E, 0xFFFFFFFF, 0xFFC6C6C6, 0xFF8A8A8A, 0xFFFF9000, 0xFF000000, 0xFFFF5400),
        new("AMOLED", "Свои", false, 0xFF000000, 0xFF0A0A0A, 0xFF151515, 0xFFEDEDED, 0xFF9A9A9A, 0xFF5E5E5E, 0xFFFF3355, 0xFFFFFFFF, 0xFF9B6CFF),

        new("Ультра Кавай", "Эстетика", true, 0xFFFFF6FA, 0xFFFFD8EA, 0xFFFFFFFF, 0xFF47203F, 0xFF8F5983, 0xFFBE92B4, 0xFFFF5FA2, 0xFFFFFFFF, 0xFFA78BFA, "kawaii", "paws", false, "dual", "round", "dashed", "soft"),
        new("Ультра Кавай Чёрный", "Эстетика", false, 0xFF040406, 0xFF08080C, 0xFF0E0D13, 0xFFFFF5FC, 0xFFCFA6C4, 0xFF7D5D76, 0xFFFF4DA6, 0xFFFFFFFF, 0xFFB87BFF, "kawaii", "paws", true, "dual", "round", "dashed", "soft"),
        new("Кавай Ночь", "Эстетика", false, 0xFF160D1E, 0xFF21132E, 0xFF2B1A3D, 0xFFFFF0FA, 0xFFCFA7C7, 0xFF8A6585, 0xFFFF6EC7, 0xFF1A0816, 0xFF7DE2FC, "kawaii", "paws", true, "dual", "round", "dashed", "soft"),
        new("Сакура", "Эстетика", true, 0xFFFDF5F7, 0xFFF9E0E8, 0xFFFFFFFF, 0xFF3E212C, 0xFF8A5D6E, 0xFFB8909F, 0xFFEC5E8C, 0xFFFFFFFF, 0xFFFFAD8F, "kawaii", "sakura", false, "dual", "round", "dashed", "soft"),
        new("Ультра Неон", "Эстетика", false, 0xFF05040C, 0xFF0B091A, 0xFF130F29, 0xFFF4F6FF, 0xFFA6A9D6, 0xFF636794, 0xFFFF00B8, 0xFFFFFFFF, 0xFF00F5FF, "neon", "circuit", true, "dual", "normal", "neon", "mono"),
        new("Аврора", "Эстетика", false, 0xFF07101C, 0xFF0D1A2B, 0xFF14253B, 0xFFECF6FF, 0xFF96B5D4, 0xFF5A7694, 0xFF38BDF8, 0xFF06101B, 0xFFC084FC, "mesh", "waves", true, "dual", "round", "framed", "segoe"),
        new("Монохром", "Эстетика", true, 0xFFF4F4F0, 0xFFECECE7, 0xFFFFFFFF, 0xFF0A0A0A, 0xFF454545, 0xFF7E7E7E, 0xFF0A0A0A, 0xFFFFFFFF, 0xFF555555, "off", "none", false, "theme", "sharp", "brutal", "tech"),

        new("MiSide", "Игры и кино", false, 0xFF1E071B, 0xFF300F2C, 0xFF4A1F44, 0xFFFFF2FA, 0xFFE3B6D8, 0xFF9F6A93, 0xFFFF4B9F, 0xFFFFFFFF, 0xFFFF99CC, "kawaii", "hearts", true, "dual", "round", "neon", "tech"),
        new("RE Requiem", "Игры и кино", false, 0xFF0B0B0C, 0xFF121214, 0xFF1A1A1D, 0xFFF0F0EE, 0xFF9C9C9A, 0xFF5E5E5C, 0xFFEAEAE6, 0xFF0A0A0B, 0xFF78787C, "noir", "noir_web", false, "theme", "sharp", "framed", "serif"),
        new("RE Village", "Игры и кино", false, 0xFF0D0F13, 0xFF14171E, 0xFF1B1F28, 0xFFECEEF2, 0xFF969CA8, 0xFF5C626E, 0xFFC93838, 0xFFFFFFFF, 0xFF8C94A4, "noir", "lines", false, "theme", "sharp", "framed", "tech"),
        new("Alien: Космос", "Игры и кино", false, 0xFF030406, 0xFF090B10, 0xFF11141C, 0xFFF2F4F8, 0xFF98A0AD, 0xFF5C6470, 0xFFE08743, 0xFF050506, 0xFFC8D6E8, "orbital", "stars", false, "theme", "sharp", "framed", "tech"),
        new("Silent Hill 2", "Игры и кино", false, 0xFF111512, 0xFF191F1B, 0xFF222A24, 0xFFE0E6DF, 0xFF94A394, 0xFF5C695D, 0xFFC94A38, 0xFFFFFFFF, 0xFF7D917E, "noir", "scanlines", false, "theme", "sharp", "framed", "serif"),
        new("Persona 5", "Игры и кино", false, 0xFF0D0A0B, 0xFF170B0E, 0xFF221015, 0xFFFFFFFF, 0xFFD4A8AE, 0xFF8C5B63, 0xFFE60012, 0xFFFFFFFF, 0xFFFFFFFF, "neon", "lines", true, "brand", "sharp", "brutal", "tech"),
        new("NieR: Automata", "Игры и кино", true, 0xFFDAD4BB, 0xFFCEC7AC, 0xFFE8E2CA, 0xFF3E3B33, 0xFF6B6759, 0xFF948E7D, 0xFF454239, 0xFFECE7D3, 0xFF8F866B, "off", "grid", false, "theme", "sharp", "brutal", "tech"),
        new("Hollow Knight", "Игры и кино", false, 0xFF0B0E18, 0xFF121728, 0xFF1A2238, 0xFFF0F4FF, 0xFF98A6CA, 0xFF5B688A, 0xFFEAF2FF, 0xFF0B0E18, 0xFF70B8FF, "noir", "stars", false, "dual", "normal", "framed", "serif"),
        new("Киберпанк", "Игры и кино", false, 0xFF08090F, 0xFF10121E, 0xFF181B2C, 0xFFF4F5FA, 0xFFA1A6C2, 0xFF646982, 0xFFFCEE0A, 0xFF08090F, 0xFF00F0FF, "neon", "grid", true, "dual", "sharp", "neon", "tech"),
        new("Синтвейв '84", "Игры и кино", false, 0xFF120924, 0xFF1A0F33, 0xFF241645, 0xFFFAF0FF, 0xFFC4A5E0, 0xFF7E6299, 0xFFFF2A85, 0xFFFFFFFF, 0xFF36F9F6, "neon", "scanlines", true, "dual", "normal", "neon", "segoe"),
        new("Матрица", "Игры и кино", false, 0xFF020804, 0xFF06120A, 0xFF0B1D11, 0xFFDCFFE6, 0xFF7CC995, 0xFF3F7A52, 0xFF00FF66, 0xFF020804, 0xFF00E5B0, "neon", "circuit", true, "brand", "sharp", "neon", "mono"),

        new("YouTube тёмная", "YouTube", false, 0xFF0F0F0F, 0xFF212121, 0xFF272727, 0xFFF1F1F1, 0xFFAAAAAA, 0xFF717171, 0xFFFF0033),
        new("YouTube светлая", "YouTube", true, 0xFFFFFFFF, 0xFFF9F9F9, 0xFFF2F2F2, 0xFF0F0F0F, 0xFF606060, 0xFF909090, 0xFFFF0033),
        new("Старый YouTube", "YouTube", true, 0xFFF1F1F1, 0xFFFBFBFB, 0xFFE3E3E3, 0xFF222222, 0xFF666666, 0xFF999999, 0xFFCC181E),

        new("One Dark", "Редакторы", false, 0xFF282C33, 0xFF2F343E, 0xFF3B414D, 0xFFDCE0E5, 0xFFA9AFBC, 0xFF878A98, 0xFF74ADE8, 0xFF1B1F27),
        new("One Light", "Редакторы", true, 0xFFFAFAFA, 0xFFEBEBEC, 0xFFE5E5E6, 0xFF242529, 0xFF58585A, 0xFFA1A1A3, 0xFF5C78E2),
        new("Ayu Dark", "Редакторы", false, 0xFF0D1017, 0xFF131721, 0xFF1C212B, 0xFFBFBDB6, 0xFF8A8986, 0xFF5C6773, 0xFFE6B450, 0xFF0D1017),
        new("Gruvbox", "Редакторы", false, 0xFF282828, 0xFF32302F, 0xFF3C3836, 0xFFEBDBB2, 0xFFBDAE93, 0xFF928374, 0xFFFE8019, 0xFF282828),
        new("Catppuccin Mocha", "Редакторы", false, 0xFF1E1E2E, 0xFF181825, 0xFF313244, 0xFFCDD6F4, 0xFFA6ADC8, 0xFF7F849C, 0xFFCBA6F7, 0xFF1E1E2E, 0xFFF5C2E7),
        new("Catppuccin Latte", "Редакторы", true, 0xFFEFF1F5, 0xFFE6E9EF, 0xFFDCE0E8, 0xFF4C4F69, 0xFF6C6F85, 0xFF9CA0B0, 0xFF8839EF, 0xFFFFFFFF, 0xFFEA76CB),
        new("Dracula", "Редакторы", false, 0xFF282A36, 0xFF21222C, 0xFF44475A, 0xFFF8F8F2, 0xFFBDC0D0, 0xFF6272A4, 0xFFFF79C6, 0xFF282A36, 0xFF8BE9FD),
        new("Nord", "Редакторы", false, 0xFF2E3440, 0xFF3B4252, 0xFF434C5E, 0xFFECEFF4, 0xFFAEB7C6, 0xFF7B88A1, 0xFF88C0D0, 0xFF2E3440, 0xFF81A1C1),
        new("Tokyo Night", "Редакторы", false, 0xFF1A1B26, 0xFF16161E, 0xFF24283B, 0xFFC0CAF5, 0xFFA9B1D6, 0xFF565F89, 0xFF7AA2F7, 0xFF1A1B26, 0xFFBB9AF7),
        new("Rosé Pine", "Редакторы", false, 0xFF191724, 0xFF1F1D2E, 0xFF26233A, 0xFFE0DEF4, 0xFF908CAA, 0xFF6E6A86, 0xFFEBBCBA, 0xFF191724, 0xFFC4A7E7),
        new("Solarized Light", "Редакторы", true, 0xFFFDF6E3, 0xFFEEE8D5, 0xFFEEE8D5, 0xFF073642, 0xFF586E75, 0xFF93A1A1, 0xFFCB4B16),
        new("VS Code", "Редакторы", false, 0xFF1F1F1F, 0xFF181818, 0xFF2B2B2B, 0xFFCCCCCC, 0xFF9D9D9D, 0xFF6E7681, 0xFF0078D4),
        new("Monokai", "Редакторы", false, 0xFF272822, 0xFF1E1F1C, 0xFF3E3D32, 0xFFF8F8F2, 0xFFCFCFC2, 0xFF75715E, 0xFFF92672, 0xFFFFFFFF, 0xFF66D9EF),
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

    public static string GroupLabel(string group) => group switch
    {
        "Свои" => L.T("Свои", "FluxTube"),
        "Эстетика" => L.T("Эстетика", "Aesthetic"),
        "Игры и кино" => L.T("Игры и кино", "Games & Cinema"),
        "Редакторы" => L.T("Редакторы", "Editors"),
        CustomGroup => L.T("Мои темы (код)", "My themes (code)"),
        _ => group,
    };

    public static string DisplayName(string name) => !L.En ? name : name switch
    {
        AdaptiveVideoName => "Adaptive (Video)",
        "Новогодняя" => "New Year",
        "Чёрно-оранжевая" => "Black & Orange",
        "Ультра Кавай" => "Ultra Kawaii",
        "Ультра Кавай Чёрный" => "Ultra Kawaii Black",
        "Кавай Ночь" => "Kawaii Night",
        "Сакура" => "Sakura",
        "Ультра Неон" => "Ultra Neon",
        "Аврора" => "Aurora",
        "Монохром" => "Monochrome Swiss",
        "Alien: Космос" => "Alien: Orbital",
        "Киберпанк" => "Cyberpunk",
        "Синтвейв '84" => "Synthwave '84",
        "Матрица" => "Matrix",
        "YouTube тёмная" => "YouTube Dark",
        "YouTube светлая" => "YouTube Light",
        "Старый YouTube" => "Classic YouTube",
        _ => name,
    };

    public static string AccentLabel(string name) => !L.En ? name : name switch
    {
        "Красный YouTube" => "YouTube Red",
        "Малиновый" => "Crimson",
        "Оранжевый" => "Orange",
        "Жёлтый" => "Yellow",
        "Зелёный" => "Green",
        "Бирюзовый" => "Teal",
        "Голубой" => "Sky Blue",
        "Синий" => "Royal Blue",
        "Фиолетовый" => "Violet",
        "Розовый" => "Pink",
        _ => name,
    };

    public static Theme Current { get; private set; } = BuiltIn[0];
    public static bool IsNewYear => Current.Name == "Новогодняя";
    public static bool IsAdaptiveVideo => Current.Name == AdaptiveVideoName;
    public static bool IsCustom(string name) => _customFiles.ContainsKey(name);

    private static string? _adaptiveVid;
    private static uint _adaptiveDom1, _adaptiveDom2;
    private static Theme? _adaptiveLiveTheme;

    /// <summary>Builds a harmonious, crisp dark palette automatically adapted from a video's dominant cover colors.</summary>
    private static Theme BuildAdaptiveTheme(uint dom1, uint dom2)
    {
        static uint BoostBrand(uint c)
        {
            float r = ((c >> 16) & 255) / 255f, g = ((c >> 8) & 255) / 255f, b = (c & 255) / 255f;
            float avg = (r + g + b) / 3f;
            r = Math.Clamp(avg + (r - avg) * 1.45f, 0f, 1f);
            g = Math.Clamp(avg + (g - avg) * 1.45f, 0f, 1f);
            b = Math.Clamp(avg + (b - avg) * 1.45f, 0f, 1f);
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max < 0.58f && max > 0.02f) { float k = 0.68f / max; r = MathF.Min(1f, r * k); g = MathF.Min(1f, g * k); b = MathF.Min(1f, b * k); }
            return 0xFF000000u | ((uint)(r * 255f + 0.5f) << 16) | ((uint)(g * 255f + 0.5f) << 8) | (uint)(b * 255f + 0.5f);
        }

        uint brand = BoostBrand(dom1);
        uint brand2 = dom2 != 0 && dom2 != dom1 ? BoostBrand(dom2) : ShiftHue(brand, 42f);
        uint bg = Gfx.Mix(0xFF09090D, brand, 0.09f);
        uint panel = Gfx.Mix(0xFF111117, brand, 0.14f);
        uint card = Gfx.Mix(0xFF191922, brand, 0.18f);
        uint text = Gfx.Mix(0xFFF4F4F8, brand, 0.05f);
        uint sub = Gfx.Mix(0xFFA2A4B4, brand, 0.17f);
        uint dim = Gfx.Mix(0xFF646778, brand, 0.15f);
        uint onBrand = Luma(brand) > 0.62f ? 0xFF0E0E12 : 0xFFFFFFFF;
        return new Theme(AdaptiveVideoName, "Свои", false, bg, panel, card, text, sub, dim, brand, onBrand, brand2,
            DefaultBackdrop: "mesh", DefaultNeonGlow: true);
    }

    private static void SyncAdaptiveFromOpenVideo()
    {
        if (!IsAdaptiveVideo) return;
        string? vid = Shell.AdaptiveVideoId();
        if (string.IsNullOrEmpty(vid)) return;
        if (!MusicArt.TryGetColors(vid, out uint dom1, out uint dom2)) return;
        if (_adaptiveVid == vid && _adaptiveDom1 == dom1 && _adaptiveDom2 == dom2 && _adaptiveLiveTheme != null)
            return;

        _adaptiveVid = vid;
        _adaptiveDom1 = dom1;
        _adaptiveDom2 = dom2;
        _adaptiveLiveTheme = BuildAdaptiveTheme(dom1, dom2);
        _from = Snapshot();
        _to = Derive(_adaptiveLiveTheme, null);
        _startedAt = UI.Now;
        _moving = true;
        Changed?.Invoke();
        UI.Animate();
    }

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
        if (t.Brand2 != 0) sb.AppendLine($"  \"brand2\": \"{Hex(t.Brand2)}\",");
        sb.AppendLine($"  \"onBrand\": \"{Hex(t.OnBrand)}\",");
        sb.AppendLine($"  \"backdrop\": \"{t.DefaultBackdrop ?? "matte"}\",");
        sb.AppendLine($"  \"pattern\": \"{t.DefaultPattern ?? "none"}\",");
        sb.AppendLine($"  \"patternTint\": \"{t.DefaultTint ?? "theme"}\",");
        sb.AppendLine($"  \"corners\": \"{t.DefaultCorners ?? "normal"}\",");
        sb.AppendLine($"  \"cardStyle\": \"{t.DefaultCardStyle ?? "clean"}\",");
        sb.AppendLine($"  \"font\": \"{t.DefaultFont ?? "segoe"}\",");
        sb.AppendLine($"  \"neonGlow\": {(t.DefaultNeonGlow ? "true" : "false")}");
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Captures the exact active colors + backdrop + pattern + corners + card frame + font + glow as a Theme ready to save or share.</summary>
    public static Theme CaptureCurrentLook(string? name = null)
    {
        var s = AppSettings.Current;
        var baseT = Current;
        return new Theme(
            name ?? (baseT.Name + " Remix"),
            CustomGroup,
            Th.Light,
            Th.Bg, Th.Panel, Th.Card, Th.Text, Th.Sub, Th.Dim, Th.Brand, Th.OnBrand,
            Th.Brand2,
            s.FrostedGlass,
            s.BgPattern,
            s.UiNeonGlow,
            s.BgPatternTint,
            s.UiCornerStyle,
            s.UiCardStyle,
            s.UiFont,
            s.SeasonalEffect == "none" ? null : s.SeasonalEffect);
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
        if (string.IsNullOrWhiteSpace(code)) { error = L.T("Введите JSON или список цветов (например bg: #0D0F18)", "Enter JSON or color lines (e.g. bg: #0D0F18)"); return false; }

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
            catch (Exception e) { error = L.T("Ошибка JSON: ", "JSON error: ") + e.Message; return false; }
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

        string name = dict.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : L.T("Моя тема", "My theme");
        if (name.Length > 32) name = name[..32].Trim();

        uint bg = TryGetColor(dict, "bg") ?? TryGetColor(dict, "background") ?? 0;
        if (bg == 0) { error = L.T("Укажите цвет фона \"bg\": \"#RRGGBB\"", "Specify background color \"bg\": \"#RRGGBB\""); return false; }

        bool light = dict.TryGetValue("light", out var ls)
            ? ls.Equals("true", StringComparison.OrdinalIgnoreCase) || ls == "1"
            : Luma(bg) > 0.55f;

        uint ink = light ? 0xFF000000u : 0xFFFFFFFFu;
        uint brand = TryGetColor(dict, "brand") ?? TryGetColor(dict, "accent") ?? 0xFFFF3355u;
        uint brand2 = TryGetColor(dict, "brand2") ?? TryGetColor(dict, "accent2") ?? 0u;
        uint panel = TryGetColor(dict, "panel") ?? Gfx.Mix(bg, ink, light ? 0.03f : 0.045f);
        uint card = TryGetColor(dict, "card") ?? Gfx.Mix(bg, ink, light ? 0.06f : 0.085f);
        uint text = TryGetColor(dict, "text") ?? (light ? 0xFF141418u : 0xFFF2F2F6u);
        uint sub = TryGetColor(dict, "sub") ?? Gfx.Mix(text, bg, 0.34f);
        uint dim = TryGetColor(dict, "dim") ?? Gfx.Mix(text, bg, 0.56f);
        uint onBrand = TryGetColor(dict, "onBrand") ?? (Luma(brand) > 0.62f ? 0xFF111111u : 0xFFFFFFFFu);

        string? Opt(string k) => dict.TryGetValue(k, out var val) && !string.IsNullOrWhiteSpace(val) ? val.Trim().ToLowerInvariant() : null;
        bool neonGlow = dict.TryGetValue("neonGlow", out var ng) && (ng.Equals("true", StringComparison.OrdinalIgnoreCase) || ng == "1");

        theme = new Theme(name, CustomGroup, light, bg, panel, card, text, sub, dim, brand, onBrand, brand2,
            Opt("backdrop"), Opt("pattern"), neonGlow, Opt("patternTint"), Opt("corners"), Opt("cardStyle"), Opt("font"), Opt("effect"));
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
        public uint Bg, Panel, Card, Text, Sub, Dim, Brand, Brand2, OnBrand, Ink, Hover, Press, Line, Thumb, Field, Menu, Icon, Track, Good, Warn;
        public bool Light;
    }

    private static Pal Derive(Theme t, uint? accent)
    {
        var ink = t.Light ? 0xFF000000 : 0xFFFFFFFF;
        uint A(float a) => ((uint)(a * 255 + 0.5f) << 24) | (ink & 0xFFFFFF);
        var brand = accent ?? t.Brand;
        var brand2 = t.Brand2 != 0 ? t.Brand2 : ShiftHue(brand, 48f);
        var onBrand = accent != null ? (Luma(brand) > 0.62f ? 0xFF111111 : 0xFFFFFFFF) : t.OnBrand;
        return new Pal
        {
            Bg = t.Bg, Panel = t.Panel, Card = t.Card, Text = t.Text, Sub = t.Sub, Dim = t.Dim, Brand = brand, Brand2 = brand2, OnBrand = onBrand, Ink = ink,
            Hover = A(0.08f), Press = A(0.13f), Line = A(t.Light ? 0.12f : 0.098f),
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

    private static uint ShiftHue(uint argb, float deg)
    {
        float r = ((argb >> 16) & 255) / 255f, g = ((argb >> 8) & 255) / 255f, b = (argb & 255) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), d = max - min;
        float h = 0f, s = max < 1e-4f ? 0f : d / max, v = max;
        if (d > 1e-4f)
        {
            if (max == r) h = (g - b) / d + (g < b ? 6f : 0f);
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h *= 60f;
        }
        h = (h + deg) % 360f;
        if (h < 0f) h += 360f;
        int hi = (int)(h / 60f) % 6;
        float f = h / 60f - MathF.Floor(h / 60f);
        float p = v * (1f - s), q = v * (1f - f * s), t = v * (1f - (1f - f) * s);
        (r, g, b) = hi switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return 0xFF000000u | ((uint)(r * 255f + 0.5f) << 16) | ((uint)(g * 255f + 0.5f) << 8) | (uint)(b * 255f + 0.5f);
    }

    private static float Luma(uint c) => (0.2126f * ((c >> 16) & 255) + 0.7152f * ((c >> 8) & 255) + 0.0722f * (c & 255)) / 255f;

    private static void Set(in Pal p)
    {
        Th.Bg = p.Bg; Th.Panel = p.Panel; Th.Card = p.Card; Th.Text = p.Text; Th.Sub = p.Sub; Th.Dim = p.Dim; Th.Brand = p.Brand; Th.Brand2 = p.Brand2;
        Th.OnBrand = p.OnBrand; Th.Ink = p.Ink; Th.Hover = p.Hover; Th.Press = p.Press; Th.Line = p.Line; Th.Thumb = p.Thumb;
        Th.Field = p.Field; Th.Menu = p.Menu; Th.Icon = p.Icon; Th.Track = p.Track; Th.Good = p.Good; Th.Warn = p.Warn; Th.Light = p.Light;
    }

    private static Pal Blend(in Pal a, in Pal b, float t)
    {
        static uint M(uint x, uint y, float t) => Gfx.Mix(x, y, t);
        return new Pal
        {
            Bg = M(a.Bg, b.Bg, t), Panel = M(a.Panel, b.Panel, t), Card = M(a.Card, b.Card, t), Text = M(a.Text, b.Text, t),
            Sub = M(a.Sub, b.Sub, t), Dim = M(a.Dim, b.Dim, t), Brand = M(a.Brand, b.Brand, t), Brand2 = M(a.Brand2, b.Brand2, t), OnBrand = M(a.OnBrand, b.OnBrand, t),
            Ink = M(a.Ink, b.Ink, t), Hover = M(a.Hover, b.Hover, t), Press = M(a.Press, b.Press, t), Line = M(a.Line, b.Line, t),
            Thumb = M(a.Thumb, b.Thumb, t), Field = M(a.Field, b.Field, t), Menu = M(a.Menu, b.Menu, t), Icon = M(a.Icon, b.Icon, t),
            Track = M(a.Track, b.Track, t), Good = M(a.Good, b.Good, t), Warn = M(a.Warn, b.Warn, t),
            Light = t < 0.5f ? a.Light : b.Light,
        };
    }

    private static Pal Snapshot() => new()
    {
        Bg = Th.Bg, Panel = Th.Panel, Card = Th.Card, Text = Th.Text, Sub = Th.Sub, Dim = Th.Dim, Brand = Th.Brand, Brand2 = Th.Brand2, OnBrand = Th.OnBrand,
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

    /// <summary>Switches theme and/or accent (null keeps the current one) and optionally saves to disk.</summary>
    public static void Apply(string? theme = null, string? accent = null, bool save = true)
    {
        var s = AppSettings.Current;
        string prevFont = s.UiFont;
        if (theme != null)
        {
            var prev = s.Theme;
            s.Theme = theme;
            var target = ByName(theme);
            if (theme == "Новогодняя" && prev != "Новогодняя" && s.SeasonalEffect == "none")
                s.SeasonalEffect = "newyear";
            if (s.ThemeAutoStyle)
            {
                s.FrostedGlass = target.DefaultBackdrop ?? "matte";
                s.BgPattern = target.DefaultPattern ?? "none";
                s.BgPatternTint = target.DefaultTint ?? "theme";
                s.UiNeonGlow = target.DefaultNeonGlow;
                s.UiCornerStyle = target.DefaultCorners ?? "normal";
                s.UiCardStyle = target.DefaultCardStyle ?? "clean";
                s.UiFont = target.DefaultFont ?? "segoe";
                if (target.DefaultEffect != null) s.SeasonalEffect = target.DefaultEffect;
            }
        }
        if (accent != null) s.Accent = accent;
        else if (theme == AdaptiveVideoName) s.Accent = "";
        if (save) s.Save();
        Current = ByName(s.Theme);
        if (IsAdaptiveVideo) { _adaptiveVid = null; SyncAdaptiveFromOpenVideo(); }
        _from = Snapshot();
        _to = Derive(IsAdaptiveVideo && _adaptiveLiveTheme != null ? _adaptiveLiveTheme : Current, IsAdaptiveVideo ? null : AccentOverride);
        _startedAt = UI.Now;
        _moving = save;
        if (!save) Set(_to);
        if (s.UiFont != prevFont) Gfx.ClearTextCache();
        Changed?.Invoke();
        App.Invalidate();
    }

    private static readonly Random _mutRng = new();
    private static readonly string[] _mutBackdrops = ["off", "soft", "matte", "mesh", "kawaii", "neon", "noir", "orbital"];
    private static readonly string[] _mutPatterns = ["none", "paws", "lines", "grid", "dots", "waves", "hex", "hearts", "stars", "circuit", "sakura", "noir_web", "scanlines"];
    private static readonly string[] _mutCards = ["clean", "framed", "dashed", "brutal", "neon"];
    private static readonly string[] _mutCorners = ["sharp", "normal", "round"];
    private static readonly string[] _mutFonts = ["segoe", "serif", "tech", "mono", "soft"];
    private static readonly string[] _mutTints = ["theme", "brand", "dual"];

    /// <summary>1-click cohesive theme mutator: remixes accent, backdrop, pattern, card frame, corners &amp; glow cleanly.</summary>
    public static void MutateCurrent()
    {
        var s = AppSettings.Current;
        string prevFont = s.UiFont;
        float deg = 35f + (float)_mutRng.NextDouble() * 290f;
        uint nextBrand = ShiftHue(AccentOverride ?? Current.Brand, deg);
        s.Accent = Hex(nextBrand);
        s.FrostedGlass = _mutBackdrops[_mutRng.Next(_mutBackdrops.Length)];
        s.BgPattern = _mutPatterns[_mutRng.Next(_mutPatterns.Length)];
        s.BgPatternTint = _mutTints[_mutRng.Next(_mutTints.Length)];
        s.UiCardStyle = _mutCards[_mutRng.Next(_mutCards.Length)];
        s.UiCornerStyle = _mutCorners[_mutRng.Next(_mutCorners.Length)];
        s.UiFont = _mutFonts[_mutRng.Next(_mutFonts.Length)];
        s.UiNeonGlow = s.FrostedGlass == "neon" || s.UiCardStyle == "neon" || _mutRng.Next(3) == 0;
        s.Save();

        _from = Snapshot();
        _to = Derive(Current, AccentOverride);
        _startedAt = UI.Now;
        _moving = true;
        if (s.UiFont != prevFont) Gfx.ClearTextCache();
        Changed?.Invoke();
        App.Invalidate();
    }

    public static event Action? Changed;

    public static void Tick()
    {
        SyncAdaptiveFromOpenVideo();
        if (!_moving) return;
        var t = Math.Clamp((UI.Now - _startedAt) / 360f, 0, 1);
        var e = t * t * (3 - 2 * t);
        Set(Blend(_from, _to, e));
        if (t >= 1) { _moving = false; Set(_to); Changed?.Invoke(); }
        UI.Animate();
    }

    public static (uint Bg, uint Panel, uint Card, uint Text, uint Sub, uint Brand, uint OnBrand, uint Line) Preview(Theme t, bool useAccentOverride = true)
    {
        var eff = t.Name == AdaptiveVideoName && _adaptiveLiveTheme != null ? _adaptiveLiveTheme : t;
        var p = Derive(eff, t.Name == AdaptiveVideoName ? null : (useAccentOverride ? AccentOverride : null));
        return (p.Bg, p.Panel, p.Card, p.Text, p.Sub, p.Brand, p.OnBrand, p.Line);
    }

    public static string Hex(uint c) => "#" + (c & 0xFFFFFF).ToString("X6");
}
