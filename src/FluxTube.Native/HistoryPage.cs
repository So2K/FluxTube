using FluxTube.Models;
using FluxTube.Services;

namespace FluxTube.Pages;

/// <summary>
/// History, two views of it:
///  - "YouTube": the account's own watch history, as youtube.com/feed/history shows it: days, a Shorts shelf per
///    day, the videos under it. It is the same list on every device, so what was watched in a browser or on a
///    phone is here too. Asked again every time the page is opened.
///  - "FluxTube": this app's own log (every start, where it stopped, how long it was watched).
/// </summary>
public sealed class HistoryPage : IPage
{
    private readonly TimelinePage _local = new(false);
    private bool _youtube = CookieStore.IsLoggedIn;
    private int _filter; // 0 all, 1 videos, 2 shorts
    private List<HistoryDay> _days = [];
    private string? _next;
    private bool _loading, _loadingMore, _failed;
    private long _loadedAt;
    private int _gen;
    private float _contentH = 1200;
    private readonly Dictionary<int, int> _shelfPage = new();
    private readonly Cards.HoverPrefetch _hover = new();
    private readonly int _scroll = UI.Id("history:yt");

    public string Tag => "history";
    public int MainScroll => _youtube ? _scroll : _local.MainScroll;

    public void Shown()
    {
        if (_youtube && !CookieStore.IsLoggedIn) _youtube = false;
        if (!_youtube) { _local.Shown(); return; }
        // the list changes whenever anything is watched anywhere: a visit after half a minute asks again
        if (_days.Count == 0 || UI.Now - _loadedAt > 30_000) Load();
    }

    public void Hidden() => _local.Hidden();
    public void Refresh() { if (_youtube) Load(); else _local.Refresh(); }

