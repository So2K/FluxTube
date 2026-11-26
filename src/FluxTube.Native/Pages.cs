using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;
using FeedPage_ = FluxTube.Models.FeedPage;

namespace FluxTube.Pages;

/// <summary>Pieces every list of videos shares.</summary>
public static class Cards
{
    /// <param name="behind">The colour around the thumbnail: its corners are rounded off with it.</param>
    public static void ThumbUrl(R r, string? url, float radius, uint? behind = null)
    {
        var img = Images.Get(url, r.W, r.H);
        var a = img != null ? Images.Alpha(img) : 0;
        if (a < 1) Gfx.Fill(r, Th.Thumb, radius);
        if (img != null)
            Gfx.ImageRounded(img.Bmp!, r, radius, a);
    }

    public static void Thumb(R r, VideoItem v, bool hq, float radius, bool badge = true, uint? behind = null)
    {
        ThumbUrl(r, v.VideoId.Length == 11 ? (hq ? v.Thumb720 : v.Thumb) : null, radius, behind);
        if (badge && v.HasDuration)
        {
            var run = Gfx.Text(v.DurationBadge, 11.5f, 600);
            var b = new R(r.Right - run.W - 15, r.Bottom - run.H - 10, run.W + 9, run.H + 3);
            Gfx.Fill(b, v.IsLive ? Th.Brand : Th.Badge, 4);
            Gfx.Draw(run, b.X + 4.5f, b.Y + 1, 0xFFFFFFFF);
        }
        if (v.Progress > 0.001)
        {
            var bar = new R(r.X + radius * 0.7f, r.Bottom - 4, r.W - radius * 1.4f, 3);
            UI.Progress(bar, v.Progress);
        }
    }

    public static void Avatar(float x, float y, float size, string? url, string name)
    {
        var img = Images.Get(url, size, size, round: true);
        if (img != null) { Gfx.Image(img.Bmp!, new R(x, y, size, size), Images.Alpha(img)); return; }
        Gfx.Circle(x + size / 2, y + size / 2, size / 2, Th.Track);
        if (name.Length > 0)
        {
            var first = System.Globalization.StringInfo.GetNextTextElement(name).ToUpperInvariant();
            var run = Gfx.Text(first, size * 0.42f, 600);
            Gfx.Draw(run, x + (size - run.W) / 2, y + (size - run.H) / 2, Th.Sub);
        }
    }

    public static string QualityName(string q) => q switch
    {
        "best" => L.T("Максимальное", "Best"),
        "audio" => L.T("Только аудио (m4a)", "Audio only (m4a)"),
        "2160" => "4K (2160p)",
        "1440" => "2K (1440p)",
        _ => q + "p",
    };

    public static void Download(VideoItem v, string? quality)
    {
        Downloader.Instance.Enqueue(v.Url, v.Title, v.Channel, v.VideoId, quality);
        Shell.Toast(L.T("Скачивается: ", "Downloading: ") + v.Title, Th.Good);
    }

    public static void CopyText(string text, string? what = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        W.SetClipboardText(App.Hwnd, text);
        Shell.Toast(what ?? L.T("Текст скопирован", "Text copied"), Th.Good, 1800);
    }

    /// <summary>Right-click on a piece of text: a one-item menu that copies it.</summary>
    public static void CopyMenu(R r, string text, string? label = null)
    {
        if (text.Length > 0 && UI.RightClick(r)) Menus.Popup([new(label ?? L.T("Копировать текст", "Copy text"), () => CopyText(text), icon: Th.Copy, shortcut: "Ctrl+C")]);
    }

    public static void Copy(string text)
    {
        W.SetClipboardText(App.Hwnd, text);
        Shell.Toast(L.T("Ссылка скопирована", "Link copied"));
    }

    public static List<MenuItem> MenuFor(VideoItem v, Action? onRemove = null)
    {
        var items = new List<MenuItem>
        {
            new(L.T("Открыть в новой вкладке", "Open in new tab"), () => Shell.OpenVideoInNewTab(v), icon: Th.Tab, shortcut: L.T("СКМ", "MMB")),
            new(L.T("Запустить в фоне (звук)", "Play in background (audio)"), () => Shell.OpenVideoInBgTab(v), icon: Th.Headphones),
            MenuItem.Sep,
            new(L.T("Скачать (", "Download (") + QualityName(AppSettings.Current.DownloadQuality) + ")", () => Download(v, null), icon: Th.Download),
            new(L.T("Скачать в качестве", "Download quality"), null, AppSettings.Qualities.Select(q => new MenuItem(QualityName(q), () => Download(v, q), icon: Th.Quality)).ToList(), icon: Th.Quality),
        };
        if (CookieStore.IsLoggedIn)
        {
            items.Add(MenuItem.Sep);
            items.Add(new(L.T("Смотреть позже", "Watch later"), () => SaveTo("WL", L.T("Смотреть позже", "Watch later"), v, true), icon: Th.Later));
            items.Add(new(L.T("Сохранить в плейлист…", "Save to playlist…"), () => PlaylistMenu(v), icon: Th.Playlist));
        }
        items.Add(MenuItem.Sep);
        if (!string.IsNullOrEmpty(v.ChannelId))
            items.Add(new(L.T("Открыть канал: ", "Open channel: ") + v.Channel, () => Shell.OpenChannel(v.ChannelId!, v.Channel), icon: Th.Account));
        items.Add(new(L.T("Копировать ссылку", "Copy link"), () => Copy(v.Url), icon: Th.Link));
        items.Add(new(L.T("Копировать название", "Copy title"), () => CopyText(v.Title, L.T("Название скопировано", "Title copied")), icon: Th.Copy));
        items.Add(new(L.T("Открыть в браузере", "Open in browser"), () => W.Open(v.Url), icon: Th.Globe));
        if (!string.IsNullOrEmpty(v.ChannelId))
        {
            items.Add(MenuItem.Sep);
            items.Add(new(L.T("Не показывать канал: ", "Don't show channel: ") + v.Channel, () =>
            {
                var s = AppSettings.Current;
                if (!s.BlockedChannels.Contains(v.ChannelId)) { s.BlockedChannels = [.. s.BlockedChannels, v.ChannelId!]; s.Save(); }
                Shell.Toast(L.T($"Канал «{v.Channel}» скрыт из поиска и рекомендаций. Вернуть: Настройки, раздел «Поиск».", $"Channel \"{v.Channel}\" hidden from search & recommendations. Undo in Settings -> Search."), null, 6000);
                Shell.Current?.Refresh();
            }, icon: Th.Block, danger: true));
        }
        if (onRemove != null) { items.Add(MenuItem.Sep); items.Add(new(L.T("Удалить из списка", "Remove from list"), onRemove, icon: Th.Delete, danger: true)); }
        return items;
    }

    private static int _previewCardId;
    private static long _previewSince;

    /// <summary>
    /// One card of a video grid: thumbnail, avatar, title, channel line. The channel name and avatar lead to the channel,
    /// a playlist card to its list, the rest of the card to the video.
    /// </summary>
    public static void GridCard(R card, float thumbH, VideoItem v, int id, HoverPrefetch hover, string source, bool channelLine = true)
    {
        float x = card.X, y = card.Y, cardW = card.W;
        var over = UI.Hover(card);
        var cardTf = over ? Gfx.DC.Transform : MusicBounce.Push(card, ((id & 0x7FFFFFFF) * 5 + 2) % 28, amp: 0.56f, rotAmp: 0.36f);
        try
        {
        var cardFrame = card.Inset(-6, -6);
        float thumbRad = Gfx.Corner(12);
        uint behind;
        if (AppSettings.Current.UiCardStyle != "clean")
        {
            Gfx.DrawCardFrame(cardFrame, 14, over, out behind);
        }
        else
        {
            var glow = UI.Glow(id, card.Inset(-7, -7), Gfx.Corner(14), over);
            behind = UI.GlowOn(Th.Bg, glow);
        }
        if (v.IsChannel)
        {
            var d = MathF.Min(thumbH - 12, cardW * 0.5f);
            Avatar(x + (cardW - d) / 2, y + (thumbH - d) / 2, d, v.AvatarUrl, v.Title);
            var nr = Gfx.Text(v.Title, 15, 600, cardW, 1);
            Gfx.Draw(nr, x + (cardW - nr.W) / 2, y + thumbH + 8, Th.Text);
            var mr = Gfx.Text(v.Meta, 12.5f, 400, cardW, 1);
            Gfx.Draw(mr, x + (cardW - mr.W) / 2, y + thumbH + 12 + nr.H, Th.Sub);
            if (UI.Click(id, card, playSfx: false)) App.Post(() => Shell.OpenChannel(v.ChannelId!, v.Title));
            return;
        }
        if (v.PlaylistId != null)
        {
            Gfx.Fill(new R(x + 16, y - 6, cardW - 32, 8), Th.Track, Gfx.Corner(6));
            Gfx.Fill(new R(x + 8, y - 3, cardW - 16, 8), Gfx.Mix(Th.Track, Th.Card, 0.5f), Gfx.Corner(7));
        }
        var thumbR = new R(x, y, cardW, thumbH);
        bool wantPreview = over && v.PlaylistId == null && !v.IsLive && v.VideoId.Length == 11;
        if (wantPreview)
        {
            if (_previewCardId != id) { _previewCardId = id; _previewSince = UI.Now; }
            double hSec = (UI.Now - _previewSince) / 1000.0;
            if (hSec < 0.38) UI.Animate();
            if (hSec >= 0.35)
            {
                // Real hardware-decoded muted HD live video preview (hq720.jpg stays underneath until 1st frame renders!)
                PreviewPlayer.Want(v.VideoId, thumbR, Gfx.Clip, thumbRad);
                bool liveReady = PreviewPlayer.IsReadyFor(v.VideoId);
                Thumb(thumbR, v, true, thumbRad, badge: !liveReady, behind: behind);
                if (!liveReady)
                {
                    var pr = Gfx.Text("HD", 10.5f, 700);
                    var pb = new R(thumbR.X + 8, thumbR.Y + 8, pr.W + 12, pr.H + 4);
                    Gfx.Fill(pb, Gfx.Fade(0xFF0A0A0E, 0.72f), Gfx.Corner(5));
                    Gfx.Draw(pr, pb.X + 6, pb.Y + 2, 0xFFFFFFFF);
                }
            }
            else Thumb(thumbR, v, true, thumbRad, behind: behind);
        }
        else
        {
            if (_previewCardId == id && !over) _previewCardId = 0;
            Thumb(thumbR, v, true, thumbRad, behind: behind);
        }

        float tx = x, tw = cardW;
        bool showAvatar = v.AvatarUrl != null || (channelLine && v.PlaylistId == null && v.Channel.Length > 0);
        if (showAvatar) { Avatar(x, y + thumbH + 11, 34, v.AvatarUrl, v.Channel); tx += 44; tw -= 44; }
        var t = Gfx.Draw(v.Title, tx, y + thumbH + 9, 14, Th.Text, 600, tw, 2);
        var cy = y + thumbH + 12 + t.H;
        Gfx.Draw(channelLine ? v.ChannelMeta : v.Meta, tx, cy, 12.5f, Th.Sub, 400, tw, 2);
        var overChannel = false;
        if (channelLine && v.PlaylistId == null && over && !string.IsNullOrEmpty(v.ChannelId) && v.Channel.Length > 0)
        {
            var name = Gfx.Text(v.Channel, 12.5f, 400, tw);
            var chRect = new R(tx, cy, name.W, name.H);
            var avRect = new R(x, y + thumbH + 11, 34, 34);
            var overName = UI.Hover(chRect);
            overChannel = overName || (showAvatar && UI.Hover(avRect));
            if (overChannel)
            {
                Gfx.Fill(new R(tx, cy + name.H - 1.5f, name.W, 1), Th.Text);
                var targetR = overName ? chRect : avRect;
                if (UI.MiddleClick(targetR)) Shell.OpenInNewTab("channel", v.ChannelId!, v.Channel);
                else if (UI.Click(UI.Id(id, 9), targetR, playSfx: false)) App.Post(() => Shell.OpenChannel(v.ChannelId!, v.Channel));
            }
        }

        if (v.PlaylistId != null)
        {
            if (UI.MiddleClick(card)) Shell.OpenInNewTab("playlist", v.PlaylistId, v.Title);
            else if (UI.Click(id, card)) App.Post(() => Shell.OpenPlaylist(v.PlaylistId, v.Title));
            return;
        }
        if (over) hover.Over(v);
        if (UI.MiddleClick(card)) Shell.OpenVideoInNewTab(v);
        else if (overChannel) { }
        else if (UI.Click(id, card, playSfx: false)) App.Post(() => Shell.OpenVideo(v, source));
        else if (UI.RightClick(card)) Menus.Popup(MenuFor(v));
        }
        finally
        {
            MusicBounce.Pop(cardTf);
        }
    }

    /// <summary>Adds the video to one of the account's playlists or takes it out.</summary>
    public static void SaveTo(string playlistId, string playlistTitle, VideoItem v, bool add)
    {
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await InnerTube.Instance.PlaylistEditAsync(playlistId, v.VideoId, add); }
            catch (Exception e) { Paths.Log("playlist edit: " + e.Message); ok = false; }
            App.Post(() => Shell.Toast(!ok ? L.T("YouTube не принял изменение плейлиста", "YouTube rejected playlist update") : add ? L.T($"Добавлено в «{playlistTitle}»", $"Added to \"{playlistTitle}\"") : L.T($"Убрано из «{playlistTitle}»", $"Removed from \"{playlistTitle}\""), ok ? Th.Good : Th.Warn));
        });
    }

    /// <summary>The account's playlists as a menu; a tick marks the ones that already hold the video.</summary>
    public static void PlaylistMenu(VideoItem v)
    {
        _ = Task.Run(async () =>
        {
            List<PlaylistRef> lists;
            try { lists = await InnerTube.Instance.PlaylistsForAsync(v.VideoId); }
            catch (Exception e) { Paths.Log("playlists: " + e.Message); lists = []; }
            App.Post(() =>
            {
                if (lists.Count == 0) { Shell.Toast(L.T("Плейлисты не загрузились", "Could not load playlists"), Th.Warn); return; }
                Menus.Popup(lists.Select(p => new MenuItem(p.Id == "WL" ? L.T("Смотреть позже", "Watch later") : p.Title, () => SaveTo(p.Id, p.Id == "WL" ? L.T("Смотреть позже", "Watch later") : p.Title, v, !p.Contains), null, p.Contains)).ToList());
            });
        });
    }

    public static FeedPage_ WithProgress(FeedPage_ page)
    {
        var progress = Db.ProgressFor(page.Items.Select(i => i.VideoId));
        foreach (var v in page.Items)
            if (progress.TryGetValue(v.VideoId, out var p)) v.Progress = p;
        return page;
    }

    /// <summary>Hover-to-prefetch shared by grids and lists: the card under the cursor gets its stream resolved first.</summary>
    public sealed class HoverPrefetch
    {
        private string? _id;
        private long _since;
        private bool _done, _link;

        public void Over(VideoItem v)
        {
            if (_id != v.VideoId) { _id = v.VideoId; _since = UI.Now; _done = false; _link = false; }
            if (_done) return;
            if (!_link && (UI.Now - _since >= 50 || UI.Down)) { _link = true; QuickStream.Prefetch(v.VideoId); Shell.WarmPlayer(); }
            // the pointer has settled on the card: get its page (title, description, related) too
            if (UI.Now - _since >= 220 || UI.Down) { _done = true; _ = InnerTube.Instance.WatchCachedAsync(v.VideoId); }
            else UI.Animate();
        }
    }
}

/// <summary>The last feed of each section on disk: the window opens with content, the fresh feed replaces it a moment later.</summary>
public static class FeedCache
{
    private static string Dir_ => Path.Combine(Paths.Data, "feeds");
    private static string File_(string kind) => Path.Combine(Dir_, kind + ".json");

    public static void ClearAll()
    {
        try
        {
            if (Directory.Exists(Dir_))
                foreach (var f in Directory.GetFiles(Dir_, "*.json"))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public static void Save(string kind, List<VideoItem> items)
    {
        try
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var v in items.Take(60))
                arr.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = v.VideoId, ["t"] = v.Title, ["c"] = v.Channel, ["ci"] = v.ChannelId, ["d"] = v.Duration, ["m"] = v.Meta, ["a"] = v.AvatarUrl, ["l"] = v.IsLive,
                });
            Directory.CreateDirectory(Dir_);
            var dest = File_(kind);
            var tmp = dest + ".tmp";
            File.WriteAllText(tmp, arr.ToJsonString());
            File.Move(tmp, dest, overwrite: true);
        }
        catch (Exception e) { Paths.Log("feed cache save: " + e.Message); }
    }

    public static List<VideoItem>? Load(string kind)
    {
        try
        {
            var f = File_(kind);
            if (!File.Exists(f) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays > 3) return null;
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)) is not System.Text.Json.Nodes.JsonArray arr) return null;
            return arr.Select(n => new VideoItem
            {
                VideoId = n.S("id") ?? "", Title = n.S("t") ?? "", Channel = n.S("c") ?? "", ChannelId = n.S("ci"), Duration = n.S("d") ?? "",
                Meta = n.S("m") ?? "", AvatarUrl = n.S("a"), IsLive = n?["l"]?.GetValue<bool>() ?? false,
            }).Where(v => v.VideoId.Length == 11).ToList();
        }
        catch { return null; }
    }
}

/// <summary>Any grid of videos: home, subscriptions, watch later, liked, search, channel.</summary>
public sealed class FeedPage(string kind, string? arg, string? title) : IPage
{
    private List<VideoItem> _items = [];
    private readonly HashSet<string> _ids = [];
    private readonly HashSet<string> _seen = [];
    private readonly Cards.HoverPrefetch _hover = new();
    private string? _cont;
    private bool _loaded, _loading, _loadingMore;
    private string? _message;
    private uint _messageColor;
    private bool _messageInline, _messageDismissed;
    private int _gen;
    private float _lastScroll = -1;
    private long _settleAt;
    private bool _settled = true;
    private readonly int _scroll = UI.Id("feed:" + kind + arg);

    public string Tag => kind is "search" or "channel" or "playlist" ? "" : kind;
    public string Kind => kind;
    public string? Arg => arg;
    public string? Caption => title;
    public int MainScroll => _scroll;
    public int Count => _items.Count;
    public VideoItem ItemAt(int i) => _items[i];

    private string Source => kind switch { "search" => "search:" + arg, "channel" => "channel:" + arg, "playlist" => "playlist:" + arg, _ => kind };

    private string Header => kind switch
    {
        "home" => L.T("Главная", "Home"),
        "subs" => L.T("Подписки", "Subscriptions"),
        "wl" => L.T("Смотреть позже", "Watch later"),
        "liked" => L.T("Понравившиеся", "Liked videos"),
        "search" => $"«{arg}»",
        "channel" => title ?? L.T("Канал", "Channel"),
        "playlist" => title ?? L.T("Плейлист", "Playlist"),
        _ => kind,
    };

    public void Shown() { if (!_loaded) { _loaded = true; Load(); } }
    public void Hidden() { }
    public void Refresh()
    {
        _chipIdx = 0;
        _chipCache.Clear();
        if (_fresh is { } p) { _fresh = null; _message = null; Replace(p, saveCache: true); }
        else Load();
    }
    public void Invalidate()
    {
        _loaded = false;
        _fresh = null;
        _chipIdx = 0;
        _chipCache.Clear();
        _chips = [];
        _items = [];
        _ids.Clear();
    }

    private bool Cacheable => kind is "home" or "subs" or "wl" or "liked";

    // ---- search: YouTube's own filters plus a relevance check of our own ----
    public static int TestSort; // scripted checks: the order a new search page starts with
    private int _sSort = TestSort, _sDate, _sType = 1, _sDur;
    private bool _strict = AppSettings.Current.SearchStrict;
    private int _hidden, _autoMore;
    private string[][]? _need; // per query word: the spellings that count as a match

    private static readonly string[] SortNamesRu = ["По релевантности", "По рейтингу", "Сначала новые", "По просмотрам", "Сначала старые"];
    private static readonly string[] SortNamesEn = ["Relevance", "Rating", "Newest first", "View count", "Oldest first"];
    private static string[] SortNames => L.En ? SortNamesEn : SortNamesRu;

    // ---- "oldest first": YouTube's search has no such order, so the dates are walked by hand ----
    // The earliest year that has anything is found with "before:" queries (a handful of requests), its results are
    // put oldest first, and every "load more" adds the following year.
    private string Filter(int sort) => InnerTube.SearchParams(sort, 0, _sType, _sDur);

