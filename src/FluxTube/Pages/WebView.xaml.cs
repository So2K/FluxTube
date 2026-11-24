using FluxTube.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluxTube.Pages;

/// <summary>Embedded site (TikTok etc.) in the shared WebView2 profile, so logins persist; any page can be sent to downloads.</summary>
public sealed partial class WebView : UserControl, INavPage
{
    private readonly string _home;
    private bool _started;
    public string NavTag { get; }

    public WebView(string home, string tag)
    {
        InitializeComponent();
        _home = home;
        NavTag = tag;
    }

    public async void OnShown()
    {
        if (_started) return;
        _started = true;
        try
        {
            await Web.EnsureCoreWebView2Async(await WebEnv.GetAsync());
            Web.CoreWebView2.SourceChanged += (_, _) => UrlText.Text = Web.Source?.ToString();
            Web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; Web.CoreWebView2.Navigate(e.Uri); };
            Web.Source = new Uri(_home);
        }
        catch (Exception e)
        {
            UrlText.Text = "WebView2 недоступен: " + e.Message;
        }
    }

    public void OnHidden()
    {
        // stop TikTok's autoplaying sound when leaving the tab
        _ = Web.CoreWebView2?.ExecuteScriptAsync("document.querySelectorAll('video').forEach(v=>v.pause())");
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Web.CanGoForward) Web.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Web.Reload();

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var url = Web.Source?.ToString();
        if (string.IsNullOrEmpty(url)) return;
        if (!url.Contains("/video/") && !url.Contains("/photo/"))
        {
            App.Main.ShowToast("Откройте конкретное видео (ссылка с /video/), потом нажмите «Скачать».", InfoBarSeverity.Warning);
            return;
        }
        if (App.Helper)
        {
            // hand the link to the main (native) window, which owns the download queue
            var inbox = Path.Combine(Paths.Data, "inbox");
            Directory.CreateDirectory(inbox);
            File.WriteAllText(Path.Combine(inbox, Guid.NewGuid().ToString("N") + ".url"), url);
        }
        else Downloader.Instance.Enqueue(url);
        App.Main.ShowToast("Добавлено в загрузки", InfoBarSeverity.Success);
    }
}
