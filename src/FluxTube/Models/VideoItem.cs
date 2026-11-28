namespace FluxTube.Models;

public sealed class VideoItem
{
    public string VideoId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public string? ChannelId { get; init; }
    public string Duration { get; init; } = "";
    public string Meta { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public bool IsLive { get; init; }
    public bool IsShort { get; init; }
    /// <summary>Marked as an injected YouTube advertisement in InnerTube responses.</summary>
    public bool IsAd { get; init; }
    /// <summary>Marked by YouTube with a paidContentOverlay ("Includes paid promotion").</summary>
    public bool HasPaidPromotion { get; init; }
    /// <summary>Optional Short description text for ad/promo inspection.</summary>
    public string Description { get; init; } = "";
    /// <summary>Set for a playlist card: VideoId is then its first video (for the thumbnail), Duration the video count.</summary>
    public string? PlaylistId { get; init; }
    /// <summary>A channel card (search results): ChannelId, Title and AvatarUrl are set, VideoId is empty.</summary>
    public bool IsChannel { get; init; }

    /// <summary>Resume progress 0..1 from local history, shown as a red bar.</summary>
    public double Progress { get; set; }

    public string Thumb => _thumb ??= $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg";
    public string ThumbHq => _thumbHq ??= $"https://i.ytimg.com/vi/{VideoId}/hqdefault.jpg";
    public string Thumb720 => _thumb720 ??= $"https://i.ytimg.com/vi/{VideoId}/hq720.jpg";
    public string Url => $"https://www.youtube.com/watch?v={VideoId}";
    private string? _channelMeta;
    public string ChannelMeta => _channelMeta ??= string.IsNullOrEmpty(Meta) ? Channel : $"{Channel} · {Meta}";
    private string? _thumb, _thumbHq, _thumb720;
    public bool HasDuration => !string.IsNullOrEmpty(Duration) || IsLive;
    public string DurationBadge => IsLive ? "LIVE" : Duration;
}

public sealed class FeedPage
{
    public List<VideoItem> Items { get; } = [];
    public string? Continuation { get; set; }
    /// <summary>Home feed category filter chips (Все, Видеоигры, Музыка, ...).</summary>
    public List<(string Title, string Token, bool Selected)> Chips { get; } = [];
}

/// <summary>A channel page: the header and either the home shelves or one tab's grid.</summary>
public sealed class ChannelInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Handle { get; set; } = "";
    public string Subscribers { get; set; } = "";
    public string VideoCount { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Avatar { get; set; }
    public string? Banner { get; set; }
    public bool Verified { get; set; }
    public bool? Subscribed { get; set; }
    public List<(string Title, string Params)> Tabs { get; } = [];
    public List<Shelf> Shelves { get; } = [];
    /// <summary>The tab's own orders ("newest", "popular", "oldest"): each reloads the grid from its token.</summary>
    public List<(string Title, string Token, bool Selected)> Chips { get; } = [];
    public FeedPage Grid { get; set; } = new();
}

/// <summary>One day of the account's watch history: the Shorts watched that day and the videos.</summary>
public sealed class HistoryDay
{
    public string Title { get; set; } = "";
    public List<VideoItem> Shorts { get; } = [];
    public List<VideoItem> Videos { get; } = [];
}

public sealed class Shelf
{
    public string Title { get; set; } = "";
    public List<VideoItem> Items { get; } = [];
    /// <summary>"Play all" / title link: a playlist (VL…) or another tab.</summary>
    public string? BrowseId { get; set; }
    public string? Params { get; set; }
}

public sealed class Comment
{
    public string Author { get; set; } = "";
    public string? Avatar { get; set; }
    public string Text { get; set; } = "";
    public string When { get; set; } = "";
    public string Likes { get; set; } = "";
    public string LikesLiked { get; set; } = "";
    /// <summary>0 = indifferent, 1 = liked, -1 = disliked.</summary>
    public int LikeState { get; set; }
    public string? LikeAction { get; set; }
    public string? UnlikeAction { get; set; }
    public string? DislikeAction { get; set; }
    public string? UndislikeAction { get; set; }
    public string Replies { get; set; } = "";
    public bool ByOwner { get; set; }
    public bool Pinned { get; set; }
    public bool Hearted { get; set; }
    /// <summary>Token that loads this comment's replies; null when there are none.</summary>
    public string? RepliesToken { get; set; }
    public bool IsReply { get; set; }
    /// <summary>InnerTube perform_comment_action token to delete this comment (present on user's own comments).</summary>
    public string? DeleteAction { get; set; }
    public bool IsOwn { get; set; }
}

public sealed class CommentPage
{
    public List<Comment> Items { get; } = [];
    public string? Continuation { get; set; }
    public string? Count { get; set; }
    /// <summary>Params token for InnerTube comment/create_comment when signed in.</summary>
    public string? CreateCommentParams { get; set; }
}

/// <summary>One of the account's playlists, as the "save to playlist" list shows it.</summary>
public sealed class PlaylistRef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Contains { get; set; }
}

public sealed class Account
{
    public string Name { get; set; } = "";
    public string Handle { get; set; } = "";
    public string? Photo { get; set; }
    public string? ChannelId { get; set; }
}

/// <summary>The list a video is being watched in: a playlist, or a mix YouTube builds around the video.</summary>
public sealed class PlaylistPanel
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<VideoItem> Items { get; } = [];
    public bool IsMix => Id.StartsWith("RD");
}

