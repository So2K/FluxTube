using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluxTube.Models;
using FluxTube.Pages;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Multi-account manager & top-right account menu:
/// - Remembers multiple YouTube accounts in %USERPROFILE%\.fluxtube\accounts\ (each with its own cookies file).
/// - Clean sign-in via the built-in browser login window (add as many accounts as you want) + 1-click swap.
/// - Sub-pages inside the dropdown (with proper scroll support): saved accounts, theme, language.
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
    public static bool SessionExpired { get; private set; }
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
    private static readonly int _scrollAcc = UI.Id("am:scr:acc");
    private static readonly int _scrollTheme = UI.Id("am:scr:theme");
    private static readonly int _scrollLang = UI.Id("am:scr:lang");
    private static readonly string File_ = Path.Combine(Paths.Data, "account.json");
    private static readonly string AccountsDir = Path.Combine(Paths.Data, "accounts");
    private static readonly string IndexFile = Path.Combine(AccountsDir, "index.json");

    private static readonly (string Code, string Name)[] UiLangs = [("ru", "Русский"), ("en", "English")];
    private static readonly (string Code, string Name)[] Langs =
        [("ru", "Русский"), ("en", "English"), ("uk", "Українська"), ("be", "Беларуская"), ("kk", "Қазақ тілі"), ("de", "Deutsch"),
         ("es", "Español"), ("fr", "Français"), ("pt", "Português"), ("ja", "日本語")];

    private static string CookiePath(string id) => Path.Combine(AccountsDir, id + ".cookies.txt");

    /// <summary>Deterministic cross-process account ID (never relies on randomized string.GetHashCode).</summary>
    private static string MakeId(Account a)
    {
        var raw = !string.IsNullOrWhiteSpace(a.ChannelId) ? a.ChannelId
            : !string.IsNullOrWhiteSpace(a.Handle) ? a.Handle
            : a.Name;
        var clean = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (clean.Length >= 3) return clean;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw.Length > 0 ? raw : "yt-account"));
        return "acc_" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
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
                        if (_saved.Any(x => x.Id == id)) continue;
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
            var tmp = IndexFile + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString());
            File.Move(tmp, IndexFile, overwrite: true);
        }
        catch (Exception e) { Paths.Log("accounts index: " + e.Message); }
    }

    /// <summary>Persists the currently active account's cookies to its vault slot before signing into a new account.</summary>
    public static void SaveActiveToVault()
    {
        if (!CookieStore.IsLoggedIn || !File.Exists(Paths.Cookies)) return;
        if (Current != null) { SaveCurrentToVault(Current); return; }
        if (_activeId != null)
        {
            try
            {
                Directory.CreateDirectory(AccountsDir);
                File.Copy(Paths.Cookies, CookiePath(_activeId), overwrite: true);
            }
            catch { }
        }
    }

    private static void SaveCurrentToVault(Account a)
    {
        if (!CookieStore.IsLoggedIn || !File.Exists(Paths.Cookies)) return;
        try
        {
            Directory.CreateDirectory(AccountsDir);
            var id = MakeId(a);
            var tmp = CookiePath(id) + ".tmp";
            File.Copy(Paths.Cookies, tmp, overwrite: true);
            File.Move(tmp, CookiePath(id), overwrite: true);

            // Match by Id, ChannelId, or Handle to avoid duplicates across migrations
            var existing = _saved.FirstOrDefault(x => x.Id == id
                || (!string.IsNullOrEmpty(a.ChannelId) && x.ChannelId == a.ChannelId)
                || (!string.IsNullOrEmpty(a.Handle) && x.Handle == a.Handle));
            if (existing == null)
            {
                existing = new SavedAccount { Id = id };
                _saved.Add(existing);
            }
            else if (existing.Id != id)
            {
                try { if (File.Exists(CookiePath(existing.Id))) File.Delete(CookiePath(existing.Id)); } catch { }
                existing.Id = id;
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

    /// <summary>Instant 1-click switch to another saved YouTube account.</summary>
    public static void SwitchTo(string id)
    {
        var target = _saved.FirstOrDefault(x => x.Id == id);
        if (target == null || !File.Exists(CookiePath(id))) return;
        try
        {
            // Persist any rotated cookies of the outgoing active account first (only if it matches activeId)
            if (_activeId != null && _activeId != id && CookieStore.IsLoggedIn && File.Exists(Paths.Cookies))
            {
                try
                {
                    var outTmp = CookiePath(_activeId) + ".tmp";
                    File.Copy(Paths.Cookies, outTmp, overwrite: true);
                    File.Move(outTmp, CookiePath(_activeId), overwrite: true);
                }
                catch { }
            }

            var inTmp = Paths.Cookies + ".tmp";
            File.Copy(CookiePath(id), inTmp, overwrite: true);
            File.Move(inTmp, Paths.Cookies, overwrite: true);
            Shell.SyncCookieStamp();

            _activeId = id;
            SessionExpired = false;
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
            Shell.Toast(L.T("Аккаунт: ", "Account: ") + (target.Name.Length > 0 ? target.Name : target.Handle), Th.Good, 2800);
            Shell.OpenRoot("home");
        }
        catch (Exception e)
        {
            Paths.Log("account switch: " + e.Message);
            Shell.Toast(L.T("Не удалось переключить аккаунт: ", "Could not switch account: ") + e.Message, Th.Warn);
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

    /// <summary>Asks YouTube who is signed in (after start-up and after every sign-in, switch, or sign-out).</summary>
    public static void Refresh()
    {
        SessionExpired = false;
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
                App.Post(() =>
                {
                    if (gen != _refreshGen || !CookieStore.IsLoggedIn) return;
                    if (a == null)
                    {
                        SessionExpired = true;
                        Current = null;
                        App.Invalidate();
                        return;
                    }
                    SessionExpired = false;
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

    private static void Go(string page)
    {
        _page = page;
        _pageAt = UI.Now;
        UI.ScrollReset(_scrollAcc);
        UI.ScrollReset(_scrollTheme);
        UI.ScrollReset(_scrollLang);
        App.Invalidate();
    }

    /// <summary>Called at the very end of the frame, above everything.</summary>
    public static void Draw()
    {
        if (!Open) return;
        const float w = 324;
        var h = _page switch
        {
            "accounts" => 54 + _saved.Count * 54 + (_saved.Count > 0 ? 12 : 34) + 178,
            "theme" => 52 + (Themes.All.Length + 1) * 38 + 10,
            "uilang" => 52 + UiLangs.Length * 38 + 10,
            "lang" => 52 + Langs.Length * 38 + 10,
            _ => _mainH,
        };
        h = MathF.Min(h, MathF.Max(180, Gfx.H - _anchorTop - 20));
        var t = Math.Clamp((UI.Now - _openedAt) / 140f, 0, 1);
        if (t < 1) UI.Animate();
        var ease = 1 - (1 - t) * (1 - t);
        var r = new R(Gfx.Snap(_anchorRight - w), Gfx.Snap(_anchorTop + (1 - ease) * -8), w, h);
        Rect = r;

        Gfx.Fill(new R(r.X + 2, r.Y + 6, r.W - 4, r.H), Gfx.Fade(0xFF000000, 0.3f * ease), 14);
        Gfx.FillFrosted(r, Gfx.Fade(Th.Menu, ease), 14, opacity: 0.94f, border: true);
        // a sub-page slides in from the right, the main page from the left
        var pt = Math.Clamp((UI.Now - _pageAt) / 150f, 0, 1);
        if (pt < 1) UI.Animate();
        var slide = (1 - pt) * (1 - pt) * (_page == "" ? -18 : 18);
        float slideVel = pt < 1 ? -2f * (1 - pt) * (_page == "" ? -18 : 18) / 0.15f : 0f;
        bool mb = Gfx.PushMotionBlur(r, slideVel, 0f);
        Gfx.PushClip(r);
        var a = ease * pt;
        switch (_page)
        {
            case "accounts": AccountsPage(r, slide, a); break;
            case "theme": ThemePage(r, slide, a); break;
            case "uilang": UiLangPage(r, slide, a); break;
            case "lang": LangPage(r, slide, a); break;
            default: MainPage(r, slide, a); break;
        }
        Gfx.PopClip();
        Gfx.PopMotionBlur(mb);

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
                var link = Gfx.Text(L.T("Перейти на канал", "View your channel"), 13.5f, 500);
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
            Gfx.Draw(SessionExpired ? L.T("Сессия YouTube устарела", "YouTube session expired") : L.T("Вы вошли в YouTube", "Signed in to YouTube"), x + 70, y + 4, 15, Th.Text, 600);
            Gfx.Draw(SessionExpired ? L.T("Войдите снова в 1 клик", "Sign in again in 1 click") : L.T("Загружаю профиль…", "Loading profile…"), x + 70, y + 25, 13, SessionExpired ? Th.Warn : Th.Sub);
            y += 62;
        }
        else
        {
            Gfx.Draw(L.T("Вы не вошли", "Not signed in"), x + 16, y, 15, Th.Text, 600);
            Gfx.Draw(L.T("Войдите в аккаунт Google — подписки, история и рекомендации появятся сразу.", "Sign in to Google for your subscriptions, history, and recommendations."), x + 16, y + 22, 12.5f, Th.Sub, 400, r.W - 32, 3);
            if (UI.Pill(UI.Id("am:login"), x + 16, y + 66, 34, Th.Account, L.T("Войти в YouTube", "Sign in to YouTube"), out _, accent: true)) { Close(); App.Post(LoginModal.Show); }
            y += 110;
        }
        Sep(r, ref y);
        var accLabel = _saved.Count > 1 ? L.T($"Сменить аккаунт ({_saved.Count})", $"Switch account ({_saved.Count})") : L.T("Аккаунты и вход", "Accounts & sign in");
        if (Item(r, x, ref y, "\uE748", accLabel, true)) Go("accounts");
        if (CookieStore.IsLoggedIn)
        {
            if (Item(r, x, ref y, Th.Add, L.T("Добавить ещё аккаунт", "Add another account"), false)) { Close(); App.Post(LoginModal.Show); }
            if (Item(r, x, ref y, "\uE7E8", L.T("Выйти (гостевой режим)", "Sign out (guest mode)"), false)) { Close(); App.Post(Shell.Logout); }
            Sep(r, ref y);
            if (Item(r, x, ref y, "\uE8A7", L.T("Творческая студия YouTube", "YouTube Studio"), false)) { Close(); W.Open("https://studio.youtube.com/"); }
            Sep(r, ref y);
        }
        if (Item(r, x, ref y, "\uE790", L.T("Тема: ", "Theme: ") + Themes.DisplayName(Themes.Current.Name), true)) Go("theme");
        var uiLangName = L.En ? "English" : "Русский";
        if (Item(r, x, ref y, Th.Globe, L.T("Язык интерфейса: ", "App language: ") + uiLangName, true)) Go("uilang");
        var lang = Langs.FirstOrDefault(l => l.Code == AppSettings.Current.Hl).Name ?? AppSettings.Current.Hl;
        if (Item(r, x, ref y, "\uF2B7", L.T("Язык YouTube: ", "YouTube language: ") + lang, true)) Go("lang");
        Sep(r, ref y);
        if (Item(r, x, ref y, Th.Settings, L.T("Настройки", "Settings"), false)) { Close(); App.Post(() => Shell.OpenRoot("settings")); }
        var need = y + 8 - r.Y;
        if (MathF.Abs(need - _mainH) > 0.5f) { _mainH = need; UI.Animate(); }
    }

    private static void AccountsPage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 8;
        Header(r, x, ref y, L.T("Аккаунты YouTube", "YouTube accounts"));
        var view = new R(r.X, y, r.W, MathF.Max(40, r.Bottom - y - 4));
        float totalH = (_saved.Count == 0 ? 34 : _saved.Count * 54 + 12) + 178;
        var scroll = UI.BeginScroll(_scrollAcc, view, totalH);
        y -= scroll;

        if (_saved.Count == 0)
        {
            Gfx.Draw(L.T("Пока нет сохранённых аккаунтов", "No saved accounts yet"), x + 16, y + 6, 13, Th.Sub);
            y += 34;
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
                Gfx.Draw(acc.Name.Length > 0 ? acc.Name : L.T("Аккаунт", "Account"), x + 62, y + 8, 13.5f, Gfx.Fade(Th.Text, a), active ? 700 : 500, r.W - 118);
                Gfx.Draw(acc.Handle, x + 62, y + 27, 12, Gfx.Fade(Th.Sub, a), 400, r.W - 118);

                var delR = new R(r.Right - 36, y + 14, 24, 24);
                bool overDel = over && UI.Hover(delR);
                if (over || !active)
                {
                    if (active) Gfx.Icon(Th.Check, new R(r.Right - 60, y, 22, 52), 12, Th.Brand);
                    if (over && UI.IconButton(UI.Id("am:accdel:" + acc.Id), delR, Th.Close, 9.5f, Th.Sub))
                    {
                        var rid = acc.Id;
                        App.Post(() => RemoveSaved(rid));
                    }
                }
                else if (active)
                {
                    Gfx.Icon(Th.Check, new R(r.Right - 36, y, 22, 52), 12, Th.Brand);
                }

                // Guard against clicking the delete × button accidentally switching accounts
                if (!overDel && !active && UI.Click(UI.Id("am:acc:" + acc.Id), row))
                {
                    var tid = acc.Id;
                    Close();
                    App.Post(() => SwitchTo(tid));
                }
                y += 54;
            }
            Sep(r, ref y);
        }

        if (Item(r, x, ref y, Th.Account, L.T("Войти / добавить аккаунт…", "Sign in / add account…"), false)) { Close(); App.Post(LoginModal.Show); }
        if (BrowserLogin.HasFirefox)
        {
            if (Item(r, x, ref y, Th.Globe, L.T("Импорт из Firefox (1 клик)", "Import from Firefox (1 click)"), false)) { Close(); App.Post(BrowserLogin.ImportFromFirefox); }
        }
        if (Item(r, x, ref y, Th.Copy, L.T("Вставить cookies из буфера", "Paste cookies from clipboard"), false)) { Close(); App.Post(BrowserLogin.ImportFromClipboard); }
        if (Item(r, x, ref y, Th.Folder, L.T("Импорт из cookies.txt…", "Import from cookies.txt…"), false))
        {
            Close();
            App.Post(() => Dialogs.PickFileAsync("cookies.txt\0*.txt;*.json\0\0", BrowserLogin.ImportFromFile));
        }
        UI.EndScroll();
    }

    private static void ThemePage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 8;
        Header(r, x, ref y, L.T("Тема", "Theme"));
        var view = new R(r.X, y, r.W, MathF.Max(40, r.Bottom - y - 4));
        float totalH = Themes.All.Length * 38 + 44;
        var scroll = UI.BeginScroll(_scrollTheme, view, totalH);
        y -= scroll;

        foreach (var t in Themes.All)
        {
            if (y + 38 >= view.Y - 4 && y <= view.Bottom + 4)
            {
                var p = Themes.Preview(t);
                var row = new R(r.X, y, r.W, 38);
                UI.Glow(UI.Id("am:t:" + t.Name), row.Inset(6, 1), 8, UI.Hover(row));
                Gfx.Circle(x + 24, y + 19, 8, p.Bg);
                Gfx.Stroke(new R(x + 16, y + 11, 16, 16), Th.A(0.3f), 8);
                Gfx.Circle(x + 40, y + 19, 6, p.Card);
                Gfx.Circle(x + 54, y + 19, 6, p.Brand);
                var on = Themes.Current.Name == t.Name;
                Gfx.Draw(Themes.DisplayName(t.Name), x + 72, y + 9, 14, Gfx.Fade(Th.Text, a), on ? 600 : 400, r.W - 110);
                if (on) Gfx.Icon(Th.Check, new R(r.Right - 40, y, 30, 38), 12, Th.Text);
                if (UI.Click(UI.Id("am:t:" + t.Name), row) && !on) { var n = t.Name; App.Post(() => Themes.Apply(theme: n)); }
            }
            y += 38;
        }
        if (Item(r, x, ref y, Th.Settings, L.T("Цвет акцента и все темы…", "Accent color & all themes…"), false)) { Close(); App.Post(() => Shell.OpenRoot("settings")); }
        UI.EndScroll();
    }

    private static void UiLangPage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 8;
        Header(r, x, ref y, L.T("Язык интерфейса", "App language"));
        foreach (var (code, name) in UiLangs)
        {
            var on = L.Code == code;
            var row = new R(r.X, y, r.W, 38);
            UI.Glow(UI.Id("am:uil:" + code), row.Inset(6, 1), 8, UI.Hover(row));
            if (on) Gfx.Icon(Th.Check, new R(x + 12, y, 24, 38), 12, Th.Brand);
            Gfx.Draw(name, x + 48, y + 9, 14, Gfx.Fade(Th.Text, a), on ? 600 : 400);
            if (UI.Click(UI.Id("am:uil:" + code), row) && !on)
            {
                var c = code;
                Close();
                App.Post(() =>
                {
                    AppSettings.Current.UiLanguage = c;
                    AppSettings.Current.Save();
                    Shell.OnLanguageChanged();
                    Shell.Toast(c == "en" ? "App language: English" : "Язык интерфейса: Русский", Th.Good, 2200);
                });
            }
            y += 38;
        }
    }

    private static void LangPage(R r, float slide, float a)
    {
        float x = r.X + slide, y = r.Y + 8;
        Header(r, x, ref y, L.T("Язык YouTube", "YouTube language"));
        var view = new R(r.X, y, r.W, MathF.Max(40, r.Bottom - y - 4));
        float totalH = Langs.Length * 38 + 8;
        var scroll = UI.BeginScroll(_scrollLang, view, totalH);
        y -= scroll;

        foreach (var (code, name) in Langs)
        {
            if (y + 38 >= view.Y - 4 && y <= view.Bottom + 4)
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
                        Shell.Toast(L.T("Названия и ленты будут на языке: ", "YouTube feeds & titles switched to: ") + Langs.First(l => l.Code == c).Name);
                    });
                }
            }
            y += 38;
        }
        UI.EndScroll();
    }

    private static void Header(R r, float x, ref float y, string title)
    {
        if (UI.IconButton(UI.Id("am:back"), new R(x + 8, y + 2, 36, 36), Th.Back, 13)) { Sfx.Back(); Go(""); }
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

    private static bool Item(R r, float x, ref float y, string glyph, string text, bool sub)
    {
        var row = new R(r.X, y, r.W, 40);
        var id = UI.Id("am:" + text);
        UI.Glow(id, row.Inset(6, 1), 8, UI.Hover(row));
        Gfx.Icon(glyph, new R(x + 12, y, 28, 40), 16, Th.Text);
        Gfx.Draw(text, x + 52, y + 10, 14, Th.Text, 400, r.W - 90);
        if (sub) Gfx.Icon(Th.ChevronRight, new R(r.Right - 38, y, 28, 40), 11, Th.Sub);
        y += 40;
        return UI.Click(id, row);
    }
}
