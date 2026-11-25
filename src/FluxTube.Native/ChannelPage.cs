using FluxTube.Models;
using FluxTube.Services;

namespace FluxTube.Pages;

/// <summary>
/// A channel the way youtube.com shows it: banner, avatar, name and counts, subscribe, tabs; the home tab is a column
/// of shelves (one row each, paged sideways), the other tabs are grids. Everything scrolls as one page.
/// </summary>
public sealed class ChannelPage(string channelId, string name) : IPage
{
    private static readonly HashSet<string> SkipTabs =
        ["Магазин", "Store", "Поиск", "Search", "Записи", "Posts", "Сообщество", "Community"];

    private sealed class Grid
    {
        public readonly List<VideoItem> Items = [];
        public string? Cont;
        public bool Loading, LoadingMore, Failed;
        public List<(string Title, string Token, bool Selected)> Chips = [];
        public string? Chip; // the order picked by the user
        public int Gen;
    }

    public static int TestTab;       // scripted checks: the tab a channel opens on, and the order picked in it
    public static string? TestChip;
    private ChannelInfo? _info;
    private bool _loading, _failed;
    private int _loadGen;
    private int _tab; // index into _tabs; 0 = home
    private readonly List<(string Title, string Params)> _tabs = [("Главная", "")];
    private readonly Dictionary<string, Grid> _grids = new();
    private readonly Dictionary<int, float> _shelfPage = new();
    private readonly Cards.HoverPrefetch _hover = new();
    private readonly int _scroll = UI.Id("channel:" + channelId);
    private bool _descOpen;
    private bool? _subscribed;
    private float _contentH = 2000;

    public string Tag => "";
    public int MainScroll => _scroll;
    public string ChannelId => channelId;
    public string Name => _info?.Title is { Length: > 0 } t ? t : name;

    public void Shown() { if (_info == null && !_loading) Load(); }
    public void Hidden() { }
    public void Refresh() { _grids.Clear(); _shelfPage.Clear(); Load(); }

