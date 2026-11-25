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
            _filter = new Edit("Искать по названию, каналу, разделу") { Bg = () => Th.Card };
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
        var cw = UI.PillWidth(Th.Delete, "Очистить");
        float topY = embedded ? r.Y + 4 : r.Y + 10;
        if (!embedded) Gfx.Draw(seen ? "Что я видел" : "История просмотров", x0, topY, 24, Th.Text, 600);
        if (UI.Pill(UI.Id("clear", _scroll), r.Right - cw - 22, topY + 2, 32, Th.Delete, "Очистить", out _, Th.A(0.08f))) App.Post(Clear);
        var fb = new R(r.Right - cw - 22 - 12 - 320, topY + 1, 320, 34);
        Gfx.Fill(fb, Th.Card, 17);
        _filter?.Place(fb, 16, 16);
        float descY = embedded ? topY + 8 : r.Y + 50;
        float descW = embedded ? MathF.Max(120, fb.X - x0 - 16) : r.W - 48;
        Gfx.Draw(seen
            ? "Каждое видео, которое появлялось на экране: на главной, в поиске, в похожих. С временем и местом."
            : "Все запуски видео с позицией остановки. Клик продолжает с того же места.", x0, descY, 12.5f, Th.Sub, 400, descW);

        float viewTop = embedded ? r.Y + 48 : r.Y + 78;
        var view = new R(x0, viewTop, r.Right - x0 - 4, r.Bottom - viewTop);
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
            _url = new Edit("Вставьте ссылку на видео (YouTube, VK, Twitch и др.)") { Bg = () => Th.Card };
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
            try { Directory.CreateDirectory(AppSettings.Current.DownloadDir); W.Open(AppSettings.Current.DownloadDir); }
            catch (Exception e) { Shell.Toast("Не удалось открыть папку: " + e.Message, Th.Warn); }
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
    private bool _sfxGuideOpen;
    private bool _dragSfxVol;
    private long _lastSfxVolAt;
    private readonly int _scroll = UI.Id("settings");
    private static readonly (string Key, string Name)[] Cats =
        [("sponsor", "Спонсоры"), ("selfpromo", "Самореклама"), ("interaction", "«Подпишись»"), ("intro", "Заставки"), ("outro", "Концовки")];
    private static readonly float[] ZoomPresets = [0.75f, 0.85f, 0.90f, 1.00f, 1.10f, 1.25f, 1.40f, 1.50f];
    private static readonly (string Key, string Label)[] SeasonalEffects =
        [("none", "Выкл"), ("newyear", "❄ Новый год"), ("halloween", "🎃 Хэллоуин")];
    private static readonly (string Key, string Label)[] FrostedModes =
        [("off", "Выкл"), ("soft", "Лёгкое"), ("matte", "Матовое стекло")];
    private static readonly (string Code, string Name)[] AudioLangs =
    [
        ("", "Оригинал (как на YouTube)"),
        ("ru", "Русский"), ("en", "English"), ("uk", "Українська"),
        ("de", "Deutsch"), ("es", "Español"), ("fr", "Français"), ("ja", "日本語"),
    ];
    private static readonly int[] CacheLimits = [0, 512, 1024, 2048, 5120, 10240];

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
    private static string CacheName(int mb) => mb switch { 0 => "Выключен", < 1024 => mb + " МБ", _ => (mb / 1024) + " ГБ" };

    // layout state for one Draw
    private float _x, _y, _w, _contentH = 2100;
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
            Gfx.Fill(r, Th.A(0.063f), 10);
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
        var box = new R(row.Right - width - 14, row.Y + 8, width, row.H - 16);
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
        if (Visible(_y, 34)) Gfx.Draw("Настройки", _x, _y, 24, Th.Text, 600);
        _y += 34;

        Section("Аккаунты YouTube");
        var logged = CookieStore.IsLoggedIn;
        var curAcc = AccountMenu.Current;
        var accTitle = logged
            ? (curAcc is { Name.Length: > 0 } ? $"Активный аккаунт: {curAcc.Name}" : "Вы вошли в YouTube")
            : "Вы не вошли в аккаунт";
        var acc = Row(accTitle,
            logged ? $"Вход через {BrowserLogin.DefaultBrowserName}: добавляйте несколько аккаунтов и переключайте их в 1 клик."
                   : $"Войдите в Google через {BrowserLogin.DefaultBrowserName} — подписки, история и рекомендации подтянутся автоматически.", 58, 430);
        if (Visible(acc.Y, acc.H))
        {
            float bx = acc.Right - 14;
            if (logged)
            {
                var lw = UI.PillWidth(null, "Выйти"); bx -= lw;
                if (UI.Pill(UI.Id("set:logout"), bx, acc.Y + 12, 34, null, "Выйти", out _)) App.Post(Shell.Logout);
                bx -= 8;
            }
            var addLabel = logged ? $"Добавить ({BrowserLogin.DefaultBrowserName})" : $"Войти через {BrowserLogin.DefaultBrowserName}";
            var aw = UI.PillWidth(logged ? Th.Add : Th.Globe, addLabel); bx -= aw;
            if (UI.Pill(UI.Id("set:login"), bx, acc.Y + 12, 34, logged ? Th.Add : Th.Globe, addLabel, out _, accent: true))
                App.Post(Shell.LoginNewAccount);
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
                    list.Add(new($"Войти через {BrowserLogin.DefaultBrowserName}…", Shell.LoginNewAccount, icon: Th.Globe));
                    list.Add(new("Встроенное окно входа…", Shell.LoginWebView, icon: Th.Account));
                    Menus.Popup(list);
                }
            }
        }
        var imp = Row("Резервные способы входа", "Встроенное окно входа или импорт файла Netscape cookies.txt.", 54, 380);
        if (Visible(imp.Y, imp.H))
        {
            float bx = imp.Right - 14;
            var w2 = UI.PillWidth(Th.Folder, "Выбрать cookies.txt"); bx -= w2;
            if (UI.Pill(UI.Id("set:ck"), bx, imp.Y + 10, 34, Th.Folder, "Выбрать cookies.txt", out _))
                App.Post(() => Dialogs.PickFileAsync("cookies.txt\0*.txt\0\0", BrowserLogin.ImportFromFile));
            bx -= 8;
            var w3 = UI.PillWidth(Th.Account, "Встроенное окно"); bx -= w3;
            if (UI.Pill(UI.Id("set:wvlogin"), bx, imp.Y + 10, 34, Th.Account, "Встроенное окно", out _))
                App.Post(Shell.LoginWebView);
        }

        Section("Оформление");
        _y += ThemePicker.Draw(_x, _y, _w);

        var customThemeRow = Row("Свои темы (редактор кода)", "Пишите темы в JSON или key: #RRGGBB с живым превью · ПКМ по любой теме открывает её код", 58, 340);
        if (Visible(customThemeRow.Y, customThemeRow.H))
        {
            float tx = customThemeRow.Right - 14;
            var tdW = UI.PillWidth(Th.Folder, "Папка тем"); tx -= tdW;
            if (UI.Pill(UI.Id("set:tdir"), tx, customThemeRow.Y + 12, 34, Th.Folder, "Папка тем", out _))
                W.Open(Themes.CustomDir);
            tx -= 8;
            var teW = UI.PillWidth(Th.Add, "Создать / код темы…"); tx -= teW;
            if (UI.Pill(UI.Id("set:tedit"), tx, customThemeRow.Y + 12, 34, Th.Add, "Создать / код темы…", out _, accent: true))
                App.Post(() => ThemeCodeEditor.Show());
        }

        var frostRow = Row("Матовое стекло на фон", "Приятная шероховатая текстура матового стекла и мягкий рассеянный свет на любую тему", 58, 360);
        if (Visible(frostRow.Y, frostRow.H))
        {
            float fx = frostRow.Right - 14;
            for (int fi = FrostedModes.Length - 1; fi >= 0; fi--)
            {
                var (fk, fl) = FrostedModes[fi];
                var fw = UI.PillWidth(null, fl); fx -= fw;
                bool on = s.FrostedGlass == fk;
                if (UI.Pill(UI.Id("set:frost:" + fk), fx, frostRow.Y + 12, 34, null, fl, out _, accent: on))
                {
                    s.FrostedGlass = fk;
                    Saved();
                    App.Invalidate();
                }
                fx -= 8;
            }
        }
        var zr = Row("Масштаб интерфейса", "Как в браузере: Ctrl + колёсико мыши, Ctrl + / − или Ctrl + 0 для сброса", 58, 220);
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
        ToggleRow("side", "Скрыть боковую панель", s.SidebarCollapsed, v => s.SidebarCollapsed = v, "Также переключается кнопкой меню сверху слева или Ctrl+B");
        ToggleRow("ambient", "Подсветка видео (Ambient Mode)", s.AmbientMode, v => s.AmbientMode = v, "Мягкое GPU-свечение вокруг плеера в цветах текущей сцены (обычные видео и Shorts)");
        ToggleRow("sfx", "Звуки интерфейса (Silent Hill / MGS)", s.UiSounds, v => { s.UiSounds = v; if (v) Sfx.Tab(); }, "Отдельные тактильные звуки на клик, открытие видео, лайк, канал, громкость (зависит от громкости видео), вкладки и тумблеры");

        if (!UI.Down && _dragSfxVol)
        {
            _dragSfxVol = false;
            Sfx.RebuildBuffers();
            Sfx.Preview("click");
            Saved();
        }
        var sfxVolRow = Row("Громкость звуков интерфейса", "Увеличьте (до 300%) или убавьте громкость стандартных и своих звуков", 58, 300);
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
        ToggleRow("sfx8d", "8D Реверберация звуков интерфейса", s.UiSoundsReverb8D, v => { s.UiSoundsReverb8D = v; Sfx.RebuildBuffers(); Sfx.Preview("video_open"); }, "Объёмный 8D стерео-ревербератор (пространственное эхо вокруг головы на все стандартные и свои звуки)");

        var sfxCustomRow = Row("Свои звуки интерфейса (WAV / MP3 / OGG / FLAC / ZIP)",
            s.CustomSfx.Count > 0 ? $"Заменено звуков: {s.CustomSfx.Count} из {Sfx.Slots.Length}. Можно закинуть сразу .zip архив со звуками."
                                  : "Замените отдельные события или загрузите готовый .zip звуковой пак целиком", 58, 430);
        if (Visible(sfxCustomRow.Y, sfxCustomRow.H))
        {
            float sfxX = sfxCustomRow.Right - 14;
            if (s.CustomSfx.Count > 0)
            {
                var rsw = UI.PillWidth(Th.Restart, "Сбросить");
                sfxX -= rsw;
                if (UI.Pill(UI.Id("set:sfxreset"), sfxX, sfxCustomRow.Y + 12, 34, Th.Restart, "Сбросить", out _)) App.Post(Sfx.ResetAll);
                sfxX -= 8;
            }
            var gw = UI.PillWidth(Th.Info, _sfxGuideOpen ? "Скрыть гайд" : "Гайд имён");
            sfxX -= gw;
            if (UI.Pill(UI.Id("set:sfxguide"), sfxX, sfxCustomRow.Y + 12, 34, Th.Info, _sfxGuideOpen ? "Скрыть гайд" : "Гайд имён", out _))
                _sfxGuideOpen = !_sfxGuideOpen;
            sfxX -= 8;

            var csw = UI.PillWidth(Th.Volume, "По одному…");
            sfxX -= csw;
            if (UI.Pill(UI.Id("set:sfxpick"), sfxX, sfxCustomRow.Y + 12, 34, Th.Volume, "По одному…", out _))
            {
                const string audioFilter = "Аудиофайлы (WAV, MP3, OGG, FLAC, M4A)\0*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.opus;*.aac\0Все файлы\0*.*\0\0";
                var menu = new List<MenuItem>();
                foreach (var (slotKey, slotLabel) in Sfx.Slots)
                {
                    var k = slotKey;
                    var hasCustom = s.CustomSfx.TryGetValue(k, out var cname) && !string.IsNullOrEmpty(cname);
                    var sub = new List<MenuItem>
                    {
                        new("Выбрать свой файл…", () => App.Post(() => Dialogs.PickFileAsync(audioFilter, f => Sfx.SetCustom(k, f))), icon: Th.Folder),
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
            sfxX -= 8;

            var zw2 = UI.PillWidth(Th.Download, "Архив .zip…");
            sfxX -= zw2;
            if (UI.Pill(UI.Id("set:sfxzip"), sfxX, sfxCustomRow.Y + 12, 34, Th.Download, "Архив .zip…", out _, accent: true))
                App.Post(() => Dialogs.PickFileAsync("ZIP-архив со звуками (*.zip)\0*.zip\0\0", Sfx.ImportZip));
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
                Gfx.Draw("Мини-гайд: как назвать файлы внутри ZIP-архива", _x + 16, _y + 12, 13.5f, Th.Text, 600);
                var cpW = UI.PillWidth(Th.Copy, "Копировать список");
                if (UI.Pill(UI.Id("set:sfxcopy"), gBox.Right - cpW - 12, _y + 7, 28, Th.Copy, "Копировать список", out _))
                    Cards.CopyText(Sfx.NamingGuide, "Список имён звуков скопирован");
                Gfx.Draw(guideRun, _x + 16, _y + 40, Th.Sub);
            }
            _y += gh + 8;
        }

        var effRow = Row("Сезонные эффекты (на любую тему)", "Гирлянда во всю ширину, снежинки со звёздочками или хэллоуинские тыквы и летучие мыши", 58, 340);
        if (Visible(effRow.Y, effRow.H))
        {
            float ex = effRow.Right - 14;
            for (int ei = SeasonalEffects.Length - 1; ei >= 0; ei--)
            {
                var (ek, el) = SeasonalEffects[ei];
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
        }

        Section("Плеер");
        ChoiceRow("pq", "Качество по умолчанию", s.PlayerQuality, AppSettings.Qualities.Where(q => q != "audio"), v => { s.PlayerQuality = v; Resolver.Instance.Invalidate(); });
        var curAudioName = AudioLangs.FirstOrDefault(a => a.Code == s.AudioLanguage).Name ?? s.AudioLanguage;
        var alr = Row("Язык озвучки по умолчанию", "Автоматически включает дубляж на выбранном языке, если он есть (субтитры не меняются)", 58, 260);
        if (Visible(alr.Y, alr.H))
        {
            var alw = UI.PillWidth(null, curAudioName);
            if (UI.Pill(UI.Id("set:audiolang"), alr.Right - alw - 14, alr.Y + 12, 34, null, curAudioName, out _))
            {
                Menus.Popup(AudioLangs.Select(a => new MenuItem(a.Name, () =>
                {
                    s.AudioLanguage = a.Code;
                    Saved();
                    QuickStream.Invalidate();
                    Resolver.Instance.Invalidate();
                    Shell.Toast("Озвучка по умолчанию: " + a.Name, Th.Good, 2400);
                }, check: a.Code == s.AudioLanguage, icon: Th.Volume)).ToList());
            }
        }
        ToggleRow("audio8d", "8D Аудио и реверберация в плеере", s.PlayerAudio8D, v => s.PlayerAudio8D = v, "Плавный 8D-эффект вращения звука и глубокая реверберация на видео и музыку (включается на лету)");
        ToggleRow("wscroll", "Прокручивать всю страницу с видео", s.ScrollWholeWatchPage, v => s.ScrollWholeWatchPage = v, "Включено: при прокрутке вниз видео скрывается, удобно читать комментарии. Выключено: комментарии листаются отдельно.");
        ToggleRow("shov", "Подпись Shorts поверх видео при наведении", s.ShortsInfoOverlay, v => { s.ShortsInfoOverlay = v; Shell.PushPlayerState(); }, "Включено: когда панель Shorts скрыта, канал и название появляются поверх видео при наведении. Выключено: аккуратный блок текста сбоку.");
        ToggleRow("sb", "SponsorBlock", s.SponsorBlock, v => s.SponsorBlock = v, "Пропускать рекламные вставки авторов (выделяются жирной зелёной полосой на таймлайне)");
        var cr = Row("Что пропускать", null, 50, 560);
        if (Visible(cr.Y, cr.H))
        {
            float cx = cr.Right - 14;
            for (int ci = Cats.Length - 1; ci >= 0; ci--)
            {
                var (key, name) = Cats[ci];
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
        ToggleRow("rmb", "Правая кнопка мыши ставит паузу", s.RightClickPause, v => { s.RightClickPause = v; Shell.PushPlayerState(); }, "Выключено: правая кнопка по видео открывает меню");
        ToggleRow("rd", "Показывать дизлайки", s.ReturnDislikes, v => s.ReturnDislikes = v, "Return YouTube Dislike (в обычных видео и Shorts)");
        ToggleRow("hs", "Скрывать Shorts в лентах", s.HideShorts, v => s.HideShorts = v);
        ToggleRow("mw", "Отмечать просмотренное в истории YouTube", s.MarkWatched, v => s.MarkWatched = v, "Рекомендации учитывают ваши просмотры");
        ToggleRow("ap", "Автовоспроизведение следующего", s.Autoplay, v => s.Autoplay = v);

        Section("Поиск");
        ToggleRow("strict", "Только по теме", s.SearchStrict, v => s.SearchStrict = v, "В выдаче остаются видео, где слова запроса есть в названии или у канала");
        var br = Row("Скрытые каналы", s.BlockedChannels.Length == 0 ? "Скрыть канал можно из меню любого видео (правый клик)" : $"Скрыто каналов: {s.BlockedChannels.Length}. Они не появляются в поиске и рекомендациях.", 58, 170);
        if (Visible(br.Y, br.H) && s.BlockedChannels.Length > 0)
        {
            var bw = UI.PillWidth(null, "Вернуть все");
            if (UI.Pill(UI.Id("set:unblock"), br.Right - bw - 14, br.Y + 12, 34, null, "Вернуть все", out _)) { s.BlockedChannels = []; Saved(); }
        }

        Section("Загрузки");
        var dr = Row("Папка", s.DownloadDir, 58, 240);
        if (Visible(dr.Y, dr.H))
        {
            float bx = dr.Right - 14;
            var ow = UI.PillWidth(null, "Открыть"); bx -= ow;
            if (UI.Pill(UI.Id("set:odir"), bx, dr.Y + 12, 34, null, "Открыть", out _))
            {
                try { Directory.CreateDirectory(s.DownloadDir); W.Open(s.DownloadDir); }
                catch (Exception e) { Shell.Toast("Не удалось открыть папку: " + e.Message, Th.Warn); }
            }
            var pw = UI.PillWidth(null, "Выбрать…"); bx -= pw + 8;
            if (UI.Pill(UI.Id("set:pdir"), bx, dr.Y + 12, 34, null, "Выбрать…", out _))
                App.Post(() => Dialogs.PickFolderAsync("Папка для загрузок", d => { s.DownloadDir = d; Saved(); }));
        }
        ChoiceRow("dq", "Качество по умолчанию", s.DownloadQuality, AppSettings.Qualities, v => s.DownloadQuality = v);
        ToggleRow("sbd", "Вырезать спонсорские вставки из скачанных файлов", s.SponsorBlockOnDownload, v => s.SponsorBlockOnDownload = v);

        Section("Кэш просмотренного");
        if (UI.Now - _cacheAt > 2000) { _cacheAt = UI.Now; ThreadPool.QueueUserWorkItem(_ => { _cacheBytes = MediaCache.TotalBytes(); App.WakeUi(); }); }
        var lr = Row("Лимит на диске", "Часто включаемое (музыка) хранится долго; просмотренное один раз удаляется через неделю, а при переполнении уходит первым.", 58, 170);
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
        var ur2 = Row("Сейчас занято", $"{Fmt.Size(_cacheBytes)} из {CacheName(s.CacheLimitMb)}. Потоки хранятся как их отдаёт YouTube (AV1/VP9 и Opus).", 58, 150);
        if (Visible(ur2.Y, ur2.H))
        {
            var cw2 = UI.PillWidth(null, "Очистить");
            if (UI.Pill(UI.Id("set:cclear"), ur2.Right - cw2 - 14, ur2.Y + 12, 34, null, "Очистить", out _))
                ThreadPool.QueueUserWorkItem(_ => { MediaCache.Clear(); _cacheAt = 0; App.WakeUi(); });
        }
        ToggleRow("qs", "Быстрый старт нового видео", s.QuickStart, v => s.QuickStart = v, "Прямой запрос потока без ожидания yt-dlp");

        Section("Сеть");
        ToggleRow("imp", "Отпечаток Chrome при загрузке с других сайтов", s.Impersonate, v => s.Impersonate = v, "TLS и HTTP/2 как у настоящего браузера (curl_cffi)");
        EditInRow(_proxy, Row("Прокси", null, 50, 410), 380);
        EditInRow(_hl, Row("Язык YouTube", null, 50, 160), 120);
        EditInRow(_gl, Row("Регион", null, 50, 160), 120);
        var ur = Row("Движок загрузки", _version, 58, 210);
        if (Visible(ur.Y, ur.H))
        {
            var uw = UI.PillWidth(null, _updating ? "Обновляю…" : "Обновить сейчас");
            if (UI.Pill(UI.Id("set:upd"), ur.Right - uw - 14, ur.Y + 12, 34, null, _updating ? "Обновляю…" : "Обновить сейчас", out _) && !_updating)
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
                        App.Post(() => Shell.Toast("yt-dlp обновлён", Th.Good));
                    }
                    catch (Exception ex)
                    {
                        App.Post(() => Shell.Toast("Ошибка обновления yt-dlp: " + ex.Message, Th.Warn));
                    }
                    finally
                    {
                        App.Post(() => { _updating = false; App.Invalidate(); });
                    }
                });
            }
        }

        Section("Обновления");
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
        }

        Section("Данные");
        var da = Row("Папка данных и журнал", Paths.Data, 58, 280);
        if (Visible(da.Y, da.H))
        {
            float bx = da.Right - 14;
            var l1 = UI.PillWidth(null, "Журнал"); bx -= l1;
            if (UI.Pill(UI.Id("set:log"), bx, da.Y + 12, 34, null, "Журнал", out _)) W.Open(Path.Combine(Paths.Logs, "app.log"));
            var l2 = UI.PillWidth(null, "Открыть папку"); bx -= l2 + 8;
            if (UI.Pill(UI.Id("set:data"), bx, da.Y + 12, 34, null, "Открыть папку", out _)) W.Open(Paths.Data);
        }

        if (UI.Now - _statsAt > 1000)
        {
            _statsAt = UI.Now;
            using var proc = Process.GetCurrentProcess();
            _stats = $"Кадр {App.LastFrameMs:0.00} мс · память {proc.WorkingSet64 >> 20} МБ · превью на GPU {Images.GpuBytes >> 20} МБ";
        }
        if (Visible(_y + 10, 20)) Gfx.Draw(_stats, _x, _y + 10, 12, Th.Dim);
        var needH = _y + 50 + scroll - r.Y;
        if (MathF.Abs(needH - _contentH) > 1f) { _contentH = needH; UI.Animate(); }
        UI.EndScroll();
    }
}
