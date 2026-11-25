using System.Diagnostics;
using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube.Pages;

/// <summary>
/// Two timelines over local data: what was watched (with position), and every card that was on screen
/// (with where it appeared), so nothing glimpsed before a feed refreshed is lost.
/// </summary>
public sealed class TimelinePage(bool seen) : IPage
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
            _filter = new Edit("Искать по названию, каналу, разделу") { Bg = () => Th.Card };
            _filter.Changed = () => { var g = ++_gen; App.After(180, () => { if (g == _gen) Reload(); }); };
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
                    Add(h.Last, h.ToVideo(), $"Остановились на {Fmt.Time(h.Position)}{(h.Duration > 0 ? " из " + Fmt.Time(h.Duration) : "")} · смотрели {Fmt.Time(h.Watched)} · {Fmt.Source(h.Source)}", h.Id, "history");
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
        Gfx.Draw(seen ? "Что я видел" : "История просмотров", x0, r.Y + 10, 24, Th.Text, 600);
        var cw = UI.PillWidth(Th.Delete, "Очистить");
        if (UI.Pill(UI.Id("clear", _scroll), r.Right - cw - 22, r.Y + 12, 32, Th.Delete, "Очистить", out _, Th.A(0.08f))) App.Post(Clear);
        var fb = new R(r.Right - cw - 22 - 12 - 320, r.Y + 11, 320, 34);
        Gfx.Fill(fb, Th.Card, 17);
        _filter?.Place(fb, 16, 16);
        Gfx.Draw(seen
            ? "Каждое видео, которое появлялось на экране: на главной, в поиске, в похожих. С временем и местом."
            : "Все запуски видео с позицией остановки. Клик продолжает с того же места.", x0, r.Y + 50, 12.5f, Th.Sub, 400, r.W - 48);

        var view = new R(x0, r.Y + 78, r.Right - x0 - 4, r.Bottom - r.Y - 78);
        if (_entries.Count == 0)
        {
            if (_loading) UI.Spinner(view.X + 20, view.Y + 30, 10, Th.Sub);
            else Gfx.Draw("Пока пусто.", view.X, view.Y + 10, 14, Th.Sub);
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
        if (Dialogs.Ask("Это только локальные данные FluxTube. Отменить нельзя.", seen ? "Очистить «Что я видел»?" : "Очистить историю просмотров?") != 1) return;
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
            _url = new Edit("Вставьте ссылку: YouTube, TikTok, Instagram, VK, Twitch… (yt-dlp понимает 1800+ сайтов)") { Bg = () => Th.Card };
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
        Gfx.Draw("Загрузки", x0, r.Y + 10, 24, Th.Text, 600);
        var fw = UI.PillWidth(Th.Folder, "Папка");
        if (UI.Pill(UI.Id("dl:folder"), right - fw, r.Y + 12, 32, Th.Folder, "Папка", out _, Th.A(0.08f)))
        {
            Directory.CreateDirectory(AppSettings.Current.DownloadDir);
            W.Open(AppSettings.Current.DownloadDir);
        }

        float y = r.Y + 58;
        var addW = UI.PillWidth(Th.Download, "Скачать");
        var ql = Cards.QualityName(_quality);
        var qW = UI.PillWidth(null, ql);
        if (UI.Pill(UI.Id("dl:add"), right - addW, y, 36, Th.Download, "Скачать", out _, accent: true)) App.Post(Add);
        if (UI.Pill(UI.Id("dl:q"), right - addW - 8 - qW, y, 36, null, ql, out _))
            Menus.Popup(AppSettings.Qualities.Select(q => new MenuItem(Cards.QualityName(q), () => _quality = q, null, q == _quality)).ToList());
        var box = new R(x0, y, right - addW - 8 - qW - 10 - x0, 36);
        Gfx.Fill(box, Th.Card, 18);
        _url?.Place(box, 18, 18);

        var items = Downloader.Instance.Items;
        var view = new R(x0, y + 50, r.Right - x0 - 4, r.Bottom - y - 50);
        if (items.Count == 0) { Gfx.Draw("Скачанные видео появятся здесь и будут играть без интернета.", view.X, view.Y + 8, 14, Th.Sub); return; }
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
        if (d.File == null || !File.Exists(d.File)) { Shell.Toast("Файл не найден: возможно, его переместили или удалили.", Th.Warn); return; }
        if (d.VideoId is { Length: 11 }) Shell.OpenVideo(new VideoItem { VideoId = d.VideoId, Title = d.Title, Channel = d.Channel }, "downloads");
        else { Shell.PlayFile(d); Shell.Toast("Играет в мини-плеере: " + d.Title); }
    }

    private static void Delete(DownloadItem d)
    {
        if (!d.IsDone) { Downloader.Instance.Remove(d, deleteFile: false); return; }
        var a = Dialogs.Ask(d.Title + "\n\nДа: удалить файл с диска.\nНет: убрать только из списка.", "Удалить загрузку?", threeWay: true);
        if (a < 0) return;
        Downloader.Instance.Remove(d, deleteFile: a == 1);
    }
}