    private void Load()
    {
        var gen = ++_loadGen;
        _loading = true; _failed = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var c = await InnerTube.Instance.ChannelAsync(channelId);
                foreach (var s in c.Shelves) Own(s.Items, c);
                App.Post(() =>
                {
                    if (gen != _loadGen) return;
                    _info = c; _loading = false;
                    _subscribed = c.Subscribed;
                    _tabs.Clear();
                    foreach (var t in c.Tabs.Where(t => !SkipTabs.Contains(t.Title))) _tabs.Add(t);
                    if (_tabs.Count == 0) _tabs.Add(("Главная", ""));
                    // a channel without a home tab (or with an empty one): open on its videos
                    if (c.Shelves.Count == 0 && _tabs.Count > 1) SelectTab(1);
                    _tab = Math.Min(_tab, _tabs.Count - 1);
                    if (TestTab > 0 && TestTab < _tabs.Count)
                    {
                        SelectTab(TestTab);
                        if (TestChip != null) App.After(3000, () => { if (_grids.TryGetValue(_tabs[_tab].Params, out var tg) && tg.Chips.FirstOrDefault(ch => ch.Title == TestChip) is { Token: not null } tc) LoadChip(tg, tc.Title, tc.Token); });
                    }
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log($"channel {channelId}: {e.Message}");
                App.Post(() => { if (gen != _loadGen) return; _loading = false; _failed = true; App.Invalidate(); });
            }
        });
    }

    /// <summary>Videos listed on the channel's own page have no channel line: fill it in, so history and the
    /// player know whose video it is, and keep views and date as the visible line.</summary>
    private static void Own(List<VideoItem> items, ChannelInfo c)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var v = items[i];
            if (v.PlaylistId != null || (v.ChannelId != null && v.ChannelId != c.Id)) continue;
            var meta = v.ChannelId == null && v.Channel.Length > 0 ? (v.Meta.Length > 0 ? v.Channel + " · " + v.Meta : v.Channel) : v.Meta;
            items[i] = new VideoItem
            {
                VideoId = v.VideoId, Title = v.Title, Channel = c.Title, ChannelId = c.Id, Duration = v.Duration, Meta = meta,
                AvatarUrl = v.AvatarUrl ?? c.Avatar, IsLive = v.IsLive, IsShort = v.IsShort, Progress = v.Progress,
            };
        }
    }

    private void SelectTab(int i)
    {
        _tab = i;
        var prm = _tabs[i].Params;
        if (prm.Length > 0 && !_grids.ContainsKey(prm)) LoadGrid(prm);
        App.Invalidate();
    }

    private void LoadGrid(string prm)
    {
        var g = _grids[prm] = new Grid { Loading = true };
        _ = Task.Run(async () =>
        {
            try
            {
                var c = await InnerTube.Instance.ChannelAsync(channelId, prm);
                var page = Cards.WithProgress(c.Grid);
                var info = _info ?? c;
                Own(page.Items, info);
                App.Post(() => { g.Items.AddRange(page.Items); g.Cont = page.Continuation; g.Chips = c.Chips; g.Loading = false; App.Invalidate(); });
            }
            catch (Exception e)
            {
                Paths.Log($"channel tab {channelId}: {e.Message}");
                App.Post(() => { g.Loading = false; g.Failed = true; App.Invalidate(); });
            }
        });
    }

    /// <summary>Another order of the same tab (newest / popular / oldest): the grid starts over from that order's token.</summary>
    private void LoadChip(Grid g, string title, string token)
    {
        var gen = ++g.Gen;
        g.Chip = title;
        g.Loading = true; g.LoadingMore = false; g.Failed = false;
        g.Items.Clear(); g.Cont = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var page = Cards.WithProgress(await InnerTube.Instance.ContinueAsync("browse", token, shorts: true));
                if (_info != null) Own(page.Items, _info);
                App.Post(() => { if (gen != g.Gen) return; g.Items.AddRange(page.Items); g.Cont = page.Continuation; g.Loading = false; App.Invalidate(); });
            }
            catch (Exception e)
            {
                Paths.Log("channel order: " + e.Message);
                App.Post(() => { if (gen != g.Gen) return; g.Loading = false; g.Failed = true; App.Invalidate(); });
            }
        });
    }

    private void LoadMore(Grid g)
    {
        if (g.Cont == null || g.LoadingMore) return;
        g.LoadingMore = true;
        var gen = g.Gen;
        var tok = g.Cont;
        _ = Task.Run(async () =>
        {
            try
            {
                var page = Cards.WithProgress(await InnerTube.Instance.ContinueAsync("browse", tok, shorts: true));
                if (_info != null) Own(page.Items, _info);
                App.Post(() =>
                {
                    if (gen != g.Gen) return;
                    var have = g.Items.Select(i => i.VideoId + i.PlaylistId).ToHashSet();
                    g.Items.AddRange(page.Items.Where(i => have.Add(i.VideoId + i.PlaylistId)));
                    g.Cont = page.Items.Count == 0 ? null : page.Continuation;
                    g.LoadingMore = false;
                    App.Invalidate();
                });
            }
            catch { App.Post(() => { if (gen != g.Gen) return; g.LoadingMore = false; g.Cont = null; App.Invalidate(); }); }
        });
    }

    private void ToggleSubscribe()
    {
        if (!CookieStore.IsLoggedIn) { Shell.Toast("Чтобы подписаться, войдите в аккаунт YouTube", Th.Warn); return; }
        var want = _subscribed != true;
        _subscribed = want; // shows at once; put back if YouTube says no
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await InnerTube.Instance.SubscribeAsync(channelId, want); }
            catch (Exception e) { Paths.Log("subscribe: " + e.Message); ok = false; }
            App.Post(() =>
            {
                if (!ok) { _subscribed = !want; Shell.Toast("YouTube не принял подписку, попробуйте позже", Th.Warn); }
                else Shell.Toast(want ? "Вы подписались на " + (_info?.Title ?? name) : "Подписка отменена", want ? Th.Good : null);
                App.Invalidate();
            });
        });
    }

    public void Draw(R r)
    {
        var view = new R(r.X, r.Y, r.W - 4, r.H);
        var scroll = UI.BeginScroll(_scroll, view, _contentH);
        var w = MathF.Min(r.W - 56, 1500);
        var x0 = Gfx.Snap(r.X + (r.W - w) / 2);
        var y = r.Y + 12 - scroll;
        var c = _info;

        // banner
        if (c?.Banner != null)
        {
            var bh = MathF.Round(w / 6.2f);
            Cards.ThumbUrl(new R(x0, y, w, bh), c.Banner, 14);
            y += bh + 18;
        }
        else if (c == null && _loading)
        {
            Gfx.Fill(new R(x0, y, w, MathF.Round(w / 6.2f)), UI.Pulse(Th.Thumb), 14);
            y += MathF.Round(w / 6.2f) + 18;
        }

        // avatar, name, counts, description, buttons
        var av = w > 900 ? 150f : 104f;
        if (c != null) Cards.Avatar(x0, y, av, c.Avatar, c.Title);
        else Gfx.Circle(x0 + av / 2, y + av / 2, av / 2, UI.Pulse(Th.Thumb));
        float tx = x0 + av + 22, tw = w - av - 22;
        var title = c?.Title is { Length: > 0 } t0 ? t0 : name;
        var tr = Gfx.Draw(title, tx, y + 4, w > 900 ? 34 : 26, Th.Text, 700, tw - 40);
        Cards.CopyMenu(new R(tx, y + 4, tr.W, tr.H), title, "Копировать название канала");
        if (c?.Verified == true) Gfx.Icon(Th.Check, new R(tx + tr.W + 8, y + 4 + tr.H / 2 - 10, 20, 20), 12, Th.Sub);
        var ty = y + 8 + tr.H;
        if (c != null)
        {
            var meta = string.Join("  ·  ", new[] { c.Handle, c.Subscribers, c.VideoCount }.Where(s => s.Length > 0));
            var mr = Gfx.Draw(meta, tx, ty, 14, Th.Sub, 400, tw);
            Cards.CopyMenu(new R(tx, ty, mr.W, mr.H), c.Handle, "Копировать " + c.Handle);
            ty += mr.H + 8;
            if (c.Description.Length > 0)
            {
                var dr = Gfx.Text(c.Description, 13.5f, 400, MathF.Min(tw, 760), _descOpen ? 12 : 2);
                var dRect = new R(tx, ty, dr.W, dr.H);
                Sel.Text(UI.Id(_scroll, 8003), c.Description, tx, ty, 13.5f, Th.Sub, 400, MathF.Min(tw, 760), _descOpen ? 12 : 2, links: true,
                    onLink: l => { if (l.Kind == '#') Shell.OpenSearch(l.Value); else if (l.Kind == 'u') W.Open(l.Value); });
                ty += dr.H + 2;
                var more = Gfx.Text(_descOpen ? "Свернуть" : "Ещё", 13, 600);
                var moreR = new R(tx, ty, more.W, more.H);
                Gfx.Draw(more, tx, ty, UI.Hover(moreR) ? Th.Text : Th.Sub);
                if (UI.Click(UI.Id("ch:desc", _scroll), moreR)) _descOpen = !_descOpen;
                ty += more.H + 10;
                Cards.CopyMenu(dRect, c.Description, "Копировать описание");
            }
            // subscribe: filled like youtube.com when not subscribed, quiet when already
            float bx = tx;
            var subText = _subscribed == true ? "Вы подписаны" : "Подписаться";
            if (_subscribed == true)
            {
                if (UI.Pill(UI.Id("ch:sub", _scroll), bx, ty, 36, Th.Subs, subText, out var sw)) ToggleSubscribe();
                bx += sw + 8;
            }
            else
            {
                if (UI.Pill(UI.Id("ch:sub", _scroll), bx, ty, 36, null, subText, out var sw, Th.Text, Th.Bg)) ToggleSubscribe();
                bx += sw + 8;
            }
            if (UI.Pill(UI.Id("ch:web", _scroll), bx, ty, 36, Th.Link, "На YouTube", out _))
                W.Open("https://www.youtube.com/" + (c.Handle.StartsWith('@') ? c.Handle : "channel/" + channelId));
            ty += 36;
        }
        else if (_failed)
        {
            Gfx.Draw("Не удалось загрузить канал.", tx, ty, 14, Th.Sub);
            if (UI.Pill(UI.Id("ch:retry", _scroll), tx, ty + 28, 34, Th.Refresh, "Повторить", out _)) App.Post(Load);
            ty += 64;
        }
        y = MathF.Max(y + av, ty) + 20;

        // tabs
        float tabX = x0;
        var tabY = y;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var run = Gfx.Text(_tabs[i].Title, 15, 600);
            var tRect = new R(tabX - 4, tabY, run.W + 8, 44);
            var on = i == _tab;
            var over = UI.Hover(tRect);
            Gfx.Draw(run, tabX, tabY + 11, on || over ? Th.Text : Th.Sub);
            if (UI.Click(UI.Id("ch:tab", i), tRect) && !on) SelectTab(i);
            if (on) _tabLineX = tabX;
            if (on) _tabLineW = run.W;
            tabX += run.W + 30;
        }
        var lx = UI.Smooth(UI.Id("ch:tabx", _scroll), _tabLineX, 22);
        var lw = UI.Smooth(UI.Id("ch:tabw", _scroll), _tabLineW, 22);
        Gfx.Fill(new R(x0, tabY + 43, w, 1), Th.Line);
        Gfx.Fill(new R(lx, tabY + 41, lw, 3), Th.Text, 1.5f);
        y = tabY + 64;

        if (_tab == 0) y = DrawShelves(x0, y, w, r);
        else y = DrawGrid(x0, y, w, r);

        var needH = y + scroll - r.Y + 30;
        if (MathF.Abs(needH - _contentH) > 1f) { _contentH = needH; UI.Animate(); }
        UI.EndScroll();
    }

    private float _tabLineX, _tabLineW;

    private static (int Cols, float CardW, float ThumbH) Layout(float w)
    {
        const float gap = 18;
        var cols = Math.Max(1, (int)((w + gap) / (250 + gap)));
        var cardW = MathF.Floor((w - gap * (cols - 1)) / cols);
        return (cols, cardW, MathF.Round(cardW * 9 / 16));
    }

    private float DrawShelves(float x0, float y, float w, R r)
    {
        var c = _info;
        if (c == null) return Skeleton(x0, y, w);
        if (c.Shelves.Count == 0) { Gfx.Draw("На главной канала пусто: загляните во вкладку «Видео».", x0, y, 14, Th.Sub); return y + 40; }
        var (cols, cardW, thumbH) = Layout(w);
        const float gap = 18;
        for (int s = 0; s < c.Shelves.Count; s++)
        {
            var sh = c.Shelves[s];
            if (sh.Title.Length == 0 && sh.Items.Count == 1)
            {
                y = Featured(sh.Items[0], x0, y, w);
                continue;
            }
            // title, "play all", paging arrows
            var tr = Gfx.Draw(sh.Title, x0, y, 18, Th.Text, 700, w - 260);
            float hx = x0 + tr.W + 16;
            if (sh.BrowseId?.StartsWith("VL") == true)
            {
                var pid = sh.BrowseId[2..];
                if (UI.Pill(UI.Id("ch:all", s), hx, y - 4, 32, Th.Play, "Воспроизвести все", out _, Th.A(0.08f)))
                    App.Post(() => Shell.OpenPlaylist(pid, sh.Title));
            }
            else if (sh.Params is { Length: > 0 } prm && _tabs.FindIndex(t => t.Params == prm) is var ti && ti > 0)
            {
                if (UI.Pill(UI.Id("ch:all", s), hx, y - 4, 32, null, "Все", out _, Th.A(0.08f))) SelectTab(ti);
            }
            var pages = Math.Max(1, (sh.Items.Count + cols - 1) / cols);
            var page = Math.Clamp((int)_shelfPage.GetValueOrDefault(s), 0, pages - 1);
            _shelfPage[s] = page;
            if (pages > 1)
            {
                if (UI.IconButton(UI.Id("ch:prev", s), new R(x0 + w - 80, y - 6, 36, 36), "", 13, null, page > 0)) _shelfPage[s] = page - 1;
                if (UI.IconButton(UI.Id("ch:next", s), new R(x0 + w - 38, y - 6, 36, 36), "", 13, null, page < pages - 1)) _shelfPage[s] = page + 1;
            }
            y += 40;
            // the row slides sideways when paged
            var shift = UI.Smooth(UI.Id(_scroll, 7000 + s), page * (cardW + gap) * cols, 16);
            var rowH = thumbH + 92;
            var clip = new R(x0 - 8, y - 8, w + 16, rowH + 8);
            if (clip.Bottom > r.Y && clip.Y < r.Bottom)
            {
                Gfx.PushClip(clip);
                for (int i = 0; i < sh.Items.Count; i++)
                {
                    var cx = x0 + i * (cardW + gap) - shift;
                    if (cx + cardW < x0 - 10 || cx > x0 + w + 10) continue;
                    Cards.GridCard(new R(cx, y, cardW, rowH - 8), thumbH, sh.Items[i], UI.Id(UI.Id("ch:shelf", s), i), _hover, "channel:" + channelId, channelLine: false);
                }
                Gfx.PopClip();
            }
            y += rowH + 18;
            Gfx.Fill(new R(x0, y - 10, w, 1), Th.Line);
            y += 14;
        }
        return y;
    }

    /// <summary>The channel's featured video: a big thumbnail with its title beside it.</summary>
    private float Featured(VideoItem v, float x0, float y, float w)
    {
        var tw = MathF.Min(480, w * 0.42f);
        var th = MathF.Round(tw * 9 / 16);
        var card = new R(x0, y, w, th);
        var id = UI.Id("ch:feat", _scroll);
        var over = UI.Hover(card);
        var glow = UI.Glow(id, card.Inset(-7, -7), 14, over);
        Cards.Thumb(new R(x0, y, tw, th), v, true, 12, behind: UI.GlowOn(Th.Bg, glow));
        var t = Gfx.Draw(v.Title, x0 + tw + 22, y + 4, 18, Th.Text, 600, w - tw - 40, 2);
        Gfx.Draw(v.Meta, x0 + tw + 22, y + 12 + t.H, 13, Th.Sub, 400, w - tw - 40);
        if (over) _hover.Over(v);
        if (UI.Click(id, card)) App.Post(() => Shell.OpenVideo(v, "channel:" + channelId));
        return y + th + 30;
    }

    private float DrawGrid(float x0, float y, float w, R r)
    {
        if (!_grids.TryGetValue(_tabs[_tab].Params, out var g)) { SelectTab(_tab); return Skeleton(x0, y, w); }
        if (g.Chips.Count > 1)
        {
            // newest / popular / oldest, as YouTube offers them for this tab
            float cx = x0;
            var cur = g.Chip ?? g.Chips.FirstOrDefault(c => c.Selected).Title ?? g.Chips[0].Title;
            foreach (var chip in g.Chips)
            {
                var on = chip.Title == cur;
                if (UI.Pill(UI.Id(UI.Id("ch:chip", _tab), chip.Title.GetHashCode()), cx, y, 32, null, chip.Title, out var pw, on ? Th.Text : Th.A(0.08f), on ? Th.Bg : Th.Text) && !on)
                    LoadChip(g, chip.Title, chip.Token);
                cx += pw + 8;
            }
            y += 48;
        }
        if (g.Loading) return Skeleton(x0, y, w);
        if (g.Failed || g.Items.Count == 0)
        {
            Gfx.Draw(g.Failed ? "Не удалось загрузить." : "Здесь пока пусто.", x0, y, 14, Th.Sub);
            if (g.Failed && UI.Pill(UI.Id("ch:tabretry", _tab), x0, y + 28, 34, Th.Refresh, "Повторить", out _))
            {
                var prm = _tabs[_tab].Params;
                App.Post(() => LoadGrid(prm));
            }
            return y + (g.Failed ? 70 : 40);
        }
        if (g.Items.Count > 0 && g.Items[0].IsShort) return DrawShortsGrid(g, x0, y, w, r);
        var (cols, cardW, thumbH) = Layout(w);
        const float gap = 18;
        var rowH = thumbH + 100;
        var rows = (g.Items.Count + cols - 1) / cols;
        for (int row = 0; row < rows; row++)
        {
            var ry = y + row * rowH;
            if (ry + rowH < r.Y || ry > r.Bottom) continue;
            for (int col = 0; col < cols; col++)
            {
                int i = row * cols + col;
                if (i >= g.Items.Count) break;
                Cards.GridCard(new R(x0 + col * (cardW + gap), ry, cardW, rowH - 14), thumbH, g.Items[i], UI.Id(UI.Id("ch:grid", _tab), i), _hover, "channel:" + channelId, channelLine: false);
            }
        }
        y += rows * rowH;
        if (g.LoadingMore) UI.Spinner(x0 + w / 2, y + 20, 10, Th.Sub);
        if (g.Cont != null && y - r.Bottom < rowH * 2) LoadMore(g);
        return y + (g.Cont != null ? 50 : 10);
    }

    /// <summary>A channel's Shorts: tall cards; a click opens the Shorts player on that one, the rest follow in order.</summary>
    private float DrawShortsGrid(Grid g, float x0, float y, float w, R r)
    {
        const float gap = 14;
        var cols = Math.Max(2, (int)((w + gap) / (190 + gap)));
        var cardW = MathF.Floor((w - gap * (cols - 1)) / cols);
        var thumbH = MathF.Round(cardW * 16 / 9);
        var rowH = thumbH + 74;
        var rows = (g.Items.Count + cols - 1) / cols;
        for (int row = 0; row < rows; row++)
        {
            var ry = y + row * rowH;
            if (ry + rowH < r.Y || ry > r.Bottom) continue;
            for (int col = 0; col < cols; col++)
            {
                int i = row * cols + col;
                if (i >= g.Items.Count) break;
                var v = g.Items[i];
                var card = new R(x0 + col * (cardW + gap), ry, cardW, rowH - 12);
                var id = UI.Id(UI.Id("ch:short", _tab), i);
                var glow = UI.Glow(id, card.Inset(-6, -6), 14, UI.Hover(card));
                Cards.ThumbUrl(new R(card.X, ry, cardW, thumbH), $"https://i.ytimg.com/vi/{v.VideoId}/oar2.jpg", 12, UI.GlowOn(Th.Bg, glow));
                var t = Gfx.Draw(v.Title, card.X, ry + thumbH + 8, 13.5f, Th.Text, 600, cardW, 2);
                Gfx.Draw(v.Meta, card.X, ry + thumbH + 10 + t.H, 12, Th.Sub, 400, cardW);
                if (UI.Click(id, card))
                {
                    var list = g.Items.Where(x => x.IsShort).Select(x => new VideoItem { VideoId = x.VideoId, IsShort = true, Title = x.Title, Channel = Name, ChannelId = channelId, AvatarUrl = _info?.Avatar, Meta = "" }).ToList();
                    var at = list.FindIndex(x => x.VideoId == v.VideoId);
                    App.Post(() => Shell.OpenShorts(list, at));
                }
                else if (UI.RightClick(card)) Menus.Popup(Cards.MenuFor(v));
            }
        }
        y += rows * rowH;
        if (g.LoadingMore) UI.Spinner(x0 + w / 2, y + 20, 10, Th.Sub);
        if (g.Cont != null && y - r.Bottom < rowH * 2) LoadMore(g);
        return y + (g.Cont != null ? 50 : 10);
    }

    private static float Skeleton(float x0, float y, float w)
    {
        var (cols, cardW, thumbH) = Layout(w);
        var sk = UI.Pulse(Th.Thumb);
        for (int i = 0; i < cols * 2; i++)
        {
            float sx = x0 + i % cols * (cardW + 18), sy = y + i / cols * (thumbH + 100);
            Gfx.Fill(new R(sx, sy, cardW, thumbH), sk, 12);
            Gfx.Fill(new R(sx, sy + thumbH + 12, cardW * 0.8f, 13), sk, 4);
            Gfx.Fill(new R(sx, sy + thumbH + 34, cardW * 0.5f, 11), sk, 4);
        }
        return y + 2 * (thumbH + 100);
    }
}