    private static double Age(VideoItem v)
    {
        var s = v.Meta;
        var dot = s.LastIndexOf('·');
        if (dot >= 0) s = s[(dot + 1)..];
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)\s*([\p{L}]+)");
        if (!m.Success) return 0;
        var n = double.Parse(m.Groups[1].Value);
        var u = m.Groups[2].Value.ToLowerInvariant();
        // YouTube writes both "14 лет назад" and the short "14 л. назад", "3 мес.", "2 ч"
        double unit = u.StartsWith("с") || u.StartsWith("sec") ? 1
            : u.StartsWith("мин") || u.StartsWith("min") ? 60
            : u.StartsWith("ч") || u.StartsWith("hour") ? 3600
            : u.StartsWith("д") || u.StartsWith("day") ? 86400
            : u.StartsWith("н") || u.StartsWith("week") ? 604800
            : u.StartsWith("мес") || u.StartsWith("month") ? 2629800
            : u.StartsWith("г") || u.StartsWith("л") || u.StartsWith("year") ? 31557600 : 0;
        return n * unit;
    }

    private static FeedPage_ OldestFirst(FeedPage_ page, string? next)
    {
        var sorted = page.Items.OrderByDescending(Age).ToList();
        var res = new FeedPage_ { Continuation = next };
        res.Items.AddRange(sorted);
        return res;
    }

    private async Task<FeedPage_> OldestStartAsync(InnerTube yt)
    {
        int lo = 2006, hi = DateTime.Now.Year + 1; // "before: January 1st of the year"
        FeedPage_? found = null;
        int foundYear = hi;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var page = await yt.SearchAsync($"{arg} before:{mid}-01-01", Filter(2));
            if (page.Items.Count > 0) { found = page; foundYear = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        if (found == null) return new FeedPage_();
        return OldestFirst(found, foundYear <= DateTime.Now.Year ? "year:" + foundYear : null);
    }

    /// <summary>The next year after the ones already shown (empty years are stepped over).</summary>
    private async Task<FeedPage_> OldestNextAsync(InnerTube yt, int year)
    {
        for (; year <= DateTime.Now.Year; year++)
        {
            var page = await yt.SearchAsync($"{arg} after:{year - 1}-12-31 before:{year + 1}-01-01", Filter(2));
            if (page.Items.Count > 0) return OldestFirst(page, year < DateTime.Now.Year ? "year:" + (year + 1) : null);
        }
        return new FeedPage_();
    }
    private static readonly string[] DateNamesRu = ["За всё время", "За час", "Сегодня", "За неделю", "За месяц", "За год"];
    private static readonly string[] DateNamesEn = ["Any time", "Last hour", "Today", "This week", "This month", "This year"];
    private static string[] DateNames => L.En ? DateNamesEn : DateNamesRu;
    private static readonly string[] TypeNamesRu = ["Всё", "Видео", "Каналы", "Плейлисты"];
    private static readonly string[] TypeNamesEn = ["All", "Videos", "Channels", "Playlists"];
    private static string[] TypeNames => L.En ? TypeNamesEn : TypeNamesRu;
    private static readonly (int Code, string Ru, string En)[] DurSpecs =
        [(0, "Любая длина", "Any duration"), (1, "До 4 минут", "Under 4 min"), (3, "4–20 минут", "4–20 min"), (2, "Больше 20 минут", "Over 20 min")];
    private static string DurLabel(int idx) => L.T(DurSpecs[idx].Ru, DurSpecs[idx].En);

    private static readonly HashSet<string> Stop = ["как", "для", "это", "что", "или", "при", "про", "без", "под", "над", "the", "and", "how", "for", "with", "from", "you", "что", "чтобы", "can", "его", "все"];
    // words people type in one language while the videos are titled in another (or shortened)
    private static readonly string[][] Same =
    [
        ["unreal", "анрил", "анриал", "ue5", "ue4", "ue 5", "ue 4", "уе5"],
        ["blender", "блендер", "блендере"],
        ["tutorial", "туториал", "урок", "гайд", "guide", "курс", "course", "обучен", "lesson", "how to", "как сделать", "с нуля", "beginner"],
        ["model", "моделир", "моделин", "модел"],
        ["engine", "движок", "движк"],
        ["godot", "годот", "годо"],
        ["unity", "юнити"],
        ["game", "игр"],
        ["animation", "анимац"],
        ["texture", "текстур"],
        ["sculpt", "скульпт"],
        ["shader", "шейдер"],
        ["programming", "программир", "coding", "кодинг"],
        ["review", "обзор"],
        ["music", "музык"],
        ["mix", "микс"],
    ];

    private static string[][] Needs(string query)
    {
        var list = new List<string[]>();
        foreach (var raw in query.ToLowerInvariant().Split([' ', ',', '.', ':', ';', '!', '?', '"', '(', ')', '/', '|', '+'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('-') || raw.StartsWith('#')) continue; // minus-words and hashtags are YouTube's business
            if (raw.Length < 3 && !raw.Any(char.IsDigit)) continue;
            if (Stop.Contains(raw)) continue;
            // word endings change (blender / в блендере, model / modeling): compare the stem
            var stem = raw.Length <= 4 ? raw : raw[..Math.Max(4, raw.Length - 2)];
            var group = Same.FirstOrDefault(g => g.Any(s => stem.StartsWith(s) || s.StartsWith(stem)));
            list.Add(group != null ? [stem, .. group] : [stem]);
        }
        return list.ToArray();
    }

    /// <summary>Does the title or the channel name really contain what was asked for?</summary>
    private bool Relevant(VideoItem v)
    {
        _need ??= Needs(arg ?? "");
        if (_need.Length == 0) return true;
        var hay = (v.Title + " " + v.Channel).ToLowerInvariant();
        int hit = 0;
        foreach (var words in _need)
            foreach (var w in words)
                if (hay.Contains(w)) { hit++; break; }
        // one or two words: every one of them; longer questions: most of them
        var need = _need.Length <= 2 ? _need.Length : (int)MathF.Ceiling(_need.Length * 0.6f);
        return hit >= need;
    }
    private bool _fromDisk;
    private FeedPage_? _fresh;
    private List<(string Title, string Token, bool Selected)> _chips = [];
    private readonly Dictionary<int, FeedPage_> _chipCache = new();
    private int _chipIdx;
    private float _chipScroll;

    // Curated Top-5 most popular & useful Home categories (mapped to real YouTube personalized filter tokens when available!)
    private static readonly (string Ru, string En, string[] MatchKeys, string FallbackToken)[] Top5HomeSpecs =
    [
        ("Рекомендации", "For you", ["все", "all", "рекомендац"], ""),
        ("Музыка", "Music", ["музык", "music"], "search:music"),
        ("Видеоигры", "Gaming", ["видеоигр", "игр", "gaming", "games"], "search:gaming"),
        ("Новое для вас", "New to you", ["новое для вас", "new to you", "недавно опубликован", "recently uploaded"], "search:new videos"),
        ("Подкасты", "Podcasts", ["подкаст", "podcast", "прямой эфир", "live", "в тренде"], "search:podcasts"),
    ];

    private static List<(string Title, string Token, bool Selected)> BuildTop5HomeChips(List<(string Title, string Token, bool Selected)> raw)
    {
        var list = new List<(string Title, string Token, bool Selected)>(5);
        for (int s = 0; s < Top5HomeSpecs.Length; s++)
        {
            var (ru, en, keys, fallback) = Top5HomeSpecs[s];
            string tok = fallback;
            if (s == 0) tok = ""; // For you always loads the exact 1-to-1 personalized Home feed
            else if (raw.Count > 0)
            {
                foreach (var c in raw)
                {
                    var low = c.Title.ToLowerInvariant();
                    if (keys.Any(k => low.Contains(k, StringComparison.Ordinal)) && !string.IsNullOrEmpty(c.Token))
                    {
                        tok = c.Token;
                        break;
                    }
                }
            }
            list.Add((L.T(ru, en), tok, s == 0));
        }
        return list;
    }

    private string _contEndpoint = "";
    private readonly Dictionary<int, string> _chipEndpoints = new();

    private void SelectChip(int idx)
    {
        if (idx < 0 || idx >= _chips.Count || idx == _chipIdx) return;
        PreviewPlayer.HideNow();

        // Always update current category snapshot so loaded infinite-scroll pages are preserved when switching back!
        if (_items.Count > 0)
        {
            var snap = new FeedPage_ { Continuation = _cont };
            snap.Items.AddRange(_items);
            _chipCache[_chipIdx] = snap;
            _chipEndpoints[_chipIdx] = _contEndpoint;
        }

        _chipIdx = idx;
        var chip = _chips[idx];
        var gen = ++_gen;
        _loadingMore = false;
        _message = null;

        // Instant 0ms switch when already visited!
        if (_chipCache.TryGetValue(idx, out var cached) && cached.Items.Count > 0)
        {
            _loading = false;
            _contEndpoint = _chipEndpoints.GetValueOrDefault(idx, "browse");
            Replace(cached, saveCache: false);
            App.Invalidate();
            return;
        }

        // Immediate visual switch: clear old cards right away so skeleton appears on click frame 0!
        _items = [];
        _ids.Clear();
        _cont = null;
        _contEndpoint = "";
        UI.ScrollReset(_scroll);
        _loading = true;
        App.Invalidate();

        var fallbackQuery = idx < Top5HomeSpecs.Length && Top5HomeSpecs[idx].FallbackToken.StartsWith("search:", StringComparison.Ordinal)
            ? (AppSettings.Current.Hl == "ru" ? chip.Title : Top5HomeSpecs[idx].FallbackToken[7..])
            : chip.Title;

        _ = Task.Run(async () =>
        {
            try
            {
                var yt = InnerTube.Instance;
                FeedPage_ page;
                string ep = "browse";
                if (string.IsNullOrEmpty(chip.Token))
                    page = await yt.HomeAsync();
                else if (chip.Token.StartsWith("search:", StringComparison.Ordinal))
                {
                    ep = "search";
                    page = await yt.SearchAsync(AppSettings.Current.Hl == "ru" ? chip.Title : chip.Token[7..]);
                }
                else
                {
                    try { page = await yt.ContinueAsync("browse", chip.Token); }
                    catch { page = new FeedPage_(); }
                    // Auto-fallback to search if YouTube continuation token expired or returned 0 items
                    if (page.Items.Count == 0 && !string.IsNullOrWhiteSpace(fallbackQuery))
                    {
                        ep = "search";
                        page = await yt.SearchAsync(fallbackQuery);
                    }
                }
                Cards.WithProgress(page);
                App.Post(() =>
                {
                    if (gen != _gen) return;
                    _loading = false;
                    _contEndpoint = ep;
                    if (page.Items.Count > 0) { _chipCache[idx] = page; _chipEndpoints[idx] = ep; }
                    Replace(page, saveCache: idx == 0);
                    if (_items.Count == 0) Say(L.T("По этой категории пока ничего не нашлось.", "Nothing found in this category yet."), Th.Sub);
                });
            }
            catch (Exception e)
            {
                Paths.Log($"home chip {chip.Title}: {e.Message}");
                App.Post(() => { if (gen == _gen) { _loading = false; Say(L.T("Не удалось загрузить категорию: ", "Failed to load category: ") + e.Message, Th.Brand); } });
            }
        });
    }

    private float DrawHomeChips(float x0, float y, float w)
    {
        if (_chips.Count == 0) return 0;
        const float h = 32, gap = 8;
        var barR = new R(x0, y, w, h);

        float totalW = 0;
        var widths = new float[_chips.Count];
        var labels = new string[_chips.Count];
        for (int i = 0; i < _chips.Count; i++)
        {
            labels[i] = i < Top5HomeSpecs.Length ? L.T(Top5HomeSpecs[i].Ru, Top5HomeSpecs[i].En) : _chips[i].Title;
            widths[i] = Gfx.Text(labels[i], 13, 600).W + 26;
            totalW += widths[i] + (i > 0 ? gap : 0);
        }
        float maxScroll = MathF.Max(0, totalW - w);
        if (UI.Hover(barR) && UI.Wheel != 0 && maxScroll > 0)
        {
            _chipScroll = Math.Clamp(_chipScroll - UI.Wheel * 96f, 0, maxScroll);
            UI.Wheel = 0;
            App.Invalidate();
        }
        _chipScroll = Math.Clamp(_chipScroll, 0, maxScroll);

        var lb = _chipScroll > 2 ? new R(x0, y, 28, h) : default;
        var rb = _chipScroll < maxScroll - 2 ? new R(x0 + w - 28, y, 28, h) : default;
        bool overChev = (lb.W > 0 && UI.Hover(lb)) || (rb.W > 0 && UI.Hover(rb));

        Gfx.PushClip(barR);
        float cx = x0 - _chipScroll;
        for (int i = 0; i < _chips.Count; i++)
        {
            float pw = widths[i];
            var r = new R(Gfx.Snap(cx), y, pw, h);
            if (r.Right >= x0 && r.X <= x0 + w)
            {
                bool active = i == _chipIdx;
                int id = UI.Id("hchip:" + i, _scroll);
                bool over = !overChev && UI.Hover(r);
                float chipRad = Gfx.Corner(8);
                bool brandChip = AppSettings.Current.UiCardStyle is "dashed" or "neon";
                if (active)
                {
                    Gfx.Fill(r, brandChip ? Th.Brand : Th.Text, chipRad);
                    if (AppSettings.Current.UiNeonGlow) Gfx.NeonGlow(r, chipRad, Th.Brand, 0.55f);
                }
                else
                {
                    Gfx.Fill(r, Th.A(over ? 0.16f : 0.085f), chipRad);
                    if (AppSettings.Current.UiCardStyle == "dashed") Gfx.StrokeDashed(r, Gfx.Fade(Th.Brand, 0.28f), chipRad, 1f);
                }
                var tr = Gfx.Text(labels[i], 13, active ? 600 : 500);
                Gfx.Draw(tr, Gfx.Snap(r.X + (r.W - tr.W) / 2), Gfx.Snap(r.Y + (r.H - tr.H) / 2), active ? (brandChip ? Th.OnBrand : Th.Bg) : Th.Text);
                if (!overChev && UI.Click(id, r)) SelectChip(i);
            }
            cx += pw + gap;
        }
        Gfx.PopClip();

        if (lb.W > 0)
        {
            Gfx.Fill(lb, Gfx.Fade(Th.Bg, 0.92f), 6);
            if (UI.IconButton(UI.Id("hchip:l", _scroll), lb, Th.Back, 11, Th.Text)) _chipScroll = MathF.Max(0, _chipScroll - 220);
        }
        if (rb.W > 0)
        {
            Gfx.Fill(rb, Gfx.Fade(Th.Bg, 0.92f), 6);
            if (UI.IconButton(UI.Id("hchip:r", _scroll), rb, Th.ChevronRight, 11, Th.Text)) _chipScroll = MathF.Min(maxScroll, _chipScroll + 220);
        }
        return h + 14;
    }

    private void Replace(FeedPage_ page, bool saveCache = true)
    {
        _items = []; _ids.Clear();
        _hidden = 0; _autoMore = 0;
        Append(page);
        UI.ScrollReset(_scroll);
        _settleAt = UI.Now + 100; _settled = false;
        if (saveCache && Cacheable && page.Items.Count > 0) { var copy = page.Items.ToList(); var k = kind; ThreadPool.QueueUserWorkItem(_ => FeedCache.Save(k, copy)); }
    }

    private void Load()
    {
        var gen = ++_gen;
        _loading = true;
        _loadingMore = false; // whatever was being fetched belonged to the old list
        _message = null;
        if (kind is "subs" or "wl" or "liked" && !CookieStore.IsLoggedIn)
        {
            _items = []; _ids.Clear(); _cont = null; _loading = false;
            Say(L.T("Войдите в аккаунт YouTube (кнопка справа сверху), чтобы видеть этот раздел.", "Sign in to YouTube (top right button) to see this section."), Th.Sub);
            return;
        }
        if (kind == "home" && _chips.Count == 0)
            _chips = BuildTop5HomeChips([]);
        if (_items.Count == 0 && Cacheable && FeedCache.Load(kind) is { Count: > 0 } saved)
        {
            // yesterday's feed is on screen (and its streams are being prepared) while today's is on its way
            var page0 = new FeedPage_();
            page0.Items.AddRange(saved);
            Cards.WithProgress(page0);
            Append(page0);
            _fromDisk = true;
            _settleAt = UI.Now + 100; _settled = false;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                string? note = null;
                var yt = InnerTube.Instance;
                var page = kind switch
                {
                    "home" => await yt.HomeAsync(),
                    "subs" => await yt.BrowseAsync("FEsubscriptions"),
                    "wl" => await yt.BrowseAsync("VLWL"),
                    "liked" => await yt.BrowseAsync("VLLL"),
                    "search" => _sSort == 4 ? await OldestStartAsync(yt) : await yt.SearchAsync(arg!, InnerTube.SearchParams(_sSort, _sDate, _sType, _sDur)),
                    "channel" => await yt.ChannelVideosAsync(arg!),
                    "playlist" => await yt.BrowseAsync(arg!),
                    _ => new FeedPage_(),
                };
                if (kind == "home" && page.Items.Count == 0)
                {
                    page = await ForYouAsync();
                    note = page.Items.Count == 0
                        ? L.T("YouTube не даёт рекомендации без входа и истории. Войдите в аккаунт или найдите что-нибудь через поиск.", "YouTube doesn't provide recommendations without sign-in or history. Sign in or search for something.")
                        : L.T("Вы не вошли: лента собрана из похожих на ваши последние просмотры.", "Guest mode: feed built from videos related to your recent watches.");
                }
                Cards.WithProgress(page);
                App.Post(() =>
                {
                    if (gen != _gen) return;
                    _loading = false;
                    var wasDisk = _fromDisk;
                    _fromDisk = false;
                    if (wasDisk && page.Items.Count > 0 && Shell.Current == this && UI.ScrollTarget(_scroll) > 120)
                    {
                        _fresh = page;
                        if (!_items.Take(8).Select(i => i.VideoId).SequenceEqual(page.Items.Take(8).Select(i => i.VideoId)))
                            Say(L.T("Лента обновилась. Нажмите «Обновить», чтобы показать новую.", "Feed updated. Click \"Refresh\" to show the latest."), Th.Sub);
                        return;
                    }
                    if (kind == "home")
                    {
                        _chips = BuildTop5HomeChips(page.Chips);
                        _chipIdx = 0;
                        if (page.Items.Count > 0) _chipCache[0] = page;
                    }
                    Replace(page, saveCache: true);
                    if (note != null) Say(note, Th.Sub, inline: page.Items.Count > 0);
                    else if (_items.Count == 0 && _cont == null) Say(L.T("Здесь пока пусто.", "Nothing here yet."), Th.Sub);
                });
            }
            catch (Exception e)
            {
                Paths.Log($"feed {kind}: {e}");
                App.Post(() => { if (gen == _gen) { _loading = false; Say(L.T("Не удалось загрузить: ", "Failed to load: ") + e.Message, Th.Brand); } });
            }
        });
    }

    private void Say(string text, uint color, bool inline = false) { _message = text; _messageColor = color; _messageInline = inline; _messageDismissed = false; App.Invalidate(); }

    private float _cardW;

    private void Append(FeedPage_ page)
    {
        var warm = new List<(string, float, int, float, int)>();
        var blocked = kind is "subs" or "wl" or "liked" or "playlist" ? [] : AppSettings.Current.BlockedChannels;
        foreach (var v in page.Items)
        {
            if (!_ids.Add(v.IsChannel ? v.ChannelId ?? "" : v.PlaylistId ?? v.VideoId)) continue;
            if (blocked.Length > 0 && v.ChannelId != null && Array.IndexOf(blocked, v.ChannelId) >= 0) continue;
            if (kind == "search" && _strict && !v.IsChannel && v.PlaylistId == null && !Relevant(v)) { _hidden++; continue; }
            _items.Add(v);
            if (_cardW <= 0) continue;
            var tw = v.AvatarUrl != null ? _cardW - 44 : _cardW;
            warm.Add((v.Title, 14, 600, tw, 2));
            warm.Add((v.ChannelMeta, 12.5f, 400, tw, 2));
            warm.Add((v.DurationBadge, 11.5f, 600, 0, 1));
        }
        Gfx.Prewarm(warm);
        _cont = page.Items.Count == 0 ? null : page.Continuation;
        // the check threw most of a page away: fetch the next one right now instead of showing a thin list
        if (kind == "search" && _strict && _items.Count < 16 && _cont != null && _autoMore < 5) { _autoMore++; App.Post(LoadMore); }
    }

    private void Refilter() { _need = null; Load(); }

    /// <summary>The filter row of a search page; returns its height.</summary>
    private float DrawSearchBar(float x, float y, float w)
    {
        float cx = x;
        void Choice(string key, string label, string[] names, int current, Action<int> set)
        {
            if (UI.Pill(UI.Id("sf:" + key, _scroll), cx, y, 32, null, label, out var pw, current != 0 && key != "type" || key == "type" && current != 1 ? Gfx.Fade(Th.Brand, 0.22f) : Th.A(0.08f)))
                Menus.Popup(names.Select((n, i) => new MenuItem(n, () => { set(i); Refilter(); }, null, i == current)).ToList());
            cx += pw + 8;
        }
        Choice("type", TypeNames[_sType], TypeNames, _sType, i => _sType = i);
        if (_sSort != 4) Choice("date", DateNames[_sDate], DateNames, _sDate, i => _sDate = i);
        var durIdx = Array.FindIndex(DurSpecs, d => d.Code == _sDur);
        Choice("dur", DurLabel(durIdx), Enumerable.Range(0, DurSpecs.Length).Select(DurLabel).ToArray(), durIdx, i => _sDur = DurSpecs[i].Code);
        Choice("sort", SortNames[_sSort], SortNames, _sSort, i => _sSort = i);
        // our own check on top of YouTube's answer
        if (UI.Pill(UI.Id("sf:strict", _scroll), cx, y, 32, _strict ? Th.Check : null, L.T("Только по теме", "On-topic only"), out var sw, _strict ? Gfx.Fade(Th.Brand, 0.85f) : Th.A(0.08f), _strict ? Th.OnBrand : Th.Text))
        {
            _strict = !_strict;
            AppSettings.Current.SearchStrict = _strict;
            AppSettings.Current.Save();
            Refilter();
        }
        cx += sw + 12;
        if (_strict && _hidden > 0) Gfx.Draw(L.T($"скрыто не по теме: {_hidden}", $"off-topic hidden: {_hidden}"), cx, y + 8, 12.5f, Th.Dim);
        return 44;
    }

    private void LogSeen(VideoItem v) { var src = Source; ThreadPool.QueueUserWorkItem(_ => Db.Seen(v, src)); }

    private void LoadMore()
    {
        if (_cont == null || _loadingMore) return;
        _loadingMore = true;
        var gen = _gen;
        var tok = _cont;
        var endpoint = !string.IsNullOrEmpty(_contEndpoint) ? _contEndpoint : (kind == "search" ? "search" : "browse");
        _ = Task.Run(async () =>
        {
            try
            {
                var page = Cards.WithProgress(tok.StartsWith("year:")
                    ? await OldestNextAsync(InnerTube.Instance, int.Parse(tok[5..]))
                    : await InnerTube.Instance.ContinueAsync(endpoint, tok));
                App.Post(() =>
                {
                    if (gen != _gen) return;
                    Append(page);
                    _loadingMore = false;
                    if (_items.Count == 0 && _cont == null) Say(kind == "search" && _strict ? L.T("По теме ничего не нашлось. Выключите «Только по теме», чтобы увидеть всё, что отдал YouTube.", "No exact matches. Turn off \"On-topic only\" to see all YouTube results.") : L.T("Здесь пока пусто.", "Nothing here yet."), Th.Sub);
                });
            }
            catch (Exception e)
            {
                Paths.Log("feed more: " + e.Message);
                App.Post(() => { if (gen == _gen) { _loadingMore = false; _cont = null; } });
            }
        });
    }

    /// <summary>Local recommendations: related videos of the last few things watched.</summary>
    private static async Task<FeedPage_> ForYouAsync()
    {
        var ids = Db.RecentWatchedIds(4);
        var result = new FeedPage_();
        if (ids.Count == 0) return result;
        var lists = await Task.WhenAll(ids.Select(async id =>
        {
            try { return (await InnerTube.Instance.WatchAsync(id)).Related.Items; }
            catch { return new List<VideoItem>(); }
        }));
        var seen = new HashSet<string>(ids);
        for (int i = 0; i < lists.Max(l => l.Count); i++)
            foreach (var l in lists)
                if (i < l.Count && seen.Add(l[i].VideoId)) result.Items.Add(l[i]);
        return result;
    }

    public void Draw(R r)
    {
        float x0 = r.X + 16;
        var headRun = Gfx.Draw(Header, x0, r.Y + 8, 22, Th.Text, 600, r.W - 260);
        var refreshLbl = L.T("Обновить", "Refresh");
        var rw = UI.PillWidth(Th.Refresh, refreshLbl);
        float rightBtnX = r.Right - rw - 16;
        if (UI.Pill(UI.Id("refresh", _scroll), rightBtnX, r.Y + 8, 32, Th.Refresh, refreshLbl, out _, Th.A(0.08f))) App.Post(Refresh);
        if (_loading && _items.Count > 0) UI.Spinner(rightBtnX - 24, r.Y + 24, 8, Th.Sub);

        // Compact inline notice chip right in the header row (saves an entire 52px banner row!)
        if (_message != null && _messageInline && !_messageDismissed)
        {
            var shortText = !CookieStore.IsLoggedIn && kind == "home" ? L.T("Гостевая лента по вашим просмотрам", "Guest feed from your recent watches") : _message;
            float chipX = x0 + headRun.W + 16;
            float maxChipW = rightBtnX - (_loading ? 42 : 14) - chipX;
            if (maxChipW > 180)
            {
                bool showLogin = !CookieStore.IsLoggedIn && kind == "home";
                var loginRun = showLogin ? Gfx.Text(L.T("Войти", "Sign in"), 12, 600) : null;
                float extraW = (loginRun != null ? loginRun.W + 18 : 0) + 26;
                var msgRun = Gfx.Text(shortText, 12, 400, MathF.Max(60, maxChipW - 32 - extraW));
                float chipW = 28 + msgRun.W + extraW;
                var chip = new R(chipX, r.Y + 10, chipW, 28);
                Gfx.Fill(chip, Th.A(0.065f), 14);
                Gfx.Icon(Th.Info, new R(chip.X + 6, chip.Y, 18, chip.H), 12, Th.Sub);
                Gfx.Draw(msgRun, chip.X + 26, Gfx.Snap(chip.Y + (chip.H - msgRun.H) / 2), Th.Sub);
                if (loginRun != null)
                {
                    var lr = new R(chip.X + 26 + msgRun.W + 10, chip.Y + 3, loginRun.W + 8, 22);
                    Gfx.Draw(loginRun, lr.X + 4, Gfx.Snap(lr.Y + (lr.H - loginRun.H) / 2), UI.Hover(lr) ? Th.Text : Th.Brand);
                    if (UI.Click(UI.Id("chip:login", _scroll), lr)) App.Post(Shell.Login);
                }
                var closeChip = new R(chip.Right - 24, chip.Y + 4, 20, 20);
                if (UI.IconButton(UI.Id("chip:close", _scroll), closeChip, Th.Close, 8.5f, Th.Dim)) _messageDismissed = true;
            }
        }

        float top = r.Y + 44;
        if (kind == "home") top += DrawHomeChips(x0, top, r.W - 32);
        if (kind == "search") top += DrawSearchBar(x0, top, r.W - 32);
        if (kind == "playlist" && _items.Count > 0 && arg is { Length: > 2 })
        {
            if (UI.Pill(UI.Id("pl:play", _scroll), x0, top, 36, Th.Play, L.T("Воспроизвести все", "Play all"), out var pw, accent: true))
            {
                var firstV = _items[0]; var pid = arg[2..];
                App.Post(() => Shell.OpenVideo(firstV, "playlist:" + pid, pid));
            }
            Gfx.Draw($"{_items.Count}{(_cont != null ? "+" : "")} " + L.T("видео", "videos"), x0 + pw + 14, top + 9, 13, Th.Sub);
            top += 48;
        }
        if (_message != null && !_messageInline && !_messageDismissed)
        {
            var run = Gfx.Text(_message, 13.5f, 400, r.W - 110, 3);
            var box = new R(x0, top, r.W - 32, run.H + 20);
            Gfx.Fill(box, Th.A(0.07f), 10);
            Gfx.Fill(new R(box.X, box.Y + 8, 3, box.H - 16), _messageColor, 1.5f);
            Gfx.Draw(run, box.X + 18, box.Y + 10, Th.Text);
            if (_items.Count > 0 && UI.IconButton(UI.Id("msg:close", _scroll), new R(box.Right - 30, box.Y + (box.H - 22) / 2, 22, 22), Th.Close, 9, Th.Dim))
                _messageDismissed = true;
            top += box.H + 10;
        }

        const float gap = 14;
        var view = new R(x0, top, r.Right - x0 - 16, r.Bottom - top);
        var usable = view.W - 12;
        var cols = Math.Max(1, (int)((usable + gap) / (270 + gap)));
        var cardW = MathF.Floor((usable - gap * (cols - 1)) / cols);
        var thumbH = MathF.Round(cardW * 9 / 16);
        var rowH = thumbH + 96;
        _cardW = cardW;

        if (_loading && _items.Count == 0)
        {
            // skeleton: the layout is on screen before the network answers
            for (int i = 0; i < cols * 3; i++)
            {
                float sx = view.X + (i % cols) * (cardW + gap), sy = view.Y + (i / cols) * rowH;
                if (sy > r.Bottom) break;
                var sk = UI.Pulse(Th.Thumb);
                Gfx.Fill(new R(sx, sy, cardW, thumbH), sk, 12);
                Gfx.Fill(new R(sx + 46, sy + thumbH + 14, cardW * 0.7f, 13), sk, 4);
                Gfx.Fill(new R(sx + 46, sy + thumbH + 36, cardW * 0.45f, 11), sk, 4);
                Gfx.Circle(sx + 17, sy + thumbH + 28, 17, sk);
            }
            return;
        }

        var rows = (_items.Count + cols - 1) / cols;
        var scroll = UI.BeginScroll(_scroll, view, rows * rowH + (_cont != null ? 70 : 24));
        int first = Math.Max(0, (int)(scroll / rowH)), last = Math.Min(rows - 1, (int)((scroll + view.H) / rowH));
        for (int row = first; row <= last; row++)
            for (int col = 0; col < cols; col++)
            {
                int i = row * cols + col;
                if (i >= _items.Count) break;
                var v = _items[i];
                float x = view.X + col * (cardW + gap), y = view.Y + row * rowH - scroll;
                Cards.GridCard(new R(x, y, cardW, rowH - 10), thumbH, v, UI.Id(_scroll, i), _hover, Source);
                if (v.PlaylistId == null && !v.IsChannel && _seen.Add(v.VideoId)) LogSeen(v);
            }
        if (kind == "home")
        {
            // Start fetching the next few rows while they are still off screen.
            // Include the scroll target so a fast wheel gesture does not outrun the queue.
            var target = UI.ScrollTarget(_scroll);
            int targetFirst = Math.Max(0, (int)(target / rowH));
            int targetLast = Math.Min(rows - 1, (int)((target + view.H) / rowH));
            int warmFirst = Math.Max(0, Math.Min(first, targetFirst) - 2);
            int warmLast = Math.Min(rows - 1, Math.Max(last, targetLast) + 3);
            for (int row = warmFirst; row <= warmLast; row++)
            {
                if (row >= first && row <= last) continue;
                for (int col = 0; col < cols; col++)
                {
                    int i = row * cols + col;
                    if (i >= _items.Count) break;
                    var v = _items[i];
                    if (v.VideoId.Length == 11) Images.Get(v.Thumb720, cardW, thumbH);
                    if (v.AvatarUrl != null) Images.Get(v.AvatarUrl, 34, 34, round: true);
                }
            }
        }
        if (_loadingMore) UI.Spinner(view.X + usable / 2, view.Y + rows * rowH - scroll + 26, 10, Th.Sub);
        UI.EndScroll();

        if (_cont != null && (UI.ScrollMax(_scroll) > 0 && UI.ScrollTarget(_scroll) > UI.ScrollMax(_scroll) - rowH * 2
            || UI.ScrollMax(_scroll) == 0 && _autoMore < 5))
        {
            if (UI.ScrollMax(_scroll) == 0) _autoMore++;
            LoadMore();
        }

        // when scrolling stops, resolve the streams of the top 6 visible cards ahead of the click (no 20-card storm)
        if (scroll != _lastScroll) { _lastScroll = scroll; _settleAt = UI.Now + 180; _settled = false; }
        if (!_settled)
        {
            if (UI.Now < _settleAt) UI.Animate();
            else
            {
                _settled = true;
                int startIdx = first * cols;
                int endIdx = Math.Min(_items.Count - 1, startIdx + 5);
                for (int i = endIdx; i >= startIdx && i >= 0; i--)
                    QuickStream.Prefetch(_items[i].VideoId);
            }
        }
    }
}

