using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube.Pages;

/// <summary>
/// Shorts: borderless vertical YouTube-style player with:
/// - Always-visible YouTube circular action rail on the bottom-right (Like + count, Dislike + RYD count,
///   Comments + count, Share, Panel/More, Channel square-rounded avatar)
/// - Clean borderless vertical swipe animation (no ugly outline during slide)
/// - Smooth volume adjustment with custom volume SFX + persistent global volume deck
/// - Inline comments drawer with live comment posting (InnerTube comment/create_comment)
/// - Instant prefetching (next 3 + prev 1) and zero idle CPU/GPU spin.
/// </summary>
public sealed class ShortsPage : IPage
{
    private sealed class ShortCommentRow
    {
        public required Comment C;
        public float W, H;
    }

    private readonly List<VideoItem> _items = [];
    private int _i;
    private string? _next;
    private bool _loading, _failed, _pendingAdvance;
    private int _feedGen;
    private readonly bool _feed;
    private long _lastStep;
    private readonly HashSet<string> _asked = [];
    private readonly Dictionary<string, string> _rating = new();
    private readonly Dictionary<string, string> _dislikes = new();
    private readonly Dictionary<string, string> _commCounts = new();
    private readonly int _id = UI.Id("shorts");
    private readonly int _commScroll = UI.Id("shorts:comm");

    private int _slideFrom;
    private long _slideAt;
    private string? _prevVideoId;
    private int _consecutiveAdSkips;
    private bool _loop = true;
    private bool _dragVol, _dragSeek;
    private double _dragSeekTarget = -1;
    private long _volSaveAt, _lastDragSeekAt, _lastDragVolAt;
    private float _videoW = 360, _videoH = 640;

    // Inline comments + composer state for the active Short
    private bool _showComments;
    private string? _commentsFor;
    private readonly List<ShortCommentRow> _comments = [];
    private string? _commentTok, _commentCount, _createCommentParams;
    private bool _commentsLoading, _commentsOff, _postingComment;
    private int _commentGen;
    private Edit? _commentEdit;

    public string Tag => _feed ? "shorts" : "";
    public int MainScroll => _showComments ? _commScroll : _id;

    public ShortsPage() { _feed = true; }

    public ShortsPage(List<VideoItem> items, int index)
    {
        _items.AddRange(items);
        _i = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
    }

    public string? CurrentId => Cur?.VideoId;
    public string CurrentTitle => Cur?.Title ?? "";
    public string CurrentChannel => Cur?.Channel ?? "";
    public string CurrentLikes => Cur?.Meta ?? "";
    private VideoItem? Cur => _i >= 0 && _i < _items.Count ? _items[_i] : null;
    private VideoItem? NextItem => _i + 1 >= 0 && _i + 1 < _items.Count ? _items[_i + 1] : null;

    public void Shown()
    {
        if (_items.Count == 0) { if (_feed) LoadMore(); }
        else PlayCurrent();
    }

    public void Hidden()
    {
        FlushVolume();
        _commentEdit?.Hide();
        if (PlaybackTracker.Instance.CurrentSource == "shorts") { PlaybackTracker.Instance.Stop(); Shell.VideoReady = false; }
    }

    private int _emptyBatches;

    public void Invalidate()
    {
        if (!_feed) return;
        _feedGen++;
        _loading = false;
        _emptyBatches = 0;
        _items.Clear(); _i = 0; _next = null; _failed = false; _pendingAdvance = false;
        _asked.Clear(); _rating.Clear(); _dislikes.Clear(); _commCounts.Clear();
        ResetComments();
        if (PlaybackTracker.Instance.CurrentSource == "shorts") { PlaybackTracker.Instance.Stop(); Shell.VideoReady = false; }
    }

