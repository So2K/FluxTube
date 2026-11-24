using System.Text.Json.Nodes;
using FluxTube.Models;
using FluxTube.Pages;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Multi-account manager & top-right account menu:
/// - Remembers multiple YouTube accounts in %USERPROFILE%\.fluxtube\accounts\ (each with its own cookies file).
/// - Supports instant 1-click switching between saved accounts without re-entering passwords.
/// - Sub-pages inside the dropdown: saved accounts, theme, language.
/// </summary>
public static class AccountMenu
{
    public sealed class SavedAccount
    {
        public required string Id;
        public string Name = "";
        public string Handle = "";
        public string? Photo;
        public string? ChannelId;
    }

    public static Account? Current { get; private set; }
    public static bool Open { get; private set; }
    /// <summary>Where the panel was drawn last frame: input outside it is blocked while it is open.</summary>
    public static R Rect { get; private set; }

    private static readonly List<SavedAccount> _saved = [];
    private static string? _activeId;
    public static IReadOnlyList<SavedAccount> Saved => _saved;
    public static string? ActiveId => _activeId;

    private static int _refreshGen;
    private static string _page = "";      // "" main, "accounts", "theme", "lang"
    private static float _mainH = 380;     // measured while drawing, used for the next frame
    private static long _openedAt, _pageAt;
    private static float _anchorRight, _anchorTop;
    private static readonly string File_ = Path.Combine(Paths.Data, "account.json");
    private static readonly string AccountsDir = Path.Combine(Paths.Data, "accounts");
    private static readonly string IndexFile = Path.Combine(AccountsDir, "index.json");

    private static readonly (string Code, string Name)[] Langs =
        [("ru", "Русский"), ("en", "English"), ("uk", "Українська"), ("be", "Беларуская"), ("kk", "Қазақ тілі"), ("de", "Deutsch"),
         ("es", "Español"), ("fr", "Français"), ("pt", "Português"), ("ja", "日本語")];

    private static string CookiePath(string id) => Path.Combine(AccountsDir, id + ".cookies.txt");

    private static string MakeId(Account a)
    {
        var raw = !string.IsNullOrWhiteSpace(a.ChannelId) ? a.ChannelId
            : !string.IsNullOrWhiteSpace(a.Handle) ? a.Handle
            : a.Name;
        var clean = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        return clean.Length > 0 ? clean : "acc_" + Math.Abs(raw.GetHashCode());
    }

    // ---------------- persistence & multi-account switching ----------------

    public static void LoadCached()
    {
        _saved.Clear();
        try
        {
            if (File.Exists(IndexFile) && JsonNode.Parse(File.ReadAllText(IndexFile)) is JsonObject root)
            {
                _activeId = root.S("active");
                if (root["items"] is JsonArray arr)
                {
                    foreach (var n in arr)
                    {
                        var id = n.S("id");
                        if (string.IsNullOrEmpty(id) || !File.Exists(CookiePath(id))) continue;
                        _saved.Add(new SavedAccount
                        {
                            Id = id,
                            Name = n.S("n") ?? "",
                            Handle = n.S("h") ?? "",
                            Photo = n.S("p"),
                            ChannelId = n.S("c"),
                        });
                    }
                }
            }
        }
        catch { }

        try
        {
            if (!CookieStore.IsLoggedIn || !File.Exists(File_)) return;
            var n = JsonNode.Parse(File.ReadAllText(File_));
            Current = new Account { Name = n.S("n") ?? "", Handle = n.S("h") ?? "", Photo = n.S("p"), ChannelId = n.S("c") };
            if (Current != null) SaveCurrentToVault(Current);
        }
        catch { }
    }

    private static void WriteIndex()
    {
        try
        {
            Directory.CreateDirectory(AccountsDir);
            var arr = new JsonArray();
            foreach (var a in _saved)
            {
                arr.Add(new JsonObject
                {
                    ["id"] = a.Id, ["n"] = a.Name, ["h"] = a.Handle, ["p"] = a.Photo, ["c"] = a.ChannelId,
                });
            }
            var root = new JsonObject { ["active"] = _activeId, ["items"] = arr };
            File.WriteAllText(IndexFile, root.ToJsonString());
        }
        catch (Exception e) { Paths.Log("accounts index: " + e.Message); }
    }

