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
    private static int _playReqGen;

    private static bool _fullscreen;
    private static W.WINDOWPLACEMENT _savedPlacement;
    private static nint _savedStyle;

    private static R _videoRect, _videoClip, _hostRect;
    private static R? _miniVideoRect;
    private static float _videoRadius;
    private static bool _videoWanted, _hostVisible;
    private static bool _npDragSeek, _npDragVol;
    private static double _npDragSeekTarget;
    private static long _npLastSeekAt, _npLastVolAt;
    private static string? _videoFor;
    private static W.RECT _hostPx;
    public static bool VideoReady { get; set; }

    private static string _toast = "";
    private static uint _toastColor;
    private static Action? _toastAction;
    private static long _toastUntil;
    private static R _toastRect;

    private static Process? _helper;
    private static DateTime _cookieStamp;
    private static bool _wasLoggedIn;
    private static bool _awaitingLogin;
    private static long _helperExitedAt;
    private static long _lastVolSfxAt, _volSaveDueAt;
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
        public bool KeepInBg = true;
    }

    private static readonly List<Tab> _tabs = [];
    private static readonly List<Tab> _closedTabs = [];
    private static int _tab = -1;
    private static int _hoverTab = -1;
    private static R _hoverTabRect, _tabPreviewRect;
    private static long _hoverTabSince, _tabVolBoostUntil;

    public static IReadOnlyList<Tab> Tabs => _tabs;
    public static R TabPreviewRect => _tabPreviewRect;
    public static void SyncCookieStamp() { _cookieStamp = CookieStamp(); _wasLoggedIn = CookieStore.IsLoggedIn; }

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
        Sfx.Tab();
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
        Sfx.Tab();
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
        Sfx.Tab();
        var t = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        var idx = Math.Clamp(_tab + 1, 0, _tabs.Count);
        _tabs.Insert(idx, t);
        GoTab(idx);
    }

    public static void DuplicateTab(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        Sfx.Tab();
        var s = _tabs[i];
        _tabs.Insert(i + 1, new Tab { Kind = s.Kind, Arg = s.Arg, Title = s.Title, Sub = s.Sub, KeepInBg = s.KeepInBg });
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

    // Exact live playback position per video tab (bounded to avoid unbounded growth)
    private static readonly Dictionary<string, double> _tabPos = new();

    private static void SaveTabPos(string videoId, double pos)
    {
        if (pos <= 0.3) return;
        if (_tabPos.Count > 64 && !_tabPos.ContainsKey(videoId))
        {
            var first = _tabPos.Keys.FirstOrDefault();
            if (first != null) _tabPos.Remove(first);
        }
        _tabPos[videoId] = pos;
    }

    public static double? GetSavedTabPos(string videoId)
        => _tabPos.TryGetValue(videoId, out var p) && p > 0.3 ? p : null;

    private static double CurrentLivePos() => P.TimePos;

    private static bool VideoStillOpenInOtherTab(string videoId, int excludeIdx)
    {
        for (int i = 0; i < _tabs.Count; i++)
            if (i != excludeIdx && _tabs[i].Kind == "video" && _tabs[i].Arg == videoId) return true;
        return false;
    }

    /// <summary>
    /// If another open tab owns the video currently playing (unpaused) in the main player (and KeepInBg is enabled),
    /// seamlessly hand it off to a lightweight audio-only BgPlayer before the main player switches to newVideoId.
    /// Never spawns duplicate players for the same video.
    /// </summary>
    private static void MaybeHandoffToBg(string newVideoId)
    {
        var cur = T.Current;
        if (cur == null || cur.VideoId == newVideoId || !P.HasMedia || T.CurrentSource == "shorts") return;
        var pos = CurrentLivePos();
        SaveTabPos(cur.VideoId, pos);
        if (P.Paused || BgPlayers.Has(cur.VideoId)) return;
        // Only keep playing in background if that video is open in another tab with KeepInBg enabled
        bool allowBg = false;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i != _tab && _tabs[i].Kind == "video" && _tabs[i].Arg == cur.VideoId && _tabs[i].KeepInBg)
            {
                allowBg = true;
                break;
            }
        }
        if (!allowBg) return;
        BgPlayers.Play(cur.VideoId, pos);
    }

    public static void GoTab(int i)
    {
        if (i < 0 || i >= _tabs.Count || i == _tab) return;
        Sfx.Tab();
        var target = _tabs[i];
        // Save exact live position of the tab we are leaving, and if it is playing + we're moving to another video tab, hand it off to BgPlayers!
        if (_tab >= 0 && _tab < _tabs.Count)
        {
            var prev = _tabs[_tab];
            if (prev.Kind == "video" && T.Current?.VideoId == prev.Arg && P.HasMedia)
            {
                var pos = CurrentLivePos();
                SaveTabPos(prev.Arg, pos);
                if (target.Kind == "video" && target.Arg != prev.Arg && !P.Paused && prev.KeepInBg && !BgPlayers.Has(prev.Arg))
                    BgPlayers.Play(prev.Arg, pos);
            }
        }
        // If target video tab is currently playing in background, capture its exact live position before switching
        if (target.Kind == "video" && BgPlayers.Has(target.Arg))
        {
            var pos = BgPlayers.Stop(target.Arg, saveToDb: false);
            SaveTabPos(target.Arg, pos);
        }
        _tab = i;
        _back.Clear();
        _forward.Clear();
        SharePanel.Close();
        Navigate(PageFor(target), push: false);
    }

    public static void CloseTab(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        Sfx.Back();
        var removed = _tabs[i];
        var wasActive = i == _tab;
        _tabs.RemoveAt(i);

        if (removed.Kind == "video" && !VideoStillOpenInOtherTab(removed.Arg, -1))
        {
            BgPlayers.Stop(removed.Arg);
            _tabPos.Remove(removed.Arg);
            // If this closed tab was the last owner of the main player's video, stop main player cleanly (no ghost mini-player!)
            if (T.Current?.VideoId == removed.Arg && T.CurrentSource != "shorts")
            {
                T.Stop();
                VideoReady = false;
            }
        }
        _closedTabs.Add(new Tab { Kind = removed.Kind, Arg = removed.Arg, Title = removed.Title, Sub = removed.Sub, KeepInBg = removed.KeepInBg });
        if (_closedTabs.Count > 12) _closedTabs.RemoveAt(0);

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
        Sfx.Back();
        var kept = _tabs[keep];
        bool needSwitch = _tab != keep;
        for (int i = _tabs.Count - 1; i >= 0; i--)
        {
            if (i == keep) continue;
            var rem = _tabs[i];
            if (rem.Kind == "video" && rem.Arg != kept.Arg)
            {
                BgPlayers.Stop(rem.Arg);
                _tabPos.Remove(rem.Arg);
                if (T.Current?.VideoId == rem.Arg && T.CurrentSource != "shorts")
                {
                    T.Stop();
                    VideoReady = false;
                }
            }
            _tabs.RemoveAt(i);
        }
        _tab = 0;
        if (_cur == null || needSwitch)
        {
            if (kept.Kind == "video" && BgPlayers.Has(kept.Arg))
            {
                var pos = BgPlayers.Stop(kept.Arg, saveToDb: false);
                SaveTabPos(kept.Arg, pos);
            }
            Navigate(PageFor(kept), push: false);
        }
        App.Invalidate();
    }

    public static void CloseTabsRight(int from)
    {
        if (from < 0 || from >= _tabs.Count - 1) return;
        Sfx.Back();
        var needGo = _tab > from;
        for (int i = _tabs.Count - 1; i > from; i--)
        {
            var rem = _tabs[i];
            _tabs.RemoveAt(i);
            if (rem.Kind == "video" && !VideoStillOpenInOtherTab(rem.Arg, -1))
            {
                BgPlayers.Stop(rem.Arg);
                _tabPos.Remove(rem.Arg);
                if (T.Current?.VideoId == rem.Arg && T.CurrentSource != "shorts")
                {
                    T.Stop();
                    VideoReady = false;
                }
            }
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
        Gfx.FillFrosted(strip, Th.Bg, 0, opacity: 0.84f);
        Gfx.Fill(new R(strip.X, strip.Bottom - 1, strip.W, 1), Th.Line);
        const float addW = 28;
        float avail = MathF.Max(100, strip.W - 48 - addW - 8);
        float tw = Math.Clamp(avail / Math.Max(1, _tabs.Count), 108, 250);
        float x = strip.X + 24;
        int hoveredNow = -1;
        R hoveredRect = default;
        Action? deferred = null;

        for (int i = 0; i < _tabs.Count && x < strip.Right - addW - 24; i++)
        {
            var t = _tabs[i];
            int tabIdx = i;
            var r = new R(Gfx.Snap(x), strip.Y + 4, tw - 6, TabH - 8);
            var active = i == _tab;
            var id = UI.Id("tab", i);
            var over = UI.Hover(r);
            if (over) { hoveredNow = i; hoveredRect = r; }
            var sel = UI.Smooth(id ^ 0x4242, active ? 1 : 0, 20);
            if (sel > 0.01f) Gfx.Fill(r, Th.A(0.11f * sel), 8);
            if (!active) UI.Glow(id, r, 8, over);

            var isVideo = t.Kind == "video";
            var isMainPlaying = isVideo && T.Current?.VideoId == t.Arg && P.HasMedia;
            var isBg = isVideo && BgPlayers.Has(t.Arg);
            var playingNow = (isMainPlaying && !P.Paused) || (isBg && BgPlayers.IsPlaying(t.Arg));
            var bgLoading = isBg && BgPlayers.IsLoading(t.Arg);

            // Mouse wheel over any video tab smoothly adjusts THAT tab's volume with video-scaled volume SFX!
            if (over && isVideo && UI.Wheel != 0)
            {
                int delta = UI.Wheel > 0 ? 5 : -5;
                UI.Wheel = 0;
                _tabVolBoostUntil = UI.Now + 1400;
                if (isMainPlaying)
                {
                    var nv = Math.Clamp((int)Math.Round(P.Volume) + delta, 0, 150);
                    AppSettings.Current.Volume = nv;
                    _volSaveDueAt = UI.Now + 450;
                    P.SetVolumeSmooth(nv);
                    PlayVolumeSfx(nv);
                }
                else if (isBg)
                {
                    BgPlayers.AdjustVolume(t.Arg, delta);
                    PlayVolumeSfx(BgPlayers.GetVolume(t.Arg));
                }
            }

            // Subtle accent indicator bar at bottom of active or playing tab
            if (playingNow && !active)
                Gfx.Fill(new R(r.X + 10, r.Bottom - 2, r.W - 20, 2), Gfx.Fade(Th.Brand, 0.85f), 1);

            // Left icon slot: for video tabs, interactive Play/Pause button right on the tab!
            var iconR = new R(r.X + 4, r.Y + (r.H - 22) / 2, 22, 22);
            bool btnHandled = false;
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
                        btnHandled = true;
                        var tabRef = t;
                        deferred ??= isMainPlaying ? P.TogglePause : () => { tabRef.KeepInBg = true; BgPlayers.TogglePause(tabRef.Arg); };
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
                    btnHandled = true;
                    var vid = t.Arg;
                    deferred ??= () => BgPlayers.ToggleMute(vid);
                }
                rightUsed += 22;
            }

            var run = Gfx.Text(TabLabel(t), 12.5f, active || playingNow ? 600 : 400, MathF.Max(10, r.W - 28 - rightUsed));
            Gfx.Draw(run, r.X + 28, Gfx.Snap(r.Y + (r.H - run.H) / 2), Gfx.Mix(Th.Sub, Th.Text, MathF.Max(sel, playingNow ? 0.85f : 0)));

            if (UI.IconButton(UI.Id(id, 1), close, Th.Close, 9, Th.Sub)) { btnHandled = true; deferred ??= () => CloseTab(tabIdx); }
            if (!btnHandled)
            {
                if (UI.MiddleClick(r)) deferred ??= () => CloseTab(tabIdx);
                else if (UI.RightClick(r)) deferred ??= () => TabContextMenu(tabIdx);
                else if (UI.Click(id, r)) deferred ??= () => GoTab(tabIdx);
            }
            x += tw;
        }

        // "+" New Tab button — always drawn without single-frame flicker
        var addR = new R(Gfx.Snap(x + 2), strip.Y + 4, addW, TabH - 9);
        if (UI.IconButton(UI.Id("tab:add"), addR, Th.Add, 12, Th.Sub))
            deferred ??= () => { NewTab(); _search.Focus(); };

        if (deferred != null) App.Post(deferred);

        if (hoveredNow != _hoverTab)
        {
            _hoverTab = hoveredNow;
            _hoverTabSince = hoveredNow >= 0 ? UI.Now : 0;
        }
        if (hoveredNow >= 0) _hoverTabRect = hoveredRect;
    }

    /// <summary>Floating preview card under the hovered tab (crisp HD thumbnail, live playback state, per-tab smooth wheel volume).</summary>
    private static void DrawTabPreview()
    {
        _tabPreviewRect = default;
        if (_hoverTab < 0 || _hoverTab >= _tabs.Count || Menus.Open || AccountMenu.Open) return;
        bool instant = UI.Now < _tabVolBoostUntil;
        long elapsed = UI.Now - _hoverTabSince;
        if (!instant && elapsed < 220) { UI.Animate(); return; }

        var t = _tabs[_hoverTab];
        float a = instant ? 1f : Math.Clamp((elapsed - 220) / 110f, 0, 1);
        if (a < 1f) UI.Animate();
        float ease = 1f - (1f - a) * (1f - a);

        if (t.Kind == "video")
        {
            const float cw = 276, pad = 10, tw = cw - pad * 2, th = 144;
            var isMain = T.Current?.VideoId == t.Arg && P.HasMedia;
            var isBg = BgPlayers.Has(t.Arg);
            bool hasAudio = isMain || isBg;
            var title = TabLabel(t);
            var titleRun = Gfx.Text(title, 13, 600, tw, 2);
            float ch = pad + th + 8 + titleRun.H + (t.Sub.Length > 0 ? 18 : 0) + (hasAudio ? 34 : 0) + pad;

            float cx = Math.Clamp(_hoverTabRect.X + (_hoverTabRect.W - cw) / 2, 8, MathF.Max(8, Gfx.W - cw - 8));
            float cy = TopH + TabH + 4 + (1f - ease) * 6;
            var card = new R(Gfx.Snap(cx), Gfx.Snap(cy), cw, ch);
            _tabPreviewRect = card;

            Gfx.Fill(card.Inset(-3), Gfx.Fade(0xFF000000, 0.30f * ease), 14);
            Gfx.Fill(card, Gfx.Fade(Th.Menu, 0.98f * ease), 12);
            Gfx.Stroke(card, Gfx.Fade(Th.Line, ease), 12, 1f);

            var thumbR = new R(card.X + pad, card.Y + pad, tw, th);
            // Crisp 720p HD thumbnail (Images.Worker automatically falls back to hqdefault.jpg if absent)
            var thumbUrl = t.Arg.Length == 11 ? $"https://i.ytimg.com/vi/{t.Arg}/hq720.jpg" : null;
            Cards.ThumbUrl(thumbR, thumbUrl, 8, Th.Menu);

            string? status = isMain ? (P.Paused ? "Пауза" : "Играет") : isBg ? (BgPlayers.IsPaused(t.Arg) ? "Пауза (фон)" : "В фоне") : null;
            if (status != null)
            {
                var sr = Gfx.Text(status, 11, 600);
                var sb = new R(thumbR.X + 7, thumbR.Y + 7, sr.W + 14, sr.H + 5);
                bool activePlay = (isMain && !P.Paused) || (isBg && BgPlayers.IsPlaying(t.Arg));
                Gfx.Fill(sb, Gfx.Fade(activePlay ? Th.Brand : 0xFF141418, 0.88f * ease), 5);
                Gfx.Draw(sr, sb.X + 7, sb.Y + 2.5f, Gfx.Fade(0xFFFFFFFF, ease));
            }

            double pos = isMain ? P.TimePos : isBg ? BgPlayers.GetTimePos(t.Arg) : (GetSavedTabPos(t.Arg) ?? 0);
            double dur = isMain ? P.Duration : isBg ? BgPlayers.GetDuration(t.Arg) : 0;
            if (dur > 1 && pos > 0)
                UI.Progress(new R(thumbR.X + 6, thumbR.Bottom - 5, thumbR.W - 12, 3), pos / dur, Th.A(0.28f));

            float ty = thumbR.Bottom + 8;
            Gfx.Draw(titleRun, card.X + pad, ty, Gfx.Fade(Th.Text, ease));
            ty += titleRun.H + 2;
            if (t.Sub.Length > 0)
            {
                Gfx.Draw(t.Sub, card.X + pad, ty, 11.5f, Gfx.Fade(Th.Sub, ease), 400, tw);
                ty += 17;
            }
            if (hasAudio)
            {
                ty += 4;
                int vol = isMain ? Math.Clamp((int)Math.Round(P.Volume), 0, 150) : BgPlayers.GetVolume(t.Arg);
                bool muted = isMain ? P.Muted : BgPlayers.IsMuted(t.Arg);
                var rowR = new R(card.X + pad, ty, tw, 24);
                Gfx.Fill(rowR, Gfx.Fade(Th.A(0.08f), ease), 6);
                Gfx.Icon(muted || vol == 0 ? Th.Mute : Th.Volume, new R(rowR.X + 6, rowR.Y + 4, 16, 16), 11, Gfx.Fade(muted ? Th.Warn : Th.Brand, ease));
                var vTxt = Gfx.Text(muted ? "Выкл" : $"{vol}%", 11.5f, 600);
                Gfx.Draw(vTxt, rowR.Right - vTxt.W - 8, rowR.Y + (rowR.H - vTxt.H) / 2, Gfx.Fade(Th.Text, ease));
                float barX = rowR.X + 26, barW = MathF.Max(20, rowR.Right - vTxt.W - 16 - barX);
                UI.Progress(new R(barX, rowR.Y + 10, barW, 4), muted ? 0 : Math.Clamp(vol / 100.0, 0, 1), Th.A(0.22f));
            }
        }
        else
        {
            var title = TabLabel(t);
            var tr = Gfx.Text(title, 12.5f, 600, 240);
            float cw = MathF.Max(120, tr.W + 34), ch = tr.H + 16;
            float cx = Math.Clamp(_hoverTabRect.X + (_hoverTabRect.W - cw) / 2, 8, MathF.Max(8, Gfx.W - cw - 8));
            float cy = TopH + TabH + 4 + (1f - ease) * 5;
            var card = new R(Gfx.Snap(cx), Gfx.Snap(cy), cw, ch);
            _tabPreviewRect = card;
            Gfx.Fill(card, Gfx.Fade(Th.Menu, 0.96f * ease), 8);
            Gfx.Stroke(card, Gfx.Fade(Th.Line, ease), 8, 1f);
            Gfx.Icon(TabGlyph(t), new R(card.X + 8, card.Y + (ch - 14) / 2, 14, 14), 11, Gfx.Fade(Th.Brand, ease));
            Gfx.Draw(tr, card.X + 26, card.Y + 8, Gfx.Fade(Th.Text, ease));
        }
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
    private static readonly List<R> _holes = new(8);
    private static long _navAt, _toastAt;
    private static float _markY = -1, _markShown;

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

    public static void PlayVolumeSfx(double? videoVolPct = null)
    {
        var now = Environment.TickCount64;
        if (now - _lastVolSfxAt < 65) return;
        _lastVolSfxAt = now;
        Sfx.Volume(videoVolPct);
    }

    /// <summary>Smoothly adjusts the main player's volume (used by wheel over video/mini-player/PiP) with scaled volume SFX.</summary>
    public static void AdjustMainVolume(int delta)
    {
        if (!P.IsReady) return;
        var nv = Math.Clamp((int)Math.Round(P.Volume) + delta, 0, 150);
        AppSettings.Current.Volume = nv;
        _volSaveDueAt = Environment.TickCount64 + 450;
        P.SetVolumeSmooth(nv);
        PlayVolumeSfx(nv);
        App.Invalidate();
    }

    public static void Init(string[] args)
    {
        _args = args;
        ThumbPack.Open();
        MediaCache.IsActive = BgPlayers.Has;
        ThreadPool.SetMinThreads(4, 2);
        _search = new Edit("Поиск или ссылка YouTube…") { Bg = () => Th.Card };
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
        P.VolumeChanged += v =>
        {
            var iv = Math.Clamp((int)Math.Round(v), 0, 150);
            if (AppSettings.Current.Volume != iv)
            {
                AppSettings.Current.Volume = iv;
                _volSaveDueAt = Environment.TickCount64 + 500;
            }
            PlayVolumeSfx(v);
            App.Invalidate();
        };
        P.MuteChanged += m => { Sfx.Toggle(!m); App.Invalidate(); };
        T.PlaybackUnavailable += msg => Toast(msg, Th.Brand, 9000);
        T.VerificationRequired += VerifyYouTube;
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
        _ = Task.Run(Resolver.ProvisionAsync);
        _ = Task.Run(Images.CleanDisk);
        _ = Task.Run(QuickStream.Warm);
        _ = Task.Run(MediaCache.Enforce);
        P.FileLoaded += () => Storyboard.Forget();
        _ = Task.Run(YtDlp.UpdateIfStaleAsync);
        _cookieStamp = CookieStamp();
        _wasLoggedIn = CookieStore.IsLoggedIn;
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
                case "--test-tracks": App.After(7000, () => (_cur as WatchPage)?.TestTracks(v)); break;
                case "--test-sort": FeedPage.TestSort = int.Parse(v); break;
                case "--test-tab": ChannelPage.TestTab = int.Parse(v); break;
                case "--test-chip": ChannelPage.TestChip = v; break;
                case "--test-pip-alpha": Pip.TestAlpha = int.Parse(v); break;
                case "--test-autoplay": AppSettings.Current.Autoplay = v == "1"; break;
                case "--theme": AppSettings.Current.Theme = v; Themes.Load(); break;
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
        PreviewPlayer.HideNow();
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
        Sfx.Back();
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
        Sfx.Tab();
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
        if (tag == "tiktok") return;
        var p = RootPage(tag);
        SetTab("root", tag, "");
        if (_cur == p) { UI.ScrollBy(p.MainScroll, -1e9f); p.Refresh(); }
        else Navigate(p);
    }

    // the playlist a video was last opened in, so a parked tab comes back with its list (bounded)
    private static readonly Dictionary<string, string> _listOf = new();

    /// <param name="playlistId">Watch inside this playlist or mix: its items show beside the video and follow one another.</param>
    public static void OpenVideo(VideoItem v, string source, string? playlistId = null)
    {
        Sfx.VideoOpen();
        if (playlistId == null && source.StartsWith("playlist:VL", StringComparison.Ordinal)) playlistId = source[11..];
        if (playlistId != null)
        {
            if (_listOf.Count > 64 && !_listOf.ContainsKey(v.VideoId))
            {
                var first = _listOf.Keys.FirstOrDefault();
                if (first != null) _listOf.Remove(first);
            }
            _listOf[v.VideoId] = playlistId;
        }
        else _listOf.Remove(v.VideoId);
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
        if (App.Test) { Paths.Log("copy link: " + url); return true; }
        W.SetClipboardText(App.Hwnd, url);
        Toast(what, Th.Good, 2200);
        return true;
    }

    public static void OpenChannel(string id, string name)
    {
        Sfx.Channel();
        SetTab("channel", id, name);
        Navigate(new ChannelPage(id, name));
    }

    /// <summary>A list of shorts (a channel's Shorts tab) in the Shorts player, starting at one of them.</summary>
    public static void OpenShorts(List<VideoItem> items, int index)
    {
        Sfx.VideoOpen();
        SetTab("root", "shorts", "");
        Navigate(new ShortsPage(items, index));
    }

    public static void OpenPlaylist(string id, string title)
    {
        Sfx.Tab();
        SetTab("playlist", id, title);
        Navigate(new FeedPage("playlist", "VL" + id, title));
    }

    public static void OpenSearch(string q)
    {
        SetTab("search", q, "");
        Navigate(new FeedPage("search", q, null));
    }

    /// <summary>mpv starts off the UI thread the first time a card is hovered or opened.</summary>
    public static Task WarmPlayer() => _mpvInit ??= Task.Run(() =>
    {
        try { P.Mute = App.Mute; P.ScriptOpts = "fluxosc-iconfont=" + Gfx.IconFont; P.Init(App.VideoHwnd); }
        catch (Exception e) { Paths.Log("mpv init: " + e); }
    });

    public static void Play(VideoItem v, string source)
    {
        CancelSpace2x();
        MaybeHandoffToBg(v.VideoId);
        double? exactStart = null;
        if (BgPlayers.Has(v.VideoId))
        {
            var pos = BgPlayers.Stop(v.VideoId, saveToDb: false);
            if (pos > 0.3) exactStart = pos;
        }
        else if (!v.IsShort && _tabPos.TryGetValue(v.VideoId, out var savedPos) && savedPos > 0.3)
        {
            exactStart = savedPos;
        }

        int reqGen = ++_playReqGen;
        VideoReady = false;
        void StartNow()
        {
            if (reqGen != _playReqGen || !P.IsReady) return;
            if (v.IsShort && source == "shorts") P.Command("set", "loop-file", "inf");
            else if (T.Current?.VideoId != v.VideoId || T.CurrentSource == "shorts") P.Command("set", "loop-file", "no");
            T.Play(v, source, exactStart);
        }

        var warm = WarmPlayer();
        if (warm.IsCompleted) StartNow();
        else _ = warm.ContinueWith(_ => App.Post(StartNow));
    }

    public static void PlayFile(DownloadItem d)
    {
        CancelSpace2x();
        int reqGen = ++_playReqGen;
        VideoReady = false;
        void StartNow()
        {
            if (reqGen != _playReqGen || !P.IsReady) return;
            P.Command("set", "loop-file", "no");
            T.PlayFile(d);
        }
        var warm = WarmPlayer();
        if (warm.IsCompleted) StartNow();
        else _ = warm.ContinueWith(_ => App.Post(StartNow));
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
            case "menu":
                if (w != null) w.MoreMenu();
                else (_cur as ShortsPage)?.OpenContextMenu();
                break;
        }
    }

    private static void OnEnded(bool eof)
    {
        CancelSpace2x();
        Paths.Log($"ended {T.Current?.VideoId}: autoplay {(AppSettings.Current.Autoplay ? "on" : "off")}");
        if (!eof) return;
        if (T.Current is { } ended) _tabPos.Remove(ended.VideoId);
        if (_cur is ShortsPage sp) { sp.OnShortEnded(); return; }
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

    public static void WantVideo(R r, string videoId, R? clipRect = null, float radius = 0)
    {
        _videoRect = r; _videoClip = clipRect ?? r; _videoRadius = radius; _videoWanted = true; _videoFor = videoId;
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

    public static bool VideoShown(string videoId) => VideoReady && P.IsReady && T.Current?.VideoId == videoId;

    public static bool OverVideo(float x, float y)
    {
        if (Pip.Active || !_hostVisible || !_hostRect.Contains(x, y)) return false;
        if (Menus.Open || AccountMenu.Open || SharePanel.Open || ThemeCodeEditor.Open) return false;
        if (_tabPreviewRect.W > 0 && _tabPreviewRect.Contains(x, y)) return false;
        if (_toastRect.W > 0 && _toastRect.Contains(x, y)) return false;
        return true;
    }

    public static void Draw()
    {
        float w = Gfx.W, h = Gfx.H;
        _videoWanted = false;
        _miniVideoRect = null;
        _toastRect = default;
        if (Menus.Open || AccountMenu.Open || ThemeCodeEditor.Open) UI.Modal = new R(-1e6f, -1e6f, 0, 0);
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

        // Tactile frosted/matte glass backdrop behind the entire window
        Gfx.DrawFrostedBackdrop(w, h);

        var playing = cur != null && P.HasMedia;
        var onWatch = _cur is WatchPage wp && wp.Video.VideoId == cur?.VideoId || _cur is ShortsPage;
        var bar = playing && !onWatch;
        var barA = UI.Anim(UI.Id("npbar"), bar ? 1 : 0, 16);
        var barH = BarH * barA;
        var sideW = UI.Smooth(UI.Id("sidew"), AppSettings.Current.SidebarCollapsed ? 0 : SideW, 18);
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

        // top bar: clean 8px symmetric grid with frosted matte glass sheen
        _topHits.Clear();
        Gfx.FillFrosted(new R(0, 0, w, TopH), Th.Bg, 0, opacity: 0.84f);
        var menuR = new R(16, 8, 36, 36);
        _topHits.Add(menuR);
        if (UI.IconButton(UI.Id("sidebar:toggle"), menuR, Th.Hamburger, 15, AppSettings.Current.SidebarCollapsed ? Th.Sub : Th.Text)) App.Post(ToggleSidebar);
        var backR = new R(56, 8, 34, 36);
        _topHits.Add(backR);
        if (UI.IconButton(UI.Id("back"), backR, Th.Back, 14, Th.Text, _back.Count > 0)) App.Post(Back);
        var fwdR = new R(92, 8, 34, 36);
        _topHits.Add(fwdR);
        if (UI.IconButton(UI.Id("fwd"), fwdR, "\uE72A", 14, Th.Text, _forward.Count > 0)) App.Post(Forward);
        var logo = new R(132, 8, 122, 36);
        _topHits.Add(logo);
        var logoId = UI.Id("logo");
        UI.Glow(logoId, logo, 8, UI.Hover(logo));
        Gfx.Fill(new R(140, 17, 27, 18), Th.Brand, 5);
        Gfx.Icon("\uF5B0", new R(140, 17, 27, 18), 9, Th.OnBrand);
        Gfx.Draw("FluxTube", 175, 15, 15.5f, Th.Text, 600);
        if (UI.MiddleClick(logo)) App.Post(() => { NewTab("root", "home", activate: false); Toast("Открыта новая вкладка: Главная", Th.Sub, 1800); });
        else if (UI.Click(logoId, logo)) App.Post(() => OpenRoot("home"));

        const float cbw = 46, cbh = TopH;
        var zoomed = W.IsZoomed(App.Hwnd);
        if (CaptionButton("cap:close", new R(w - cbw, 0, cbw, cbh), "\uE8BB", close: true)) App.Post(() => W.PostMessageW(App.Hwnd, W.WM_CLOSE, 0, 0));
        if (CaptionButton("cap:max", new R(w - cbw * 2, 0, cbw, cbh), zoomed ? "\uE923" : "\uE922")) App.Post(() => W.ShowWindow(App.Hwnd, zoomed ? 9 : 3));
        if (CaptionButton("cap:min", new R(w - cbw * 3, 0, cbw, cbh), "\uE921")) App.Post(() => W.ShowWindow(App.Hwnd, 6));
        var rightEdge = w - cbw * 3 - 12;

        var sw = Math.Clamp(w - 720, 220, 600);
        float contentCenter = sideW + (w - sideW) * 0.5f;
        float searchX = Math.Clamp(contentCenter - sw * 0.5f, logo.Right + 16, MathF.Max(logo.Right + 16, rightEdge - sw - 64));
        _searchBox = new R(Gfx.Snap(searchX), 8, sw, 36);
        Gfx.Fill(_searchBox, Th.Card, 18);
        if (_search.Focused) Gfx.Stroke(_searchBox, Gfx.Fade(Th.Brand, 0.8f), 18, 1.2f);
        _topHits.Add(_searchBox);
        _search.Place(_searchBox, 18, 44);
        if (UI.IconButton(UI.Id("go"), new R(_searchBox.Right - 40, 10, 34, 32), Th.Search, 14, Th.Sub)) App.Post(() => Submit(_search.Text));

        var logged = CookieStore.IsLoggedIn;
        float accLeft;
        if (logged)
        {
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

        // side bar (TikTok removed cleanly)
        if (sideW > 0.5f)
        {
            Gfx.PushClip(new R(0, TopH, sideW, h - TopH));
            float sx = sideW - SideW;
            Gfx.FillFrosted(new R(sx, TopH, SideW, h - TopH), Th.Bg, 0, opacity: 0.82f);
            Gfx.Fill(new R(sx + SideW - 1, TopH, 1, h - TopH), Gfx.Fade(Th.Line, 0.7f));
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
            var sy = h - 64 - barH;
            if (sy > y) SideItem(sx, ref sy, Th.Settings, "Настройки", "settings", tag);
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
        var eff = AppSettings.Current.SeasonalEffect;
        if (eff == "halloween") DrawHalloweenFestive(w, h);
        else if (eff == "newyear") DrawNewYearFestive(w, h);
        UI.DrawAutoScrollIndicator();
        UI.Modal = null;
        DrawTabPreview();
        ThemeCodeEditor.Draw();
        AccountMenu.Draw();
        Menus.Draw();
    }

    private static readonly (float X, float Y, float R, float Spd, float Phase, float Alpha, byte Kind)[] _snow = InitSnow();
    private static readonly uint[] _garlandCols = [0xFFFFD166, 0xFFFF4D6D, 0xFF2EC4B6, 0xFF56CFE1, 0xFFFF9F1C, 0xFF80ED99, 0xFFC77DFF];
    private static readonly uint[] _halloCols = [0xFFFF6D00, 0xFF9D4EDD, 0xFFFFB703, 0xFF70E000, 0xFFFF006E];

    private static (float, float, float, float, float, float, byte)[] InitSnow()
    {
        var rng = new Random(2026);
        var arr = new (float, float, float, float, float, float, byte)[48];
        for (int i = 0; i < arr.Length; i++)
        {
            byte kind = (byte)(i < 12 ? 2 : i < 24 ? 1 : 0); // 2 = crisp star/pumpkin, 1 = mid/bat, 0 = soft dot/ember
            float r = kind == 2 ? 2.4f + (float)rng.NextDouble() * 2.2f : 1.1f + (float)rng.NextDouble() * 1.9f;
            float spd = kind == 2 ? 22f + (float)rng.NextDouble() * 20f : 13f + (float)rng.NextDouble() * 24f;
            arr[i] = ((float)rng.NextDouble(), (float)rng.NextDouble(), r, spd,
                (float)(rng.NextDouble() * Math.PI * 2), 0.22f + (float)rng.NextDouble() * 0.42f, kind);
        }
        return arr;
    }

    /// <summary>Full-width draped festive fairy-light garland spanning 0..w with hanging glowing bulbs + dark cord.</summary>
    private static void DrawFullWidthGarland(float w, float t, uint[] palette, float twinkleSpeed)
    {
        const float span = 112f; // width of one draped cord swag
        const float step = 28f;  // spacing between glowing hanging bulbs
        // Subtle draped dark wire curves across the entire window width
        int wireSegs = Math.Max(4, (int)MathF.Ceiling(w / 10f));
        for (int i = 0; i < wireSegs; i++)
        {
            float x0 = i * 10f;
            float u = (x0 % span) / span;
            float sag = MathF.Sin(u * MathF.PI) * 5.2f;
            Gfx.Fill(new R(x0, 1.5f + sag, 10.5f, 1.15f), Gfx.Fade(Th.Ink, 0.16f));
        }

        // Hanging twinkling bulbs all the way from x=12 to w-8 (full width!)
        int idx = 0;
        for (float gx = 12f; gx < w - 6f; gx += step, idx++)
        {
            float u = (gx % span) / span;
            float sag = MathF.Sin(u * MathF.PI) * 5.2f;
            float cy = 4.2f + sag;
            float wave = 0.5f + 0.5f * MathF.Sin(t * twinkleSpeed + idx * 1.15f);
            float tw = 0.42f + 0.58f * wave;
            var col = palette[idx % palette.Length];

            // Tiny socket cap hanging from the cord
            Gfx.Fill(new R(gx - 1.3f, cy - 3.2f, 2.6f, 2.2f), Gfx.Fade(0xFF1A1E28, 0.72f), 0.8f);
            // Outer atmospheric glow halo
            Gfx.Circle(gx, cy + 0.5f, 9.5f, Gfx.Fade(col, 0.10f * tw));
            Gfx.Circle(gx, cy + 0.5f, 5.2f, Gfx.Fade(col, 0.24f * tw));
            // Glowing bulb body + bright specular core
            Gfx.Circle(gx, cy + 0.6f, 2.6f, Gfx.Fade(col, 0.92f * tw));
            Gfx.Circle(gx - 0.5f, cy, 1.0f, Gfx.Fade(0xFFFFFFFF, 0.78f * tw));
        }
    }

    /// <summary>Full-width draped garland + multi-depth snowflakes & sparkling ice crystals (works on any theme).</summary>
    private static void DrawNewYearFestive(float w, float h)
    {
        float t = (UI.Now - UI.StartTick) / 1000f;
        DrawFullWidthGarland(w, t, _garlandCols, 2.3f);

        for (int i = 0; i < _snow.Length; i++)
        {
            var f = _snow[i];
            float y = (f.Y * h + t * f.Spd) % (h + 26) - 13;
            float drift = MathF.Sin(t * 0.75f + f.Phase) * (f.Kind == 2 ? 28f : 18f);
            float x = (f.X * w + drift + w) % w;

            if (f.Kind == 2)
            {
                // Sparkling crystal snowflake (soft halo + crisp cross/star core)
                float pulse = 0.72f + 0.28f * MathF.Sin(t * 2.8f + f.Phase);
                float a = f.Alpha * pulse;
                float r = f.R;
                Gfx.Circle(x, y, r * 2.2f, Gfx.Fade(0xFFBDE0FE, 0.14f * a));
                Gfx.Fill(new R(x - r * 1.15f, y - 0.65f, r * 2.3f, 1.3f), Gfx.Fade(0xFFFFFFFF, a * 0.92f), 0.6f);
                Gfx.Fill(new R(x - 0.65f, y - r * 1.15f, 1.3f, r * 2.3f), Gfx.Fade(0xFFFFFFFF, a * 0.92f), 0.6f);
                Gfx.Circle(x, y, r * 0.55f, Gfx.Fade(0xFFFFFFFF, a));
            }
            else
            {
                Gfx.Circle(x, y, f.R * 1.6f, Gfx.Fade(0xFFCAF0F8, f.Alpha * 0.15f));
                Gfx.Circle(x, y, f.R, Gfx.Fade(0xFFF0F8FF, f.Alpha * 0.85f));
            }
        }
        UI.Animate();
    }

    /// <summary>Full-width spooky garland + glowing jack-o'-lantern pumpkins + fluttering mini bats + embers.</summary>
    private static void DrawHalloweenFestive(float w, float h)
    {
        float t = (UI.Now - UI.StartTick) / 1000f;
        DrawFullWidthGarland(w, t, _halloCols, 3.0f);

        for (int i = 0; i < _snow.Length; i++)
        {
            var f = _snow[i];
            float y = (f.Y * h + t * (f.Spd * 0.82f)) % (h + 36) - 18;
            float x = (f.X * w + MathF.Sin(t * 0.72f + f.Phase) * 26f + w) % w;

            if (f.Kind == 2)
            {
                // Carved glowing jack-o'-lantern pumpkin
                float pr = 5.0f + (f.R - 2.4f) * 1.8f;
                float flick = 0.78f + 0.22f * MathF.Sin(t * 5.2f + f.Phase);
                float a = MathF.Min(0.85f, f.Alpha * 1.35f);
                // Warm candle aura
                Gfx.Circle(x, y, pr * 2.1f, Gfx.Fade(0xFFFF6D00, 0.11f * a * flick));
                // Ribbed pumpkin body (3 overlapping ovals/circles)
                Gfx.Circle(x - pr * 0.32f, y, pr * 0.85f, Gfx.Fade(0xFFE85D04, a));
                Gfx.Circle(x + pr * 0.32f, y, pr * 0.85f, Gfx.Fade(0xFFDC2F02, a));
                Gfx.Circle(x, y, pr * 0.88f, Gfx.Fade(0xFFFF7B00, a));
                // Stem
                Gfx.Fill(new R(x - 1.2f, y - pr * 1.22f, 2.4f, pr * 0.48f), Gfx.Fade(0xFF4CAF50, a), 1f);
                // Glowing amber carved eyes + grin
                uint glow = Gfx.Fade(0xFFFFEA00, a * flick);
                Gfx.Circle(x - pr * 0.30f, y - pr * 0.16f, MathF.Max(1.1f, pr * 0.18f), glow);
                Gfx.Circle(x + pr * 0.30f, y - pr * 0.16f, MathF.Max(1.1f, pr * 0.18f), glow);
                Gfx.Fill(new R(x - pr * 0.36f, y + pr * 0.18f, pr * 0.72f, MathF.Max(1.3f, pr * 0.18f)), glow, 1f);
            }
            else if (f.Kind == 1)
            {
                // Fluttering purple-shadow mini bat
                float flap = MathF.Sin(t * 9.5f + f.Phase) * 2.4f;
                float a = f.Alpha * 0.85f;
                uint batCol = Gfx.Fade(0xFF9D4EDD, a);
                Gfx.Circle(x, y, 2.2f, batCol);
                Gfx.Fill(new R(x - 7.5f, y - 1.2f + flap * 0.5f, 6.2f, 2.2f), batCol, 1f);
                Gfx.Fill(new R(x + 1.3f, y - 1.2f + flap * 0.5f, 6.2f, 2.2f), batCol, 1f);
            }
            else
            {
                // Glowing autumn / violet embers
                uint col = (i & 1) == 0 ? 0xFFFF9F1C : 0xFFC77DFF;
                Gfx.Circle(x, y, f.R * 1.5f, Gfx.Fade(col, f.Alpha * 0.16f));
                Gfx.Circle(x, y, f.R * 0.85f, Gfx.Fade(col, f.Alpha * 0.85f));
            }
        }
        UI.Animate();
    }

    private static void SideItem(float sx, ref float y, string glyph, string label, string tag, string current, bool dot = false)
    {
        var r = new R(sx + 8, y, SideW - 16, 48);
        var sel = tag == current;
        var id = UI.Id(tag, 77);
        var selA = UI.Anim(id ^ 0x77, sel ? 1 : 0, 18);
        if (selA > 0.01f) Gfx.Fill(r, Th.A(0.086f * selA), 10);
        if (!sel) UI.Glow(id, r, 10, UI.Hover(r));
        if (sel) _markY = y + 12;
        Gfx.Icon(glyph, new R(r.X, y + 5, r.W, 22), 18, sel ? Th.Text : Th.Icon);
        var run = Gfx.Text(label, 10.5f, sel ? 600 : 400);
        Gfx.Draw(run, Gfx.Snap(r.X + (r.W - run.W) / 2), y + 29, sel ? Th.Text : Th.Sub);
        if (dot) Gfx.Circle(r.X + r.W / 2 + 12, y + 9, 3.5f, Th.Brand);
        if (UI.MiddleClick(r)) App.Post(() => OpenInNewTab("root", tag, ""));
        else if (UI.Click(id, r)) App.Post(() => OpenRoot(tag));
        y += 52;
    }

    private static void NowPlaying(VideoItem cur, float sideW, float w, float h)
    {
        // Commit final seek accurately when user releases the mini-player seek scrubber
        if (!UI.Down)
        {
            if (_npDragSeek) { _npDragSeek = false; if (P.Duration > 0) P.SeekAbsolute(_npDragSeekTarget); }
            _npDragVol = false;
        }

        var r = new R(sideW, h - BarH, w - sideW, BarH);
        Gfx.FillFrosted(r, Th.Panel, 0, opacity: 0.86f);
        Gfx.Fill(new R(r.X, r.Y, r.W, 1), Th.A(0.14f));

        // Wheel over the mini-player bar smoothly adjusts playback volume
        if (UI.Wheel != 0 && UI.Hover(r))
        {
            AdjustMainVolume(UI.Wheel > 0 ? 5 : -5);
            UI.Wheel = 0;
        }

        var thumb = new R(r.X + 14, r.Y + 9, 78, 44);
        Cards.Thumb(thumb, cur, false, 7, badge: false, behind: Th.Panel);
        if (UI.Click(UI.Id("np:thumb"), thumb)) App.Post(() => OpenVideo(cur, T.CurrentSource ?? "home"));

        // Right controls: optional inline volume slider + transport buttons
        bool showVol = r.W > 740;
        float volW = showVol ? 132 : 0;
        float bx = r.Right - 14 - 5 * 40;
        float volX = bx - volW - (showVol ? 10 : 0);
        float tx = thumb.Right + 12, tw = (showVol ? volX : bx) - tx - 14;

        var titleText = string.IsNullOrEmpty(cur.Title) ? "Видео" : cur.Title;
        var titleRun = Gfx.Text(titleText, 14, 600, MathF.Max(40, tw));
        var titleR = new R(tx, r.Y + 8, MathF.Min(titleRun.W, MathF.Max(40, tw)), titleRun.H + 2);
        Gfx.Draw(titleRun, titleR.X, titleR.Y, UI.Hover(titleR) ? Th.Brand : Th.Text);
        if (UI.Click(UI.Id("np:title"), titleR)) App.Post(() => OpenVideo(cur, T.CurrentSource ?? "home"));

        var d = P.Duration;
        var shownPos = _npDragSeek ? _npDragSeekTarget : P.TimePos;
        var time = $"{Fmt.Time(shownPos)} / {Fmt.Time(d)}";
        var timeRun = Gfx.Text(time, 12);
        var ch = Gfx.Draw(cur.Channel, tx, r.Y + 31, 12, Th.Sub, 400, MathF.Min(210, MathF.Max(40, tw / 3)));
        var px = tx + ch.W + 12;
        var pw = tx + tw - timeRun.W - 12 - px;
        if (pw > 40)
        {
            var hit = new R(px - 4, r.Y + 26, pw + 8, 24);
            var hot = d > 0 && (UI.Hover(hit) || _npDragSeek);
            if (d > 0)
            {
                if (UI.Pressed && UI.Hover(hit))
                {
                    _npDragSeek = true;
                    _npDragSeekTarget = d * Math.Clamp((UI.MX - px) / pw, 0, 1);
                    _npLastSeekAt = UI.Now;
                    P.SeekAbsolute(_npDragSeekTarget);
                }
                else if (_npDragSeek)
                {
                    _npDragSeekTarget = d * Math.Clamp((UI.MX - px) / pw, 0, 1);
                    if (UI.Now - _npLastSeekAt >= 55) { _npLastSeekAt = UI.Now; P.SeekAbsolute(_npDragSeekTarget); }
                }
                if (hot) UI.Hand();
            }
            float trackH = hot ? 6f : 4f;
            var pr = new R(px, r.Y + 39 - trackH / 2, pw, trackH);
            double frac = d > 0 ? Math.Clamp(shownPos / d, 0, 1) : 0;
            UI.Progress(pr, frac, Th.A(0.22f), Th.Brand);
            if (hot)
            {
                float kx = px + (float)(pw * frac);
                Gfx.Circle(kx, r.Y + 39, 6.5f, Th.Brand);
                Gfx.Circle(kx, r.Y + 39, 2.5f, 0xFFFFFFFF);
                // Floating hover/drag timestamp tooltip above the slider
                double hoverT = _npDragSeek ? _npDragSeekTarget : d * Math.Clamp((UI.MX - px) / pw, 0, 1);
                var tipRun = Gfx.Text(Fmt.Time(hoverT), 11.5f, 600);
                float tipW = tipRun.W + 14, tipH = tipRun.H + 6;
                float tipX = Math.Clamp((_npDragSeek ? kx : UI.MX) - tipW / 2, px, px + pw - tipW);
                var tipR = new R(Gfx.Snap(tipX), r.Y + 12 - tipH, tipW, tipH);
                Gfx.Fill(tipR, Th.Menu, 5);
                Gfx.Stroke(tipR, Th.Line, 5);
                Gfx.Draw(tipRun, tipR.X + 7, tipR.Y + 3, Th.Text);
            }
        }
        Gfx.Draw(timeRun, tx + tw - timeRun.W, r.Y + 31, Th.Sub);

        float by = r.Y + 13;
        if (showVol)
        {
            bool muted = P.Muted;
            int vol = muted ? 0 : Math.Clamp((int)Math.Round(P.Volume), 0, 150);
            var muteR = new R(volX, by, 30, 36);
            if (UI.IconButton(UI.Id("np:mute"), muteR, muted || vol == 0 ? Th.Mute : Th.Volume, 14, muted ? Th.Warn : Th.Text))
                P.Command("cycle", "mute");
            var vPct = Gfx.Text($"{vol}%", 11, 600);
            Gfx.Draw(vPct, volX + volW - vPct.W, by + (36 - vPct.H) / 2, Th.Sub);
            float vtx = muteR.Right + 4, vtw = MathF.Max(28, volX + volW - vPct.W - 6 - vtx);
            var vHit = new R(vtx - 4, by + 4, vtw + 8, 28);
            var vHot = UI.Hover(vHit) || _npDragVol;
            if (UI.Pressed && UI.Hover(vHit)) _npDragVol = true;
            if (_npDragVol && UI.Now - _npLastVolAt >= 40)
            {
                _npLastVolAt = UI.Now;
                int nv = (int)MathF.Round(Math.Clamp((UI.MX - vtx) / vtw, 0, 1) * 100);
                AppSettings.Current.Volume = nv;
                _volSaveDueAt = UI.Now + 450;
                P.SetVolumeSmooth(nv, 0);
                PlayVolumeSfx(nv);
            }
            if (vHot) UI.Hand();
            UI.Progress(new R(vtx, by + 16, vtw, vHot ? 5 : 4), Math.Clamp(vol / 100.0, 0, 1), Th.A(0.22f), muted ? Th.Dim : Th.Brand);
            Gfx.Circle(vtx + vtw * Math.Clamp(vol / 100f, 0, 1), by + 18, vHot ? 5.5f : 4f, muted ? Th.Sub : Th.Text);
        }

        if (UI.IconButton(UI.Id("np:b"), new R(bx, by, 36, 36), Th.Rewind)) P.Command("seek", "-10");
        if (UI.IconButton(UI.Id("np:p"), new R(bx + 40, by, 36, 36), P.Paused ? Th.Play : Th.Pause)) P.TogglePause();
        if (UI.IconButton(UI.Id("np:f"), new R(bx + 80, by, 36, 36), Th.Forward)) P.Command("seek", "10");
        if (UI.IconButton(UI.Id("np:o"), new R(bx + 120, by, 36, 36), Th.Expand)) App.Post(() => OpenVideo(cur, T.CurrentSource ?? "home"));
        if (UI.IconButton(UI.Id("np:x"), new R(bx + 160, by, 36, 36), Th.Close)) App.Post(() => { T.Stop(); VideoReady = false; });
    }

    private static void DrawToast(float w, float bottom)
    {
        var left = _toastUntil - UI.Now;
        if (left <= 0 || _toast.Length == 0) return;
        var run = Gfx.Text(_toast, 13.5f, 400, MathF.Min(680, w - 200), 3);
        var rise = Math.Clamp((UI.Now - _toastAt) / 170f, 0, 1);
        var a = Math.Clamp(left / 250f, 0, 1) * rise;
        if (rise < 1) UI.Animate();
        var r = new R(Gfx.Snap((w - run.W) / 2 - 18), bottom - run.H - 44 + (1 - rise) * (1 - rise) * 16, run.W + 36, run.H + 20);
        _toastRect = r;
        Gfx.Fill(r, Gfx.Fade(Th.Menu, 0.96f * a), 12);
        Gfx.Stroke(r, Gfx.Fade(Th.Line, a), 12);
        Gfx.Fill(new R(r.X, r.Y + 8, 3, r.H - 16), Gfx.Fade(_toastColor, a), 1.5f);
        Gfx.Draw(run, r.X + 18, r.Y + 10, Gfx.Fade(Th.Text, a));
        if (_toastAction != null && UI.Click(UI.Id("toast:action"), r))
        {
            var action = _toastAction;
            _toastAction = null;
            _toastUntil = 0;
            App.Post(action);
        }
        if (left < 260) UI.Animate();
    }

    public static void Toast(string text, uint? color = null, int ms = 4000, Action? action = null)
    {
        _toast = text; _toastColor = color ?? Th.Sub; _toastAction = action; _toastAt = Environment.TickCount64; _toastUntil = _toastAt + ms;
        App.After(ms - 250, App.Invalidate);
        App.After(ms + 20, App.Invalidate);
        App.Invalidate();
    }

    private static int _rgnHash;

    public static void VideoHostReturned()
    {
        _hostVisible = false;
        _hostPx = default;
        _rgnHash = 0;
        W.SetWindowRgn(App.VideoHwnd, 0, false);
    }

    public static void OpenCurrentVideo()
    {
        if (T.Current is { } cur && !(_cur is WatchPage wp && wp.Video.VideoId == cur.VideoId)) OpenVideo(cur, T.CurrentSource ?? "home");
    }

    public static void AfterFrame()
    {
        if (!Pip.Active)
        {
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

                // Resize/move BEFORE applying SetWindowRgn so the new region matches the new window dimensions
                bool moved = !_hostVisible || px.L != _hostPx.L || px.T != _hostPx.T || px.R != _hostPx.R || px.B != _hostPx.B;
                if (moved)
                {
                    _hostPx = px; _hostRect = vis; _hostVisible = true;
                    W.SetWindowPos(App.VideoHwnd, 0, px.L, px.T, vw, vh, W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
                }
                else _hostRect = vis;

                int cl = Math.Clamp((int)MathF.Round((vis.X - _videoRect.X) * s), 0, vw);
                int ct = Math.Clamp((int)MathF.Round((vis.Y - _videoRect.Y) * s), 0, vh);
                int cr = Math.Clamp((int)MathF.Round((vis.Right - _videoRect.X) * s), 0, vw);
                int cb = Math.Clamp((int)MathF.Round((vis.Bottom - _videoRect.Y) * s), 0, vh);
                bool clipped = cl > 0 || ct > 0 || cr < vw || cb < vh;

                _holes.Clear();
                if (_tabPreviewRect.W > 0 && _tabPreviewRect.Intersect(_videoRect) is { W: > 0, H: > 0 })
                    _holes.Add(_tabPreviewRect);
                if (_toastRect.W > 0 && _toastRect.Intersect(_videoRect) is { W: > 0, H: > 0 })
                    _holes.Add(_toastRect);
                if (SharePanel.Open && SharePanel.Rect.W > 0 && SharePanel.Rect.Intersect(_videoRect) is { W: > 0, H: > 0 })
                    _holes.Add(SharePanel.Rect);
                if (ThemeCodeEditor.Open && ThemeCodeEditor.Rect.W > 0 && ThemeCodeEditor.Rect.Intersect(_videoRect) is { W: > 0, H: > 0 })
                    _holes.Add(ThemeCodeEditor.Rect);
                if (AccountMenu.Open && AccountMenu.Rect.W > 0 && AccountMenu.Rect.Intersect(_videoRect) is { W: > 0, H: > 0 })
                    _holes.Add(AccountMenu.Rect);
                if (Menus.Open)
                    foreach (var mr in Menus.ActiveRects)
                        if (mr.W > 0 && mr.Intersect(_videoRect) is { W: > 0, H: > 0 }) _holes.Add(mr);

                int radPx = _videoRadius > 0.5f ? Math.Max(1, (int)MathF.Round(_videoRadius * s)) : 0;
                int hash = HashCode.Combine(cl, ct, cr, cb, vw, vh, radPx, _holes.Count);
                foreach (var hr in _holes) hash = HashCode.Combine(hash, (int)hr.X, (int)hr.Y, (int)hr.W, (int)hr.H);

                if (hash != _rgnHash || moved)
                {
                    _rgnHash = hash;
                    if (!clipped && _holes.Count == 0 && radPx == 0) W.SetWindowRgn(App.VideoHwnd, 0, true);
                    else
                    {
                        nint rgn;
                        if (radPx > 0)
                        {
                            rgn = W.CreateRoundRectRgn(0, 0, vw + 1, vh + 1, radPx * 2, radPx * 2);
                            if (clipped)
                            {
                                var clipRgn = W.CreateRectRgn(cl, ct, cr, cb);
                                W.CombineRgn(rgn, rgn, clipRgn, 1 /*RGN_AND*/);
                                W.DeleteObject(clipRgn);
                            }
                        }
                        else rgn = W.CreateRectRgn(cl, ct, cr, cb);

                        foreach (var hr in _holes)
                        {
                            var cut = W.CreateRectRgn(
                                (int)MathF.Floor((hr.X - _videoRect.X) * s), (int)MathF.Floor((hr.Y - _videoRect.Y) * s),
                                (int)MathF.Ceiling((hr.Right - _videoRect.X) * s), (int)MathF.Ceiling((hr.Bottom - _videoRect.Y) * s));
                            W.CombineRgn(rgn, rgn, cut, 4 /*RGN_DIFF*/);
                            W.DeleteObject(cut);
                        }
                        W.SetWindowRgn(App.VideoHwnd, rgn, true);
                    }
                }
            }
            else if (_hostVisible)
            {
                _hostVisible = false;
                W.ShowWindow(App.VideoHwnd, W.SW_HIDE);
            }
        }
        PreviewPlayer.AfterFrame(_miniVideoRect, _toastRect);
        Edit.AfterFrame();
    }

    // ---------------- hold Space for 2x speed (YouTube-style) ----------------
    private static bool _spaceHeld, _space2x;
    private static long _spaceDownAt;
    private static double _spaceSavedSpeed = 1.0;

    public static bool SpaceDown(bool repeat)
    {
        if (Menus.Open || AccountMenu.Open || SharePanel.Open || ThemeCodeEditor.Open) return false;
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
        var cur = P.Speed;
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

    /// <summary>Global Ctrl+V when no text box is focused: pastes clipboard link or query straight into the search bar.</summary>
    private static bool PasteToSearch()
    {
        var raw = W.GetClipboardText(App.Hwnd, 1024);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var clean = string.Join(' ', raw.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (clean.Length == 0) return false;
        _search.Text = clean;
        _search.Focus(selectAll: false);
        OnSearchChanged();
        bool isLink = ExtractYouTubeId(clean) != null || clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        Toast(isLink ? "Ссылка вставлена в поиск — нажмите Enter, чтобы открыть" : "Текст вставлен в поиск — нажмите Enter", Th.Good, 2400);
        App.Invalidate();
        return true;
    }

    public static bool Key(int vk, bool ctrl, bool alt)
    {
        if (Menus.Open && Menus.Key(vk)) return true;
        if (vk == W.VK_ESCAPE)
        {
            if (UI.AutoScrollActive) UI.StopAutoScroll();
            else if (ThemeCodeEditor.Open) ThemeCodeEditor.Close();
            else if (AccountMenu.Open) AccountMenu.Close();
            else if (SharePanel.Open) SharePanel.Close();
            else if (_fullscreen) P.SetFullscreenFlag(false);
            else if (_cur is WatchPage || _cur is ChannelPage || _cur is ShortsPage { Tag: "" } || _cur is FeedPage { Tag: "" }) Back();
            return true;
        }
        // Block navigation/player hotkeys while an overlay panel is open
        if (AccountMenu.Open || SharePanel.Open || ThemeCodeEditor.Open) return false;

        // Global Ctrl+V anywhere outside Edit boxes -> immediately paste link or text into search!
        if (ctrl && !alt && vk == 'V' && !Edit.AnyFocused)
            return PasteToSearch();

        if (alt && vk == W.VK_LEFT) { Back(); return true; }
        if (alt && vk == W.VK_RIGHT) { Forward(); return true; }
        if (alt) return false;
        if (ctrl && vk == 'Z') { if (W.KeyDown(0x10)) Forward(); else Back(); return true; }
        if (ctrl && vk == 'Y') { Forward(); return true; }
        if (ctrl && vk == 'F' || vk == 0xBF && !ctrl) { _search.Focus(); return true; }
        if (ctrl && vk == 'B') { ToggleSidebar(); return true; }
        if (ctrl && (vk == 0xBB || vk == 0x6B)) { AdjustZoom(0.10f); return true; }
        if (ctrl && (vk == 0xBD || vk == 0x6D)) { AdjustZoom(-0.10f); return true; }
        if (ctrl && (vk == '0' || vk == 0x60)) { SetZoom(1.0f); return true; }
        if (ctrl && (vk == 'C' || vk == 0x2D) && (Sel.CopySelection() || CopyPageLink(W.KeyDown(0x10)))) return true;
        if (ctrl && vk == 'W') { CloseTab(_tab); return true; }
        if (ctrl && vk == 9 && _tabs.Count > 1)
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
        if (vk == W.VK_F5 || ctrl && vk == 'R') { _cur?.Refresh(); return true; }
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
        if (fs && cur != null && _cur is not ShortsPage && !(_cur is WatchPage wp && wp.Video.VideoId == cur.VideoId))
            OpenVideo(cur, T.CurrentSource ?? "home");
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

    // ---------------- account, default browser & WebView login ----------------

    public static void Login() => LoginNewAccount();

    /// <summary>Primary sign-in: opens the user's real DEFAULT Windows browser (Chrome/Edge/Brave/etc.) with clean profile + auto cookie capture.</summary>
    public static void LoginNewAccount() => BrowserLogin.OpenExistingBrowserForLogin();

    /// <summary>Open the default browser for a YouTube challenge and capture the refreshed session.</summary>
    public static void VerifyYouTube() => BrowserLogin.OpenExistingBrowserForLogin();

    /// <summary>Secondary fallback: opens the built-in WebView2 Login Window with a clean session.</summary>
    public static void LoginWebView()
    {
        AccountMenu.SaveActiveToVault();
        try
        {
            if (_helper is { HasExited: false }) { _helper.Kill(true); _helper.WaitForExit(1200); }
        }
        catch { }

        // Clear WebView2 browser cookies so Google always prompts for a fresh account sign-in
        try
        {
            var netDir = Path.Combine(Paths.WebViewData, "EBWebView", "Default", "Network");
            if (Directory.Exists(netDir))
            {
                foreach (var f in Directory.GetFiles(netDir, "Cookies*"))
                    try { File.Delete(f); } catch { }
            }
            var oldCookies = Path.Combine(Paths.WebViewData, "EBWebView", "Default", "Cookies");
            if (File.Exists(oldCookies)) try { File.Delete(oldCookies); } catch { }
        }
        catch { }

        _awaitingLogin = true;
        _helperExitedAt = 0;
        StartHelper("--login", "Вход в YouTube");
    }

    public static void Logout()
    {
        CookieStore.Clear();
        _cookieStamp = CookieStamp();
        _wasLoggedIn = false;
        _awaitingLogin = false;

        OnAccountChanged();
        Toast("Вы вышли. Локальная история сохранена.");
    }

    /// <summary>Browser sign-in window lives in a separate process that exists only while signing in.</summary>
    private static void StartHelper(string args, string what)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "web", "FluxTube.exe");
        if (!File.Exists(exe)) { _awaitingLogin = false; Toast(what + ": не найден модуль web\\FluxTube.exe. В настройках есть импорт cookies.txt.", Th.Warn, 7000); return; }
        try
        {
            _helper = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        }
        catch (Exception e) { _awaitingLogin = false; Toast(what + ": " + e.Message, Th.Brand, 7000); }
    }

    public static void OnAccountChanged()
    {
        FeedCache.ClearAll();
        CookieStore.NotifyExternalChange();
        InnerTube.Instance.ResetSession();
        QuickStream.Invalidate();
        Resolver.Instance.Invalidate();
        AccountMenu.Refresh();
        if (P.IsReady) P.ApplySettings();
        _back.Clear();
        _forward.Clear();
        foreach (var p in _roots.Values)
        {
            switch (p)
            {
                case FeedPage fp: fp.Invalidate(); break;
                case HistoryPage hp: hp.Invalidate(); break;
                case ShortsPage sp: sp.Invalidate(); break;
            }
        }
        if (_cur is FeedPage f) f.Shown();
        else if (_cur is HistoryPage h) h.Shown();
        else if (_cur is ShortsPage s) s.Shown();
        App.Invalidate();
    }

    // ---------------- housekeeping ----------------

    /// <summary>Twice a second: things that have no event of their own.</summary>
    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (_volSaveDueAt > 0 && now >= _volSaveDueAt)
        {
            _volSaveDueAt = 0;
            AppSettings.Current.Save();
        }

        // Gentle idle cleanup (no blocking full Gen2 GC stalls)
        if (now - UI.LastInput > 5000)
        {
            Gfx.DropIdleText();
            UI.PruneCaches();
        }

        var stamp = CookieStamp();
        if (stamp != _cookieStamp)
        {
            var was = _wasLoggedIn;
            _cookieStamp = stamp;
            CookieStore.NotifyExternalChange();
            var nowLogged = CookieStore.IsLoggedIn;
            _wasLoggedIn = nowLogged;

            bool loginDone = _awaitingLogin || was != nowLogged || _helper is { HasExited: false };
            if (loginDone)
            {
                bool wasAwaiting = _awaitingLogin;
                _awaitingLogin = false;
                OnAccountChanged();
                if (nowLogged && (wasAwaiting || !was))
                {
                    Toast(was ? "Новый аккаунт добавлен и активирован" : "Вы вошли в YouTube", Th.Good, 3500);
                    OpenRoot("home");
                }
            }
        }
        else if (_awaitingLogin && _helper is { HasExited: true })
        {
            if (_helperExitedAt == 0) _helperExitedAt = now;
            else if (now - _helperExitedAt > 3000) _awaitingLogin = false;
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

        if (_testClickAt > 0 && now > _testClickAt && _roots.GetValueOrDefault("home") is FeedPage home && home.Count > _testClickIndex)
        {
            OpenVideo(home.ItemAt(_testClickIndex), "test");
            _testClickIndex += 4;
            _testClickAt = _testClickIndex <= 15 ? now + 14000 : -1;
        }

        if (_testNextAt > 0 && now > _testNextAt)
        {
            _testNextAt = -1;
            if (_cur is WatchPage w && w.NextUp() is { } next) OpenVideo(next, "test");
        }

        if (_benchStep >= 0 && _cur is FeedPage fp && fp.Count > 0)
        {
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
            Downloader.Instance.Shutdown();
            Loupe.Shutdown();
            PreviewPlayer.Shutdown();
            Sfx.Shutdown();
            BgPlayers.DisposeAll();
            AppSettings.Current.Save();
            P.Dispose();
            StreamProxy.Instance.Dispose();
            Resolver.Instance.Dispose();
            MediaCache.Shutdown();
            ThumbPack.Close();
            Db.Shutdown();
        }
        catch (Exception e) { Paths.Log("shutdown: " + e.Message); }
    }
}