public sealed class WatchPage : IPage
{
    private bool _musicVolumeDrag, _musicLoop;
    private int _musicVolumeTarget;
    private float _commentsSectionY;
    public VideoItem Video { get; private set; }
    public string Title => _info?.Title ?? Video.Title;
    private readonly string _source;
    private WatchInfo? _info;
    private List<VideoItem> _related = [];
    private readonly HashSet<string> _seen = [];
    private readonly Cards.HoverPrefetch _hover = new();
    private bool _infoLoading, _descOpen;
    private string _dislikes = "—", _likes = "—", _speed = "1×";
    private string? _note;
    private readonly int _infoScroll, _relScroll;
    private float _relTextW;
    private List<SoundCloudBar.TimedMarker> _scMarkers = [];
    private int _scMarkerComments = -1;
    private int _scMarkerDurSec = -1;
    private int _scPushedComments = -1;
    private int _scPushedDurSec = -1;

    private IReadOnlyList<SoundCloudBar.TimedMarker> GetSoundCloudMarkers(double dur)
    {
        int dSec = (int)Math.Round(dur);
        if (_scMarkerComments != _comments.Count || Math.Abs(_scMarkerDurSec - dSec) > 2)
        {
            _scMarkerComments = _comments.Count;
            _scMarkerDurSec = dSec;
            var rawComments = new List<Comment>(_comments.Count);
            foreach (var row in _comments) rawComments.Add(row.C);
            _scMarkers = SoundCloudBar.ExtractMarkers(rawComments, _info?.Chapters ?? [], dur);
        }
        return _scMarkers;
    }

    /// <summary>Timed comment avatar markers for the mpv video seekbar overlay (fluxosc.lua).</summary>
    public System.Text.Json.Nodes.JsonArray PlayerCommentsJson()
    {
        if (!AppSettings.Current.SoundCloudWave || AppSettings.Current.AudioOnlyMode || Pip.Active
            || PlaybackTracker.Instance.Current?.VideoId != Video.VideoId)
            return new System.Text.Json.Nodes.JsonArray();
        var dur = MpvPlayer.Instance.Duration;
        if (dur <= 5) return new System.Text.Json.Nodes.JsonArray();
        var markers = GetSoundCloudMarkers(dur);
        return SoundCloudBar.BuildPlayerCommentsJson(markers, dur, Gfx.Scale, Shell.PushPlayerState);
    }

    public string Tag => "";
    public int MainScroll => _infoScroll; // Page Up/Down read the description and comments

    private readonly string? _playlistId;
    private PlaylistPanel? _list;
    private readonly int _listScroll;
    private bool _listJump;
    private bool _startPaused;
    public string? PlaylistId => _playlistId;

    public WatchPage(VideoItem v, string source, string? playlistId = null, bool startPaused = false)
    {
        Video = v;
        _source = source;
        _playlistId = playlistId;
        _startPaused = startPaused;
        _listScroll = UI.Id("watch:list:" + v.VideoId);
        _infoScroll = UI.Id("watch:info:" + v.VideoId);
        _relScroll = UI.Id("watch:rel:" + v.VideoId);
    }