    private static void SaveCurrentToVault(Account a)
    {
        if (!CookieStore.IsLoggedIn || !File.Exists(Paths.Cookies)) return;
        try
        {
            Directory.CreateDirectory(AccountsDir);
            var id = MakeId(a);
            File.Copy(Paths.Cookies, CookiePath(id), overwrite: true);
            var existing = _saved.FirstOrDefault(x => x.Id == id);
            if (existing == null)
            {
                existing = new SavedAccount { Id = id };
                _saved.Add(existing);
            }
            existing.Name = a.Name;
            existing.Handle = a.Handle;
            existing.Photo = a.Photo;
            existing.ChannelId = a.ChannelId;
            _activeId = id;
            WriteIndex();
        }
        catch (Exception e) { Paths.Log("account vault: " + e.Message); }
    }

    /// <summary>1-click switch to another saved YouTube account.</summary>
    public static void SwitchTo(string id)
    {
        var target = _saved.FirstOrDefault(x => x.Id == id);
        if (target == null || !File.Exists(CookiePath(id))) return;
        try
        {
            // Persist any rotated cookies of the outgoing active account first
            if (_activeId != null && _activeId != id && CookieStore.IsLoggedIn && File.Exists(Paths.Cookies))
                try { File.Copy(Paths.Cookies, CookiePath(_activeId), overwrite: true); } catch { }

            File.Copy(CookiePath(id), Paths.Cookies, overwrite: true);
            _activeId = id;
            WriteIndex();
            Current = new Account { Name = target.Name, Handle = target.Handle, Photo = target.Photo, ChannelId = target.ChannelId };
            try
            {
                File.WriteAllText(File_, new JsonObject
                {
                    ["n"] = target.Name, ["h"] = target.Handle, ["p"] = target.Photo, ["c"] = target.ChannelId,
                }.ToJsonString());
            }
            catch { }

            InnerTube.Instance.ResetSession();
            Shell.OnAccountChanged();
            Shell.Toast($"Аккаунт: {(target.Name.Length > 0 ? target.Name : target.Handle)}", Th.Good, 2800);
            Shell.OpenRoot("home");
        }
        catch (Exception e)
        {
            Paths.Log("account switch: " + e.Message);
            Shell.Toast("Не удалось переключить аккаунт: " + e.Message, Th.Warn);
        }
    }

    public static void RemoveSaved(string id)
    {
        var idx = _saved.FindIndex(x => x.Id == id);
        if (idx < 0) return;
        var wasActive = _activeId == id && CookieStore.IsLoggedIn;
        _saved.RemoveAt(idx);
        try { if (File.Exists(CookiePath(id))) File.Delete(CookiePath(id)); } catch { }
        if (_activeId == id) _activeId = null;
        WriteIndex();
        if (wasActive) Shell.Logout();
        else App.Invalidate();
    }

    /// <summary>Asks YouTube who is signed in (after start-up and after every sign-in or sign-out).</summary>
    public static void Refresh()
    {
        if (!CookieStore.IsLoggedIn)
        {
            _refreshGen++;
            Current = null;
            _activeId = null;
            WriteIndex();
            try { File.Delete(File_); } catch { }
            App.Invalidate();
            return;
        }
        var gen = ++_refreshGen;
        _ = Task.Run(async () =>
        {
            try
            {
                var a = await InnerTube.Instance.AccountAsync();
                if (a == null) return;
                App.Post(() =>
                {
                    if (gen != _refreshGen || !CookieStore.IsLoggedIn) return;
                    Current = a;
                    try { File.WriteAllText(File_, new JsonObject { ["n"] = a.Name, ["h"] = a.Handle, ["p"] = a.Photo, ["c"] = a.ChannelId }.ToJsonString()); } catch { }
                    SaveCurrentToVault(a);
                    App.Invalidate();
                });
            }
            catch (Exception e) { Paths.Log("account: " + e.Message); }
        });
    }

    // ---------------- the panel ----------------

    public static void Toggle(float anchorRight, float anchorTop)
    {
        if (Open) { Close(); return; }
        _anchorRight = anchorRight; _anchorTop = anchorTop;
        _page = ""; _openedAt = _pageAt = UI.Now;
        Open = true;
        App.Invalidate();
    }

