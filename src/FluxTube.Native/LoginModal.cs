using FluxTube.Pages;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Clean, modern 1-click frosted-glass Sign-In Modal:
/// - Primary big CTA launches an isolated app-mode Google sign-in window (no tabs, no Chrome locks, auto-closes &amp; saves).
/// - Live status progress with "Focus window", "Done", and "Cancel" buttons while signing in.
/// - Smart optional 1-click Firefox/Zen direct SQLite import (when detected) + universal clipboard/file cookies importer.
/// </summary>
public static class LoginModal
{
    public static bool Open { get; private set; }
    public static R Rect { get; private set; }

    private static long _openedAt;
    private static bool _verification;
    private static string? _verifyVideoId;

    public static void Show() => Show(false, null, false);

    public static void Show(bool verification = false, string? verifyVideoId = null, bool autoStart = false)
    {
        AccountMenu.Close();
        Menus.Close();
        SharePanel.Close();
        ThemeCodeEditor.Close();
        AudioStudioPanel.Close();
        PreviewPlayer.HideNow();

        _verification = verification;
        _verifyVideoId = verifyVideoId;
        BrowserLogin.ResetUiState();
        Open = true;
        _openedAt = UI.Now;
        Sfx.Tab();
        App.Invalidate();

        if (autoStart)
            App.Post(() => BrowserLogin.StartGoogleSignIn(verification, verifyVideoId));
    }

    public static void Close()
    {
        if (!Open) return;
        if (BrowserLogin.IsBusy) BrowserLogin.CancelAuth();
        Open = false;
        Rect = default;
        Sfx.Back();
        App.Invalidate();
    }

