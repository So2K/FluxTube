using FluxTube.Models;
using FluxTube.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace FluxTube.UI;

/// <summary>Right-click menu shared by every list of videos.</summary>
public static class VideoMenu
{
    public static void Attach(ListViewBase list)
    {
        list.RightTapped += (s, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not VideoItem v) return;
            Build(v).ShowAt(list, new FlyoutShowOptions { Position = e.GetPosition(list) });
            e.Handled = true;
        };
    }

    public static MenuFlyout Build(VideoItem v, Action? onRemove = null)
    {
        var m = new MenuFlyout();
        m.Items.Add(MainWindow.Item("Скачать (" + AppSettings.Current.DownloadQuality + ")", "", () => Download(v, null)));
        var sub = new MenuFlyoutSubItem { Text = "Скачать в качестве", Icon = new FontIcon { Glyph = "" } };
        foreach (var q in AppSettings.Qualities)
            sub.Items.Add(MainWindow.Item(QualityName(q), "", () => Download(v, q)));
        m.Items.Add(sub);
        m.Items.Add(new MenuFlyoutSeparator());
        if (!string.IsNullOrEmpty(v.ChannelId))
            m.Items.Add(MainWindow.Item("Открыть канал: " + v.Channel, "", () => App.Main.OpenChannel(v.ChannelId!, v.Channel)));
        m.Items.Add(MainWindow.Item("Копировать ссылку", "", () => Copy(v.Url)));
        m.Items.Add(MainWindow.Item("Открыть в браузере", "", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(v.Url))));
        if (onRemove != null)
        {
            m.Items.Add(new MenuFlyoutSeparator());
            m.Items.Add(MainWindow.Item("Удалить из списка", "", onRemove));
        }
        return m;
    }

    public static string QualityName(string q) => q switch
    {
        "best" => "Максимальное",
        "audio" => "Только аудио (m4a)",
        "2160" => "4K (2160p)",
        "1440" => "2K (1440p)",
        _ => q + "p",
    };

    public static void Download(VideoItem v, string? quality)
    {
        Downloader.Instance.Enqueue(v.Url, v.Title, v.Channel, v.VideoId, quality);
        App.Main.ShowToast("Скачивается: " + v.Title, InfoBarSeverity.Success);
    }

    public static void Copy(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        App.Main.ShowToast("Ссылка скопирована");
    }
}
