using System.Diagnostics;
using FluxTube.Models;
using FluxTube.Pages;
using FluxTube.Player;
using FluxTube.Services;
using FeedPage = FluxTube.Pages.FeedPage;

namespace FluxTube;

public interface IPage
{
    string Tag { get; }
    int MainScroll { get; }
    void Draw(R r);
    void Shown();
    void Hidden();
    void Refresh();
}

/// <summary>Window chrome, navigation, the video surface placement and everything global.</summary>
public static class Shell
{
    private static readonly MpvPlayer P = MpvPlayer.Instance;
    private static readonly PlaybackTracker T = PlaybackTracker.Instance;

    private static readonly List<IPage> _back = [];
    private static readonly List<IPage> _forward = [];
    private static readonly Dictionary<string, IPage> _roots = new();
    private static IPage? _cur;
    private static Edit _search = null!;
    private static R _searchBox;
    private static int _suggestGen;
    private static Task? _mpvInit;

    private static bool _fullscreen;
    private static W.WINDOWPLACEMENT _savedPlacement;
    private static nint _savedStyle;
    private static bool _mini = true;

    private static R _videoRect, _videoClip, _hostRect;
    private static bool _videoWanted, _hostVisible;
    private static string? _videoFor;
    private static W.RECT _hostPx;
    public static bool VideoReady { get; set; }

    private static string _toast = "";
    private static uint _toastColor;
    private static long _toastUntil;

    private static Process? _helper;
    private static DateTime _cookieStamp;
    private static string[] _args = [];
    private static int _benchStep = -1;
    private static double _testNextAt = -1, _testClickAt = -1;
    private static int _testClickIndex = 7;

    public const float TopH = 52, SideW = 76, BarH = 62, TabH = 36;

    /// <summary>
    /// A tab is three strings plus background-playback state. Only the tab you are looking at has a page object;
    /// switching drops the other one and builds it again from this reference (feeds and links are cached, so it is instant).
    /// Video tabs can also keep playing in the background via BgPlayers so 2+ videos run simultaneously.
    /// </summary>
    public sealed class Tab
    {
        public required string Kind;  // video | root | search | channel | playlist
        public required string Arg;
        public string Title = "";
        public string Sub = "";
        public bool KeepInBg;
    }

    private static readonly List<Tab> _tabs = [];
    private static readonly List<Tab> _closedTabs = [];
    private static int _tab = -1;

    public static IReadOnlyList<Tab> Tabs => _tabs;

    private static string TabLabel(Tab t) => t.Title.Length > 0 ? t.Title : t.Kind switch
    {
        "root" => RootName(t.Arg),
        "search" => "«" + t.Arg + "»",
        "channel" => "Канал",
        "playlist" => "Плейлист",
        _ => "Видео",
    };

    private static string RootName(string tag) => tag switch
    {
        "home" => "Главная", "subs" => "Подписки", "wl" => "Смотреть позже", "liked" => "Понравившиеся",
        "history" => "История", "seen" => "Что я видел", "shorts" => "Shorts", "downloads" => "Загрузки", "settings" => "Настройки", _ => tag,
    };

    private static string TabGlyph(Tab t) => t.Kind switch { "video" => Th.Play, "search" => Th.Search, "channel" => Th.Account, "playlist" => Th.Later, _ => Th.Home };

    /// <summary>The active tab now points here (the user navigated inside it).</summary>
    private static void SetTab(string kind, string arg, string title, string sub = "")
    {
        if (_tab >= 0 && _tab < _tabs.Count)
        {
            var t = _tabs[_tab];
            t.Kind = kind; t.Arg = arg; t.Title = title; t.Sub = sub;
        }
        else
        {
            _tabs.Add(new Tab { Kind = kind, Arg = arg, Title = title, Sub = sub });
            _tab = _tabs.Count - 1;
        }
    }

    /// <summary>Opens a new tab (default: Home) and optionally switches to it immediately.</summary>
    public static void NewTab(string kind = "root", string arg = "home", string title = "", string sub = "", bool activate = true)
    {
        if (_tab < 0) SetTab("root", "home", "");
        var idx = Math.Min(_tab + 1, _tabs.Count);
        _tabs.Insert(idx, new Tab { Kind = kind, Arg = arg, Title = title, Sub = sub });
        if (activate) GoTab(idx);
        else App.Invalidate();
    }

    /// <summary>Parks something for later without leaving what is on screen.</summary>
    public static void OpenInNewTab(string kind, string arg, string title, string sub = "", bool activate = false)
    {
        var existing = _tabs.FindIndex(t => t.Kind == kind && t.Arg == arg);
        if (existing >= 0)
        {
            if (activate) GoTab(existing);
            else Toast("Это уже открыто во вкладке");
            return;
        }
        if (_tab < 0) SetTab("root", "home", "");
        var idx = Math.Min(_tab + 1, _tabs.Count);
        _tabs.Insert(idx, new Tab { Kind = kind, Arg = arg, Title = title, Sub = sub });
        if (activate) GoTab(idx);
        else
        {
            Toast("Открыто во вкладке: " + (title.Length > 0 ? title : arg));
            App.Invalidate();
        }
    }

    public static void OpenVideoInNewTab(VideoItem v, bool activate = false) => OpenInNewTab("video", v.VideoId, v.Title, v.Channel, activate);