    public static void Draw()
    {
        if (!Open) { Rect = default; return; }

        float t = Math.Clamp((UI.Now - _openedAt) / 260f, 0f, 1f);
        if (t < 1f) UI.Animate();
        float inv = 1f - t;
        float ease = 1f - inv * inv * inv * inv;

        float w = Gfx.W, h = Gfx.H;
        Gfx.Fill(new R(0, 0, w, h), Gfx.Fade(0xFF000000, (Th.Light ? 0.28f : 0.56f) * ease));

        var phase = BrowserLogin.Phase;
        var ff = phase == LoginPhase.Idle ? BrowserLogin.DetectFirefoxSession() : null;
        bool showSaved = phase == LoginPhase.Idle && AccountMenu.Saved.Count > 0 && !CookieStore.IsLoggedIn;

        float cardW = MathF.Min(460, w - 40);
        float cardH = phase switch
        {
            LoginPhase.Launching or LoginPhase.WaitingForUser or LoginPhase.Verifying => 270,
            LoginPhase.Success => 226,
            LoginPhase.Error => 276,
            _ => 256 + (ff != null ? 48 : 0) + (showSaved ? 56 : 0),
        };
        float dy = (1f - ease) * 14f;
        var card = new R(Gfx.Snap((w - cardW) / 2), Gfx.Snap((h - cardH) / 2 + dy), cardW, cardH);
        Rect = card;

        float mbVelY = t < 1 ? -2f * (1f - t) * 14f / 0.15f * 8f : 0f;
        bool mb = Gfx.PushMotionBlur(card.Inset(-14), 0f, mbVelY);

        var savedModal = UI.Modal;
        UI.Modal = card;

        Gfx.Fill(card.Inset(-4), Gfx.Fade(0xFF000000, 0.36f * ease), 20);
        Gfx.FillFrosted(card, Th.Panel, 16, opacity: 0.96f, border: true);

        float pad = 22, x = card.X + pad, innerW = card.W - pad * 2, y = card.Y + 18;

        // Top close button
        if (UI.IconButton(UI.Id("lm:close"), new R(card.Right - 46, y - 4, 32, 32), Th.Close, 12, Th.Sub))
        {
            UI.Modal = savedModal;
            Gfx.PopMotionBlur(mb);
            Close();
            return;
        }

        // Header badge + Title
        var logoBadge = new R(x, y, 36, 36);
        Gfx.Fill(logoBadge, Gfx.Fade(Th.Brand, 0.18f), 10);
        Gfx.Icon(Th.Account, logoBadge, 17, Th.Brand);

        var heading = _verification
            ? L.T("Проверка YouTube", "YouTube Verification")
            : L.T("Вход в YouTube", "Sign in to YouTube");
        var subheading = _verification
            ? L.T("Подтвердите сессию, чтобы продолжить просмотр", "Confirm your session to continue watching")
            : L.T("Подписки, рекомендации, лайки, история и комментарии", "Subscriptions, feed, likes, history & comments");
        Gfx.Draw(heading, x + 46, y, 17.5f, Th.Text, 600, innerW - 76);
        Gfx.Draw(subheading, x + 46, y + 22, 12, Th.Sub, 400, innerW - 76);
        y += 52;
        Gfx.Fill(new R(x, y, innerW, 1), Th.Line);
        y += 16;

        if (phase is LoginPhase.Launching or LoginPhase.WaitingForUser or LoginPhase.Verifying)
        {
            UI.Spinner(card.X + card.W / 2, y + 26, 15, Th.Brand);
            var stRun = Gfx.Text(BrowserLogin.StatusText, 14.5f, 600, innerW, 2);
            Gfx.Draw(stRun, Gfx.Snap(card.X + (card.W - stRun.W) / 2), y + 54, Th.Text);

            var hint = phase == LoginPhase.WaitingForUser
                ? L.T("Если у вас несколько каналов — выберите нужный. Окно закроется само.",
                      "If you have multiple channels, choose yours. The window closes automatically.")
                : L.T("Сессия хранится только локально на вашем ПК.",
                      "Your session stays strictly local on your PC.");
            var hRun = Gfx.Text(hint, 12, 400, innerW, 2);
            Gfx.Draw(hRun, Gfx.Snap(card.X + (card.W - hRun.W) / 2), y + 58 + stRun.H, Th.Sub);

            float by = card.Bottom - 54;
            float bx = x;
            if (phase == LoginPhase.WaitingForUser)
            {
                if (UI.Pill(UI.Id("lm:focus"), bx, by, 36, Th.OpenNew, L.T("Показать окно", "Focus window"), out var fw, Th.A(0.10f)))
                    BrowserLogin.FocusAuthWindow();
                bx += fw + 8;
                if (BrowserLogin.HasCapturedCookies && UI.Pill(UI.Id("lm:done"), bx, by, 36, Th.Check, L.T("Готово", "Done"), out _, accent: true))
                    BrowserLogin.RequestFinishNow();
            }
            var cw = UI.PillWidth(null, L.T("Отмена", "Cancel"));
            if (UI.Pill(UI.Id("lm:cancel"), card.Right - pad - cw, by, 36, null, L.T("Отмена", "Cancel"), out _, Th.A(0.08f)))
                BrowserLogin.CancelAuth();
        }
        else if (phase == LoginPhase.Success)
        {
            var acc = BrowserLogin.SignedInAccount;
            float cx = card.X + card.W / 2;
            if (acc?.Photo != null)
            {
                Cards.Avatar(cx - 26, y + 2, 52, acc.Photo, acc.Name);
            }
            else
            {
                Gfx.Circle(cx, y + 28, 24, Gfx.Fade(Th.Good, 0.20f));
                Gfx.Icon(Th.Check, new R(cx - 18, y + 10, 36, 36), 20, Th.Good);
            }
            var okRun = Gfx.Text(BrowserLogin.StatusText, 15.5f, 600, innerW, 2);
            Gfx.Draw(okRun, Gfx.Snap(cx - okRun.W / 2), y + 66, Th.Good);
            if (acc is { Handle.Length: > 0 })
            {
                var hdRun = Gfx.Text(acc.Handle, 12.5f, 400, innerW);
                Gfx.Draw(hdRun, Gfx.Snap(cx - hdRun.W / 2), y + 70 + okRun.H, Th.Sub);
            }
        }
        else if (phase == LoginPhase.Error)
        {
            var errBox = new R(x, y, innerW, 88);
            Gfx.Fill(errBox, Gfx.Fade(Th.Warn, 0.12f), 10);
            Gfx.Stroke(errBox, Gfx.Fade(Th.Warn, 0.40f), 10, 1f);
            Gfx.Draw(L.T("Не удалось выполнить вход", "Sign-in could not complete"), x + 14, y + 12, 14, Th.Warn, 600);
            Gfx.Draw(BrowserLogin.ErrorText, x + 14, y + 34, 12.5f, Th.Text, 400, innerW - 28, 3);

            float by = card.Bottom - 54;
            if (UI.Pill(UI.Id("lm:retry"), x, by, 36, Th.Refresh, L.T("Повторить", "Try again"), out var rw, accent: true))
                App.Post(() => BrowserLogin.StartGoogleSignIn(_verification, _verifyVideoId));
            if (BrowserLogin.HasWebViewHelper)
            {
                if (UI.Pill(UI.Id("lm:wvfallback"), x + rw + 8, by, 36, Th.Account, "WebView2", out _, Th.A(0.08f)))
                {
                    UI.Modal = savedModal;
                    Gfx.PopMotionBlur(mb);
                    Close();
                    App.Post(Shell.LoginWebView);
                    return;
                }
            }
            var bw = UI.PillWidth(null, L.T("Назад", "Back"));
            if (UI.Pill(UI.Id("lm:errback"), card.Right - pad - bw, by, 36, null, L.T("Назад", "Back"), out _, Th.A(0.08f)))
                BrowserLogin.ResetUiState();
        }
        else // LoginPhase.Idle
        {
            // Big primary 1-click CTA button
            var primaryR = new R(x, y, innerW, 44);
            bool overP = UI.Hover(primaryR);
            Gfx.Fill(primaryR, overP ? Gfx.Mix(Th.Brand, 0xFFFFFFFF, 0.08f) : Th.Brand, 12);
            var ctaText = _verification
                ? L.T("Пройти проверку в окне Google", "Verify in Google window")
                : L.T("Войти через Google", "Sign in with Google");
            var ctaRun = Gfx.Text(ctaText, 14.5f, 600);
            float ctaTotalW = 18 + 8 + ctaRun.W;
            float ctaX = primaryR.X + (primaryR.W - ctaTotalW) / 2;
            Gfx.Icon(Th.Globe, new R(ctaX, primaryR.Y, 18, primaryR.H), 16, Th.OnBrand);
            Gfx.Draw(ctaRun, ctaX + 26, primaryR.Y + (primaryR.H - ctaRun.H) / 2, Th.OnBrand);
            if (UI.Click(UI.Id("lm:google"), primaryR))
                App.Post(() => BrowserLogin.StartGoogleSignIn(_verification, _verifyVideoId));

            y += 50;
            var engHint = L.T(
                $"Откроется чистое окно {BrowserLogin.BestSignInEngineName} · ваш браузер закрывать не нужно",
                $"Opens a clean {BrowserLogin.BestSignInEngineName} window · no need to close your browser");
            var ehRun = Gfx.Text(engHint, 11.5f, 400, innerW, 2);
            Gfx.Draw(ehRun, Gfx.Snap(x + (innerW - ehRun.W) / 2), y, Th.Sub);
            y += ehRun.H + 14;

            if (showSaved)
            {
                var firstAcc = AccountMenu.Saved[0];
                var accRow = new R(x, y, innerW, 46);
                Gfx.Fill(accRow, Th.A(0.07f), 10);
                UI.Glow(UI.Id("lm:saved0"), accRow, 10, UI.Hover(accRow));
                Cards.Avatar(accRow.X + 10, accRow.Y + 7, 32, firstAcc.Photo, firstAcc.Name.Length > 0 ? firstAcc.Name : firstAcc.Handle);
                Gfx.Draw(L.T("Вернуться: ", "Resume: ") + (firstAcc.Name.Length > 0 ? firstAcc.Name : firstAcc.Handle), accRow.X + 52, accRow.Y + 7, 13, Th.Text, 600, innerW - 96);
                Gfx.Draw(firstAcc.Handle.Length > 0 ? firstAcc.Handle : L.T("Сохранённый аккаунт", "Saved account"), accRow.X + 52, accRow.Y + 24, 11.5f, Th.Sub, 400, innerW - 96);
                Gfx.Icon(Th.ChevronRight, new R(accRow.Right - 32, accRow.Y, 24, accRow.H), 11, Th.Sub);
                if (UI.Click(UI.Id("lm:saved0"), accRow))
                {
                    var tid = firstAcc.Id;
                    UI.Modal = savedModal;
                    Gfx.PopMotionBlur(mb);
                    Close();
                    App.Post(() => AccountMenu.SwitchTo(tid));
                    return;
                }
                y += 56;
            }

            // Divider "Другие способы"
            var divRun = Gfx.Text(L.T("Другие способы", "Other options"), 11.5f, 500);
            float divMid = y + divRun.H / 2;
            float lineW = MathF.Max(16, (innerW - divRun.W - 20) / 2);
            Gfx.Fill(new R(x, divMid, lineW, 1), Th.Line);
            Gfx.Draw(divRun, x + lineW + 10, y, Th.Dim);
            Gfx.Fill(new R(x + innerW - lineW, divMid, lineW, 1), Th.Line);
            y += divRun.H + 12;

            // Optional 1-click native Firefox / Zen direct SQLite import
            if (ff != null)
            {
                var ffR = new R(x, y, innerW, 40);
                Gfx.Fill(ffR, Th.A(0.08f), 10);
                UI.Glow(UI.Id("lm:ff"), ffR, 10, UI.Hover(ffR));
                Gfx.Icon(Th.Account, new R(ffR.X + 12, ffR.Y, 20, ffR.H), 14, Th.Good);
                Gfx.Draw(L.T($"Импорт из {ff.Value.Name} в 1 клик", $"1-click import from {ff.Value.Name}"), ffR.X + 38, ffR.Y + 11, 13, Th.Text, 500, innerW - 68);
                Gfx.Icon(Th.ChevronRight, new R(ffR.Right - 30, ffR.Y, 20, ffR.H), 11, Th.Sub);
                if (UI.Click(UI.Id("lm:ff"), ffR))
                    App.Post(BrowserLogin.ImportFromFirefoxDirect);
                y += 48;
            }

            // Bottom row: Paste cookies (Netscape/JSON/Header) | Pick cookies.txt | Optional WebView2
            bool showWv = BrowserLogin.HasWebViewHelper;
            int cols = showWv ? 3 : 2;
            float gap = 8;
            float bw = (innerW - gap * (cols - 1)) / cols;

            void SecBtn(string id, float bx, string glyph, string lbl, Action onClick)
            {
                var br = new R(bx, y, bw, 38);
                Gfx.Fill(br, Th.A(0.08f), 10);
                UI.Glow(UI.Id(id), br, 10, UI.Hover(br));
                var tr = Gfx.Text(lbl, 12.5f, 500, bw - 34);
                float tw = 16 + 6 + tr.W;
                float tx = br.X + (br.W - tw) / 2;
                Gfx.Icon(glyph, new R(tx, br.Y, 16, br.H), 13, Th.Sub);
                Gfx.Draw(tr, tx + 22, br.Y + (br.H - tr.H) / 2, Th.Text);
                if (UI.Click(UI.Id(id), br)) onClick();
            }

            SecBtn("lm:paste", x, Th.Copy, L.T("Вставить cookies", "Paste cookies"), () => App.Post(BrowserLogin.ImportFromClipboard));
            SecBtn("lm:file", x + bw + gap, Th.Folder, "cookies.txt…", () =>
                App.Post(() => Dialogs.PickFileAsync("Cookies (*.txt;*.json)\0*.txt;*.json\0All files\0*.*\0\0", BrowserLogin.ImportFromFile)));
            if (showWv)
            {
                SecBtn("lm:wv", x + (bw + gap) * 2, Th.Account, "WebView2", () =>
                {
                    Close();
                    App.Post(Shell.LoginWebView);
                });
            }
        }

        UI.Modal = savedModal;
        Gfx.PopMotionBlur(mb);

        if (UI.Pressed && !card.Contains(UI.MX, UI.MY) && !Menus.Open && !BrowserLogin.IsBusy)
            Close();
    }
}