    public static void Close()
    {
        if (!Open) return;
        Open = false;
        Rect = default;
        App.Invalidate();
    }

    private static void Go(string page) { _page = page; _pageAt = UI.Now; App.Invalidate(); }

    /// <summary>Called at the very end of the frame, above everything.</summary>
    public static void Draw()
    {
        if (!Open) return;
        const float w = 310;
        var h = _page switch
        {
            "accounts" => 54 + _saved.Count * 54 + (_saved.Count > 0 ? 10 : 30) + 126,
            "theme" => 52 + (Themes.All.Length + 1) * 38 + 10,
            "lang" => 52 + Langs.Length * 38 + 10,
            _ => _mainH,
        };
        h = MathF.Min(h, Gfx.H - _anchorTop - 20);
        var t = Math.Clamp((UI.Now - _openedAt) / 140f, 0, 1);
        if (t < 1) UI.Animate();
        var ease = 1 - (1 - t) * (1 - t);
        var r = new R(Gfx.Snap(_anchorRight - w), Gfx.Snap(_anchorTop + (1 - ease) * -8), w, h);
        Rect = r;

        Gfx.Fill(new R(r.X + 2, r.Y + 6, r.W - 4, r.H), Gfx.Fade(0xFF000000, 0.3f * ease), 14);
        Gfx.Fill(r, Gfx.Fade(Th.Menu, ease), 14);
        Gfx.Stroke(r, Gfx.Fade(Th.Line, ease), 14);
        // a sub-page slides in from the right, the main page from the left
        var pt = Math.Clamp((UI.Now - _pageAt) / 150f, 0, 1);
        if (pt < 1) UI.Animate();
        var slide = (1 - pt) * (1 - pt) * (_page == "" ? -18 : 18);
        Gfx.PushClip(r);
        var a = ease * pt;
        switch (_page)
        {
            case "accounts": AccountsPage(r, slide, a); break;
            case "theme": ThemePage(r, slide, a); break;
            case "lang": LangPage(r, slide, a); break;
            default: MainPage(r, slide, a); break;
        }
        Gfx.PopClip();

        // a click anywhere else closes it
        if ((UI.Pressed || UI.RightClicked) && !r.Contains(UI.MX, UI.MY)) Close();
    }