    public void Refresh()
    {
        if (!_feed) return;
        Invalidate();
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
            Shell.PushPlayerState();
            _showComments = true;
        }
        else _showComments = !_showComments;
        if (!_showComments) _commentEdit?.Hide();
        else if (Cur is { } v && (_commentsFor != v.VideoId || _commentsOff)) StartComments(v.VideoId);
        App.Invalidate();
    }

    public void TogglePanel()
    {
        AppSettings.Current.ShortsPanelHidden = !AppSettings.Current.ShortsPanelHidden;
        AppSettings.Current.Save();
        if (AppSettings.Current.ShortsPanelHidden) _commentEdit?.Hide();
        Shell.PushPlayerState();
        App.Invalidate();
    }

    public void ToggleInfoOverlay()
    {
        AppSettings.Current.ShortsInfoOverlay = !AppSettings.Current.ShortsInfoOverlay;
        AppSettings.Current.Save();
        Shell.PushPlayerState();
        App.Invalidate();
    }

    public void OpenContextMenu()
    {
        if (Cur is { } v) ContextMenuFor(v);
    }

    public void OnShortEnded()
    {
        if (!_loop) Step(1);
    }

    /// <summary>Global smooth volume for all Shorts (persisted in AppSettings.Volume + plays custom volume SFX).</summary>
    public void SetVolume(int vol, bool smooth = true)
    {
        vol = Math.Clamp(vol, 0, 150);
        if (vol != AppSettings.Current.Volume) Shell.PlayVolumeSfx(vol);
        AppSettings.Current.Volume = vol;
        var p = MpvPlayer.Instance;
        if (vol > 0 && p.Muted) p.Command("set", "mute", "no");
        if (smooth) p.SetVolumeSmooth(vol);
        else p.Command("set", "volume", vol.ToString());
        p.ShowText($"{L.T("Громкость", "Volume")}: {vol}%", 900);
        _volSaveAt = Environment.TickCount64 + 400;
        App.Invalidate();
    }

    public void AdjustVolume(int delta) => SetVolume(AppSettings.Current.Volume + delta, smooth: true);

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
        _createCommentParams = null;
        _commentsLoading = false;
        _commentsOff = false;
        _commentEdit?.Hide();
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
                    foreach (var c in page.Items) _comments.Add(new ShortCommentRow { C = c });
                    _commentTok = page.Items.Count == 0 ? null : page.Continuation;
                    _commentCount = page.Count ?? info.CommentsCount;
                    if (!string.IsNullOrEmpty(_commentCount)) _commCounts[videoId] = _commentCount;
                    _createCommentParams = page.CreateCommentParams ?? info.CreateCommentParams;
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

    private void SubmitComment()
    {
        if (_postingComment || _commentEdit == null || Cur is not { } v) return;
        var text = _commentEdit.Text.Trim();
        if (text.Length == 0) return;
        if (!CookieStore.IsLoggedIn) { Shell.Toast(L.T("Войдите в аккаунт YouTube, чтобы оставлять комментарии", "Sign in to YouTube to post comments"), Th.Warn); return; }
        if (string.IsNullOrEmpty(_createCommentParams))
        {
            Shell.Toast(L.T("Токен комментариев ещё загружается — подождите секунду", "Comment token still loading — wait a second"), Th.Warn);
            if (!_commentsLoading) StartComments(v.VideoId);
            return;
        }
        _postingComment = true;
        var vid = v.VideoId;
        var gen = _commentGen;
        var cp = _createCommentParams;
        _ = Task.Run(async () =>
        {
            bool ok;
            string? err;
            try { (ok, err) = await InnerTube.Instance.CreateCommentAsync(cp, text, vid); }
            catch (Exception ex) { ok = false; err = ex.Message; }
            App.Post(() =>
            {
                _postingComment = false;
                if (!ok) { Shell.Toast(L.T("Не удалось отправить комментарий: ", "Failed to post comment: ") + (err ?? L.T("ошибка YouTube", "YouTube error")), Th.Warn, 5000); App.Invalidate(); return; }
                if (_commentEdit != null) _commentEdit.Text = "";
                Sfx.Like();
                if (gen == _commentGen)
                {
                    var acc = AccountMenu.Current;
                    _comments.Insert(0, new ShortCommentRow
                    {
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
                    foreach (var c in page.Items) _comments.Add(new ShortCommentRow { C = c });
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
        var gen = _feedGen;
        var tok = _next;
        _ = Task.Run(async () =>
        {
            try
            {
                var (ids, next) = await InnerTube.Instance.ShortsFeedAsync(tok);
                App.Post(() =>
                {
                    if (gen != _feedGen) return;
                    _loading = false;
                    var have = _items.Select(v => v.VideoId).ToHashSet();
                    var wasEmpty = _items.Count == 0;
                    int added = 0;
                    foreach (var id in ids) if (have.Add(id)) { _items.Add(new VideoItem { VideoId = id, IsShort = true }); added++; }
                    _next = next;
                    if (added == 0 && next != null && _emptyBatches < 3)
                    {
                        _emptyBatches++;
                        LoadMore();
                        return;
                    }
                    _emptyBatches = 0;
                    bool wasPending = _pendingAdvance;
                    _pendingAdvance = false;
                    if (wasEmpty && _items.Count > 0 && Shell.Current == this) PlayCurrent();
                    else if (wasPending && _i < _items.Count)
                    {
                        if (PlaybackTracker.Instance.Current?.VideoId != _items[_i].VideoId) PlayCurrent();
                        else if (_i + 1 < _items.Count) Step(1);
                    }
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("shorts: " + e.Message);
                App.Post(() => { if (gen != _feedGen) return; _loading = false; _pendingAdvance = false; _failed = _items.Count == 0; App.Invalidate(); });
            }
        });
    }

    private void PlayCurrent()
    {
        if (Cur is not { } v) return;
        if (Pip.Active) Pip.Exit();
        if (PlaybackTracker.Instance.Current?.VideoId != v.VideoId || !MpvPlayer.Instance.HasMedia)
        {
            Shell.Play(v, "shorts");
            MpvPlayer.Instance.Command("set", "loop-file", _loop ? "inf" : "no");
        }
        NeedInfo(v);
        if (_showComments) { if (_commentsFor != v.VideoId) StartComments(v.VideoId); }
        else if (_commentsFor != null && _commentsFor != v.VideoId) ResetComments();

        for (int k = 3; k >= 1; k--)
            if (_i + k < _items.Count) PrefetchShort(_items[_i + k]);
        if (_i - 1 >= 0) PrefetchShort(_items[_i - 1]);
        if (_feed && _i >= _items.Count - 5 && _next != null) LoadMore();
    }

    private void PrefetchShort(VideoItem item)
    {
        if (!QuickStream.IsBlocked) QuickStream.Prefetch(item.VideoId);
        else if (item.VideoId == NextItem?.VideoId) Resolver.Instance.Prefetch(item.VideoId, urgent: true);
        NeedInfo(item);
        Images.Get($"https://i.ytimg.com/vi/{item.VideoId}/oar2.jpg", _videoW, _videoH);
    }

    private void SkipAdShort(string id, string reason)
    {
        var at = _items.FindIndex(x => x.VideoId == id);
        if (at < 0) return;
        ShortsAdBlocker.RecordBlocked();
        Paths.Log($"shorts-adblock: removed {id} ({reason})");

        if (at != _i)
        {
            _items.RemoveAt(at);
            if (at < _i) _i--;
            if (_feed && _items.Count - _i <= 5 && _next != null) LoadMore();
            App.Invalidate();
            return;
        }

        if (_consecutiveAdSkips >= 15)
        {
            _consecutiveAdSkips = 0;
            return;
        }
        // Currently playing Short is an ad/sponsored video -> auto-skip cleanly!
        _items.RemoveAt(at);
        _consecutiveAdSkips++;
        Shell.Toast(L.T($"Рекламный Short пропущен ({reason})", $"Ad Short skipped ({reason})"), Th.Good, 2100);
        if (_i >= _items.Count)
        {
            if (_feed && _next != null) { _pendingAdvance = true; LoadMore(); }
            else _i = Math.Max(0, _items.Count - 1);
        }
        if (_i >= 0 && _i < _items.Count)
        {
            _prevVideoId = id;
            _slideFrom = 1;
            _slideAt = Environment.TickCount64;
            PlayCurrent();
        }
        else if (_items.Count == 0 && (!_feed || _next == null))
        {
            PlaybackTracker.Instance.Stop();
            Shell.VideoReady = false;
        }
        App.Invalidate();
    }

    private void NeedInfo(VideoItem v)
    {
        if ((v.Title.Length > 0 && v.Channel.Length > 0 && _dislikes.ContainsKey(v.VideoId)) || !_asked.Add(v.VideoId)) return;
        var id = v.VideoId;
        var gen = _feedGen;
        _ = Task.Run(async () =>
        {
            VideoItem? full = null;
            WatchInfo? watchInfo = null;
            List<Extras.Segment>? sponsorSegs = null;

            try { full = await InnerTube.Instance.ShortInfoAsync(id); }
            catch (Exception e)
            {
                Paths.Log("short info: " + e.Message);
                App.Post(() => { if (gen == _feedGen) _asked.Remove(id); });
            }

            // Fetch full watch metadata (comments count + full description + paidPromotion) and SponsorBlock in parallel!
            var watchTask = Task.Run(async () =>
            {
                try { return await InnerTube.Instance.WatchCachedAsync(id); }
                catch { return null; }
            });
            var sbTask = ShortsAdBlocker.Enabled
                ? Task.Run(async () =>
                {
                    try { return await Extras.ShortsSponsorSegmentsAsync(id); }
                    catch { return new List<Extras.Segment>(); }
                })
                : Task.FromResult(new List<Extras.Segment>());

            // Early fast-reject right after ShortInfoAsync if YouTube ad/paid/erid already detected!
            if (_feed && full != null && ShortsAdBlocker.Check(full, null, null) is { } earlyReason)
            {
                App.Post(() => { if (gen == _feedGen) SkipAdShort(id, earlyReason); });
                return;
            }

            if (full != null)
            {
                App.Post(() =>
                {
                    if (gen != _feedGen) return;
                    var at = _items.FindIndex(x => x.VideoId == id);
                    if (at < 0) return;
                    var prevItem = _items[at];
                    _items[at] = new VideoItem
                    {
                        VideoId = full.VideoId,
                        IsShort = true,
                        Title = full.Title.Length > 0 ? full.Title : prevItem.Title,
                        Channel = full.Channel.Length > 0 ? full.Channel : prevItem.Channel,
                        ChannelId = full.ChannelId ?? prevItem.ChannelId,
                        AvatarUrl = full.AvatarUrl ?? prevItem.AvatarUrl,
                        Meta = full.Meta.Length > 0 ? full.Meta : prevItem.Meta,
                        IsAd = full.IsAd,
                        HasPaidPromotion = full.HasPaidPromotion,
                        Description = full.Description.Length > 0 ? full.Description : prevItem.Description,
                    };
                    if (PlaybackTracker.Instance.Current?.VideoId == id)
                    {
                        PlaybackTracker.Instance.UpdateMeta(full.Title, full.Channel);
                        Shell.PushPlayerState();
                    }
                    App.Invalidate();
                });
            }

            watchInfo = await watchTask;
            sponsorSegs = await sbTask;

            // Full max-power ad / sponsored check combining ShortInfo + Watch description + SponsorBlock!
            if (_feed)
            {
                double dur = PlaybackTracker.Instance.Current?.VideoId == id ? MpvPlayer.Instance.Duration : 0;
                if (ShortsAdBlocker.Check(full ?? v, watchInfo, sponsorSegs, dur) is { } fullReason)
                {
                    App.Post(() => { if (gen == _feedGen) SkipAdShort(id, fullReason); });
                    return;
                }
            }

            if (!string.IsNullOrEmpty(watchInfo?.CommentsCount))
            {
                var cc = watchInfo.CommentsCount;
                App.Post(() => { if (gen == _feedGen) { _commCounts[id] = cc; App.Invalidate(); } });
            }

            if (AppSettings.Current.ReturnDislikes)
            {
                try
                {
                    if (await Extras.DislikesAsync(id) is { } votes)
                    {
                        var dis = Extras.Compact(votes.Dislikes);
                        App.Post(() => { if (gen == _feedGen) { _dislikes[id] = dis; App.Invalidate(); } });
                    }
                }
                catch { }
            }
        });
    }

    /// <summary>Next (+1) or previous (-1) short; debounced to 160 ms so mouse wheel notches feel crisp.</summary>
    public void Step(int d)
    {
        var now = Environment.TickCount64;
        if (now - _lastStep < 160 && _lastStep != 0) return;
        var to = _i + d;
        if (to < 0 || to >= _items.Count)
        {
            if (to >= _items.Count && _feed && _next != null) { _pendingAdvance = true; LoadMore(); }
            return;
        }
        _pendingAdvance = false;
        _consecutiveAdSkips = 0;
        _lastStep = now;
        _prevVideoId = Cur?.VideoId;
        _i = to;
        _slideFrom = d;
        _slideAt = now;
        PlayCurrent();
        App.Invalidate();
    }

    private void Rate(VideoItem v, bool like)
    {
        if (!CookieStore.IsLoggedIn) { Shell.Toast(L.T("Чтобы ставить оценки, войдите в аккаунт YouTube", "Sign in to YouTube to rate videos"), Th.Warn); return; }
        Sfx.Like();
        var want = like ? "LIKE" : "DISLIKE";
        var before = _rating.GetValueOrDefault(v.VideoId, "");
        var action = before == want ? "removelike" : like ? "like" : "dislike";
        _rating[v.VideoId] = before == want ? "" : want;
        var id = v.VideoId;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await InnerTube.Instance.RateAsync(id, action); } catch { ok = false; }
            if (!ok) App.Post(() => { _rating[id] = before; Shell.Toast(L.T("YouTube не принял оценку", "YouTube rejected the rating"), Th.Warn); App.Invalidate(); });
        });
    }

    private void ContextMenuFor(VideoItem v)
    {
        var p = MpvPlayer.Instance;
        var muted = p.Muted;
        var items = new List<MenuItem>
        {
            new(p.Paused ? L.T("Продолжить", "Resume") : L.T("Пауза", "Pause"), p.TogglePause, icon: p.Paused ? Th.Play : Th.Pause, shortcut: "Space"),
            new(muted ? L.T("Включить звук", "Unmute") : L.T("Выключить звук", "Mute"), ToggleMute, icon: muted ? Th.Volume : Th.Mute, shortcut: "M"),
            new(L.T("Повтор Shorts", "Loop Short"), ToggleLoop, check: _loop, icon: Th.Repeat),
            new(AppSettings.Current.ShortsPanelHidden ? L.T("Показать боковую панель", "Show side panel") : L.T("Скрыть боковую панель", "Hide side panel"), TogglePanel, icon: Th.Theater),
            new(L.T("Подпись поверх видео (при наведении)", "Title overlay on hover"), ToggleInfoOverlay, check: AppSettings.Current.ShortsInfoOverlay, icon: Th.Info),
            new(L.T("Блокировка рекламы в Shorts", "Shorts Ad Blocker"), null,
            [
                new(L.T("Максимум (реклама + erid + промо + SponsorBlock)", "Maximum (ads + paid promo + SponsorBlock)"), () => SetShortsAdBlock("max"), check: AppSettings.Current.ShortsAdBlock == "max"),
                new(L.T("Стандарт (реклама + erid + SponsorBlock)", "Standard (ads + paid promo + SponsorBlock)"), () => SetShortsAdBlock("standard"), check: AppSettings.Current.ShortsAdBlock == "standard"),
                new(L.T("Выключено", "Off"), () => SetShortsAdBlock("off"), check: AppSettings.Current.ShortsAdBlock == "off"),
            ], icon: Th.Block),
            MenuItem.Sep,
            new(L.T("Открыть как обычное видео", "Open as regular video"), () => Shell.OpenVideo(new VideoItem { VideoId = v.VideoId, Title = v.Title, Channel = v.Channel, ChannelId = v.ChannelId }, "shorts-open"), icon: Th.Expand),
            new(L.T("Открыть в новой вкладке", "Open in new tab"), () => Shell.OpenVideoInNewTab(v), icon: Th.Add),
            new(L.T("Скачать", "Download") + " (" + Cards.QualityName(AppSettings.Current.DownloadQuality) + ")", () => Cards.Download(v, null), icon: Th.Download),
            MenuItem.Sep,
            new(L.T("Копировать ссылку на Shorts", "Copy Short link"), () => Cards.Copy("https://youtube.com/shorts/" + v.VideoId), icon: Th.Link),
            new(L.T("Открыть в браузере", "Open in browser"), () => W.Open("https://youtube.com/shorts/" + v.VideoId), icon: Th.Globe),
        };
        if (v.ChannelId != null)
            items.Insert(6, new(L.T("Открыть канал: ", "Open channel: ") + v.Channel, () => Shell.OpenChannel(v.ChannelId, v.Channel), icon: Th.Account));
        Menus.Popup(items);
    }

    private static void SetShortsAdBlock(string mode)
    {
        AppSettings.Current.ShortsAdBlock = mode;
        AppSettings.Current.Save();
        Shell.Toast(L.T("Блокировка рекламы в Shorts: ", "Shorts Ad Blocker: ") + mode switch
        {
            "max" => L.T("Максимум", "Maximum"),
            "standard" => L.T("Стандарт", "Standard"),
            _ => L.T("Выкл", "Off"),
        }, Th.Good, 2200);
        App.Invalidate();
    }

    private void ToggleMute()
    {
        MpvPlayer.Instance.Command("cycle", "mute");
        App.Invalidate();
    }

    public void ToggleLoop()
    {
        _loop = !_loop;
        MpvPlayer.Instance.Command("set", "loop-file", _loop ? "inf" : "no");
        App.Invalidate();
    }

    /// <summary>Draws one YouTube-style circular action button with label/counter centered underneath.</summary>
    private static bool RailCircleButton(int id, float cx, float topY, string glyph, string label, bool active, bool playSfx = true)
    {
        const float size = 46;
        var circleR = new R(cx - size / 2, topY, size, size);
        bool over = UI.Hover(circleR);
        float hoverA = UI.Anim(id ^ 0x3311, over ? 1 : 0, 22);
        uint bg = active
            ? Gfx.Fade(Th.Brand, 0.26f + 0.10f * hoverA)
            : Th.A(0.11f + 0.08f * hoverA);
        Gfx.Fill(circleR, bg, size / 2);
        if (active) Gfx.Stroke(circleR, Gfx.Fade(Th.Brand, 0.55f), size / 2, 1.2f);
        else if (hoverA > 0.01f) Gfx.Stroke(circleR, Th.A(0.18f * hoverA), size / 2, 1f);

        Gfx.Icon(glyph, circleR, 17, active ? Th.Brand : Th.Text);
        if (label.Length > 0)
        {
            var tr = Gfx.Text(label, 11.5f, active ? 600 : 500, 74, 1);
            Gfx.Draw(tr, Gfx.Snap(cx - tr.W / 2), topY + size + 4, active ? Th.Brand : Th.Sub);
        }
        return UI.Click(id, circleR, playSfx: playSfx);
    }

    public void Draw(R r)
    {
        if (_volSaveAt > 0 && UI.Now >= _volSaveAt) FlushVolume();
        if (!UI.Down)
        {
            if (_dragSeek && _dragSeekTarget >= 0) { MpvPlayer.Instance.SeekAbsolute(_dragSeekTarget); _dragSeekTarget = -1; }
            _dragVol = false;
            _dragSeek = false;
        }

        var v = Cur;
        if (v == null)
        {
            _commentEdit?.Hide();
            if (_loading) UI.Spinner(r.X + r.W / 2, r.Y + r.H / 2, 16, Th.Sub);
            else
            {
                var msg = _failed ? L.T("Shorts не загрузились.", "Could not load Shorts.") : L.T("Здесь пока пусто.", "Nothing here yet.");
                Gfx.Draw(msg, r.X + r.W / 2 - 90, r.Y + r.H / 2 - 30, 15, Th.Sub);
                if (_feed && UI.Pill(UI.Id("shorts:retry"), r.X + r.W / 2 - 60, r.Y + r.H / 2 + 4, 36, Th.Refresh, L.T("Повторить", "Retry"), out _)) App.Post(Refresh);
            }
            return;
        }

        bool hidden = AppSettings.Current.ShortsPanelHidden;
        float panelT = UI.Smooth(UI.Id("shorts:panelT"), hidden ? 0 : 1, 16);
        const float railW = 68; // Always-visible YouTube circular action rail right beside the Short!
        float fullSide = MathF.Min(410, MathF.Max(320, r.W * 0.32f));
        float side = fullSide * panelT;
        float panelGap = 16 * panelT;

        var vh = MathF.Floor(MathF.Min(r.H - 60, (r.W - railW - side - panelGap - 56) * 16 / 9));
        vh = MathF.Max(260, vh);
        var vw = MathF.Floor(vh * 9 / 16);
        _videoW = vw; _videoH = vh;

        float totalW = vw + 12 + railW + panelGap + side;
        var vx = Gfx.Snap(r.X + MathF.Max(18, (r.W - totalW) / 2));
        var vy = Gfx.Snap(r.Y + 14);
        var video = new R(vx, vy, vw, vh);
        float railX = video.Right + 12;
        var panelR = new R(railX + railW + panelGap, video.Y, MathF.Min(fullSide, r.Right - (railX + railW + panelGap) - 20), video.H);

        // Cinema GPU Ambient Mode glow behind the Short
        Gfx.PushClip(r);
        Ambient.Draw(video, v.VideoId, isShort: true);
        Gfx.PopClip();

        // Smooth cubic-out vertical card-stack swipe animation — completely BORDERLESS (no ugly outline!)
        float slideT = _slideAt > 0 ? Math.Clamp((UI.Now - _slideAt) / 250f, 0, 1) : 1f;
        bool sliding = slideT < 1f;
        if (sliding) UI.Animate();
        float ease = 1f - MathF.Pow(1f - slideT, 3f);
        float stride = vh + 16;
        float curY = sliding ? Gfx.Snap(video.Y + (1f - ease) * stride * _slideFrom) : video.Y;

        void DrawShortPoster(string vid, R rect, float opacity)
        {
            if (rect.Bottom <= r.Y || rect.Y >= r.Bottom) return;
            Gfx.Fill(rect, 0xFF0A0A0E, 14);
            var img = Images.Get($"https://i.ytimg.com/vi/{vid}/oar2.jpg", rect.W, rect.H)
                   ?? Images.Get($"https://i.ytimg.com/vi/{vid}/hq720.jpg", rect.W, rect.H);
            if (img != null)
            {
                Gfx.Image(img.Bmp!, rect, opacity * Images.Alpha(img));
                Gfx.RoundOff(rect, 14, Th.Bg);
            }
        }

        var stackClip = new R(video.X - 2, r.Y, video.W + 4, r.H);
        float swipeVelY = sliding ? -3f * (1f - slideT) * (1f - slideT) * stride * _slideFrom / 0.25f : 0f;
        bool swipeMb = Gfx.PushMotionBlur(stackClip, 0f, swipeVelY);
        Gfx.PushClip(stackClip);
        if (sliding && _prevVideoId != null)
        {
            var outR = new R(video.X, curY - stride * _slideFrom, video.W, video.H);
            DrawShortPoster(_prevVideoId, outR, 0.92f - 0.15f * ease);
        }
        if (NextItem is { } peekNext)
        {
            var peekR = new R(video.X, curY + stride, video.W, video.H);
            if (peekR.Y < r.Bottom)
            {
                DrawShortPoster(peekNext.VideoId, peekR, 0.52f);
                if (!sliding && UI.Click(UI.Id("shorts:peeknext"), peekR.Intersect(r))) Step(1);
            }
        }
        if (_i > 0 && sliding)
        {
            var prevPeek = new R(video.X, curY - stride, video.W, video.H);
            if (prevPeek.Bottom > r.Y && _items[_i - 1].VideoId != _prevVideoId)
                DrawShortPoster(_items[_i - 1].VideoId, prevPeek, 0.52f);
        }

        if (sliding)
        {
            DrawShortPoster(v.VideoId, new R(video.X, curY, video.W, video.H), 0.96f);
        }
        else
        {
            // Settled: crisp borderless rounded live video surface
            Gfx.Fill(video, 0xFF0A0A0E, 14);
            bool audioOnlyQuality = AppSettings.Current.PlayerQuality == "audio";
            if (!audioOnlyQuality) Shell.WantVideo(video, v.VideoId, radius: 14);
            if (audioOnlyQuality || !Shell.VideoShown(v.VideoId))
            {
                DrawShortPoster(v.VideoId, video, audioOnlyQuality ? 0.96f : 0.85f);
                if (!audioOnlyQuality) UI.Spinner(video.X + vw / 2, video.Y + vh / 2, 14, 0xFFFFFFFF);
            }
        }
        Gfx.PopClip();
        Gfx.PopMotionBlur(swipeMb);
        if (UI.RightClick(video)) ContextMenuFor(v);

        // ---- ALWAYS-VISIBLE YOUTUBE-STYLE VERTICAL CIRCULAR ACTION RAIL (bottom-right of the Short!) ----
        float rcx = Gfx.Snap(railX + railW / 2);
        float avSize = 42;
        float itemStep = Math.Clamp((video.H - avSize - 14) / 5f, 54f, 72f);
        float railItemsH = itemStep * 5 + avSize + 6;
        float ry = MathF.Max(video.Y + 4, video.Bottom - railItemsH - 4);
        var mine = _rating.GetValueOrDefault(v.VideoId, "");
        var likeLbl = v.Meta.Length > 0 ? v.Meta : L.T("Лайк", "Like");
        var disLbl = _dislikes.TryGetValue(v.VideoId, out var dlCnt) && dlCnt.Length > 0 ? dlCnt : L.T("Не нрав.", "Dislike");
        var commLbl = _commentsFor == v.VideoId && _commentCount is { Length: > 0 } cc0 ? cc0
                    : (_commCounts.TryGetValue(v.VideoId, out var cc1) ? cc1 : L.T("Комм.", "Chat"));

        if (RailCircleButton(UI.Id("shorts:rlike"), rcx, ry, Th.Like, likeLbl, mine == "LIKE", playSfx: false)) Rate(v, true);
        ry += itemStep;
        if (RailCircleButton(UI.Id("shorts:rdis"), rcx, ry, Th.Dislike, disLbl, mine == "DISLIKE", playSfx: false)) Rate(v, false);
        ry += itemStep;
        if (RailCircleButton(UI.Id("shorts:rcomm"), rcx, ry, Th.Comment, commLbl, _showComments && !hidden)) ToggleComments();
        ry += itemStep;
        if (RailCircleButton(UI.Id("shorts:rshare"), rcx, ry, Th.Share, L.T("Поделиться", "Share"), false)) Cards.Copy("https://youtube.com/shorts/" + v.VideoId);
        ry += itemStep;
        if (RailCircleButton(UI.Id("shorts:rpanel"), rcx, ry, hidden ? Th.Theater : Th.More, hidden ? L.T("Панель", "Panel") : L.T("Скрыть", "Hide"), !hidden)) TogglePanel();
        ry += itemStep + 4;

        // Rounded-square Channel Avatar at the bottom of the YouTube action rail!
        var railAv = new R(rcx - avSize / 2, ry, avSize, avSize);
        var avImg = Images.Get(v.AvatarUrl, avSize, avSize, round: false);
        if (avImg != null)
        {
            Gfx.Image(avImg.Bmp!, railAv, Images.Alpha(avImg));
            Gfx.RoundOff(railAv, 10, Th.Bg);
        }
        else
        {
            Gfx.Fill(railAv, Th.Card, 10);
            var cname0 = v.Channel.Length > 0 ? v.Channel.TrimStart('@') : "S";
            var init = System.Globalization.StringInfo.GetNextTextElement(cname0).ToUpperInvariant();
            var ir = Gfx.Text(init, 16, 600);
            Gfx.Draw(ir, railAv.X + (avSize - ir.W) / 2, railAv.Y + (avSize - ir.H) / 2, Th.Sub);
        }
        Gfx.Stroke(railAv, UI.Hover(railAv) && v.ChannelId != null ? Th.Brand : Th.A(0.22f), 10, 1.3f);
        if (v.ChannelId != null && UI.Click(UI.Id("shorts:rav"), railAv, playSfx: false))
        {
            var cid = v.ChannelId; var cn = v.Channel;
            App.Post(() => Shell.OpenChannel(cid, cn));
        }

        // Top-right quick Up/Down step arrows above the rail when panel is collapsed
        if (panelT < 0.05f)
        {
            _commentEdit?.Hide();
            float navTop = video.Y + 6;
            if (ry - navTop > 88)
            {
                var upR = new R(rcx - 19, navTop, 38, 36);
                Gfx.Fill(upR, Th.A(0.09f), 18);
                if (UI.IconButton(UI.Id("shorts:qup"), upR, Th.ChevronUp, 13, Th.Text, _i > 0)) Step(-1);
                var dnR = new R(rcx - 19, navTop + 42, 38, 36);
                Gfx.Fill(dnR, Th.A(0.09f), 18);
                if (UI.IconButton(UI.Id("shorts:qdn"), dnR, Th.ChevronDown, 13, Th.Brand, _i < _items.Count - 1 || _next != null)) Step(1);
            }
            if (!AppSettings.Current.ShortsInfoOverlay)
            {
                float tx = railX + railW + 18;
                float tw = MathF.Min(260, r.Right - tx - 18);
                if (tw > 120)
                {
                    float ty = video.Y + 12;
                    var cname = v.Channel.Length > 0 ? v.Channel : "…";
                    var cnr = Gfx.Text(cname, 15, 600, tw);
                    var cnrRect = new R(tx, ty, cnr.W, cnr.H);
                    Gfx.Draw(cnr, cnrRect.X, cnrRect.Y, UI.Hover(cnrRect) && v.ChannelId != null ? Th.Brand : Th.Text);
                    if (v.ChannelId != null && UI.Click(UI.Id("shorts:cch"), cnrRect, playSfx: false))
                    {
                        var cid = v.ChannelId; var cn = cname;
                        App.Post(() => Shell.OpenChannel(cid, cn));
                    }
                    if (v.Title.Length > 0)
                    {
                        var tr0 = Sel.Text(UI.Id("shorts:ctitle"), v.Title, tx, ty + cnr.H + 10, 14, Th.Sub, 500, tw, 6, links: true,
                            onLink: l => { if (l.Kind == '#') Shell.OpenSearch(l.Value); else if (l.Kind == 'u') W.Open(l.Value); });
                        Cards.CopyMenu(new R(tx, ty + cnr.H + 10, tr0.W, tr0.H), v.Title, L.T("Копировать название", "Copy title"));
                    }
                }
            }
            if (UI.Wheel != 0)
            {
                if (UI.Hover(video) && W.KeyDown(W.VK_SHIFT) && AppSettings.Current.ShiftWheelFrameStep)
                    Shell.StepVideoFrame(UI.Wheel);
                else if (UI.Hover(r))
                    Step(UI.Wheel > 0 ? -1 : 1);
                UI.Wheel = 0;
            }
            return;
        }

        if (panelR.W < 200)
        {
            _commentEdit?.Hide();
            if (UI.Wheel != 0 && UI.Hover(r)) { Step(UI.Wheel > 0 ? -1 : 1); UI.Wheel = 0; }
            return;
        }

        // ---- Structured Right-Side Card (Playback Deck, Comments + Composer, Up-Next) ----
        Gfx.PushClip(new R(panelR.X - 2, panelR.Y - 2, side + 4, panelR.H + 4));
        Gfx.Fill(panelR, Th.Panel, 16);
        Gfx.Stroke(panelR, Th.Line, 16, 1f);
        if (UI.RightClick(panelR)) ContextMenuFor(v);

        float pad = 16;
        float x = panelR.X + pad, w = panelR.W - pad * 2, y = panelR.Y + 14;

        // 1. HEADER ZONE
        var name = v.Channel.Length > 0 ? v.Channel : "…";
        var avR = new R(x, y, 40, 40);
        Cards.Avatar(avR.X, avR.Y, 40, v.AvatarUrl, name.TrimStart('@'));
        if (v.ChannelId != null && UI.Hover(avR)) Gfx.Stroke(avR.Inset(-1), Gfx.Fade(Th.Brand, 0.8f), 21, 1.5f);

        var hideBtn = new R(panelR.Right - pad - 28, y + 6, 28, 28);
        if (UI.IconButton(UI.Id("shorts:hidepanel"), hideBtn, Th.ChevronRight, 11, Th.Sub)) TogglePanel();

        var badge = (_feed ? $"Shorts #{_i + 1}" : $"{_i + 1} / {_items.Count}")
            + (ShortsAdBlocker.BlockedSessionCount > 0 ? $" · -{ShortsAdBlocker.BlockedSessionCount} ad" : "");
        var br = Gfx.Text(badge, 11.5f, 600);
        var badgeR = new R(hideBtn.X - 6 - br.W - 14, y + 8, br.W + 14, 24);
        Gfx.Fill(badgeR, Th.A(0.08f), 12);
        Gfx.Draw(br, badgeR.X + 7, badgeR.Y + 4, Th.Sub);

        var nr = Gfx.Text(name, 14.5f, 600, w - 50 - badgeR.W - 40);
        var nameR = new R(x + 50, y + 3, nr.W, nr.H);
        Gfx.Draw(nr, nameR.X, nameR.Y, UI.Hover(nameR) && v.ChannelId != null ? Th.Brand : Th.Text);
        Gfx.Draw(v.ChannelId != null ? L.T("Перейти на канал", "Open channel") : "YouTube Shorts", x + 50, y + 22, 11.5f, Th.Dim, 400, MathF.Max(40, w - 50 - badgeR.W - 40));
        if (v.ChannelId != null && (UI.Click(UI.Id("shorts:ch"), new R(x + 50, y + 2, nr.W + 20, 36), playSfx: false) || UI.Click(UI.Id("shorts:av"), avR, playSfx: false)))
        {
            var cid = v.ChannelId; var cn = name;
            App.Post(() => Shell.OpenChannel(cid, cn));
        }
        y += 50;

        if (v.Title.Length > 0)
        {
            var tr = Sel.Text(UI.Id("shorts:title"), v.Title, x, y, 15, Th.Text, 600, w, _showComments ? 2 : 4, links: true,
                onLink: l => { if (l.Kind == '#') Shell.OpenSearch(l.Value); else if (l.Kind == 'u') W.Open(l.Value); });
            Cards.CopyMenu(new R(x, y, tr.W, tr.H), v.Title, L.T("Копировать название", "Copy title"));
            y += tr.H + 14;
        }
        else { Gfx.Fill(new R(x, y, w * 0.78f, 15), UI.Pulse(Th.Thumb), 5); y += 28; }

        // 2. PLAYBACK & SMOOTH VOLUME DECK
        var p = MpvPlayer.Instance;
        var muted = p.Muted;
        var deck = new R(x, y, w, 84);
        Gfx.Fill(deck, Th.Card, 12);
        Gfx.Stroke(deck, Th.A(0.08f), 12, 1f);

        double dur = p.Duration, pos = p.TimePos;
        float sx = deck.X + 14, sw = deck.W - 28;
        var curRun = Gfx.Text(Fmt.Time(pos), 11.5f, 600);
        var durRun = Gfx.Text(dur > 0 ? Fmt.Time(dur) : "0:00", 11.5f, 400);
        Gfx.Draw(curRun, sx, deck.Y + 9, Th.Text);
        Gfx.Draw(durRun, sx + sw - durRun.W, deck.Y + 9, Th.Sub);

        var seekTrack = new R(sx, deck.Y + 28, sw, 5);
        var seekHit = new R(sx, deck.Y + 19, sw, 22);
        var seekHot = UI.Hover(seekHit) || _dragSeek;
        if (dur > 0)
        {
            if (UI.Pressed && UI.Hover(seekHit))
            {
                _dragSeek = true;
                _dragSeekTarget = dur * Math.Clamp((UI.MX - sx) / sw, 0, 1);
                _lastDragSeekAt = UI.Now;
                p.SeekAbsolute(_dragSeekTarget);
            }
            if (_dragSeek)
            {
                _dragSeekTarget = dur * Math.Clamp((UI.MX - sx) / sw, 0, 1);
                if (UI.Now - _lastDragSeekAt >= 55) { _lastDragSeekAt = UI.Now; p.SeekAbsolute(_dragSeekTarget); }
                pos = _dragSeekTarget;
            }
            if (seekHot) UI.Hand();
        }
        double prog = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
        UI.Progress(seekTrack, prog, Th.A(0.2f), Th.Brand);
        if (seekHot && dur > 0) Gfx.Circle(sx + (float)(sw * prog), seekTrack.Y + 2.5f, 6, Th.Brand);

        float ctrlY = deck.Y + 42;
        var playBtn = new R(deck.X + 10, ctrlY, 34, 34);
        Gfx.Fill(playBtn, Gfx.Fade(Th.Brand, p.Paused ? 0.22f : 0.14f), 17);
        if (UI.IconButton(UI.Id("shorts:play"), playBtn, p.Paused ? Th.Play : Th.Pause, 13.5f, Th.Brand))
            p.TogglePause();

        var loopBtn = new R(playBtn.Right + 6, ctrlY, 34, 34);
        if (_loop) Gfx.Fill(loopBtn, Th.A(0.08f), 17);
        if (UI.IconButton(UI.Id("shorts:loop"), loopBtn, Th.Repeat, 13, _loop ? Th.Brand : Th.Dim))
            ToggleLoop();

        int vol = muted ? 0 : Math.Clamp((int)Math.Round(p.IsReady ? p.Volume : AppSettings.Current.Volume), 0, 100);
        float volAreaX = loopBtn.Right + 12;
        var volZone = new R(volAreaX, ctrlY, deck.Right - 12 - volAreaX, 34);
        bool wheelConsumedByVol = false;
        if (UI.Wheel != 0 && UI.Hover(volZone))
        {
            AdjustVolume(UI.Wheel > 0 ? 5 : -5);
            UI.Wheel = 0;
            wheelConsumedByVol = true;
        }

        var muteBtn = new R(volZone.X, ctrlY, 28, 34);
        if (UI.IconButton(UI.Id("shorts:mute"), muteBtn, muted || vol == 0 ? Th.Mute : Th.Volume, 14, muted ? Th.Warn : Th.Text))
            ToggleMute();

        var volPct = Gfx.Text($"{vol}%", 11.5f, 600);
        Gfx.Draw(volPct, volZone.Right - volPct.W, ctrlY + (34 - volPct.H) / 2, Th.Sub);

        float vtx = muteBtn.Right + 6;
        float vtw = MathF.Max(36, volZone.Right - volPct.W - 8 - vtx);
        var volTrack = new R(vtx, ctrlY + 14.5f, vtw, 5);
        var volHit = new R(vtx - 4, ctrlY + 4, vtw + 8, 26);
        var volHot = UI.Hover(volHit) || _dragVol;
        if (UI.Pressed && UI.Hover(volHit)) _dragVol = true;
        if (_dragVol && UI.Now - _lastDragVolAt >= 45)
        {
            _lastDragVolAt = UI.Now;
            SetVolume((int)MathF.Round(Math.Clamp((UI.MX - vtx) / vtw, 0, 1) * 100), smooth: false);
        }
        if (volHot) UI.Hand();
        UI.Progress(volTrack, vol / 100.0, Th.A(0.2f), muted ? Th.Dim : Th.Brand);
        Gfx.Circle(vtx + vtw * (vol / 100f), volTrack.Y + 2.5f, volHot ? 6f : 4.5f, muted ? Th.Sub : Th.Text);

        y += deck.H + 12;

        // 3. QUICK ACTIONS GRID: 4 crisp tiles
        float tileGap = 8, tileW = (w - tileGap * 3) / 4, tileH = 48;
        ActionTile(new R(x, y, tileW, tileH), UI.Id("shorts:dl"), Th.Download, L.T("Скачать", "Save"), () => Cards.Download(v, null));
        ActionTile(new R(x + (tileW + tileGap), y, tileW, tileH), UI.Id("shorts:commt"), Th.Comment, L.T("Комм.", "Chat"), ToggleComments);
        ActionTile(new R(x + (tileW + tileGap) * 2, y, tileW, tileH), UI.Id("shorts:open"), Th.Expand, L.T("Плеер", "Player"), () =>
        {
            var vv = v;
            App.Post(() => Shell.OpenVideo(new VideoItem { VideoId = vv.VideoId, Title = vv.Title, Channel = vv.Channel, ChannelId = vv.ChannelId }, "shorts-open"));
        });
        ActionTile(new R(x + (tileW + tileGap) * 3, y, tileW, tileH), UI.Id("shorts:tab"), Th.Add, L.T("Вкладка", "Tab"), () => Shell.OpenVideoInNewTab(v));
        y += tileH + 12;

        float footerY = panelR.Bottom - 54;

        // 4. MIDDLE ZONE: Comments Drawer (with comment composer!) OR Up-Next Card
        if (_showComments && footerY - y > 100)
        {
            Gfx.Fill(new R(x, y, w, 1), Th.Line);
            y += 8;
            var chTitle = _commentCount is { Length: > 0 }
                ? (_commentCount.Any(char.IsLetter) ? _commentCount : L.T($"Комментарии · {_commentCount}", $"Comments · {_commentCount}"))
                : L.T("Комментарии", "Comments");
            Gfx.Draw(chTitle, x, y + 3, 13.5f, Th.Text, 600);
            if (UI.IconButton(UI.Id("shorts:commclose"), new R(x + w - 24, y, 24, 24), Th.Close, 10, Th.Sub))
            {
                _showComments = false;
                _commentEdit?.Hide();
            }
            y += 28;

            // Inline comment composer inside Shorts drawer
            if (CookieStore.IsLoggedIn && !_commentsOff)
            {
                if (_commentEdit == null)
                {
                    _commentEdit = new Edit(L.T("Написать комментарий…", "Add a comment…")) { Bg = () => Th.Field };
                    _commentEdit.Enter = SubmitComment;
                }
                else _commentEdit.SetPlaceholder(L.T("Написать комментарий…", "Add a comment…"));
                const float sendSize = 32;
                var boxR = new R(x, y, MathF.Max(100, w - sendSize - 6), 34);
                Gfx.Fill(boxR, Th.Field, 9);
                Gfx.Stroke(boxR, _commentEdit.Focused ? Gfx.Fade(Th.Brand, 0.8f) : Th.Line, 9, _commentEdit.Focused ? 1.2f : 1f);
                _commentEdit.Place(boxR, 10, 10);
                var sendR = new R(boxR.Right + 6, y + 1, sendSize, sendSize);
                bool canSend = !_postingComment && !string.IsNullOrWhiteSpace(_commentEdit.Text);
                Gfx.Fill(sendR, canSend ? Th.Brand : Th.A(0.08f), 9);
                if (UI.IconButton(UI.Id("shorts:csend"), sendR, "\uE724", 13, canSend ? Th.OnBrand : Th.Dim, canSend))
                    App.Post(SubmitComment);
                y += 42;
            }
            else _commentEdit?.Hide();

            var commView = new R(x, y, w + 8, MathF.Max(40, footerY - y - 10));
            DrawCommentsDrawer(commView);
        }
        else
        {
            _commentEdit?.Hide();
            if (footerY - y > 92)
            {
                Gfx.Fill(new R(x, y, w, 1), Th.Line);
                y += 12;
                if (NextItem is { } nxt)
                {
                    Gfx.Draw(L.T("Далее в ленте", "Up next"), x, y, 12, Th.Dim, 600);
                    y += 20;
                    var upNext = new R(x, y, w, 68);
                    Gfx.Fill(upNext, Th.Card, 10);
                    var glow = UI.Glow(UI.Id("shorts:nextcard"), upNext, 10, UI.Hover(upNext));
                    var thR = new R(upNext.X + 8, upNext.Y + 8, 36, 52);
                    Cards.ThumbUrl(thR, $"https://i.ytimg.com/vi/{nxt.VideoId}/oar2.jpg", 6, UI.GlowOn(Th.Card, glow));
                    float ntx = thR.Right + 12, ntw = upNext.Right - 12 - ntx;
                    var ntTitle = nxt.Title.Length > 0 ? nxt.Title : L.T("Следующий Shorts…", "Next Short…");
                    var ntRun = Gfx.Draw(ntTitle, ntx, upNext.Y + 12, 13, Th.Text, 600, ntw, 2);
                    if (nxt.Channel.Length > 0) Gfx.Draw(nxt.Channel, ntx, upNext.Y + 14 + ntRun.H, 11.5f, Th.Sub, 400, ntw);
                    if (UI.Click(UI.Id("shorts:nextcard"), upNext)) Step(1);
                }

                float hy = footerY - 28;
                if (hy > y + 74)
                    Gfx.Draw(L.T("Колесо / ↑↓ — лента · Shift+колесо — по кадрам · Space — пауза", "Wheel / ↑↓ — feed · Shift+wheel — frame step · Space — pause"), x, hy, 11.5f, Th.Dim, 400, w);
            }
        }

        // 5. BOTTOM NAVIGATION FOOTER
        Gfx.Fill(new R(x, footerY - 8, w, 1), Th.Line);
        var prevBtn = new R(x, footerY, (w - 10) / 2, 38);
        var nextBtn = new R(prevBtn.Right + 10, footerY, (w - 10) / 2, 38);
        bool canPrev = _i > 0, canNext = _i < _items.Count - 1 || _next != null;

        Gfx.Fill(prevBtn, Th.A(canPrev ? 0.08f : 0.03f), 10);
        if (canPrev) UI.Glow(UI.Id("shorts:up"), prevBtn, 10, UI.Hover(prevBtn));
        var pt = Gfx.Text(L.T("Предыдущий", "Previous"), 13, 600);
        float px = prevBtn.X + (prevBtn.W - (18 + 6 + pt.W)) / 2;
        Gfx.Icon(Th.ChevronUp, new R(px, prevBtn.Y, 18, prevBtn.H), 12, canPrev ? Th.Text : Th.Dim);
        Gfx.Draw(pt, px + 24, prevBtn.Y + (prevBtn.H - pt.H) / 2, canPrev ? Th.Text : Th.Dim);
        if (canPrev && UI.Click(UI.Id("shorts:up"), prevBtn)) Step(-1);

        Gfx.Fill(nextBtn, canNext ? Gfx.Fade(Th.Brand, 0.16f) : Th.A(0.03f), 10);
        if (canNext) UI.Glow(UI.Id("shorts:down"), nextBtn, 10, UI.Hover(nextBtn));
        var nt = Gfx.Text(L.T("Следующий", "Next"), 13, 600);
        float nx = nextBtn.X + (nextBtn.W - (18 + 6 + nt.W)) / 2;
        Gfx.Draw(nt, nx, nextBtn.Y + (nextBtn.H - nt.H) / 2, canNext ? Th.Text : Th.Dim);
        Gfx.Icon(Th.ChevronDown, new R(nx + nt.W + 6, nextBtn.Y, 18, nextBtn.H), 12, canNext ? Th.Brand : Th.Dim);
        if (canNext && UI.Click(UI.Id("shorts:down"), nextBtn)) Step(1);

        if (_loading) UI.Spinner(panelR.Right - 28, panelR.Y + 20, 7, Th.Sub);
        Gfx.PopClip();

        if (!wheelConsumedByVol && UI.Wheel != 0)
        {
            if (UI.Hover(video))
            {
                if (W.KeyDown(W.VK_SHIFT) && AppSettings.Current.ShiftWheelFrameStep) Shell.StepVideoFrame(UI.Wheel);
                else if (AppSettings.Current.WheelVolumeOnVideo) AdjustVolume(UI.Wheel > 0 ? 5 : -5);
                else Step(UI.Wheel > 0 ? -1 : 1);
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
        Gfx.Icon(glyph, new R(tile.X, tile.Y + 5, tile.W, 20), 14, over ? Th.Text : Th.Icon);
        var tr = Gfx.Text(label, 11.5f, 500, tile.W - 8);
        Gfx.Draw(tr, Gfx.Snap(tile.X + (tile.W - tr.W) / 2), tile.Y + 27, over ? Th.Text : Th.Sub);
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
            Gfx.Draw(L.T("Комментарии отключены или недоступны.", "Comments are turned off or unavailable."), view.X, view.Y + 8, 13, Th.Sub, 400, view.W - 12);
            return;
        }
        if (_comments.Count == 0)
        {
            Gfx.Draw(L.T("Пока нет комментариев.", "No comments yet."), view.X, view.Y + 8, 13, Th.Sub);
            return;
        }

        float tw = MathF.Max(80, view.W - 54);
        float totalH = 12;
        for (int i = 0; i < _comments.Count; i++)
        {
            var row = _comments[i];
            if (row.H <= 0 || MathF.Abs(row.W - tw) > 1f)
            {
                var tr = Gfx.Text(row.C.Text, 13, 400, tw, 4);
                row.H = 22 + tr.H + 24;
                if (tr.Layout != null) row.W = tw;
            }
            totalH += row.H;
        }
        if (_commentTok != null || _commentsLoading) totalH += 44;

        var scroll = UI.BeginScroll(_commScroll, view, totalH);
        float cy = view.Y - scroll;
        for (int i = 0; i < _comments.Count; i++)
        {
            var row = _comments[i];
            var c = row.C;
            float rh = row.H;
            if (cy + rh > view.Y - 20 && cy < view.Bottom + 20)
            {
                var tr = Gfx.Text(c.Text, 13, 400, tw, 4);
                Cards.Avatar(view.X, cy + 2, 30, c.Avatar, c.Author.TrimStart('@'));
                float tx = view.X + 40;
                var au = Gfx.Draw(c.Author, tx, cy, 12.5f, Th.Text, 600, tw - 60);
                Gfx.Draw(c.When, tx + au.W + 8, cy + 1, 11.5f, Th.Dim, 400, MathF.Max(20, tw - au.W - 8));
                Gfx.Draw(tr, tx, cy + 19, Gfx.Mix(Th.Text, Th.Sub, 0.1f));
                Gfx.Icon(Th.Like, new R(tx - 2, cy + 21 + tr.H, 16, 18), 11, Th.Dim);
                Gfx.Draw(c.Likes, tx + 16, cy + 22 + tr.H, 11.5f, Th.Dim);
                if (UI.RightClick(new R(view.X, cy, view.W - 10, rh)))
                    Menus.Popup([
                        new(L.T("Копировать комментарий", "Copy comment"), () => Cards.CopyText(c.Text, L.T("Комментарий скопирован", "Comment copied")), icon: Th.Copy),
                        new(L.T("Копировать ник", "Copy handle"), () => Cards.CopyText(c.Author, L.T("Ник скопирован", "Handle copied")), icon: Th.Account),
                    ]);
            }
            cy += rh;
        }
        if (_commentsLoading) UI.Spinner(view.X + view.W / 2, cy + 16, 8, Th.Sub);
        else if (_commentTok != null && cy < view.Bottom + 200) LoadMoreComments();
        UI.EndScroll();
    }
}
