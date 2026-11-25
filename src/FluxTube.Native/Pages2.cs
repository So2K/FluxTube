using System.Diagnostics;
using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube.Pages;

/// <summary>
/// Two timelines over local data: what was watched (with position), and every card that was on screen
/// (with where it appeared), so nothing glimpsed before a feed refreshed is lost.
/// </summary>
public sealed class TimelinePage(bool seen, bool embedded = false) : IPage
{
    private sealed record Entry(float Y, float H, string? Header, VideoItem? V, string Time, string Detail, long WatchId, string Source);

    private List<Entry> _entries = [];
    private float _total;
    private Edit? _filter;
    private int _gen;
    private bool _loading;
    private readonly int _scroll = UI.Id(seen ? "tl:seen" : "tl:history");
    private const float RowH = 124, HeadH = 46;

    public string Tag => seen ? "seen" : "history";
    public int MainScroll => _scroll;

    public void Shown()
    {
        if (_filter == null)
        {
            _filter = new Edit(L.T("Искать по названию, каналу, разделу", "Filter by title, channel, or section")) { Bg = () => Th.Card };
            _filter.Changed = () =>
            {
                UI.ScrollReset(_scroll);
                var g = ++_gen;
                App.After(180, () => { if (g == _gen) Reload(); });
            };
        }
        Reload();
    }

    public void Hidden() => _filter?.Hide();
    public void Refresh() => Reload();

    private void Reload()
    {
        var gen = ++_gen;
        var f = _filter?.Text ?? "";
        _loading = true;
        _ = Task.Run(() =>
        {
          try
          {
            var list = new List<Entry>();
            float y = 0;
            string? day = null;
            void Add(DateTime at, VideoItem v, string detail, long watchId, string source)
            {
                var d = Fmt.Day(at);
                if (d != day) { day = d; list.Add(new Entry(y, HeadH, d, null, "", "", 0, "")); y += HeadH; }
                list.Add(new Entry(y, RowH, null, v, at.ToString("HH:mm"), detail, watchId, source));
                y += RowH;
            }
            if (seen)
            {
                var rows = Db.SeenList(f);
                var progress = Db.ProgressFor(rows.Select(r => r.Video.VideoId));
                foreach (var s in rows)
                {
                    if (progress.TryGetValue(s.Video.VideoId, out var p)) s.Video.Progress = p;
                    Add(s.At, s.Video, $"{Fmt.Source(s.Source)} · {s.Video.Meta}".TrimEnd(' ', '·'), 0, s.Source);
                }
            }
            else
                foreach (var h in Db.History(f))
                    Add(h.Last, h.ToVideo(), L.T(
                        $"Остановились на {Fmt.Time(h.Position)}{(h.Duration > 0 ? " из " + Fmt.Time(h.Duration) : "")} · смотрели {Fmt.Time(h.Watched)} · {Fmt.Source(h.Source)}",
                        $"Stopped at {Fmt.Time(h.Position)}{(h.Duration > 0 ? " / " + Fmt.Time(h.Duration) : "")} · watched {Fmt.Time(h.Watched)} · {Fmt.Source(h.Source)}"), h.Id, "history");
            App.Post(() => { if (gen == _gen) { _entries = list; _total = y; _loading = false; } });
          }
          catch (Exception e)
          {
            Paths.Log("timeline: " + e.Message);
            App.Post(() => { if (gen == _gen) _loading = false; });
          }
        });
    }

    public void Draw(R r)
    {
        float x0 = r.X + 24;
        var clearLbl = L.T("Очистить", "Clear");
        var cw = UI.PillWidth(Th.Delete, clearLbl);
        float topY = embedded ? r.Y + 4 : r.Y + 10;
        if (!embedded) Gfx.Draw(seen ? L.T("Что я видел", "Seen") : L.T("История просмотров", "Watch history"), x0, topY, 24, Th.Text, 600);
        if (UI.Pill(UI.Id("clear", _scroll), r.Right - cw - 22, topY + 2, 32, Th.Delete, clearLbl, out _, Th.A(0.08f))) App.Post(Clear);
        var fb = new R(r.Right - cw - 22 - 12 - 320, topY + 1, 320, 34);
        Gfx.Fill(fb, Th.Card, 17);
        _filter?.SetPlaceholder(L.T("Искать по названию, каналу, разделу", "Filter by title, channel, or section"));
        _filter?.Place(fb, 16, 16);
        float descY = embedded ? topY + 8 : r.Y + 50;
        float descW = embedded ? MathF.Max(120, fb.X - x0 - 16) : r.W - 48;
        Gfx.Draw(seen
            ? L.T("Каждое видео, которое появлялось на экране: на главной, в поиске, в похожих. С временем и местом.", "Every video shown on screen across Home, search, and related — with time and source.")
            : L.T("Все запуски видео с позицией остановки. Клик продолжает с того же места.", "Every played video with resume position. Click to continue right where you left off."), x0, descY, 12.5f, Th.Sub, 400, descW);

        float viewTop = embedded ? r.Y + 48 : r.Y + 78;
        var view = new R(x0, viewTop, r.Right - x0 - 4, r.Bottom - viewTop);
        if (_entries.Count == 0)
        {
            if (_loading) UI.Spinner(view.X + 20, view.Y + 30, 10, Th.Sub);
            else Gfx.Draw(L.T("Пока пусто.", "Nothing here yet."), view.X, view.Y + 10, 14, Th.Sub);
            return;
        }
        var scroll = UI.BeginScroll(_scroll, view, _total + 30);
        // binary search for the first visible entry
        int lo = 0, hi = _entries.Count - 1;
        while (lo < hi) { var mid = (lo + hi) / 2; if (_entries[mid].Y + _entries[mid].H < scroll) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < _entries.Count; i++)
        {
            var e = _entries[i];
            float y = view.Y + e.Y - scroll;
            if (y > view.Bottom) break;
            if (e.Header != null) { Gfx.Draw(e.Header, view.X, y + 14, 17, Th.Text, 600); continue; }
            var v = e.V!;
            var row = new R(view.X, y, view.W - 16, RowH - 10);
            var id = UI.Id(_scroll, i);
            var glow = UI.Glow(id, row.Inset(-6, -4), 10, UI.Hover(row));
            Gfx.Draw(e.Time, row.X, y + 4, 13, Th.Sub);
            Cards.Thumb(new R(row.X + 52, y + 1, 200, 112), v, false, 9, behind: UI.GlowOn(Th.Bg, glow));
            float tx = row.X + 52 + 214, w = row.W - 52 - 220;
            var t = Gfx.Draw(v.Title, tx, y + 2, 15, Th.Text, 600, w, 2);
            Gfx.Draw(v.Channel, tx, y + t.H + 7, 13, Th.Sub, 400, w);
            Gfx.Draw(e.Detail, tx, y + t.H + 28, 12.5f, Th.Sub, 400, w, 2);
            if (UI.MiddleClick(row)) Shell.OpenVideoInNewTab(v);
            else if (UI.Click(id, row)) App.Post(() => Shell.OpenVideo(v, e.Source));
            else if (UI.RightClick(row))
                Menus.Popup(Cards.MenuFor(v, seen ? null : () => { Db.DeleteWatch(e.WatchId); Reload(); }));
        }
        UI.EndScroll();
    }

    private void Clear()
    {
        if (Dialogs.Ask(L.T("Это только локальные данные FluxTube. Отменить нельзя.", "This only clears local FluxTube data and cannot be undone."),
            seen ? L.T("Очистить «Что я видел»?", "Clear Seen history?") : L.T("Очистить историю просмотров?", "Clear watch history?")) != 1) return;
        if (seen) Db.ClearSeen(); else Db.ClearHistory();
        Reload();
    }
}

public sealed class DownloadsPage : IPage
{
    private Edit? _url;
    private string _quality = AppSettings.Current.DownloadQuality;
    private readonly int _scroll = UI.Id("downloads");

    public string Tag => "downloads";
    public int MainScroll => _scroll;

    public void Shown()
    {
        if (_url == null)
        {
            _url = new Edit(L.T("Вставьте ссылку на видео (YouTube, VK, Twitch и др.)", "Paste a video link (YouTube, Twitch, VK, etc.)")) { Bg = () => Th.Card };
            _url.Enter = Add;
        }
        foreach (var d in Downloader.Instance.Items) d.Refresh();
    }

    public void Hidden() => _url?.Hide();
    public void Refresh() { }

    private void Add()
    {
        var urls = (_url?.Text ?? "").Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(u => u.StartsWith("http")).ToList();
        foreach (var u in urls) Downloader.Instance.Enqueue(u, videoId: Shell.ExtractYouTubeId(u), quality: _quality);
        if (urls.Count > 0 && _url != null) _url.Text = "";
        App.Invalidate();
    }

