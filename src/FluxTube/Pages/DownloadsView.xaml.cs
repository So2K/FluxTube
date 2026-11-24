using System.Diagnostics;
using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;
using FluxTube.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluxTube.Pages;

public sealed partial class DownloadsView : UserControl, INavPage
{
    public string NavTag => "downloads";

    public DownloadsView()
    {
        InitializeComponent();
        foreach (var q in AppSettings.Qualities)
            QualityBox.Items.Add(new ComboBoxItem { Content = VideoMenu.QualityName(q), Tag = q });
        QualityBox.SelectedIndex = Array.IndexOf(AppSettings.Qualities, AppSettings.Current.DownloadQuality);
        List.ItemsSource = Downloader.Instance.Items;
    }

    public void OnShown()
    {
        foreach (var d in Downloader.Instance.Items) d.Refresh();
    }

    public void OnHidden() { }

    private string SelectedQuality => (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? AppSettings.Current.DownloadQuality;

    private void Add_Click(object sender, RoutedEventArgs e) => Add();

    private void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Add();
    }

    private void Add()
    {
        var urls = UrlBox.Text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(u => u.StartsWith("http")).ToList();
        foreach (var u in urls)
            Downloader.Instance.Enqueue(u, videoId: MainWindow.ExtractYouTubeId(u), quality: SelectedQuality);
        if (urls.Count > 0) UrlBox.Text = "";
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var f = Filter.Text.Trim();
        List.ItemsSource = f.Length == 0
            ? Downloader.Instance.Items
            : Downloader.Instance.Items.Where(d => d.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
                                                || d.Channel.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static DownloadItem Of(object sender) => (DownloadItem)((FrameworkElement)sender).Tag;

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DownloadItem d && d.IsDone) Play(d);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Play(Of(sender));

    private static void Play(DownloadItem d)
    {
        if (d.File == null || !File.Exists(d.File))
        {
            App.Main.ShowToast("Файл не найден: возможно, его переместили или удалили.", InfoBarSeverity.Warning);
            return;
        }
        if (d.VideoId is { Length: 11 })
            App.Main.OpenVideo(new VideoItem { VideoId = d.VideoId, Title = d.Title, Channel = d.Channel }, "downloads");
        else
        {
            PlaybackTracker.Instance.PlayFile(d);
            App.Main.ShowToast("Играет в мини-плеере: " + d.Title);
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var d = Of(sender);
        if (d.File != null && File.Exists(d.File))
            Process.Start("explorer.exe", $"/select,\"{d.File}\"");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.Current.DownloadDir);
        Process.Start("explorer.exe", $"\"{AppSettings.Current.DownloadDir}\"");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Downloader.Instance.Cancel(Of(sender));
    private void Retry_Click(object sender, RoutedEventArgs e) => Downloader.Instance.Retry(Of(sender));

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var d = Of(sender);
        if (d.IsDone)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Удалить загрузку?", Content = d.Title,
                PrimaryButtonText = "Удалить файл", SecondaryButtonText = "Только из списка", CloseButtonText = "Отмена",
            };
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.None) return;
            Downloader.Instance.Remove(d, deleteFile: r == ContentDialogResult.Primary);
        }
        else Downloader.Instance.Remove(d, deleteFile: false);
    }
}