    public static void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        var t = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        var idx = Math.Clamp(_tab + 1, 0, _tabs.Count);
        _tabs.Insert(idx, t);
        GoTab(idx);
    }

    public static void DuplicateTab(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        var s = _tabs[i];
        _tabs.Insert(i + 1, new Tab { Kind = s.Kind, Arg = s.Arg, Title = s.Title, Sub = s.Sub });
        GoTab(i + 1);
    }

    private static IPage PageFor(Tab t) => t.Kind switch
    {
        "video" => new WatchPage(new VideoItem { VideoId = t.Arg, Title = t.Title, Channel = t.Sub }, "tab", _listOf.GetValueOrDefault(t.Arg)),
        "search" => new FeedPage("search", t.Arg, null),
        "channel" => new ChannelPage(t.Arg, t.Title),
        "playlist" => new FeedPage("playlist", "VL" + t.Arg, t.Title),
        _ => RootPage(t.Arg),
    };

    /// <summary>
    /// If another open tab owns the video currently playing (unpaused) in the main player,
    /// seamlessly hand it off to a lightweight audio-only BgPlayer before the main player switches to newVideoId.
    /// Paused videos are never handed off (zero wasted RAM/CPU — their resume position is already in DB).
    /// </summary>
    private static void MaybeHandoffToBg(string newVideoId)
    {
        var cur = T.Current;
        if (cur == null || cur.VideoId == newVideoId || !P.HasMedia || P.Paused || T.CurrentSource == "shorts") return;
        // Only keep playing in background if that video is open in another tab
        bool inAnotherTab = false;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i != _tab && _tabs[i].Kind == "video" && _tabs[i].Arg == cur.VideoId)
            {
                inAnotherTab = true;
                break;
            }
        }
        if (!inAnotherTab) return;
        var pos = P.TimePos;
        BgPlayers.Play(cur.VideoId, pos);
    }

    public static void GoTab(int i)
    {
        if (i < 0 || i >= _tabs.Count || i == _tab) return;
        var target = _tabs[i];
        // If leaving a playing video tab to another video tab, hand off the previous video to BgPlayers automatically!
        if (_tab >= 0 && _tab < _tabs.Count)
        {
            var prev = _tabs[_tab];
            if (prev.Kind == "video" && target.Kind == "video" && target.Arg != prev.Arg
                && T.Current?.VideoId == prev.Arg && P.HasMedia && !P.Paused)
            {
                BgPlayers.Play(prev.Arg, P.TimePos);
            }
        }
        // If target video tab is currently playing in background, seamlessly hand its position back to the main player
        if (target.Kind == "video" && BgPlayers.Has(target.Arg))
        {
            var pos = BgPlayers.Stop(target.Arg);
            if (pos > 2)
            {
                var sid = Db.StartWatch(new VideoItem { VideoId = target.Arg, Title = target.Title, Channel = target.Sub }, "tab");
                Db.UpdateWatch(sid, null, null, pos, 0, 0);
            }
        }
        _tab = i;
        _back.Clear(); // the pages of the tab we just left go with it
        _forward.Clear();
        SharePanel.Close();
        Navigate(PageFor(target), push: false);
    }

    public static void CloseTab(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        var removed = _tabs[i];
        if (removed.Kind == "video") BgPlayers.Stop(removed.Arg);
        _closedTabs.Add(new Tab { Kind = removed.Kind, Arg = removed.Arg, Title = removed.Title, Sub = removed.Sub });
        if (_closedTabs.Count > 12) _closedTabs.RemoveAt(0);

        var wasActive = i == _tab;
        _tabs.RemoveAt(i);
        if (_tabs.Count == 0) { _tab = -1; _back.Clear(); OpenRoot("home"); return; }
        if (wasActive)
        {
            _tab = -1;
            GoTab(Math.Min(i, _tabs.Count - 1));
        }
        else if (i < _tab) _tab--;
        App.Invalidate();
    }

    public static void CloseOtherTabs(int keep)
    {
        if (keep < 0 || keep >= _tabs.Count) return;
        var kept = _tabs[keep];
        for (int i = _tabs.Count - 1; i >= 0; i--)
        {
            if (i == keep) continue;
            if (_tabs[i].Kind == "video") BgPlayers.Stop(_tabs[i].Arg);
            _tabs.RemoveAt(i);
        }
        _tab = 0;
        if (_cur == null || keep != _tab) Navigate(PageFor(kept), push: false);
        App.Invalidate();
    }

    public static void CloseTabsRight(int from)
    {
        if (from < 0 || from >= _tabs.Count - 1) return;
        var needGo = _tab > from;
        for (int i = _tabs.Count - 1; i > from; i--)
        {
            if (_tabs[i].Kind == "video") BgPlayers.Stop(_tabs[i].Arg);
            _tabs.RemoveAt(i);
        }
        if (needGo) { _tab = -1; GoTab(from); }
        App.Invalidate();
    }

    private static void TabContextMenu(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        var t = _tabs[i];
        var items = new List<MenuItem>();
        if (t.Kind == "video")
        {
            var isMain = T.Current?.VideoId == t.Arg && P.HasMedia;
            var isBg = BgPlayers.Has(t.Arg);
            if (isMain)
            {
                items.Add(new(P.Paused ? "Продолжить воспроизведение" : "Поставить на паузу", P.TogglePause, icon: P.Paused ? Th.Play : Th.Pause, shortcut: "Space"));
                items.Add(new("Продолжать в фоне при переключении", () => { t.KeepInBg = !t.KeepInBg; Toast(t.KeepInBg ? "Видео продолжит играть в фоне" : "Фоновое воспроизведение выключено", Th.Good, 2200); }, check: t.KeepInBg, icon: Th.Headphones));
            }
            else if (isBg)
            {
                var bgPaused = BgPlayers.IsPaused(t.Arg);
                items.Add(new(bgPaused ? "Продолжить в фоне" : "Пауза (в фоне)", () => BgPlayers.TogglePause(t.Arg), icon: bgPaused ? Th.Play : Th.Pause));
                items.Add(new(BgPlayers.IsMuted(t.Arg) ? "Включить звук" : "Выключить звук", () => BgPlayers.ToggleMute(t.Arg), icon: BgPlayers.IsMuted(t.Arg) ? Th.Volume : Th.Mute));
                items.Add(new("Остановить фоновое воспроизведение", () => BgPlayers.Stop(t.Arg), icon: Th.Stop));
            }
            else
            {
                items.Add(new("Запустить в фоне (звук)", () => { t.KeepInBg = true; BgPlayers.Play(t.Arg); Toast("Играет в фоне: " + TabLabel(t), Th.Good, 2000); }, icon: Th.Headphones));
            }
            items.Add(MenuItem.Sep);
        }
        items.Add(new("Новая вкладка", () => { NewTab(); _search.Focus(); }, icon: Th.Add, shortcut: "Ctrl+T"));
        items.Add(new("Дублировать вкладку", () => DuplicateTab(i), icon: Th.Copy));
        if (_closedTabs.Count > 0) items.Add(new("Открыть закрытую вкладку", ReopenClosedTab, icon: Th.Restart, shortcut: "Ctrl+Shift+T"));
        items.Add(MenuItem.Sep);
        items.Add(new("Закрыть вкладку", () => CloseTab(i), icon: Th.Close, shortcut: "Ctrl+W", danger: true));
        if (_tabs.Count > 1) items.Add(new("Закрыть другие вкладки", () => CloseOtherTabs(i), icon: Th.Close));
        if (i < _tabs.Count - 1) items.Add(new("Закрыть вкладки справа", () => CloseTabsRight(i), icon: Th.Close));
        Menus.Popup(items);
    }

    /// <summary>The page learned the real title: the tab shows it too.</summary>
    public static void UpdateTabTitle(string kind, string arg, string title, string sub = "")
    {
        foreach (var t in _tabs)
            if (t.Kind == kind && t.Arg == arg && (t.Title != title || t.Sub != sub))
            {
                t.Title = title;
                if (sub.Length > 0) t.Sub = sub;
                App.Invalidate();
            }
    }

    private static void DrawTabs(R strip)
    {
        Gfx.Fill(strip, Th.Bg);
        Gfx.Fill(new R(strip.X, strip.Bottom - 1, strip.W, 1), Th.Line);
        const float addW = 30;
        float avail = MathF.Max(100, strip.W - 12 - addW - 6);
        float tw = Math.Clamp(avail / Math.Max(1, _tabs.Count), 108, 250);
        float x = strip.X + 6;
        for (int i = 0; i < _tabs.Count && x < strip.Right - addW - 10; i++)
        {
            var t = _tabs[i];
            var r = new R(Gfx.Snap(x), strip.Y + 3, tw - 4, TabH - 7);
            var active = i == _tab;
            var id = UI.Id("tab", i);
            var over = UI.Hover(r);
            var sel = UI.Smooth(id ^ 0x4242, active ? 1 : 0, 20);
            if (sel > 0.01f) Gfx.Fill(r, Th.A(0.11f * sel), 8);
            if (!active) UI.Glow(id, r, 8, over);

            var isVideo = t.Kind == "video";
            var isMainPlaying = isVideo && T.Current?.VideoId == t.Arg && P.HasMedia;
            var isBg = isVideo && BgPlayers.Has(t.Arg);
            var playingNow = (isMainPlaying && !P.Paused) || (isBg && BgPlayers.IsPlaying(t.Arg));
            var bgLoading = isBg && BgPlayers.IsLoading(t.Arg);

            // Subtle accent indicator bar at bottom of active or playing tab
            if (playingNow && !active)
                Gfx.Fill(new R(r.X + 10, r.Bottom - 2, r.W - 20, 2), Gfx.Fade(Th.Brand, 0.85f), 1);

            // Left icon slot: for video tabs, interactive Play/Pause button right on the tab!
            var iconR = new R(r.X + 4, r.Y + (r.H - 22) / 2, 22, 22);
            bool iconHandled = false;
            if (isVideo)
            {
                if (bgLoading)
                {
                    UI.Spinner(iconR.X + 11, iconR.Y + 11, 5.5f, Th.Brand);
                }
                else
                {
                    var glyph = playingNow ? Th.Pause : Th.Play;
                    var col = playingNow ? Th.Brand : (isMainPlaying || isBg ? Th.Warn : Gfx.Mix(Th.Dim, Th.Text, MathF.Max(sel, over ? 0.7f : 0)));
                    if (UI.IconButton(UI.Id(id, 2), iconR, glyph, 10, col))
                    {
                        iconHandled = true;
                        var tabRef = t;
                        if (isMainPlaying) App.Post(P.TogglePause);
                        else App.Post(() => { tabRef.KeepInBg = true; BgPlayers.TogglePause(tabRef.Arg); });
                    }
                }
            }
            else
            {
                Gfx.Icon(TabGlyph(t), iconR, 11, Gfx.Mix(Th.Dim, Th.Brand, sel));
            }

            // Right buttons: Close (and Mute when playing in background)
            var close = new R(r.Right - 24, r.Y + (r.H - 21) / 2, 21, 21);
            float rightUsed = 26;
            if (isBg)
            {
                var muteR = new R(close.X - 22, r.Y + (r.H - 21) / 2, 21, 21);
                var muted = BgPlayers.IsMuted(t.Arg);
                if (UI.IconButton(UI.Id(id, 3), muteR, muted ? Th.Mute : Th.Volume, 10.5f, muted ? Th.Warn : Th.Brand))
                {
                    var vid = t.Arg;
                    App.Post(() => BgPlayers.ToggleMute(vid));
                    return;
                }
                rightUsed += 22;
            }

            var run = Gfx.Text(TabLabel(t), 12.5f, active || playingNow ? 600 : 400, MathF.Max(10, r.W - 28 - rightUsed));
            Gfx.Draw(run, r.X + 28, Gfx.Snap(r.Y + (r.H - run.H) / 2), Gfx.Mix(Th.Sub, Th.Text, MathF.Max(sel, playingNow ? 0.85f : 0)));

            if (UI.IconButton(UI.Id(id, 1), close, Th.Close, 9, Th.Sub)) { App.Post(() => CloseTab(i)); return; }
            if (UI.MiddleClick(r)) { App.Post(() => CloseTab(i)); return; }
            if (UI.RightClick(r)) { TabContextMenu(i); return; }
            if (!iconHandled && UI.Click(id, r)) { App.Post(() => GoTab(i)); return; }
            x += tw;
        }

        // "+" New Tab button — always available!
        var addR = new R(Gfx.Snap(x + 2), strip.Y + 4, addW, TabH - 9);
        if (UI.IconButton(UI.Id("tab:add"), addR, Th.Add, 12, Th.Sub))
            App.Post(() => { NewTab(); _search.Focus(); });
    }

    private static IPage RootPage(string tag)
    {
        if (!_roots.TryGetValue(tag, out var p))
        {
            p = tag switch
            {
                "history" => new HistoryPage(),
                "seen" => new TimelinePage(true),
                "downloads" => new DownloadsPage(),
                "settings" => new SettingsPage(),
                "shorts" => new ShortsPage(),
                _ => new FeedPage(tag, null, null),
            };
            _roots[tag] = p;
        }
        return p;
    }
    public static bool IsFullscreen => _fullscreen;

    // rectangles of the controls in the top bar: everything else there drags the window
    private static readonly List<R> _topHits = new(8);
    private static long _navAt, _toastAt;
    private static float _markY = -1, _markShown;
    private static long _gcAllocated;

    public static bool TopBarControlAt(float x, float y)
    {
        foreach (var r in _topHits) if (r.Contains(x, y)) return true;
        return false;
    }

    private static bool CaptionButton(string id, R r, string glyph, bool close = false)
    {
        _topHits.Add(r);
        var over = UI.Hover(r);
        float red = 0;
        if (close)
        {
            red = UI.Anim(UI.Id(id) ^ 0x11, over ? 1 : 0, 26);
            if (red > 0.01f) Gfx.Fill(r, Gfx.Fade(0xFFE81123, red));
        }
        else UI.Glow(UI.Id(id), r, 0, over);
        Gfx.Icon(glyph, r, 10, Gfx.Mix(Th.Icon, 0xFFFFFFFF, red));
        return UI.Click(UI.Id(id), r, hand: false);
    }

    public static void Init(string[] args)
    {
        _args = args;
        ThumbPack.Open();
        ThreadPool.SetMinThreads(4, 2); // the default is one per core: 32 here, and nothing in this app needs that many at once
        _search = new Edit("Поиск или ссылка (YouTube, TikTok, …)") { Bg = () => Th.Card };
        _search.Enter = () => Submit(_search.Text);
        _search.Changed = OnSearchChanged;
        _search.Escape = Suggest.Hide;
        _search.Arrow = d =>
        {
            var s = Suggest.Move(d);
            if (s == null) return false;
            _search.Text = s;
            return true;
        };
        Suggest.Chosen = s => { _search.Text = s; Submit(s); };

        Downloader.Instance.Init();
        P.FullscreenChanged += SetFullscreen;
        P.PlaybackStarted += () => { VideoReady = true; PushPlayerState(); };
        P.Message += OnPlayerMessage;
        P.LoadFailed += msg =>
        {
            if (T.Retrying) Toast("Поток оборвался, открываю заново…", Th.Warn, 2500);
            else Toast("Видео не открылось (" + msg + "). Попробуйте другое качество в настройках или обновите страницу.", Th.Brand, 9000);
        };
        P.EndOfFile += OnEnded;
        P.TimeChanged += t => { if (_cur is WatchPage near) near.NearEnd(t, P.Duration); };
        T.CurrentChanged += PushPlayerState;

        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Google\Chrome\BLBeacon")
                          ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Edge\BLBeacon");
            if (k?.GetValue("version") is string v && int.TryParse(v.Split('.')[0], out var major)) InnerTube.ChromeMajor = major;
        }
        catch { }

        // warm everything the first click needs while the window appears
        _ = Task.Run(Db.Warm);
        _ = Task.Run(() => InnerTube.Instance.WarmAsync());
        _ = Task.Run(Resolver.ProvisionAsync); // yt-dlp is the fallback now: keep it up to date, start it only when needed
        _ = Task.Run(Images.CleanDisk);
        _ = Task.Run(QuickStream.Warm);
        _ = Task.Run(MediaCache.Enforce);
        P.FileLoaded += () => Storyboard.Forget();
        _ = Task.Run(YtDlp.UpdateIfStaleAsync);
        _cookieStamp = CookieStamp();
        AccountMenu.LoadCached();
        AccountMenu.Refresh();
        if (!App.Test) App.After(4000, () => Updater.Check(manual: false));

        OpenRoot("home");
        for (int i = 0; i + 1 < args.Length; i++)
        {
            var v = args[i + 1];
            switch (args[i])
            {
                case "--watch":
                {
                    var wl = System.Text.RegularExpressions.Regex.Match(v, @"[?&]list=([A-Za-z0-9_-]{10,})");
                    OpenVideo(new VideoItem { VideoId = ExtractYouTubeId(v) ?? v }, "link", wl.Success ? wl.Groups[1].Value : null);
                    break;
                }
                case "--test-copylink": App.After(int.Parse(v), () => CopyPageLink(false)); break;
                case "--search": OpenSearch(v); break;
                case "--channel": OpenChannel(v, ""); break;
                case "--test-api":
                    // signed-in calls, checked without clicking: playlists are only read; Watch later and the like are set and put back
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var yt = InnerTube.Instance;
                            var info = await yt.WatchAsync(v);
                            Paths.Log($"api: like status {info.LikeStatus}, chapters {info.Chapters.Count}, sorts {info.CommentSorts.Count}");
                            var lists = await yt.PlaylistsForAsync(v);
                            var wl = lists.FirstOrDefault(p => p.Id == "WL");
                            Paths.Log($"api: {lists.Count} playlists, watch later {(wl == null ? "missing" : wl.Contains ? "has it" : "does not have it")}");
                            if (wl is { Contains: false })
                            {
                                var add = await yt.PlaylistEditAsync("WL", v, true);
                                var has = (await yt.PlaylistsForAsync(v)).FirstOrDefault(p => p.Id == "WL")?.Contains;
                                var del = await yt.PlaylistEditAsync("WL", v, false);
                                var after = (await yt.PlaylistsForAsync(v)).FirstOrDefault(p => p.Id == "WL")?.Contains;
                                Paths.Log($"api: watch later add {add} -> contains {has}; remove {del} -> contains {after}");
                            }
                            if (info.LikeStatus == "INDIFFERENT")
                            {
                                var like = await yt.RateAsync(v, "like");
                                var st = (await yt.WatchAsync(v)).LikeStatus;
                                var undo = await yt.RateAsync(v, "removelike");
                                var st2 = (await yt.WatchAsync(v)).LikeStatus;
                                Paths.Log($"api: like {like} -> {st}; removelike {undo} -> {st2}");
                            }
                        }
                        catch (Exception e) { Paths.Log("api test: " + e.Message); }
                    });
                    break;
                case "--test-tracks": App.After(7000, () => (_cur as WatchPage)?.TestTracks(v)); break;
                case "--test-sort": FeedPage.TestSort = int.Parse(v); break;
                case "--test-tab": ChannelPage.TestTab = int.Parse(v); break;
                case "--test-chip": ChannelPage.TestChip = v; break;
                case "--test-pip-alpha": Pip.TestAlpha = int.Parse(v); break;
                case "--test-dump":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var d = await InnerTube.Instance.BrowseRawAsync(v);
                            File.WriteAllText(Path.Combine(Path.GetTempPath(), "fluxtube-dump.json"), d.ToJsonString());
                            Paths.Log("dump: written");
                        }
                        catch (Exception e) { Paths.Log("dump: " + e.Message); }
                    });
                    break;
                case "--test-autoplay": AppSettings.Current.Autoplay = v == "1"; break; // in memory only: test windows are killed, not closed
                case "--theme": AppSettings.Current.Theme = v; Themes.Load(); break; // tests: not saved unless the test window saves settings
                case "--test-menu": App.After(int.Parse(v), () => AccountMenu.Toggle(Gfx.W - 46 * 3 - 8, TopH - 2)); break;
                case "--test-pip": App.After(int.Parse(v), Pip.Enter); break;
                case "--test-pip-lock": App.After(int.Parse(v), () => { Pip.Enter(); App.After(1500, () => Pip.SetLocked(true)); }); break;
                case "--test-scroll": App.After(2500, () => UI.ScrollBy(_cur?.MainScroll ?? 0, float.Parse(v, System.Globalization.CultureInfo.InvariantCulture))); break;
                case "--page": OpenRoot(v); break;
                case "--test-click": _testClickAt = Environment.TickCount64 + double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) * 1000; break;
                case "--test-next": _testNextAt = Environment.TickCount64 + double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) * 1000; break;
            }
        }
        if (App.Bench) _benchStep = 0;
        if (args.Contains("--test-tabs"))
        {
            OpenInNewTab("video", "kJQP7kiw5Fk", "Luis Fonsi - Despacito ft. Daddy Yankee", "Luis Fonsi");
            OpenInNewTab("root", "downloads", "");
        }
    }

    private static DateTime CookieStamp() => File.Exists(Paths.Cookies) ? File.GetLastWriteTimeUtc(Paths.Cookies) : default;

    // ---------------- navigation ----------------

    public static IPage? Current => _cur;

    public static void Navigate(IPage page, bool push = true)
    {
        if (_cur == page) return;
        if (_cur != null)
        {
            _cur.Hidden();
            if (push)
            {
                _back.Remove(_cur);
                _back.Add(_cur);
                if (_back.Count > 16) _back.RemoveAt(0);
                _forward.Clear();
            }
        }
        _cur = page;
        _navAt = Environment.TickCount64;
        page.Shown();
        App.Invalidate();
    }

    public static void Back()
    {
        if (_fullscreen) { P.SetFullscreenFlag(false); return; }
        if (_back.Count == 0) return;
        var prev = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        if (_cur != null)
        {
            _forward.Remove(_cur);
            _forward.Add(_cur);
            if (_forward.Count > 16) _forward.RemoveAt(0);
        }
        Navigate(prev, push: false);
        SyncTab(prev);
    }

    public static void Forward()
    {
        if (_fullscreen || _forward.Count == 0) return;
        var next = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        if (_cur != null)
        {
            _back.Remove(_cur);
            _back.Add(_cur);
            if (_back.Count > 16) _back.RemoveAt(0);
        }
        Navigate(next, push: false);
        SyncTab(next);
    }

    /// <summary>The active tab follows the page on screen, also when the user steps back.</summary>
    private static void SyncTab(IPage p)
    {
        switch (p)
        {
            case WatchPage w: SetTab("video", w.Video.VideoId, w.Title, w.Video.Channel); break;
            case ChannelPage c: SetTab("channel", c.ChannelId, c.Name); break;
            case FeedPage { Kind: "search" } f: SetTab("search", f.Arg ?? "", ""); break;
            case FeedPage { Kind: "playlist" } f: SetTab("playlist", f.Arg is { Length: > 2 } a && a.StartsWith("VL") ? a[2..] : f.Arg ?? "", f.Caption ?? ""); break;
            default: if (p.Tag.Length > 0) SetTab("root", p.Tag, ""); break;
        }
    }

    public static void OpenRoot(string tag)
    {
        if (tag == "tiktok") { StartHelper("--page tiktok --helper", "TikTok"); return; }
        var p = RootPage(tag);
        SetTab("root", tag, "");
        if (_cur == p) { UI.ScrollBy(p.MainScroll, -1e9f); p.Refresh(); }
        else Navigate(p);
    }

    // the playlist a video was last opened in, so a parked tab comes back with its list
    private static readonly Dictionary<string, string> _listOf = new();

    /// <param name="playlistId">Watch inside this playlist or mix: its items show beside the video and follow one another.</param>
    public static void OpenVideo(VideoItem v, string source, string? playlistId = null)
    {
        // a video opened from a playlist page plays inside that playlist ("playlist:VL<id>")
        if (playlistId == null && source.StartsWith("playlist:VL", StringComparison.Ordinal)) playlistId = source[11..];
        if (playlistId != null) _listOf[v.VideoId] = playlistId; else _listOf.Remove(v.VideoId);
        // reuse the page if this video is in the back stack: it keeps its scroll and related list
        SetTab("video", v.VideoId, v.Title, v.Channel);
        var existing = _back.OfType<WatchPage>().LastOrDefault(w => w.Video.VideoId == v.VideoId && w.PlaylistId == playlistId);
        Navigate(existing ?? new WatchPage(v, source, playlistId));
    }

    /// <summary>Ctrl+C with nothing selected: the link to what is on screen, ready to send to someone.</summary>
    private static bool CopyPageLink(bool withTime)
    {
        string? url = null, what = "Ссылка на страницу скопирована";
        switch (_cur)
        {
            case WatchPage w:
                url = w.PageLink(withTime);
                what = w.PlaylistId != null ? "Ссылка на видео в плейлисте скопирована" : withTime ? "Ссылка на видео с текущего момента скопирована" : "Ссылка на видео скопирована";
                break;
            case ChannelPage c: url = "https://www.youtube.com/channel/" + c.ChannelId; what = "Ссылка на канал скопирована"; break;
            case ShortsPage { CurrentId: { } sid }: url = "https://www.youtube.com/shorts/" + sid; what = "Ссылка на Shorts скопирована"; break;
            case FeedPage { Kind: "search", Arg: { } q }: url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q); what = "Ссылка на поиск скопирована"; break;
            case FeedPage { Kind: "playlist", Arg: { Length: > 2 } pl }: url = "https://www.youtube.com/playlist?list=" + pl[2..]; what = "Ссылка на плейлист скопирована"; break;
        }
        if (url == null) return false;
        if (App.Test) { Paths.Log("copy link: " + url); return true; } // scripted checks leave the user's clipboard alone
        W.SetClipboardText(App.Hwnd, url);
        Toast(what, Th.Good, 2200);
        return true;
    }

    public static void OpenChannel(string id, string name)
    {
        SetTab("channel", id, name);
        Navigate(new ChannelPage(id, name));
    }

    /// <summary>A list of shorts (a channel's Shorts tab) in the Shorts player, starting at one of them.</summary>
    public static void OpenShorts(List<VideoItem> items, int index)
    {
        SetTab("root", "shorts", "");
        Navigate(new ShortsPage(items, index));
    }

    public static void OpenPlaylist(string id, string title)
    {
        SetTab("playlist", id, title);
        Navigate(new FeedPage("playlist", "VL" + id, title));
    }

    public static void OpenSearch(string q)
    {
        SetTab("search", q, "");
        Navigate(new FeedPage("search", q, null));
    }

    /// <summary>
    /// mpv starts the first time a click looks likely (a card is hovered), off the UI thread:
    /// a window that only browses never pays for the player, and the first click still finds it ready.
    /// </summary>
    public static Task WarmPlayer() => _mpvInit ??= Task.Run(() =>
    {
        try { P.Mute = App.Mute; P.ScriptOpts = "fluxosc-iconfont=" + Gfx.IconFont; P.Init(App.VideoHwnd); }
        catch (Exception e) { Paths.Log("mpv init: " + e); }
    });

    public static void Play(VideoItem v, string source)
    {
        // If another tab's video is currently playing (unpaused) in the main player, keep it playing in background!
        MaybeHandoffToBg(v.VideoId);
        // If the video we're about to play on screen was playing in background, stop the bg copy so audio doesn't double
        if (BgPlayers.Has(v.VideoId))
        {
            var pos = BgPlayers.Stop(v.VideoId);
            if (pos > 2)
            {
                var sid = Db.StartWatch(v, source);
                Db.UpdateWatch(sid, null, null, pos, 0, 0);
            }
        }
        WarmPlayer().Wait();
        VideoReady = false;
        // a short repeats until you move on; the repeat button of a normal video holds for that video only
        if (v.IsShort && source == "shorts") P.Command("set", "loop-file", "inf");
        else if (T.Current?.VideoId != v.VideoId || T.CurrentSource == "shorts") P.Command("set", "loop-file", "no");
        T.Play(v, source);
    }

    public static void PlayFile(DownloadItem d)
    {
        WarmPlayer().Wait();
        VideoReady = false;
        T.PlayFile(d);
    }

    /// <summary>Tells the player's controls what only the app knows: is there a next video, quality, speed, title, sponsor segments.</summary>
    public static void PushPlayerState()
    {
        if (!P.IsReady) return;
        var w = _cur as WatchPage;
        var sp = _cur as ShortsPage;
        var q = AppSettings.Current.PlayerQuality == "best" ? "Макс" : Cards.QualityName(AppSettings.Current.PlayerQuality);
        var segs = new System.Text.Json.Nodes.JsonArray();
        foreach (var s in T.Segments)
            segs.Add(new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonValue.Create(s.Start), System.Text.Json.Nodes.JsonValue.Create(s.End)));
        var shortOv = sp != null && AppSettings.Current.ShortsPanelHidden && AppSettings.Current.ShortsInfoOverlay;
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["next"] = w?.NextUp() != null,
            ["quality"] = T.IsLocal ? "" : q,
            ["speed"] = w?.Speed ?? "1×",
            ["title"] = sp != null ? sp.CurrentTitle : (T.Current?.Title ?? ""),
            ["channel"] = sp != null ? sp.CurrentChannel : "",
            ["likes"] = sp != null ? sp.CurrentLikes : "",
            ["short_overlay"] = shortOv,
            ["segments"] = segs,
            ["rclick"] = AppSettings.Current.RightClickPause ? "pause" : "menu",
            ["accent"] = Themes.Hex(Themes.AccentOverride ?? Themes.Current.Brand),
            ["pip"] = Pip.Active,
            ["subs"] = w?.SubsOn ?? false,
            ["chapters"] = w?.ChaptersJson() ?? new System.Text.Json.Nodes.JsonArray(),
        };
        P.Command("script-message-to", "fluxosc", "flux-state", o.ToJsonString());
    }

    private static void OnPlayerMessage(string what, string arg)
    {
        var w = _cur as WatchPage;
        switch (what)
        {
            case "thumb":
                Storyboard.Request(T.Current?.VideoId,
                    double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : 0);
                break;
            case "next": w?.GoNext(); break;
            case "theater": w?.ToggleTheater(); break;
            case "pip": Pip.Toggle(); break;
            case "pip-lock": Pip.SetLocked(!Pip.Locked); break;
            case "pip-back": Pip.BackToApp(); break;
            case "pip-close": Pip.Exit(); T.Stop(); VideoReady = false; break;
            case "menu-quality": w?.QualityMenu(); break;
            case "menu-speed": w?.SpeedMenu(); break;
            case "menu-subs": w?.TracksMenu(); break;
            case "menu": w?.MoreMenu(); break;
        }
    }

    private static void OnEnded(bool eof)
    {
        Paths.Log($"ended {T.Current?.VideoId}: autoplay {(AppSettings.Current.Autoplay ? "on" : "off")}");
        if (!eof) return;
        // inside a playlist the next item always follows; otherwise only with autoplay switched on
        if (_cur is WatchPage w && w.Video.VideoId == T.Current?.VideoId && (w.ContinuesList || AppSettings.Current.Autoplay)) w.GoNext();
    }

    // ---------------- search ----------------

    private static void OnSearchChanged()
    {
        var q = _search.Text.Trim();
        var gen = ++_suggestGen;
        if (q.Length == 0 || q.StartsWith("http")) { Suggest.Hide(); return; }
        App.After(110, () =>
        {
            if (gen != _suggestGen) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var list = await InnerTube.Instance.SuggestAsync(q);
                    App.Post(() => { if (gen == _suggestGen && _search.Focused) Suggest.Show(list, _searchBox); });
                }
                catch { }
            });
        });
    }

    private static void Submit(string text)
    {
        var q = text.Trim();
        _suggestGen++;
        Suggest.Hide();
        if (q.Length == 0) return;
        W.SetFocus(App.Hwnd);
        var vid = ExtractYouTubeId(q);
        var list = System.Text.RegularExpressions.Regex.Match(q, @"[?&]list=([A-Za-z0-9_-]{10,})");
        if (vid != null) OpenVideo(new VideoItem { VideoId = vid }, "link", list.Success ? list.Groups[1].Value : null);
        else if (list.Success && q.Contains("youtube.com/")) OpenPlaylist(list.Groups[1].Value, "");
        else if (Uri.TryCreate(q, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http"))
        {
            Downloader.Instance.Enqueue(q);
            OpenRoot("downloads");
            Toast("Ссылка добавлена в загрузки", Th.Good);
        }
        else OpenSearch(q);
    }

    public static string? ExtractYouTubeId(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|live/|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------------- the frame ----------------

    /// <summary>A page asks for the video to sit in this rectangle this frame, optionally clipped to clipRect.</summary>
    public static void WantVideo(R r, string videoId, R? clipRect = null)
    {
        _videoRect = r; _videoClip = clipRect ?? r; _videoWanted = true; _videoFor = videoId;
    }

    public static void ToggleSidebar()
    {
        var s = AppSettings.Current;
        s.SidebarCollapsed = !s.SidebarCollapsed;
        s.Save();
        App.Invalidate();
    }

    public static void SetZoom(float scale)
    {
        var s = AppSettings.Current;
        var next = MathF.Round(Math.Clamp(scale, 0.75f, 1.50f) * 20f) / 20f;
        if (MathF.Abs(next - s.UiScale) < 0.001f) return;
        s.UiScale = next;
        s.Save();
        Gfx.ApplyScale();
        Toast($"Масштаб: {(int)MathF.Round(next * 100)}%", Th.Sub, 1600);
    }

    public static void AdjustZoom(float delta) => SetZoom(AppSettings.Current.UiScale + delta);

    /// <summary>True when the native video surface covers the rect (so the page should not draw a poster).</summary>
    public static bool VideoShown(string videoId) => VideoReady && P.IsReady && T.Current?.VideoId == videoId;

    public static bool OverVideo(float x, float y) => !Pip.Active && _hostVisible && _hostRect.Contains(x, y);

    public static void Draw()
    {
        float w = Gfx.W, h = Gfx.H;
        _videoWanted = false;
        // an open menu takes the input: the page under it does not react to the pointer
        if (Menus.Open) UI.Modal = Menus.Bounds.W > 0 ? Menus.Bounds : new R(0, 0, 0, 0);
        else if (AccountMenu.Open) UI.Modal = AccountMenu.Rect.W > 0 ? AccountMenu.Rect : new R(0, 0, 0, 0);
        var cur = T.Current;

        if (_fullscreen)
        {
            if (cur != null)
            {
                WantVideo(new R(0, 0, w, h), cur.VideoId);
                if (!VideoShown(cur.VideoId)) UI.Spinner(w / 2, h / 2, 18, Th.Text);
            }
            _search.Hide();
            UI.Modal = null;
            Menus.Draw();
            return;
        }

        var playing = cur != null && P.HasMedia;
        var onWatch = _cur is WatchPage wp && wp.Video.VideoId == cur?.VideoId || _cur is ShortsPage;
        var bar = playing && !onWatch;
        // the now-playing bar slides up from the bottom edge and back down
        var barA = UI.Anim(UI.Id("npbar"), bar ? 1 : 0, 16);
        var barH = BarH * barA;
        // collapsible sidebar smoothly glides in/out
        var sideW = UI.Smooth(UI.Id("sidew"), AppSettings.Current.SidebarCollapsed ? 0 : SideW, 18);
        // a new page settles in: a few pixels of travel and a fade
        var navT = Math.Clamp((UI.Now - _navAt) / 150f, 0, 1);
        var slide = (1 - navT) * (1 - navT) * 10;

        var tabsH = TabH;
        _cur?.Draw(new R(sideW, TopH + tabsH + slide, w - sideW, h - TopH - tabsH - barH - slide));
        DrawTabs(new R(sideW, TopH, w - sideW, TabH));

        if (navT < 1)
        {
            Gfx.Fill(new R(sideW, TopH + tabsH, w - sideW, h - TopH - tabsH - barH), Gfx.Fade(Th.Bg, (1 - navT) * (1 - navT)));
            UI.Animate();
        }

        // top bar: it is also the window's title bar
        _topHits.Clear();
        Gfx.Fill(new R(0, 0, w, TopH), Th.Bg);
        var menuR = new R(10, 8, 36, 36);
        _topHits.Add(menuR);
        if (UI.IconButton(UI.Id("sidebar:toggle"), menuR, Th.Hamburger, 15, AppSettings.Current.SidebarCollapsed ? Th.Sub : Th.Text)) App.Post(ToggleSidebar);
        var backR = new R(48, 8, 34, 36);
        _topHits.Add(backR);
        if (UI.IconButton(UI.Id("back"), backR, Th.Back, 14, Th.Text, _back.Count > 0)) App.Post(Back);
        var fwdR = new R(84, 8, 34, 36);
        _topHits.Add(fwdR);
        if (UI.IconButton(UI.Id("fwd"), fwdR, "\uE72A", 14, Th.Text, _forward.Count > 0)) App.Post(Forward);
        // the logo is the way home
        var logo = new R(122, 8, 122, 36);
        _topHits.Add(logo);
        var logoId = UI.Id("logo");
        UI.Glow(logoId, logo, 8, UI.Hover(logo));
        Gfx.Fill(new R(130, 17, 27, 18), Th.Brand, 5);
        Gfx.Icon("\uF5B0", new R(130, 17, 27, 18), 9, Th.OnBrand);
        Gfx.Draw("FluxTube", 165, 15, 15.5f, Th.Text, 600);
        if (UI.Click(logoId, logo)) App.Post(() => OpenRoot("home"));

        // window buttons
        const float cbw = 46, cbh = 34;
        var zoomed = W.IsZoomed(App.Hwnd);
        if (CaptionButton("cap:close", new R(w - cbw, 0, cbw, cbh), "\uE8BB", close: true)) App.Post(() => W.PostMessageW(App.Hwnd, W.WM_CLOSE, 0, 0));
        if (CaptionButton("cap:max", new R(w - cbw * 2, 0, cbw, cbh), zoomed ? "\uE923" : "\uE922")) App.Post(() => W.ShowWindow(App.Hwnd, zoomed ? 9 : 3));
        if (CaptionButton("cap:min", new R(w - cbw * 3, 0, cbw, cbh), "\uE921")) App.Post(() => W.ShowWindow(App.Hwnd, 6));
        var rightEdge = w - cbw * 3 - 8;

        var sw = Math.Clamp(w - 720, 200, 600);
        _searchBox = new R(Gfx.Snap((w - sw) / 2), 8, sw, 36);
        Gfx.Fill(_searchBox, Th.Card, 18);
        if (_search.Focused) Gfx.Stroke(_searchBox, Gfx.Fade(Th.Brand, 0.8f), 18, 1.2f);
        _topHits.Add(_searchBox);
        _search.Place(_searchBox, 18, 44);
        if (UI.IconButton(UI.Id("go"), new R(_searchBox.Right - 40, 10, 34, 32), Th.Search, 14, Th.Sub)) App.Post(() => Submit(_search.Text));

        var logged = CookieStore.IsLoggedIn;
        float accLeft;
        if (logged)
        {
            // the account photo, like youtube.com; the menu opens under it
            var ar = new R(rightEdge - 40, 8, 36, 36);
            accLeft = ar.X;
            _topHits.Add(ar);
            var aid = UI.Id("acc");
            var ring = UI.Anim(aid ^ 0x1313, UI.Hover(ar) || AccountMenu.Open ? 1 : 0, 22);
            if (ring > 0.01f) Gfx.Circle(ar.X + 18, ar.Y + 18, 18, Th.A(0.18f * ring));
            var acc = AccountMenu.Current;
            Cards.Avatar(ar.X + 3, ar.Y + 3, 30, acc?.Photo, acc?.Name is { Length: > 0 } nm ? nm : "?");
            if (UI.Click(aid, ar)) AccountMenu.Toggle(rightEdge, TopH - 2);
        }
        else
        {
            var accW = UI.PillWidth(Th.Account, "Войти");
            accLeft = rightEdge - accW;
            _topHits.Add(new R(accLeft, 9, accW, 34));
            if (UI.Pill(UI.Id("acc"), accLeft, 9, 34, Th.Account, "Войти", out _, Th.A(0.08f))) AccountMenu.Toggle(rightEdge, TopH - 2);
        }

        // Non-intrusive update pill in the top bar when a new GitHub Release is available
        if (Updater.HasUpdate || Updater.Downloading)
        {
            var upLabel = Updater.Downloading ? $"{(int)(Updater.Progress * 100)}%" : $"Обновить {Updater.AvailableVersion}";
            var upW = UI.PillWidth(Th.Download, upLabel);
            var upX = accLeft - upW - 10;
            if (upX > _searchBox.Right + 10)
            {
                _topHits.Add(new R(upX, 9, upW, 34));
                if (UI.Pill(UI.Id("top:upd"), upX, 9, 34, Th.Download, upLabel, out _, accent: true) && !Updater.Downloading)
                    App.Post(Updater.DownloadAndInstall);
            }
        }

        // side bar (slides out smoothly when collapsed)
        if (sideW > 0.5f)
        {
            Gfx.PushClip(new R(0, TopH, sideW, h - TopH));
            float sx = sideW - SideW;
            Gfx.Fill(new R(sx, TopH, SideW, h - TopH), Th.Bg);
            float y = TopH + 4;
            var tag = _cur?.Tag ?? "";
            SideItem(sx, ref y, Th.Home, "Главная", "home", tag);
            SideItem(sx, ref y, Th.Subs, "Подписки", "subs", tag);
            SideItem(sx, ref y, Th.Later, "Позже", "wl", tag);
            SideItem(sx, ref y, Th.Like, "Лайки", "liked", tag);
            y += 6; Gfx.Fill(new R(sx + 18, y, SideW - 36, 1), Th.Line); y += 7;
            SideItem(sx, ref y, Th.History, "История", "history", tag);
            SideItem(sx, ref y, Th.Eye, "Видел", "seen", tag);
            SideItem(sx, ref y, Th.Download, "Загрузки", "downloads", tag, Downloader.Instance.Items.Any(i => i.IsActive));
            y += 6; Gfx.Fill(new R(sx + 18, y, SideW - 36, 1), Th.Line); y += 7;
            SideItem(sx, ref y, "\uE714", "Shorts", "shorts", tag);
            SideItem(sx, ref y, Th.TikTok, "TikTok", "tiktok", tag);
            var sy = h - 64 - barH;
            if (sy > y) SideItem(sx, ref sy, Th.Settings, "Настройки", "settings", tag);
            // one selection marker for the whole side bar: it glides from the old section to the new one
            var markA = UI.Anim(UI.Id("sidemark:a"), _markY >= 0 ? 1 : 0, 18);
            if (_markY >= 0) _markShown = UI.Smooth(UI.Id("sidemark:y"), _markY, 20);
            if (markA > 0.01f) Gfx.Fill(new R(sx, _markShown, 3, 24), Gfx.Fade(Th.Brand, markA), 1.5f);
            _markY = -1;
            Gfx.PopClip();
        }
        else _markY = -1;

        if (barA > 0.01f)
        {
            var slideDown = BarH * (1 - barA);
            if (bar) NowPlaying(cur!, sideW, w, h + slideDown);
            else Gfx.Fill(new R(sideW, h - barH, w - sideW, barH), Th.Panel);
        }
        DrawToast(w, h - barH);
        UI.DrawAutoScrollIndicator();
        UI.Modal = null;
        AccountMenu.Draw();
        Menus.Draw();
    }

    private static void SideItem(float sx, ref float y, string glyph, string label, string tag, string current, bool dot = false)
    {
        var r = new R(sx + 6, y, SideW - 12, 56);
        var sel = tag == current;
        var id = UI.Id(tag, 77);
        var selA = UI.Anim(id ^ 0x77, sel ? 1 : 0, 18);
        if (selA > 0.01f) Gfx.Fill(r, Th.A(0.086f * selA), 10);
        if (!sel) UI.Glow(id, r, 10, UI.Hover(r));
        if (sel) _markY = y + 16;
        Gfx.Icon(glyph, new R(r.X, y + 7, r.W, 24), 19, sel ? Th.Text : Th.Icon);
        var run = Gfx.Text(label, 10.5f, sel ? 600 : 400);
        Gfx.Draw(run, Gfx.Snap(r.X + (r.W - run.W) / 2), y + 34, sel ? Th.Text : Th.Sub);
        if (dot) Gfx.Circle(r.X + r.W / 2 + 13, y + 11, 3.5f, Th.Brand);
        if (UI.MiddleClick(r) && tag != "tiktok") App.Post(() => OpenInNewTab("root", tag, ""));
        else if (UI.Click(id, r)) App.Post(() => OpenRoot(tag));
        y += 60;
    }

    private static void NowPlaying(VideoItem cur, float sideW, float w, float h)
    {
        var r = new R(sideW, h - BarH, w - sideW, BarH);
        Gfx.Fill(r, Th.Panel);
        Gfx.Fill(new R(r.X, r.Y, r.W, 1), Th.Line);

        if (_mini)
        {
            float mw = 384, mh = 216;
            var mr = new R(w - mw - 18, h - BarH - mh - 14, mw, mh);
            Gfx.Fill(mr.Inset(-1), 0xFF000000, 2);
            WantVideo(mr, cur.VideoId);
            if (!VideoShown(cur.VideoId)) UI.Spinner(mr.X + mr.W / 2, mr.Y + mr.H / 2, 12, Th.Text);
        }

        var thumb = new R(r.X + 14, r.Y + 9, 78, 44);
        Cards.Thumb(thumb, cur, false, 6, badge: false, behind: Th.Panel);
        float bx = r.Right - 14 - 6 * 40;
        float tx = thumb.Right + 12, tw = bx - tx - 16;
        Gfx.Draw(string.IsNullOrEmpty(cur.Title) ? "Видео" : cur.Title, tx, r.Y + 9, 14, Th.Text, 600, tw);
        var d = P.Duration;
        var time = $"{Fmt.Time(P.TimePos)} / {Fmt.Time(d)}";
        var timeRun = Gfx.Text(time, 12);
        var ch = Gfx.Draw(cur.Channel, tx, r.Y + 31, 12, Th.Sub, 400, MathF.Min(240, tw / 3));
        var px = tx + ch.W + 12;
        var pw = tx + tw - timeRun.W - 12 - px;
        if (pw > 40)
        {
            var pr = new R(px, r.Y + 37, pw, 4);
            UI.Progress(pr, d > 0 ? P.TimePos / d : 0, Th.A(0.2f));
            var hit = new R(px, r.Y + 28, pw, 22);
            if (d > 0 && UI.Click(UI.Id("np:seek"), hit)) P.SeekAbsolute(d * Math.Clamp((UI.MX - px) / pw, 0, 1));
        }
        Gfx.Draw(timeRun, tx + tw - timeRun.W, r.Y + 31, Th.Sub);

        float by = r.Y + 13;
        if (UI.IconButton(UI.Id("np:b"), new R(bx, by, 36, 36), Th.Rewind)) P.Command("seek", "-10");
        if (UI.IconButton(UI.Id("np:p"), new R(bx + 40, by, 36, 36), P.Paused ? Th.Play : Th.Pause)) P.TogglePause();
        if (UI.IconButton(UI.Id("np:f"), new R(bx + 80, by, 36, 36), Th.Forward)) P.Command("seek", "10");
        if (UI.IconButton(UI.Id("np:m"), new R(bx + 120, by, 36, 36), Th.Mini, 16, _mini ? Th.Brand : Th.Text)) _mini = !_mini;
        if (UI.IconButton(UI.Id("np:o"), new R(bx + 160, by, 36, 36), Th.Expand)) App.Post(() => OpenVideo(cur, T.CurrentSource ?? "home"));
        if (UI.IconButton(UI.Id("np:x"), new R(bx + 200, by, 36, 36), Th.Close)) App.Post(() => { T.Stop(); VideoReady = false; });
    }

    private static void DrawToast(float w, float bottom)
    {
        var left = _toastUntil - UI.Now;
        if (left <= 0 || _toast.Length == 0) return;
        var run = Gfx.Text(_toast, 13.5f, 400, MathF.Min(680, w - 200), 3);
        // rises into place, fades away
        var rise = Math.Clamp((UI.Now - _toastAt) / 170f, 0, 1);
        var a = Math.Clamp(left / 250f, 0, 1) * rise;
        if (rise < 1) UI.Animate();
        var r = new R(Gfx.Snap((w - run.W) / 2 - 18), bottom - run.H - 44 + (1 - rise) * (1 - rise) * 16, run.W + 36, run.H + 20);
        Gfx.Fill(r, Gfx.Fade(Th.Menu, 0.96f * a), 12);
        Gfx.Stroke(r, Gfx.Fade(Th.Line, a), 12);
        Gfx.Fill(new R(r.X, r.Y + 8, 3, r.H - 16), Gfx.Fade(_toastColor, a), 1.5f);
        Gfx.Draw(run, r.X + 18, r.Y + 10, Gfx.Fade(Th.Text, a));
        if (left < 260) UI.Animate();
    }

    public static void Toast(string text, uint? color = null, int ms = 4000)
    {
        _toast = text; _toastColor = color ?? Th.Sub; _toastAt = Environment.TickCount64; _toastUntil = _toastAt + ms;
        App.After(ms - 250, App.Invalidate);
        App.After(ms + 20, App.Invalidate);
        App.Invalidate();
    }

    /// <summary>After the frame is submitted: move the native video surface to where the frame wanted it.</summary>
    private static int _rgnHash;

    /// <summary>The video surface came back from the separate window: place it again on the next frame.</summary>
    public static void VideoHostReturned()
    {
        _hostVisible = false;
        _hostPx = default;
        _rgnHash = 0;
        W.SetWindowRgn(App.VideoHwnd, 0, false);
    }

    /// <summary>The page of whatever is playing (from the separate window's "back" button).</summary>
    public static void OpenCurrentVideo()
    {
        if (T.Current is { } cur && !(_cur is WatchPage wp && wp.Video.VideoId == cur.VideoId)) OpenVideo(cur, T.CurrentSource ?? "home");
    }

    public static void AfterFrame()
    {
        if (Pip.Active) return; // the surface lives in its own window now
        var vis = _videoRect.Intersect(_videoClip);
        var show = _videoWanted && _videoFor != null && VideoShown(_videoFor) && vis.W > 1 && vis.H > 1;
        if (show)
        {
            var s = Gfx.Scale;
            var px = new W.RECT
            {
                L = (int)MathF.Round(_videoRect.X * s), T = (int)MathF.Round(_videoRect.Y * s),
                R = (int)MathF.Round(_videoRect.Right * s), B = (int)MathF.Round(_videoRect.Bottom * s),
            };
            int vw = Math.Max(1, px.R - px.L), vh = Math.Max(1, px.B - px.T);

            // Compute visible region (clipped to _videoClip for whole-page scrolling, minus any open menus)
            int cl = Math.Clamp((int)MathF.Round((vis.X - _videoRect.X) * s), 0, vw);
            int ct = Math.Clamp((int)MathF.Round((vis.Y - _videoRect.Y) * s), 0, vh);
            int cr = Math.Clamp((int)MathF.Round((vis.Right - _videoRect.X) * s), 0, vw);
            int cb = Math.Clamp((int)MathF.Round((vis.Bottom - _videoRect.Y) * s), 0, vh);
            bool clipped = cl > 0 || ct > 0 || cr < vw || cb < vh;

            var holes = new List<R>(4);
            if (AccountMenu.Open && AccountMenu.Rect.W > 0 && AccountMenu.Rect.Intersect(_videoRect) is { W: > 0, H: > 0 })
                holes.Add(AccountMenu.Rect);
            if (Menus.Open)
                foreach (var mr in Menus.ActiveRects)
                    if (mr.W > 0 && mr.Intersect(_videoRect) is { W: > 0, H: > 0 }) holes.Add(mr);

            int hash = HashCode.Combine(cl, ct, cr, cb, vw, vh, holes.Count);
            foreach (var hr in holes) hash = HashCode.Combine(hash, (int)hr.X, (int)hr.Y, (int)hr.W, (int)hr.H);

            if (hash != _rgnHash || px.L != _hostPx.L || px.T != _hostPx.T || px.R != _hostPx.R || px.B != _hostPx.B)
            {
                _rgnHash = hash;
                if (!clipped && holes.Count == 0) W.SetWindowRgn(App.VideoHwnd, 0, true);
                else
                {
                    var rgn = W.CreateRectRgn(cl, ct, cr, cb);
                    foreach (var hr in holes)
                    {
                        var cut = W.CreateRectRgn(
                            (int)MathF.Floor((hr.X - _videoRect.X) * s), (int)MathF.Floor((hr.Y - _videoRect.Y) * s),
                            (int)MathF.Ceiling((hr.Right - _videoRect.X) * s), (int)MathF.Ceiling((hr.Bottom - _videoRect.Y) * s));
                        W.CombineRgn(rgn, rgn, cut, 4 /*RGN_DIFF*/);
                        W.DeleteObject(cut);
                    }
                    W.SetWindowRgn(App.VideoHwnd, rgn, true); // the window owns rgn now
                }
            }
            if (!_hostVisible || px.L != _hostPx.L || px.T != _hostPx.T || px.R != _hostPx.R || px.B != _hostPx.B)
            {
                _hostPx = px; _hostRect = vis; _hostVisible = true;
                W.SetWindowPos(App.VideoHwnd, 0, px.L, px.T, vw, vh, W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
            }
            else _hostRect = vis;
        }
        else if (_hostVisible)
        {
            _hostVisible = false;
            W.ShowWindow(App.VideoHwnd, W.SW_HIDE);
        }
    }

    // ---------------- hold Space for 2x speed (YouTube-style) ----------------
    private static bool _spaceHeld, _space2x;
    private static long _spaceDownAt;
    private static double _spaceSavedSpeed = 1.0;

    public static bool SpaceDown(bool repeat)
    {
        if (Menus.Open || AccountMenu.Open || SharePanel.Open) return false;
        if (T.Current == null || !P.HasMedia) return false;
        if (!repeat)
        {
            _spaceHeld = true;
            _space2x = false;
            _spaceDownAt = Environment.TickCount64;
            App.After(240, CheckSpaceHold);
        }
        else if (_spaceHeld && !_space2x && Environment.TickCount64 - _spaceDownAt >= 240)
        {
            ActivateSpace2x();
        }
        return true;
    }

    private static void CheckSpaceHold()
    {
        if (_spaceHeld && !_space2x && Environment.TickCount64 - _spaceDownAt >= 230)
            ActivateSpace2x();
    }

    private static void ActivateSpace2x()
    {
        if (_space2x || P.Paused || !P.HasMedia) return;
        _space2x = true;
        var cur = P.GetDouble("speed");
        _spaceSavedSpeed = cur > 0.1 ? cur : 1.0;
        var boost = _spaceSavedSpeed < 2.0 ? 2.0 : Math.Min(4.0, _spaceSavedSpeed * 2.0);
        P.SetSpeed(boost);
        P.Command("script-message-to", "fluxosc", "flux-2x", boost.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×");
    }

    public static bool SpaceUp()
    {
        if (!_spaceHeld) return false;
        _spaceHeld = false;
        if (_space2x)
        {
            _space2x = false;
            P.SetSpeed(_spaceSavedSpeed);
            P.Command("script-message-to", "fluxosc", "flux-2x", "off");
        }
        else if (T.Current != null && P.HasMedia)
        {
            P.Command("keypress", "SPACE");
        }
        return true;
    }

    public static void CancelSpace2x()
    {
        _spaceHeld = false;
        if (!_space2x) return;
        _space2x = false;
        P.SetSpeed(_spaceSavedSpeed);
        P.Command("script-message-to", "fluxosc", "flux-2x", "off");
    }

    // ---------------- keys ----------------

    public static bool Key(int vk, bool ctrl, bool alt)
    {
        if (Menus.Open && Menus.Key(vk)) return true;
        if (alt && vk == W.VK_LEFT) { Back(); return true; }
        if (alt && vk == W.VK_RIGHT) { Forward(); return true; }
        if (alt) return false;
        // Ctrl+Z = Back, Ctrl+Y / Ctrl+Shift+Z = Forward
        if (ctrl && vk == 'Z') { if (W.KeyDown(0x10)) Forward(); else Back(); return true; }
        if (ctrl && vk == 'Y') { Forward(); return true; }
        if (ctrl && vk == 'F' || vk == 0xBF && !ctrl) { _search.Focus(); return true; }
        if (ctrl && vk == 'B') { ToggleSidebar(); return true; }
        // Chrome-like UI Zoom: Ctrl + / - / 0
        if (ctrl && (vk == 0xBB /*OEM_PLUS*/ || vk == 0x6B /*ADD*/)) { AdjustZoom(0.10f); return true; }
        if (ctrl && (vk == 0xBD /*OEM_MINUS*/ || vk == 0x6D /*SUBTRACT*/)) { AdjustZoom(-0.10f); return true; }
        if (ctrl && (vk == '0' || vk == 0x60 /*NUMPAD0*/)) { SetZoom(1.0f); return true; }
        // Ctrl+C: the selected text; with nothing selected, the link to this page (with Shift: from the current moment)
        if (ctrl && (vk == 'C' || vk == 0x2D /*Insert*/) && (Sel.CopySelection() || CopyPageLink(W.KeyDown(0x10)))) return true;
        if (ctrl && vk == 'W') { CloseTab(_tab); return true; }
        if (ctrl && vk == 9 /*Tab*/ && _tabs.Count > 1)
        {
            int dir = W.KeyDown(0x10) ? -1 : 1;
            GoTab((_tab + dir + _tabs.Count) % _tabs.Count);
            return true;
        }
        if (ctrl && vk == 'T')
        {
            if (W.KeyDown(0x10)) ReopenClosedTab();
            else { NewTab(); _search.Focus(); }
            return true;
        }
        if (ctrl && vk >= '1' && vk <= '9')
        {
            int idx = vk == '9' ? _tabs.Count - 1 : vk - '1';
            if (idx >= 0 && idx < _tabs.Count) GoTab(idx);
            return true;
        }
        if (vk == W.VK_F5) { _cur?.Refresh(); return true; }
        if (vk == W.VK_ESCAPE)
        {
            if (UI.AutoScrollActive) UI.StopAutoScroll();
            else if (AccountMenu.Open) AccountMenu.Close();
            else if (SharePanel.Open) SharePanel.Close();
            else if (_fullscreen) P.SetFullscreenFlag(false);
            else if (_cur is WatchPage || _cur is ChannelPage || _cur is ShortsPage { Tag: "" } || _cur is FeedPage { Tag: "" }) Back(); // out of a video or a search, like Esc in a dialog
            return true;
        }
        if (ctrl) return false;

        if (_cur is ShortsPage shorts)
        {
            if (vk is W.VK_DOWN or W.VK_NEXT or 'S' or 'J') { shorts.Step(1); return true; }
            if (vk is W.VK_UP or W.VK_PRIOR or 'W' or 'K') { shorts.Step(-1); return true; }
            if (vk == 'L') { shorts.ToggleLike(); return true; }
            if (vk == 'C') { shorts.ToggleComments(); return true; }
        }
        var playing = T.Current != null && P.HasMedia;
        var onWatch = _cur is WatchPage or ShortsPage;
        if (playing && (onWatch || vk == W.VK_SPACE || vk == 'K' || vk == 'M'))
        {
            var key = vk switch
            {
                W.VK_SPACE => "SPACE", W.VK_LEFT => "LEFT", W.VK_RIGHT => "RIGHT", W.VK_UP => "UP", W.VK_DOWN => "DOWN",
                W.VK_HOME => "HOME", W.VK_BACK => "BS", W.VK_OEM_COMMA => ",", W.VK_OEM_PERIOD => ".", W.VK_OEM_4 => "[", W.VK_OEM_6 => "]",
                >= 'A' and <= 'Z' => ((char)(vk + 32)).ToString(),
                >= '0' and <= '9' => ((char)vk).ToString(),
                _ => null,
            };
            if (key != null) { P.Command("keypress", key); return true; }
        }
        if (_cur != null)
        {
            var page = Gfx.H - TopH - 80;
            switch (vk)
            {
                case W.VK_NEXT: UI.ScrollBy(_cur.MainScroll, page); return true;
                case W.VK_PRIOR: UI.ScrollBy(_cur.MainScroll, -page); return true;
                case W.VK_DOWN: UI.ScrollBy(_cur.MainScroll, 120); return true;
                case W.VK_UP: UI.ScrollBy(_cur.MainScroll, -120); return true;
                case W.VK_END: UI.ScrollBy(_cur.MainScroll, 1e9f); return true;
                case W.VK_HOME: UI.ScrollBy(_cur.MainScroll, -1e9f); return true;
                case W.VK_BACK: Back(); return true;
            }
        }
        return false;
    }

    // ---------------- fullscreen ----------------

    private static unsafe void SetFullscreen(bool fs)
    {
        if (fs == _fullscreen) return;
        if (fs && Pip.Active) Pip.Exit();
        AccountMenu.Close();
        var cur = T.Current;
        if (fs && cur != null && !(_cur is WatchPage wp && wp.Video.VideoId == cur.VideoId)) OpenVideo(cur, T.CurrentSource ?? "home");
        _fullscreen = fs;
        var h = App.Hwnd;
        if (fs)
        {
            fixed (W.WINDOWPLACEMENT* p = &_savedPlacement) { p->length = (uint)sizeof(W.WINDOWPLACEMENT); W.GetWindowPlacement(h, p); }
            _savedStyle = W.GetWindowLongPtrW(h, W.GWL_STYLE);
            var mi = new W.MONITORINFO { cbSize = (uint)sizeof(W.MONITORINFO) };
            W.GetMonitorInfoW(W.MonitorFromWindow(h, 2), &mi);
            W.SetWindowLongPtrW(h, W.GWL_STYLE, unchecked((nint)(W.WS_POPUP | W.WS_VISIBLE | W.WS_CLIPCHILDREN)));
            W.SetWindowPos(h, 0, mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R - mi.rcMonitor.L, mi.rcMonitor.B - mi.rcMonitor.T, W.SWP_NOZORDER | W.SWP_FRAMECHANGED);
        }
        else
        {
            W.SetWindowLongPtrW(h, W.GWL_STYLE, _savedStyle);
            fixed (W.WINDOWPLACEMENT* p = &_savedPlacement) W.SetWindowPlacement(h, p);
            W.SetWindowPos(h, 0, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOZORDER | W.SWP_FRAMECHANGED);
        }
        App.Invalidate();
    }

    // ---------------- account, helper process ----------------

    public static void Login() => BrowserLogin.StartSmartLogin();
    public static void LoginWebView() => StartHelper("--login", "Вход");

    public static void Logout()
    {
        CookieStore.Clear();
        _cookieStamp = CookieStamp();

        OnAccountChanged();
        Toast("Вы вышли. Локальная история сохранена.");
    }

    /// <summary>Sign-in and TikTok need a browser engine; it lives in a separate process that exists only while used.</summary>
    private static void StartHelper(string args, string what)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "web", "FluxTube.exe");
        if (!File.Exists(exe)) { Toast(what + ": не найден модуль web\\FluxTube.exe. В настройках есть импорт cookies.txt.", Th.Warn, 7000); return; }
        try
        {
            _helper = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        }
        catch (Exception e) { Toast(what + ": " + e.Message, Th.Brand, 7000); }
    }

    public static void OnAccountChanged()
    {
        CookieStore.NotifyExternalChange();
        AccountMenu.Refresh();
        Resolver.Instance.Invalidate();
        if (P.IsReady) P.ApplySettings();
        foreach (var p in _roots.Values.OfType<FeedPage>()) p.Invalidate();
        if (_cur is FeedPage f) f.Shown();
        App.Invalidate();
    }

    // ---------------- housekeeping ----------------

    /// <summary>Twice a second: things that have no event of their own.</summary>
    public static void Tick()
    {
        // while nobody is touching anything, hand back what the last burst of work left behind
        // (parsed feeds, network buffers): the managed heap otherwise keeps tens of megabytes it no longer needs
        var allocated = GC.GetTotalAllocatedBytes();
        if (Environment.TickCount64 - UI.LastInput > 3500 && allocated - _gcAllocated > 6 * 1024 * 1024)
        {
            _gcAllocated = allocated;
            Gfx.DropIdleText();
            ThreadPool.QueueUserWorkItem(_ => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true));
        }

        var stamp = CookieStamp();
        if (stamp != _cookieStamp)
        {
            var was = CookieStore.IsLoggedIn;
            _cookieStamp = stamp;
            CookieStore.NotifyExternalChange();
            // yt-dlp rewrites the file too (rotated cookies); only a login state change is news
            if (was != CookieStore.IsLoggedIn || _helper is { HasExited: false })
            {
                OnAccountChanged();
                if (CookieStore.IsLoggedIn && !was) { Toast("Вы вошли в YouTube", Th.Good); OpenRoot("home"); }
            }
        }

        Storyboard.Idle();
        BgPlayers.ReapIdle();

        // links sent from the helper's "download this video" button
        try
        {
            var inbox = Path.Combine(Paths.Data, "inbox");
            if (Directory.Exists(inbox))
                foreach (var f in Directory.GetFiles(inbox, "*.url"))
                {
                    var url = File.ReadAllText(f).Trim();
                    File.Delete(f);
                    if (url.StartsWith("http")) { Downloader.Instance.Enqueue(url); Toast("Добавлено в загрузки", Th.Good); }
                }
        }
        catch { }

        // scripted check: after a while on the feed, open a card nobody hovered (the worst case for start time), then two more
        if (_testClickAt > 0 && Environment.TickCount64 > _testClickAt && _roots.GetValueOrDefault("home") is FeedPage home && home.Count > _testClickIndex)
        {
            OpenVideo(home.ItemAt(_testClickIndex), "test");
            _testClickIndex += 4;
            _testClickAt = _testClickIndex <= 15 ? Environment.TickCount64 + 14000 : -1;
        }

        if (_testNextAt > 0 && Environment.TickCount64 > _testNextAt)
        {
            _testNextAt = -1;
            if (_cur is WatchPage w && w.NextUp() is { } next) OpenVideo(next, "test");
        }

        if (_benchStep >= 0 && _cur is FeedPage fp && fp.Count > 0)
        {
            // scripted scroll: down in big jumps, then back up; frame times go to the log
            if (_benchStep == 0) App.ResetBench();
            _benchStep++;
            if (_benchStep <= 14) UI.ScrollBy(fp.MainScroll, 2600);
            else if (_benchStep <= 20) UI.ScrollBy(fp.MainScroll, -6000);
            else
            {
                _benchStep = -1;
                using var proc = Process.GetCurrentProcess();
                Paths.Log($"bench: {App.BenchReport()} | items={fp.Count} | ws={proc.WorkingSet64 >> 20}MB private={proc.PrivateMemorySize64 >> 20}MB gpuImages={Images.GpuBytes >> 20}MB managed={GC.GetTotalMemory(false) >> 20}MB");
            }
            App.Invalidate();
        }
    }

    public static unsafe void Shutdown()
    {
        try
        {
            if (!_fullscreen && !W.IsZoomed(App.Hwnd) && !W.IsIconic(App.Hwnd))
            {
                W.RECT rc;
                W.GetWindowRect(App.Hwnd, &rc);
                var s = W.GetDpiForWindow(App.Hwnd) / 96f;
                AppSettings.Current.WindowWidth = (rc.R - rc.L) / s;
                AppSettings.Current.WindowHeight = (rc.B - rc.T) / s;
            }
            T.Flush();
            BgPlayers.DisposeAll();
            AppSettings.Current.Save();
            Resolver.Instance.Dispose();
            P.Dispose();
            MediaCache.Shutdown();
            ThumbPack.SaveIndex();
        }
        catch (Exception e) { Paths.Log("shutdown: " + e.Message); }
    }
}