    public void Draw(R r)
    {
        float x0 = r.X + 24, right = r.Right - 22;
        Gfx.Draw(L.T("Загрузки", "Downloads"), x0, r.Y + 10, 24, Th.Text, 600);
        var folderLbl = L.T("Папка", "Folder");
        var fw = UI.PillWidth(Th.Folder, folderLbl);
        if (UI.Pill(UI.Id("dl:folder"), right - fw, r.Y + 12, 32, Th.Folder, folderLbl, out _, Th.A(0.08f)))
        {
            try { Directory.CreateDirectory(AppSettings.Current.DownloadDir); W.Open(AppSettings.Current.DownloadDir); }
            catch (Exception e) { Shell.Toast(L.T("Не удалось открыть папку: ", "Could not open folder: ") + e.Message, Th.Warn); }
        }

        float y = r.Y + 58;
        var dlLbl = L.T("Скачать", "Download");
        var addW = UI.PillWidth(Th.Download, dlLbl);
        var ql = Cards.QualityName(_quality);
        var qW = UI.PillWidth(null, ql);
        if (UI.Pill(UI.Id("dl:add"), right - addW, y, 36, Th.Download, dlLbl, out _, accent: true)) App.Post(Add);
        if (UI.Pill(UI.Id("dl:q"), right - addW - 8 - qW, y, 36, null, ql, out _))
            Menus.Popup(AppSettings.Qualities.Select(q => new MenuItem(Cards.QualityName(q), () => _quality = q, null, q == _quality)).ToList());
        var box = new R(x0, y, right - addW - 8 - qW - 10 - x0, 36);
        Gfx.Fill(box, Th.Card, 18);
        _url?.SetPlaceholder(L.T("Вставьте ссылку на видео (YouTube, VK, Twitch и др.)", "Paste a video link (YouTube, Twitch, VK, etc.)"));
        _url?.Place(box, 18, 18);

        var items = Downloader.Instance.Items;
        var view = new R(x0, y + 50, r.Right - x0 - 4, r.Bottom - y - 50);
        if (items.Count == 0) { Gfx.Draw(L.T("Скачанные видео появятся здесь и будут играть без интернета.", "Downloaded videos appear here and play offline."), view.X, view.Y + 8, 14, Th.Sub); return; }
        const float rowH = 116;
        var scroll = UI.BeginScroll(_scroll, view, items.Count * rowH + 20);
        int first = Math.Max(0, (int)(scroll / rowH)), last = Math.Min(items.Count - 1, (int)((scroll + view.H) / rowH));
        for (int i = first; i <= last; i++)
        {
            var d = items[i];
            float ry = view.Y + i * rowH - scroll;
            var row = new R(view.X, ry, view.W - 16, rowH - 10);
            var id = UI.Id(_scroll, (int)d.Id);
            float bx = row.Right - 8;
            var buttons = new R(row.Right - 200, ry, 200, row.H);
            var glow = UI.Glow(id, row.Inset(-6, -4), 10, UI.Hover(row));
            Cards.ThumbUrl(new R(row.X, ry + 3, 176, 99), d.Thumb, 8, UI.GlowOn(Th.Bg, glow));
            float tx = row.X + 190, w = row.W - 190 - 200;
            var t = Gfx.Draw(d.Title, tx, ry + 6, 15, Th.Text, 600, w, 2);
            Gfx.Draw(d.Channel, tx, ry + t.H + 10, 13, Th.Sub, 400, w);
            Gfx.Draw(d.StatusText, tx, ry + t.H + 30, 12.5f, d.Status == "error" ? Th.Warn : Th.Sub, 400, w);
            if (d.IsActive) UI.Progress(new R(tx, ry + t.H + 54, w, 4), d.Progress / 100, Th.A(0.2f));

            bx -= 36; if (UI.IconButton(UI.Id(id, 1), new R(bx, ry + 34, 36, 36), Th.Delete)) App.Post(() => Delete(d));
            if (!d.IsActive && !d.IsDone) { bx -= 40; if (UI.IconButton(UI.Id(id, 2), new R(bx, ry + 34, 36, 36), Th.Refresh)) Downloader.Instance.Retry(d); }
            if (d.IsActive) { bx -= 40; if (UI.IconButton(UI.Id(id, 3), new R(bx, ry + 34, 36, 36), Th.Stop)) Downloader.Instance.Cancel(d); }
            if (d.IsDone)
            {
                bx -= 40; if (UI.IconButton(UI.Id(id, 4), new R(bx, ry + 34, 36, 36), Th.Folder) && d.File != null && File.Exists(d.File)) W.Open("explorer.exe", $"/select,\"{d.File}\"");
                bx -= 40; if (UI.IconButton(UI.Id(id, 5), new R(bx, ry + 34, 36, 36), Th.Play, 16, Th.Brand)) App.Post(() => Play(d));
                if (!UI.Hover(buttons) && UI.Click(id, row)) App.Post(() => Play(d));
            }
        }
        UI.EndScroll();
    }

    private static void Play(DownloadItem d)
    {
        if (d.File == null || !File.Exists(d.File)) { Shell.Toast(L.T("Файл не найден: возможно, его переместили или удалили.", "File not found: it may have been moved or deleted."), Th.Warn); return; }
        if (d.VideoId is { Length: 11 }) Shell.OpenVideo(new VideoItem { VideoId = d.VideoId, Title = d.Title, Channel = d.Channel }, "downloads");
        else { Shell.PlayFile(d); Shell.Toast(L.T("Играет в мини-плеере: ", "Playing in mini-player: ") + d.Title); }
    }

    private static void Delete(DownloadItem d)
    {
        if (!d.IsDone) { Downloader.Instance.Remove(d, deleteFile: false); return; }
        var a = Dialogs.Ask(d.Title + L.T("\n\nДа: удалить файл с диска.\nНет: убрать только из списка.", "\n\nYes: delete file from disk.\nNo: remove from list only."), L.T("Удалить загрузку?", "Delete download?"), threeWay: true);
        if (a < 0) return;
        Downloader.Instance.Remove(d, deleteFile: a == 1);
    }
}

public sealed class SettingsPage : IPage
{
    private Edit? _proxy, _hl, _gl;
    private string _version = "";
    private bool _updating;
    private bool _sfxGuideOpen;
    private bool _dragSfxVol;
    private long _lastSfxVolAt;
    private readonly int _scroll = UI.Id("settings");
    private static readonly (string Key, string Ru, string En)[] Cats =
        [("sponsor", "Спонсоры", "Sponsors"), ("selfpromo", "Самореклама", "Self-promo"), ("interaction", "«Подпишись»", "Interaction"), ("intro", "Заставки", "Intro"), ("outro", "Концовки", "Outro")];
    private static readonly float[] ZoomPresets = [0.75f, 0.85f, 0.90f, 1.00f, 1.10f, 1.25f, 1.40f, 1.50f];
    private static readonly (string Code, string Label)[] AppLangs =
        [("ru", "Русский"), ("en", "English")];
    private static readonly (string Key, string Ru, string En)[] MotionBlurStrengths =
        [("soft", "Лёгкий", "Light"), ("medium", "Средний", "Medium"), ("cinema", "Кино", "Cinema")];
    private static readonly (string Key, string Ru, string En)[] ShortsAdModes =
        [("off", "Выкл", "Off"), ("standard", "Стандарт", "Standard"), ("max", "Максимум", "Max")];
    private static readonly (string Key, string Ru, string En)[] SeasonalEffects =
        [("none", "Выкл", "Off"), ("newyear", "❄ Новый год", "❄ New Year"), ("halloween", "🎃 Хэллоуин", "🎃 Halloween"), ("kawaii", "🌸 Кавай", "🌸 Kawaii"), ("neon", "⚡ Неон", "⚡ Neon")];
    private static readonly (string Key, string Ru, string En)[] FrostedModes =
    [
        ("off", "Чистый", "Solid"),
        ("soft", "Лёгкое стекло", "Soft glass"),
        ("matte", "Матовое стекло", "Matte glass"),
        ("mesh", "Градиент", "Mesh aura"),
        ("kawaii", "Кавай-пастель", "Kawaii pastel"),
        ("neon", "Неон-аура", "Neon aura"),
        ("noir", "Нуар / Хоррор", "Noir / Horror"),
        ("orbital", "Орбита (Космос)", "Orbital Space"),
    ];
    private static readonly (string Key, string Ru, string En)[] GlassShaders =
    [
        ("acrylic", "Матовое стекло (Stencil)", "Frosted Glass (Stencil)"),
        ("liquid", "Жидкое стекло", "Liquid Refraction"),
        ("toon", "Тун-шейдер (Аниме)", "Toon Cel + Ink"),
        ("poster", "Постеризация", "Posterize Pop"),
        ("sketch", "Рисованный (Штрих)", "Hand-Drawn Sketch"),
        ("off", "Выкл (Классика)", "Off (Classic)"),
    ];
    private static readonly (string Key, string Ru, string En)[] CornerStyles =
        [("sharp", "Острые (0px)", "Sharp (0px)"), ("normal", "Обычные", "Normal"), ("round", "Мягкие", "Rounded")];
    private static readonly (string Key, string Ru, string En)[] CardStyles =
        [("clean", "Чистый", "Clean"), ("framed", "Рамка (RE / Alien)", "Framed (RE / Alien)"), ("dashed", "Пунктир (Кавай)", "Dashed (Kawaii)"), ("brutal", "Брутализм (Ч/Б)", "Brutalist"), ("neon", "Неон-контур", "Neon Rim")];
    private static readonly (string Key, string Ru, string En)[] FontPresets =
        [("segoe", "Segoe UI", "Segoe UI"), ("serif", "Georgia (Requiem)", "Georgia (Serif)"), ("tech", "Bahnschrift (Village)", "Bahnschrift (Tech)"), ("mono", "Consolas (Neon)", "Consolas (Mono)"), ("soft", "Trebuchet (Kawaii)", "Trebuchet (Soft)"), ("comic", "Comic Sans", "Comic Sans")];
    private static readonly (string Code, string Ru, string En)[] AudioLangs =
    [
        ("", "Оригинал (как на YouTube)", "Original (YouTube default)"),
        ("ru", "Русский", "Русский"), ("en", "English", "English"), ("uk", "Українська", "Українська"),
        ("de", "Deutsch", "Deutsch"), ("es", "Español", "Español"), ("fr", "Français", "Français"), ("ja", "日本語", "日本語"),
    ];
    private static readonly int[] CacheLimits = [0, 512, 1024, 2048, 5120, 10240];

    public string Tag => "settings";
    public int MainScroll => _scroll;

    public void Shown()
    {
        var s = AppSettings.Current;
        if (_proxy == null)
        {
            _proxy = new Edit(L.T("пусто = системный прокси; пример: socks5://127.0.0.1:1080", "empty = system proxy; e.g. socks5://127.0.0.1:1080"));
            _hl = new Edit("ru");
            _gl = new Edit(L.T("по IP", "by IP"));
            _proxy.Changed = () => { s.Proxy = _proxy.Text.Trim(); Saved(); };
            _hl.Changed = () => { s.Hl = string.IsNullOrWhiteSpace(_hl.Text) ? "ru" : _hl.Text.Trim(); Saved(); InnerTube.Instance.ResetSession(); };
            _gl.Changed = () => { s.Gl = _gl.Text.Trim().ToUpperInvariant(); Saved(); InnerTube.Instance.ResetSession(); };
        }
        if (_proxy.Text != s.Proxy) _proxy.Text = s.Proxy;
        if (_hl != null && _hl.Text != s.Hl) _hl.Text = s.Hl;
        if (_gl != null && _gl.Text != s.Gl) _gl.Text = s.Gl;
        _ = ShowVersionAsync();
    }