public sealed class SettingsPage : IPage
{
    private Edit? _proxy, _hl, _gl;
    private string _version = "";
    private bool _updating;
    private readonly int _scroll = UI.Id("settings");
    private static readonly (string Key, string Name)[] Cats =
        [("sponsor", "Спонсоры"), ("selfpromo", "Самореклама"), ("interaction", "«Подпишись»"), ("intro", "Заставки"), ("outro", "Концовки")];

    public string Tag => "settings";
    public int MainScroll => _scroll;

    public void Shown()
    {
        var s = AppSettings.Current;
        if (_proxy == null)
        {
            _proxy = new Edit("пусто = напрямую; пример: socks5://127.0.0.1:1080");
            _hl = new Edit("ru");
            _gl = new Edit("по IP");
            _proxy.Text = s.Proxy; _hl.Text = s.Hl; _gl.Text = s.Gl;
            _proxy.Changed = () => { s.Proxy = _proxy.Text.Trim(); Saved(); };
            _hl.Changed = () => { s.Hl = string.IsNullOrWhiteSpace(_hl.Text) ? "ru" : _hl.Text.Trim(); Saved(); };
            _gl.Changed = () => { s.Gl = _gl.Text.Trim().ToUpperInvariant(); Saved(); };
        }
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
    private static string CacheName(int mb) => mb switch { 0 => "Выключен", < 1024 => mb + " МБ", _ => (mb / 1024) + " ГБ" };

    // layout state for one Draw
    private float _x, _y, _w, _contentH = 1900;
    private R _view;

    private void Section(string title)
    {
        _y += 18;
        Gfx.Draw(title, _x, _y, 16, Th.Text, 600);
        _y += 30;
    }

    private R Row(string label, string? hint = null, float h = 50)
    {
        var r = new R(_x, _y, _w, h);
        Gfx.Fill(r, Th.A(0.063f), 10);
        var t = Gfx.Draw(label, _x + 16, _y + (hint == null ? (h - 19) / 2 : 8), 14, Th.Text, 400, _w - 240);
        if (hint != null) Gfx.Draw(hint, _x + 16, _y + 8 + t.H + 1, 12, Th.Sub, 400, _w - 240);
        _y += h + 6;
        return r;
    }

    private void ToggleRow(string key, string label, bool value, Action<bool> set, string? hint = null)
    {
        var r = Row(label, hint, hint == null ? 50 : 58);
        if (UI.Toggle(UI.Id("set:" + key), new R(r.X, r.Y, r.W - 16, r.H), value)) { set(!value); Saved(); }
    }

    private void ChoiceRow(string key, string label, string current, IEnumerable<string> options, Action<string> set)
    {
        var r = Row(label);
        var text = Cards.QualityName(current);
        var w = UI.PillWidth(null, text);
        if (UI.Pill(UI.Id("set:" + key), r.Right - w - 14, r.Y + 8, 34, null, text, out _))
            Menus.Popup(options.Select(o => new MenuItem(Cards.QualityName(o), () => { set(o); Saved(); }, null, o == current)).ToList());
    }

    private void EditInRow(Edit? e, R row, float width)
    {
        var box = new R(row.Right - width - 14, row.Y + 8, width, row.H - 16);
        // a native control cannot be clipped by the scroll view: hide it when its row is not fully visible
        if (e == null || box.Y < _view.Y || box.Bottom > _view.Bottom) { e?.Hide(); return; }
        Gfx.Fill(box, Th.Field, 8);
        e.Place(box, 12, 12);
    }

    public void Draw(R r)
    {
        var s = AppSettings.Current;
        _view = new R(r.X, r.Y, r.W - 4, r.H);
        var scroll = UI.BeginScroll(_scroll, _view, _contentH);
        _x = r.X + 24; _w = MathF.Min(880, r.W - 60); _y = r.Y + 10 - scroll;
        Gfx.Draw("Настройки", _x, _y, 24, Th.Text, 600);
        _y += 34;

        Section("Аккаунт");
        var logged = CookieStore.IsLoggedIn;
        var defBrowser = BrowserLogin.DetectDefault();
        var acc = Row(logged ? "Вы вошли в YouTube" : "Вы не вошли",
            logged ? "Подписки, «Смотреть позже» и рекомендации берутся из вашего аккаунта." : $"Вход подхватывается напрямую из вашего браузера ({defBrowser.Name}) — без повторного ввода пароля.", 58);
        float bx = acc.Right - 14;
        var loginLabel = logged ? "Выйти" : $"Войти через {defBrowser.Name}";
        var lw = UI.PillWidth(logged ? null : Th.Globe, loginLabel);
        bx -= lw;
        if (UI.Pill(UI.Id("set:login"), bx, acc.Y + 12, 34, logged ? null : Th.Globe, loginLabel, out _, accent: !logged))
            App.Post(logged ? Shell.Logout : Shell.Login);
        if (AccountMenu.Saved.Count > 0)
        {
            var swLabel = $"Аккаунты ({AccountMenu.Saved.Count})";
            var swW = UI.PillWidth(Th.Account, swLabel); bx -= swW + 8;
            if (UI.Pill(UI.Id("set:accs"), bx, acc.Y + 12, 34, Th.Account, swLabel, out _))
            {
                var list = AccountMenu.Saved.Select(a => new MenuItem(
                    (a.Name.Length > 0 ? a.Name : a.Handle) + (a.Handle.Length > 0 && a.Name.Length > 0 ? $" ({a.Handle})" : ""),
                    () => AccountMenu.SwitchTo(a.Id),
                    check: logged && a.Id == AccountMenu.ActiveId,
                    icon: Th.Account)).ToList();
                list.Add(MenuItem.Sep);
                list.Add(new("Добавить аккаунт (окно входа)", Shell.LoginWebView, icon: Th.Add));
                Menus.Popup(list);
            }
        }
        var imp = Row("Вход из другого браузера", "Выберите установленный браузер или импортируйте cookies.txt.", 58);
        bx = imp.Right - 14;
        var w1 = UI.PillWidth(Th.Globe, "Браузер…"); bx -= w1;
        if (UI.Pill(UI.Id("set:br"), bx, imp.Y + 12, 34, Th.Globe, "Браузер…", out _))
        {
            var list = BrowserLogin.Supported.Select(b => new MenuItem(b.Name, () => BrowserLogin.ImportFromBrowser(b), icon: Th.Globe)).ToList();
            list.Add(MenuItem.Sep);
            list.Add(new("Открыть YouTube в браузере", () => W.Open("https://www.youtube.com/"), icon: Th.OpenNew));
            list.Add(new("Запасное окно входа (WebView2)", Shell.LoginWebView, icon: Th.Account));
            Menus.Popup(list);
        }
        var w2 = UI.PillWidth(Th.Folder, "cookies.txt"); bx -= w2 + 8;
        if (UI.Pill(UI.Id("set:ck"), bx, imp.Y + 12, 34, Th.Folder, "cookies.txt", out _))
            App.Post(() => { if (Dialogs.PickFile("cookies.txt\0*.txt\0\0") is { } f) BrowserLogin.ImportFromFile(f); });
        var w3 = UI.PillWidth(Th.Download, "Все аккаунты"); bx -= w3 + 8;
        if (UI.Pill(UI.Id("set:allacc"), bx, imp.Y + 12, 34, Th.Download, "Все аккаунты", out _))
            App.Post(BrowserLogin.ImportAllAccounts);

        Section("Оформление");
        _y += ThemePicker.Draw(_x, _y, _w);
        var zr = Row("Масштаб интерфейса", "Как в браузере: Ctrl + колёсико мыши, Ctrl + / − или Ctrl + 0 для сброса", 58);
        bx = zr.Right - 14;
        var zp = new R(bx - 34, zr.Y + 12, 34, 34);
        Gfx.Fill(zp, Th.A(0.1f), 17);
        if (UI.IconButton(UI.Id("set:zplus"), zp, Th.Add, 12)) Shell.AdjustZoom(0.10f);
        bx -= 40;
        var zLabel = $"{(int)MathF.Round(s.UiScale * 100)}%";
        var zw = UI.PillWidth(null, zLabel);
        bx -= zw;
        if (UI.Pill(UI.Id("set:zval"), bx, zr.Y + 12, 34, null, zLabel, out _))
        {
            var presets = new[] { 0.75f, 0.85f, 0.90f, 1.00f, 1.10f, 1.25f, 1.40f, 1.50f };
            Menus.Popup(presets.Select(p => new MenuItem($"{(int)MathF.Round(p * 100)}%", () => Shell.SetZoom(p), check: MathF.Abs(s.UiScale - p) < 0.02f)).ToList());
        }
        bx -= 40;
        var zm = new R(bx, zr.Y + 12, 34, 34);
        Gfx.Fill(zm, Th.A(0.1f), 17);
        if (UI.IconButton(UI.Id("set:zminus"), zm, "\uE738", 12)) Shell.AdjustZoom(-0.10f);
        ToggleRow("side", "Скрыть боковую панель", s.SidebarCollapsed, v => s.SidebarCollapsed = v, "Также переключается кнопкой меню сверху слева или Ctrl+B");
        ToggleRow("ambient", "Подсветка видео (Ambient Mode)", s.AmbientMode, v => s.AmbientMode = v, "Мягкое GPU-свечение вокруг плеера в цветах текущей сцены (обычные видео и Shorts)");
        ToggleRow("sfx", "Звуки интерфейса (Silent Hill / MGS)", s.UiSounds, v => { s.UiSounds = v; if (v) Sfx.Tab(); }, "Тихие тактильные звуки кликов, переключателей и ползунков в духе PS1/PS2 (в 4 раза тише видео)");
        var sfxCustomRow = Row("Свои звуки интерфейса (WAV / MP3 / OGG / FLAC)",
            s.CustomSfx.Count > 0 ? $"Заменено звуков: {s.CustomSfx.Count} из {Sfx.Slots.Length}" : "Можно заменить любой звук на свой аудиофайл", 58);
        float sfxX = sfxCustomRow.Right - 14;
        if (s.CustomSfx.Count > 0)
        {
            var rsw = UI.PillWidth(Th.Restart, "Сбросить");
            sfxX -= rsw;
            if (UI.Pill(UI.Id("set:sfxreset"), sfxX, sfxCustomRow.Y + 12, 34, Th.Restart, "Сбросить", out _)) App.Post(Sfx.ResetAll);
            sfxX -= 8;
        }
        var csw = UI.PillWidth(Th.Folder, "Заменить звуки…");
        sfxX -= csw;
        if (UI.Pill(UI.Id("set:sfxpick"), sfxX, sfxCustomRow.Y + 12, 34, Th.Folder, "Заменить звуки…", out _))
        {
            const string audioFilter = "Аудиофайлы (WAV, MP3, OGG, FLAC, M4A)\0*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.opus;*.aac\0Все файлы\0*.*\0\0";
            var menu = new List<MenuItem>();
            foreach (var (slotKey, slotLabel) in Sfx.Slots)
            {
                var k = slotKey;
                var hasCustom = s.CustomSfx.TryGetValue(k, out var cname) && !string.IsNullOrEmpty(cname);
                var sub = new List<MenuItem>
                {
                    new("Выбрать свой файл…", () => App.Post(() => { if (Dialogs.PickFile(audioFilter) is { } f) Sfx.SetCustom(k, f); }), icon: Th.Folder),
                    new("Прослушать", () => Sfx.Preview(k), icon: Th.Play),
                };
                if (hasCustom) sub.Add(new("Сбросить на стандартный", () => Sfx.ResetSlot(k), icon: Th.Restart, danger: true));
                menu.Add(new(hasCustom ? $"{slotLabel}  ·  {cname}" : slotLabel, null, sub, check: hasCustom, icon: Th.Volume));
            }
            if (s.CustomSfx.Count > 0)
            {
                menu.Add(MenuItem.Sep);
                menu.Add(new("Сбросить все на стандартные", Sfx.ResetAll, icon: Th.Restart, danger: true));
            }
            Menus.Popup(menu);
        }
        var effRow = Row("Сезонные эффекты (на любую тему)", "Снежинки с гирляндой или падающие тыквы поверх любой выбранной темы", 58);
        var effects = new (string Key, string Label)[] { ("none", "Выкл"), ("newyear", "❄ Новый год"), ("halloween", "🎃 Хэллоуин") };
        float ex = effRow.Right - 14;
        for (int ei = effects.Length - 1; ei >= 0; ei--)
        {
            var (ek, el) = effects[ei];
            var ew = UI.PillWidth(null, el);
            ex -= ew;
            bool on = s.SeasonalEffect == ek;
            if (UI.Pill(UI.Id("set:eff:" + ek), ex, effRow.Y + 12, 34, null, el, out _, accent: on))
            {
                s.SeasonalEffect = ek;
                Saved();
            }
            ex -= 8;
        }

        Section("Плеер");
        ChoiceRow("pq", "Качество по умолчанию", s.PlayerQuality, AppSettings.Qualities.Where(q => q != "audio"), v => { s.PlayerQuality = v; Resolver.Instance.Invalidate(); });
        var audioLangs = new (string Code, string Name)[]
        {
            ("", "Оригинал (как на YouTube)"),
            ("ru", "Русский"),
            ("en", "English"),
            ("uk", "Українська"),
            ("de", "Deutsch"),
            ("es", "Español"),
            ("fr", "Français"),
            ("ja", "日本語"),
        };
        var curAudioName = audioLangs.FirstOrDefault(a => a.Code == s.AudioLanguage).Name ?? s.AudioLanguage;
        var alr = Row("Язык озвучки по умолчанию", "Автоматически включает дубляж на выбранном языке, если он есть (субтитры не меняются)", 58);
        var alw = UI.PillWidth(null, curAudioName);
        if (UI.Pill(UI.Id("set:audiolang"), alr.Right - alw - 14, alr.Y + 12, 34, null, curAudioName, out _))
        {
            Menus.Popup(audioLangs.Select(a => new MenuItem(a.Name, () =>
            {
                s.AudioLanguage = a.Code;
                Saved();
                QuickStream.Invalidate();
                Resolver.Instance.Invalidate();
                Shell.Toast("Озвучка по умолчанию: " + a.Name, Th.Good, 2400);
            }, check: a.Code == s.AudioLanguage, icon: Th.Volume)).ToList());
        }
        ToggleRow("wscroll", "Прокручивать всю страницу с видео", s.ScrollWholeWatchPage, v => s.ScrollWholeWatchPage = v, "Включено: при прокрутке вниз видео скрывается, удобно читать комментарии. Выключено: комментарии листаются отдельно.");
        ToggleRow("shov", "Подпись Shorts поверх видео при наведении", s.ShortsInfoOverlay, v => { s.ShortsInfoOverlay = v; Shell.PushPlayerState(); }, "Включено: когда панель Shorts скрыта, канал и название появляются поверх видео при наведении. Выключено: аккуратный блок текста сбоку.");
        ToggleRow("sb", "SponsorBlock", s.SponsorBlock, v => s.SponsorBlock = v, "Пропускать рекламные вставки авторов");
        var cr = Row("Что пропускать");
        float cx = cr.Right - 14;
        foreach (var (key, name) in Cats.Reverse())
        {
            var on = s.SponsorCategories.Contains(key);
            cx -= UI.PillWidth(null, name);
            if (UI.Pill(UI.Id("set:cat:" + key), cx, cr.Y + 9, 32, null, name, out _, on ? Gfx.Fade(Th.Brand, 0.85f) : Th.A(0.08f), on ? Th.OnBrand : Th.Sub))
            {
                s.SponsorCategories = on ? s.SponsorCategories.Where(c => c != key).ToArray() : [.. s.SponsorCategories, key];
                Saved();
            }
            cx -= 6;
        }
        ToggleRow("rmb", "Правая кнопка мыши ставит паузу", s.RightClickPause, v => { s.RightClickPause = v; Shell.PushPlayerState(); }, "Выключено: правая кнопка по видео открывает меню");
        ToggleRow("rd", "Показывать дизлайки", s.ReturnDislikes, v => s.ReturnDislikes = v, "Return YouTube Dislike");
        ToggleRow("hs", "Скрывать Shorts в лентах", s.HideShorts, v => s.HideShorts = v);
        ToggleRow("mw", "Отмечать просмотренное в истории YouTube", s.MarkWatched, v => s.MarkWatched = v, "Рекомендации учитывают ваши просмотры");
        ToggleRow("ap", "Автовоспроизведение следующего", s.Autoplay, v => s.Autoplay = v);

        Section("Поиск");
        ToggleRow("strict", "Только по теме", s.SearchStrict, v => s.SearchStrict = v, "В выдаче остаются видео, где слова запроса есть в названии или у канала");
        var br = Row("Скрытые каналы", s.BlockedChannels.Length == 0 ? "Скрыть канал можно из меню любого видео (правый клик)" : $"Скрыто каналов: {s.BlockedChannels.Length}. Они не появляются в поиске и рекомендациях.", 58);
        if (s.BlockedChannels.Length > 0)
        {
            var bw = UI.PillWidth(null, "Вернуть все");
            if (UI.Pill(UI.Id("set:unblock"), br.Right - bw - 14, br.Y + 12, 34, null, "Вернуть все", out _)) { s.BlockedChannels = []; Saved(); }
        }

        Section("Загрузки");
        var dr = Row("Папка", s.DownloadDir, 58);
        bx = dr.Right - 14;
        var ow = UI.PillWidth(null, "Открыть"); bx -= ow;
        if (UI.Pill(UI.Id("set:odir"), bx, dr.Y + 12, 34, null, "Открыть", out _)) { Directory.CreateDirectory(s.DownloadDir); W.Open(s.DownloadDir); }
        var pw = UI.PillWidth(null, "Выбрать…"); bx -= pw + 8;
        if (UI.Pill(UI.Id("set:pdir"), bx, dr.Y + 12, 34, null, "Выбрать…", out _))
            App.Post(() => { if (Dialogs.PickFolder("Папка для загрузок") is { } d) { s.DownloadDir = d; Saved(); } });
        ChoiceRow("dq", "Качество по умолчанию", s.DownloadQuality, AppSettings.Qualities, v => s.DownloadQuality = v);
        ToggleRow("sbd", "Вырезать спонсорские вставки из скачанных файлов", s.SponsorBlockOnDownload, v => s.SponsorBlockOnDownload = v);

        Section("Кэш просмотренного");
        if (UI.Now - _cacheAt > 2000) { _cacheAt = UI.Now; ThreadPool.QueueUserWorkItem(_ => { _cacheBytes = MediaCache.TotalBytes(); App.WakeUi(); }); }
        var lr = Row("Лимит на диске", "Часто включаемое (музыка) хранится долго; просмотренное один раз удаляется через неделю, а при переполнении уходит первым.", 58);
        var lim = CacheName(s.CacheLimitMb);
        var lw2 = UI.PillWidth(null, lim);
        if (UI.Pill(UI.Id("set:climit"), lr.Right - lw2 - 14, lr.Y + 12, 34, null, lim, out _))
            Menus.Popup(new[] { 0, 512, 1024, 2048, 5120, 10240 }.Select(mb => new MenuItem(CacheName(mb), () =>
            {
                s.CacheLimitMb = mb; Saved();
                ThreadPool.QueueUserWorkItem(_ => { if (mb == 0) MediaCache.Clear(); else MediaCache.Enforce(); });
            }, null, mb == s.CacheLimitMb)).ToList());
        var ur2 = Row("Сейчас занято", $"{Fmt.Size(_cacheBytes)} из {CacheName(s.CacheLimitMb)}. Потоки хранятся как их отдаёт YouTube (AV1/VP9 и Opus): сильнее они не сжимаются.", 58);
        var cw2 = UI.PillWidth(null, "Очистить");
        if (UI.Pill(UI.Id("set:cclear"), ur2.Right - cw2 - 14, ur2.Y + 12, 34, null, "Очистить", out _))
            ThreadPool.QueueUserWorkItem(_ => { MediaCache.Clear(); _cacheAt = 0; App.WakeUi(); });
        ToggleRow("qs", "Быстрый старт нового видео", s.QuickStart, v => s.QuickStart = v, "Сразу в 360p, через несколько секунд само переключается на полное качество");

        Section("Сеть");
        ToggleRow("imp", "Отпечаток Chrome при загрузке с других сайтов", s.Impersonate, v => s.Impersonate = v, "TLS и HTTP/2 как у настоящего браузера (curl_cffi): TikTok и подобные");
        EditInRow(_proxy, Row("Прокси"), 380);
        EditInRow(_hl, Row("Язык YouTube"), 120);
        EditInRow(_gl, Row("Регион"), 120);
        var ur = Row("Движок загрузки", _version, 58);
        var uw = UI.PillWidth(null, _updating ? "Обновляю…" : "Обновить сейчас");
        if (UI.Pill(UI.Id("set:upd"), ur.Right - uw - 14, ur.Y + 12, 34, null, _updating ? "Обновляю…" : "Обновить сейчас", out _) && !_updating)
        {
            _updating = true;
            _ = Task.Run(async () =>
            {
                s.LastYtDlpUpdate = default; s.LastPyUpdate = default;
                await YtDlp.UpdateIfStaleAsync();
                await Resolver.ProvisionAsync();
                await ShowVersionAsync();
                App.Post(() => { _updating = false; Shell.Toast("yt-dlp обновлён", Th.Good); });
            });
        }

        Section("Обновления");
        var upr = Row($"FluxTube {Updater.CurrentVersion}", Updater.StatusText, Updater.Downloading ? 66 : 58);
        if (Updater.Downloading)
            UI.Progress(new R(upr.X + 16, upr.Bottom - 10, upr.W - 32, 4), Updater.Progress, Th.A(0.2f), Th.Brand);
        else
        {
            bx = upr.Right - 14;
            if (Updater.HasUpdate)
            {
                var instLabel = $"Установить {Updater.AvailableVersion}";
                var iw = UI.PillWidth(Th.Download, instLabel); bx -= iw;
                if (UI.Pill(UI.Id("set:appupd:go"), bx, upr.Y + 12, 34, Th.Download, instLabel, out _, accent: true))
                    App.Post(Updater.DownloadAndInstall);
                bx -= 8;
            }
            var chkLabel = Updater.Checking ? "Проверяю…" : "Проверить";
            var cw3 = UI.PillWidth(Th.Refresh, chkLabel); bx -= cw3;
            if (UI.Pill(UI.Id("set:appupd:chk"), bx, upr.Y + 12, 34, Th.Refresh, chkLabel, out _) && !Updater.Checking)
                App.Post(() => Updater.Check(manual: true));
        }

        Section("Данные");
        var da = Row("Папка данных и журнал", Paths.Data, 58);
        bx = da.Right - 14;
        var l1 = UI.PillWidth(null, "Журнал"); bx -= l1;
        if (UI.Pill(UI.Id("set:log"), bx, da.Y + 12, 34, null, "Журнал", out _)) W.Open(Path.Combine(Paths.Logs, "app.log"));
        var l2 = UI.PillWidth(null, "Открыть папку"); bx -= l2 + 8;
        if (UI.Pill(UI.Id("set:data"), bx, da.Y + 12, 34, null, "Открыть папку", out _)) W.Open(Paths.Data);

        if (UI.Now - _statsAt > 1000)
        {
            _statsAt = UI.Now;
            using var proc = Process.GetCurrentProcess();
            _stats = $"Кадр {App.LastFrameMs:0.00} мс · память {proc.WorkingSet64 >> 20} МБ · превью на GPU {Images.GpuBytes >> 20} МБ";
        }
        Gfx.Draw(_stats, _x, _y + 10, 12, Th.Dim);
        _contentH = _y + 50 + scroll - r.Y;
        UI.EndScroll();
    }

    private static async Task ImportFirefox()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fluxtube-ff-cookies.txt");
        try
        {
            var psi = new ProcessStartInfo(Paths.YtDlp) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in new[] { "--cookies-from-browser", "firefox", "--cookies", tmp, "--skip-download", "--no-warnings", "--flat-playlist", "https://www.youtube.com/feed/history" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (!File.Exists(tmp)) { App.Post(() => Shell.Toast("Firefox не найден или в нём нет входа: " + err.Trim(), Th.Warn, 7000)); return; }
            App.Post(() => { ImportFrom(tmp); try { File.Delete(tmp); } catch { } });
        }
        catch (Exception e) { App.Post(() => Shell.Toast("Firefox: " + e.Message, Th.Warn)); }
    }

    private static void ImportFrom(string path)
    {
        // keep only Google/YouTube cookies, nothing else from the browser jar
        var keep = File.ReadAllLines(path).Where(l =>
        {
            var d = l.StartsWith("#HttpOnly_") ? l[10..] : l;
            if (d.StartsWith('#') || d.Length == 0) return false;
            var dom = d.Split('\t')[0].TrimStart('.');
            return dom.EndsWith("youtube.com") || dom.EndsWith("google.com");
        });
        File.WriteAllLines(Paths.Cookies, new[] { "# Netscape HTTP Cookie File" }.Concat(keep));
        Shell.OnAccountChanged();
        Shell.Toast(CookieStore.IsLoggedIn ? "Вход импортирован" : "В файле нет входа в YouTube", CookieStore.IsLoggedIn ? Th.Good : Th.Warn);
    }
}
