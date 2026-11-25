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
    /// <summary>Set for a playlist card: VideoId is then its first video (for the thumbnail), Duration the video count.</summary>
    public string? PlaylistId { get; init; }
    /// <summary>A channel card (search results): ChannelId, Title and AvatarUrl are set, VideoId is empty.</summary>
    public bool IsChannel { get; init; }

    /// <summary>Resume progress 0..1 from local history, shown as a red bar.</summary>
    public double Progress { get; set; }

    public string Thumb => _thumb ??= $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg";
    public string ThumbHq => _thumbHq ??= $"https://i.ytimg.com/vi/{VideoId}/hqdefault.jpg";
    public string Url => $"https://www.youtube.com/watch?v={VideoId}";
    private string? _channelMeta;
    public string ChannelMeta => _channelMeta ??= string.IsNullOrEmpty(Meta) ? Channel : $"{Channel} · {Meta}";
    private string? _thumb, _thumbHq;
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
    public string Replies { get; set; } = "";
    public bool ByOwner { get; set; }
    public bool Pinned { get; set; }
    public bool Hearted { get; set; }
    /// <summary>Token that loads this comment's replies; null when there are none.</summary>
    public string? RepliesToken { get; set; }
    public bool IsReply { get; set; }
}

public sealed class CommentPage
{
    public List<Comment> Items { get; } = [];
    public string? Continuation { get; set; }
    public string? Count { get; set; }
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
    /// <summary>Comment orders ("popular first", "newest"): each is a token that reloads the list.</summary>
    public List<(string Title, string Token, bool Selected)> CommentSorts { get; } = [];
    public List<(double Start, string Title)> Chapters { get; } = [];
    /// <summary>"LIKE", "DISLIKE" or "INDIFFERENT" for the signed-in account; null when not known.</summary>
    public string? LikeStatus { get; set; }
}
