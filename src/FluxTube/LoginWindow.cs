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
        Title = "Вход в YouTube";
        var grid = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        _status.Text = "Войдите в аккаунт Google. Окно закроется само, как только откроется YouTube.";
        grid.Children.Add(_status);
        Grid.SetRow(_web, 1);
        grid.Children.Add(_web);
        Content = grid;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await WebEnv.GetAsync());
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckAsync();
            _web.Source = new Uri("https://accounts.google.com/ServiceLogin?service=youtube&hl=ru&passive=true" +
                                  "&continue=" + Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&app=desktop&next=%2F"));
        }
        catch (Exception e)
        {
            _status.Text = "Не удалось открыть окно входа: " + e.Message + ". Используйте «Импорт cookies.txt» в настройках.";
        }
    }

    private async Task CheckAsync()
    {
        if (_done) return;
        var host = _web.Source?.Host ?? "";
        if (!host.EndsWith("youtube.com")) return;
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com");
        if (!cookies.Any(c => c.Name is "SAPISID" or "__Secure-3PAPISID")) return;
        _done = true;
        CookieStore.Save(cookies.Select(Convert));
        _status.Text = "Готово, вы вошли.";
        if (App.Main != null)
        {
            MpvPlayer.Instance.ApplySettings();
            App.Main.ShowToast("Вы вошли в YouTube", InfoBarSeverity.Success);
            App.Main.OpenRoot("home");
        }
        Close();
    }

    private static CookieStore.Cookie Convert(CoreWebView2Cookie c)
        => new(c.Domain, c.Domain.StartsWith('.'), c.Path, c.IsSecure,
            c.IsSession ? 0 : (long)c.Expires, c.Name, c.Value, c.IsHttpOnly);
}