    public void Hidden() { _proxy?.Hide(); _hl?.Hide(); _gl?.Hide(); }
    public void Refresh() { }

    private static void Saved()
    {
        AppSettings.Current.Save();
        if (MpvPlayer.Instance.IsReady) MpvPlayer.Instance.ApplySettings();
    }

    private async Task ShowVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(Paths.YtDlp, "--version") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var v = (await p.StandardOutput.ReadToEndAsync()).Trim();
            App.Post(() => _version = "yt-dlp " + v);
        }
        catch (Exception e) { App.Post(() => _version = "yt-dlp: " + e.Message); }
    }

    private long _cacheAt, _cacheBytes, _statsAt;
    private string _stats = "";
    private static string CacheName(int mb) => mb switch { 0 => L.T("Выключен", "Off"), < 1024 => mb + L.T(" МБ", " MB"), _ => (mb / 1024) + L.T(" ГБ", " GB") };

    // layout state for one Draw
    private float _x, _y, _w, _contentH = 2400;
    private R _view;

    private bool Visible(float y, float h) => y + h >= _view.Y - 24 && y <= _view.Bottom + 24;

    private void Section(string title)
    {
        _y += 18;
        if (Visible(_y, 30)) Gfx.Draw(title, _x, _y, 16, Th.Text, 600);
        _y += 30;
    }

    private R Row(string label, string? hint = null, float h = 50, float reserveRight = 250)
    {
        var r = new R(_x, _y, _w, h);
        if (Visible(_y, h))
        {
            Gfx.FillFrosted(r, Th.Card, 10, opacity: 0.46f, border: true);
            float maxTextW = MathF.Max(120, _w - reserveRight);
            var t = Gfx.Draw(label, _x + 16, _y + (hint == null ? (h - 19) / 2 : 8), 14, Th.Text, 400, maxTextW);
            if (hint != null) Gfx.Draw(hint, _x + 16, _y + 8 + t.H + 1, 12, Th.Sub, 400, maxTextW);
        }
        _y += h + 6;
        return r;
    }

    private void ToggleRow(string key, string label, bool value, Action<bool> set, string? hint = null)
    {
        var r = Row(label, hint, hint == null ? 50 : 58, 90);
        if (Visible(r.Y, r.H) && UI.Toggle(UI.Id("set:" + key), new R(r.X, r.Y, r.W - 16, r.H), value)) { set(!value); Saved(); }
    }

    /// <summary>Wrapping pill selector inside a frosted settings card — never crowds or clips at any zoom/language.</summary>
    private void PillGridRow(string key, string title, string hint, (string Key, string Ru, string En)[] items, string activeKey, Action<string> onPick)
    {
        float pad = 16, maxW = _w - pad * 2, pillH = 30, gap = 7;
        // Measure total wrapped height first
        float cx = 0; int rows = 1;
        for (int i = 0; i < items.Length; i++)
        {
            float pw = UI.PillWidth(null, L.T(items[i].Ru, items[i].En)) - 4;
            if (cx > 0 && cx + pw > maxW) { cx = 0; rows++; }
            cx += pw + gap;
        }
        float h = 50 + rows * (pillH + gap) + 6;
        var r = new R(_x, _y, _w, h);
        if (Visible(_y, h))
        {
            Gfx.FillFrosted(r, Th.Card, 10, opacity: 0.46f, border: true);
            var t = Gfx.Draw(title, _x + pad, _y + 9, 14, Th.Text, 500, maxW);
            Gfx.Draw(hint, _x + pad, _y + 9 + t.H + 1, 12, Th.Sub, 400, maxW);
            float px = _x + pad, py = _y + 48;
            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                var lbl = L.T(it.Ru, it.En);
                float pw = UI.PillWidth(null, lbl) - 4;
                if (px > _x + pad && px + pw > _x + _w - pad) { px = _x + pad; py += pillH + gap; }
                bool on = activeKey == it.Key;
                var br = new R(px, py, pw, pillH);
                Gfx.Fill(br, on ? Th.Brand : Th.A(0.10f), Gfx.Corner(pillH / 2));
                if (on && AppSettings.Current.UiNeonGlow) Gfx.NeonGlow(br, Gfx.Corner(pillH / 2), Th.Brand, 0.65f);
                if (!on) UI.Glow(UI.Id("set:" + key + ":" + it.Key), br, Gfx.Corner(pillH / 2), UI.Hover(br));
                var tr = Gfx.Text(lbl, 12.5f, on ? 600 : 500);
                Gfx.Draw(tr, br.X + (br.W - tr.W) / 2, Gfx.Snap(br.Y + (br.H - tr.H) / 2), on ? Th.OnBrand : Th.Text);
                if (UI.Click(UI.Id("set:" + key + ":" + it.Key), br))
                {
                    var picked = it.Key;
                    onPick(picked);
                    Saved();
                    App.Invalidate();
                }
                px += pw + gap;
            }
        }
        _y += h + 6;
    }

    private void ChoiceRow(string key, string label, string current, IEnumerable<string> options, Action<string> set)
    {
        var r = Row(label, null, 50, 220);
        if (!Visible(r.Y, r.H)) return;
        var text = Cards.QualityName(current);
        var w = UI.PillWidth(null, text);
        if (UI.Pill(UI.Id("set:" + key), r.Right - w - 14, r.Y + 8, 34, null, text, out _))
            Menus.Popup(options.Select(o => new MenuItem(Cards.QualityName(o), () => { set(o); Saved(); }, null, o == current)).ToList());
    }

    private void EditInRow(Edit? e, R row, float width)
    {
        if (e == null || !Visible(row.Y, row.H)) return;
        var box = new R(row.Right - width - 14, row.Y + 8, width, row.H - 16);
        Gfx.Fill(box, Th.Field, 8);
        if (e.Focused) Gfx.Stroke(box, Gfx.Fade(Th.Brand, 0.8f), 8, 1.2f);
        e.Place(box, 12, 12);
    }

    public void Draw(R r)
    {
        var s = AppSettings.Current;
        _proxy?.SetPlaceholder(L.T("пусто = системный прокси; пример: socks5://127.0.0.1:1080", "empty = system proxy; e.g. socks5://127.0.0.1:1080"));
        _gl?.SetPlaceholder(L.T("по IP", "by IP"));

        _view = new R(r.X, r.Y, r.W - 4, r.H);
        var scroll = UI.BeginScroll(_scroll, _view, _contentH);
        _x = r.X + 24; _w = MathF.Min(880, r.W - 60); _y = r.Y + 10 - scroll;
        if (Visible(_y, 34)) Gfx.Draw(L.T("Настройки", "Settings"), _x, _y, 24, Th.Text, 600);
        _y += 34;

        Section(L.T("Аккаунты YouTube", "YouTube Accounts"));
        var logged = CookieStore.IsLoggedIn;
        var curAcc = AccountMenu.Current;
        var accTitle = logged
            ? (curAcc is { Name.Length: > 0 } ? L.T($"Активный аккаунт: {curAcc.Name}", $"Active account: {curAcc.Name}") : L.T("Вы вошли в YouTube", "Signed in to YouTube"))
            : L.T("Вы не вошли в аккаунт", "Not signed in");
        var addLabel = logged ? L.T("Добавить аккаунт", "Add account") : L.T("Войти в YouTube", "Sign in to YouTube");
        var outLabel = L.T("Выйти", "Sign out");
        var accsLabel = L.T($"Аккаунты ({AccountMenu.Saved.Count})", $"Accounts ({AccountMenu.Saved.Count})");
        float accReserve = 36 + UI.PillWidth(logged ? Th.Add : Th.Account, addLabel)
            + (logged ? UI.PillWidth(null, outLabel) + 8 : 0)
            + (AccountMenu.Saved.Count > 0 ? UI.PillWidth(Th.Account, accsLabel) + 8 : 0);
        var acc = Row(accTitle,
            logged ? L.T("Простой вход в 1 клик через изолированное окно браузера, Firefox, буфер обмена или cookies.txt.", "Easy 1-click sign-in via isolated browser window, Firefox, clipboard, or cookies.txt.")
                   : L.T("Войдите в Google в 1 клик — подписки, история и рекомендации подтянутся автоматически.", "Sign in to Google in 1 click — subscriptions, history, and recommendations sync automatically."), 58, accReserve);
        if (Visible(acc.Y, acc.H))
        {
            float bx = acc.Right - 14;
            if (logged)
            {
                var lw = UI.PillWidth(null, outLabel); bx -= lw;
                if (UI.Pill(UI.Id("set:logout"), bx, acc.Y + 12, 34, null, outLabel, out _)) App.Post(Shell.Logout);
                bx -= 8;
            }
            var aw = UI.PillWidth(logged ? Th.Add : Th.Account, addLabel); bx -= aw;
            if (UI.Pill(UI.Id("set:login"), bx, acc.Y + 12, 34, logged ? Th.Add : Th.Account, addLabel, out _, accent: true))
                App.Post(LoginModal.Show);
            if (AccountMenu.Saved.Count > 0)
            {
                var swW = UI.PillWidth(Th.Account, accsLabel); bx -= swW + 8;
                if (UI.Pill(UI.Id("set:accs"), bx, acc.Y + 12, 34, Th.Account, accsLabel, out _))
                {
                    var list = AccountMenu.Saved.Select(a => new MenuItem(
                        (a.Name.Length > 0 ? a.Name : a.Handle) + (a.Handle.Length > 0 && a.Name.Length > 0 ? $" ({a.Handle})" : ""),
                        () => AccountMenu.SwitchTo(a.Id),
                        check: logged && a.Id == AccountMenu.ActiveId,
                        icon: Th.Account)).ToList();
                    list.Add(MenuItem.Sep);
                    list.Add(new(L.T("Войти / добавить аккаунт…", "Sign in / add account…"), LoginModal.Show, icon: Th.Add));
                    Menus.Popup(list);
                }
            }
        }
        var imp = Row(L.T("Быстрый импорт сессии", "Quick session import"),
            L.T("Импорт из Firefox (1 клик), вставка cookies из буфера обмена или файл cookies.txt.", "1-click Firefox import, paste cookies from clipboard, or load cookies.txt."), 54, 410);
        if (Visible(imp.Y, imp.H))
        {
            float bx = imp.Right - 14;
            var ckLbl = "cookies.txt";
            var w2 = UI.PillWidth(Th.Folder, ckLbl); bx -= w2;
            if (UI.Pill(UI.Id("set:ck"), bx, imp.Y + 10, 34, Th.Folder, ckLbl, out _))
                App.Post(() => Dialogs.PickFileAsync("cookies.txt / JSON\0*.txt;*.json\0\0", BrowserLogin.ImportFromFile));
            bx -= 8;
            var clipLbl = L.T("Из буфера", "Clipboard");
            var wClip = UI.PillWidth(Th.Copy, clipLbl); bx -= wClip;
            if (UI.Pill(UI.Id("set:ckclip"), bx, imp.Y + 10, 34, Th.Copy, clipLbl, out _))
                App.Post(BrowserLogin.ImportFromClipboard);
            if (BrowserLogin.HasFirefox)
            {
                bx -= 8;
                var ffLbl = "Firefox";
                var wFf = UI.PillWidth(Th.Globe, ffLbl); bx -= wFf;
                if (UI.Pill(UI.Id("set:ckff"), bx, imp.Y + 10, 34, Th.Globe, ffLbl, out _))
                    App.Post(BrowserLogin.ImportFromFirefox);
            }
        }

        Section(L.T("Оформление и язык", "Appearance & Language"));
        var langRow = Row(L.T("Язык интерфейса", "App language"),
            L.T("Меняет язык всех меню, настроек и кнопок FluxTube мгновенно без перезапуска", "Switches all FluxTube menus, settings, and labels instantly without restart"), 58, 240);
        if (Visible(langRow.Y, langRow.H))
        {
            float lx = langRow.Right - 14;
            for (int li = AppLangs.Length - 1; li >= 0; li--)
            {
                var (lc, ll) = AppLangs[li];
                var lw = UI.PillWidth(null, ll); lx -= lw;
                bool on = L.Code == lc;
                if (UI.Pill(UI.Id("set:uilang:" + lc), lx, langRow.Y + 12, 34, null, ll, out _, accent: on) && !on)
                {
                    s.UiLanguage = lc;
                    Saved();
                    Shell.OnLanguageChanged();
                }
                lx -= 8;
            }
        }

        _y += ThemePicker.Draw(_x, _y, _w);

        var customThemeRow = Row(L.T("Конструктор и мутация тем", "Theme studio & mutator"),
            L.T("Мутируйте в 1 клик, сохраните текущий вид как тему или правьте код (ПКМ по теме)", "Mutate in 1 click, save your current look as a theme, or edit code (right-click theme)"), 58, 415);
        if (Visible(customThemeRow.Y, customThemeRow.H))
        {
            float tx = customThemeRow.Right - 14;
            var tdLbl = L.T("Папка", "Folder");
            var tdW = UI.PillWidth(Th.Folder, tdLbl); tx -= tdW;
            if (UI.Pill(UI.Id("set:tdir"), tx, customThemeRow.Y + 12, 34, Th.Folder, tdLbl, out _))
                W.Open(Themes.CustomDir);
            tx -= 8;
            var teLbl = L.T("Код / сохранить вид…", "Code / save look…");
            var teW = UI.PillWidth(Th.Add, teLbl); tx -= teW;
            if (UI.Pill(UI.Id("set:tedit"), tx, customThemeRow.Y + 12, 34, Th.Add, teLbl, out _))
                App.Post(() => ThemeCodeEditor.Show(Themes.CaptureCurrentLook()));
            tx -= 8;
            var mutLbl = L.T("Мутировать", "Mutate");
            var mutW = UI.PillWidth(Th.Refresh, mutLbl); tx -= mutW;
            if (UI.Pill(UI.Id("set:tmut"), tx, customThemeRow.Y + 12, 34, Th.Refresh, mutLbl, out _, accent: true))
            {
                Themes.MutateCurrent();
                Shell.Toast(L.T("Тема мутирована! Нажмите «Код / сохранить вид…», чтобы сохранить её.", "Theme mutated! Click \"Code / save look…\" to save it."), Th.Good, 2800);
            }
        }

        ToggleRow("tautostyle", L.T("Авто-стиль при выборе темы", "Auto-apply theme style preset"), s.ThemeAutoStyle, v => s.ThemeAutoStyle = v,
            L.T("Включено: темы вроде Ультра Кавай, MiSide, RE Requiem и Неон сами включают свой фон, узор, углы и шрифт", "On: themes like Ultra Kawaii, MiSide, RE Requiem & Neon automatically set their backdrop, pattern, corners & font"));

        PillGridRow("frost", L.T("Стиль фона и стекла", "Backdrop & glass style"),
            L.T("Запекается на GPU один раз (~0.04 мс/кадр). Комбинируется с обоями и узорами.", "Pre-baked once on GPU (~0.04 ms/frame). Stacks freely with wallpapers & patterns."),
            FrostedModes, s.FrostedGlass, v => s.FrostedGlass = v);

        PillGridRow("gshader", L.T("Шейдер стекла и графики (GPU)", "Glass & graphics shader (GPU)"),
            L.T("Живой Stencil-блюр под панелями, преломление жидкого стекла, тун-шейдер с контуром, постеризация или штриховка (~0.15 мс GPU)", "Live stencil-masked panel blur, liquid glass refraction, anime toon cel+ink, posterize, or hand-drawn sketch (~0.15 ms GPU)"),
            GlassShaders, s.UiGlassShader, v => s.UiGlassShader = v);

        PillGridRow("bgpat", L.T("Узор (паттерн) на фон", "Background pattern"),
            L.T("Бесшовный векторный GPU-тайл (~0.02 мс/кадр, 0% CPU). Работает на стекле, обоях и чистом фоне.", "Seamless vector GPU tile (~0.02 ms/frame, 0% CPU). Works on glass, wallpapers, and solid backgrounds."),
            BgEngine.Patterns, s.BgPattern, v => s.BgPattern = v);

        if (!string.IsNullOrEmpty(s.BgPattern) && s.BgPattern != "none")
        {
            var patCfgRow = Row(L.T("Настройки узора", "Pattern tuning"),
                L.T("Прозрачность, размер тайла и расцветка узора (двойной неон использует 2 акцента темы)", "Opacity, tile size, and pattern coloring (Dual Neon alternates both theme accents)"), 58, 520);
            if (Visible(patCfgRow.Y, patCfgRow.H))
            {
                float px = patCfgRow.Right - 14;
                // Tint pills
                for (int ti = BgEngine.PatternTints.Length - 1; ti >= 0; ti--)
                {
                    var (tk, tru, ten) = BgEngine.PatternTints[ti];
                    var tl = L.T(tru, ten);
                    var tw = UI.PillWidth(null, tl); px -= tw;
                    bool on = s.BgPatternTint == tk;
                    if (UI.Pill(UI.Id("set:ptint:" + tk), px, patCfgRow.Y + 13, 32, null, tl, out _, accent: on))
                    { s.BgPatternTint = tk; Saved(); App.Invalidate(); }
                    px -= 6;
                }
                px -= 6;
                // Scale pills S/M/L
                for (int si = BgEngine.PatternScales.Length - 1; si >= 0; si--)
                {
                    var (sc, slbl) = BgEngine.PatternScales[si];
                    float swBtn = 32; px -= swBtn;
                    bool on = MathF.Abs(s.BgPatternScale - sc) < 0.12f;
                    var sr = new R(px, patCfgRow.Y + 13, swBtn, 32);
                    Gfx.Fill(sr, on ? Th.Brand : Th.A(0.10f), Gfx.Corner(16));
                    if (!on) UI.Glow(UI.Id("set:psc:" + si), sr, Gfx.Corner(16), UI.Hover(sr));
                    var str = Gfx.Text(slbl, 12, 600);
                    Gfx.Draw(str, sr.X + (sr.W - str.W) / 2, sr.Y + (sr.H - str.H) / 2, on ? Th.OnBrand : Th.Text);
                    if (UI.Click(UI.Id("set:psc:" + si), sr)) { s.BgPatternScale = sc; Saved(); App.Invalidate(); }
                    px -= 5;
                }
                px -= 6;
                // Opacity stepper (- % +)
                var opPlus = new R(px - 30, patCfgRow.Y + 14, 30, 30);
                Gfx.Fill(opPlus, Th.A(0.10f), 15);
                if (UI.IconButton(UI.Id("set:pop+"), opPlus, Th.Add, 11)) { s.BgPatternOpacity = Math.Clamp(s.BgPatternOpacity + 0.04f, 0.04f, 0.55f); Saved(); App.Invalidate(); }
                px -= 36;
                var opTxt = Gfx.Text($"{(int)MathF.Round(s.BgPatternOpacity * 100)}%", 12.5f, 600);
                Gfx.Draw(opTxt, px - opTxt.W, patCfgRow.Y + (patCfgRow.H - opTxt.H) / 2, Th.Text);
                px -= opTxt.W + 8;
                var opMinus = new R(px - 30, patCfgRow.Y + 14, 30, 30);
                Gfx.Fill(opMinus, Th.A(0.10f), 15);
                if (UI.IconButton(UI.Id("set:pop-"), opMinus, "\uE738", 11)) { s.BgPatternOpacity = Math.Clamp(s.BgPatternOpacity - 0.04f, 0.04f, 0.55f); Saved(); App.Invalidate(); }
            }
        }

        var hasWall = !string.IsNullOrWhiteSpace(s.BgImagePath) && File.Exists(s.BgImagePath);
        var wallHint = hasWall
            ? L.T($"Файл: {Path.GetFileName(s.BgImagePath)} · Загружается в GPU 1 раз (~0.04 мс/кадр)", $"File: {Path.GetFileName(s.BgImagePath)} · Cached on GPU once (~0.04 ms/frame)")
            : L.T("Поставьте любое фото (PNG/JPG/WEBP) на фон вместо стекла или вместе со стеклом и узорами", "Use any image (PNG/JPG/WEBP) as wallpaper instead of glass or layered with glass & patterns");
        var wallRow = Row(L.T("Своя картинка на фон (обои)", "Custom background image (wallpaper)"), wallHint, 58, 300);
        if (Visible(wallRow.Y, wallRow.H))
        {
            float wx = wallRow.Right - 14;
            if (hasWall)
            {
                var clrLbl = L.T("Убрать", "Clear");
                var clrW = UI.PillWidth(Th.Close, clrLbl); wx -= clrW;
                if (UI.Pill(UI.Id("set:wallclr"), wx, wallRow.Y + 12, 34, Th.Close, clrLbl, out _))
                { s.BgImagePath = ""; BgEngine.InvalidateWallpaper(); Saved(); }
                wx -= 8;
            }
            var pickLbl = L.T("Выбрать фото…", "Choose image…");
            var pickW = UI.PillWidth(Th.Folder, pickLbl); wx -= pickW;
            if (UI.Pill(UI.Id("set:wallpick"), wx, wallRow.Y + 12, 34, Th.Folder, pickLbl, out _, accent: !hasWall))
            {
                string imgFilter = L.T("Картинки (PNG, JPG, WEBP, BMP)\0*.png;*.jpg;*.jpeg;*.webp;*.bmp\0Все файлы\0*.*\0\0",
                    "Images (PNG, JPG, WEBP, BMP)\0*.png;*.jpg;*.jpeg;*.webp;*.bmp\0All files\0*.*\0\0");
                App.Post(() => Dialogs.PickFileAsync(imgFilter, file =>
                {
                    s.BgImagePath = file;
                    BgEngine.InvalidateWallpaper();
                    Saved();
                    Shell.Toast(L.T("Обои применены!", "Wallpaper applied!"), Th.Good, 2000);
                }));
            }
        }
        if (hasWall)
        {
            var wallCfg = Row(L.T("Яркость и размытие обоев", "Wallpaper opacity & blur"),
                L.T("Размытие запекается на GPU один раз — 0% нагрузки при прокрутке", "Blur is pre-baked once on GPU — zero cost during scrolling"), 56, 460);
            if (Visible(wallCfg.Y, wallCfg.H))
            {
                float wx = wallCfg.Right - 14;
                for (int bi = BgEngine.WallpaperBlurs.Length - 1; bi >= 0; bi--)
                {
                    var (bv, bru, ben) = BgEngine.WallpaperBlurs[bi];
                    var bl = L.T(bru, ben);
                    var bw = UI.PillWidth(null, bl); wx -= bw;
                    bool on = MathF.Abs(s.BgImageBlur - bv) < 2f;
                    if (UI.Pill(UI.Id("set:wblur:" + bi), wx, wallCfg.Y + 12, 32, null, bl, out _, accent: on))
                    { s.BgImageBlur = bv; Saved(); App.Invalidate(); }
                    wx -= 6;
                }
                wx -= 8;
                var wpPlus = new R(wx - 30, wallCfg.Y + 13, 30, 30);
                Gfx.Fill(wpPlus, Th.A(0.10f), 15);
                if (UI.IconButton(UI.Id("set:wop+"), wpPlus, Th.Add, 11)) { s.BgImageOpacity = Math.Clamp(s.BgImageOpacity + 0.08f, 0.10f, 0.95f); Saved(); App.Invalidate(); }
                wx -= 36;
                var wopTxt = Gfx.Text($"{(int)MathF.Round(s.BgImageOpacity * 100)}%", 12.5f, 600);
                Gfx.Draw(wopTxt, wx - wopTxt.W, wallCfg.Y + (wallCfg.H - wopTxt.H) / 2, Th.Text);
                wx -= wopTxt.W + 8;
                var wpMinus = new R(wx - 30, wallCfg.Y + 13, 30, 30);
                Gfx.Fill(wpMinus, Th.A(0.10f), 15);
                if (UI.IconButton(UI.Id("set:wop-"), wpMinus, "\uE738", 11)) { s.BgImageOpacity = Math.Clamp(s.BgImageOpacity - 0.08f, 0.10f, 0.95f); Saved(); App.Invalidate(); }
            }
        }

        PillGridRow("corners", L.T("Форма углов интерфейса", "UI corner geometry"),
            L.T("Острые 0px (RE / Alien / Монохром), стандартные или мягкие скруглённые (Кавай / MiSide)", "Sharp 0px (RE / Alien / Swiss), standard, or bubbly rounded (Kawaii / MiSide)"),
            CornerStyles, s.UiCornerStyle, v => s.UiCornerStyle = v);

        PillGridRow("cards", L.T("Стиль рамок и карточек", "Card & panel frame style"),
            L.T("Пунктирные рамки как в Kawaii, строгие рамки RE/Alien, ч/б брутализм или неоновый контур", "Kawaii dashed borders, crisp RE/Alien frames, Swiss brutalist shadows, or neon rim"),
            CardStyles, s.UiCardStyle, v => s.UiCardStyle = v);

        PillGridRow("font", L.T("Шрифт интерфейса", "UI typography font"),
            L.T("Мгновенно меняет шрифт всего приложения без перезапуска", "Switches typography across the entire app instantly without restart"),
            FontPresets, s.UiFont, v => { s.UiFont = v; Gfx.ClearTextCache(); });

        ToggleRow("neonglow", L.T("Неоновое свечение интерфейса (Neon Glow)", "UI Neon Glow"), s.UiNeonGlow, v => s.UiNeonGlow = v,
            L.T("Дёшевый GPU-ореол вокруг активных кнопок, поиска и индикаторов (~0.01 мс/кадр)", "Ultra-cheap GPU neon halo around active pills, search bar & indicators (~0.01 ms/frame)"));

        ToggleRow("mblur", L.T("Motion Blur интерфейса (GPU)", "UI Motion Blur (GPU)"), s.UiMotionBlur, v => s.UiMotionBlur = v,
            L.T("Аппаратное направленное размытие в движении при скролле, переходах страниц и свайпе Shorts", "Hardware-accelerated directional motion blur on scrolling, page transitions, and Shorts swipes"));
        if (s.UiMotionBlur)
        {
            var mbRow = Row(L.T("Сила Motion Blur", "Motion Blur strength"),
                L.T("Регулирует интенсивность размытия при быстром движении интерфейса", "Controls blur intensity during fast UI motion"), 54, 340);
            if (Visible(mbRow.Y, mbRow.H))
            {
                float mx = mbRow.Right - 14;
                for (int mi = MotionBlurStrengths.Length - 1; mi >= 0; mi--)
                {
                    var (mk, mru, men) = MotionBlurStrengths[mi];
                    var ml = L.T(mru, men);
                    var mw = UI.PillWidth(null, ml); mx -= mw;
                    bool on = s.UiMotionBlurStrength == mk;
                    if (UI.Pill(UI.Id("set:mbs:" + mk), mx, mbRow.Y + 10, 34, null, ml, out _, accent: on))
                    {
                        s.UiMotionBlurStrength = mk;
                        Saved();
                        App.Invalidate();
                    }
                    mx -= 8;
                }
            }
        }

        var zr = Row(L.T("Масштаб интерфейса", "Interface zoom"),
            L.T("Как в браузере: Ctrl + колёсико мыши, Ctrl + / − или Ctrl + 0 для сброса", "Like a browser: Ctrl + wheel, Ctrl + / −, or Ctrl + 0 to reset"), 58, 220);
        if (Visible(zr.Y, zr.H))
        {
            float bx = zr.Right - 14;
            var zp = new R(bx - 34, zr.Y + 12, 34, 34);
            Gfx.Fill(zp, Th.A(0.1f), 17);
            if (UI.IconButton(UI.Id("set:zplus"), zp, Th.Add, 12)) Shell.AdjustZoom(0.10f);
            bx -= 40;
            var zLabel = $"{(int)MathF.Round(s.UiScale * 100)}%";
            var zw = UI.PillWidth(null, zLabel);
            bx -= zw;
            if (UI.Pill(UI.Id("set:zval"), bx, zr.Y + 12, 34, null, zLabel, out _))
                Menus.Popup(ZoomPresets.Select(p => new MenuItem($"{(int)MathF.Round(p * 100)}%", () => Shell.SetZoom(p), check: MathF.Abs(s.UiScale - p) < 0.02f)).ToList());
            bx -= 40;
            var zm = new R(bx, zr.Y + 12, 34, 34);
            Gfx.Fill(zm, Th.A(0.1f), 17);
            if (UI.IconButton(UI.Id("set:zminus"), zm, "\uE738", 12)) Shell.AdjustZoom(-0.10f);
        }
        ToggleRow("side", L.T("Скрыть боковую панель", "Collapse sidebar"), s.SidebarCollapsed, v => s.SidebarCollapsed = v, L.T("Также переключается кнопкой меню сверху слева или Ctrl+B", "Also toggled by top-left menu button or Ctrl+B"));
        ToggleRow("ambient", L.T("Подсветка видео (Ambient Mode)", "Video glow (Ambient Mode)"), s.AmbientMode, v => s.AmbientMode = v, L.T("Мягкое GPU-свечение вокруг плеера в цветах текущей сцены (обычные видео и Shorts)", "Soft GPU glow around the player matching the current scene (videos & Shorts)"));
        ToggleRow("sfx", L.T("Звуки интерфейса (Silent Hill / MGS)", "UI Sound Effects (Silent Hill / MGS)"), s.UiSounds, v => { s.UiSounds = v; if (v) Sfx.Tab(); }, L.T("Отдельные тактильные звуки на клик, открытие видео, лайк, канал, громкость, вкладки и тумблеры", "Tactile sound effects for clicks, opening videos, likes, channels, volume, tabs, and toggles"));

        if (!UI.Down && _dragSfxVol)
        {
            _dragSfxVol = false;
            Sfx.RebuildBuffers();
            Sfx.Preview("click");
            Saved();
        }
        var sfxVolRow = Row(L.T("Громкость звуков интерфейса", "UI sounds volume"),
            L.T("Увеличьте (до 300%) или убавьте громкость стандартных и своих звуков", "Boost (up to 300%) or lower built-in and custom UI sound volume"), 58, 300);
        if (Visible(sfxVolRow.Y, sfxVolRow.H))
        {
            int sv = Math.Clamp(s.UiSoundsVolume, 10, 300);
            float bx = sfxVolRow.Right - 14;
            var plusR = new R(bx - 30, sfxVolRow.Y + 14, 30, 30);
            Gfx.Fill(plusR, Th.A(0.1f), 15);
            if (UI.IconButton(UI.Id("set:svplus"), plusR, Th.Add, 11))
            {
                s.UiSoundsVolume = Math.Clamp(sv + 15, 10, 300);
                Sfx.RebuildBuffers(); Sfx.Preview("click"); Saved();
            }
            bx -= 36;
            var svTxt = Gfx.Text($"{sv}%", 12.5f, 600);
            Gfx.Draw(svTxt, bx - svTxt.W, sfxVolRow.Y + (sfxVolRow.H - svTxt.H) / 2, Th.Text);
            bx -= 48;
            float slW = 136, slX = bx - slW;
            var slHit = new R(slX - 4, sfxVolRow.Y + 12, slW + 8, 34);
            var slHot = UI.Hover(slHit) || _dragSfxVol;
            if (UI.Pressed && UI.Hover(slHit)) _dragSfxVol = true;
            if (_dragSfxVol)
            {
                int nv = (int)MathF.Round((10 + Math.Clamp((UI.MX - slX) / slW, 0, 1) * 290) / 5f) * 5;
                if (nv != s.UiSoundsVolume)
                {
                    s.UiSoundsVolume = Math.Clamp(nv, 10, 300);
                    if (UI.Now - _lastSfxVolAt > 90) { _lastSfxVolAt = UI.Now; Sfx.RebuildBuffers(); Sfx.Preview("volume"); }
                }
            }
            if (slHot) UI.Hand();
            float frac = (s.UiSoundsVolume - 10) / 290f;
            UI.Progress(new R(slX, sfxVolRow.Y + 27, slW, slHot ? 6 : 4), frac, Th.A(0.22f), Th.Brand);
            Gfx.Circle(slX + slW * frac, sfxVolRow.Y + 29, slHot ? 6.5f : 5f, Th.Text);
            var minusR = new R(slX - 36, sfxVolRow.Y + 14, 30, 30);
            Gfx.Fill(minusR, Th.A(0.1f), 15);
            if (UI.IconButton(UI.Id("set:svminus"), minusR, "\uE738", 11))
            {
                s.UiSoundsVolume = Math.Clamp(sv - 15, 10, 300);
                Sfx.RebuildBuffers(); Sfx.Preview("click"); Saved();
            }
        }
        ToggleRow("sfx8d", L.T("Реверберация звуков интерфейса", "UI sound reverb"), s.UiSoundsReverb8D, v => { s.UiSoundsReverb8D = v; Sfx.RebuildBuffers(); Sfx.Preview("video_open"); }, L.T("Короткое мягкое эхо по центру для стандартных и своих звуков", "Subtle centered hall tail for built-in and custom UI sounds"));

        var sfxCustomRow = Row(L.T("Свои звуки интерфейса (WAV / MP3 / OGG / FLAC / ZIP)", "Custom UI sounds (WAV / MP3 / OGG / FLAC / ZIP)"),
            s.CustomSfx.Count > 0 ? L.T($"Заменено звуков: {s.CustomSfx.Count} из {Sfx.Slots.Length}. Можно закинуть сразу .zip архив со звуками.", $"Custom sounds: {s.CustomSfx.Count} of {Sfx.Slots.Length}. You can also import a .zip soundpack.")
                                  : L.T("Замените отдельные события или загрузите готовый .zip звуковой пак целиком", "Replace individual UI events or import a complete .zip soundpack"), 58, 430);
        if (Visible(sfxCustomRow.Y, sfxCustomRow.H))
        {
            float sfxX = sfxCustomRow.Right - 14;
            if (s.CustomSfx.Count > 0)
            {
                var rsl = L.T("Сбросить", "Reset");
                var rsw = UI.PillWidth(Th.Restart, rsl);
                sfxX -= rsw;
                if (UI.Pill(UI.Id("set:sfxreset"), sfxX, sfxCustomRow.Y + 12, 34, Th.Restart, rsl, out _)) App.Post(Sfx.ResetAll);
                sfxX -= 8;
            }
            var glbl = _sfxGuideOpen ? L.T("Скрыть гайд", "Hide guide") : L.T("Гайд имён", "Naming guide");
            var gw = UI.PillWidth(Th.Info, glbl);
            sfxX -= gw;
            if (UI.Pill(UI.Id("set:sfxguide"), sfxX, sfxCustomRow.Y + 12, 34, Th.Info, glbl, out _))
                _sfxGuideOpen = !_sfxGuideOpen;
            sfxX -= 8;

            var csl = L.T("По одному…", "Per event…");
            var csw = UI.PillWidth(Th.Volume, csl);
            sfxX -= csw;
            if (UI.Pill(UI.Id("set:sfxpick"), sfxX, sfxCustomRow.Y + 12, 34, Th.Volume, csl, out _))
            {
                string audioFilter = L.T("Аудиофайлы (WAV, MP3, OGG, FLAC, M4A)\0*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.opus;*.aac\0Все файлы\0*.*\0\0",
                    "Audio files (WAV, MP3, OGG, FLAC, M4A)\0*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.opus;*.aac\0All files\0*.*\0\0");
                var menu = new List<MenuItem>();
                foreach (var (slotKey, slotLabel) in Sfx.Slots)
                {
                    var k = slotKey;
                    var hasCustom = s.CustomSfx.TryGetValue(k, out var cname) && !string.IsNullOrEmpty(cname);
                    var sub = new List<MenuItem>
                    {
                        new(L.T("Выбрать свой файл…", "Choose audio file…"), () => App.Post(() => Dialogs.PickFileAsync(audioFilter, f => Sfx.SetCustom(k, f))), icon: Th.Folder),
                        new(L.T("Прослушать", "Preview"), () => Sfx.Preview(k), icon: Th.Play),
                    };
                    if (hasCustom) sub.Add(new(L.T("Сбросить на стандартный", "Reset to default"), () => Sfx.ResetSlot(k), icon: Th.Restart, danger: true));
                    menu.Add(new(hasCustom ? $"{slotLabel}  ·  {cname}" : slotLabel, null, sub, check: hasCustom, icon: Th.Volume));
                }
                if (s.CustomSfx.Count > 0)
                {
                    menu.Add(MenuItem.Sep);
                    menu.Add(new(L.T("Сбросить все на стандартные", "Reset all to defaults"), Sfx.ResetAll, icon: Th.Restart, danger: true));
                }
                Menus.Popup(menu);
            }
            sfxX -= 8;

            var zlbl = L.T("Архив .zip…", "Import .zip…");
            var zw2 = UI.PillWidth(Th.Download, zlbl);
            sfxX -= zw2;
            if (UI.Pill(UI.Id("set:sfxzip"), sfxX, sfxCustomRow.Y + 12, 34, Th.Download, zlbl, out _, accent: true))
                App.Post(() => Dialogs.PickFileAsync("ZIP (*.zip)\0*.zip\0\0", Sfx.ImportZip));
        }

        if (_sfxGuideOpen)
        {
            var guideRun = Gfx.Text(Sfx.NamingGuide, 12.5f, 400, _w - 36);
            float gh = guideRun.H + 54;
            var gBox = new R(_x, _y, _w, gh);
            if (Visible(_y, gh))
            {
                Gfx.Fill(gBox, Th.Card, 10);
                Gfx.Stroke(gBox, Gfx.Fade(Th.Brand, 0.35f), 10);
                Gfx.Draw(L.T("Мини-гайд: как назвать файлы внутри ZIP-архива", "Quick guide: file names inside your ZIP pack"), _x + 16, _y + 12, 13.5f, Th.Text, 600);
                var cpLbl = L.T("Копировать список", "Copy list");
                var cpW = UI.PillWidth(Th.Copy, cpLbl);
                if (UI.Pill(UI.Id("set:sfxcopy"), gBox.Right - cpW - 12, _y + 7, 28, Th.Copy, cpLbl, out _))
                    Cards.CopyText(Sfx.NamingGuide, L.T("Список имён звуков скопирован", "Sound names copied"));
                Gfx.Draw(guideRun, _x + 16, _y + 40, Th.Sub);
            }
            _y += gh + 8;
        }

        PillGridRow("eff", L.T("Атмосферные и сезонные эффекты", "Ambient & seasonal effects"),
            L.T("Гирлянда и снег, Хэллоуин, лепестки Кавай или неоновые искры (0% нагрузки когда выключено)", "Snow & string lights, Halloween, Kawaii petals, or Neon laser sparks (0% idle cost when off)"),
            SeasonalEffects, s.SeasonalEffect, v => s.SeasonalEffect = v);

        Section(L.T("Плеер", "Player"));
        ChoiceRow("pq", L.T("Качество по умолчанию", "Default quality"), s.PlayerQuality, AppSettings.Qualities.Where(q => q != "audio"), v => { s.PlayerQuality = v; Resolver.Instance.Invalidate(); });
        var curAudioTuple = AudioLangs.FirstOrDefault(a => a.Code == s.AudioLanguage);
        var curAudioName = curAudioTuple.Ru != null ? L.T(curAudioTuple.Ru, curAudioTuple.En) : s.AudioLanguage;
        var alr = Row(L.T("Язык озвучки по умолчанию", "Default audio language"),
            L.T("Автоматически включает дубляж на выбранном языке, если он есть (субтитры не меняются)", "Automatically selects dubbed audio in your preferred language when available"), 58, 260);
        if (Visible(alr.Y, alr.H))
        {
            var alw = UI.PillWidth(null, curAudioName);
            if (UI.Pill(UI.Id("set:audiolang"), alr.Right - alw - 14, alr.Y + 12, 34, null, curAudioName, out _))
            {
                Menus.Popup(AudioLangs.Select(a =>
                {
                    var label = L.T(a.Ru, a.En);
                    return new MenuItem(label, () =>
                    {
                        s.AudioLanguage = a.Code;
                        Saved();
                        QuickStream.Invalidate();
                        Resolver.Instance.Invalidate();
                        Shell.Toast(L.T("Озвучка по умолчанию: ", "Default audio: ") + label, Th.Good, 2400);
                    }, check: a.Code == s.AudioLanguage, icon: Th.Volume);
                }).ToList());
            }
        }
        var dspRow = Row(L.T("DSP Студия: Эквалайзер, Nightcore, Реверб, Бас", "DSP Studio: EQ, Nightcore, Reverb, Bass"),
            L.T($"10 полос EQ, Nightcore / Slowed+Reverb, многоступенчатый реверб, Exciter и лимитер (Ctrl+E). Сейчас: {AudioDsp.DisplayPresetName(s)}",
                $"10-band EQ, Nightcore / Slowed+Reverb, studio reverb, Exciter & limiter (Ctrl+E). Active: {AudioDsp.DisplayPresetName(s)}"), 58, 220);
        if (Visible(dspRow.Y, dspRow.H))
        {
            var dLbl = L.T("Открыть студию…", "Open studio…");
            var dw = UI.PillWidth(Th.Headphones, dLbl);
            if (UI.Pill(UI.Id("set:dsp"), dspRow.Right - dw - 14, dspRow.Y + 12, 34, Th.Headphones, dLbl, out _, accent: s.AudioFxEnabled))
                AudioStudioPanel.Show();
        }
        ToggleRow("scwave", L.T("Кругляшки комментариев на плеере (и волна в музыке)", "SoundCloud comment avatars on player bar (wave in Music Mode)"), s.SoundCloudWave, v => { s.SoundCloudWave = v; Shell.PushPlayerState(); },
            L.T("В обычных видео показывает круглые аватарки комментариев прямо над полоской плеера (без волны), а в режиме «Только звук» — полную волну SoundCloud", "In normal videos shows circular comment avatars directly above the player seekbar (no bulky waveform); full SoundCloud waveform in Audio-Only mode"));
        ToggleRow("bassshake", L.T("Поэлементная тряска и прыжки UI от баса («Только звук»)", "Per-element bass bounce in Audio-Only mode"), s.MusicBassShake, v => s.MusicBassShake = v,
            L.T("Каждый элемент UI (обложка, кнопки, вкладки, сайдбар, карточки) прыгает и пульсирует отдельно на своей частоте спектра", "Every UI element (cover art, buttons, tabs, sidebar icons, cards) hops and bounces independently to its own FFT frequency band"));
        ToggleRow("wheelvol", L.T("Колёсико мыши меняет громкость на видео", "Mouse wheel adjusts volume over video"), s.WheelVolumeOnVideo, v => s.WheelVolumeOnVideo = v,
            L.T("Выключено: прокрутка колёсика над видео просто скроллит страницу (а в Shorts листает ленту)", "Off: wheel over video scrolls the page normally (and switches Shorts in Shorts feed)"));
        ToggleRow("shiftframe", L.T("Shift + колёсико: покадровая прокрутка видео", "Shift + wheel: frame-by-frame scrubbing"), s.ShiftWheelFrameStep, v => s.ShiftWheelFrameStep = v,
            L.T("Зажмите Shift и крутите колёсико над плеером, чтобы точно листать видео по одному кадру (счётчик кадров и мс)", "Hold Shift and scroll over the player to step frame by frame with frame counter & exact timestamp"));

        var sbAdsHint = ShortsAdBlocker.BlockedSessionCount > 0
            ? L.T($"Рекламные слоты, платные промо, erid/«Реклама», SponsorBlock и казино/промокоды · Скрыто за сессию: {ShortsAdBlocker.BlockedSessionCount}",
                  $"Ad slots, paid promos, SponsorBlock full-sponsor & affiliate spam · Blocked this session: {ShortsAdBlocker.BlockedSessionCount}")
            : L.T("Скрывает и автопропускает рекламные Shorts, платные интеграции, erid-рекламу, SponsorBlock и промокоды",
                  "Filters and auto-skips ad Shorts, paid promotions, SponsorBlock full sponsors, and promo spam");
        var sabRow = Row(L.T("Блокировка рекламы в Shorts", "Shorts Ad Blocker"), sbAdsHint, 58, 320);
        if (Visible(sabRow.Y, sabRow.H))
        {
            float sx = sabRow.Right - 14;
            for (int si = ShortsAdModes.Length - 1; si >= 0; si--)
            {
                var (sk, sru, sen) = ShortsAdModes[si];
                var sl = L.T(sru, sen);
                var sw = UI.PillWidth(null, sl); sx -= sw;
                bool on = s.ShortsAdBlock == sk;
                if (UI.Pill(UI.Id("set:sab:" + sk), sx, sabRow.Y + 12, 34, null, sl, out _, accent: on))
                {
                    s.ShortsAdBlock = sk;
                    Saved();
                    App.Invalidate();
                }
                sx -= 8;
            }
        }

        ToggleRow("audio8d", L.T("8D Аудио и реверберация в плеере", "8D Audio & Reverb in player"), s.PlayerAudio8D, v => { s.PlayerAudio8D = v; BgPlayers.ApplyAudioDsp(); }, L.T("Плавный 8D-эффект вращения звука и глубокая реверберация на видео и музыку (включается на лету)", "Smooth 8D spatial orbit and rich hall reverb on video & music (toggles live)"));
        ToggleRow("audioonly", L.T("Режим «Только звук» (волны вместо видео, 0% GPU)", "Audio-only mode (visualizer, 0% GPU)"), s.AudioOnlyMode, _ => Shell.ToggleAudioOnlyMode(), L.T("Полностью отключает декодирование видео и показывает живые аудиоволны поверх обложки", "Disables video decoding completely and renders a live audio visualizer over cover art"));
        ToggleRow("suspvid", L.T("Не декодировать видео при сворачивании и в фоне", "Suspend video decode when minimized/hidden"), s.AutoSuspendVideoWhenHidden, v => s.AutoSuspendVideoWhenHidden = v, L.T("Когда окно свёрнуто или открыта другая вкладка, видео не рендерится (0% GPU), звук продолжает играть", "Suspends video decoding (0% GPU) when minimized or on another tab while audio continues"));
        ToggleRow("shov", L.T("Подпись Shorts поверх видео при наведении", "Shorts title overlay on hover"), s.ShortsInfoOverlay, v => { s.ShortsInfoOverlay = v; Shell.PushPlayerState(); }, L.T("Включено: когда панель Shorts скрыта, канал и название появляются поверх видео при наведении.", "On: when the Shorts side panel is hidden, channel & title appear over the video on hover."));
        ToggleRow("sb", "SponsorBlock", s.SponsorBlock, v => s.SponsorBlock = v, L.T("Пропускать рекламные вставки авторов (выделяются жирной зелёной полосой на таймлайне)", "Auto-skip creator sponsor segments (highlighted in green on the timeline)"));
        var cr = Row(L.T("Что пропускать", "Categories to skip"), null, 50, 560);
        if (Visible(cr.Y, cr.H))
        {
            float cx = cr.Right - 14;
            for (int ci = Cats.Length - 1; ci >= 0; ci--)
            {
                var (key, ru, en) = Cats[ci];
                var name = L.T(ru, en);
                var on = s.SponsorCategories.Contains(key);
                cx -= UI.PillWidth(null, name);
                if (UI.Pill(UI.Id("set:cat:" + key), cx, cr.Y + 9, 32, null, name, out _, on ? Gfx.Fade(Th.Brand, 0.85f) : Th.A(0.08f), on ? Th.OnBrand : Th.Sub))
                {
                    s.SponsorCategories = on ? s.SponsorCategories.Where(c => c != key).ToArray() : [.. s.SponsorCategories, key];
                    Saved();
                }
                cx -= 6;
            }
        }
        ToggleRow("rmb", L.T("Правая кнопка мыши ставит паузу", "Right-click pauses video"), s.RightClickPause, v => { s.RightClickPause = v; Shell.PushPlayerState(); }, L.T("Выключено: правая кнопка по видео открывает меню", "Off: right-clicking the video opens the context menu"));
        ToggleRow("rd", L.T("Показывать дизлайки", "Show dislikes"), s.ReturnDislikes, v => s.ReturnDislikes = v, L.T("Return YouTube Dislike (в обычных видео и Shorts)", "Return YouTube Dislike (in videos & Shorts)"));
        ToggleRow("hs", L.T("Скрывать Shorts в лентах", "Hide Shorts in feeds"), s.HideShorts, v => s.HideShorts = v);
        ToggleRow("mw", L.T("Отмечать просмотренное в истории YouTube", "Mark watched in YouTube history"), s.MarkWatched, v => s.MarkWatched = v, L.T("Рекомендации учитывают ваши просмотры", "Keeps YouTube recommendations synced with your watches"));
        ToggleRow("ap", L.T("Автовоспроизведение следующего", "Autoplay next video"), s.Autoplay, v => s.Autoplay = v);
        ToggleRow("tabplay", L.T("Автостарт видео при открытии вкладки", "Autoplay video when switching tabs"), s.AutoPlayOpenedTabs, v => s.AutoPlayOpenedTabs = v, L.T("Выключено: открытые в фоне и поставленные на паузу вкладки остаются на паузе при переключении", "Off: background or paused tabs stay paused when you switch to them"));

        Section(L.T("Поиск", "Search"));
        ToggleRow("strict", L.T("Только по теме", "On-topic only"), s.SearchStrict, v => s.SearchStrict = v, L.T("В выдаче остаются видео, где слова запроса есть в названии или у канала", "Keeps only videos matching your query words in the title or channel name"));
        var br = Row(L.T("Скрытые каналы", "Hidden channels"),
            s.BlockedChannels.Length == 0 ? L.T("Скрыть канал можно из меню любого видео (правый клик)", "Hide any channel via right-click menu on a video")
                                          : L.T($"Скрыто каналов: {s.BlockedChannels.Length}. Они не появляются в поиске и рекомендациях.", $"Hidden channels: {s.BlockedChannels.Length}. Excluded from search, feeds, and Shorts."), 58, 170);
        if (Visible(br.Y, br.H) && s.BlockedChannels.Length > 0)
        {
            var ubLbl = L.T("Вернуть все", "Restore all");
            var bw = UI.PillWidth(null, ubLbl);
            if (UI.Pill(UI.Id("set:unblock"), br.Right - bw - 14, br.Y + 12, 34, null, ubLbl, out _)) { s.BlockedChannels = []; Saved(); }
        }

        Section(L.T("Загрузки", "Downloads"));
        var dr = Row(L.T("Папка", "Folder"), s.DownloadDir, 58, 240);
        if (Visible(dr.Y, dr.H))
        {
            float bx = dr.Right - 14;
            var openLbl = L.T("Открыть", "Open");
            var ow = UI.PillWidth(null, openLbl); bx -= ow;
            if (UI.Pill(UI.Id("set:odir"), bx, dr.Y + 12, 34, null, openLbl, out _))
            {
                try { Directory.CreateDirectory(s.DownloadDir); W.Open(s.DownloadDir); }
                catch (Exception e) { Shell.Toast(L.T("Не удалось открыть папку: ", "Could not open folder: ") + e.Message, Th.Warn); }
            }
            var pickLbl = L.T("Выбрать…", "Browse…");
            var pw = UI.PillWidth(null, pickLbl); bx -= pw + 8;
            if (UI.Pill(UI.Id("set:pdir"), bx, dr.Y + 12, 34, null, pickLbl, out _))
                App.Post(() => Dialogs.PickFolderAsync(L.T("Папка для загрузок", "Downloads folder"), d => { s.DownloadDir = d; Saved(); }));
        }
        ChoiceRow("dq", L.T("Качество по умолчанию", "Default quality"), s.DownloadQuality, AppSettings.Qualities, v => s.DownloadQuality = v);
        ToggleRow("sbd", L.T("Вырезать спонсорские вставки из скачанных файлов", "Remove SponsorBlock segments from downloads"), s.SponsorBlockOnDownload, v => s.SponsorBlockOnDownload = v);

        Section(L.T("Кэш просмотренного", "Media Cache"));
        if (UI.Now - _cacheAt > 2000) { _cacheAt = UI.Now; ThreadPool.QueueUserWorkItem(_ => { _cacheBytes = MediaCache.TotalBytes(); App.WakeUi(); }); }
        var lr = Row(L.T("Лимит на диске", "Disk limit"),
            L.T("Часто включаемое (музыка) хранится долго; просмотренное один раз удаляется через неделю, а при переполнении уходит первым.", "Frequently replayed media (music) stays cached; one-off watches expire after a week or when full."), 58, 170);
        if (Visible(lr.Y, lr.H))
        {
            var lim = CacheName(s.CacheLimitMb);
            var lw2 = UI.PillWidth(null, lim);
            if (UI.Pill(UI.Id("set:climit"), lr.Right - lw2 - 14, lr.Y + 12, 34, null, lim, out _))
                Menus.Popup(CacheLimits.Select(mb => new MenuItem(CacheName(mb), () =>
                {
                    s.CacheLimitMb = mb; Saved();
                    ThreadPool.QueueUserWorkItem(_ => { if (mb == 0) MediaCache.Clear(); else MediaCache.Enforce(); });
                }, null, mb == s.CacheLimitMb)).ToList());
        }
        var ur2 = Row(L.T("Сейчас занято", "Used space"),
            L.T($"{Fmt.Size(_cacheBytes)} из {CacheName(s.CacheLimitMb)}. Потоки хранятся как их отдаёт YouTube (AV1/VP9 и Opus).",
                $"{Fmt.Size(_cacheBytes)} of {CacheName(s.CacheLimitMb)}. Streams are stored directly as served by YouTube (AV1/VP9 & Opus)."), 58, 150);
        if (Visible(ur2.Y, ur2.H))
        {
            var clrLbl = L.T("Очистить", "Clear");
            var cw2 = UI.PillWidth(null, clrLbl);
            if (UI.Pill(UI.Id("set:cclear"), ur2.Right - cw2 - 14, ur2.Y + 12, 34, null, clrLbl, out _))
                ThreadPool.QueueUserWorkItem(_ => { MediaCache.Clear(); _cacheAt = 0; App.WakeUi(); });
        }
        ToggleRow("qs", L.T("Быстрый старт нового видео", "Instant video quick-start"), s.QuickStart, v => s.QuickStart = v, L.T("Прямой запрос потока без ожидания yt-dlp", "Direct stream lookup without waiting for yt-dlp"));

        Section(L.T("Сеть", "Network"));
        ToggleRow("imp", L.T("Отпечаток Chrome при загрузке с других сайтов", "Chrome fingerprint for external sites"), s.Impersonate, v => s.Impersonate = v, L.T("TLS и HTTP/2 как у настоящего браузера (curl_cffi)", "Browser-like TLS and HTTP/2 via curl_cffi"));
        EditInRow(_proxy, Row(L.T("Прокси", "Proxy"), L.T("Пусто — использовать прокси Windows, если он включён", "Leave blank to use Windows system proxy when enabled"), 58, 410), 380);
        var route = string.IsNullOrWhiteSpace(s.Proxy)
            ? YtDlp.EffectiveProxy is null ? L.T("Напрямую", "Direct") : L.T("Системный прокси Windows", "Windows system proxy")
            : L.T("Прокси FluxTube", "FluxTube proxy");
        Row(L.T("Маршрут YouTube", "YouTube route"), route, 50, 410);
        EditInRow(_hl, Row(L.T("Язык YouTube", "YouTube language"), null, 50, 160), 120);
        EditInRow(_gl, Row(L.T("Регион", "Region"), null, 50, 160), 120);
        var ur = Row(L.T("Движок загрузки", "Download engine"), _version, 58, 210);
        if (Visible(ur.Y, ur.H))
        {
            var updLbl = _updating ? L.T("Обновляю…", "Updating…") : L.T("Обновить сейчас", "Update now");
            var uw = UI.PillWidth(null, updLbl);
            if (UI.Pill(UI.Id("set:upd"), ur.Right - uw - 14, ur.Y + 12, 34, null, updLbl, out _) && !_updating)
            {
                _updating = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        s.LastYtDlpUpdate = default; s.LastPyUpdate = default;
                        await YtDlp.UpdateIfStaleAsync();
                        await Resolver.ProvisionAsync();
                        await ShowVersionAsync();
                        App.Post(() => Shell.Toast(L.T("yt-dlp обновлён", "yt-dlp updated"), Th.Good));
                    }
                    catch (Exception ex)
                    {
                        App.Post(() => Shell.Toast(L.T("Ошибка обновления yt-dlp: ", "yt-dlp update error: ") + ex.Message, Th.Warn));
                    }
                    finally
                    {
                        App.Post(() => { _updating = false; App.Invalidate(); });
                    }
                });
            }
        }

        Section(L.T("Обновления", "Updates"));
        var upr = Row($"FluxTube {Updater.CurrentVersion}", Updater.StatusText, Updater.Downloading ? 66 : 58, 320);
        if (Visible(upr.Y, upr.H))
        {
            if (Updater.Downloading)
                UI.Progress(new R(upr.X + 16, upr.Bottom - 10, upr.W - 32, 4), Updater.Progress, Th.A(0.2f), Th.Brand);
            else
            {
                float bx = upr.Right - 14;
                if (Updater.HasUpdate)
                {
                    var instLabel = L.T($"Установить {Updater.AvailableVersion}", $"Install {Updater.AvailableVersion}");
                    var iw = UI.PillWidth(Th.Download, instLabel); bx -= iw;
                    if (UI.Pill(UI.Id("set:appupd:go"), bx, upr.Y + 12, 34, Th.Download, instLabel, out _, accent: true))
                        App.Post(Updater.DownloadAndInstall);
                    bx -= 8;
                }
                var chkLabel = Updater.Checking ? L.T("Проверяю…", "Checking…") : L.T("Проверить", "Check");
                var cw3 = UI.PillWidth(Th.Refresh, chkLabel); bx -= cw3;
                if (UI.Pill(UI.Id("set:appupd:chk"), bx, upr.Y + 12, 34, Th.Refresh, chkLabel, out _) && !Updater.Checking)
                    App.Post(() => Updater.Check(manual: true));
            }
        }

        Section(L.T("Данные", "Data"));
        var da = Row(L.T("Папка данных и журнал", "Data folder & log"), Paths.Data, 58, 280);
        if (Visible(da.Y, da.H))
        {
            float bx = da.Right - 14;
            var logLbl = L.T("Журнал", "Log");
            var l1 = UI.PillWidth(null, logLbl); bx -= l1;
            if (UI.Pill(UI.Id("set:log"), bx, da.Y + 12, 34, null, logLbl, out _)) W.Open(Path.Combine(Paths.Logs, "app.log"));
            var dirLbl = L.T("Открыть папку", "Open folder");
            var l2 = UI.PillWidth(null, dirLbl); bx -= l2 + 8;
            if (UI.Pill(UI.Id("set:data"), bx, da.Y + 12, 34, null, dirLbl, out _)) W.Open(Paths.Data);
        }

        if (UI.Now - _statsAt > 1000)
        {
            _statsAt = UI.Now;
            using var proc = Process.GetCurrentProcess();
            _stats = L.T(
                $"Кадр {App.LastFrameMs:0.00} мс · память {proc.WorkingSet64 >> 20} МБ · превью на GPU {Images.GpuBytes >> 20} МБ",
                $"Frame {App.LastFrameMs:0.00} ms · RAM {proc.WorkingSet64 >> 20} MB · GPU thumbs {Images.GpuBytes >> 20} MB");
        }
        if (Visible(_y + 10, 20)) Gfx.Draw(_stats, _x, _y + 10, 12, Th.Dim);
        var needH = _y + 50 + scroll - r.Y;
        if (MathF.Abs(needH - _contentH) > 1f) { _contentH = needH; UI.Animate(); }
        UI.EndScroll();
    }
}
