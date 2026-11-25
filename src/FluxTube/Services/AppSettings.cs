using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxTube.Services;

public sealed class AppSettings
{
    public static AppSettings Current { get; private set; } = Load();

    public string PlayerQuality { get; set; } = "1080";
    public string DownloadQuality { get; set; } = "1080";
    public string DownloadDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FluxTube");
    public string Proxy { get; set; } = "";
    private string _hl = "ru";
    public string Hl
    {
        get => _hl;
        set
        {
            var clean = SanitizeCode(value, 10);
            _hl = clean.Length >= 2 ? clean.ToLowerInvariant() : "ru";
        }
    }
    private string _gl = "";
    /// <summary>Empty = keep the region YouTube detects (matches the IP, so it looks natural behind a VPN).</summary>
    public string Gl
    {
        get => _gl;
        set => _gl = SanitizeCode(value, 4).ToUpperInvariant();
    }

    private static string SanitizeCode(string? s, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(maxLen, s.Length));
        foreach (var ch in s)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')
            {
                sb.Append(ch);
                if (sb.Length >= maxLen) break;
            }
        }
        return sb.ToString();
    }
    public bool Impersonate { get; set; } = true;
    public bool MarkWatched { get; set; } = true;
    public bool HideShorts { get; set; } = true;
    public bool Autoplay { get; set; } = false;
    /// <summary>True: switching to a background-opened or paused video tab immediately starts playing it. False: stays paused until Play is pressed.</summary>
    public bool AutoPlayOpenedTabs { get; set; } = false;
    public bool SponsorBlock { get; set; } = true;
    public string[] SponsorCategories { get; set; } = ["sponsor", "selfpromo", "interaction"];
    public bool SponsorBlockOnDownload { get; set; } = false;
    public bool ReturnDislikes { get; set; } = true;
    public int Volume { get; set; } = 100;
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 950;
    public DateTime LastYtDlpUpdate { get; set; }
    public DateTime LastPyUpdate { get; set; }
    /// <summary>Disk cache of played streams, megabytes. 0 turns it off.</summary>
    public int CacheLimitMb { get; set; } = 1024;
    /// <summary>Start a new video at once in 360p while the full-quality stream is being prepared.</summary>
    public bool QuickStart { get; set; } = true;
    /// <summary>Right-click on the picture pauses (off: it opens the video menu).</summary>
    public bool RightClickPause { get; set; } = true;
    /// <summary>Colour theme by name (see Themes in the native shell) and an optional accent override, "#RRGGBB" or empty.</summary>
    public string Theme { get; set; } = "FluxTube";
    /// <summary>Search keeps only results whose title or channel really contains the words asked for.</summary>
    public bool SearchStrict { get; set; } = true;
    /// <summary>Channels the user never wants to see in search and recommendations (channel ids).</summary>
    public string[] BlockedChannels { get; set; } = [];
    public string Accent { get; set; } = "";
    /// <summary>Left navigation sidebar collapsed to maximize content area.</summary>
    public bool SidebarCollapsed { get; set; } = false;
    /// <summary>True: scrolling the watch page scrolls the video out of view (like YouTube web). False: video stays pinned and comments scroll separately.</summary>
    public bool ScrollWholeWatchPage { get; set; } = false;
    /// <summary>UI zoom multiplier (0.75 to 1.50, default 1.0 = 100%).</summary>
    public float UiScale { get; set; } = 1.0f;
    /// <summary>Hides the right-side info/comments panel on the Shorts page for distraction-free viewing.</summary>
    public bool ShortsPanelHidden { get; set; } = false;
    /// <summary>Soft GPU radial ambient glow around video players matching the current scene colors.</summary>
    public bool AmbientMode { get; set; } = true;
    /// <summary>Preferred audio dubbing language code (e.g. "ru", "en", or "" for original). Never enables subtitles.</summary>
    public string AudioLanguage { get; set; } = "";
    /// <summary>When Shorts side panel is hidden: true = show channel/title as hover overlay on the video; false = show clean text block beside the video.</summary>
    public bool ShortsInfoOverlay { get; set; } = false;
    /// <summary>Theater mode on WatchPage (hides the right-side recommendations column), persisted across videos and restarts.</summary>
    public bool TheaterMode { get; set; } = false;
    /// <summary>Seasonal overlay effect that can be applied on top of ANY theme: "none", "newyear" (snowflakes), or "halloween" (pumpkins).</summary>
    public string SeasonalEffect { get; set; } = "none";
    /// <summary>Subtle PS1/PS2-style UI sound effects (Silent Hill / Metal Gear Solid inspired) on clicks, toggles, and sliders.</summary>
    public bool UiSounds { get; set; } = false;
    /// <summary>UI sound master volume percentage (10..300%, default 100%). Applies to both built-in and custom SFX.</summary>
    public int UiSoundsVolume { get; set; } = 100;
    /// <summary>Spatial 8D stereo hall reverb applied to all UI sounds (built-in & custom).</summary>
    public bool UiSoundsReverb8D { get; set; } = false;
    /// <summary>8D spatial audio & reverb filter on video/music playback in mpv.</summary>
    public bool PlayerAudio8D { get; set; } = false;
    /// <summary>Master enable for Reaper-style DSP Studio (10-band EQ, reverb, Nightcore/Slowed, exciter, limiter).</summary>
    public bool AudioFxEnabled { get; set; } = false;
    /// <summary>Active audio DSP preset id ("Flat", "Juicy", "Nightcore", "NightcoreUltra", "SlowedReverb", "BassBoost", "Phonk", "Space8D", "Lofi", "Vocal", "Custom").</summary>
    public string AudioPreset { get; set; } = "Flat";
    /// <summary>10-band graphic EQ gains in dB (-12..+12): 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz.</summary>
    public float[] EqBands { get; set; } = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    /// <summary>Studio multi-tap reverb wetness (0..100).</summary>
    public float FxReverb { get; set; } = 0f;
    /// <summary>Reverb space profile ("room", "plate", "hall", "cathedral").</summary>
    public string FxReverbRoom { get; set; } = "hall";
    /// <summary>Clean sub-bass punch boost (0..100).</summary>
    public float FxBass { get; set; } = 0f;
    /// <summary>Harmonic brilliance exciter + studio glue compression (0..100).</summary>
    public float FxExciter { get; set; } = 0f;
    /// <summary>Mid/Side 3D stereo soundstage width (0..100).</summary>
    public float FxStereo { get; set; } = 0f;
    /// <summary>Playback speed used by DSP presets such as Nightcore / Slowed+Reverb (0.70..1.45).</summary>
    public float FxSpeed { get; set; } = 1.0f;
    /// <summary>True = vinyl/tape pitch shift locked to speed (authentic Nightcore & Slowed+Reverb).</summary>
    public bool FxTapePitch { get; set; } = false;
    /// <summary>Independent formant-preserving pitch shift in semitones (-6..+6) when FxTapePitch is off.</summary>
    public float FxPitchSemitones { get; set; } = 0f;
    /// <summary>Transparent master bus limiter to keep heavy bass & exciter punchy with zero clipping.</summary>
    public bool FxLimiter { get; set; } = true;
    /// <summary>Audio-only music stage on WatchPage: suspends mpv video decoding (0% GPU) and shows live audio waves.</summary>
    public bool AudioOnlyMode { get; set; } = false;
    /// <summary>Automatically suspends mpv video decoding (vid=no) whenever FluxTube is minimized or viewing another tab.</summary>
    public bool AutoSuspendVideoWhenHidden { get; set; } = false;
    /// <summary>Live audio visualizer style ("bars_wave", "ribbon", "bars", "off").</summary>
    public string VisualizerStyle { get; set; } = "bars_wave";
    /// <summary>Backdrop &amp; frosted glass style ("off", "soft", "matte", "mesh", "kawaii", "neon").</summary>
    public string FrostedGlass { get; set; } = "matte";
    /// <summary>Hardware Direct2D GPU stencil glass &amp; stylization shader ("acrylic", "liquid", "toon", "poster", "sketch", "off").</summary>
    public string UiGlassShader { get; set; } = "acrylic";
    /// <summary>Seamless GPU background pattern overlay ("none", "paws", "lines", "grid", "dots", "waves", "hex", "hearts", "stars", "circuit", "sakura").</summary>
    public string BgPattern { get; set; } = "none";
    /// <summary>Background pattern opacity (0.04..0.55).</summary>
    public float BgPatternOpacity { get; set; } = 0.14f;
    /// <summary>Background pattern tile scale multiplier (0.7 = S, 1.0 = M, 1.45 = L).</summary>
    public float BgPatternScale { get; set; } = 1.0f;
    /// <summary>Background pattern coloring ("theme", "brand", "dual").</summary>
    public string BgPatternTint { get; set; } = "theme";
    /// <summary>Optional path to a local custom wallpaper image (PNG/JPG/WEBP/BMP), pre-scaled &amp; cached once on GPU.</summary>
    public string BgImagePath { get; set; } = "";
    /// <summary>Custom wallpaper visibility / blend opacity (0.10..0.95).</summary>
    public float BgImageOpacity { get; set; } = 0.38f;
    /// <summary>Pre-baked one-shot GPU Gaussian blur radius for custom wallpaper (0 = crisp, 6 = soft, 18 = dreamy).</summary>
    public float BgImageBlur { get; set; } = 0f;
    /// <summary>Ultra-cheap neon rim glow on active chrome (search bar, tabs, accent pills, sidebar marker, modals).</summary>
    public bool UiNeonGlow { get; set; } = false;
    /// <summary>UI corner geometry ("sharp" = 0px, "normal" = default, "round" = bubbly soft kawaii).</summary>
    public string UiCornerStyle { get; set; } = "normal";
    /// <summary>Card &amp; panel frame style ("clean", "framed", "dashed", "brutal", "neon").</summary>
    public string UiCardStyle { get; set; } = "clean";
    /// <summary>UI typography font preset ("segoe", "serif", "tech", "mono", "soft", "comic").</summary>
    public string UiFont { get; set; } = "segoe";
    /// <summary>When switching to a signature theme (Ultra Kawaii, Ultra Neon, RE Requiem, etc.), automatically apply its matching backdrop, pattern, corners, card style &amp; font.</summary>
    public bool ThemeAutoStyle { get; set; } = true;
    /// <summary>Application UI language ("ru" or "en"). Separate from YouTube content language (Hl).</summary>
    public string UiLanguage { get; set; } = "ru";
    /// <summary>Hardware-accelerated GPU DirectionalBlur motion blur on UI scrolling, transitions, and Shorts swipes.</summary>
    public bool UiMotionBlur { get; set; } = true;
    /// <summary>UI motion blur shutter strength ("soft", "medium", "cinema").</summary>
    public string UiMotionBlurStrength { get; set; } = "medium";
    /// <summary>YouTube Shorts ad &amp; sponsored video auto-blocker ("off", "standard", "max").</summary>
    public string ShortsAdBlock { get; set; } = "max";
    /// <summary>True: mouse wheel over video adjusts volume. False: wheel scrolls the page (or switches Shorts).</summary>
    public bool WheelVolumeOnVideo { get; set; } = true;
    /// <summary>True: Shift + mouse wheel over video steps frame-by-frame with exact millisecond &amp; frame OSD readout.</summary>
    public bool ShiftWheelFrameStep { get; set; } = true;
    /// <summary>SoundCloud-style interactive audio waveform seekbar with timed YouTube comment avatars below the player &amp; in Audio-Only mode.</summary>
    public bool SoundCloudWave { get; set; } = true;
    /// <summary>Ultra-cheap GPU camera shake + bass thump on the UI during Audio-Only Music Mode (single Matrix3x2 transform, ~0.001ms).</summary>
    public bool MusicBassShake { get; set; } = true;
    public bool MusicImpactEffects { get; set; } = true;
    public bool VideoBeatShake { get; set; } = true;
    /// <summary>User-saved custom DSP Studio presets.</summary>
    public List<SavedAudioPreset> CustomAudioPresets { get; set; } = [];
    /// <summary>Optional custom sound display names per UI sound slot (normalized WAV copies stored in .fluxtube/sfx).</summary>
    public Dictionary<string, string> CustomSfx { get; set; } = new();

    public string PlayerFormat => FormatFor(PlayerQuality);
    public string DownloadFormat => FormatFor(DownloadQuality);

    public static string FormatFor(string q) => q switch
    {
        "audio" => "ba/b",
        "best" => "bv*+ba/b",
        _ => $"bv*[height<=?{q}]+ba/b[height<=?{q}]/b",
    };

    public static readonly string[] Qualities = ["best", "2160", "1440", "1080", "720", "480", "360", "audio"];

    private static readonly object _saveLock = new();

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var tmp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
                File.Move(tmp, Paths.SettingsFile, overwrite: true);
            }
            catch (Exception e) { Paths.Log("settings save: " + e.Message); }
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(Paths.SettingsFile), SettingsJson.Default.AppSettings) ?? new();
        }
        catch (Exception e) { Paths.Log("settings load: " + e.Message); }
        return new();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;

public sealed class SavedAudioPreset
{
    public string Name { get; set; } = "";
    public float[] Eq { get; set; } = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    public float Reverb { get; set; }
    public string Room { get; set; } = "hall";
    public float Bass { get; set; }
    public float Exciter { get; set; }
    public float Stereo { get; set; }
    public float Speed { get; set; } = 1.0f;
    public bool TapePitch { get; set; }
    public float PitchSemi { get; set; }
    public bool Space8D { get; set; }
    public bool Limiter { get; set; } = true;
}