public sealed class WatchInfo
{
    public PlaylistPanel? Playlist { get; set; }
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public string? ChannelId { get; set; }
    public string? ChannelAvatar { get; set; }
    public string Subscribers { get; set; } = "";
    public string Views { get; set; } = "";
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public string Likes { get; set; } = "";
    public FeedPage Related { get; set; } = new();
    public string? CommentsToken { get; set; }
    public string? CommentsCount { get; set; }
    public string? CreateCommentParams { get; set; }
    /// <summary>Comment orders ("popular first", "newest"): each is a token that reloads the list.</summary>
    public List<(string Title, string Token, bool Selected)> CommentSorts { get; } = [];
    public List<(double Start, string Title)> Chapters { get; } = [];
    /// <summary>100-point YouTube "Most Replayed" normalized intensity curve (0..1) for seekbar heatmap hills.</summary>
    public List<float> Heatmap { get; } = [];
    /// <summary>"LIKE", "DISLIKE" or "INDIFFERENT" for the signed-in account; null when not known.</summary>
    public string? LikeStatus { get; set; }
    /// <summary>Whether the signed-in user is subscribed to this video's channel.</summary>
    public bool? Subscribed { get; set; }
    /// <summary>True when YouTube attaches a paidContentOverlay ("Includes paid promotion").</summary>
    public bool HasPaidPromotion { get; set; }
    /// <summary>Initial InnerTube live_chat/get_live_chat continuation token when this video is a LIVE stream with chat.</summary>
    public string? LiveChatToken { get; set; }
}

public sealed record LiveChatMessage(
    string Id,
    string Author,
    string? Avatar,
    string Text,
    bool IsOwner,
    bool IsMod,
    bool IsMember,
    bool IsVerified,
    bool IsSystem,
    string? BadgeText,
    string? SuperChatAmount,
    uint AccentColor,
    List<string>? EmojiUrls = null);

public sealed record LiveChatViewOption(string Title, string Token, bool Selected);

public sealed record LiveChatPollResult(
    List<LiveChatMessage> Messages,
    List<string> DeletedIds,
    LiveChatMessage? PinnedMessage,
    string? NextToken,
    int TimeoutMs,
    string? SendParams,
    string? InputPlaceholder,
    string? RestrictedMessage,
    string? RestrictedDetail,
    int MaxChars,
    List<LiveChatViewOption>? Views);

public sealed record HeatmapPoint(double StartSec, double DurationSec, float Intensity);

public sealed class VideoAnalyticsData
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public string? ChannelId { get; set; }
    public string? ChannelAvatar { get; set; }
    public long Views { get; set; }
    public long Likes { get; set; }
    public long Dislikes { get; set; }
    public long CommentsCount { get; set; }
    public double Rating { get; set; }
    public double DurationSec { get; set; }
    public string PublishDate { get; set; } = "";
    public DateTime? PublishedUtc { get; set; }
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsLive { get; set; }
    public bool IsFamilySafe { get; set; } = true;
    public bool IsUnlisted { get; set; }
    public bool HasPaidPromotion { get; set; }
    public bool? AdsDetected { get; set; } // true if player response contains adPlacements/playerAds (monetized)
    public int ChaptersCount { get; set; }
    public List<string> Tags { get; } = [];

    // Format / technical capabilities straight from YouTube adaptiveFormats & captionTracks
    public int MaxHeight { get; set; }
    public int MaxFps { get; set; }
    public bool HasHdr { get; set; }
    public bool HasAv1 { get; set; }
    public bool HasVp9 { get; set; }
    public bool HasH264 { get; set; }
    public int MaxVideoBitrateKbps { get; set; }
    public int MaxAudioBitrateKbps { get; set; }
    public int AudioTrackCount { get; set; }
    public List<string> CaptionLangs { get; } = [];

    // 100-point YouTube "Most Replayed" viewer retention heatmap!
    public List<HeatmapPoint> Heatmap { get; } = [];

    // SponsorBlock crowdsourced segments
    public double SponsorSec { get; set; }
    public double SelfPromoSec { get; set; }
    public double OtherSkipSec { get; set; }
    public int SegmentCount { get; set; }

    // Personal local history from SQLite Db
    public int LocalPlays { get; set; }
    public double LocalWatchedSec { get; set; }
}

public sealed record ChannelVideoPoint(string VideoId, string Title, long Views, string When, string Duration, double ApproxAgeDays);

public sealed class ChannelAnalyticsData
{
    public string ChannelId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Handle { get; set; } = "";
    public string? Avatar { get; set; }
    public bool Verified { get; set; }
    public string SubscribersText { get; set; } = "";
    public long SubscribersNum { get; set; }
    public string VideoCountText { get; set; } = "";
    public long VideoCountNum { get; set; }
    public string TotalViewsText { get; set; } = "";
    public long TotalViewsNum { get; set; }
    public string JoinedDate { get; set; } = "";
    public DateTime? JoinedUtc { get; set; }
    public string Country { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> ChannelKeywords { get; } = [];
    public List<ChannelVideoPoint> RecentVideos { get; } = [];
    public long AvgViews { get; set; }
    public long MedianViews { get; set; }
    public long MinViews { get; set; }
    public long PeakViews { get; set; }
    public string PeakTitle { get; set; } = "";
    public double AvgDurationSec { get; set; }
    public double UploadsPerWeek { get; set; }
    public long EstMonthlyViews { get; set; }
    public double ConsistencyScore { get; set; } // 0..100 (low variance = high consistency)
    public int LocalUniqueVideos { get; set; }
    public int LocalSessions { get; set; }
    public double LocalWatchedSec { get; set; }
    public string? LocalFavVideoTitle { get; set; }
    public double LocalFavVideoSec { get; set; }
}
