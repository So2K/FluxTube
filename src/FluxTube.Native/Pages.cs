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
        {
            Gfx.Image(img.Bmp!, r, a);
            Gfx.RoundOff(r, radius, behind ?? Th.Bg);
        }
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
        "best" => "Максимальное",
        "audio" => "Только аудио (m4a)",
        "2160" => "4K (2160p)",
        "1440" => "2K (1440p)",
        _ => q + "p",
    };

    public static void Download(VideoItem v, string? quality)
    {
        Downloader.Instance.Enqueue(v.Url, v.Title, v.Channel, v.VideoId, quality);
        Shell.Toast("Скачивается: " + v.Title, Th.Good);
    }

    public static void CopyText(string text, string what = "Текст скопирован")
    {
        if (string.IsNullOrEmpty(text)) return;
        W.SetClipboardText(App.Hwnd, text);
        Shell.Toast(what, Th.Good, 1800);
    }

    /// <summary>Right-click on a piece of text: a one-item menu that copies it.</summary>
    public static void CopyMenu(R r, string text, string label = "Копировать текст")
    {
        if (text.Length > 0 && UI.RightClick(r)) Menus.Popup([new(label, () => CopyText(text), icon: Th.Copy, shortcut: "Ctrl+C")]);
    }

    public static void Copy(string text)
    {
        W.SetClipboardText(App.Hwnd, text);
        Shell.Toast("Ссылка скопирована");
    }

    public static List<MenuItem> MenuFor(VideoItem v, Action? onRemove = null)
    {
        var items = new List<MenuItem>
        {
            new("Открыть в новой вкладке", () => Shell.OpenVideoInNewTab(v), icon: Th.Tab, shortcut: "СКМ"),
            new("Запустить в фоне (звук)", () => { Shell.OpenVideoInNewTab(v); BgPlayers.Play(v.VideoId); }, icon: Th.Headphones),
            MenuItem.Sep,
            new("Скачать (" + QualityName(AppSettings.Current.DownloadQuality) + ")", () => Download(v, null), icon: Th.Download),
            new("Скачать в качестве", null, AppSettings.Qualities.Select(q => new MenuItem(QualityName(q), () => Download(v, q), icon: Th.Quality)).ToList(), icon: Th.Quality),
        };
        if (CookieStore.IsLoggedIn)
        {
            items.Add(MenuItem.Sep);
            items.Add(new("Смотреть позже", () => SaveTo("WL", "Смотреть позже", v, true), icon: Th.Later));
            items.Add(new("Сохранить в плейлист…", () => PlaylistMenu(v), icon: Th.Playlist));
        }
        items.Add(MenuItem.Sep);
        if (!string.IsNullOrEmpty(v.ChannelId))
            items.Add(new("Открыть канал: " + v.Channel, () => Shell.OpenChannel(v.ChannelId!, v.Channel), icon: Th.Account));
        items.Add(new("Копировать ссылку", () => Copy(v.Url), icon: Th.Link));
        items.Add(new("Копировать название", () => CopyText(v.Title, "Название скопировано"), icon: Th.Copy));
        items.Add(new("Открыть в браузере", () => W.Open(v.Url), icon: Th.Globe));
        if (!string.IsNullOrEmpty(v.ChannelId))
        {
            items.Add(MenuItem.Sep);
            items.Add(new("Не показывать канал: " + v.Channel, () =>
            {
                var s = AppSettings.Current;
                if (!s.BlockedChannels.Contains(v.ChannelId)) { s.BlockedChannels = [.. s.BlockedChannels, v.ChannelId!]; s.Save(); }
                Shell.Toast($"Канал «{v.Channel}» скрыт из поиска и рекомендаций. Вернуть: Настройки, раздел «Поиск».", null, 6000);
                Shell.Current?.Refresh();
            }, icon: Th.Block, danger: true));
        }
        if (onRemove != null) { items.Add(MenuItem.Sep); items.Add(new("Удалить из списка", onRemove, icon: Th.Delete, danger: true)); }
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
        var glow = UI.Glow(id, card.Inset(-7, -7), 14, over);
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
            Gfx.Fill(new R(x + 16, y - 6, cardW - 32, 8), Th.Track, 6);
            Gfx.Fill(new R(x + 8, y - 3, cardW - 16, 8), Gfx.Mix(Th.Track, Th.Card, 0.5f), 7);
        }
        var thumbR = new R(x, y, cardW, thumbH);
        var behind = UI.GlowOn(Th.Bg, glow);
        bool wantPreview = over && v.PlaylistId == null && !v.IsLive && v.VideoId.Length == 11;
        if (wantPreview)
        {
            if (_previewCardId != id) { _previewCardId = id; _previewSince = UI.Now; }
            double hSec = (UI.Now - _previewSince) / 1000.0;
            if (hSec < 0.38) UI.Animate();
            if (hSec >= 0.35)
            {
                // Real hardware-decoded muted HD live video preview (hq720.jpg stays underneath until 1st frame renders!)
                PreviewPlayer.Want(v.VideoId, thumbR, Gfx.Clip, 12);
                bool liveReady = PreviewPlayer.IsReadyFor(v.VideoId);
                Thumb(thumbR, v, true, 12, badge: !liveReady, behind: behind);
                if (!liveReady)
                {
                    var pr = Gfx.Text("HD", 10.5f, 700);
                    var pb = new R(thumbR.X + 8, thumbR.Y + 8, pr.W + 12, pr.H + 4);
                    Gfx.Fill(pb, Gfx.Fade(0xFF0A0A0E, 0.72f), 5);
                    Gfx.Draw(pr, pb.X + 6, pb.Y + 2, 0xFFFFFFFF);
                }
            }
            else Thumb(thumbR, v, true, 12, behind: behind);
        }
        else
        {
            if (_previewCardId == id && !over) _previewCardId = 0;
            Thumb(thumbR, v, true, 12, behind: behind);
        }

        float tx = x, tw = cardW;
        if (v.AvatarUrl != null) { Avatar(x, y + thumbH + 11, 34, v.AvatarUrl, v.Channel); tx += 44; tw -= 44; }
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
            overChannel = overName || (v.AvatarUrl != null && UI.Hover(avRect));
            if (overChannel)
            {
                Gfx.Fill(new R(tx, cy + name.H - 1.5f, name.W, 1), Th.Text);
                if (UI.Click(UI.Id(id, 9), overName ? chRect : avRect, playSfx: false)) App.Post(() => Shell.OpenChannel(v.ChannelId!, v.Channel));
            }
        }

        if (v.PlaylistId != null)
        {
            if (UI.Click(id, card)) App.Post(() => Shell.OpenPlaylist(v.PlaylistId, v.Title));
            return;
        }
        if (over) hover.Over(v);
        if (UI.MiddleClick(card)) Shell.OpenVideoInNewTab(v);
        else if (overChannel) { }
        else if (UI.Click(id, card, playSfx: false)) App.Post(() => Shell.OpenVideo(v, source));
        else if (UI.RightClick(card)) Menus.Popup(MenuFor(v));
    }

    /// <summary>Adds the video to one of the account's playlists or takes it out.</summary>
    public static void SaveTo(string playlistId, string playlistTitle, VideoItem v, bool add)
    {
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await InnerTube.Instance.PlaylistEditAsync(playlistId, v.VideoId, add); }
            catch (Exception e) { Paths.Log("playlist edit: " + e.Message); ok = false; }
            App.Post(() => Shell.Toast(!ok ? "YouTube не принял изменение плейлиста" : add ? $"Добавлено в «{playlistTitle}»" : $"Убрано из «{playlistTitle}»", ok ? Th.Good : Th.Warn));
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
                if (lists.Count == 0) { Shell.Toast("Плейлисты не загрузились", Th.Warn); return; }
                Menus.Popup(lists.Select(p => new MenuItem(p.Id == "WL" ? "Смотреть позже" : p.Title, () => SaveTo(p.Id, p.Id == "WL" ? "Смотреть позже" : p.Title, v, !p.Contains), null, p.Contains)).ToList());
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
        "home" => "Главная", "subs" => "Подписки", "wl" => "Смотреть позже", "liked" => "Понравившиеся",
        "search" => $"«{arg}»", "channel" => title ?? "Канал", "playlist" => title ?? "Плейлист", _ => kind,
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

    private static readonly string[] SortNames = ["По релевантности", "По рейтингу", "Сначала новые", "По просмотрам", "Сначала старые"];

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
    private static readonly string[] DateNames = ["За всё время", "За час", "Сегодня", "За неделю", "За месяц", "За год"];
    private static readonly string[] TypeNames = ["Всё", "Видео", "Каналы", "Плейлисты"];
    private static readonly (int Code, string Name)[] DurNames = [(0, "Любая длина"), (1, "До 4 минут"), (3, "4–20 минут"), (2, "Больше 20 минут")];

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
    private static readonly (string Title, string[] MatchKeys, string FallbackToken)[] Top5HomeSpecs =
    [
        ("Рекомендации", ["все", "all", "рекомендац"], ""),
        ("Музыка", ["музык", "music"], "search:музыка"),
        ("Видеоигры", ["видеоигр", "игр", "gaming", "games"], "search:видеоигры"),
        ("Новое для вас", ["новое для вас", "new to you", "недавно опубликован", "recently uploaded"], "search:новые видео"),
        ("Подкасты", ["подкаст", "podcast", "прямой эфир", "live", "в тренде"], "search:подкасты"),
    ];

    private static List<(string Title, string Token, bool Selected)> BuildTop5HomeChips(List<(string Title, string Token, bool Selected)> raw)
    {
        var list = new List<(string Title, string Token, bool Selected)>(5);
        for (int s = 0; s < Top5HomeSpecs.Length; s++)
        {
            var (title, keys, fallback) = Top5HomeSpecs[s];
            string tok = fallback;
            if (s == 0) tok = ""; // Рекомендации always loads the exact 1-to-1 personalized Home feed
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
            list.Add((title, tok, s == 0));
        }
        return list;
    }

    private void SelectChip(int idx)
    {
        if (idx < 0 || idx >= _chips.Count || idx == _chipIdx) return;
        PreviewPlayer.HideNow();

        // Snapshot current category into memory cache so switching back is 0ms instant!
        if (_items.Count > 0 && !_chipCache.ContainsKey(_chipIdx))
        {
            var snap = new FeedPage_ { Continuation = _cont };
            snap.Items.AddRange(_items);
            _chipCache[_chipIdx] = snap;
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
            Replace(cached, saveCache: false);
            App.Invalidate();
            return;
        }

        // Immediate visual switch: clear old cards right away so skeleton appears on click frame 0!
        _items = [];
        _ids.Clear();
        _cont = null;
        UI.ScrollReset(_scroll);
        _loading = true;
        App.Invalidate();

        var fallbackQuery = idx < Top5HomeSpecs.Length && Top5HomeSpecs[idx].FallbackToken.StartsWith("search:", StringComparison.Ordinal)
            ? Top5HomeSpecs[idx].FallbackToken[7..]
            : chip.Title;

        _ = Task.Run(async () =>
        {
            try
            {
                var yt = InnerTube.Instance;
                FeedPage_ page;
                if (string.IsNullOrEmpty(chip.Token))
                    page = await yt.HomeAsync();
                else if (chip.Token.StartsWith("search:", StringComparison.Ordinal))
                    page = await yt.SearchAsync(chip.Token[7..]);
                else
                {
                    try { page = await yt.ContinueAsync("browse", chip.Token); }
                    catch { page = new FeedPage_(); }
                    // Auto-fallback to search if YouTube continuation token expired or returned 0 items
                    if (page.Items.Count == 0 && !string.IsNullOrWhiteSpace(fallbackQuery))
                        page = await yt.SearchAsync(fallbackQuery);
                }
                Cards.WithProgress(page);
                App.Post(() =>
                {
                    if (gen != _gen) return;
                    _loading = false;
                    if (page.Items.Count > 0) _chipCache[idx] = page;
                    Replace(page, saveCache: idx == 0);
                    if (_items.Count == 0) Say("По этой категории пока ничего не нашлось.", Th.Sub);
                });
            }
            catch (Exception e)
            {
                Paths.Log($"home chip {chip.Title}: {e.Message}");
                App.Post(() => { if (gen == _gen) { _loading = false; Say("Не удалось загрузить категорию: " + e.Message, Th.Brand); } });
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
        for (int i = 0; i < _chips.Count; i++)
        {
            widths[i] = Gfx.Text(_chips[i].Title, 13, 600).W + 26;
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
                if (active) Gfx.Fill(r, Th.Text, 8);
                else Gfx.Fill(r, Th.A(over ? 0.16f : 0.085f), 8);
                var tr = Gfx.Text(_chips[i].Title, 13, active ? 600 : 500);
                Gfx.Draw(tr, Gfx.Snap(r.X + (r.W - tr.W) / 2), Gfx.Snap(r.Y + (r.H - tr.H) / 2), active ? Th.Bg : Th.Text);
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
            Say("Войдите в аккаунт YouTube (кнопка справа сверху), чтобы видеть этот раздел.", Th.Sub);
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
                        ? "YouTube не даёт рекомендации без входа и истории. Войдите в аккаунт или найдите что-нибудь через поиск."
                        : "Вы не вошли: лента собрана из похожих на ваши последние просмотры.";
                }
                Cards.WithProgress(page);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false); // the parsed JSON tree is garbage now
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
                            Say("Лента обновилась. Нажмите «Обновить», чтобы показать новую.", Th.Sub);
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
                    else if (_items.Count == 0 && _cont == null) Say("Здесь пока пусто.", Th.Sub);
                });
            }
            catch (Exception e)
            {
                Paths.Log($"feed {kind}: {e}");
                App.Post(() => { if (gen == _gen) { _loading = false; Say("Не удалось загрузить: " + e.Message, Th.Brand); } });
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
        var durIdx = Array.FindIndex(DurNames, d => d.Code == _sDur);
        Choice("dur", DurNames[durIdx].Name, DurNames.Select(d => d.Name).ToArray(), durIdx, i => _sDur = DurNames[i].Code);
        Choice("sort", SortNames[_sSort], SortNames, _sSort, i => _sSort = i);
        // our own check on top of YouTube's answer
        if (UI.Pill(UI.Id("sf:strict", _scroll), cx, y, 32, _strict ? Th.Check : null, "Только по теме", out var sw, _strict ? Gfx.Fade(Th.Brand, 0.85f) : Th.A(0.08f), _strict ? Th.OnBrand : Th.Text))
        {
            _strict = !_strict;
            AppSettings.Current.SearchStrict = _strict;
            AppSettings.Current.Save();
            Refilter();
        }
        cx += sw + 12;
        if (_strict && _hidden > 0) Gfx.Draw($"скрыто не по теме: {_hidden}", cx, y + 8, 12.5f, Th.Dim);
        return 44;
    }

    private void LogSeen(VideoItem v) { var src = Source; ThreadPool.QueueUserWorkItem(_ => Db.Seen(v, src)); }

    private void LoadMore()
    {
        if (_cont == null || _loadingMore) return;
        _loadingMore = true;
        var gen = _gen;
        var tok = _cont;
        var endpoint = kind == "search" ? "search" : "browse";
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
                    if (_items.Count == 0 && _cont == null) Say(kind == "search" && _strict ? "По теме ничего не нашлось. Выключите «Только по теме», чтобы увидеть всё, что отдал YouTube." : "Здесь пока пусто.", Th.Sub);
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
        var rw = UI.PillWidth(Th.Refresh, "Обновить");
        float rightBtnX = r.Right - rw - 16;
        if (UI.Pill(UI.Id("refresh", _scroll), rightBtnX, r.Y + 8, 32, Th.Refresh, "Обновить", out _, Th.A(0.08f))) App.Post(Refresh);
        if (_loading && _items.Count > 0) UI.Spinner(rightBtnX - 24, r.Y + 24, 8, Th.Sub);

        // Compact inline notice chip right in the header row (saves an entire 52px banner row!)
        if (_message != null && _messageInline && !_messageDismissed)
        {
            var shortText = !CookieStore.IsLoggedIn && kind == "home" ? "Гостевая лента по вашим просмотрам" : _message;
            float chipX = x0 + headRun.W + 16;
            float maxChipW = rightBtnX - (_loading ? 42 : 14) - chipX;
            if (maxChipW > 180)
            {
                bool showLogin = !CookieStore.IsLoggedIn && kind == "home";
                var loginRun = showLogin ? Gfx.Text("Войти", 12, 600) : null;
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
            if (UI.Pill(UI.Id("pl:play", _scroll), x0, top, 36, Th.Play, "Воспроизвести все", out var pw, accent: true))
            {
                var firstV = _items[0]; var pid = arg[2..];
                App.Post(() => Shell.OpenVideo(firstV, "playlist:" + pid, pid));
            }
            Gfx.Draw($"{_items.Count}{(_cont != null ? "+" : "")} видео", x0 + pw + 14, top + 9, 13, Th.Sub);
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

    public string Tag => "";
    public int MainScroll => _infoScroll; // Page Up/Down read the description and comments

    private readonly string? _playlistId;
    private PlaylistPanel? _list;
    private readonly int _listScroll;
    private bool _listJump;
    public string? PlaylistId => _playlistId;

    public WatchPage(VideoItem v, string source, string? playlistId = null)
    {
        Video = v;
        _source = source;
        _playlistId = playlistId;
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
        if (!CookieStore.IsLoggedIn) { Shell.Toast("Чтобы ставить оценки, войдите в аккаунт YouTube", Th.Warn); return; }
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
                if (!ok) { _rating = before; Shell.Toast("YouTube не принял оценку, попробуйте позже", Th.Warn); }
                else Shell.Toast(action switch { "like" => "Вам понравилось это видео", "dislike" => "Вам не понравилось это видео", _ => "Оценка снята" }, Th.Good, 2000);
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
    public string Speed => _speed;
    public void ToggleTheater() { AppSettings.Current.TheaterMode = !AppSettings.Current.TheaterMode; AppSettings.Current.Save(); App.Invalidate(); }

    private int _infoGen;
    private int _relPrewarmed;

    public void Shown()
    {
        if (PlaybackTracker.Instance.Current?.VideoId != Video.VideoId || !MpvPlayer.Instance.HasMedia)
        {
            Shell.Play(Video, _source);
            _subKey = ""; _audioId = "";
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
                App.Post(() => { if (gen != _infoGen) return; _infoLoading = false; _note = "Описание и похожие не загрузились: " + e.Message; App.Invalidate(); });
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
        if (!CookieStore.IsLoggedIn) { Shell.Toast("Войдите в аккаунт YouTube, чтобы оставлять комментарии", Th.Warn); return; }
        var createParams = _createCommentParams ?? _info?.CreateCommentParams;
        if (string.IsNullOrEmpty(createParams))
        {
            Shell.Toast("Токен отправки ещё загружается — подождите секунду и попробуйте снова", Th.Warn);
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
                if (!ok) { Shell.Toast("Не удалось отправить комментарий: " + (err ?? "ошибка YouTube"), Th.Warn, 5000); App.Invalidate(); return; }
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
                            Author = acc?.Handle is { Length: > 0 } h ? h : (acc?.Name is { Length: > 0 } nm ? nm : "Вы"),
                            Avatar = acc?.Photo,
                            Text = text,
                            When = "только что",
                            Likes = "0",
                        },
                    });
                }
                Shell.Toast("Комментарий опубликован", Th.Good, 2400);
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
        if (_info == null) { _commentEdit?.Hide(); return y; }
        var count = _commentCount ?? _info.CommentsCount;
        if (_info.CommentsToken == null)
        {
            _commentEdit?.Hide();
            Gfx.Draw("Комментарии отключены", x0, y, 14, Th.Sub);
            return y + 40;
        }
        var head = count is { Length: > 0 } ? (count.Any(char.IsLetter) ? count : "Комментарии: " + count) : "Комментарии";
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
                    _commentEdit = new Edit("Введите комментарий…") { Bg = () => Th.Field };
                    _commentEdit.Enter = SubmitComment;
                }
                var acc = AccountMenu.Current;
                Cards.Avatar(x0, y + 2, 36, acc?.Photo, acc?.Name is { Length: > 0 } nm ? nm : "?");
                var sendLbl = _postingComment ? "Отправка…" : "Отправить";
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
                Gfx.Draw("Войдите в аккаунт YouTube, чтобы оставлять комментарии", x0 + 14, y + 10, 13, Th.Sub, 400, w - 120);
                var lw = UI.PillWidth(Th.Account, "Войти");
                if (UI.Pill(UI.Id("clogin", _infoScroll), hintR.Right - lw - 8, y + 4, 30, Th.Account, "Войти", out _, accent: true))
                    App.Post(Shell.LoginNewAccount);
            }
            y += 48;
        }

        if (!_commentsStarted && y < view.Bottom + 600) LoadComments();
        if (_commentsOff) { Gfx.Draw("Не удалось загрузить комментарии.", x0, y, 13.5f, Th.Sub); return y + 34; }

        foreach (var row in _comments)
        {
            y = DrawRow(row, x0, y, w, view, 40);
            if (!row.Shown) continue;
            foreach (var rep in row.Replies) y = DrawRow(rep, x0 + 56, y, w - 56, view, 28);
            if (row.Loading) { UI.Spinner(x0 + 56 + 12, y + 12, 7, Th.Sub); y += 34; }
            else if (row.More != null)
            {
                if (y > view.Y - 40 && y < view.Bottom + 40 && UI.Pill(UI.Id(_infoScroll, 60000 + row.RowId), x0 + 56, y, 30, null, "Ещё ответы", out _, Th.A(0.08f))) LoadReplies(row);
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

        Cards.Avatar(x0, y, avatar, c.Avatar, c.Author.TrimStart('@'));
        if (UI.RightClick(new R(x0, y, w, h - 8)))
            Sel.Popup([new("Копировать комментарий", () => Cards.CopyText(c.Text, "Комментарий скопирован"), icon: Th.Copy),
                       new("Копировать с автором", () => Cards.CopyText(c.Author + ": " + c.Text, "Комментарий скопирован"), icon: Th.Comment),
                       new("Копировать ник", () => Cards.CopyText(c.Author, "Ник скопирован"), icon: Th.Account)]);
        float tx = x0 + avatar + 16;
        var au = Gfx.Text(c.Author, 13, 600);
        if (c.ByOwner) { var b = new R(tx - 6, y - 2, au.W + 12, au.H + 4); Gfx.Fill(b, Th.A(0.16f), b.H / 2); }
        Gfx.Draw(au, tx, y, Th.Text);
        var wx = tx + au.W + 10;
        if (c.Pinned) { Gfx.Draw("закреплён", wx, y + 1, 12, Th.Sub); wx += Gfx.Text("закреплён", 12).W + 8; }
        Gfx.Draw(c.When, wx, y + 1, 12, Th.Dim);
        var ty = y + 22;
        Sel.Text(UI.Id(_infoScroll, 20000 + i), c.Text, tx, ty, 14, Th.Text, 400, tw, row.Open ? 0 : 5, links: true, onLink: OnLink);
        ty += text.H;
        if (longText)
        {
            var more = Gfx.Text(row.Open ? "Свернуть" : "Читать дальше", 13, 600);
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
            var pr = new R(tx + 22 + likes.W + 14, ty - 4, UI.PillWidth(null, "Ответы: " + c.Replies) + 22, 30);
            var pid = UI.Id(_infoScroll, 50000 + i);
            UI.Glow(pid, pr, 15, UI.Hover(pr));
            Gfx.Icon(row.Shown ? "\uE70E" : "\uE70D", new R(pr.X + 8, pr.Y, 18, 30), 10, Sel.LinkColor);
            Gfx.Draw(c.Replies is { Length: > 0 } r2 && r2 != "0" ? "Ответы: " + r2 : "Ответы", pr.X + 30, pr.Y + 6, 13, Sel.LinkColor, 600);
            if (UI.Click(pid, pr)) { row.Shown = !row.Shown; if (row.Shown && !row.RepliesLoaded) LoadReplies(row); }
        }
        return y + h;
    }

    private float _actionExtraH;

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

        // Keep the rating control visually consistent with the neighboring action pills.
        Gfx.Fill(pill, Th.Card, h / 2);

        uint likeC = isLike || overL ? Th.Brand : Th.Text;
        uint disC = isDis || overD ? Th.Brand : Th.Text;
        Gfx.Icon(Th.Like, new R(likeR.X + 14, y, 18, h), 15.5f, likeC);
        Gfx.Draw(lr, likeR.X + 39, y + (h - lr.H) / 2, likeC);
        Gfx.Fill(new R(x + leftW, y + 9, 1, h - 18), Th.Line);
        Gfx.Icon(Th.Dislike, new R(disR.X + 12, y, 18, h), 15.5f, disC);
        Gfx.Draw(dr, disR.X + 37, y + (h - dr.H) / 2, disC);

        if (UI.Click(likeId, likeR, playSfx: false)) Rate(true);
        if (UI.Click(disId, disR, playSfx: false)) Rate(false);
    }

    public void Draw(R r)
    {
        var savedModal = UI.Modal;
        if (SharePanel.Open) UI.Modal = new R(-1e6f, -1e6f, 0, 0);
        const float padX = 20, topPad = 10;
        float x0 = Gfx.Snap(r.X + padX);
        float mainW = Gfx.Snap(r.W - padX * 2);
        float maxVideoH = MathF.Max(360, r.H * (AppSettings.Current.TheaterMode ? 1.45f : 1.08f));
        float videoW = Gfx.Snap(MathF.Min(mainW, maxVideoH * 16 / 9));
        float videoH = MathF.Round(videoW * 9 / 16);
        float videoX = Gfx.Snap(x0 + (mainW - videoW) / 2);
        var id = Video.VideoId;

        void DrawVideoBox(R video, R? clip)
        {
            Ambient.Draw(video, id, isShort: false);
            Gfx.Fill(video, 0xFF0A0A0E, 12);
            Shell.WantVideo(video, id, clip, radius: 12);
            if (Pip.Active && PlaybackTracker.Instance.Current?.VideoId == id)
            {
                Gfx.Fill(video, 0xFF0A0A0C, 12);
                var img = Images.Get(Video.Thumb720, 640, 360);
                var pw = MathF.Min(video.W, video.H * 16 / 9);
                var pr = new R(video.X + (video.W - pw) / 2, video.Y + (video.H - pw * 9 / 16) / 2, pw, pw * 9 / 16);
                if (img != null) { Gfx.Image(img.Bmp!, pr, 0.25f * Images.Alpha(img)); Gfx.RoundOff(video, 12, Th.Bg); }
                var msg = Gfx.Text("Видео в отдельном окне", 18, 600);
                Gfx.Draw(msg, video.X + (video.W - msg.W) / 2, video.Y + video.H / 2 - 40, 0xFFFFFFFF);
                var bw = UI.PillWidth(Th.Mini, "Вернуть сюда");
                if (UI.Pill(UI.Id("pipback", _infoScroll), video.X + (video.W - bw) / 2, video.Y + video.H / 2, 38, Th.Mini, "Вернуть сюда", out _, accent: true)) App.Post(Pip.Exit);
            }
            else if (!Shell.VideoShown(id))
            {
                var pw = MathF.Min(video.W, video.H * 16 / 9);
                var pr = new R(video.X + (video.W - pw) / 2, video.Y + (video.H - pw * 9 / 16) / 2, pw, pw * 9 / 16);
                var img = Images.Get(Video.Thumb720, 640, 360);
                if (img != null) { Gfx.Image(img.Bmp!, pr, 0.55f * Images.Alpha(img)); Gfx.RoundOff(video, 12, Th.Bg); }
                UI.Spinner(video.X + video.W / 2, video.Y + video.H / 2, 16, 0xFFFFFFFF);
            }
        }

        // ---- info column ----
        var titleRun = Gfx.Text(Title, 20, 600, mainW, 3);
        var desc = _info?.Description ?? "";
        var descRun = desc.Length > 0 ? Gfx.Text(desc, 13.5f, 400, mainW - 32, _descOpen ? 0 : 4) : null;
        var canToggle = desc.Length > 280 || desc.Count(c => c == '\n') > 3;
        float descBoxH = 14 + 20 + (descRun != null ? descRun.H + 6 : 0) + (canToggle ? 24 : 0) + 12;
        var listInline = _list != null;
        int relatedCols = mainW >= 1440 ? 3 : mainW >= 900 ? 2 : 1;
        float relatedH = 52 + (int)Math.Ceiling(_related.Count / (double)relatedCols) * 112 + (_relCont != null ? 54 : 16);
        float infoContentH = 14 + titleRun.H + 14 + 44 + _actionExtraH + 14 + descBoxH + (_note != null ? 40 : 0) + relatedH + 30 + _commentsH + (listInline ? _listInlineH : 0);

        var view = new R(x0, r.Y, mainW + 12, r.H);
        var scroll = UI.BeginScroll(_infoScroll, view, topPad + videoH + infoContentH);
        var video = new R(videoX, Gfx.Snap(view.Y + topPad - scroll), videoW, videoH);
        DrawVideoBox(video, view);
        float y = video.Bottom + 16;
        if (Title.Length > 0) Sel.Text(UI.Id(_infoScroll, 8001), Title, x0, y, 20, Th.Text, 600, mainW, 3);
        Cards.CopyMenu(new R(x0, y, titleRun.W, titleRun.H), Title, "Копировать название");
        y += titleRun.H + 14;

        // channel + upgraded Like/Dislike pill + actions (wraps cleanly onto 2 rows if narrow so Like/Dislike NEVER vanishes!)
        var chName = _info?.Channel ?? Video.Channel;
        var subText = _info?.Subscribers ?? "";
        float bx = x0 + mainW, by = y + 4;
        const float ph = 36;
        bx -= 40;
        if (UI.IconButton(UI.Id("theater", _infoScroll), new R(bx, by, 36, 36), Th.Theater, 16, AppSettings.Current.TheaterMode ? Th.Brand : Th.Text)) ToggleTheater();
        bx -= 42;
        if (UI.IconButton(UI.Id("pip", _infoScroll), new R(bx, by, 36, 36), "\uE8A7", 16, Pip.Active ? Th.Brand : Th.Text)) App.Post(Pip.Toggle);
        bx -= 42;
        if (UI.IconButton(UI.Id("more", _infoScroll), new R(bx, by, 36, 36), Th.More)) MoreMenu();
        bx -= UI.PillWidth(Th.Share, "Поделиться") + 8;
        if (UI.Pill(UI.Id("share", _infoScroll), bx, by, ph, Th.Share, "Поделиться", out _)) OpenShare();
        var s = AppSettings.Current;
        var ql = Cards.QualityName(s.PlayerQuality);
        bx -= UI.PillWidth(null, ql) + 8;
        if (UI.Pill(UI.Id("q", _infoScroll), bx, by, ph, null, ql, out _)) QualityMenu();
        bx -= UI.PillWidth(null, _speed) + 8;
        if (UI.Pill(UI.Id("sp", _infoScroll), bx, by, ph, null, _speed, out _)) SpeedMenu();
        if (UI.Now - _dlCheckedAt > 1000) { _dlCheckedAt = UI.Now; _isDownloaded = Downloader.Instance.IsDownloaded(id, out _); }
        var dl = _isDownloaded ? "Скачано" : "Скачать";
        bx -= UI.PillWidth(Th.Download, dl) + 8;
        if (UI.Pill(UI.Id("dl", _infoScroll), bx, by, ph, Th.Download, dl, out _)) DownloadMenu();

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
        Cards.Avatar(x0, y + 1, 42, _info?.ChannelAvatar, chName);
        var nameRun = Gfx.Text(chName, 15, 600, chMaxW);
        var nameRect = new R(x0 + 54, y + 2, nameRun.W, nameRun.H);
        Gfx.Draw(nameRun, nameRect.X, nameRect.Y, UI.Hover(nameRect) && (_info?.ChannelId ?? Video.ChannelId) != null ? Th.Brand : Th.Text);
        Cards.CopyMenu(nameRect, chName, "Копировать название канала");
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
            var used = DrawPlaylist(new R(x0, y, mainW + 14, 560));
            if (MathF.Abs(used - _listInlineH) > 0.5f) { _listInlineH = used; UI.Animate(); }
            y += used;
        }

        // description
        var box = new R(x0, y, mainW, descBoxH);
        Gfx.Fill(box, Th.A(0.07f), 12);
        if (desc.Length > 0 && UI.RightClick(box))
        {
            var url = Video.Url; var full = Title + "\n" + url + "\n\n" + desc;
            Sel.Popup([new("Копировать описание", () => Cards.CopyText(desc, "Описание скопировано"), icon: Th.Copy),
                       new("Копировать название, ссылку и описание", () => Cards.CopyText(full), icon: Th.Link)]);
        }
        var stats = _info == null ? (_infoLoading ? "Загрузка…" : "") : string.Join("  ·  ", new[] { _info.Views, _info.Date }.Where(t => !string.IsNullOrEmpty(t)));
        Gfx.Draw(stats, x0 + 16, y + 12, 13, Th.Text, 600, mainW - 32);
        float dy = y + 14 + 20;
        if (descRun != null)
        {
            Sel.Text(UI.Id(_infoScroll, 8002), desc, x0 + 16, dy, 13.5f, Gfx.Mix(Th.Text, Th.Sub, 0.15f), 400, mainW - 32, _descOpen ? 0 : 4, links: true, onLink: OnLink);
            dy += descRun.H + 6;
        }
        if (canToggle)
        {
            var tr = Gfx.Text(_descOpen ? "Свернуть" : "Ещё", 13, 600);
            var hit = new R(x0 + 16, dy, tr.W, tr.H + 4);
            Gfx.Draw(tr, hit.X, hit.Y, UI.Hover(hit) ? Th.Text : Th.Brand);
            if (UI.Click(UI.Id("desc", _infoScroll), hit)) _descOpen = !_descOpen;
        }
        y += descBoxH + 10;
        if (_note != null) { Gfx.Draw(_note, x0, y, 13, Th.Warn, 400, mainW, 2); y += 40; }
        y = DrawRelatedInline(x0, y + 14, mainW, view);
        var cy0 = y;
        y = DrawComments(x0, y + 14, mainW, view);
        var ch = y - cy0;
        if (MathF.Abs(ch - _commentsH) > 0.5f) { _commentsH = ch; UI.Animate(); }
        UI.EndScroll();

        // the share panel sits under the video, above everything else on the page
        if (SharePanel.Open)
        {
            var px = Gfx.Snap(x0 + (mainW - SharePanel.W) / 2);
            var py = Gfx.Snap(Math.Clamp(video.Bottom + 10, r.Y + 12, MathF.Max(r.Y + 12, r.Bottom - SharePanel.H - 14)));
            UI.Modal = savedModal;
            SharePanel.Draw(new R(px, py, SharePanel.W, SharePanel.H));
        }

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
        var sub = (cur >= 0 ? $"{cur + 1} из {items.Count}" : $"{items.Count} видео") + (_list.IsMix ? "  ·  микс YouTube" : "");
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
        Gfx.Draw("Похожие видео", x, y + 4, 18, Th.Text, 700);
        var settings = AppSettings.Current;
        var label = Gfx.Text("Автовоспроизведение", 12.5f);
        var toggle = new R(x + width - label.W - 60, y, label.W + 60, 30);
        Gfx.Draw(label, toggle.X, toggle.Y + (30 - label.H) / 2, Th.Sub);
        if (UI.Toggle(UI.Id("auto", _relScroll), toggle, settings.Autoplay))
        {
            settings.Autoplay = !settings.Autoplay;
            settings.Save();
        }
        y += 40;
        int cols = width >= 1440 ? 3 : width >= 900 ? 2 : 1;
        const float gap = 14, rowH = 112, thumbW = 168, thumbH = 94;
        float cardW = (width - gap * (cols - 1)) / cols;
        for (int i = 0; i < _related.Count; i++)
        {
            var v = _related[i];
            var row = new R(x + (i % cols) * (cardW + gap), y + (i / cols) * rowH, cardW, rowH - 8);
            if (row.Bottom < view.Y - 30 || row.Y > view.Bottom + 30) continue;
            var id = UI.Id(_relScroll, i);
            var over = UI.Hover(row);
            var glow = UI.Glow(id, row.Inset(-4), 9, over);
            Cards.Thumb(new R(row.X, row.Y + 2, thumbW, thumbH), v, false, 8, behind: UI.GlowOn(Th.Bg, glow));
            float tx = row.X + thumbW + 12, textW = row.W - thumbW - 12;
            var title = Gfx.Draw(v.Title, tx, row.Y + 2, 13.5f, Th.Text, 600, textW, 2);
            Gfx.Draw(v.Channel, tx, row.Y + title.H + 5, 12, Th.Sub, 400, textW, 1);
            Gfx.Draw(v.Meta, tx, row.Y + title.H + 25, 12, Th.Sub, 400, textW, 1);
            if (over) _hover.Over(v);
            if (UI.MiddleClick(row)) Shell.OpenVideoInNewTab(v);
            else if (UI.Click(id, row, playSfx: false)) OpenRelated(v);
            else if (UI.RightClick(row)) Menus.Popup(Cards.MenuFor(v));
            if (_seen.Add(v.VideoId)) LogSeen(v);
        }
        y += (int)Math.Ceiling(_related.Count / (double)cols) * rowH;
        if (_related.Count == 0 && _infoLoading) UI.Spinner(x + width / 2, y + 12, 9, Th.Sub);
        if (_relCont != null)
        {
            if (y < view.Bottom + 30 && UI.Pill(UI.Id("rel:more", _relScroll), x, y + 6, 34, Th.More, "Ещё похожие", out _)) LoadMoreRelated();
            if (_relLoadingMore) UI.Spinner(x + 160, y + 23, 8, Th.Sub);
            y += 54;
        }
        else y += 16;
        return y;
    }

    private void DrawRelated(R r)
    {
        Gfx.Draw("Похожие", r.X, r.Y + 6, 16, Th.Text, 600);
        var s = AppSettings.Current;
        var lab = Gfx.Text("Автовоспроизведение", 12.5f);
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
        var items = new List<MenuItem> { new("Скачать (" + Cards.QualityName(AppSettings.Current.DownloadQuality) + ")", () => Cards.Download(v, null), icon: Th.Download), MenuItem.Sep };
        items.AddRange(AppSettings.Qualities.Select(q => new MenuItem(Cards.QualityName(q), () => Cards.Download(v, q), icon: Th.Quality)));
        Menus.Popup(items);
    }

    public void SpeedMenu()
    {
        var items = new List<MenuItem> { MenuItem.Title("Скорость воспроизведения") };
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
        var items = new List<MenuItem> { MenuItem.Title("Качество видео") };
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

    private static readonly (string Code, string Name)[] TranslateTo = [("ru", "русский"), ("en", "английский"), ("uk", "украинский")];

    /// <summary>The CC button: subtitle languages, YouTube's machine translation, and the dubbed audio tracks.</summary>
    public void TracksMenu()
    {
        var id = Video.VideoId;
        if (PlaybackTracker.Instance.IsLocal) { Shell.Toast("У скачанного файла дорожки те, что в нём сохранены"); return; }
        var x = QuickStream.ExtrasOf(id);
        if (x == null)
        {
            Shell.Toast("Загружаю список дорожек…", null, 1500);
            _ = QuickStream.EnsureSpecAsync(id).ContinueWith(_ => App.Post(() =>
            {
                if (Shell.Current != this) return;
                if (QuickStream.ExtrasOf(id) != null) TracksMenu();
                else Shell.Toast("Список субтитров не загрузился", Th.Warn);
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
        if (x.Captions.Count == 0) items.Add(new("У этого видео нет субтитров"));
        else
        {
            items.Add(new("Субтитры выключены", () => SetSubs(x, null, null), null, _subKey.Length == 0));
            var hl = AppSettings.Current.Hl;
            foreach (var t in x.Captions.OrderByDescending(t => t.Lang.StartsWith(hl)).ThenBy(t => t.Auto).Take(14))
                items.Add(new(t.Name, () => SetSubs(x, t, null), null, _subKey == t.Lang + (t.Auto ? "*" : "")));
            if (x.Captions.Count > 14)
                items.Add(new("Другие языки", null, x.Captions.OrderBy(t => t.Name).Select(t => new MenuItem(t.Name, () => SetSubs(x, t, null), null, _subKey == t.Lang + (t.Auto ? "*" : ""))).ToList()));
            var from = x.Captions.FirstOrDefault(t => !t.Auto) ?? x.Captions[0];
            items.Add(new("Автоперевод", null, TranslateTo.Where(l => !from.Lang.StartsWith(l.Code))
                .Select(l => new MenuItem("На " + l.Name, () => SetSubs(x, from, l.Code), null, _subKey == from.Lang + (from.Auto ? "*" : "") + ">" + l.Code)).ToList()));
        }
        if (x.Audio.Count > 1)
        {
            items.Add(MenuItem.Sep);
            items.Add(new("Язык озвучки", null, x.Audio.Select(a => new MenuItem(a.Name, () => SetAudio(x, a), null, IsAudioSelected(x, a))).ToList()));
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
                if (file == null) { Shell.Toast(translateTo != null ? "Перевод субтитров не получился, попробуйте ещё раз" : "Субтитры не загрузились", Th.Warn); return; }
                _subKey = key;
                p.Command("sub-add", file.Replace('\\', '/'), "cached", translateTo != null ? "Перевод: " + TranslateTo.First(l => l.Code == translateTo).Name : t.Name, translateTo ?? t.Lang);
                Shell.PushPlayerState();
            });
        });
    }

    private void SetAudio(QuickStream.Extras x, QuickStream.AudioTrack a)
    {
        var p = MpvPlayer.Instance;
        _audioId = a.Id;
        p.Command("audio-add", QuickStream.AudioUrl(x, a), "cached", a.Name, a.Lang);
        Shell.Toast("Озвучка: " + a.Name, null, 2000);
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
            new("Поделиться…", OpenShare, icon: Th.Share),
            new("Копировать ссылку с текущего момента", () => Cards.Copy(SharePanel.LinkFor(Video.VideoId, MpvPlayer.Instance.TimePos)), icon: Th.Link, shortcut: "Ctrl+Shift+C"),
            new("Начать сначала", () => MpvPlayer.Instance.SeekAbsolute(0), icon: Th.Restart),
        };
        if (!PlaybackTracker.Instance.IsLocal && QuickStream.ExtrasOf(Video.VideoId) is { } tracks)
            top.Add(new("Субтитры и озвучка", null, TrackItems(tracks), icon: Th.Subtitle));
        else top.Add(new("Субтитры и озвучка…", TracksMenu, icon: Th.Subtitle));
        top.Add(MenuItem.Sep);
        items.InsertRange(0, top);
        Menus.Popup(items);
    }
}
