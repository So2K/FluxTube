using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FluxTube.Player;
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
    public static bool IsLiveAdaptiveEnabled => IsAdaptiveVideo || AppSettings.Current.VideoThemeAdapt == "all";
    public static bool IsLiveMorphing => _liveMorphing;
    public static bool IsCustom(string name) => _customFiles.ContainsKey(name);

    private static string? _adaptiveSourceKey;
    private static uint _adaptiveDom1, _adaptiveDom2;
    private static bool _adaptiveNeutral;
    private static int _adaptiveLumaBucket = -1;
    private static string? _adaptiveBaseName;
    private static Theme? _adaptiveLiveTheme;
    private static long _lastFrameReqAt;
    private static bool _liveActive, _hasLiveTarget, _liveMorphing;
    private static Pal _liveTarget;
    private static PalF _liveCurF;
    private static bool _liveCurFValid;
    private static long _lastChromeAt;
    private static uint _lastChromeBg, _lastChromeBrand;
    // Live 5x3 (15-zone) pinpoint spatial lightfield eased smoothly for app-wide spatial backdrop projection
    private static readonly float[] _spCurR = new float[15];
    private static readonly float[] _spCurG = new float[15];
    private static readonly float[] _spCurB = new float[15];
    private static readonly float[] _spTgtR = new float[15];
    private static readonly float[] _spTgtG = new float[15];
    private static readonly float[] _spTgtB = new float[15];
    private static float _spStrength, _spTargetStrength;
    private static uint _spHash;

    public static uint SpatialFieldHash => _spHash;
    public static float SpatialFieldStrength => _spStrength;

    /// <summary>Bilinearly samples the smoothly-eased 5x3 live video spatial color field at normalized window coordinates (u, v).</summary>
    public static bool TrySampleSpatialField(float u, float v, out float r, out float g, out float b, out float strength)
    {
        strength = _spStrength;
        if (strength < 0.015f) { r = g = b = 0f; return false; }
        float fx = Math.Clamp(u, 0f, 1f) * (SpatialGrid15.Cols - 1);
        float fy = Math.Clamp(v, 0f, 1f) * (SpatialGrid15.Rows - 1);
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(SpatialGrid15.Cols - 1, x0 + 1), y1 = Math.Min(SpatialGrid15.Rows - 1, y0 + 1);
        float tx = fx - x0, ty = fy - y0;
        int i00 = y0 * 5 + x0, i10 = y0 * 5 + x1, i01 = y1 * 5 + x0, i11 = y1 * 5 + x1;
        float r0 = _spCurR[i00] + (_spCurR[i10] - _spCurR[i00]) * tx;
        float g0 = _spCurG[i00] + (_spCurG[i10] - _spCurG[i00]) * tx;
        float b0 = _spCurB[i00] + (_spCurB[i10] - _spCurB[i00]) * tx;
        float r1 = _spCurR[i01] + (_spCurR[i11] - _spCurR[i01]) * tx;
        float g1 = _spCurG[i01] + (_spCurG[i11] - _spCurG[i01]) * tx;
        float b1 = _spCurB[i01] + (_spCurB[i11] - _spCurB[i01]) * tx;
        r = r0 + (r1 - r0) * ty;
        g = g0 + (g1 - g0) * ty;
        b = b0 + (b1 - b0) * ty;
        return true;
    }

    private static void SetSpatialTargetGrid(in SpatialGrid15 grid, float strength)
    {
        _spTargetStrength = strength;
        for (int i = 0; i < 15; i++)
        {
            uint c = grid[i];
            float r = ((c >> 16) & 255) * (1f / 255f);
            float g = ((c >> 8) & 255) * (1f / 255f);
            float b = (c & 255) * (1f / 255f);
            // Boost chroma slightly so localized colors stay vivid across the window backdrop
            float avg = (r + g + b) / 3f;
            _spTgtR[i] = Math.Clamp(avg + (r - avg) * 1.45f, 0f, 1f);
            _spTgtG[i] = Math.Clamp(avg + (g - avg) * 1.45f, 0f, 1f);
            _spTgtB[i] = Math.Clamp(avg + (b - avg) * 1.45f, 0f, 1f);
        }
    }

    private static void StepSpatialField(float dt)
    {
        float maxD = MathF.Abs(_spTargetStrength - _spStrength);
        float sRate = _spTargetStrength > _spStrength ? 3.5f : 2.6f;
        _spStrength += (_spTargetStrength - _spStrength) * (1f - MathF.Exp(-dt * sRate));
        if (_spStrength < 0.004f && _spTargetStrength == 0f) _spStrength = 0f;

        if (_spStrength > 0.004f || _spTargetStrength > 0.004f)
        {
            uint h = (uint)(_spStrength * 31f);
            for (int i = 0; i < 15; i++)
            {
                float d = MathF.Abs(_spTgtR[i] - _spCurR[i]) + MathF.Abs(_spTgtG[i] - _spCurG[i]) + MathF.Abs(_spTgtB[i] - _spCurB[i]);
                if (d > maxD) maxD = d;
                float rate = 3.2f + MathF.Min(1f, d * 2.2f) * 6.8f;
                float k = 1f - MathF.Exp(-dt * rate);
                _spCurR[i] += (_spTgtR[i] - _spCurR[i]) * k;
                _spCurG[i] += (_spTgtG[i] - _spCurG[i]) * k;
                _spCurB[i] += (_spTgtB[i] - _spCurB[i]) * k;
                // Quantized 5-bit spatial hash detects localized light motion across the frame
                uint q = ((uint)(_spCurR[i] * 31f) << 10) | ((uint)(_spCurG[i] * 31f) << 5) | (uint)(_spCurB[i] * 31f);
                h = unchecked(h * 16777619u ^ q);
            }
            _spHash = h;
            if (maxD > 0.012f)
            {
                _liveMorphing = true;
                UI.Animate();
            }
        }
        else
        {
            _spHash = 0;
        }
    }

    public static bool WantsLiveVideoFrames()
        => IsLiveAdaptiveEnabled
        && AppSettings.Current.VideoThemeAdapt != "thumb"
        && !Shell.IsAudioOnlyActive
        && AppSettings.Current.PlayerQuality != "audio"
        && MpvPlayer.Instance.HasActiveVideo;

    /// <summary>Pumps async live-frame color sampling from libmpv when video is actively playing (or once on pause/seek).</summary>
    public static void PumpLiveVideoSample()
    {
        if (App.Test || !WantsLiveVideoFrames()) return;
        if (App.Hwnd != 0 && W.IsIconic(App.Hwnd)) return;
        if (Gfx.Occluded) return;

        var p = MpvPlayer.Instance;
        string? openVid = Shell.CurrentWatchVideoId();
        if (!string.IsNullOrEmpty(openVid) && !string.IsNullOrEmpty(p.CurrentVideoId) && openVid != p.CurrentVideoId)
            return;

        long now = Environment.TickCount64;
        if (p.Paused)
        {
            if (p.HasPendingFrameProbe && now - _lastFrameReqAt >= 120)
            {
                _lastFrameReqAt = now;
                p.RequestFrameColorSample();
            }
            return;
        }
        if (p.Buffering) return;
        long interval = Shell.IsFullscreen ? 500 : 160;
        if (now - _lastFrameReqAt >= interval)
        {
            _lastFrameReqAt = now;
            p.RequestFrameColorSample();
        }
    }

    private static uint BoostBrand(uint c)
    {
        float r = ((c >> 16) & 255) / 255f, g = ((c >> 8) & 255) / 255f, b = (c & 255) / 255f;
        float avg = (r + g + b) / 3f;
        r = Math.Clamp(avg + (r - avg) * 1.42f, 0f, 1f);
        g = Math.Clamp(avg + (g - avg) * 1.42f, 0f, 1f);
        b = Math.Clamp(avg + (b - avg) * 1.42f, 0f, 1f);
        float max = MathF.Max(r, MathF.Max(g, b));
        if (max < 0.60f && max > 0.02f) { float k = 0.70f / max; r = MathF.Min(1f, r * k); g = MathF.Min(1f, g * k); b = MathF.Min(1f, b * k); }
        return 0xFF000000u | ((uint)(r * 255f + 0.5f) << 16) | ((uint)(g * 255f + 0.5f) << 8) | (uint)(b * 255f + 0.5f);
    }

    private static uint DeepenForLight(uint c)
    {
        float r = ((c >> 16) & 255) / 255f, g = ((c >> 8) & 255) / 255f, b = (c & 255) / 255f;
        float avg = (r + g + b) / 3f;
        r = Math.Clamp(avg + (r - avg) * 1.38f, 0f, 1f);
        g = Math.Clamp(avg + (g - avg) * 1.38f, 0f, 1f);
        b = Math.Clamp(avg + (b - avg) * 1.38f, 0f, 1f);
        float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        if (lum > 0.50f)
        {
            float k = 0.50f / lum;
            r *= k; g *= k; b *= k;
        }
        return 0xFF000000u | ((uint)(r * 255f + 0.5f) << 16) | ((uint)(g * 255f + 0.5f) << 8) | (uint)(b * 255f + 0.5f);
    }

    /// <summary>
    /// Builds a harmonious palette adapted from live video frame (or cover art) colors while preserving
    /// each theme's Light/Dark/AMOLED character, contrast, and legibility.
    /// </summary>
    private static Theme BuildAdaptedTheme(Theme baseTheme, uint dom1, uint dom2, bool isNeutral, float sceneLuma)
    {
        float lumNorm = Math.Clamp(sceneLuma, 0.05f, 0.90f);
        bool isFullAdaptive = baseTheme.Name == AdaptiveVideoName;

        uint nBrand = dom1 != 0 ? dom1 : 0xFFD2D6DDu;
        uint nBrand2 = dom2 != 0 ? dom2 : 0xFF9096A2u;
        uint brand = isNeutral ? nBrand : BoostBrand(dom1);
        uint brand2 = isNeutral ? nBrand2 : (dom2 != 0 && dom2 != dom1 ? BoostBrand(dom2) : ShiftHue(brand, 42f));

        // 1. Flagship "Adaptive (Video)" theme — full atmospheric real-time palette (deepens to obsidian on dark covers!)
        if (isFullAdaptive)
        {
            float darkCover = Math.Clamp((0.28f - sceneLuma) / 0.24f, 0f, 1f);
            if (isNeutral)
            {
                float lift = lumNorm * 0.045f * (1f - darkCover * 0.8f);
                uint nBg = Gfx.Mix(Gfx.Mix(0xFF0F1013, 0xFF040406, darkCover), nBrand, 0.025f + lift * 0.6f);
                uint nPanel = Gfx.Mix(Gfx.Mix(0xFF17181D, 0xFF08090C, darkCover), nBrand, 0.04f + lift * 0.7f);
                uint nCard = Gfx.Mix(Gfx.Mix(0xFF21232A, 0xFF101116, darkCover), nBrand, 0.05f + lift * 0.8f);
                uint nText = 0xFFF3F4F6;
                uint nSub = 0xFFA4A8B2;
                uint nDim = 0xFF6B707B;
                uint nOnBrand = Luma(nBrand) > 0.56f ? 0xFF101114 : 0xFFFFFFFF;
                return baseTheme with { Bg = nBg, Panel = nPanel, Card = nCard, Text = nText, Sub = nSub, Dim = nDim, Brand = nBrand, OnBrand = nOnBrand, Brand2 = nBrand2 };
            }

            float washMul = 1f - darkCover * 0.68f;
            float kBg = (0.11f + lumNorm * 0.06f) * washMul;
            float kPanel = (0.16f + lumNorm * 0.07f) * washMul;
            float kCard = (0.20f + lumNorm * 0.08f) * washMul;
            uint cardMix = Gfx.Mix(brand, brand2, 0.26f);
            uint bg = Gfx.Mix(Gfx.Mix(0xFF08080C, 0xFF030305, darkCover), brand, kBg);
            uint panel = Gfx.Mix(Gfx.Mix(0xFF101118, 0xFF07070B, darkCover), brand, kPanel);
            uint card = Gfx.Mix(Gfx.Mix(0xFF181924, 0xFF0E0F15, darkCover), cardMix, kCard);
            uint text = Gfx.Mix(0xFFF5F5FA, brand, 0.05f);
            uint sub = Gfx.Mix(0xFFA3A5B6, brand, 0.20f * (1f - darkCover * 0.35f));
            uint dim = Gfx.Mix(0xFF65687A, brand, 0.18f * (1f - darkCover * 0.35f));
            uint onBrand = Luma(brand) > 0.59f ? 0xFF0D0E12 : 0xFFFFFFFF;
            return baseTheme with { Bg = bg, Panel = panel, Card = card, Text = text, Sub = sub, Dim = dim, Brand = brand, OnBrand = onBrand, Brand2 = brand2 };
        }

        // 2. Light themes — keep airy paper clarity, harmonize vivid deepened accent + soft pastel surface wash
        if (baseTheme.Light)
        {
            uint lBrand = isNeutral ? 0xFF4B505C : DeepenForLight(brand);
            uint lBrand2 = isNeutral ? 0xFF7B818E : DeepenForLight(brand2);
            uint pastel1 = Gfx.Mix(0xFFFFFFFF, lBrand, 0.22f);
            uint pastel2 = Gfx.Mix(0xFFFFFFFF, lBrand2, 0.18f);
            uint bg = Gfx.Mix(baseTheme.Bg, pastel1, isNeutral ? 0.06f : 0.22f);
            uint panel = Gfx.Mix(baseTheme.Panel, pastel1, isNeutral ? 0.09f : 0.30f);
            uint card = Gfx.Mix(baseTheme.Card, pastel2, isNeutral ? 0.05f : 0.14f);
            uint sub = Gfx.Mix(baseTheme.Sub, lBrand, isNeutral ? 0.05f : 0.16f);
            uint dim = Gfx.Mix(baseTheme.Dim, lBrand, isNeutral ? 0.05f : 0.14f);
            uint onBrand = Luma(lBrand) > 0.60f ? 0xFF101116 : 0xFFFFFFFF;
            return baseTheme with { Bg = bg, Panel = panel, Card = card, Sub = sub, Dim = dim, Brand = lBrand, OnBrand = onBrand, Brand2 = lBrand2 };
        }

        // 3. AMOLED / Pitch-black themes — keep pure obsidian black Bg, illuminate Panel/Card & live neon accents
        if (Luma(baseTheme.Bg) < 0.018f)
        {
            uint bg = Gfx.Mix(baseTheme.Bg, brand, isNeutral ? 0.015f : 0.032f);
            uint panel = Gfx.Mix(baseTheme.Panel, brand, isNeutral ? 0.05f : 0.13f);
            uint card = Gfx.Mix(baseTheme.Card, Gfx.Mix(brand, brand2, 0.25f), isNeutral ? 0.07f : 0.19f);
            uint sub = Gfx.Mix(baseTheme.Sub, brand, isNeutral ? 0.06f : 0.18f);
            uint dim = Gfx.Mix(baseTheme.Dim, brand, isNeutral ? 0.05f : 0.15f);
            uint onBrand = Luma(brand) > 0.59f ? 0xFF0A0A0D : 0xFFFFFFFF;
            return baseTheme with { Bg = bg, Panel = panel, Card = card, Sub = sub, Dim = dim, Brand = brand, OnBrand = onBrand, Brand2 = brand2 };
        }

        // 4. Standard Dark themes — rich live video mood tinting while preserving theme contrast & structure
        float dDarkCover = Math.Clamp((0.28f - sceneLuma) / 0.24f, 0f, 1f);
        float dWash = 1f - dDarkCover * 0.62f;
        uint baseB2 = baseTheme.Brand2 != 0 ? baseTheme.Brand2 : ShiftHue(baseTheme.Brand, 42f);
        uint effBrand = isNeutral ? nBrand : Gfx.Mix(brand, baseTheme.Brand, 0.10f);
        uint effBrand2 = isNeutral ? nBrand2 : Gfx.Mix(brand2, baseB2, 0.10f);
        uint targetBg = isNeutral ? Gfx.Mix(Gfx.Mix(0xFF101114, 0xFF040406, dDarkCover), nBrand, 0.03f * dWash) : Gfx.Mix(Gfx.Mix(0xFF0A0A0F, 0xFF030305, dDarkCover), effBrand, (0.12f + lumNorm * 0.06f) * dWash);
        uint targetPanel = isNeutral ? Gfx.Mix(Gfx.Mix(0xFF17181E, 0xFF08090C, dDarkCover), nBrand, 0.05f * dWash) : Gfx.Mix(Gfx.Mix(0xFF12131B, 0xFF07070B, dDarkCover), effBrand, (0.17f + lumNorm * 0.06f) * dWash);
        uint targetCard = isNeutral ? Gfx.Mix(Gfx.Mix(0xFF20222A, 0xFF101116, dDarkCover), nBrand, 0.06f * dWash) : Gfx.Mix(Gfx.Mix(0xFF1B1C27, 0xFF0E0F15, dDarkCover), Gfx.Mix(effBrand, effBrand2, 0.25f), (0.21f + lumNorm * 0.07f) * dWash);
        uint dBg = Gfx.Mix(baseTheme.Bg, targetBg, 0.62f + dDarkCover * 0.26f);
        uint dPanel = Gfx.Mix(baseTheme.Panel, targetPanel, 0.65f + dDarkCover * 0.24f);
        uint dCard = Gfx.Mix(baseTheme.Card, targetCard, 0.65f + dDarkCover * 0.24f);
        uint dSub = Gfx.Mix(baseTheme.Sub, Gfx.Mix(0xFFA4A6B6, effBrand, isNeutral ? 0.05f : 0.22f), 0.48f);
        uint dDim = Gfx.Mix(baseTheme.Dim, Gfx.Mix(0xFF66697A, effBrand, isNeutral ? 0.05f : 0.18f), 0.45f);
        uint dOnBrand = Luma(effBrand) > 0.59f ? 0xFF0D0E12 : 0xFFFFFFFF;
        return baseTheme with { Bg = dBg, Panel = dPanel, Card = dCard, Sub = dSub, Dim = dDim, Brand = effBrand, OnBrand = dOnBrand, Brand2 = effBrand2 };
    }

    private static bool TryExtractFromStoryboardZones(
        (float R, float G, float B) tl, (float R, float G, float B) tr,
        (float R, float G, float B) bl, (float R, float G, float B) br,
        (float R, float G, float B) c,
        out uint dom1, out uint dom2, out bool isNeutral, out float avgLuma)
    {
        Span<(float R, float G, float B)> zs = [c, tl, tr, bl, br];
        float sumLum = 0;
        int best1 = -1, best2 = -1;
        float bestS1 = -1, bestS2 = -1;
        for (int i = 0; i < zs.Length; i++)
        {
            var z = zs[i];
            float mx = MathF.Max(z.R, MathF.Max(z.G, z.B));
            float mn = MathF.Min(z.R, MathF.Min(z.G, z.B));
            float sat = mx > 0.01f ? (mx - mn) / mx : 0f;
            sumLum += 0.2126f * z.R + 0.7152f * z.G + 0.0722f * z.B;
            float score = sat * sat * (0.3f + 0.7f * mx) * (i == 0 ? 1.25f : 1f);
            if (sat >= 0.14f && mx >= 0.12f && score > bestS1) { bestS1 = score; best1 = i; }
        }
        avgLuma = sumLum / zs.Length;
        if (best1 < 0)
        {
            isNeutral = true;
            float tone = avgLuma >= 0.52f ? 0.84f : 0.72f;
            byte v1 = (byte)(tone * 255f + 0.5f), v2 = (byte)((tone - 0.20f) * 255f + 0.5f);
            dom1 = 0xFF000000u | ((uint)v1 << 16) | ((uint)v1 << 8) | v1;
            dom2 = 0xFF000000u | ((uint)v2 << 16) | ((uint)v2 << 8) | v2;
            return true;
        }
        for (int i = 0; i < zs.Length; i++)
        {
            if (i == best1) continue;
            var z = zs[i];
            var z1 = zs[best1];
            float diff = MathF.Abs(z.R - z1.R) + MathF.Abs(z.G - z1.G) + MathF.Abs(z.B - z1.B);
            float mx = MathF.Max(z.R, MathF.Max(z.G, z.B));
            float mn = MathF.Min(z.R, MathF.Min(z.G, z.B));
            float sat = mx > 0.01f ? (mx - mn) / mx : 0f;
            float score = sat * (0.3f + 0.7f * mx) * (0.5f + diff);
            if (sat >= 0.12f && diff >= 0.18f && score > bestS2) { bestS2 = score; best2 = i; }
        }
        static uint Pack((float R, float G, float B) z)
            => 0xFF000000u | ((uint)(Math.Clamp(z.R, 0f, 1f) * 255f + 0.5f) << 16)
                           | ((uint)(Math.Clamp(z.G, 0f, 1f) * 255f + 0.5f) << 8)
                           |  (uint)(Math.Clamp(z.B, 0f, 1f) * 255f + 0.5f);
        isNeutral = false;
        dom1 = Pack(zs[best1]);
        dom2 = best2 >= 0 ? Pack(zs[best2]) : ShiftHue(dom1, 42f);
        return true;
    }

    private static void SetLiveTarget(in Pal target)
    {
        if (_moving)
        {
            _to = target;
            _liveTarget = target;
            _hasLiveTarget = true;
            return;
        }
        if (!_liveCurFValid)
        {
            _liveCurF = ToPalF(Snapshot());
            _liveCurFValid = true;
        }
        _liveTarget = target;
        _hasLiveTarget = true;
        _liveMorphing = true;
        UI.Animate();
    }

    public static void SyncAdaptiveFromOpenVideo()
    {
        if (!IsLiveAdaptiveEnabled)
        {
            if (_liveActive)
            {
                _liveActive = false;
                _adaptiveSourceKey = null;
                _adaptiveLiveTheme = null;
                SetLiveTarget(Derive(Current, AccentOverride));
            }
            return;
        }

        PumpLiveVideoSample();

        var p = MpvPlayer.Instance;
        string? openWatchVid = Shell.CurrentWatchVideoId();
        string? vid = Shell.AdaptiveVideoId();
        bool allowLiveFrames = AppSettings.Current.VideoThemeAdapt != "thumb";
        bool audioOnly = Shell.IsAudioOnlyActive || AppSettings.Current.PlayerQuality == "audio" || !p.HasActiveVideo;
        bool playerMatchesContext = p.HasActiveVideo
            && (string.IsNullOrEmpty(openWatchVid) || string.IsNullOrEmpty(p.CurrentVideoId) || openWatchVid == p.CurrentVideoId);

        uint dom1 = 0, dom2 = 0;
        bool isNeutral = false;
        float avgLuma = 0.34f;
        string? sourceKey = null;

        // 1. Video mode -> real-time current decoded video frame!
        if (allowLiveFrames && !audioOnly && playerMatchesContext)
        {
            var sample = p.LatestFrameSample;
            if (sample.Gen > 0 && sample.MediaKey == p.ActiveMediaKey && sample.Dom1 != 0)
            {
                dom1 = sample.Dom1;
                dom2 = sample.Dom2;
                isNeutral = sample.IsNeutral;
                avgLuma = sample.AvgLuma;
                sourceKey = "frame:" + (sample.MediaKey ?? "local");
                var sg = sample.Grid;
                SetSpatialTargetGrid(in sg, isNeutral ? 0.22f : 1.0f);
            }
            else if (!string.IsNullOrEmpty(vid)
                && Storyboard.TrySampleGrid15(vid, p.CurrentVideoId == vid ? p.TimePos : 0, out var sbGrid)
                && TryExtractFromStoryboardZones(sbGrid.Cell(0, 0), sbGrid.Cell(4, 0), sbGrid.Cell(0, 2), sbGrid.Cell(4, 2), sbGrid.Cell(2, 1), out dom1, out dom2, out isNeutral, out avgLuma))
            {
                sourceKey = "sb:" + vid;
                SetSpatialTargetGrid(in sbGrid, isNeutral ? 0.22f : 0.85f);
            }
            else if (!string.IsNullOrEmpty(vid) && MusicArt.TryGetColors(vid, out dom1, out dom2, out isNeutral, out avgLuma))
            {
                sourceKey = "thumb:" + vid;
                _spTargetStrength = 0f;
            }
        }
        // 2. Audio-only mode (or no active video stream) -> crisp thumbnail / cover art with real luminance!
        else if (!string.IsNullOrEmpty(vid))
        {
            _spTargetStrength = 0f;
            if (MusicArt.TryGetColors(vid, out dom1, out dom2, out isNeutral, out avgLuma))
                sourceKey = "thumb:" + vid;
        }
        else
        {
            _spTargetStrength = 0f;
        }

        if (sourceKey == null || dom1 == 0)
        {
            // When no video is open or playing, non-adaptive themes glide cleanly back to their original colors
            if (!IsAdaptiveVideo && _liveActive && !Shell.HasAnyOpenOrPlayingVideo())
            {
                _liveActive = false;
                _adaptiveSourceKey = null;
                _adaptiveLiveTheme = null;
                SetLiveTarget(Derive(Current, AccentOverride));
            }
            return;
        }

        int lumaBucket = (int)MathF.Round(avgLuma * 14f);
        if (_liveActive
            && _adaptiveBaseName == Current.Name
            && _adaptiveSourceKey == sourceKey
            && _adaptiveDom1 == dom1
            && _adaptiveDom2 == dom2
            && _adaptiveNeutral == isNeutral
            && _adaptiveLumaBucket == lumaBucket
            && _adaptiveLiveTheme != null)
            return;

        _liveActive = true;
        _adaptiveBaseName = Current.Name;
        _adaptiveSourceKey = sourceKey;
        _adaptiveDom1 = dom1;
        _adaptiveDom2 = dom2;
        _adaptiveNeutral = isNeutral;
        _adaptiveLumaBucket = lumaBucket;
        _adaptiveLiveTheme = BuildAdaptedTheme(Current, dom1, dom2, isNeutral, avgLuma);
        SetLiveTarget(Derive(_adaptiveLiveTheme, null));
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

    private struct ColF
    {
        public float A, R, G, B;
        public static ColF From(uint c) => new() { A = (c >> 24) & 255, R = (c >> 16) & 255, G = (c >> 8) & 255, B = c & 255 };
        public uint Pack() => ((uint)Math.Clamp((int)(A + 0.5f), 0, 255) << 24)
                            | ((uint)Math.Clamp((int)(R + 0.5f), 0, 255) << 16)
                            | ((uint)Math.Clamp((int)(G + 0.5f), 0, 255) << 8)
                            |  (uint)Math.Clamp((int)(B + 0.5f), 0, 255);
        public float StepToward(uint target, float k)
        {
            float ta = (target >> 24) & 255, tr = (target >> 16) & 255, tg = (target >> 8) & 255, tb = target & 255;
            A += (ta - A) * k; R += (tr - R) * k; G += (tg - G) * k; B += (tb - B) * k;
            return MathF.Max(MathF.Max(MathF.Abs(ta - A), MathF.Abs(tr - R)), MathF.Max(MathF.Abs(tg - G), MathF.Abs(tb - B)));
        }
    }

    private struct PalF
    {
        public ColF Bg, Panel, Card, Text, Sub, Dim, Brand, Brand2, OnBrand, Ink, Hover, Press, Line, Thumb, Field, Menu, Icon, Track, Good, Warn;
    }

    private static PalF ToPalF(in Pal p) => new()
    {
        Bg = ColF.From(p.Bg), Panel = ColF.From(p.Panel), Card = ColF.From(p.Card), Text = ColF.From(p.Text),
        Sub = ColF.From(p.Sub), Dim = ColF.From(p.Dim), Brand = ColF.From(p.Brand), Brand2 = ColF.From(p.Brand2), OnBrand = ColF.From(p.OnBrand),
        Ink = ColF.From(p.Ink), Hover = ColF.From(p.Hover), Press = ColF.From(p.Press), Line = ColF.From(p.Line),
        Thumb = ColF.From(p.Thumb), Field = ColF.From(p.Field), Menu = ColF.From(p.Menu), Icon = ColF.From(p.Icon),
        Track = ColF.From(p.Track), Good = ColF.From(p.Good), Warn = ColF.From(p.Warn),
    };

    private static Pal PackPalF(in PalF f, bool light) => new()
    {
        Bg = f.Bg.Pack(), Panel = f.Panel.Pack(), Card = f.Card.Pack(), Text = f.Text.Pack(),
        Sub = f.Sub.Pack(), Dim = f.Dim.Pack(), Brand = f.Brand.Pack(), Brand2 = f.Brand2.Pack(), OnBrand = f.OnBrand.Pack(),
        Ink = f.Ink.Pack(), Hover = f.Hover.Pack(), Press = f.Press.Pack(), Line = f.Line.Pack(),
        Thumb = f.Thumb.Pack(), Field = f.Field.Pack(), Menu = f.Menu.Pack(), Icon = f.Icon.Pack(),
        Track = f.Track.Pack(), Good = f.Good.Pack(), Warn = f.Warn.Pack(), Light = light,
    };

    private static float StepPalF(ref PalF f, in Pal dst, float k)
    {
        float m = 0f;
        m = MathF.Max(m, f.Bg.StepToward(dst.Bg, k));
        m = MathF.Max(m, f.Panel.StepToward(dst.Panel, k));
        m = MathF.Max(m, f.Card.StepToward(dst.Card, k));
        m = MathF.Max(m, f.Text.StepToward(dst.Text, k));
        m = MathF.Max(m, f.Sub.StepToward(dst.Sub, k));
        m = MathF.Max(m, f.Dim.StepToward(dst.Dim, k));
        m = MathF.Max(m, f.Brand.StepToward(dst.Brand, k));
        m = MathF.Max(m, f.Brand2.StepToward(dst.Brand2, k));
        m = MathF.Max(m, f.OnBrand.StepToward(dst.OnBrand, k));
        m = MathF.Max(m, f.Ink.StepToward(dst.Ink, k));
        m = MathF.Max(m, f.Hover.StepToward(dst.Hover, k));
        m = MathF.Max(m, f.Press.StepToward(dst.Press, k));
        m = MathF.Max(m, f.Line.StepToward(dst.Line, k));
        m = MathF.Max(m, f.Thumb.StepToward(dst.Thumb, k));
        m = MathF.Max(m, f.Field.StepToward(dst.Field, k));
        m = MathF.Max(m, f.Menu.StepToward(dst.Menu, k));
        m = MathF.Max(m, f.Icon.StepToward(dst.Icon, k));
        m = MathF.Max(m, f.Track.StepToward(dst.Track, k));
        m = MathF.Max(m, f.Good.StepToward(dst.Good, k));
        m = MathF.Max(m, f.Warn.StepToward(dst.Warn, k));
        return m;
    }

    private static int RgbDist(uint a, uint b)
        => Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255))
         + Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255))
         + Math.Abs((int)(a & 255) - (int)(b & 255));

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
        _adaptiveSourceKey = null;
        _adaptiveBaseName = null;
        _adaptiveLiveTheme = null;
        _liveActive = false;
        _hasLiveTarget = false;
        _liveMorphing = false;
        _liveCurFValid = false;
        if (IsLiveAdaptiveEnabled) SyncAdaptiveFromOpenVideo();
        _from = Snapshot();
        _to = _liveActive && _adaptiveLiveTheme != null
            ? Derive(_adaptiveLiveTheme, null)
            : Derive(Current, IsAdaptiveVideo ? null : AccentOverride);
        _startedAt = UI.Now;
        _moving = save;
        if (!save) Set(_to);
        if (s.UiFont != prevFont) Gfx.ClearTextCache();
        Changed?.Invoke();
        App.Invalidate();
    }

    /// <summary>Called when the user toggles the live video adaptation mode in Settings.</summary>
    public static void OnAdaptationModeChanged()
    {
        _adaptiveSourceKey = null;
        _adaptiveBaseName = null;
        _lastFrameReqAt = 0;
        if (WantsLiveVideoFrames()) MpvPlayer.Instance.MarkFrameDirty();
        SyncAdaptiveFromOpenVideo();
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
    /// <summary>Throttled lightweight hook fired during live video color morphing (updates DWM window frame + mpv scrubber accent).</summary>
    public static event Action? LiveChromeChanged;

    public static void Tick()
    {
        SyncAdaptiveFromOpenVideo();
        float dt = (float)Math.Clamp(UI.Dt, 0.001, 0.05);
        StepSpatialField(dt);
        if (_moving)
        {
            var t = Math.Clamp((UI.Now - _startedAt) / 560f, 0f, 1f);
            var e = t * t * t * (t * (t * 6f - 15f) + 10f); // quintic smootherstep
            Set(Blend(_from, _to, e));
            if (t >= 1)
            {
                _moving = false;
                Set(_to);
                _liveCurF = ToPalF(_to);
                _liveCurFValid = true;
                _lastChromeBg = Th.Bg;
                _lastChromeBrand = Th.Brand;
                _lastChromeAt = UI.Now;
                Gfx.InvalidateBackdrop();
                Changed?.Invoke();
                App.Invalidate();
            }
            UI.Animate();
            return;
        }

        if (!_hasLiveTarget)
        {
            if (_liveMorphing && _spStrength <= 0.01f) _liveMorphing = false;
            return;
        }

        // Ultra-silky C-infinity exponential color glide (cinematic, never jarring!)
        int deltaMag = RgbDist(Th.Brand, _liveTarget.Brand) + RgbDist(Th.Bg, _liveTarget.Bg);
        float adaptiveRate = 1.65f + Math.Clamp(deltaMag / 320f, 0f, 1f) * 1.75f;
        float k = 1f - MathF.Exp(-dt * adaptiveRate);
        float maxDelta = StepPalF(ref _liveCurF, _liveTarget, k);

        if (maxDelta < 0.25f)
        {
            Set(_liveTarget);
            _liveCurF = ToPalF(_liveTarget);
            _hasLiveTarget = false;
            _liveMorphing = false;
            Gfx.InvalidateBackdrop();
            if (RgbDist(Th.Bg, _lastChromeBg) >= 2 || RgbDist(Th.Brand, _lastChromeBrand) >= 2)
            {
                _lastChromeBg = Th.Bg;
                _lastChromeBrand = Th.Brand;
                _lastChromeAt = UI.Now;
                LiveChromeChanged?.Invoke();
            }
            return;
        }

        Set(PackPalF(_liveCurF, _liveTarget.Light));
        _liveMorphing = true;
        UI.Animate();

        if (UI.Now - _lastChromeAt >= 200 && (RgbDist(Th.Bg, _lastChromeBg) >= 6 || RgbDist(Th.Brand, _lastChromeBrand) >= 6))
        {
            _lastChromeBg = Th.Bg;
            _lastChromeBrand = Th.Brand;
            _lastChromeAt = UI.Now;
            LiveChromeChanged?.Invoke();
        }
    }

    public static (uint Bg, uint Panel, uint Card, uint Text, uint Sub, uint Brand, uint OnBrand, uint Line) Preview(Theme t, bool useAccentOverride = true)
    {
        var eff = t.Name == AdaptiveVideoName && _adaptiveLiveTheme != null && IsAdaptiveVideo ? _adaptiveLiveTheme : t;
        var p = Derive(eff, t.Name == AdaptiveVideoName ? null : (useAccentOverride ? AccentOverride : null));
        return (p.Bg, p.Panel, p.Card, p.Text, p.Sub, p.Brand, p.OnBrand, p.Line);
    }

    public static string Hex(uint c) => "#" + (c & 0xFFFFFF).ToString("X6");

    internal static (bool Ok, string Summary) SelfTestLiveAdaptation()
    {
        var adaptive = ByName(AdaptiveVideoName);
        var amoled = ByName("AMOLED");
        var light = ByName("YouTube светлая");
        var dark = ByName("FluxTube");

        var aBlue = BuildAdaptedTheme(adaptive, 0xFF1E88FF, 0xFF00E5FF, false, 0.45f);
        if ((aBlue.Brand & 0xFF) < 180 || Luma(aBlue.Bg) > 0.22f)
            return (false, $"Adaptive blue bad: Brand={Hex(aBlue.Brand)} Bg={Hex(aBlue.Bg)}");

        var aNeut = BuildAdaptedTheme(adaptive, 0xFFD4D4D4, 0xFF9A9A9A, true, 0.80f);
        int nr = (int)((aNeut.Brand >> 16) & 255), ng = (int)((aNeut.Brand >> 8) & 255), nb = (int)(aNeut.Brand & 255);
        if (Math.Max(nr, Math.Max(ng, nb)) - Math.Min(nr, Math.Min(ng, nb)) > 20)
            return (false, $"Adaptive neutral not neutral: Brand={Hex(aNeut.Brand)}");

        var amRed = BuildAdaptedTheme(amoled, 0xFFFF2244, 0xFFFF9900, false, 0.40f);
        if (Luma(amRed.Bg) > 0.03f || ((amRed.Brand >> 16) & 255) < 180)
            return (false, $"AMOLED adaptation broke black Bg or accent: Bg={Hex(amRed.Bg)} Brand={Hex(amRed.Brand)}");

        var ltGreen = BuildAdaptedTheme(light, 0xFF33FF77, 0xFF00DDCC, false, 0.70f);
        if (!ltGreen.Light || Luma(ltGreen.Bg) < 0.78f || Luma(ltGreen.Brand) > 0.55f)
            return (false, $"Light adaptation bad: Bg={Hex(ltGreen.Bg)} Brand={Hex(ltGreen.Brand)}");

        var dkOrange = BuildAdaptedTheme(dark, 0xFFFF8800, 0xFFFF3366, false, 0.50f);
        if (dkOrange.Light || Luma(dkOrange.Bg) > 0.25f || ((dkOrange.Brand >> 16) & 255) < 180)
            return (false, $"Dark adaptation bad: Bg={Hex(dkOrange.Bg)} Brand={Hex(dkOrange.Brand)}");

        // Verify high-refresh-rate PalF convergence (zero 8-bit quantization stall at 240Hz!)
        var from = Derive(dark, null);
        var to = Derive(dkOrange, null);
        var pf = ToPalF(from);
        float k240 = 1f - MathF.Exp(-(1f / 240f) * 4.4f);
        float maxD = 999f;
        for (int i = 0; i < 420; i++) maxD = StepPalF(ref pf, to, k240);
        if (maxD >= 0.45f) return (false, $"PalF 240Hz convergence stalled (maxDelta={maxD:0.00})");

        return (true, $"themes OK (Adaptive/AMOLED/Light/Dark + 240Hz PalF d={maxD:0.02})");
    }
}
