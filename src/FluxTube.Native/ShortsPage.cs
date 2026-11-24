using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube.Pages;

/// <summary>
/// Shorts: structured vertical player with instant prefetching (next 3 + prev 1),
/// unified playback & global volume deck (mouse wheel + drag, persisted across all Shorts),
/// balanced engagement & quick-action tiles, inline comments drawer, Up-Next preview card,
/// and full keyboard/context-menu support.
/// </summary>
public sealed class ShortsPage : IPage
{
    private readonly List<VideoItem> _items = [];
    private int _i;
    private string? _next;
    private bool _loading, _failed;
    private readonly bool _feed;
    private long _lastStep;
    private readonly HashSet<string> _asked = [];
    private readonly Dictionary<string, string> _rating = new();
    private readonly int _id = UI.Id("shorts");
    private readonly int _commScroll = UI.Id("shorts:comm");

    private int _slideFrom;
    private long _slideAt;
    private bool _loop = true;
    private bool _muted;
    private bool _dragVol, _dragSeek;
    private long _volSaveAt;

    // Inline comments state for the active Short
    private bool _showComments;
    private string? _commentsFor;
    private readonly List<Comment> _comments = [];
    private string? _commentTok, _commentCount;
    private bool _commentsLoading, _commentsOff;
    private int _commentGen;

    public string Tag => _feed ? "shorts" : "";
    public int MainScroll => _showComments ? _commScroll : _id;

    /// <summary>YouTube's Shorts feed.</summary>
    public ShortsPage() { _feed = true; }

    /// <summary>A fixed list, starting at one of its items.</summary>
    public ShortsPage(List<VideoItem> items, int index)
    {
        _items.AddRange(items);
        _i = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
    }

    public string? CurrentId => Cur?.VideoId;
    private VideoItem? Cur => _i >= 0 && _i < _items.Count ? _items[_i] : null;
    private VideoItem? NextItem => _i + 1 >= 0 && _i + 1 < _items.Count ? _items[_i + 1] : null;

    public void Shown()
    {
        if (_items.Count == 0) { if (_feed) LoadMore(); }
        else PlayCurrent();
    }

    /// <summary>Leaving the page ends the short: it would otherwise loop in the bar at the bottom forever.</summary>
    public void Hidden()
    {
        FlushVolume();
        if (PlaybackTracker.Instance.CurrentSource == "shorts") { PlaybackTracker.Instance.Stop(); Shell.VideoReady = false; }
    }

    public void Refresh()
    {
        if (!_feed) return;
        _items.Clear(); _i = 0; _next = null; _failed = false;
        ResetComments();
        LoadMore();
    }

    public void ToggleLike()
    {
        if (Cur is { } v) Rate(v, true);
    }

    public void ToggleComments()
    {
        if (AppSettings.Current.ShortsPanelHidden)
        {
            AppSettings.Current.ShortsPanelHidden = false;
            AppSettings.Current.Save();
            _showComments = true;
        }
        else _showComments = !_showComments;
        if (_showComments && Cur is { } v && _commentsFor != v.VideoId) StartComments(v.VideoId);
        App.Invalidate();
    }

    public void TogglePanel()
    {
        AppSettings.Current.ShortsPanelHidden = !AppSettings.Current.ShortsPanelHidden;
        AppSettings.Current.Save();
        App.Invalidate();
    }

    /// <summary>Global volume for all Shorts (persisted in AppSettings.Volume).</summary>
    public void SetVolume(int vol)
    {
        vol = Math.Clamp(vol, 0, 100);
        AppSettings.Current.Volume = vol;
        if (vol > 0 && _muted)
        {
            _muted = false;
            MpvPlayer.Instance.Command("set", "mute", "no");
        }
        MpvPlayer.Instance.Command("set", "volume", vol.ToString());
        MpvPlayer.Instance.ShowText($"Громкость: {vol}%", 900);
        _volSaveAt = Environment.TickCount64 + 400;
        App.Invalidate();
    }

    public void AdjustVolume(int delta) => SetVolume(AppSettings.Current.Volume + delta);

    private void FlushVolume()
    {
        if (_volSaveAt > 0) { _volSaveAt = 0; AppSettings.Current.Save(); }
    }

    private void ResetComments()
    {
        _commentGen++;
        _commentsFor = null;
        _comments.Clear();
        _commentTok = null;
        _commentCount = null;
        _commentsLoading = false;
        _commentsOff = false;
        UI.ScrollReset(_commScroll);
    }

