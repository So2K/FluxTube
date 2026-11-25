using FluxTube.Player;
using FluxTube.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace FluxTube;

/// <summary>
/// Google sign-in on Google's own page inside WebView2. The user types the password there;
/// afterwards only the youtube.com session cookies are written to cookies.txt.
/// </summary>
public sealed class LoginWindow : Window
{
    private readonly Microsoft.UI.Xaml.Controls.WebView2 _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(14, 10, 14, 10), TextWrapping = TextWrapping.Wrap };
    private bool _done;

    public LoginWindow()
    {
        Title = L.T("Вход в YouTube", "Sign in to YouTube");
        var grid = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        _status.Text = L.T("Войдите в аккаунт Google. Окно закроется само, как только откроется YouTube.",
                           "Sign in to your Google account. This window closes automatically once YouTube loads.");
        grid.Children.Add(_status);
        Grid.SetRow(_web, 1);
        grid.Children.Add(_web);
        Content = grid;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(540, 760));
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await WebEnv.GetAsync());
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckAsync();
            var hl = Uri.EscapeDataString(AppSettings.Current.Hl);
            _web.Source = new Uri($"https://accounts.google.com/ServiceLogin?service=youtube&hl={hl}&passive=true" +
                                  "&continue=" + Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&app=desktop&next=%2F"));
        }
        catch (Exception e)
        {
            _status.Text = L.T("Не удалось открыть окно входа: ", "Could not open sign-in window: ") + e.Message;
        }
    }

    private async Task CheckAsync()
    {
        if (_done) return;
        var uri = _web.Source;
        var host = uri?.Host ?? "";
        var path = uri?.AbsolutePath.ToLowerInvariant() ?? "";
        if (!host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)) return;
        if (path.StartsWith("/signin") || path.StartsWith("/channel_switcher") || path.StartsWith("/create_channel")) return;

        await Task.Delay(800);
        var ytCookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com");
        if (!ytCookies.Any(c => c.Name is "SAPISID" or "__Secure-3PAPISID" or "__Secure-1PAPISID")) return;
        var gCookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.google.com");
        var all = ytCookies.Concat(gCookies).Select(Convert)
            .GroupBy(c => (c.Domain.ToLowerInvariant(), c.Path.ToLowerInvariant(), c.Name.ToLowerInvariant()))
            .Select(g => g.First());
        _done = true;
        CookieStore.Save(all);
        _status.Text = L.T("Готово, вы вошли.", "Signed in!");
        if (App.Main != null)
        {
            MpvPlayer.Instance.ApplySettings();
            App.Main.ShowToast(L.T("Вы вошли в YouTube", "Signed in to YouTube"), InfoBarSeverity.Success);
            App.Main.OpenRoot("home");
        }
        Close();
    }

    private static CookieStore.Cookie Convert(CoreWebView2Cookie c)
        => new(c.Domain, c.Domain.StartsWith('.'), c.Path, c.IsSecure,
            c.IsSession ? 0 : (long)c.Expires, c.Name, c.Value, c.IsHttpOnly);
}