    private void Load()
    {
        var gen = ++_gen;
        _loading = true; _failed = false; _loadingMore = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var (days, next) = await InnerTube.Instance.HistoryAsync(null);
                Progress(days);
                Paths.Log($"history: {days.Count} days, {days.Sum(d => d.Videos.Count(v => !v.IsShort))} videos, {days.Sum(d => d.Videos.Count(v => v.IsShort))} shorts watched as videos, {days.Sum(d => d.Shorts.Count)} shorts in shelves");
                App.Post(() =>
                {
                    if (gen != _gen) return;
                    _days = days; _next = next; _loading = false; _loadedAt = UI.Now;
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("history: " + e.Message);
                App.Post(() => { if (gen != _gen) return; _loading = false; _failed = true; App.Invalidate(); });
            }
        });
    }

    private void LoadMore()
    {
        if (_next == null || _loadingMore || _loading) return;
        _loadingMore = true;
        var gen = _gen;
        var tok = _next;
        _ = Task.Run(async () =>
        {
            try
            {
                var (days, next) = await InnerTube.Instance.HistoryAsync(tok);
                Progress(days);
                App.Post(() =>
                {
                    if (gen != _gen) return;
                    foreach (var d in days)
                    {
                        // a day that continues from the previous page joins it
                        var same = _days.Count > 0 && _days[^1].Title == d.Title ? _days[^1] : null;
                        if (same != null) { same.Videos.AddRange(d.Videos); same.Shorts.AddRange(d.Shorts); }
                        else _days.Add(d);
                    }
                    _next = days.Count == 0 ? null : next;
                    _loadingMore = false;
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log("history more: " + e.Message);
                App.Post(() => { if (gen == _gen) { _loadingMore = false; _next = null; } });
            }
        });
    }

    /// <summary>The red bar under a thumbnail: how far this app got into the video.</summary>
    private static void Progress(List<HistoryDay> days)
    {
        var all = days.SelectMany(d => d.Videos).ToList();
        var p = Db.ProgressFor(all.Select(v => v.VideoId));
        foreach (var v in all) if (p.TryGetValue(v.VideoId, out var x)) v.Progress = x;
    }

    public void Draw(R r)
    {
        // which history: the account's or this app's
        float x0 = r.X + 24, y0 = r.Y + 10;
        Gfx.Draw("История", x0, y0, 24, Th.Text, 600);
        float px = x0 + 130;
        if (CookieStore.IsLoggedIn)
        {
            if (UI.Pill(UI.Id("hist:yt"), px, y0 + 2, 32, null, "YouTube", out var w1, _youtube ? Th.Text : Th.A(0.08f), _youtube ? Th.Bg : Th.Text) && !_youtube)
            {
                _youtube = true; _local.Hidden(); Shown();
            }
            px += w1 + 8;
            if (UI.Pill(UI.Id("hist:local"), px, y0 + 2, 32, null, "FluxTube, подробно", out var w2, !_youtube ? Th.Text : Th.A(0.08f), !_youtube ? Th.Bg : Th.Text) && _youtube)
            {
                _youtube = false; _local.Shown();
            }
            px += w2 + 20;
        }
        if (!_youtube)
        {
            _local.Draw(new R(r.X, r.Y + 44, r.W, r.H - 44));
            return;
        }
        string[] names = ["Все", "Видео", "Shorts"];
        for (int i = 0; i < names.Length; i++)
        {
            if (UI.Pill(UI.Id("hist:f", i), px, y0 + 2, 32, null, names[i], out var fw, _filter == i ? Gfx.Fade(Th.Brand, 0.85f) : Th.A(0.08f), _filter == i ? Th.OnBrand : Th.Text)) _filter = i;
            px += fw + 8;
        }
        var rw = UI.PillWidth(Th.Refresh, "Обновить");
        if (UI.Pill(UI.Id("hist:refresh"), r.Right - rw - 22, y0 + 2, 32, Th.Refresh, "Обновить", out _, Th.A(0.08f))) App.Post(Load);
        if (_loading && _days.Count > 0) UI.Spinner(r.Right - rw - 46, y0 + 18, 8, Th.Sub);

        var view = new R(r.X, r.Y + 54, r.W - 4, r.H - 54);
        if (_days.Count == 0)
        {
            if (_loading) UI.Spinner(view.X + view.W / 2, view.Y + 80, 14, Th.Sub);
            else
            {
                Gfx.Draw(_failed ? "История YouTube не загрузилась." : "В истории YouTube пусто (или её запись приостановлена в аккаунте).", x0, view.Y + 20, 14, Th.Sub);
                if (UI.Pill(UI.Id("hist:retry"), x0, view.Y + 52, 34, Th.Refresh, "Повторить", out _)) App.Post(Load);
            }
            return;
        }

        var scroll = UI.BeginScroll(_scroll, view, _contentH);
        var w = MathF.Min(view.W - 48, 1280);
        float y = view.Y + 6 - scroll;
        for (int di = 0; di < _days.Count; di++)
        {
            var d = _days[di];
            var showShorts = _filter != 1 && d.Shorts.Count > 0;
            // a Short that was opened as an ordinary video is listed among the videos, like on the site
            var showVideos = _filter switch { 1 => d.Videos.Any(v => !v.IsShort), 2 => d.Videos.Any(v => v.IsShort), _ => d.Videos.Count > 0 };
            if (!showShorts && !showVideos) continue;
            Gfx.Draw(d.Title, x0, y, 19, Th.Text, 700);
            y += 40;
            if (showShorts) y = DrawShorts(d, di, x0, y, w, view);
            if (showVideos)
                for (int i = 0; i < d.Videos.Count; i++)
                {
                    const float rowH = 138;
                    if (_filter == 1 && d.Videos[i].IsShort || _filter == 2 && !d.Videos[i].IsShort) continue;
                    if (y + rowH > view.Y - 20 && y < view.Bottom + 20) DrawRow(d.Videos[i], UI.Id(UI.Id("hist:v", di), i), x0, y, w);
                    y += rowH;
                }
            y += 18;
        }
        if (_loadingMore) UI.Spinner(x0 + w / 2, y + 16, 10, Th.Sub);
        if (_next != null && y < view.Bottom + 700) LoadMore();
        _contentH = y + scroll - view.Y + 60;
        UI.EndScroll();
    }

    /// <summary>One watched video, laid out like a row of YouTube's history: picture left, title and channel right.</summary>
    private void DrawRow(VideoItem v, int id, float x, float y, float w)
    {
        const float tw = 218, th = 123;
        var row = new R(x - 8, y - 6, w + 16, th + 12);
        var over = UI.Hover(row);
        var glow = UI.Glow(id, row, 12, over);
        if (v.IsShort)
        {
            // a vertical picture in the middle of the usual frame, with the Shorts mark, as youtube.com draws it
            Gfx.Fill(new R(x, y, tw, th), Th.Thumb, 10);
            var sw = MathF.Round(th * 9 / 16);
            Cards.ThumbUrl(new R(x + (tw - sw) / 2, y, sw, th), $"https://i.ytimg.com/vi/{v.VideoId}/oar2.jpg", 0, Th.Thumb);
            var b = new R(x + tw - 26, y + th - 26, 20, 20);
            Gfx.Fill(b, Th.Badge, 5);
            Gfx.Icon("\uE768", b, 8, 0xFFFFFFFF);
            if (v.Progress > 0.001) UI.Progress(new R(x + 7, y + th - 4, tw - 14, 3), v.Progress);
        }
        else Cards.Thumb(new R(x, y, tw, th), v, false, 10, behind: UI.GlowOn(Th.Bg, glow));
        float tx = x + tw + 16, textW = w - tw - 30;
        var t = Gfx.Draw(v.Title, tx, y + 2, 15.5f, Th.Text, 600, textW, 2);
        Gfx.Draw(v.ChannelMeta, tx, y + 8 + t.H, 12.5f, Th.Sub, 400, textW);
        // the channel name leads to the channel
        var overChannel = false;
        if (over && !string.IsNullOrEmpty(v.ChannelId) && v.Channel.Length > 0)
        {
            var name = Gfx.Text(v.Channel, 12.5f, 400, textW);
            var cr = new R(tx, y + 8 + t.H, name.W, name.H);
            overChannel = UI.Hover(cr);
            if (overChannel)
            {
                Gfx.Fill(new R(cr.X, cr.Bottom - 1.5f, cr.W, 1), Th.Sub);
                if (UI.Click(UI.Id(id, 9), cr)) App.Post(() => Shell.OpenChannel(v.ChannelId!, v.Channel));
            }
        }
        if (over) _hover.Over(v);
        if (UI.MiddleClick(row)) Shell.OpenVideoInNewTab(v);
        else if (overChannel) { }
        else if (UI.Click(id, row)) App.Post(() => Shell.OpenVideo(v, "history"));
        else if (UI.RightClick(row)) Menus.Popup(Cards.MenuFor(v));
    }

    /// <summary>The day's Shorts as one row of tall cards, paged sideways.</summary>
    private float DrawShorts(HistoryDay d, int di, float x0, float y, float w, R view)
    {
        const float gap = 10, cardW = 150, cardH = 267;
        Gfx.Fill(new R(x0, y + 3, 16, 16), Th.Brand, 4);
        Gfx.Icon("", new R(x0, y + 3, 16, 16), 7, Th.OnBrand);
        Gfx.Draw("Shorts", x0 + 24, y, 16, Th.Text, 700);
        var cols = Math.Max(1, (int)((w + gap) / (cardW + gap)));
        var pages = Math.Max(1, (d.Shorts.Count + cols - 1) / cols);
        var page = Math.Clamp(_shelfPage.GetValueOrDefault(di), 0, pages - 1);
        if (pages > 1)
        {
            if (UI.IconButton(UI.Id("hist:prev", di), new R(x0 + w - 80, y - 8, 36, 36), "", 13, null, page > 0)) _shelfPage[di] = page - 1;
            if (UI.IconButton(UI.Id("hist:next", di), new R(x0 + w - 38, y - 8, 36, 36), "", 13, null, page < pages - 1)) _shelfPage[di] = page + 1;
        }
        y += 34;
        var rowH = cardH + 56;
        var clip = new R(x0 - 6, y - 6, w + 12, rowH + 6);
        if (clip.Bottom > view.Y && clip.Y < view.Bottom)
        {
            var shift = UI.Smooth(UI.Id("hist:shift", di), page * (cardW + gap) * cols, 16);
            Gfx.PushClip(clip);
            for (int i = 0; i < d.Shorts.Count; i++)
            {
                var cx = x0 + i * (cardW + gap) - shift;
                if (cx + cardW < x0 - 10 || cx > x0 + w + 10) continue;
                var v = d.Shorts[i];
                var card = new R(cx, y, cardW, rowH - 8);
                var id = UI.Id(UI.Id("hist:s", di), i);
                var glow = UI.Glow(id, card.Inset(-5, -5), 12, UI.Hover(card));
                Cards.ThumbUrl(new R(cx, y, cardW, cardH), $"https://i.ytimg.com/vi/{v.VideoId}/oar2.jpg", 10, UI.GlowOn(Th.Bg, glow));
                var t = Gfx.Draw(v.Title, cx, y + cardH + 6, 12.5f, Th.Text, 600, cardW, 2);
                Gfx.Draw(v.Meta, cx, y + cardH + 8 + t.H, 11.5f, Th.Sub, 400, cardW);
                if (UI.Click(id, card))
                {
                    var list = d.Shorts.ToList();
                    var at = i;
                    App.Post(() => Shell.OpenShorts(list, at));
                }
            }
            Gfx.PopClip();
        }
        y += rowH + 6;
        Gfx.Fill(new R(x0, y, w, 1), Th.Line);
        return y + 16;
    }
}