    private static void MainPage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 14;
        var acc = Current;
        if (acc != null)
        {
            Cards.Avatar(x + 16, y + 2, 40, acc.Photo, acc.Name);
            Gfx.Draw(acc.Name, x + 70, y, 15, Gfx.Fade(Th.Text, a), 600, r.W - 90);
            Gfx.Draw(acc.Handle, x + 70, y + 21, 13.5f, Gfx.Fade(Th.Text, a), 400, r.W - 90);
            if (acc.ChannelId != null)
            {
                var link = Gfx.Text("Перейти на канал", 13.5f, 500);
                var lr = new R(x + 70, y + 46, link.W, link.H);
                var over = UI.Hover(lr);
                Gfx.Draw(link, lr.X, lr.Y, Gfx.Fade(over ? Gfx.Mix(Th.Brand, Th.Text, 0.3f) : Th.Brand, a));
                if (over) Gfx.Fill(new R(lr.X, lr.Bottom - 1, lr.W, 1), Th.Brand);
                if (UI.Click(UI.Id("am:chan"), lr)) { var id = acc.ChannelId; var n = acc.Name; Close(); App.Post(() => Shell.OpenChannel(id, n)); }
            }
            y += 80;
        }
        else if (CookieStore.IsLoggedIn)
        {
            Cards.Avatar(x + 16, y + 2, 40, null, "?");
            Gfx.Draw("Вы вошли в YouTube", x + 70, y + 4, 15, Th.Text, 600);
            Gfx.Draw("Загружаю профиль…", x + 70, y + 25, 13, Th.Sub);
            y += 62;
        }
        else
        {
            var def = BrowserLogin.DetectDefault();
            Gfx.Draw("Вы не вошли", x + 16, y, 15, Th.Text, 600);
            Gfx.Draw($"Вход подхватывается прямо из {def.Name} — без повторного ввода пароля", x + 16, y + 22, 12.5f, Th.Sub, 400, r.W - 32, 2);
            if (UI.Pill(UI.Id("am:login"), x + 16, y + 58, 34, Th.Globe, $"Войти через {def.Name}", out _, accent: true)) { Close(); App.Post(Shell.Login); }
            y += 104;
        }
        Sep(r, ref y);
        var accLabel = _saved.Count > 1 ? $"Сменить аккаунт ({_saved.Count})" : "Аккаунты и вход";
        if (Item(r, x, ref y, "", accLabel, null, true)) Go("accounts");
        if (CookieStore.IsLoggedIn)
        {
            if (Item(r, x, ref y, "", "Выйти (гостевой режим)", null, false)) { Close(); App.Post(Shell.Logout); }
            Sep(r, ref y);
            if (Item(r, x, ref y, "", "Творческая студия YouTube", null, false)) { Close(); W.Open("https://studio.youtube.com/"); }
            Sep(r, ref y);
        }
        else
        {
            if (Item(r, x, ref y, Th.Globe, "Открыть YouTube в браузере", null, false)) { Close(); W.Open("https://www.youtube.com/"); }
            Sep(r, ref y);
        }
        if (Item(r, x, ref y, "", "Тема: " + Themes.Current.Name, null, true)) Go("theme");
        var lang = Langs.FirstOrDefault(l => l.Code == AppSettings.Current.Hl).Name ?? AppSettings.Current.Hl;
        if (Item(r, x, ref y, "", "Язык YouTube: " + lang, null, true)) Go("lang");
        Sep(r, ref y);
        if (Item(r, x, ref y, Th.Settings, "Настройки", null, false)) { Close(); App.Post(() => Shell.OpenRoot("settings")); }
        var need = y + 8 - r.Y;
        if (MathF.Abs(need - _mainH) > 0.5f) { _mainH = need; UI.Animate(); }
    }

    private static void AccountsPage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 8;
        Header(r, x, ref y, "Аккаунты YouTube");
        if (_saved.Count == 0)
        {
            Gfx.Draw("Пока нет сохранённых аккаунтов", x + 16, y + 4, 13, Th.Sub);
            y += 30;
        }
        else
        {
            foreach (var acc in _saved.ToArray())
            {
                var row = new R(r.X, y, r.W, 52);
                var active = CookieStore.IsLoggedIn && acc.Id == _activeId;
                var over = UI.Hover(row);
                if (active) Gfx.Fill(row.Inset(6, 2), Th.A(0.08f), 8);
                UI.Glow(UI.Id("am:acc:" + acc.Id), row.Inset(6, 2), 8, over);
                Cards.Avatar(x + 16, y + 8, 36, acc.Photo, acc.Name.Length > 0 ? acc.Name : acc.Handle);
                Gfx.Draw(acc.Name.Length > 0 ? acc.Name : "Аккаунт", x + 62, y + 8, 13.5f, Gfx.Fade(Th.Text, a), active ? 700 : 500, r.W - 118);
                Gfx.Draw(acc.Handle, x + 62, y + 27, 12, Gfx.Fade(Th.Sub, a), 400, r.W - 118);

                var delR = new R(r.Right - 36, y + 14, 24, 24);
                bool delClicked = false;
                if (over || !active)
                {
                    if (active) Gfx.Icon(Th.Check, new R(r.Right - 60, y, 22, 52), 12, Th.Brand);
                    if (over && UI.IconButton(UI.Id("am:accdel:" + acc.Id), delR, Th.Close, 9.5f, Th.Sub))
                    {
                        delClicked = true;
                        var rid = acc.Id;
                        App.Post(() => RemoveSaved(rid));
                    }
                }
                else if (active)
                {
                    Gfx.Icon(Th.Check, new R(r.Right - 36, y, 22, 52), 12, Th.Brand);
                }

                if (!delClicked && !active && UI.Click(UI.Id("am:acc:" + acc.Id), row))
                {
                    var tid = acc.Id;
                    Close();
                    App.Post(() => SwitchTo(tid));
                }
                y += 54;
            }
            Sep(r, ref y);
        }

        if (Item(r, x, ref y, Th.Add, "Добавить аккаунт (браузер)…", null, false))
        {
            Close();
            var list = BrowserLogin.Supported.Select(b => new MenuItem(b.Name, () => BrowserLogin.ImportFromBrowser(b), icon: Th.Globe)).ToList();
            list.Add(MenuItem.Sep);
            list.Add(new("Импорт cookies.txt…", () => { if (Dialogs.PickFile("cookies.txt\0*.txt\0\0") is { } f) BrowserLogin.ImportFromFile(f); }, icon: Th.Folder));
            Menus.Popup(list);
        }
        if (Item(r, x, ref y, Th.OpenNew, "Добавить через окно входа", null, false)) { Close(); App.Post(Shell.LoginWebView); }
        if (Item(r, x, ref y, Th.Globe, "Открыть YouTube в браузере", null, false)) { Close(); W.Open("https://www.youtube.com/"); }
    }

    private static void ThemePage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 8;
        Header(r, x, ref y, "Тема");
        foreach (var t in Themes.All)
        {
            var p = Themes.Preview(t);
            var row = new R(r.X, y, r.W, 38);
            UI.Glow(UI.Id("am:t:" + t.Name), row.Inset(6, 1), 8, UI.Hover(row));
            // three dots of the palette: background, card, accent
            Gfx.Circle(x + 24, y + 19, 8, p.Bg);
            Gfx.Stroke(new R(x + 16, y + 11, 16, 16), Th.A(0.3f), 8);
            Gfx.Circle(x + 40, y + 19, 6, p.Card);
            Gfx.Circle(x + 54, y + 19, 6, p.Brand);
            var on = Themes.Current.Name == t.Name;
            Gfx.Draw(t.Name, x + 72, y + 9, 14, Gfx.Fade(Th.Text, a), on ? 600 : 400, r.W - 110);
            if (on) Gfx.Icon(Th.Check, new R(r.Right - 40, y, 30, 38), 12, Th.Text);
            if (UI.Click(UI.Id("am:t:" + t.Name), row) && !on) { var n = t.Name; App.Post(() => Themes.Apply(theme: n)); }
            y += 38;
        }
        if (Item(r, x, ref y, Th.Settings, "Цвет акцента и все темы…", null, false)) { Close(); App.Post(() => Shell.OpenRoot("settings")); }
    }

    private static void LangPage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 8;
        Header(r, x, ref y, "Язык YouTube");
        foreach (var (code, name) in Langs)
        {
            var on = AppSettings.Current.Hl == code;
            var row = new R(r.X, y, r.W, 38);
            UI.Glow(UI.Id("am:l:" + code), row.Inset(6, 1), 8, UI.Hover(row));
            if (on) Gfx.Icon(Th.Check, new R(x + 12, y, 24, 38), 12, Th.Text);
            Gfx.Draw(name, x + 48, y + 9, 14, Gfx.Fade(Th.Text, a), on ? 600 : 400);
            if (UI.Click(UI.Id("am:l:" + code), row) && !on)
            {
                var c = code;
                Close();
                App.Post(() =>
                {
                    AppSettings.Current.Hl = c;
                    AppSettings.Current.Save();
                    InnerTube.Instance.ResetSession();
                    Shell.OnAccountChanged();
                    Shell.Toast("Названия и ленты будут на языке: " + Langs.First(l => l.Code == c).Name);
                });
            }
            y += 38;
        }
    }

    private static void Header(R r, float x, ref float y, string title)
    {
        if (UI.IconButton(UI.Id("am:back"), new R(x + 8, y + 2, 36, 36), Th.Back, 13)) Go("");
        Gfx.Draw(title, x + 52, y + 10, 15, Th.Text, 600);
        y += 44;
        Gfx.Fill(new R(r.X, y - 2, r.W, 1), Th.Line);
        y += 6;
    }

    private static void Sep(R r, ref float y)
    {
        Gfx.Fill(new R(r.X, y + 4, r.W, 1), Th.Line);
        y += 9;
    }

    private static bool Item(R r, float x, ref float y, string glyph, string text, string? hint, bool sub)
    {
        var row = new R(r.X, y, r.W, 40);
        var id = UI.Id("am:" + text);
        UI.Glow(id, row.Inset(6, 1), 8, UI.Hover(row));
        Gfx.Icon(glyph, new R(x + 12, y, 28, 40), 16, Th.Text);
        Gfx.Draw(text, x + 52, y + 10, 14, Th.Text, 400, r.W - 90);
        if (sub) Gfx.Icon("", new R(r.Right - 38, y, 28, 40), 11, Th.Sub);
        y += 40;
        return UI.Click(id, row);
    }
}