    /// <summary>Chapters of this video for the player's seek bar (empty while another video is playing).</summary>
    public System.Text.Json.Nodes.JsonArray ChaptersJson()
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        if (_info == null || PlaybackTracker.Instance.Current?.VideoId != Video.VideoId) return arr;
        foreach (var (start, title) in _info.Chapters)
            arr.Add(new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonValue.Create(start), System.Text.Json.Nodes.JsonValue.Create(title)));
        return arr;
    }

    private string _rating = ""; // "LIKE" / "DISLIKE" / "" for the signed-in account

    /// <summary>Like or dislike; pressing the active one takes the mark back. Plays dedicated Like SFX.</summary>
    private void Rate(bool like)
    {
        if (!CookieStore.IsLoggedIn) { Shell.Toast(L.T("Чтобы ставить оценки, войдите в аккаунт YouTube", "Sign in to YouTube to rate videos"), Th.Warn); return; }
        Sfx.Like();
        var want = like ? "LIKE" : "DISLIKE";
        var before = _rating;
        var action = _rating == want ? "removelike" : like ? "like" : "dislike";
        _rating = _rating == want ? "" : want;
        var id = Video.VideoId;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await InnerTube.Instance.RateAsync(id, action); }
            catch (Exception e) { Paths.Log("rate: " + e.Message); ok = false; }
            App.Post(() =>
            {
                if (!ok) { _rating = before; Shell.Toast(L.T("YouTube не принял оценку, попробуйте позже", "YouTube rejected the rating, try later"), Th.Warn); }
                else Shell.Toast(action switch { "like" => L.T("Вам понравилось это видео", "Liked video"), "dislike" => L.T("Вам не понравилось это видео", "Disliked video"), _ => L.T("Оценка снята", "Rating removed") }, Th.Good, 2000);
                App.Invalidate();
            });
        });
    }

    private bool _nextReady;

    /// <summary>The last half minute: the next video gets its link, first bytes and page data now, so it starts at once.</summary>
    public void NearEnd(double t, double duration)
    {
        if (_nextReady || duration < 60 || duration - t > 30 || PlaybackTracker.Instance.Current?.VideoId != Video.VideoId) return;
        if (NextUp() is not { } next) return;
        _nextReady = true;
        QuickStream.Prefetch(next.VideoId);
        _ = InnerTube.Instance.WatchCachedAsync(next.VideoId);
        Images.Get(next.ThumbHq, 480, 270);
    }

    private int ListIndex => _list?.Items.FindIndex(i => i.VideoId == Video.VideoId) ?? -1;

    /// <summary>What plays after this: the next item of the playlist when there is one, otherwise the first related video not seen yet.</summary>
    public VideoItem? NextUp()
    {
        if (_list != null && ListIndex is var i && i + 1 < _list.Items.Count) return _list.Items[i + 1];
        return _related.FirstOrDefault(r => r.Progress < 0.9);
    }

    /// <summary>Inside a playlist the next item follows on its own, whatever the autoplay switch says.</summary>
    public bool ContinuesList => _list != null && ListIndex + 1 < _list.Items.Count;

    public void GoNext()
    {
        if (NextUp() is not { } next) return;
        if (ContinuesList) OpenInList(next);
        else Shell.OpenVideo(next, RelSource);
    }

    private void OpenInList(VideoItem v)
    {
        var id = _list!.Id;
        App.Post(() => Shell.OpenVideo(v, "playlist:" + id, id));
    }

    /// <summary>The link to this page (Ctrl+C with nothing selected), with the playlist when watching inside one.</summary>
    public string PageLink(bool withTime)
    {
        var at = withTime ? (int)MpvPlayer.Instance.TimePos : 0;
        if (_list == null) return SharePanel.LinkFor(Video.VideoId, at);
        return $"https://www.youtube.com/watch?v={Video.VideoId}&list={_list.Id}" + (at > 1 ? "&t=" + at : "");
    }
    public string Speed
    {
        get
        {
            var sp = MpvPlayer.Instance.Speed;
            if (sp > 0.1) return Math.Abs(sp - 1.0) < 0.01 ? "1×" : sp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
            return _speed;
        }
    }
    public void ToggleTheater() { AppSettings.Current.TheaterMode = !AppSettings.Current.TheaterMode; AppSettings.Current.Save(); App.Invalidate(); }

    private int _infoGen;
    private int _relPrewarmed;

    public void Shown()
    {
        bool startPaused = _startPaused;
        _startPaused = false;
        // Never hijack an active Picture-in-Picture window when browsing or switching to another video tab!
        if (Pip.Active && PlaybackTracker.Instance.Current?.VideoId != Video.VideoId)
        {
            if (_info == null && !_infoLoading) LoadInfo(bypassCache: false);
            return;
        }
        if (!Shell.CurrentTabOwnsMainPlayer || PlaybackTracker.Instance.Current?.VideoId != Video.VideoId || !MpvPlayer.Instance.HasMedia)
        {
            Shell.Play(Video, _source, startPaused);
            _subKey = ""; _audioId = "";
        }
        else if (!startPaused && AppSettings.Current.AutoPlayOpenedTabs && MpvPlayer.Instance.Paused)
        {
            MpvPlayer.Instance.SetPause(false);
        }
        var sp = MpvPlayer.Instance.Speed;
        if (sp > 0.1) _speed = sp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
        if (_info == null && !_infoLoading) LoadInfo(bypassCache: false);
    }

    public void Hidden()
    {
        SharePanel.Close();
        _commentEdit?.Hide();
        if (_comments.Count > 120) _comments.RemoveRange(120, _comments.Count - 120);
    }

    public void Refresh()
    {
        _info = null;
        _note = null;
        _commentGen++;
        _comments.Clear();
        _commentTok = null;
        _commentCount = null;
        _createCommentParams = null;
        _commentsStarted = _commentsLoading = _commentsOff = false;
        _scMarkerComments = _scMarkerDurSec = _scPushedComments = _scPushedDurSec = -1;
        _scMarkers = [];
        LoadInfo(bypassCache: true);
    }

    private string RelSource => $"related:{Video.VideoId}|{Title}";

    private void LoadInfo(bool bypassCache = false)
    {
        var gen = ++_infoGen;
        _infoLoading = true;
        var id = Video.VideoId;
        _ = Task.Run(async () =>
        {
            try
            {
                var info = bypassCache
                    ? await InnerTube.Instance.WatchAsync(id, default, _playlistId)
                    : await InnerTube.Instance.WatchCachedAsync(id, _playlistId);
                Cards.WithProgress(info.Related);
                App.Post(() =>
                {
                    if (gen != _infoGen) return;
                    _info = info;
                    _note = null;
                    var blocked = AppSettings.Current.BlockedChannels;
                    _related = [];
                    _relIds.Clear();
                    foreach (var rv in info.Related.Items)
                    {
                        if (!_relIds.Add(rv.VideoId)) continue;
                        if (blocked.Length > 0 && rv.ChannelId != null && Array.IndexOf(blocked, rv.ChannelId) >= 0) continue;
                        _related.Add(rv);
                    }
                    _relCont = info.Related.Continuation;
                    _relLoadingMore = false;
                    _relPrewarmed = 0;
                    _list = info.Playlist;
                    _listJump = _list != null;
                    _relTextW = 0;
                    _likes = string.IsNullOrEmpty(info.Likes) ? "—" : info.Likes;
                    _rating = info.LikeStatus is "LIKE" or "DISLIKE" ? info.LikeStatus : "";
                    _createCommentParams ??= info.CreateCommentParams;
                    _infoLoading = false;
                    if (string.IsNullOrEmpty(Video.Title))
                        Video = new VideoItem { VideoId = id, Title = info.Title, Channel = info.Channel, ChannelId = info.ChannelId };
                    if (PlaybackTracker.Instance.Current?.VideoId == id) PlaybackTracker.Instance.UpdateMeta(info.Title, info.Channel);
                    Shell.UpdateTabTitle("video", id, info.Title, info.Channel);
                    Shell.PushPlayerState();
                    for (int i = Math.Min(4, _related.Count - 1); i >= 0; i--) QuickStream.Prefetch(_related[i].VideoId);
                });
                if (AppSettings.Current.ReturnDislikes && await Extras.DislikesAsync(id) is { } votes)
                    App.Post(() =>
                    {
                        if (gen != _infoGen) return;
                        _dislikes = Extras.Compact(votes.Dislikes);
                        if (_likes == "—") _likes = Extras.Compact(votes.Likes);
                        App.Invalidate();
                    });
            }
            catch (Exception e)
            {
                Paths.Log("watch info: " + e);
                App.Post(() => { if (gen != _infoGen) return; _infoLoading = false; _note = L.T("Описание и похожие не загрузились: ", "Failed to load description & related: ") + e.Message; App.Invalidate(); });
            }
        });
    }

    /// <summary>A click on a time code, a link or a hashtag in the description or a comment.</summary>
    private void OnLink(TextLink l)
    {
        var mine = PlaybackTracker.Instance.Current?.VideoId == Video.VideoId;
        switch (l.Kind)
        {
            case 't':
                if (!mine) Shell.Play(Video, _source);
                MpvPlayer.Instance.SeekAbsolute(l.Seconds);
                MpvPlayer.Instance.SetPause(false);
                break;
            case '#': Shell.OpenSearch(l.Value); break;
            default:
                var vid = Shell.ExtractYouTubeId(l.Value);
                if (vid == null) { W.Open(l.Value); break; }
                var at = System.Text.RegularExpressions.Regex.Match(l.Value, @"[?&]t=(\d+)");
                if (vid == Video.VideoId) { if (at.Success && mine) MpvPlayer.Instance.SeekAbsolute(int.Parse(at.Groups[1].Value)); }
                else Shell.OpenVideo(new VideoItem { VideoId = vid }, "link");
                break;
        }
    }

    public void OpenShare()
    {
        var at = PlaybackTracker.Instance.Current?.VideoId == Video.VideoId ? MpvPlayer.Instance.TimePos : 0;
        SharePanel.Show(Video.VideoId, at);
    }

    // ---- comments: nothing is asked for until the section scrolls near; one page (20) at a time + comment composer ----
    private sealed class CRow
    {
        public int RowId;
        public required Comment C;
        public bool Open;                 // long text expanded
        public float W, H; public bool SizedOpen; // measured height for this width and state
        public readonly List<CRow> Replies = [];
        public bool Shown, Loading, RepliesLoaded;
        public string? More;              // token for the next replies
    }

    private long _dlCheckedAt;
    private bool _isDownloaded;
    private readonly List<CRow> _comments = [];
    private string? _commentTok, _commentCount, _sortTitle, _createCommentParams;
    private bool _commentsStarted, _commentsLoading, _commentsOff, _postingComment;
    private float _commentsH = 120;
    private int _commentGen, _nextRowId;
    private Edit? _commentEdit;

    private void SubmitComment()
    {
        if (_postingComment || _commentEdit == null) return;
        var text = _commentEdit.Text.Trim();
        if (text.Length == 0) return;
        if (!CookieStore.IsLoggedIn) { Shell.Toast(L.T("Войдите в аккаунт YouTube, чтобы оставлять комментарии", "Sign in to YouTube to post comments"), Th.Warn); return; }
        var createParams = _createCommentParams ?? _info?.CreateCommentParams;
        if (string.IsNullOrEmpty(createParams))
        {
            Shell.Toast(L.T("Токен отправки ещё загружается — подождите секунду и попробуйте снова", "Comment token still loading — wait a second and try again"), Th.Warn);
            if (!_commentsStarted) LoadComments();
            return;
        }
        _postingComment = true;
        var vid = Video.VideoId;
        var gen = _commentGen;
        _ = Task.Run(async () =>
        {
            var (ok, err) = await InnerTube.Instance.CreateCommentAsync(createParams, text, vid);
            App.Post(() =>
            {
                _postingComment = false;
                if (!ok) { Shell.Toast(L.T("Не удалось отправить комментарий: ", "Failed to post comment: ") + (err ?? L.T("ошибка YouTube", "YouTube error")), Th.Warn, 5000); App.Invalidate(); return; }
                if (_commentEdit != null) _commentEdit.Text = "";
                Sfx.Like();
                if (gen == _commentGen)
                {
                    var acc = AccountMenu.Current;
                    _comments.Insert(0, new CRow
                    {
                        RowId = ++_nextRowId,
                        C = new Comment
                        {
                            Author = acc?.Handle is { Length: > 0 } h ? h : (acc?.Name is { Length: > 0 } nm ? nm : L.T("Вы", "You")),
                            Avatar = acc?.Photo,
                            Text = text,
                            When = L.T("только что", "just now"),
                            Likes = "0",
                        },
                    });
                }
                Shell.Toast(L.T("Комментарий опубликован", "Comment posted"), Th.Good, 2400);
                App.Invalidate();
            });
        });
    }

    private void LoadComments(string? fromToken = null)
    {
        var tok = fromToken ?? (_commentsStarted ? _commentTok : _info?.CommentsToken);
        if (tok == null || _commentsLoading) return;
        _commentsStarted = true;
        _commentsLoading = true;
        var gen = _commentGen;
        _ = Task.Run(async () =>
        {
            try
            {
                var page = await InnerTube.Instance.CommentsAsync(tok);
                App.Post(() =>
                {
                    if (gen != _commentGen) return;
                    foreach (var c in page.Items) _comments.Add(new CRow { RowId = ++_nextRowId, C = c });
                    _commentTok = page.Items.Count == 0 ? null : page.Continuation;
                    _commentCount ??= page.Count;
                    if (!string.IsNullOrEmpty(page.CreateCommentParams)) _createCommentParams = page.CreateCommentParams;
                    _commentsLoading = false;
                    if (AppSettings.Current.SoundCloudWave && !AppSettings.Current.AudioOnlyMode)
                        Shell.PushPlayerState();
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("comments: " + e.Message);
                App.Post(() => { if (gen != _commentGen) return; _commentsLoading = false; _commentTok = null; _commentsOff = _comments.Count == 0; App.Invalidate(); });
            }
        });
    }

    /// <summary>Another order ("newest first"): the list starts over from that order's token.</summary>
    private void SortComments(string title, string token)
    {
        _commentGen++;
        _comments.Clear();
        _commentTok = null; _commentsLoading = false; _commentsOff = false;
        _sortTitle = title;
        LoadComments(token);
    }

    private void LoadReplies(CRow row)
    {
        var tok = !row.RepliesLoaded ? row.C.RepliesToken : row.More;
        if (tok == null || row.Loading) return;
        row.Loading = true;
        var gen = _commentGen;
        _ = Task.Run(async () =>
        {
            CommentPage? page = null;
            try { page = await InnerTube.Instance.CommentsAsync(tok); }
            catch (Exception e) { Paths.Log("replies: " + e.Message); }
            App.Post(() =>
            {
                if (gen != _commentGen) return;
                row.Loading = false;
                row.RepliesLoaded = true;
                if (page != null) { foreach (var c in page.Items) row.Replies.Add(new CRow { RowId = ++_nextRowId, C = c }); row.More = page.Items.Count == 0 ? null : page.Continuation; }
                else row.More = null;
                App.Invalidate();
            });
        });
    }

    /// <summary>The comment list under the description + composer box to post comments on videos.</summary>
    private float DrawComments(float x0, float y, float w, R view)
    {
        _commentsSectionY = y + UI.ScrollTarget(_infoScroll) - view.Y;
        if (_info == null) { _commentEdit?.Hide(); return y; }
        var count = _commentCount ?? _info.CommentsCount;
        if (_info.CommentsToken == null)
        {
            _commentEdit?.Hide();
            Gfx.Draw(L.T("Комментарии отключены", "Comments are turned off"), x0, y, 14, Th.Sub);
            return y + 40;
        }
        var head = count is { Length: > 0 } ? (count.Any(char.IsLetter) ? count : L.T("Комментарии: ", "Comments: ") + count) : L.T("Комментарии", "Comments");
        var hr = Gfx.Draw(head, x0, y, 18, Th.Text, 700);
        if (_info.CommentSorts.Count > 1)
        {
            var cur = _sortTitle ?? _info.CommentSorts.FirstOrDefault(s => s.Selected).Title ?? _info.CommentSorts[0].Title;
            if (UI.Pill(UI.Id("csort", _infoScroll), x0 + hr.W + 18, y - 5, 32, "\uE8CB", cur, out _, Th.A(0.08f)))
                Menus.Popup(_info.CommentSorts.Select(s => new MenuItem(s.Title, () => SortComments(s.Title, s.Token), null, s.Title == cur)).ToList());
        }
        y += 40;

        // Comment composer (logged-in users can write and post comments directly!)
        if (CookieStore.IsLoggedIn)
        {
            var composerR = new R(x0, y, w, 40);
            if (composerR.Bottom >= view.Y && composerR.Y <= view.Bottom)
            {
                if (_commentEdit == null)
                {
                    _commentEdit = new Edit(L.T("Введите комментарий…", "Add a comment…")) { Bg = () => Th.Field };
                    _commentEdit.Enter = SubmitComment;
                }
                else _commentEdit.SetPlaceholder(L.T("Введите комментарий…", "Add a comment…"));
                var acc = AccountMenu.Current;
                Cards.Avatar(x0, y + 2, 36, acc?.Photo, acc?.Name is { Length: > 0 } nm ? nm : "?");
                var sendLbl = _postingComment ? L.T("Отправка…", "Posting…") : L.T("Отправить", "Comment");
                var sendW = UI.PillWidth(null, sendLbl);
                var boxR = new R(x0 + 48, y + 1, MathF.Max(120, w - 48 - sendW - 10), 38);
                Gfx.Fill(boxR, Th.Field, 10);
                Gfx.Stroke(boxR, _commentEdit.Focused ? Gfx.Fade(Th.Brand, 0.8f) : Th.Line, 10, _commentEdit.Focused ? 1.2f : 1f);
                _commentEdit.Place(boxR, 14, 14);
                bool canSend = !_postingComment && !string.IsNullOrWhiteSpace(_commentEdit.Text);
                if (UI.Pill(UI.Id("cpost", _infoScroll), boxR.Right + 10, y + 2, 36, null, sendLbl, out _, accent: canSend) && canSend)
                    App.Post(SubmitComment);
            }
            else _commentEdit?.Hide();
            y += 54;
        }
        else
        {
            _commentEdit?.Hide();
            var hintR = new R(x0, y, w, 38);
            if (hintR.Bottom >= view.Y && hintR.Y <= view.Bottom)
            {
                Gfx.Fill(hintR, Th.A(0.055f), 10);
                Gfx.Draw(L.T("Войдите в аккаунт YouTube, чтобы оставлять комментарии", "Sign in to YouTube to post comments"), x0 + 14, y + 10, 13, Th.Sub, 400, w - 120);
                var loginTxt = L.T("Войти", "Sign in");
                var lw = UI.PillWidth(Th.Account, loginTxt);
                if (UI.Pill(UI.Id("clogin", _infoScroll), hintR.Right - lw - 8, y + 4, 30, Th.Account, loginTxt, out _, accent: true))
                    App.Post(LoginModal.Show);
            }
            y += 48;
        }

        if (!_commentsStarted && y < view.Bottom + 600) LoadComments();
        if (_commentsOff) { Gfx.Draw(L.T("Не удалось загрузить комментарии.", "Could not load comments."), x0, y, 13.5f, Th.Sub); return y + 34; }

        foreach (var row in _comments)
        {
            y = DrawRow(row, x0, y, w, view, 40);
            if (!row.Shown) continue;
            foreach (var rep in row.Replies) y = DrawRow(rep, x0 + 56, y, w - 56, view, 28);
            if (row.Loading) { UI.Spinner(x0 + 56 + 12, y + 12, 7, Th.Sub); y += 34; }
            else if (row.More != null)
            {
                if (y > view.Y - 40 && y < view.Bottom + 40 && UI.Pill(UI.Id(_infoScroll, 60000 + row.RowId), x0 + 56, y, 30, null, L.T("Ещё ответы", "More replies"), out _, Th.A(0.08f))) LoadReplies(row);
                y += 40;
            }
        }
        if (_commentsLoading) { UI.Spinner(x0 + w / 2, y + 22, 9, Th.Sub); y += 50; }
        else if (_commentTok != null)
        {
            if (y < view.Bottom + 400) LoadComments();
            y += 50;
        }
        return y + 20;
    }

    private float DrawRow(CRow row, float x0, float y, float w, R view, float avatar)
    {
        int i = row.RowId;
        var c = row.C;
        var tw = w - avatar - 16;
        if (row.H > 0 && row.W == tw && row.SizedOpen == row.Open && (y + row.H < view.Y - 20 || y > view.Bottom + 20))
            return y + row.H;
        var text = Gfx.Text(c.Text, 14, 400, tw, row.Open ? 0 : 5);
        var full = row.Open ? text : Gfx.Text(c.Text, 14, 400, tw, 0);
        var longText = full.H > Gfx.LineHeight(14) * 5 + 1;
        var hasReplies = c.RepliesToken != null;
        var h = 22 + text.H + (longText ? 22 : 0) + 34 + (hasReplies ? 6 : 0);
        if (text.Layout != null && full.Layout != null) { row.W = tw; row.H = h; row.SizedOpen = row.Open; }
        if (y + h <= view.Y - 20 || y >= view.Bottom + 20) return y + h;
        PageBeatMotion.Current?.At(x0 + w / 2, y + 30);

        Cards.Avatar(x0, y, avatar, c.Avatar, c.Author.TrimStart('@'));
        if (UI.RightClick(new R(x0, y, w, h - 8)))
            Sel.Popup([new(L.T("Копировать комментарий", "Copy comment"), () => Cards.CopyText(c.Text, L.T("Комментарий скопирован", "Comment copied")), icon: Th.Copy),
                       new(L.T("Копировать с автором", "Copy with author"), () => Cards.CopyText(c.Author + ": " + c.Text, L.T("Комментарий скопирован", "Comment copied")), icon: Th.Comment),
                       new(L.T("Копировать ник", "Copy handle"), () => Cards.CopyText(c.Author, L.T("Ник скопирован", "Handle copied")), icon: Th.Account)]);
        float tx = x0 + avatar + 16;
        var au = Gfx.Text(c.Author, 13, 600);
        if (c.ByOwner) { var b = new R(tx - 6, y - 2, au.W + 12, au.H + 4); Gfx.Fill(b, Th.A(0.16f), b.H / 2); }
        Gfx.Draw(au, tx, y, Th.Text);
        var wx = tx + au.W + 10;
        if (c.Pinned) { var pinLbl = L.T("закреплён", "pinned"); Gfx.Draw(pinLbl, wx, y + 1, 12, Th.Sub); wx += Gfx.Text(pinLbl, 12).W + 8; }
        Gfx.Draw(c.When, wx, y + 1, 12, Th.Dim);
        var ty = y + 22;
        Sel.Text(UI.Id(_infoScroll, 20000 + i), c.Text, tx, ty, 14, Th.Text, 400, tw, row.Open ? 0 : 5, links: true, onLink: OnLink);
        ty += text.H;
        if (longText)
        {
            var more = Gfx.Text(row.Open ? L.T("Свернуть", "Show less") : L.T("Читать дальше", "Read more"), 13, 600);
            var mr = new R(tx, ty + 4, more.W, more.H);
            Gfx.Draw(more, mr.X, mr.Y, UI.Hover(mr) ? Th.Text : Th.Sub);
            if (UI.Click(UI.Id(_infoScroll, 40000 + i), mr)) row.Open = !row.Open;
            ty += 22;
        }
        ty += 6;
        Gfx.Icon(Th.Like, new R(tx - 2, ty, 20, 22), 13, Th.Sub);
        var likes = Gfx.Draw(c.Likes, tx + 22, ty + 3, 12, Th.Sub);
        if (hasReplies)
        {
            var repTxt = c.Replies is { Length: > 0 } r2 && r2 != "0" ? L.T("Ответы: ", "Replies: ") + r2 : L.T("Ответы", "Replies");
            var pr = new R(tx + 22 + likes.W + 14, ty - 4, UI.PillWidth(null, repTxt) + 22, 30);
            var pid = UI.Id(_infoScroll, 50000 + i);
            UI.Glow(pid, pr, 15, UI.Hover(pr));
            Gfx.Icon(row.Shown ? "\uE70E" : "\uE70D", new R(pr.X + 8, pr.Y, 18, 30), 10, Sel.LinkColor);
            Gfx.Draw(repTxt, pr.X + 30, pr.Y + 6, 13, Sel.LinkColor, 600);
            if (UI.Click(pid, pr)) { row.Shown = !row.Shown; if (row.Shown && !row.RepliesLoaded) LoadReplies(row); }
        }
        return y + h;
    }

    private float _actionExtraH;

    private static void DrawWaveText(string text, float x, float y, float size, uint color, int weight, float maxW, int maxLines = 2, bool shadow = false, uint accentColor = 0)
        => Gfx.DrawDancingText(text, x, y, size, color, weight, maxW, maxLines, shadow: shadow, amp: 1.12f, accentMixColor: accentColor);

    private void DrawLikeDislikePill(float x, float y, float h, out float width)
    {
        var lr = Gfx.Text(_likes, 13.5f, 600);
        var dr = Gfx.Text(_dislikes, 13.5f, 600);
        float leftW = 14 + 18 + 7 + lr.W + 14;
        float rightW = 12 + 18 + 7 + dr.W + 14;
        width = leftW + 1 + rightW;

        var pill = new R(x, y, width, h);
        var likeR = new R(x, y, leftW, h);
        var disR = new R(x + leftW + 1, y, rightW, h);
        int likeId = UI.Id("like", _infoScroll), disId = UI.Id("dislike", _infoScroll);
        bool overL = UI.Hover(likeR), overD = UI.Hover(disR);
        bool isLike = _rating == "LIKE", isDis = _rating == "DISLIKE";

        var tfPill = MusicBounce.Push(pill, 3, amp: 1.24f, rotAmp: 0.90f);
        float rad = h / 2;
        Gfx.Fill(pill, Th.Card, rad);

        // Clip each half strictly to its side while filling the full rounded pill so outer corners stay perfectly round.
        if (isLike || overL)
        {
            Gfx.PushClip(likeR);
            Gfx.Fill(pill, isLike ? Gfx.Fade(Th.Brand, overL ? 0.22f : 0.16f) : Th.A(0.08f), rad);
            Gfx.PopClip();
        }
        if (isDis || overD)
        {
            Gfx.PushClip(disR);
            Gfx.Fill(pill, isDis ? Gfx.Fade(Th.Brand, overD ? 0.22f : 0.16f) : Th.A(0.08f), rad);
            Gfx.PopClip();
        }
        if (isLike || isDis)
            Gfx.Stroke(pill, Gfx.Fade(Th.Brand, 0.35f), rad, 1f);

        uint likeC = isLike || overL ? Th.Brand : Th.Text;
        uint disC = isDis || overD ? Th.Brand : Th.Text;
        var lIconR = new R(likeR.X + 14, y, 18, h);
        var tfLi = MusicBounce.Push(lIconR, 0, amp: 1.18f, rotAmp: 1.15f);
        Gfx.Icon(Th.Like, lIconR, 15.5f, likeC);
        MusicBounce.Pop(tfLi);
        Gfx.DrawDancingText(_likes, likeR.X + 39, y + (h - lr.H) / 2, 13.5f, likeC, 600, 0, 1, amp: 0.92f, accentMixColor: Th.Brand);
        Gfx.Fill(new R(x + leftW, y + 9, 1, h - 18), Th.Line);
        var dIconR = new R(disR.X + 12, y, 18, h);
        var tfDi = MusicBounce.Push(dIconR, 6, amp: 1.05f, rotAmp: 1.0f);
        Gfx.Icon(Th.Dislike, dIconR, 15.5f, disC);
        MusicBounce.Pop(tfDi);
        Gfx.DrawDancingText(_dislikes, disR.X + 37, y + (h - dr.H) / 2, 13.5f, disC, 600, 0, 1, amp: 0.85f);
        MusicBounce.Pop(tfPill);

        if (UI.Click(likeId, likeR, playSfx: false)) Rate(true);
        if (UI.Click(disId, disR, playSfx: false)) Rate(false);
    }

    public void Draw(R r)
    {
        var savedModal = UI.Modal;
        if (SharePanel.Open) UI.Modal = new R(-1e6f, -1e6f, 0, 0);
        // Clip the entire WatchPage strictly to its tab area so shake & effects NEVER leak into top bar/sidebar/tabs
        Gfx.PushClip(r);
        const float padX = 20, topPad = 10;
        float x0 = Gfx.Snap(r.X + padX);
        float mainW = Gfx.Snap(r.W - padX * 2);
        float videoW = mainW;
        float videoH = Gfx.Snap(MathF.Max(360f, r.H * 0.90f));
        float videoX = x0;
        var id = Video.VideoId;

        void DrawVideoBox(R video, R? clip)
        {
            var sOpt = AppSettings.Current;
            bool audioStage = sOpt.AudioOnlyMode && !Pip.Active && PlaybackTracker.Instance.Current?.VideoId == id;
            if (!audioStage)
            {
                Ambient.Draw(video, id, isShort: false);
                if (Shell.VideoBeatGlow > 0.02f && !Pip.Active && MpvPlayer.Instance.CurrentVideoId == id)
                    Gfx.Stroke(video.Inset(-1.5f), Gfx.Fade(Th.Brand, Shell.VideoBeatGlow * 0.52f), Gfx.Corner(13.5f), 2.0f);
            }
            Gfx.Fill(video, 0xFF0A0A0E, 12);
            if (!audioStage) Shell.WantVideo(video, id, clip, radius: 12);
            if (Pip.Active && PlaybackTracker.Instance.Current?.VideoId == id)
            {
                Gfx.Fill(video, 0xFF0A0A0C, 12);
                var img = Images.Get(Video.Thumb720, 640, 360);
                var pw = MathF.Min(video.W, video.H * 16 / 9);
                var pr = new R(video.X + (video.W - pw) / 2, video.Y + (video.H - pw * 9 / 16) / 2, pw, pw * 9 / 16);
                if (img != null) Gfx.ImageRounded(img.Bmp!, pr, Gfx.Corner(12), 0.25f * Images.Alpha(img));
                var msg = Gfx.Text(L.T("Видео в отдельном окне", "Playing in Picture-in-Picture"), 18, 600);
                Gfx.Draw(msg, video.X + (video.W - msg.W) / 2, video.Y + video.H / 2 - 40, 0xFFFFFFFF);
                var retLbl = L.T("Вернуть сюда", "Return here");
                var bw = UI.PillWidth(Th.Mini, retLbl);
                if (UI.Pill(UI.Id("pipback", _infoScroll), video.X + (video.W - bw) / 2, video.Y + video.H / 2, 38, Th.Mini, retLbl, out _, accent: true)) App.Post(Pip.Exit);
            }
            else if (Pip.Active)
            {
                Gfx.Fill(video, 0xFF0A0A0C, 12);
                var img = Images.Get(Video.Thumb720, 640, 360);
                var pw = MathF.Min(video.W, video.H * 16 / 9);
                var pr = new R(video.X + (video.W - pw) / 2, video.Y + (video.H - pw * 9 / 16) / 2, pw, pw * 9 / 16);
                if (img != null) Gfx.ImageRounded(img.Bmp!, pr, Gfx.Corner(12), 0.38f * Images.Alpha(img));
                var msg = Gfx.Text(L.T("В окне поверх всех играет другое видео", "Another video is playing in Picture-in-Picture"), 17, 600);
                Gfx.Draw(msg, video.X + (video.W - msg.W) / 2, video.Y + video.H / 2 - 48, 0xFFFFFFFF);
                var b1Lbl = L.T("Смотреть здесь", "Watch here");
                var b2Lbl = L.T("Заменить в мини-окне", "Replace in PiP");
                var b1w = UI.PillWidth(Th.Play, b1Lbl);
                var b2w = UI.PillWidth(Th.Mini, b2Lbl);
                float totalW = b1w + 10 + b2w;
                float bx0 = video.X + (video.W - totalW) / 2;
                float by0 = video.Y + video.H / 2 - 6;
                if (UI.Pill(UI.Id("piphere", _infoScroll), bx0, by0, 38, Th.Play, b1Lbl, out _, accent: true))
                    App.Post(() => { Pip.Exit(); Shell.Play(Video, _source, startPaused: false, forceReplacePip: true); });
                if (UI.Pill(UI.Id("pipswap", _infoScroll), bx0 + b1w + 10, by0, 38, Th.Mini, b2Lbl, out _, Gfx.Fade(0xFF1E1E26, 0.90f), 0xFFFFFFFF))
                    App.Post(() => Shell.Play(Video, _source, startPaused: false, forceReplacePip: true));
            }
            else if (audioStage)
            {
                DrawMusicStage(video);
            }
            else if (!Shell.VideoShown(id))
            {
                var pw = MathF.Min(video.W, video.H * 16 / 9);
                var pr = new R(video.X + (video.W - pw) / 2, video.Y + (video.H - pw * 9 / 16) / 2, pw, pw * 9 / 16);
                var img = Images.Get(Video.Thumb720, 640, 360);
                if (img != null) Gfx.ImageRounded(img.Bmp!, pr, Gfx.Corner(12), 0.55f * Images.Alpha(img));
                UI.Spinner(video.X + video.W / 2, video.Y + video.H / 2, 16, 0xFFFFFFFF);
            }
        }

        void DrawMusicStage(R video)
        {
            var mpv = MpvPlayer.Instance;
            var sOpt = AppSettings.Current;
            float stageRad = Gfx.Corner(14);
            bool fullArtMode = string.Equals(sOpt.MusicStageBg, "full", StringComparison.OrdinalIgnoreCase);

            Shell.MusicStageRect = video.Intersect(Gfx.Clip);
            // Clip ALL full-bleed stage backdrops (art, vertical fades, glows, RGB fluid/fire, breakdown dim) strictly inside rounded corners!
            bool stageRoundClip = Gfx.PushRoundedClip(video, stageRad);
            Gfx.Fill(video, 0xFF07070B, stageRad);

            bool isThisStagePlaying = Shell.CurrentTabOwnsMainPlayer && mpv.CurrentVideoId == id && mpv.HasMedia && !mpv.Paused && !mpv.Muted && mpv.Volume > 0;
            if (isThisStagePlaying) AudioAnalyzer.RequestFrame();
            float bass = isThisStagePlaying ? AudioAnalyzer.BassEnergy : 0f;
            float kick = isThisStagePlaying ? MusicBounce.Kick : 0f;
            float br = ((Th.Brand >> 16) & 255) / 255f;
            float bg = ((Th.Brand >> 8) & 255) / 255f;
            float bb = (Th.Brand & 255) / 255f;

            // 1. Full-Bleed Living Album-Art Backdrop (auto-cropped cover, GPU blur+saturation OR full crisp artwork!)
            var art = MusicArt.Get(id);
            float zoom = 1.03f + (sOpt.MusicBassShake ? bass * 0.045f + kick * 0.025f : 0f);
            float zW = video.W * zoom, zH = video.H * zoom;
            var bgArtR = new R(video.X - (zW - video.W) * 0.5f, video.Y - (zH - video.H) * 0.5f, zW, zH);

            if (art != null)
            {
                float fadeA = MusicArt.Alpha(art);
                if (fullArtMode && art.SharpBmp != null)
                {
                    Gfx.ImageRoundedCover(art.SharpBmp, bgArtR, stageRad, 0.96f * fadeA);
                    // Cinema legibility gradient scrim (top subtle vignette + bottom dark dock fade)
                    Gfx.FillVerticalFade(new R(video.X, video.Y, video.W, 96), 0x040407, 0.62f, 0.24f, 0.0f);
                    Gfx.FillVerticalFade(new R(video.X, video.Bottom - 295, video.W, 295), 0x040407, 0.0f, 0.52f, 0.92f);
                }
                else if (art.BlurBmp != null)
                {
                    Gfx.ImageRoundedCover(art.BlurBmp, bgArtR, stageRad, (Th.Light ? 0.68f : 0.86f) * fadeA);
                    Gfx.FillVerticalFade(video, 0x050509, 0.28f, 0.52f, 0.88f);
                }
                else if (art.SharpBmp != null)
                {
                    Gfx.ImageRoundedCover(art.SharpBmp, bgArtR, stageRad, 0.36f * fadeA);
                    Gfx.FillVerticalFade(video, 0x050509, 0.42f, 0.66f, 0.92f);
                }
            }
            else
            {
                Gfx.FillFrosted(video, Th.Panel, stageRad, opacity: 0.92f, border: false);
            }

            // Dominant-color studio blooms reacting to bass (dedicated slots 9 & 10 -> zero collision!)
            uint dom1 = art?.Dom1 ?? Th.Brand;
            uint dom2 = art?.Dom2 ?? (Th.Brand2 != 0 ? Th.Brand2 : Th.Brand);
            uint visCol1 = Gfx.Mix(dom1, Th.Brand, 0.35f);
            uint visCol2 = Gfx.Mix(dom2, 0xFF73D8EB, 0.32f);
            float d1r = ((dom1 >> 16) & 255) / 255f, d1g = ((dom1 >> 8) & 255) / 255f, d1b = (dom1 & 255) / 255f;
            float d2r = ((dom2 >> 16) & 255) / 255f, d2g = ((dom2 >> 8) & 255) / 255f, d2b = (dom2 & 255) / 255f;
            if (!fullArtMode)
            {
                Gfx.RadialGlow(video.X + video.W * 0.24f, video.Y + video.H * 0.36f,
                    video.W * (0.42f + bass * 0.14f), video.H * (0.50f + bass * 0.16f),
                    d1r, d1g, d1b, 0.26f + bass * 0.22f, slot: 9);
                Gfx.RadialGlow(video.X + video.W * 0.78f, video.Y + video.H * 0.48f,
                    video.W * (0.38f + kick * 0.10f), video.H * (0.44f + kick * 0.12f),
                    d2r, d2g, d2b, 0.18f + kick * 0.18f, slot: 10);
            }
            else
            {
                Gfx.RadialGlow(video.X + video.W * 0.5f, video.Bottom - 110,
                    video.W * 0.52f, 170f + bass * 50f, d1r, d1g, d1b, 0.18f + bass * 0.16f, slot: 9);
            }

            // Cover-matched Gaussian-blurred 2D fluid side fire + quiet-breakdown starlight fireflies
            if (isThisStagePlaying)
                MusicStageEffects.DrawBackdropEffects(video, dom1, dom2);

            // Atmospheric dimming during quiet breakdowns & slow rising flash when sound returns
            float qDim = isThisStagePlaying ? AudioAnalyzer.QuietDim : 0f;
            float qFlash = isThisStagePlaying ? AudioAnalyzer.QuietFlash : 0f;
            if (qDim > 0.01f)
                Gfx.Fill(video, Gfx.Fade(0xFF030308, qDim * 0.46f), stageRad);
            if (qFlash > 0.01f)
                Gfx.Fill(video, Gfx.Fade(Gfx.Mix(dom1, 0xFFFFFFFF, 0.62f), qFlash * 0.22f), stageRad);
            Gfx.PopRoundedClip(stageRoundClip);

            // Foreground stage clip (keeps DC.Target switches in DrawDancingText 100% free of active D2D layers!)
            Gfx.PushClip(video);

            // 2. Clean Non-Overlapping Vertical Layout: Hero -> 16px -> Visualizer -> 14px -> SoundCloud (84px) -> 10px -> Dock (42px)
            float dockH = 42f;
            float dockY = video.Bottom - dockH - 12f;
            float scHeight = 84f;
            float scY = dockY - scHeight - 10f;
            float spectrumH = Math.Clamp(video.H * (fullArtMode ? 0.17f : 0.18f), 68f, 128f);
            var vizR = new R(video.X + 20f, scY - spectrumH - 14f, video.W - 40f, spectrumH);
            if (isThisStagePlaying && sOpt.VisualizerStyle != "off")
            {
                var tfViz = MusicBounce.Push(vizR, 3, amp: 0.85f, rotAmp: 0.40f, textSafe: true);
                AudioAnalyzer.DrawStageVisualizer(vizR, sOpt.VisualizerStyle,
                    opacity: fullArtMode ? 0.72f : 0.96f,
                    gain: 1.15f + kick * 0.35f + (sOpt.MusicImpactEffects ? 0.22f : 0f),
                    primary: visCol1, secondary: visCol2);
                MusicBounce.Pop(tfViz);
            }

            // 3. Top Controls Row (dark frosted pills laid right-to-left, zero overlap!)
            float topRx = video.Right - 16;
            float topY = video.Y + 12;
            uint darkPill = Gfx.Fade(0xFF101018, 0.72f);

            var showVidLbl = L.T("Показать видео", "Show video");
            var showVidW = UI.PillWidth(Th.Play, showVidLbl);
            topRx -= showVidW;
            var tfVid = MusicBounce.Push(new R(topRx, topY, showVidW, 30), 26, amp: 0.85f, textSafe: true);
            if (UI.Pill(UI.Id("ms:vid", _infoScroll), topRx, topY, 30, Th.Play, showVidLbl, out _, darkPill, 0xFFFFFFFF))
                App.Post(Shell.ToggleAudioOnlyMode);
            MusicBounce.Pop(tfVid);
            topRx -= 7;

            var eqW = UI.PillWidth(Th.Headphones, "EQ / FX");
            topRx -= eqW;
            var tfEq = MusicBounce.Push(new R(topRx, topY, eqW, 30), 21, amp: 0.90f, textSafe: true);
            if (UI.Pill(UI.Id("ms:eq", _infoScroll), topRx, topY, 30, Th.Headphones, "EQ / FX", out _,
                sOpt.AudioFxEnabled ? Th.Brand : darkPill, sOpt.AudioFxEnabled ? Th.OnBrand : 0xFFFFFFFF))
                App.Post(AudioStudioPanel.Toggle);
            MusicBounce.Pop(tfEq);
            topRx -= 7;

            bool djOn = AudioStudioPanel.IsDjFxActive(sOpt);
            var djLbl = "DJ";
            var djW = UI.PillWidth("\uE958", djLbl);
            topRx -= djW;
            var tfDj = MusicBounce.Push(new R(topRx, topY, djW, 30), 16, amp: 0.95f, textSafe: true);
            if (UI.Pill(UI.Id("ms:dj", _infoScroll), topRx, topY, 30, "\uE958", djLbl, out _,
                djOn ? Th.Brand : darkPill, djOn ? Th.OnBrand : 0xFFFFFFFF))
                App.Post(AudioStudioPanel.ShowDj);
            MusicBounce.Pop(tfDj);
            topRx -= 7;

            // 4-State Shake Mode Cycler (Выкл / Мин / Средн / Макс)
            string shakeLvl = !sOpt.MusicBassShake ? "off" : (sOpt.MusicShakeLevel ?? "med");
            var shLbl = shakeLvl switch
            {
                "off" => L.T("Тряска: Выкл", "Shake: Off"),
                "low" => L.T("Тряска: Мин", "Shake: Min"),
                "high" => L.T("Тряска: Макс", "Shake: Max"),
                _ => L.T("Тряска: Средн", "Shake: Med"),
            };
            var shW = UI.PillWidth(null, shLbl);
            topRx -= shW;
            var tfSh = MusicBounce.Push(new R(topRx, topY, shW, 30), 11, amp: 0.95f, textSafe: true);
            if (UI.Pill(UI.Id("ms:shake", _infoScroll), topRx, topY, 30, null, shLbl, out _,
                shakeLvl != "off" ? Th.Brand : darkPill, shakeLvl != "off" ? Th.OnBrand : 0xFFFFFFFF))
            {
                string next = shakeLvl switch { "off" => "low", "low" => "med", "med" => "high", _ => "off" };
                sOpt.MusicShakeLevel = next;
                sOpt.MusicBassShake = next != "off";
                sOpt.Save();
                App.Invalidate();
            }
            MusicBounce.Pop(tfSh);
            topRx -= 7;

            string fxMode = !sOpt.MusicImpactEffects ? "off" : (sOpt.MusicStageEffect ?? "fluid");
            var impLbl = fxMode switch
            {
                "fire" => L.T("Огонь", "Fire"),
                "off" => L.T("Эффект: Выкл", "FX: Off"),
                _ => L.T("Флюид", "Fluid"),
            };
            var impW = UI.PillWidth(null, impLbl);
            topRx -= impW;
            var tfImp = MusicBounce.Push(new R(topRx, topY, impW, 30), 7, amp: 0.90f, textSafe: true);
            if (UI.Pill(UI.Id("ms:impact", _infoScroll), topRx, topY, 30, null, impLbl, out _,
                fxMode != "off" ? Th.Brand : darkPill, fxMode != "off" ? Th.OnBrand : 0xFFFFFFFF))
            {
                string nextFx = fxMode switch { "fluid" => "fire", "fire" => "off", _ => "fluid" };
                sOpt.MusicStageEffect = nextFx;
                sOpt.MusicImpactEffects = nextFx != "off";
                sOpt.SaveAsync();
                App.Invalidate();
            }
            MusicBounce.Pop(tfImp);
            topRx -= 7;

            var visLbl = L.T("Визуал: ", "Visual: ") + AudioStudioPanel.CurrentVisualizerName(sOpt);
            var visW = UI.PillWidth(null, visLbl);
            topRx -= visW;
            var tfVis = MusicBounce.Push(new R(topRx, topY, visW, 30), 5, amp: 0.85f, textSafe: true);
            if (UI.Pill(UI.Id("ms:visstyle", _infoScroll), topRx, topY, 30, null, visLbl, out _,
                sOpt.VisualizerStyle != "off" ? Gfx.Fade(Th.Brand, 0.28f) : darkPill, 0xFFFFFFFF))
                AudioStudioPanel.CycleVisualizerStyle(sOpt);
            MusicBounce.Pop(tfVis);
            topRx -= 7;

            bool centerLayout = string.Equals(sOpt.MusicStageLayout, "center", StringComparison.OrdinalIgnoreCase);
            if (!fullArtMode)
            {
                var layLbl = centerLayout ? L.T("Центр", "Center") : L.T("Слева", "Left");
                var layW = UI.PillWidth(null, layLbl);
                topRx -= layW;
                var tfLay = MusicBounce.Push(new R(topRx, topY, layW, 30), 4, amp: 0.85f, textSafe: true);
                if (UI.Pill(UI.Id("ms:layout", _infoScroll), topRx, topY, 30, null, layLbl, out _, darkPill, 0xFFFFFFFF))
                {
                    sOpt.MusicStageLayout = centerLayout ? "left" : "center";
                    sOpt.SaveAsync();
                    App.Invalidate();
                }
                MusicBounce.Pop(tfLay);
                topRx -= 7;
            }

            var bgLbl = fullArtMode ? L.T("Карточка", "Card") : L.T("На весь фон", "Full Art");
            var bgW = UI.PillWidth(null, bgLbl);
            topRx -= bgW;
            var tfBg = MusicBounce.Push(new R(topRx, topY, bgW, 30), 3, amp: 0.85f, textSafe: true);
            if (UI.Pill(UI.Id("ms:bgmode", _infoScroll), topRx, topY, 30, null, bgLbl, out _,
                fullArtMode ? Th.Brand : darkPill, fullArtMode ? Th.OnBrand : 0xFFFFFFFF))
            {
                sOpt.MusicStageBg = fullArtMode ? "hero" : "full";
                sOpt.Save();
                App.Invalidate();
            }
            MusicBounce.Pop(tfBg);

            float maxChipW = topRx - (video.X + 26);
            if (maxChipW > 90)
            {
                string liveAudioInfo = isThisStagePlaying ? mpv.AudioQualityBadge : "";
                string losslessHint = isThisStagePlaying && mpv.LosslessSearching ? L.T(" · Поиск FLAC…", " · Finding FLAC…")
                    : (isThisStagePlaying && mpv.HasLosslessTrack && !mpv.IsLosslessAudio ? L.T(" · FLAC готов", " · FLAC ready") : "");
                var chipTxt = sOpt.AudioFxEnabled
                    ? L.T($"Только звук · FX: {AudioDsp.DisplayPresetName(sOpt)}", $"Audio only · FX: {AudioDsp.DisplayPresetName(sOpt)}")
                    : (liveAudioInfo.Length > 0 && liveAudioInfo != "HD"
                        ? L.T($"Только звук · {liveAudioInfo}{losslessHint}", $"Audio only · {liveAudioInfo}{losslessHint}")
                        : L.T($"Только звук · Видео отключено{losslessHint}", $"Audio only · Video disabled{losslessHint}"));
                var chip = Gfx.Text(chipTxt, 11.5f, 600, maxChipW - 22);
                var cb = new R(video.X + 16, video.Y + 14, MathF.Min(maxChipW, chip.W + 22), 26);
                var tfCb = MusicBounce.Push(cb, 6, amp: 0.65f, rotAmp: 0.30f, textSafe: true);
                bool losslessChip = isThisStagePlaying && mpv.IsLosslessAudio;
                bool hiFiChip = isThisStagePlaying && mpv.IsHiFiAudio;
                bool overCb = isThisStagePlaying && UI.Hover(cb);
                uint cbBg = losslessChip ? Gfx.Fade(0xFF0A2A1E, overCb ? 0.94f : 0.86f)
                    : (hiFiChip ? Gfx.Fade(0xFF281D0A, overCb ? 0.92f : 0.84f) : darkPill);
                uint cbStroke = losslessChip ? Gfx.Fade(0xFF39E68B, 0.80f)
                    : (hiFiChip ? Gfx.Fade(0xFFFFD166, 0.65f) : Gfx.Fade(0xFFFFFFFF, overCb ? 0.34f : 0.14f));
                uint cbFg = losslessChip ? 0xFF7CFFB8 : (hiFiChip ? 0xFFFFE18A : 0xFFF2F2F7);
                Gfx.Fill(cb, cbBg, 13);
                Gfx.Stroke(cb, cbStroke, 13, losslessChip ? 1.3f : 1f);
                Gfx.Draw(chip, cb.X + 11, cb.Y + (cb.H - chip.H) / 2, cbFg);
                if (isThisStagePlaying && UI.Click(UI.Id("ms:flacchip", _infoScroll), cb))
                {
                    var res = PlaybackTracker.Instance.RequestOrToggleLossless();
                    if (res == "flac") Shell.Toast($"ULTRA FLAC · {mpv.LosslessSourceLabel ?? "Lossless"}", 0xFF39E68B, 1800);
                    else if (res == "youtube") Shell.Toast(L.T("A/B: YouTube аудио", "A/B: YouTube audio"), Th.Sub, 1600);
                    else if (res == "searching") Shell.Toast(L.T("Ищем ULTRA FLAC…", "Searching ULTRA FLAC…"), 0xFFFFD166, 1800);
                }
                MusicBounce.Pop(tfCb);
            }

            // 4. Middle Hero Section (Centered Cover Art + Title & Controls neatly below — ZERO overlap with visualizer!)
            float heroTop = video.Y + 52f;
            float heroBot = vizR.Y - 16f;
            float heroH = MathF.Max(140f, heroBot - heroTop);
            float pulse = 1f + bass * (sOpt.MusicBassShake && shakeLvl != "off" ? (sOpt.MusicImpactEffects ? 0.068f : 0.052f) : 0.022f);
            var chText = _info?.Channel ?? Video.Channel;

            if (!fullArtMode)
            {
                float aspect = Math.Clamp(art?.Aspect ?? (16f / 9f), 0.85f, 1.78f);
                bool bdLossless = isThisStagePlaying && mpv.IsLosslessAudio;
                bool bdHiRes = isThisStagePlaying && mpv.IsHiResLossless;
                bool bdHiFi = isThisStagePlaying && mpv.IsHiFiAudio;
                bool bdDownloading = isThisStagePlaying && mpv.LosslessDownloading;
                int dlPct = bdDownloading ? mpv.LosslessDownloadPercent : 0;
                string liveBadge = isThisStagePlaying
                    ? (bdDownloading ? $"{mpv.AudioCodec.ToUpperInvariant()} → FLAC {dlPct}%"
                        : (mpv.LosslessSearching && !bdLossless ? L.T("Поиск FLAC…", "Finding FLAC…") : mpv.TidalQualityLabel))
                    : "HD";
                var badgeTxt = sOpt.AudioFxEnabled
                    ? $"{AudioDsp.DisplayPresetName(sOpt)} · {liveBadge}"
                    : liveBadge;
                var bdRun = Gfx.Text(badgeTxt, bdLossless ? 11.5f : 10.5f, 600);
                float bdH = bdLossless ? 26f : 22f;
                float bdPadX = bdLossless ? 13f : 9f;
                float bdW = bdRun.W + bdPadX * 2f;

                void DrawHeroCover(R artR)
                {
                    var tfArt = MusicBounce.Push(artR, 1, amp: 1.42f, rotAmp: 1.20f, textSafe: false);
                    if (sOpt.MusicBassShake && shakeLvl != "off" && bass > 0.14f)
                    {
                        float ringExp = (bass - 0.14f) * 28f;
                        Gfx.Stroke(artR.Inset(-ringExp), Gfx.Fade(Gfx.Mix(dom1, Th.Brand, 0.5f), Math.Clamp((bass - 0.12f) * 0.70f, 0f, 0.60f)), Gfx.Corner(16) + ringExp * 0.4f, 2.0f);
                        if (bass > 0.30f)
                        {
                            float ring2 = (bass - 0.30f) * 46f;
                            Gfx.Stroke(artR.Inset(-ring2), Gfx.Fade(Th.Brand, Math.Clamp((bass - 0.28f) * 0.48f, 0f, 0.40f)), Gfx.Corner(16) + ring2 * 0.35f, 1.3f);
                        }
                    }
                    if (isThisStagePlaying) AudioAnalyzer.DrawCoverSpectrum(artR, 1f, visCol1, visCol2);

                    Gfx.Fill(artR.Inset(-5), Gfx.Fade(0xFF000000, 0.45f), Gfx.Corner(16));
                    if (art?.SharpBmp != null) Gfx.ImageRoundedCover(art.SharpBmp, artR, Gfx.Corner(14), MusicArt.Alpha(art));
                    else
                    {
                        var fb = Images.Get(Video.Thumb720, 640, 360);
                        if (fb != null) Gfx.ImageRoundedCover(fb.Bmp!, artR, Gfx.Corner(14), Images.Alpha(fb));
                        else Gfx.Fill(artR, Th.Card, Gfx.Corner(14));
                    }
                    Gfx.Stroke(artR, Gfx.Fade(Gfx.Mix(dom1, 0xFFFFFFFF, 0.35f), 0.38f + bass * 0.52f), Gfx.Corner(14), 1.6f);
                    if (UI.Click(UI.Id("ms:artplay", _infoScroll), artR)) mpv.TogglePause();
                    PeakStarBurst.Draw(video, artR, id, mpv.CurrentAudioUrl, mpv.TimePos, mpv.Duration,
                        sOpt.MusicImpactEffects && mpv.CurrentVideoId == id && !mpv.Paused && !mpv.Muted && mpv.Volume > 0);
                    MusicBounce.Pop(tfArt);
                }

                void DrawLosslessPill(float bx, float by)
                {
                    var bdR = new R(bx, by, bdW, bdH);
                    var tfBd = MusicBounce.Push(bdR, 14, amp: 0.90f, textSafe: true);
                    bool overBd = isThisStagePlaying && UI.Hover(bdR);
                    float bdRad = bdH * 0.5f;
                    if (bdLossless)
                    {
                        // TIDAL-style dark capsule with subtle bottom cyan/gold inner glow & crisp border
                        Gfx.Fill(bdR, Gfx.Fade(0xFF080C0C, 0.94f), bdRad);
                        Gfx.PushClip(bdR);
                        if (bdHiRes)
                            Gfx.RadialGlow(bdR.X + bdR.W * 0.5f, bdR.Bottom + 2f, bdR.W * 0.58f, bdR.H * 0.92f, 1.00f, 0.82f, 0.32f, overBd ? 0.34f : 0.24f);
                        else
                            Gfx.RadialGlow(bdR.X + bdR.W * 0.5f, bdR.Bottom + 2f, bdR.W * 0.58f, bdR.H * 0.92f, 0.11f, 0.91f, 0.71f, overBd ? 0.34f : 0.24f);
                        Gfx.PopClip();
                        uint borderCol = bdHiRes
                            ? Gfx.Fade(0xFFFFD166, overBd ? 0.85f : 0.52f)
                            : Gfx.Fade(0xFF1DE9B6, overBd ? 0.82f : 0.45f);
                        uint textCol = bdHiRes ? 0xFFFFD86B : 0xFF20F0BE;
                        Gfx.Stroke(bdR, borderCol, bdRad, 1.25f);
                        Gfx.Draw(bdRun, bdR.X + bdPadX, bdR.Y + (bdR.H - bdRun.H) * 0.5f, textCol);
                    }
                    else if (bdDownloading)
                    {
                        // Live FLAC background download progress fill inside the pill while YouTube plays uninterrupted!
                        Gfx.Fill(bdR, Gfx.Fade(0xFF0A1012, 0.90f), bdRad);
                        float fillW = MathF.Max(bdRad * 2f, bdR.W * (dlPct / 100f));
                        Gfx.PushClip(new R(bdR.X, bdR.Y, MathF.Min(bdR.W, fillW), bdR.H));
                        Gfx.Fill(bdR, Gfx.Fade(0xFF1DE9B6, 0.24f), bdRad);
                        Gfx.PopClip();
                        Gfx.Stroke(bdR, Gfx.Fade(0xFF1DE9B6, 0.55f), bdRad, 1.1f);
                        Gfx.Draw(bdRun, bdR.X + bdPadX, bdR.Y + (bdR.H - bdRun.H) * 0.5f, 0xFF7CFFE0);
                    }
                    else
                    {
                        Gfx.Fill(bdR, bdHiFi ? Gfx.Fade(0xFF1E170A, 0.88f) : Gfx.Fade(0xFF0C0F12, overBd ? 0.86f : 0.74f), bdRad);
                        Gfx.Stroke(bdR, bdHiFi ? Gfx.Fade(0xFFFFD166, 0.55f) : Gfx.Fade(0xFFFFFFFF, overBd ? 0.34f : 0.20f), bdRad, 1f);
                        Gfx.Draw(bdRun, bdR.X + bdPadX, bdR.Y + (bdR.H - bdRun.H) * 0.5f, bdHiFi ? 0xFFFFE18A : 0xFFFFFFFF);
                    }
                    if (isThisStagePlaying && UI.Click(UI.Id("ms:flacbd", _infoScroll), bdR))
                    {
                        var res = PlaybackTracker.Instance.RequestOrToggleLossless();
                        if (res == "flac") Shell.Toast($"FLAC {mpv.TidalQualityLabel} · {mpv.LosslessSourceLabel ?? "Lossless"}", 0xFF20F0BE, 1800);
                        else if (res == "youtube") Shell.Toast(L.T("A/B: YouTube аудио", "A/B: YouTube audio"), Th.Sub, 1600);
                        else if (res == "searching") Shell.Toast(L.T("Ищем ULTRA FLAC…", "Searching ULTRA FLAC…"), 0xFF20F0BE, 1800);
                    }
                    MusicBounce.Pop(tfBd);
                }

                if (centerLayout)
                {
                    float txtMaxW = MathF.Min(video.W - 84f, 760f);
                    float titleSize = video.W > 960 ? 22f : 19f;
                    var titleMeas = Gfx.Text(Title, titleSize, 700, txtMaxW, 1);
                    float metaBlockH = 14f + titleMeas.H + 8f + 36f;

                    float maxArtH = Math.Clamp(heroH - metaBlockH - 10f, 108f, 260f);
                    float maxArtW = MathF.Min(video.W * 0.44f, maxArtH * aspect);
                    float baseH = MathF.Min(maxArtH, maxArtW / aspect);
                    float baseW = baseH * aspect;
                    float artW = baseW * pulse, artH = baseH * pulse;

                    float groupH = baseH + metaBlockH;
                    float groupTop = heroTop + MathF.Max(0f, (heroH - groupH) * 0.5f);
                    float centerX = video.X + video.W * 0.5f;
                    var artR = new R(Gfx.Snap(centerX - artW * 0.5f), Gfx.Snap(groupTop - (artH - baseH) * 0.5f), artW, artH);
                    DrawHeroCover(artR);

                    float titleY = Gfx.Snap(groupTop + baseH + 14f);
                    float titleX = Gfx.Snap(centerX - MathF.Min(titleMeas.W, txtMaxW) * 0.5f);
                    DrawWaveText(Title, titleX, titleY, titleSize, 0xFFFFFFFF, 700, txtMaxW, maxLines: 1, shadow: true, accentColor: dom1);

                    float rowY = Gfx.Snap(titleY + titleMeas.H + 8f);
                    float playSz = 34f;
                    var chMeas = Gfx.Text(chText, 13.5f, 500, MathF.Min(320f, txtMaxW - 200f), 1);
                    float rowTotalW = playSz + 12f + chMeas.W + 10f + bdW;
                    float rx0 = Gfx.Snap(centerX - rowTotalW * 0.5f);

                    var playR = new R(rx0, rowY, playSz, playSz);
                    var tfPlay = MusicBounce.Push(playR, 0, amp: 1.22f, rotAmp: 0.75f);
                    bool overPlay = UI.Hover(playR);
                    Gfx.Circle(playR.X + playSz * 0.5f, playR.Y + playSz * 0.5f, playSz * 0.5f + 2.5f, Gfx.Fade(dom1, 0.26f + kick * 0.24f));
                    Gfx.Circle(playR.X + playSz * 0.5f, playR.Y + playSz * 0.5f, playSz * 0.5f, overPlay ? Gfx.Mix(0xFFFFFFFF, Th.Brand, 0.18f) : 0xFFFFFFFF);
                    Gfx.Icon(mpv.Paused ? Th.Play : Th.Pause, new R(playR.X + (mpv.Paused ? 1.5f : 0f), playR.Y, playSz, playSz), 14.5f, 0xFF08080E);
                    if (UI.Click(UI.Id("ms:bigplay", _infoScroll), playR)) mpv.TogglePause();
                    MusicBounce.Pop(tfPlay);

                    float chX = playR.Right + 12f;
                    float chY = rowY + (playSz - chMeas.H) * 0.5f;
                    Gfx.DrawDancingText(chText, chX, chY, 13.5f, Gfx.Fade(0xFFFFFFFF, 0.86f), 500, MathF.Min(360f, txtMaxW - 140f), 1, shadow: true, amp: 0.80f);
                    DrawLosslessPill(chX + chMeas.W + 10f, rowY + (playSz - bdH) * 0.5f);
                }
                else
                {
                    // Classic Left-Aligned Hero: Large Cover on Left + Dancing Title, Big Play Button, Channel & TIDAL Badge on Right
                    float maxArtH = Math.Clamp(heroH - 14f, 118f, 250f);
                    float maxArtW = MathF.Min(video.W * 0.40f, maxArtH * aspect);
                    float baseH = MathF.Min(maxArtH, maxArtW / aspect);
                    float baseW = baseH * aspect;
                    float artW = baseW * pulse, artH = baseH * pulse;

                    float artBaseX = Gfx.Snap(video.X + 28f);
                    float artBaseY = Gfx.Snap(heroTop + (heroH - baseH) * 0.5f);
                    var artR = new R(Gfx.Snap(artBaseX - (artW - baseW) * 0.5f), Gfx.Snap(artBaseY - (artH - baseH) * 0.5f), artW, artH);
                    DrawHeroCover(artR);

                    float colX = Gfx.Snap(artBaseX + baseW + 26f);
                    float colW = MathF.Max(180f, video.Right - 28f - colX);
                    float titleSize = video.W > 960 ? 24f : 20f;
                    var titleMeas = Gfx.Text(Title, titleSize, 700, colW, 2);
                    float playSz = 44f;
                    float stackH = titleMeas.H + 14f + playSz;
                    float stackY = Gfx.Snap(heroTop + MathF.Max(0f, (heroH - stackH) * 0.5f));

                    DrawWaveText(Title, colX, stackY, titleSize, 0xFFFFFFFF, 700, colW, maxLines: 2, shadow: true, accentColor: dom1);

                    float rowY = Gfx.Snap(stackY + titleMeas.H + 14f);
                    var playR = new R(colX, rowY, playSz, playSz);
                    var tfPlay = MusicBounce.Push(playR, 0, amp: 1.28f, rotAmp: 0.80f);
                    bool overPlay = UI.Hover(playR);
                    Gfx.Circle(playR.X + playSz * 0.5f, playR.Y + playSz * 0.5f, playSz * 0.5f + 3f, Gfx.Fade(dom1, 0.28f + kick * 0.26f));
                    Gfx.Circle(playR.X + playSz * 0.5f, playR.Y + playSz * 0.5f, playSz * 0.5f, overPlay ? Gfx.Mix(0xFFFFFFFF, Th.Brand, 0.18f) : 0xFFFFFFFF);
                    Gfx.Icon(mpv.Paused ? Th.Play : Th.Pause, new R(playR.X + (mpv.Paused ? 1.8f : 0f), playR.Y, playSz, playSz), 18f, 0xFF08080E);
                    if (UI.Click(UI.Id("ms:bigplay", _infoScroll), playR)) mpv.TogglePause();
                    MusicBounce.Pop(tfPlay);

                    float chX = playR.Right + 14f;
                    float chMaxW = MathF.Max(80f, video.Right - 28f - chX - bdW - 12f);
                    var chMeas = Gfx.Text(chText, 14f, 500, chMaxW, 1);
                    float chY = rowY + (playSz - chMeas.H) * 0.5f;
                    Gfx.DrawDancingText(chText, chX, chY, 14f, Gfx.Fade(0xFFFFFFFF, 0.88f), 500, chMaxW, 1, shadow: true, amp: 0.82f);
                    DrawLosslessPill(chX + MathF.Min(chMeas.W, chMaxW) + 12f, rowY + (playSz - bdH) * 0.5f);
                }
            }
            else
            {
                var centerArt = new R(video.X + video.W * 0.38f, heroTop + heroH * 0.25f, video.W * 0.24f, heroH * 0.45f);
                PeakStarBurst.Draw(video, centerArt, id, mpv.CurrentAudioUrl, mpv.TimePos, mpv.Duration,
                    sOpt.MusicImpactEffects && mpv.CurrentVideoId == id && !mpv.Paused && !mpv.Muted && mpv.Volume > 0);

                float playSz = 56f;
                float rowY = vizR.Y - playSz - 16f;
                var playR = new R(Gfx.Snap(video.X + 24f), Gfx.Snap(rowY), playSz, playSz);
                var tfPlay = MusicBounce.Push(playR, 0, amp: 1.38f, rotAmp: 0.90f);
                bool overPlay = UI.Hover(playR);
                Gfx.Circle(playR.X + playSz * 0.5f, playR.Y + playSz * 0.5f, playSz * 0.5f + 3f, Gfx.Fade(0xFF000000, 0.42f));
                Gfx.Circle(playR.X + playSz * 0.5f, playR.Y + playSz * 0.5f, playSz * 0.5f, overPlay ? Gfx.Mix(0xFFFFFFFF, Th.Brand, 0.18f) : 0xFFFFFFFF);
                Gfx.Icon(mpv.Paused ? Th.Play : Th.Pause, new R(playR.X + (mpv.Paused ? 2f : 0f), playR.Y, playSz, playSz), 22f, 0xFF08080E);
                if (UI.Click(UI.Id("ms:bigplay", _infoScroll), playR)) mpv.TogglePause();
                MusicBounce.Pop(tfPlay);

                float txtX = playR.Right + 16f;
                float txtW = MathF.Max(140f, video.Right - 28f - txtX);
                float titleSize = video.W > 960 ? 26f : 22f;
                var titleMeas = Gfx.Text(Title, titleSize, 700, txtW, 1);
                DrawWaveText(Title, txtX, rowY + 2f, titleSize, 0xFFFFFFFF, 700, txtW, maxLines: 1, shadow: true, accentColor: dom1);
                Gfx.DrawDancingText(chText, txtX, rowY + titleMeas.H + 4f, 13.5f, Gfx.Fade(0xFFFFFFFF, 0.88f), 500, txtW, 1, shadow: true, amp: 0.85f);
            }

            // 5. Tall Full-Width SoundCloud Pro Waveform (THE ONLY SEEKBAR — no redundant bottom progress bar!)
            if (!_commentsStarted && _info?.CommentsToken != null) LoadComments();
            var scR = new R(video.X + 18, scY, video.W - 36, scHeight);
            bool isThisPlaying = mpv.CurrentVideoId == id;
            SoundCloudBar.Draw(scR, id, isThisPlaying ? mpv.TimePos : 0, isThisPlaying ? mpv.Duration : 0,
                isThisPlaying ? mpv.CurrentAudioUrl : null,
                GetSoundCloudMarkers(isThisPlaying ? mpv.Duration : 0),
                _info?.Chapters ?? [], compact: true, musicStage: true);

            // 6. SoundCloud Pro Bottom Action Dock: [♥ Likes] [Avatar · Комментарий на X:XX] [🔊 ━━●━━ 100%] [↺ ↻ 🔁 🔗 ↑]
            var barR = new R(video.X + 18, dockY, video.W - 36, dockH);
            float lx = barR.X;
            float btnH = 38f;
            float btnY = barR.Y + (barR.H - btnH) * 0.5f;

            // Left: Bouncing Heart Like Pill
            bool isLiked = _rating == "LIKE";
            var likeTxt = _likes == "—" ? L.T("Нравится", "Like") : _likes;
            var likeRun = Gfx.Text(likeTxt, 13f, 600);
            float likeW = MathF.Max(74f, 14f + 16f + 7f + likeRun.W + 14f);
            var likePill = new R(lx, btnY, likeW, btnH);
            var tfLike = MusicBounce.Push(likePill, 2, amp: 1.32f, rotAmp: 0.90f);
            bool overLike = UI.Hover(likePill);
            Gfx.Fill(likePill, isLiked ? Gfx.Fade(Th.Brand, 0.28f) : Gfx.Fade(0xFF0E0E14, overLike ? 0.86f : 0.72f), btnH * 0.5f);
            Gfx.Stroke(likePill, isLiked ? Gfx.Fade(Th.Brand, 0.78f) : Gfx.Fade(0xFFFFFFFF, 0.16f), btnH * 0.5f, 1f);
            Gfx.Icon(isLiked ? "\uEB52" : "\uEB51", new R(likePill.X + 12f, likePill.Y, 18f, btnH), 14.5f, isLiked ? Th.Brand : 0xFFFFFFFF);
            Gfx.DrawDancingText(likeTxt, likePill.X + 35f, likePill.Y + (btnH - likeRun.H) * 0.5f, 13f, isLiked ? Th.Brand : 0xFFFFFFFF, 600, 0, 1, amp: 0.95f, accentMixColor: Th.Brand);
            if (UI.Click(UI.Id("ms:like", _infoScroll), likePill, playSfx: false)) Rate(true);
            MusicBounce.Pop(tfLike);
            lx += likeW + 10f;

            // Right: 5 Circular Action Buttons (Rewind, Forward, Loop, Copy Timestamp Link, Share)
            float rx = barR.Right;
            bool CircleBtn(string cid, string icon, int band, bool active, string? tip, Action click)
            {
                rx -= btnH;
                var cr = new R(rx, btnY, btnH, btnH);
                var tf = MusicBounce.Push(cr, band, amp: 1.0f, rotAmp: 0.8f);
                bool ov = UI.Hover(cr);
                Gfx.Circle(cr.X + btnH * 0.5f, cr.Y + btnH * 0.5f, btnH * 0.5f,
                    active ? Gfx.Fade(Th.Brand, 0.30f) : Gfx.Fade(0xFF0E0E14, ov ? 0.88f : 0.72f));
                Gfx.Stroke(cr, active ? Gfx.Fade(Th.Brand, 0.75f) : Gfx.Fade(0xFFFFFFFF, 0.16f), btnH * 0.5f, 1f);
                Gfx.Icon(icon, cr, 14f, active ? Th.Brand : 0xFFFFFFFF);
                if (UI.Click(UI.Id(cid, _infoScroll), cr)) { click(); MusicBounce.Pop(tf); rx -= 7f; return true; }
                MusicBounce.Pop(tf);
                rx -= 7f;
                return false;
            }

            CircleBtn("ms:share", Th.Share, 28, false, null, OpenShare);
            CircleBtn("ms:link", Th.Link, 24, false, null, () => Cards.CopyText(PageLink(true), L.T("Ссылка с таймкодом скопирована", "Timestamp link copied")));
            CircleBtn("ms:loop", "\uE8EE", 20, _musicLoop, null, () =>
            {
                _musicLoop = !_musicLoop;
                mpv.Command("set", "loop-file", _musicLoop ? "inf" : "no");
                Shell.Toast(_musicLoop ? L.T("Повтор трека включён", "Track repeat ON") : L.T("Повтор трека выключен", "Track repeat OFF"), _musicLoop ? Th.Good : Th.Sub, 1400);
            });
            CircleBtn("ms:fwd", Th.Forward, 15, false, null, () => mpv.Command("seek", "10"));
            CircleBtn("ms:rew", Th.Rewind, 11, false, null, () => mpv.Command("seek", "-10"));

            // Middle-Right: Interactive Volume Capsule (Mute + 0..150% Slider + Wheel!)
            float volPillW = barR.W < 700 ? 128f : 162f;
            rx -= volPillW;
            var volPill = new R(rx, btnY, volPillW, btnH);
            Gfx.Fill(volPill, Gfx.Fade(0xFF0E0E14, 0.74f), btnH * 0.5f);
            Gfx.Stroke(volPill, Gfx.Fade(0xFFFFFFFF, 0.16f), btnH * 0.5f, 1f);
            int volume = mpv.Muted ? 0 : (int)Math.Round(mpv.Volume);
            if (UI.IconButton(UI.Id("ms:mute", _infoScroll), new R(volPill.X + 4f, volPill.Y + 1f, 30f, btnH - 2f),
                mpv.Muted || volume == 0 ? Th.Mute : Th.Volume, 14f, 0xFFFFFFFF))
                mpv.Command("cycle", "mute");
            float vx = volPill.X + 36f;
            float vw = volPillW - 76f;
            var volumeHit = new R(vx - 4f, volPill.Y + 2f, vw + 8f, btnH - 4f);
            if (UI.Pressed && UI.Hover(volumeHit))
            {
                _musicVolumeDrag = true;
                _musicVolumeTarget = -1;
            }
            if (_musicVolumeDrag)
            {
                if (UI.Down || UI.Released)
                {
                    int target = (int)MathF.Round(Math.Clamp((UI.MX - vx) / MathF.Max(1f, vw), 0f, 1f) * 150f);
                    if (target != _musicVolumeTarget) { _musicVolumeTarget = target; Shell.SetMusicVolume(target); }
                    volume = _musicVolumeTarget;
                    UI.Animate();
                }
                if (!UI.Down) _musicVolumeDrag = false;
            }
            if (UI.Hover(volumeHit)) UI.Hand();
            if (UI.Wheel != 0 && UI.Hover(barR))
            {
                Shell.SetMusicVolume(volume + (UI.Wheel > 0 ? 5 : -5));
                UI.Wheel = 0;
            }
            float volFrac = Math.Clamp(volume / 150f, 0f, 1f);
            UI.Progress(new R(vx, volPill.Y + btnH * 0.5f - 2f, vw, 4f), volFrac, Gfx.Fade(0xFFFFFFFF, 0.24f), Th.Brand);
            Gfx.Circle(vx + vw * volFrac, volPill.Y + btnH * 0.5f, _musicVolumeDrag || UI.Hover(volumeHit) ? 5.5f : 4.2f, 0xFFFFFFFF);
            var vPct = Gfx.Text($"{volume}%", 10.5f, 600);
            Gfx.Draw(vPct, volPill.Right - vPct.W - 9f, volPill.Y + (btnH - vPct.H) * 0.5f, Gfx.Fade(0xFFFFFFFF, 0.78f));
            rx -= 10f;

            // Middle-Left: SoundCloud Comment Pill "[Avatar] Комментарий на 0:14  ➤"
            float cmtW = rx - lx;
            if (cmtW > 130f)
            {
                var cmtR = new R(lx, btnY, cmtW, btnH);
                bool overCmt = UI.Hover(cmtR);
                Gfx.Fill(cmtR, Gfx.Fade(0xFF0E0E14, overCmt ? 0.86f : 0.72f), btnH * 0.5f);
                Gfx.Stroke(cmtR, overCmt ? Gfx.Fade(Th.Brand, 0.55f) : Gfx.Fade(0xFFFFFFFF, 0.16f), btnH * 0.5f, 1f);
                var acc = AccountMenu.Current;
                Cards.Avatar(cmtR.X + 7f, cmtR.Y + (btnH - 24f) * 0.5f, 24f, acc?.Photo, acc?.Name is { Length: > 0 } nm ? nm : "?");
                string tcStr = Fmt.Time(mpv.TimePos);
                string cmtTxt = L.T($"Комментарий на {tcStr}", $"Comment at {tcStr}");
                var cRun = Gfx.Text(cmtTxt, 12.5f, 500, MathF.Max(40f, cmtW - 76f));
                Gfx.Draw(cRun, cmtR.X + 39f, cmtR.Y + (btnH - cRun.H) * 0.5f, Gfx.Fade(0xFFFFFFFF, overCmt ? 0.95f : 0.72f));
                Gfx.Icon("\uE724", new R(cmtR.Right - 34f, cmtR.Y, 26f, btnH), 13.5f, overCmt ? Th.Brand : Gfx.Fade(0xFFFFFFFF, 0.60f));
                if (UI.Click(UI.Id("ms:comment", _infoScroll), cmtR))
                {
                    if (!CookieStore.IsLoggedIn) App.Post(LoginModal.Show);
                    else
                    {
                        float jumpTo = _commentsSectionY > 40f ? _commentsSectionY - 30f : videoH + 260f;
                        UI.ScrollBy(_infoScroll, jumpTo - UI.ScrollTarget(_infoScroll));
                        App.Post(() =>
                        {
                            if (_commentEdit == null)
                            {
                                _commentEdit = new Edit(L.T("Введите комментарий…", "Add a comment…")) { Bg = () => Th.Field };
                                _commentEdit.Enter = SubmitComment;
                            }
                            if (string.IsNullOrWhiteSpace(_commentEdit.Text))
                                _commentEdit.Text = tcStr + " ";
                            _commentEdit.Focus();
                        });
                    }
                }
            }

            // Floating Volume HUD pill when adjusting volume by mouse wheel over Music Stage
            if (Shell.MusicVolHudActive)
            {
                int hv = Shell.MusicVolHudValue;
                var hTxt = $"{hv}%";
                var hRun = Gfx.Text(hTxt, 13f, 700);
                float hw = 148f, hh = 34f;
                var hudR = new R(video.Right - hw - 18f, video.Y + 52f, hw, hh);
                Gfx.Fill(hudR, Gfx.Fade(0xFF0A0A12, 0.86f), hh * 0.5f);
                Gfx.Stroke(hudR, Gfx.Fade(Th.Brand, 0.72f), hh * 0.5f, 1.2f);
                Gfx.Icon(hv == 0 ? Th.Mute : Th.Volume, new R(hudR.X + 10f, hudR.Y, 18f, hh), 14f, Th.Brand);
                UI.Progress(new R(hudR.X + 34f, hudR.Y + hh * 0.5f - 2.5f, 68f, 5f), Math.Clamp(hv / 150.0, 0, 1), Gfx.Fade(0xFFFFFFFF, 0.22f), Th.Brand);
                Gfx.Draw(hRun, hudR.Right - hRun.W - 12f, hudR.Y + (hh - hRun.H) * 0.5f, 0xFFFFFFFF);
                UI.Animate();
            }

            // Crisp stage border
            Gfx.Stroke(video, Gfx.Fade(0xFFFFFFFF, 0.10f), stageRad, 1f);
            Gfx.PopClip();
        }

        // ---- info column ----
        var titleRun = Gfx.Text(Title, 20, 600, mainW, 3);
        var desc = _info?.Description ?? "";
        var descRun = desc.Length > 0 ? Gfx.Text(desc, 13.5f, 400, mainW - 32, _descOpen ? 0 : 4) : null;
        var canToggle = desc.Length > 280 || desc.Count(c => c == '\n') > 3;
        float descBoxH = 14 + 20 + (descRun != null ? descRun.H + 6 : 0) + (canToggle ? 24 : 0) + 12;
        var listInline = _list != null;
        const float relGap = 14;
        int relatedCols = Math.Max(1, (int)((mainW + relGap) / (270 + relGap)));
        float relatedCardW = MathF.Floor((mainW - relGap * (relatedCols - 1)) / relatedCols);
        float relatedThumbH = MathF.Round(relatedCardW * 9f / 16f);
        float relatedRowH = relatedThumbH + 96f;
        int relatedRows = (_related.Count + relatedCols - 1) / relatedCols;
        float relatedH = 44 + relatedRows * relatedRowH + (_relCont != null ? 54 : 16);
        float infoContentH = 14 + titleRun.H + 14 + 44 + _actionExtraH + 14 + descBoxH + (_note != null ? 40 : 0) + relatedH + 30 + _commentsH + (listInline ? _listInlineH : 0);

        var view = new R(x0, r.Y, mainW + 12, r.H);
        var scroll = UI.BeginScroll(_infoScroll, view, topPad + videoH + infoContentH);
        bool beatHere = MpvPlayer.Instance.CurrentVideoId == id && !AppSettings.Current.AudioOnlyMode && !Pip.Active;
        var vShift = beatHere ? Shell.VideoBeatShift : default;
        var videoBase = new R(videoX, Gfx.Snap(view.Y + topPad - scroll), videoW, videoH);
        var video = vShift == default ? videoBase : new R(Gfx.Snap(videoBase.X + vShift.X), Gfx.Snap(videoBase.Y + vShift.Y), videoW, videoH);
        using var pageBeat = new PageBeatMotion(videoBase, MpvPlayer.Instance.CurrentVideoId == id);
        float pageCenterX = videoBase.X + videoBase.W / 2;
        pageBeat.At(pageCenterX, videoBase.Y + videoBase.H / 2, AppSettings.Current.AudioOnlyMode);
        DrawVideoBox(video, view);
        pageBeat.At(pageCenterX, videoBase.Bottom + 40);
        float y = videoBase.Bottom + 12;

        // In normal video mode, no waveform strip under the player — instead feed timed comment avatar circles
        // directly to the mpv media player bar (fluxosc.lua) right above the seekbar!
        if (AppSettings.Current.SoundCloudWave && !AppSettings.Current.AudioOnlyMode && !Pip.Active && PlaybackTracker.Instance.Current?.VideoId == id)
        {
            if (!_commentsStarted && _info?.CommentsToken != null) LoadComments();
            var durNow = MpvPlayer.Instance.Duration;
            int dSec = (int)Math.Round(durNow);
            if (_comments.Count > 0 && durNow > 5 && (_scPushedComments != _comments.Count || Math.Abs(_scPushedDurSec - dSec) > 2))
            {
                _scPushedComments = _comments.Count;
                _scPushedDurSec = dSec;
                App.Post(Shell.PushPlayerState);
            }
        }

        pageBeat.At(pageCenterX, y);
        if (Title.Length > 0)
        {
            if (MusicBounce.Active && !UI.Down)
                Gfx.DrawDancingText(Title, x0 + 3f, y, 20, Th.Text, 600, mainW - 6f, 3, amp: 1.05f, accentMixColor: Th.Brand);
            else
                Sel.Text(UI.Id(_infoScroll, 8001), Title, x0, y, 20, Th.Text, 600, mainW, 3);
        }
        Cards.CopyMenu(new R(x0, y, titleRun.W, titleRun.H), Title, L.T("Копировать название", "Copy title"));
        y += titleRun.H + 14;

        pageBeat.At(pageCenterX, y + 22);
        // channel + upgraded Like/Dislike pill + actions (wraps cleanly onto 2 rows if narrow so Like/Dislike NEVER vanishes!)
        var chName = _info?.Channel ?? Video.Channel;
        var subText = _info?.Subscribers ?? "";
        float bx = x0 + mainW, by = y + 4;
        const float ph = 36;
        bx -= 40;
        var rTh = new R(bx, by, 36, 36); var tfA1 = MusicBounce.Push(rTh, 28, amp: 1.0f);
        if (UI.IconButton(UI.Id("theater", _infoScroll), rTh, Th.Theater, 16, AppSettings.Current.TheaterMode ? Th.Brand : Th.Text)) ToggleTheater();
        MusicBounce.Pop(tfA1);
        bx -= 42;
        var rPip = new R(bx, by, 36, 36); var tfA2 = MusicBounce.Push(rPip, 25, amp: 1.0f);
        if (UI.IconButton(UI.Id("pip", _infoScroll), rPip, "\uE8A7", 16, Pip.Active ? Th.Brand : Th.Text)) App.Post(Pip.Toggle);
        MusicBounce.Pop(tfA2);
        bx -= 42;
        var rAo = new R(bx, by, 36, 36); var tfA3 = MusicBounce.Push(rAo, 22, amp: 1.05f);
        if (UI.IconButton(UI.Id("audioonly", _infoScroll), rAo, Th.Headphones, 16, AppSettings.Current.AudioOnlyMode ? Th.Brand : Th.Text)) App.Post(Shell.ToggleAudioOnlyMode);
        MusicBounce.Pop(tfA3);
        bx -= 42;
        var rMr = new R(bx, by, 36, 36); var tfA4 = MusicBounce.Push(rMr, 19, amp: 1.0f);
        if (UI.IconButton(UI.Id("more", _infoScroll), rMr, Th.More)) MoreMenu();
        MusicBounce.Pop(tfA4);
        var s = AppSettings.Current;
        if (!s.AudioOnlyMode)
        {
            var beatLabel = L.T("Видео в бит", "Video beat");
            var beatW = UI.PillWidth(null, beatLabel);
            bx -= beatW + 8;
            var tfVb = MusicBounce.Push(new R(bx, by, beatW, ph), 17, amp: 1.0f);
            if (UI.Pill(UI.Id("videobeat", _infoScroll), bx, by, ph, null, beatLabel, out _, accent: s.VideoBeatShake))
                Shell.ToggleVideoBeatShake();
            MusicBounce.Pop(tfVb);

            var scLbl = L.T("Кругляшки", "Comments");
            var scW = UI.PillWidth("\uE8BD", scLbl);
            bx -= scW + 8;
            var tfSc = MusicBounce.Push(new R(bx, by, scW, ph), 15, amp: 1.05f);
            if (UI.Pill(UI.Id("scwave", _infoScroll), bx, by, ph, "\uE8BD", scLbl, out _, accent: s.SoundCloudWave))
            {
                s.SoundCloudWave = !s.SoundCloudWave;
                s.Save();
                Shell.PushPlayerState();
                App.Invalidate();
            }
            MusicBounce.Pop(tfSc);
        }
        var eqLbl = s.AudioFxEnabled ? AudioDsp.DisplayPresetName(s) : "EQ";
        var eqW = UI.PillWidth(null, eqLbl);
        bx -= eqW + 8;
        var tfEqP = MusicBounce.Push(new R(bx, by, eqW, ph), 13, amp: 1.1f);
        if (UI.Pill(UI.Id("eq", _infoScroll), bx, by, ph, null, eqLbl, out _, accent: s.AudioFxEnabled)) App.Post(AudioStudioPanel.Toggle);
        MusicBounce.Pop(tfEqP);
        var shareLbl = L.T("Поделиться", "Share");
        var shW = UI.PillWidth(Th.Share, shareLbl);
        bx -= shW + 8;
        var tfShP = MusicBounce.Push(new R(bx, by, shW, ph), 10, amp: 0.95f);
        if (UI.Pill(UI.Id("share", _infoScroll), bx, by, ph, Th.Share, shareLbl, out _)) OpenShare();
        MusicBounce.Pop(tfShP);
        var ql = Cards.QualityName(s.PlayerQuality);
        var qW = UI.PillWidth(null, ql);
        bx -= qW + 8;
        var tfQP = MusicBounce.Push(new R(bx, by, qW, ph), 7, amp: 0.95f);
        if (UI.Pill(UI.Id("q", _infoScroll), bx, by, ph, null, ql, out _)) QualityMenu();
        MusicBounce.Pop(tfQP);
        var spTxt = Speed;
        var spW = UI.PillWidth(null, spTxt);
        bx -= spW + 8;
        var tfSpP = MusicBounce.Push(new R(bx, by, spW, ph), 4, amp: 0.85f);
        if (UI.Pill(UI.Id("sp", _infoScroll), bx, by, ph, null, spTxt, out _)) SpeedMenu();
        MusicBounce.Pop(tfSpP);
        if (UI.Now - _dlCheckedAt > 1000) { _dlCheckedAt = UI.Now; _isDownloaded = Downloader.Instance.IsDownloaded(id, out _); }
        var dl = _isDownloaded ? L.T("Скачано", "Saved") : L.T("Скачать", "Download");
        var dlW = UI.PillWidth(Th.Download, dl);
        bx -= dlW + 8;
        var tfDlP = MusicBounce.Push(new R(bx, by, dlW, ph), 2, amp: 1.0f);
        if (UI.Pill(UI.Id("dl", _infoScroll), bx, by, ph, Th.Download, dl, out _)) DownloadMenu();
        MusicBounce.Pop(tfDlP);

        float estLikeW = 14 + 18 + 7 + Gfx.Text(_likes, 13.5f, 600).W + 14 + 1 + 12 + 18 + 7 + Gfx.Text(_dislikes, 13.5f, 600).W + 14;
        var nameFullW = Gfx.Text(chName, 15, 600, 260).W;
        var subFullW = subText.Length > 0 ? Gfx.Text(subText, 12, 400, 260).W : 0;
        var minChNeed = MathF.Min(190, MathF.Max(nameFullW, subFullW));
        float leftEdge = bx;
        bool likeOnSameRow = bx - estLikeW - 8 > x0 + 54 + minChNeed + 14;
        if (likeOnSameRow)
        {
            bx -= estLikeW + 8;
            leftEdge = bx;
            DrawLikeDislikePill(bx, by, ph, out _);
        }

        var chMaxW = MathF.Max(70, MathF.Min(260, leftEdge - (x0 + 54) - 14));
        var chAvR = new R(x0, y + 1, 42, 42);
        var tfChAv = MusicBounce.Push(chAvR, 3, amp: 1.22f, textSafe: false);
        Cards.Avatar(chAvR.X, chAvR.Y, chAvR.W, _info?.ChannelAvatar, chName);
        MusicBounce.Pop(tfChAv);
        var nameRun = Gfx.Text(chName, 15, 600, chMaxW);
        var nameRect = new R(x0 + 54, y + 2, nameRun.W, nameRun.H);
        Gfx.DrawDancingText(chName, nameRect.X, nameRect.Y, 15, UI.Hover(nameRect) && (_info?.ChannelId ?? Video.ChannelId) != null ? Th.Brand : Th.Text, 600, chMaxW, 1, amp: 0.85f, accentMixColor: Th.Brand);
        Cards.CopyMenu(nameRect, chName, L.T("Копировать название канала", "Copy channel name"));
        if (subText.Length > 0) Gfx.Draw(subText, x0 + 54, y + 24, 12, Th.Sub, 400, chMaxW);
        var chId = _info?.ChannelId ?? Video.ChannelId;
        if (chId != null && (UI.Click(UI.Id("ch", _infoScroll), nameRect, playSfx: false) || UI.Click(UI.Id("chav", _infoScroll), new R(x0, y + 1, 42, 42), playSfx: false)))
            App.Post(() => Shell.OpenChannel(chId, chName));
        y += 44;

        float extraH = 0;
        if (!likeOnSameRow)
        {
            y += 8;
            DrawLikeDislikePill(x0, y, ph, out _);
            y += ph;
            extraH = ph + 8;
        }
        if (MathF.Abs(extraH - _actionExtraH) > 0.5f) { _actionExtraH = extraH; UI.Animate(); }
        y += 14;
        if (listInline)
        {
            pageBeat.At(pageCenterX, y + 120);
            var used = DrawPlaylist(new R(x0, y, mainW + 14, 560));
            if (MathF.Abs(used - _listInlineH) > 0.5f) { _listInlineH = used; UI.Animate(); }
            y += used;
        }

        pageBeat.At(pageCenterX, y + descBoxH / 2);
        // description
        var box = new R(x0, y, mainW, descBoxH);
        Gfx.Fill(box, Th.A(0.07f), 12);
        if (desc.Length > 0 && UI.RightClick(box))
        {
            var url = Video.Url; var full = Title + "\n" + url + "\n\n" + desc;
            Sel.Popup([new(L.T("Копировать описание", "Copy description"), () => Cards.CopyText(desc, L.T("Описание скопировано", "Description copied")), icon: Th.Copy),
                       new(L.T("Копировать название, ссылку и описание", "Copy title, link & description"), () => Cards.CopyText(full), icon: Th.Link)]);
        }
        var stats = _info == null ? (_infoLoading ? L.T("Загрузка…", "Loading…") : "") : string.Join("  ·  ", new[] { _info.Views, _info.Date }.Where(t => !string.IsNullOrEmpty(t)));
        Gfx.Draw(stats, x0 + 16, y + 12, 13, Th.Text, 600, mainW - 32);
        float dy = y + 14 + 20;
        if (descRun != null)
        {
            Sel.Text(UI.Id(_infoScroll, 8002), desc, x0 + 16, dy, 13.5f, Gfx.Mix(Th.Text, Th.Sub, 0.15f), 400, mainW - 32, _descOpen ? 0 : 4, links: true, onLink: OnLink);
            dy += descRun.H + 6;
        }
        if (canToggle)
        {
            var tr = Gfx.Text(_descOpen ? L.T("Свернуть", "Show less") : L.T("Ещё", "Show more"), 13, 600);
            var hit = new R(x0 + 16, dy, tr.W, tr.H + 4);
            Gfx.Draw(tr, hit.X, hit.Y, UI.Hover(hit) ? Th.Text : Th.Brand);
            if (UI.Click(UI.Id("desc", _infoScroll), hit)) _descOpen = !_descOpen;
        }
        y += descBoxH + 10;
        if (_note != null) { Gfx.Draw(_note, x0, y, 13, Th.Warn, 400, mainW, 2); y += 40; }
        pageBeat.At(pageCenterX, y + 100);
        y = DrawRelatedInline(x0, y + 14, mainW, view);
        var cy0 = y;
        pageBeat.At(pageCenterX, y + 100);
        y = DrawComments(x0, y + 14, mainW, view);
        var ch = y - cy0;
        if (MathF.Abs(ch - _commentsH) > 0.5f) { _commentsH = ch; UI.Animate(); }
        pageBeat.Dispose();
        UI.EndScroll();

        // the share panel sits under the video, above everything else on the page
        if (SharePanel.Open)
        {
            var px = Gfx.Snap(x0 + (mainW - SharePanel.W) / 2);
            var py = Gfx.Snap(Math.Clamp(video.Bottom + 10, r.Y + 12, MathF.Max(r.Y + 12, r.Bottom - SharePanel.H - 14)));
            UI.Modal = savedModal;
            SharePanel.Draw(new R(px, py, SharePanel.W, SharePanel.H));
        }

        Gfx.PopClip();
        UI.Modal = savedModal;
    }

    /// <summary>The playlist (or mix) this video is being watched in: its items, the current one marked. Returns the height used.</summary>
    private float _listInlineH;

    private float DrawPlaylist(R r)
    {
        var items = _list!.Items;
        const float rowH = 66;
        int cur = ListIndex;
        var listH = MathF.Min(items.Count * rowH + 6, MathF.Max(190, r.H * 0.5f));
        var box = new R(r.X, r.Y + 2, r.W - 14, 58 + listH);
        var tint = Gfx.Mix(Th.Bg, Th.Ink, 0.06f);
        Gfx.Fill(box, tint, 12);
        Gfx.Stroke(box, Th.Line, 12);
        Gfx.Draw(_list.Title, box.X + 14, box.Y + 10, 15, Th.Text, 700, box.W - 110);
        var sub = (cur >= 0 ? L.T($"{cur + 1} из {items.Count}", $"{cur + 1} / {items.Count}") : L.T($"{items.Count} видео", $"{items.Count} videos")) + (_list.IsMix ? L.T("  ·  микс YouTube", "  ·  YouTube Mix") : "");
        Gfx.Draw(sub, box.X + 14, box.Y + 32, 12, Th.Sub, 400, box.W - 110);
        // previous / next in the list
        if (UI.IconButton(UI.Id("pl:prev", _listScroll), new R(box.Right - 84, box.Y + 11, 34, 34), "\uE892", 14, null, cur > 0)) OpenInList(items[cur - 1]);
        if (UI.IconButton(UI.Id("pl:next", _listScroll), new R(box.Right - 46, box.Y + 11, 34, 34), "\uE893", 14, null, cur >= 0 && cur + 1 < items.Count)) OpenInList(items[cur + 1]);

        var view = new R(box.X + 4, box.Y + 54, box.W - 6, listH);
        var scroll = UI.BeginScroll(_listScroll, view, items.Count * rowH + 6);
        int first = Math.Max(0, (int)(scroll / rowH)), last = Math.Min(items.Count - 1, (int)((scroll + view.H) / rowH));
        for (int i = first; i <= last; i++)
        {
            var v = items[i];
            float y = view.Y + i * rowH - scroll;
            var row = new R(view.X + 2, y + 2, view.W - 14, rowH - 4);
            var id = UI.Id(_listScroll, i);
            var here = i == cur;
            if (here) Gfx.Fill(row, Th.A(0.1f), 8);
            var glow = here ? 0 : UI.Glow(id, row, 8, UI.Hover(row));
            if (here) Gfx.Fill(new R(row.X, row.Y + 10, 3, row.H - 20), Th.Brand, 1.5f);
            Cards.Thumb(new R(row.X + 10, row.Y + 3, 100, 56), v, false, 6, behind: here ? Gfx.Mix(tint, Th.Ink, 0.1f) : UI.GlowOn(tint, glow));
            float tx = row.X + 120, tw = row.W - 126;
            var t = Gfx.Draw(v.Title, tx, row.Y + 4, 13, Th.Text, here ? 700 : 600, tw, 2);
            Gfx.Draw(v.Channel, tx, row.Y + 7 + t.H, 11.5f, Th.Sub, 400, tw);
            if (UI.Hover(row) && !here) _hover.Over(v);
            if (UI.MiddleClick(row)) Shell.OpenVideoInNewTab(v);
            else if (UI.Click(id, row) && !here) OpenInList(v);
            else if (UI.RightClick(row)) Menus.Popup(Cards.MenuFor(v));
        }
        UI.EndScroll();
        // the first time the list is on screen it stands on the video being played
        if (_listJump && cur >= 0)
        {
            _listJump = false;
            UI.ScrollBy(_listScroll, cur * rowH - rowH - UI.ScrollTarget(_listScroll));
        }
        return box.H + 14;
    }

    private string? _relCont;
    private bool _relLoadingMore;
    private readonly HashSet<string> _relIds = [];

    private void LoadMoreRelated()
    {
        if (_relCont == null || _relLoadingMore) return;
        _relLoadingMore = true;
        var tok = _relCont;
        var vid = Video.VideoId;
        _ = Task.Run(async () =>
        {
            try
            {
                var page = Cards.WithProgress(await InnerTube.Instance.ContinueAsync("next", tok));
                App.Post(() =>
                {
                    if (Video.VideoId != vid) return;
                    var blocked = AppSettings.Current.BlockedChannels;
                    int added = 0;
                    foreach (var rv in page.Items)
                    {
                        if (!_relIds.Add(rv.VideoId)) continue;
                        if (blocked.Length > 0 && rv.ChannelId != null && Array.IndexOf(blocked, rv.ChannelId) >= 0) continue;
                        _related.Add(rv);
                        added++;
                    }
                    _relCont = page.Items.Count > 0 ? page.Continuation : null;
                    _relLoadingMore = false;
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("related more: " + e.Message);
                App.Post(() => { _relLoadingMore = false; _relCont = null; App.Invalidate(); });
            }
        });
    }

    private float DrawRelatedInline(float x, float y, float width, R view)
    {
        Gfx.Draw(L.T("Похожие видео", "Related videos"), x, y + 4, 18, Th.Text, 700);
        var settings = AppSettings.Current;
        var label = Gfx.Text(L.T("Автовоспроизведение", "Autoplay"), 12.5f);
        var toggle = new R(x + width - label.W - 60 - 6, y, label.W + 60, 30);
        Gfx.Draw(label, toggle.X, toggle.Y + (30 - label.H) / 2, Th.Sub);
        if (UI.Toggle(UI.Id("auto", _relScroll), toggle, settings.Autoplay))
        {
            settings.Autoplay = !settings.Autoplay;
            settings.Save();
        }
        y += 44;

        // Exact same big 16:9 Home-style GridCard layout (big 720p thumbs, avatars, live HD hover previews)
        const float gap = 14;
        int cols = Math.Max(1, (int)((width + gap) / (270 + gap)));
        float cardW = MathF.Floor((width - gap * (cols - 1)) / cols);
        float thumbH = MathF.Round(cardW * 9f / 16f);
        float rowH = thumbH + 96f;
        int rows = (_related.Count + cols - 1) / cols;

        for (int i = 0; i < _related.Count; i++)
        {
            var v = _related[i];
            float cx = x + (i % cols) * (cardW + gap);
            float cy = y + (i / cols) * rowH;
            var card = new R(cx, cy, cardW, rowH - 10);
            if (card.Bottom < view.Y - 40 || card.Y > view.Bottom + 40)
            {
                // Prewarm upcoming row thumbnails while scrolling toward them
                if (card.Y >= view.Bottom + 40 && card.Y <= view.Bottom + rowH * 2.5f && v.VideoId.Length == 11)
                    Images.Get(v.Thumb720, cardW, thumbH);
                continue;
            }
            PageBeatMotion.Current?.At(cx + cardW / 2, cy + rowH / 2);
            Cards.GridCard(card, thumbH, v, UI.Id(_relScroll, i), _hover, RelSource);
            if (_seen.Add(v.VideoId)) LogSeen(v);
        }
        y += rows * rowH;
        if (_related.Count == 0 && _infoLoading) UI.Spinner(x + width / 2, y + 12, 9, Th.Sub);
        if (_relCont != null)
        {
            if (y < view.Bottom + 30 && UI.Pill(UI.Id("rel:more", _relScroll), x, y + 6, 34, Th.More, L.T("Ещё похожие", "More related"), out _)) LoadMoreRelated();
            if (_relLoadingMore) UI.Spinner(x + 170, y + 23, 8, Th.Sub);
            y += 54;
        }
        else y += 16;
        return y;
    }

    private void DrawRelated(R r)
    {
        Gfx.Draw(L.T("Похожие", "Related"), r.X, r.Y + 6, 16, Th.Text, 600);
        var s = AppSettings.Current;
        var lab = Gfx.Text(L.T("Автовоспроизведение", "Autoplay"), 12.5f);
        var tog = new R(r.Right - 14 - 46 - lab.W - 8, r.Y + 2, lab.W + 8 + 46, 30);
        Gfx.Draw(lab, tog.X, tog.Y + (30 - lab.H) / 2, Th.Sub);
        if (UI.Toggle(UI.Id("auto", _relScroll), tog, s.Autoplay)) { s.Autoplay = !s.Autoplay; s.Save(); }

        var view = new R(r.X, r.Y + 40, r.W, r.H - 40);
        var textW = view.W - 14 - 168 - 14;
        if (textW != _relTextW) { _relTextW = textW; _relPrewarmed = 0; }
        if (_relPrewarmed < _related.Count)
        {
            var batch = new List<(string, float, int, float, int)>();
            for (int k = _relPrewarmed; k < _related.Count; k++)
            {
                var rv = _related[k];
                batch.Add((rv.Title, 13.5f, 600, textW, 2));
                batch.Add((rv.Channel, 12, 400, textW, 1));
                batch.Add((rv.Meta, 12, 400, textW, 1));
            }
            _relPrewarmed = _related.Count;
            Gfx.Prewarm(batch);
        }
        if (_related.Count == 0)
        {
            if (_infoLoading) UI.Spinner(view.X + view.W / 2 - 10, view.Y + 40, 10, Th.Sub);
            return;
        }
        const float rowH = 104, tw = 168, th = 94;
        float totalH = _related.Count * rowH + (_relCont != null ? 56 : 20);
        var scroll = UI.BeginScroll(_relScroll, view, totalH);
        if (_relCont != null && !_relLoadingMore && scroll + view.H >= _related.Count * rowH - 360)
            LoadMoreRelated();
        int first = Math.Max(0, (int)(scroll / rowH)), last = Math.Min(_related.Count - 1, (int)((scroll + view.H) / rowH));
        for (int i = first; i <= last; i++)
        {
            var v = _related[i];
            float y = view.Y + i * rowH - scroll;
            var row = new R(view.X, y, view.W - 14, rowH - 8);
            var id = UI.Id(_relScroll, i);
            var over = UI.Hover(row);
            var glow = UI.Glow(id, row.Inset(-5, -4), 10, over);
            Cards.Thumb(new R(row.X, y + 1, tw, th), v, false, 8, behind: UI.GlowOn(Th.Bg, glow));
            float tx = row.X + tw + 12, w = row.W - tw - 14;
            var t = Gfx.Draw(v.Title, tx, y, 13.5f, Th.Text, 600, w, 2);
            Gfx.Draw(v.Channel, tx, y + t.H + 4, 12, Th.Sub, 400, w);
            Gfx.Draw(v.Meta, tx, y + t.H + 22, 12, Th.Sub, 400, w);
            if (over) _hover.Over(v);
            if (UI.MiddleClick(row)) Shell.OpenVideoInNewTab(v);
            else if (UI.Click(id, row, playSfx: false)) OpenRelated(v);
            else if (UI.RightClick(row)) Menus.Popup(Cards.MenuFor(v));
            if (_seen.Add(v.VideoId)) LogSeen(v);
        }
        if (_relLoadingMore)
            UI.Spinner(view.X + view.W / 2 - 10, view.Y + _related.Count * rowH + 22 - scroll, 9, Th.Sub);
        UI.EndScroll();
    }

    private void OpenRelated(VideoItem v) { var src = RelSource; App.Post(() => Shell.OpenVideo(v, src)); }
    private void LogSeen(VideoItem v) { var src = RelSource; ThreadPool.QueueUserWorkItem(_ => Db.Seen(v, src)); }

    // ---------------- actions ----------------

    private VideoItem Full => new() { VideoId = Video.VideoId, Title = Title, Channel = _info?.Channel ?? Video.Channel, ChannelId = _info?.ChannelId ?? Video.ChannelId };

    private void DownloadMenu()
    {
        var v = Full;
        var items = new List<MenuItem> { new(L.T("Скачать (", "Download (") + Cards.QualityName(AppSettings.Current.DownloadQuality) + ")", () => Cards.Download(v, null), icon: Th.Download), MenuItem.Sep };
        items.AddRange(AppSettings.Qualities.Select(q => new MenuItem(Cards.QualityName(q), () => Cards.Download(v, q), icon: Th.Quality)));
        Menus.Popup(items);
    }

    public void SpeedMenu()
    {
        var items = new List<MenuItem> { MenuItem.Title(L.T("Скорость воспроизведения", "Playback speed")) };
        foreach (var sp in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0 })
        {
            var label = sp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
            items.Add(new(label, () => { MpvPlayer.Instance.SetSpeed(sp); _speed = label; Shell.PushPlayerState(); }, null, label == _speed, icon: Th.Speed));
        }
        Menus.Popup(items);
    }

    public void QualityMenu()
    {
        var s = AppSettings.Current;
        var items = new List<MenuItem> { MenuItem.Title(L.T("Качество видео", "Video quality")) };
        items.AddRange(AppSettings.Qualities.Where(q => q != "audio").Select(q => new MenuItem(Cards.QualityName(q), () =>
        {
            s.PlayerQuality = q;
            s.Save();
            MpvPlayer.Instance.ApplySettings();
            Resolver.Instance.Invalidate();
            QuickStream.Invalidate();
            PlaybackTracker.Instance.Flush();
            Shell.Play(Video, _source); // same position, new format
        }, null, q == s.PlayerQuality, icon: Th.Quality)));
        Menus.Popup(items);
    }

    // ---- subtitles and audio language ----
    private string _subKey = "";   // "" = off, otherwise language (+ ">ru" when translated)
    private string _audioId = "";  // "" = initial track
    private int _subGen;
    public bool SubsOn => _subKey.Length > 0 && PlaybackTracker.Instance.Current?.VideoId == Video.VideoId;

    private static readonly (string Code, string Ru, string En)[] TranslateTo = [("ru", "русский", "Russian"), ("en", "английский", "English"), ("uk", "украинский", "Ukrainian")];

    /// <summary>The CC button: subtitle languages, YouTube's machine translation, and the dubbed audio tracks.</summary>
    public void TracksMenu()
    {
        var id = Video.VideoId;
        if (PlaybackTracker.Instance.IsLocal) { Shell.Toast(L.T("У скачанного файла дорожки те, что в нём сохранены", "Local file uses its embedded tracks")); return; }
        var x = QuickStream.ExtrasOf(id);
        if (x == null)
        {
            Shell.Toast(L.T("Загружаю список дорожек…", "Loading track list…"), null, 1500);
            _ = QuickStream.EnsureSpecAsync(id).ContinueWith(_ => App.Post(() =>
            {
                if (Shell.Current != this) return;
                if (QuickStream.ExtrasOf(id) != null) TracksMenu();
                else Shell.Toast(L.T("Список субтитров не загрузился", "Could not load subtitles list"), Th.Warn);
            }));
            return;
        }
        Menus.Popup(TrackItems(x));
    }

    private bool IsAudioSelected(QuickStream.Extras x, QuickStream.AudioTrack a)
    {
        if (_audioId.Length > 0) return _audioId == a.Id;
        var pref = AppSettings.Current.AudioLanguage;
        if (!string.IsNullOrEmpty(pref) && x.Audio.Any(t => t.Lang.Equals(pref, StringComparison.OrdinalIgnoreCase) || t.Lang.StartsWith(pref + "-", StringComparison.OrdinalIgnoreCase)))
            return a.Lang.Equals(pref, StringComparison.OrdinalIgnoreCase) || a.Lang.StartsWith(pref + "-", StringComparison.OrdinalIgnoreCase);
        return a.IsDefault;
    }

    private List<MenuItem> TrackItems(QuickStream.Extras x)
    {
        var items = new List<MenuItem>();
        if (x.Captions.Count == 0) items.Add(new(L.T("У этого видео нет субтитров", "No subtitles available")));
        else
        {
            items.Add(new(L.T("Субтитры выключены", "Subtitles off"), () => SetSubs(x, null, null), null, _subKey.Length == 0));
            var hl = AppSettings.Current.Hl;
            foreach (var t in x.Captions.OrderByDescending(t => t.Lang.StartsWith(hl)).ThenBy(t => t.Auto).Take(14))
                items.Add(new(t.Name, () => SetSubs(x, t, null), null, _subKey == t.Lang + (t.Auto ? "*" : "")));
            if (x.Captions.Count > 14)
                items.Add(new(L.T("Другие языки", "Other languages"), null, x.Captions.OrderBy(t => t.Name).Select(t => new MenuItem(t.Name, () => SetSubs(x, t, null), null, _subKey == t.Lang + (t.Auto ? "*" : ""))).ToList()));
            var from = x.Captions.FirstOrDefault(t => !t.Auto) ?? x.Captions[0];
            items.Add(new(L.T("Автоперевод", "Auto-translate"), null, TranslateTo.Where(l => !from.Lang.StartsWith(l.Code))
                .Select(l => new MenuItem(L.T("На " + l.Ru, "To " + l.En), () => SetSubs(x, from, l.Code), null, _subKey == from.Lang + (from.Auto ? "*" : "") + ">" + l.Code)).ToList()));
        }
        if (x.Audio.Count > 1)
        {
            items.Add(MenuItem.Sep);
            items.Add(new(L.T("Язык озвучки", "Audio track"), null, x.Audio.Select(a => new MenuItem(a.Name, () => SetAudio(x, a), null, IsAudioSelected(x, a))).ToList()));
        }
        return items;
    }

    private void SetSubs(QuickStream.Extras x, QuickStream.CaptionTrack? t, string? translateTo)
    {
        var p = MpvPlayer.Instance;
        var sgen = ++_subGen;
        if (t == null)
        {
            _subKey = "";
            p.Command("set", "sid", "no");
            Shell.PushPlayerState();
            return;
        }
        var id = Video.VideoId;
        var key = t.Lang + (t.Auto ? "*" : "") + (translateTo != null ? ">" + translateTo : "");
        _ = Task.Run(async () =>
        {
            string? file = null;
            try
            {
                if (translateTo != null && x.Captions.FirstOrDefault(c => c.Lang.StartsWith(translateTo) && !c.Auto) is { } native)
                    file = await QuickStream.CaptionFileAsync(id, x, native, null);
                file ??= await QuickStream.CaptionFileAsync(id, x, t, translateTo);
                if (file == null && translateTo != null)
                    foreach (var other in x.Captions.Where(c => c != t).OrderBy(c => c.Auto).Take(2))
                        if ((file = await QuickStream.CaptionFileAsync(id, x, other, translateTo)) != null) break;
            }
            catch (Exception e) { Paths.Log("captions: " + e.Message); }
            App.Post(() =>
            {
                if (sgen != _subGen || PlaybackTracker.Instance.Current?.VideoId != id) return;
                if (file == null) { Shell.Toast(translateTo != null ? L.T("Перевод субтитров не получился, попробуйте ещё раз", "Subtitle translation failed, try again") : L.T("Субтитры не загрузились", "Failed to load subtitles"), Th.Warn); return; }
                _subKey = key;
                var trName = translateTo != null ? TranslateTo.First(l => l.Code == translateTo) : default;
                p.Command("sub-add", file.Replace('\\', '/'), "cached", translateTo != null ? L.T("Перевод: " + trName.Ru, "Translated: " + trName.En) : t.Name, translateTo ?? t.Lang);
                Shell.PushPlayerState();
            });
        });
    }

    private void SetAudio(QuickStream.Extras x, QuickStream.AudioTrack a)
    {
        var p = MpvPlayer.Instance;
        _audioId = a.Id;
        p.Command("audio-add", QuickStream.AudioUrl(x, a), "cached", a.Name, a.Lang);
        Shell.Toast(L.T("Озвучка: ", "Audio track: ") + a.Name, null, 2000);
    }

    /// <summary>Scripted check: "sub:en", "sub:en>ru", "audio:ru", joined with commas.</summary>
    private bool _testAsked;
    public void TestTracks(string spec)
    {
        var x = QuickStream.ExtrasOf(Video.VideoId);
        if (x == null && !_testAsked)
        {
            _testAsked = true;
            _ = QuickStream.EnsureSpecAsync(Video.VideoId).ContinueWith(_ => App.Post(() => TestTracks(spec)));
            return;
        }
        if (x == null) { Paths.Log("tracks test: no list"); return; }
        Paths.Log($"tracks test: {x.Captions.Count} subtitle tracks, {x.Audio.Count} audio tracks");
        foreach (var part in spec.Split(','))
        {
            var kv = part.Split(':');
            if (kv[0] == "sub")
            {
                var lt = kv[1].Split('>');
                var t = x.Captions.FirstOrDefault(c => c.Lang == lt[0] && !c.Auto) ?? x.Captions.FirstOrDefault(c => c.Lang == lt[0]);
                if (t != null) SetSubs(x, t, lt.Length > 1 ? lt[1] : null); else Paths.Log("tracks test: no subtitle " + lt[0]);
            }
            else if (kv[0] == "audio")
            {
                var a = x.Audio.FirstOrDefault(c => c.Lang == kv[1]);
                if (a != null) SetAudio(x, a); else Paths.Log("tracks test: no audio " + kv[1]);
            }
        }
    }

    public void MoreMenu()
    {
        var items = Cards.MenuFor(Full);
        var top = new List<MenuItem>
        {
            new(L.T("Поделиться…", "Share…"), OpenShare, icon: Th.Share),
            new(L.T("Копировать ссылку с текущего момента", "Copy link at current time"), () => Cards.Copy(SharePanel.LinkFor(Video.VideoId, MpvPlayer.Instance.TimePos)), icon: Th.Link, shortcut: "Ctrl+Shift+C"),
            new(L.T("Начать сначала", "Restart from beginning"), () => MpvPlayer.Instance.SeekAbsolute(0), icon: Th.Restart),
        };
        if (!PlaybackTracker.Instance.IsLocal && QuickStream.ExtrasOf(Video.VideoId) is { } tracks)
            top.Add(new(L.T("Субтитры и озвучка", "Subtitles & audio"), null, TrackItems(tracks), icon: Th.Subtitle));
        else top.Add(new(L.T("Субтитры и озвучка…", "Subtitles & audio…"), TracksMenu, icon: Th.Subtitle));
        top.Add(MenuItem.Sep);
        items.InsertRange(0, top);
        Menus.Popup(items);
    }
}
