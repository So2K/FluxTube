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
    public string Hl { get; set; } = "ru";
    /// <summary>Empty = keep the region YouTube detects (matches the IP, so it looks natural behind a VPN).</summary>
    public string Gl { get; set; } = "";
    public bool Impersonate { get; set; } = true;
    public bool MarkWatched { get; set; } = true;
    public bool HideShorts { get; set; } = true;
    public bool Autoplay { get; set; } = false;
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

    public string PlayerFormat => FormatFor(PlayerQuality);
    public string DownloadFormat => FormatFor(DownloadQuality);

    public static string FormatFor(string q) => q switch
    {
        "audio" => "ba/b",
        "best" => "bv*+ba/b",
        _ => $"bv*[height<=?{q}]+ba/b[height<=?{q}]/b",
    };

    public static readonly string[] Qualities = ["best", "2160", "1440", "1080", "720", "480", "360", "audio"];

    public void Save()
    {
        try { File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings)); }
        catch (Exception e) { Paths.Log("settings save: " + e.Message); }
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