    private void StartComments(string videoId)
    {
        ResetComments();
        _commentsFor = videoId;
        _commentsLoading = true;
        var gen = _commentGen;
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await InnerTube.Instance.WatchCachedAsync(videoId);
                if (info.CommentsToken == null)
                {
                    App.Post(() => { if (gen == _commentGen) { _commentsLoading = false; _commentsOff = true; App.Invalidate(); } });
                    return;
                }
                var page = await InnerTube.Instance.CommentsAsync(info.CommentsToken);
                App.Post(() =>
                {
                    if (gen != _commentGen) return;
                    _comments.AddRange(page.Items);
                    _commentTok = page.Items.Count == 0 ? null : page.Continuation;
                    _commentCount = page.Count ?? info.CommentsCount;
                    _commentsLoading = false;
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("shorts comments: " + e.Message);
                App.Post(() => { if (gen == _commentGen) { _commentsLoading = false; _commentsOff = true; App.Invalidate(); } });
            }
        });
    }

    private void LoadMoreComments()
    {
        if (_commentTok == null || _commentsLoading) return;
        _commentsLoading = true;
        var tok = _commentTok;
        var gen = _commentGen;
        _ = Task.Run(async () =>
        {
            try
            {
                var page = await InnerTube.Instance.CommentsAsync(tok);
                App.Post(() =>
                {
                    if (gen != _commentGen) return;
                    _comments.AddRange(page.Items);
                    _commentTok = page.Items.Count == 0 ? null : page.Continuation;
                    _commentsLoading = false;
                    App.Invalidate();
                });
            }
            catch
            {
                App.Post(() => { if (gen == _commentGen) { _commentsLoading = false; _commentTok = null; App.Invalidate(); } });
            }
        });
    }

    private void LoadMore()
    {
        if (_loading) return;
        _loading = true; _failed = false;
        var tok = _next;
        _ = Task.Run(async () =>
        {
            try
            {
                var (ids, next) = await InnerTube.Instance.ShortsFeedAsync(tok);
                App.Post(() =>
                {
                    _loading = false;
                    var have = _items.Select(v => v.VideoId).ToHashSet();
                    var wasEmpty = _items.Count == 0;
                    foreach (var id in ids) if (have.Add(id)) _items.Add(new VideoItem { VideoId = id, IsShort = true });
                    _next = ids.Count == 0 ? null : next;
                    if (wasEmpty && _items.Count > 0 && Shell.Current == this) PlayCurrent();
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("shorts: " + e.Message);
                App.Post(() => { _loading = false; _failed = _items.Count == 0; App.Invalidate(); });
            }
        });
    }

    private void PlayCurrent()
    {
        if (Cur is not { } v) return;
        if (PlaybackTracker.Instance.Current?.VideoId != v.VideoId || !MpvPlayer.Instance.HasMedia)
        {
            Shell.Play(v, "shorts");
            MpvPlayer.Instance.Command("set", "loop-file", _loop ? "inf" : "no");
            MpvPlayer.Instance.Command("set", "volume", Math.Clamp(AppSettings.Current.Volume, 0, 100).ToString());
            MpvPlayer.Instance.Command("set", "mute", _muted ? "yes" : "no");
        }
        NeedInfo(v);
        if (_showComments && _commentsFor != v.VideoId) StartComments(v.VideoId);
        // Aggressively prefetch next 3 + prev 1 (streams, metadata, and posters) so stepping is instantaneous!
        for (int k = 3; k >= 1; k--)
            if (_i + k < _items.Count) PrefetchShort(_items[_i + k]);
        if (_i - 1 >= 0) PrefetchShort(_items[_i - 1]);
        if (_feed && _i >= _items.Count - 5 && _next != null) LoadMore();
    }

    private void PrefetchShort(VideoItem item)
    {
        QuickStream.Prefetch(item.VideoId);
        NeedInfo(item);
        Images.Get($"https://i.ytimg.com/vi/{item.VideoId}/oar2.jpg", 360, 640);
        Images.Get(item.ThumbHq, 360, 270);
    }

    /// <summary>The feed gives bare ids: title, channel and likes come with one small request per short.</summary>
    private void NeedInfo(VideoItem v)
    {
        if (v.Title.Length > 0 && v.Channel.Length > 0 || !_asked.Add(v.VideoId)) return;
        var id = v.VideoId;
        _ = Task.Run(async () =>
        {
            try
            {
                var full = await InnerTube.Instance.ShortInfoAsync(id);
                if (full == null) return;
                App.Post(() =>
                {
                    var at = _items.FindIndex(x => x.VideoId == id);
                    if (at < 0) return;
                    _items[at] = full;
                    if (PlaybackTracker.Instance.Current?.VideoId == id) PlaybackTracker.Instance.UpdateMeta(full.Title, full.Channel);
                    App.Invalidate();
                });
            }
            catch (Exception e) { Paths.Log("short info: " + e.Message); }
        });
    }

    /// <summary>Next (+1) or previous (-1) short; debounced to 160 ms so mouse wheel notches feel crisp.</summary>
    public void Step(int d)
    {
        if (UI.Now - _lastStep < 160 && _lastStep != 0) return;
        var to = _i + d;
        if (to < 0 || to >= _items.Count) { if (to >= _items.Count && _feed && _next != null) LoadMore(); return; }
        _lastStep = UI.Now;
        _i = to;
        _slideFrom = d;
        _slideAt = UI.Now;
        PlayCurrent();
        App.Invalidate();
    }

    private void Rate(VideoItem v, bool like)
    {
        if (!CookieStore.IsLoggedIn) { Shell.Toast("Чтобы ставить оценки, войдите в аккаунт YouTube", Th.Warn); return; }
        var want = like ? "LIKE" : "DISLIKE";
        var before = _rating.GetValueOrDefault(v.VideoId, "");
        var action = before == want ? "removelike" : like ? "like" : "dislike";
        _rating[v.VideoId] = before == want ? "" : want;
        var id = v.VideoId;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await InnerTube.Instance.RateAsync(id, action); } catch { ok = false; }
            if (!ok) App.Post(() => { _rating[id] = before; Shell.Toast("YouTube не принял оценку", Th.Warn); });
        });
    }

    private void ContextMenuFor(VideoItem v)
    {
        var p = MpvPlayer.Instance;
        var items = new List<MenuItem>
        {
            new(p.Paused ? "Продолжить" : "Пауза", p.TogglePause, icon: p.Paused ? Th.Play : Th.Pause, shortcut: "Space"),
            new(_muted ? "Включить звук" : "Выключить звук", ToggleMute, icon: _muted ? Th.Volume : Th.Mute, shortcut: "M"),
            new("Повтор Shorts", ToggleLoop, check: _loop, icon: Th.Repeat),
            new(AppSettings.Current.ShortsPanelHidden ? "Показать боковую панель" : "Скрыть боковую панель", TogglePanel, icon: Th.Theater),
            MenuItem.Sep,
            new("Открыть как обычное видео", () => Shell.OpenVideo(new VideoItem { VideoId = v.VideoId, Title = v.Title, Channel = v.Channel, ChannelId = v.ChannelId }, "shorts"), icon: Th.Expand),
            new("Открыть в новой вкладке", () => Shell.OpenVideoInNewTab(v), icon: Th.Add),
            new("Скачать (" + Cards.QualityName(AppSettings.Current.DownloadQuality) + ")", () => Cards.Download(v, null), icon: Th.Download),
            MenuItem.Sep,
            new("Копировать ссылку на Shorts", () => Cards.Copy("https://youtube.com/shorts/" + v.VideoId), icon: Th.Link),
            new("Открыть в браузере", () => W.Open("https://youtube.com/shorts/" + v.VideoId), icon: Th.Globe),
        };
        if (v.ChannelId != null)
            items.Insert(5, new("Открыть канал: " + v.Channel, () => Shell.OpenChannel(v.ChannelId, v.Channel), icon: Th.Account));
        Menus.Popup(items);
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        MpvPlayer.Instance.Command("set", "mute", _muted ? "yes" : "no");
        App.Invalidate();
    }

    private void ToggleLoop()
    {
        _loop = !_loop;
        MpvPlayer.Instance.Command("set", "loop-file", _loop ? "inf" : "no");
        App.Invalidate();
    }

    public void Draw(R r)
    {
        if (_volSaveAt > 0 && UI.Now >= _volSaveAt) FlushVolume();
        if (!UI.Down) { _dragVol = false; _dragSeek = false; }

        var v = Cur;
        if (v == null)
        {
            if (_loading) UI.Spinner(r.X + r.W / 2, r.Y + r.H / 2, 16, Th.Sub);
            else
            {
                var msg = _failed ? "Shorts не загрузились." : "Здесь пока пусто.";
                Gfx.Draw(msg, r.X + r.W / 2 - 90, r.Y + r.H / 2 - 30, 15, Th.Sub);
                if (_feed && UI.Pill(UI.Id("shorts:retry"), r.X + r.W / 2 - 60, r.Y + r.H / 2 + 4, 36, Th.Refresh, "Повторить", out _)) App.Post(Refresh);
            }
            return;
        }

        bool hidden = AppSettings.Current.ShortsPanelHidden;
        float panelT = UI.Smooth(UI.Id("shorts:panelT"), hidden ? 0 : 1, 16);
        float fullSide = MathF.Min(430, MathF.Max(340, r.W * 0.34f));
        float side = fullSide * panelT;
        float gap = 22 * panelT;
        var vh = MathF.Floor(MathF.Min(r.H - 28, (r.W - side - (hidden ? 110 : 90)) * 16 / 9));
        var vw = MathF.Floor(vh * 9 / 16);
        var vx = Gfx.Snap(r.X + MathF.Max(20, (r.W - vw - side - gap) / 2));
        var video = new R(vx, Gfx.Snap(r.Y + (r.H - vh) / 2), vw, vh);
        var panelR = new R(video.Right + gap, video.Y, MathF.Min(fullSide, r.Right - video.Right - 36), video.H);

        // Subtle elevated frame around the 9:16 player
        Gfx.Fill(video.Inset(-2), 0xFF000000, 14);
        Gfx.Stroke(video.Inset(-2), Th.A(0.14f), 14, 1f);

        Shell.WantVideo(video, v.VideoId);
        if (!Shell.VideoShown(v.VideoId))
        {
            var t = Math.Clamp((UI.Now - _slideAt) / 180f, 0, 1);
            if (t < 1) UI.Animate();
            var ease = 1 - (1 - t) * (1 - t) * (1 - t);
            var off = (1 - ease) * 44 * _slideFrom;
            Gfx.PushClip(video);
            Gfx.Fill(video, 0xFF09090C, 12);
            var img = Images.Get($"https://i.ytimg.com/vi/{v.VideoId}/oar2.jpg", vw, vh) ?? Images.Get(v.ThumbHq, vw, vh);
            if (img != null) Gfx.Image(img.Bmp!, new R(video.X, video.Y + off, video.W, video.H), 0.78f * Images.Alpha(img));
            UI.Spinner(video.X + vw / 2, video.Y + vh / 2, 14, 0xFFFFFFFF);
            Gfx.PopClip();
        }
        if (UI.RightClick(video)) ContextMenuFor(v);

        // When panel is hidden: compact floating toggle + quick nav rail beside the centered Short
        if (panelT < 0.05f)
        {
            float rx = video.Right + 14;
            if (rx + 46 < r.Right)
            {
                var rail = new R(rx, video.Y + 6, 44, 194);
                Gfx.Fill(rail, Th.Panel, 22);
                Gfx.Stroke(rail, Th.Line, 22, 1f);
                if (UI.IconButton(UI.Id("shorts:showpanel"), new R(rx + 4, rail.Y + 6, 36, 36), "\uE76B", 13, Th.Text)) TogglePanel();
                var mine0 = _rating.GetValueOrDefault(v.VideoId, "");
                if (UI.IconButton(UI.Id("shorts:qlike"), new R(rx + 4, rail.Y + 46, 36, 36), Th.Like, 15, mine0 == "LIKE" ? Th.Brand : Th.Icon)) Rate(v, true);
                if (UI.IconButton(UI.Id("shorts:qcomm"), new R(rx + 4, rail.Y + 86, 36, 36), Th.Comment, 15, Th.Icon)) ToggleComments();
                if (UI.IconButton(UI.Id("shorts:qup"), new R(rx + 4, rail.Y + 126, 36, 30), Th.ChevronUp, 12, Th.Text, _i > 0)) Step(-1);
                if (UI.IconButton(UI.Id("shorts:qdn"), new R(rx + 4, rail.Y + 158, 36, 30), Th.ChevronDown, 12, Th.Brand, _i < _items.Count - 1 || _next != null)) Step(1);
            }
            if (UI.Wheel != 0)
            {
                if (UI.Hover(video)) { AdjustVolume(UI.Wheel > 0 ? 5 : -5); UI.Wheel = 0; }
                else if (UI.Hover(r)) { Step(UI.Wheel > 0 ? -1 : 1); UI.Wheel = 0; }
            }
            return;
        }

        if (panelR.W < 200)
        {
            if (UI.Wheel != 0 && UI.Hover(r)) { Step(UI.Wheel > 0 ? -1 : 1); UI.Wheel = 0; }
            return;
        }

        // ---- Structured Right-Side Card ----
        Gfx.PushClip(new R(panelR.X - 2, panelR.Y - 2, side + 4, panelR.H + 4));
        Gfx.Fill(panelR, Th.Panel, 16);
        Gfx.Stroke(panelR, Th.Line, 16, 1f);
        if (UI.RightClick(panelR)) ContextMenuFor(v);

        float pad = 18;
        float x = panelR.X + pad, w = panelR.W - pad * 2, y = panelR.Y + 16;

        // 1. HEADER ZONE: Channel + Shorts Index Pill + Hide Panel button + Title
        var name = v.Channel.Length > 0 ? v.Channel : "…";
        var avR = new R(x, y, 42, 42);
        Cards.Avatar(avR.X, avR.Y, 42, v.AvatarUrl, name.TrimStart('@'));
        if (v.ChannelId != null && UI.Hover(avR)) Gfx.Stroke(avR.Inset(-1), Gfx.Fade(Th.Brand, 0.8f), 22, 1.5f);

        var hideBtn = new R(panelR.Right - pad - 28, y + 7, 28, 28);
        if (UI.IconButton(UI.Id("shorts:hidepanel"), hideBtn, Th.ChevronRight, 11, Th.Sub)) TogglePanel();

        var badge = _feed ? $"Shorts #{_i + 1}" : $"{_i + 1} / {_items.Count}";
        var br = Gfx.Text(badge, 11.5f, 600);
        var badgeR = new R(hideBtn.X - 6 - br.W - 14, y + 9, br.W + 14, 24);
        Gfx.Fill(badgeR, Th.A(0.08f), 12);
        Gfx.Draw(br, badgeR.X + 7, badgeR.Y + 4, Th.Sub);

        var nr = Gfx.Text(name, 15, 600, w - 54 - badgeR.W - 42);
        var nameR = new R(x + 52, y + 4, nr.W, nr.H);
        Gfx.Draw(nr, nameR.X, nameR.Y, UI.Hover(nameR) && v.ChannelId != null ? Th.Brand : Th.Text);
        Gfx.Draw(v.ChannelId != null ? "Перейти на канал" : "YouTube Shorts", x + 52, y + 24, 11.5f, Th.Dim);
        if (v.ChannelId != null && (UI.Click(UI.Id("shorts:ch"), new R(x + 52, y + 2, nr.W + 20, 38)) || UI.Click(UI.Id("shorts:av"), avR)))
        {
            var cid = v.ChannelId; var cn = name;
            App.Post(() => Shell.OpenChannel(cid, cn));
        }
        y += 54;

        if (v.Title.Length > 0)
        {
            var tr = Sel.Text(UI.Id("shorts:title"), v.Title, x, y, 15.5f, Th.Text, 600, w, _showComments ? 2 : 4, links: true,
                onLink: l => { if (l.Kind == '#') Shell.OpenSearch(l.Value); else if (l.Kind == 'u') W.Open(l.Value); });
            Cards.CopyMenu(new R(x, y, tr.W, tr.H), v.Title, "Копировать название");
            y += tr.H + 16;
        }
        else { Gfx.Fill(new R(x, y, w * 0.78f, 15), UI.Pulse(Th.Thumb), 5); y += 30; }

        // 2. PLAYBACK & GLOBAL VOLUME DECK (Card inside panel)
        var p = MpvPlayer.Instance;
        var deck = new R(x, y, w, 86);
        Gfx.Fill(deck, Th.Card, 12);
        Gfx.Stroke(deck, Th.A(0.08f), 12, 1f);

        // Top row of deck: Scrubbable Seek Bar + Timestamps
        double dur = p.Duration, pos = p.TimePos;
        float sx = deck.X + 14, sw = deck.W - 28;
        var curTxt = Fmt.Time(pos);
        var durTxt = dur > 0 ? Fmt.Time(dur) : "0:00";
        var curRun = Gfx.Text(curTxt, 11.5f, 600);
        var durRun = Gfx.Text(durTxt, 11.5f, 400);
        Gfx.Draw(curRun, sx, deck.Y + 10, Th.Text);
        Gfx.Draw(durRun, sx + sw - durRun.W, deck.Y + 10, Th.Sub);

        var seekTrack = new R(sx, deck.Y + 29, sw, 5);
        var seekHit = new R(sx, deck.Y + 20, sw, 22);
        var seekHot = UI.Hover(seekHit) || _dragSeek;
        if (dur > 0)
        {
            if (UI.Pressed && UI.Hover(seekHit)) _dragSeek = true;
            if (_dragSeek)
            {
                double target = dur * Math.Clamp((UI.MX - sx) / sw, 0, 1);
                p.SeekAbsolute(target);
                pos = target;
            }
            if (seekHot) UI.Hand();
        }
        double prog = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
        UI.Progress(seekTrack, prog, Th.A(0.2f), Th.Brand);
        if (seekHot && dur > 0)
            Gfx.Circle(sx + (float)(sw * prog), seekTrack.Y + 2.5f, 6, Th.Brand);
        if (p.HasMedia && !p.Paused) UI.Animate();

        // Bottom row of deck: Play/Pause + Repeat on left, Global Volume Slider on right!
        float ctrlY = deck.Y + 44;
        var playBtn = new R(deck.X + 10, ctrlY, 34, 34);
        Gfx.Fill(playBtn, Gfx.Fade(Th.Brand, p.Paused ? 0.22f : 0.14f), 17);
        if (UI.IconButton(UI.Id("shorts:play"), playBtn, p.Paused ? Th.Play : Th.Pause, 13.5f, Th.Brand))
            p.TogglePause();

        var loopBtn = new R(playBtn.Right + 6, ctrlY, 34, 34);
        if (_loop) Gfx.Fill(loopBtn, Th.A(0.08f), 17);
        if (UI.IconButton(UI.Id("shorts:loop"), loopBtn, Th.Repeat, 13, _loop ? Th.Brand : Th.Dim))
            ToggleLoop();

        // Global Volume Control (applies to ALL Shorts — supports click, drag, AND mouse wheel!)
        int vol = _muted ? 0 : Math.Clamp(AppSettings.Current.Volume, 0, 100);
        float volAreaX = loopBtn.Right + 14;
        var volZone = new R(volAreaX, ctrlY, deck.Right - 12 - volAreaX, 34);
        bool wheelConsumedByVol = false;
        if (UI.Wheel != 0 && UI.Hover(volZone))
        {
            AdjustVolume(UI.Wheel > 0 ? 5 : -5);
            UI.Wheel = 0;
            wheelConsumedByVol = true;
        }

        var muteBtn = new R(volZone.X, ctrlY, 30, 34);
        if (UI.IconButton(UI.Id("shorts:mute"), muteBtn, _muted || vol == 0 ? Th.Mute : Th.Volume, 14, _muted ? Th.Warn : Th.Text))
            ToggleMute();

        var volPct = Gfx.Text($"{vol}%", 11.5f, 600);
        Gfx.Draw(volPct, volZone.Right - volPct.W, ctrlY + (34 - volPct.H) / 2, Th.Sub);

        float vtx = muteBtn.Right + 6;
        float vtw = MathF.Max(40, volZone.Right - volPct.W - 10 - vtx);
        var volTrack = new R(vtx, ctrlY + 14.5f, vtw, 5);
        var volHit = new R(vtx - 4, ctrlY + 4, vtw + 8, 26);
        var volHot = UI.Hover(volHit) || _dragVol;
        if (UI.Pressed && UI.Hover(volHit)) _dragVol = true;
        if (_dragVol) SetVolume((int)MathF.Round(Math.Clamp((UI.MX - vtx) / vtw, 0, 1) * 100));
        if (volHot) UI.Hand();
        UI.Progress(volTrack, vol / 100.0, Th.A(0.2f), _muted ? Th.Dim : Th.Brand);
        Gfx.Circle(vtx + vtw * (vol / 100f), volTrack.Y + 2.5f, volHot ? 6f : 4.5f, _muted ? Th.Sub : Th.Text);

        y += deck.H + 14;

        // 3. ENGAGEMENT BAR: Unified Like|Dislike segmented pill + Comments button
        var mine = _rating.GetValueOrDefault(v.VideoId, "");
        var likes = v.Meta.Length > 0 ? v.Meta : "Лайк";
        float engH = 38;
        float splitW = MathF.Round(w * 0.52f);
        var rateBox = new R(x, y, splitW, engH);
        Gfx.Fill(rateBox, Th.A(0.08f), 10);
        float likeW = MathF.Round(splitW * 0.66f);
        var likeR = new R(rateBox.X, rateBox.Y, likeW, engH);
        var disR = new R(rateBox.X + likeW, rateBox.Y, splitW - likeW, engH);
        if (mine == "LIKE") Gfx.Fill(likeR, Gfx.Fade(Th.Brand, 0.22f), 10);
        if (mine == "DISLIKE") Gfx.Fill(disR, Gfx.Fade(Th.Brand, 0.22f), 10);
        UI.Glow(UI.Id("shorts:like"), likeR, 10, UI.Hover(likeR));
        UI.Glow(UI.Id("shorts:dis"), disR, 10, UI.Hover(disR));
        Gfx.Fill(new R(disR.X, rateBox.Y + 9, 1, engH - 18), Th.Line);

        var lr = Gfx.Text(likes, 13, 600, likeW - 36);
        float lx = likeR.X + (likeR.W - (18 + 6 + lr.W)) / 2;
        uint likeCol = mine == "LIKE" ? Th.Brand : Th.Text;
        Gfx.Icon(Th.Like, new R(lx, likeR.Y, 18, engH), 14.5f, likeCol);
        Gfx.Draw(lr, lx + 24, likeR.Y + (engH - lr.H) / 2, likeCol);
        Gfx.Icon(Th.Dislike, disR, 14.5f, mine == "DISLIKE" ? Th.Brand : Th.Text);
        if (UI.Click(UI.Id("shorts:like"), likeR)) Rate(v, true);
        if (UI.Click(UI.Id("shorts:dis"), disR)) Rate(v, false);

        var commBtn = new R(rateBox.Right + 8, y, w - splitW - 8, engH);
        Gfx.Fill(commBtn, _showComments ? Gfx.Fade(Th.Brand, 0.9f) : Th.A(0.08f), 10);
        UI.Glow(UI.Id("shorts:commbtn"), commBtn, 10, UI.Hover(commBtn));
        var cLabel = _commentCount is { Length: > 0 } ? _commentCount : "Комменты";
        var cr = Gfx.Text(cLabel, 13, 600, commBtn.W - 34);
        float cx = commBtn.X + (commBtn.W - (18 + 6 + cr.W)) / 2;
        uint commFg = _showComments ? Th.OnBrand : Th.Text;
        Gfx.Icon(Th.Comment, new R(cx, commBtn.Y, 18, engH), 14, commFg);
        Gfx.Draw(cr, cx + 24, commBtn.Y + (engH - cr.H) / 2, commFg);
        if (UI.Click(UI.Id("shorts:commbtn"), commBtn)) ToggleComments();
        y += engH + 10;

        // 4. QUICK ACTIONS GRID: 4 equal, pixel-aligned tiles (Скачать / Ссылка / Плеер / Вкладка)
        float tileGap = 8, tileW = (w - tileGap * 3) / 4, tileH = 50;
        ActionTile(new R(x, y, tileW, tileH), UI.Id("shorts:dl"), Th.Download, "Скачать", () => Cards.Download(v, null));
        ActionTile(new R(x + (tileW + tileGap), y, tileW, tileH), UI.Id("shorts:share"), Th.Share, "Ссылка", () => Cards.Copy("https://youtube.com/shorts/" + v.VideoId));
        ActionTile(new R(x + (tileW + tileGap) * 2, y, tileW, tileH), UI.Id("shorts:open"), Th.Expand, "Плеер", () =>
        {
            var vv = v;
            App.Post(() => Shell.OpenVideo(new VideoItem { VideoId = vv.VideoId, Title = vv.Title, Channel = vv.Channel, ChannelId = vv.ChannelId }, "shorts"));
        });
        ActionTile(new R(x + (tileW + tileGap) * 3, y, tileW, tileH), UI.Id("shorts:tab"), Th.Add, "Вкладка", () => Shell.OpenVideoInNewTab(v));
        y += tileH + 14;

        // Bottom navigation footer height
        float footerY = panelR.Bottom - 56;

        // 5. MIDDLE CONTENT ZONE: Inline Comments Drawer OR "Up Next" Preview Card
        if (_showComments && footerY - y > 80)
        {
            Gfx.Fill(new R(x, y, w, 1), Th.Line);
            y += 8;
            var chTitle = _commentCount is { Length: > 0 }
                ? (_commentCount.Any(char.IsLetter) ? _commentCount : $"Комментарии · {_commentCount}")
                : "Комментарии";
            Gfx.Draw(chTitle, x, y + 3, 13.5f, Th.Text, 600);
            if (UI.IconButton(UI.Id("shorts:commclose"), new R(x + w - 24, y, 24, 24), Th.Close, 10, Th.Sub)) _showComments = false;
            y += 28;
            var commView = new R(x, y, w + 8, footerY - y - 10);
            DrawCommentsDrawer(commView);
        }
        else if (!_showComments && footerY - y > 96)
        {
            Gfx.Fill(new R(x, y, w, 1), Th.Line);
            y += 12;
            if (NextItem is { } nxt)
            {
                Gfx.Draw("Далее в ленте", x, y, 12, Th.Dim, 600);
                y += 20;
                var upNext = new R(x, y, w, 68);
                Gfx.Fill(upNext, Th.Card, 10);
                var glow = UI.Glow(UI.Id("shorts:nextcard"), upNext, 10, UI.Hover(upNext));
                var thR = new R(upNext.X + 8, upNext.Y + 8, 36, 52);
                Cards.ThumbUrl(thR, $"https://i.ytimg.com/vi/{nxt.VideoId}/oar2.jpg", 6, UI.GlowOn(Th.Card, glow));
                float ntx = thR.Right + 12, ntw = upNext.Right - 12 - ntx;
                var ntTitle = nxt.Title.Length > 0 ? nxt.Title : "Следующий Shorts…";
                var ntRun = Gfx.Draw(ntTitle, ntx, upNext.Y + 12, 13, Th.Text, 600, ntw, 2);
                if (nxt.Channel.Length > 0) Gfx.Draw(nxt.Channel, ntx, upNext.Y + 14 + ntRun.H, 11.5f, Th.Sub, 400, ntw);
                if (UI.Click(UI.Id("shorts:nextcard"), upNext)) Step(1);
            }

            // Subtle compact keyboard hint bar just above footer
            float hy = footerY - 30;
            if (hy > y + 74)
                Gfx.Draw("Колесо / ↑↓ — лента · Колесо по звуку — громкость · Space — пауза", x, hy, 11.5f, Th.Dim, 400, w);
        }

        // 6. BOTTOM NAVIGATION FOOTER
        Gfx.Fill(new R(x, footerY - 8, w, 1), Th.Line);
        var prevBtn = new R(x, footerY, (w - 10) / 2, 40);
        var nextBtn = new R(prevBtn.Right + 10, footerY, (w - 10) / 2, 40);
        bool canPrev = _i > 0, canNext = _i < _items.Count - 1 || _next != null;

        Gfx.Fill(prevBtn, Th.A(canPrev ? 0.08f : 0.03f), 10);
        if (canPrev) UI.Glow(UI.Id("shorts:up"), prevBtn, 10, UI.Hover(prevBtn));
        var pt = Gfx.Text("Предыдущий", 13, 600);
        float px = prevBtn.X + (prevBtn.W - (18 + 6 + pt.W)) / 2;
        Gfx.Icon(Th.ChevronUp, new R(px, prevBtn.Y, 18, prevBtn.H), 12, canPrev ? Th.Text : Th.Dim);
        Gfx.Draw(pt, px + 24, prevBtn.Y + (prevBtn.H - pt.H) / 2, canPrev ? Th.Text : Th.Dim);
        if (canPrev && UI.Click(UI.Id("shorts:up"), prevBtn)) Step(-1);

        Gfx.Fill(nextBtn, canNext ? Gfx.Fade(Th.Brand, 0.16f) : Th.A(0.03f), 10);
        if (canNext) UI.Glow(UI.Id("shorts:down"), nextBtn, 10, UI.Hover(nextBtn));
        var nt = Gfx.Text("Следующий", 13, 600);
        float nx = nextBtn.X + (nextBtn.W - (18 + 6 + nt.W)) / 2;
        Gfx.Draw(nt, nx, nextBtn.Y + (nextBtn.H - nt.H) / 2, canNext ? Th.Text : Th.Dim);
        Gfx.Icon(Th.ChevronDown, new R(nx + nt.W + 6, nextBtn.Y, 18, nextBtn.H), 12, canNext ? Th.Brand : Th.Dim);
        if (canNext && UI.Click(UI.Id("shorts:down"), nextBtn)) Step(1);

        if (_loading) UI.Spinner(panelR.Right - 28, panelR.Y + 20, 7, Th.Sub);
        Gfx.PopClip();

        // Wheel over the video (even while loading) adjusts volume; wheel outside video (and not in comments/volume) steps Shorts!
        if (!wheelConsumedByVol && UI.Wheel != 0)
        {
            if (UI.Hover(video))
            {
                AdjustVolume(UI.Wheel > 0 ? 5 : -5);
                UI.Wheel = 0;
            }
            else if (UI.Hover(r) && !(_showComments && UI.Hover(panelR)))
            {
                Step(UI.Wheel > 0 ? -1 : 1);
                UI.Wheel = 0;
            }
        }
    }

    private static void ActionTile(R tile, int id, string glyph, string label, Action onClick)
    {
        Gfx.Fill(tile, Th.A(0.065f), 10);
        var over = UI.Hover(tile);
        UI.Glow(id, tile, 10, over);
        Gfx.Icon(glyph, new R(tile.X, tile.Y + 6, tile.W, 20), 14.5f, over ? Th.Text : Th.Icon);
        var tr = Gfx.Text(label, 11.5f, 500, tile.W - 8);
        Gfx.Draw(tr, Gfx.Snap(tile.X + (tile.W - tr.W) / 2), tile.Y + 29, over ? Th.Text : Th.Sub);
        if (UI.Click(id, tile)) onClick();
    }

    private void DrawCommentsDrawer(R view)
    {
        if (_commentsLoading && _comments.Count == 0)
        {
            UI.Spinner(view.X + view.W / 2, view.Y + 34, 10, Th.Sub);
            return;
        }
        if (_commentsOff)
        {
            Gfx.Draw("Комментарии отключены или недоступны.", view.X, view.Y + 8, 13, Th.Sub, 400, view.W - 12);
            return;
        }
        if (_comments.Count == 0)
        {
            Gfx.Draw("Пока нет комментариев.", view.X, view.Y + 8, 13, Th.Sub);
            return;
        }

        float tw = view.W - 54;
        float totalH = 12;
        foreach (var c in _comments)
        {
            var tr = Gfx.Text(c.Text, 13, 400, tw, 4);
            totalH += 22 + tr.H + 26;
        }
        if (_commentTok != null || _commentsLoading) totalH += 44;

        var scroll = UI.BeginScroll(_commScroll, view, totalH);
        float cy = view.Y - scroll;
        for (int i = 0; i < _comments.Count; i++)
        {
            var c = _comments[i];
            var tr = Gfx.Text(c.Text, 13, 400, tw, 4);
            float rh = 22 + tr.H + 26;
            if (cy + rh > view.Y - 20 && cy < view.Bottom + 20)
            {
                Cards.Avatar(view.X, cy + 2, 30, c.Avatar, c.Author.TrimStart('@'));
                float tx = view.X + 40;
                var au = Gfx.Draw(c.Author, tx, cy, 12.5f, Th.Text, 600, tw - 60);
                Gfx.Draw(c.When, tx + au.W + 8, cy + 1, 11.5f, Th.Dim);
                Gfx.Draw(tr, tx, cy + 19, Gfx.Mix(Th.Text, Th.Sub, 0.1f));
                Gfx.Icon(Th.Like, new R(tx - 2, cy + 21 + tr.H, 16, 18), 11, Th.Dim);
                Gfx.Draw(c.Likes, tx + 16, cy + 22 + tr.H, 11.5f, Th.Dim);
                if (UI.RightClick(new R(view.X, cy, view.W - 10, rh)))
                    Menus.Popup([
                        new("Копировать комментарий", () => Cards.CopyText(c.Text, "Комментарий скопирован"), icon: Th.Copy),
                        new("Копировать ник", () => Cards.CopyText(c.Author, "Ник скопирован"), icon: Th.Account),
                    ]);
            }
            cy += rh;
        }
        if (_commentsLoading) UI.Spinner(view.X + view.W / 2, cy + 16, 8, Th.Sub);
        else if (_commentTok != null && cy < view.Bottom + 200) LoadMoreComments();
        UI.EndScroll();
    }
}
