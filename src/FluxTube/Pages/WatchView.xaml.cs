using System.Collections.ObjectModel;
using FluxTube.Models;
using FluxTube.Player;
using FluxTube.Services;
using FluxTube.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluxTube.Pages;

public sealed partial class WatchView : UserControl, INavPage
{
    public VideoItem Video { get; private set; }
    public FrameworkElement VideoHost => VideoBox;

    private readonly string _source;
    private readonly ObservableCollection<VideoItem> _related = [];
    private WatchInfo? _info;
    private bool _infoLoading;
    private bool _fullscreen;
    private bool _shown;
    private bool _descExpanded;

    public string NavTag => "";
    public VideoItem? FirstRelated => _related.FirstOrDefault();

    public WatchView(VideoItem v, string source)
    {
        InitializeComponent();
        Video = v;
        _source = source;
        Related.ItemsSource = _related;
        VideoMenu.Attach(Related);
        ListPerf.Attach(Related, () => $"related:{Video.VideoId}|{TitleText.Text}", decodeWidth: 168);
        MainCol.SizeChanged += (_, _) => Relayout();
        Layout.SizeChanged += (_, _) => Relayout();
        AutoplaySwitch.IsOn = AppSettings.Current.Autoplay;
        TitleText.Text = v.Title;
        ChannelLink.Content = v.Channel;
        BuildMenus();
        MpvPlayer.Instance.EndOfFile += OnEnded;
    }

    public void OnShown()
    {
        _shown = true;
        if (PlaybackTracker.Instance.Current?.VideoId != Video.VideoId || !MpvPlayer.Instance.HasMedia)
            PlaybackTracker.Instance.Play(Video, _source);
        if (_info == null && !_infoLoading) _ = LoadInfoAsync();
        UpdateDownloadState();
        PlayQualityText.Text = VideoMenu.QualityName(AppSettings.Current.PlayerQuality);
        Relayout();
    }

    public void OnHidden() => _shown = false;

    private void Relayout()
    {
        var w = MainCol.ActualWidth;
        if (w <= 0) return;
        if (_fullscreen)
        {
            VideoBox.Height = Layout.ActualHeight;
            return;
        }
        var h = Math.Min(w * 9 / 16, Math.Max(240, Layout.ActualHeight * 0.80));
        if (double.IsNaN(VideoBox.Height) || Math.Abs(VideoBox.Height - h) > 0.5) VideoBox.Height = h;
    }

    public void SetFullscreen(bool fs)
    {
        _fullscreen = fs;
        Layout.Padding = fs ? new Thickness(0) : new Thickness(20, 4, 0, 0);
        Layout.ColumnSpacing = fs ? 0 : 20;
        SideCol.Width = fs || TheaterToggle.IsChecked == true ? new GridLength(0) : new GridLength(410);
        SidePanel.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
        InfoScroll.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
        Relayout();
    }

    private void Theater_Click(object sender, RoutedEventArgs e)
    {
        var on = TheaterToggle.IsChecked == true;
        SideCol.Width = on ? new GridLength(0) : new GridLength(410);
        SidePanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadInfoAsync()
    {
        _infoLoading = true;
        try
        {
            var vid = Video.VideoId;
            var info = await Task.Run(async () =>
            {
                var i = await InnerTube.Instance.WatchAsync(vid);
                VideoCollection.WithProgress(i.Related);
                return i;
            });
            _info = info;
            TitleText.Text = info.Title;
            ChannelLink.Content = info.Channel;
            SubsText.Text = info.Subscribers;
            if (info.ChannelAvatar != null) Avatar.ProfilePicture = new BitmapImage(new Uri(info.ChannelAvatar)) { DecodePixelWidth = 84 };
            StatsText.Text = string.Join("  ·  ", new[] { info.Views, info.Date }.Where(s => !string.IsNullOrEmpty(s)));
            DescText.Text = info.Description;
            DescToggle.Visibility = info.Description.Count(c => c == '\n') > 3 || info.Description.Length > 300 ? Visibility.Visible : Visibility.Collapsed;
            LikesText.Text = string.IsNullOrEmpty(info.Likes) ? "—" : info.Likes;
            if (string.IsNullOrEmpty(Video.Title))
                Video = new VideoItem { VideoId = Video.VideoId, Title = info.Title, Channel = info.Channel, ChannelId = info.ChannelId };
            if (PlaybackTracker.Instance.Current?.VideoId == Video.VideoId) PlaybackTracker.Instance.UpdateMeta(info.Title, info.Channel);

            _related.Clear();
            foreach (var r in info.Related.Items) _related.Add(r);
            // the likely next clicks: resolve them while this one plays
            for (int i = Math.Min(2, _related.Count - 1); i >= 0; i--) Resolver.Instance.Prefetch(_related[i].VideoId);
            if (AppSettings.Current.ReturnDislikes)
            {
                var votes = await Extras.DislikesAsync(Video.VideoId);
                if (votes != null)
                {
                    DislikesText.Text = Extras.Compact(votes.Dislikes);
                    if (LikesText.Text == "—") LikesText.Text = Extras.Compact(votes.Likes);
                }
            }
        }
        catch (Exception e)
        {
            Paths.Log("watch info: " + e);
            Message.Message = "Описание и похожие не загрузились: " + e.Message;
            Message.Severity = InfoBarSeverity.Warning;
            Message.IsOpen = true;
        }
        finally
        {
            _infoLoading = false;
            Busy.IsActive = false;
        }
    }

    private void DescToggle_Click(object sender, RoutedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        DescText.MaxLines = _descExpanded ? 0 : 4;
        DescToggle.Content = _descExpanded ? "Свернуть" : "Ещё";
    }

    private void Channel_Click(object sender, RoutedEventArgs e)
    {
        var id = _info?.ChannelId ?? Video.ChannelId;
        if (id != null) App.Main.OpenChannel(id, _info?.Channel ?? Video.Channel);
    }

    private void Related_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoItem v) App.Main.OpenVideo(v, $"related:{Video.VideoId}|{TitleText.Text}");
    }


    private void OnEnded(bool eof)
    {
        if (!_shown || !eof || !AutoplaySwitch.IsOn) return;
        if (PlaybackTracker.Instance.Current?.VideoId != Video.VideoId) return;
        var next = _related.FirstOrDefault(r => r.Progress < 0.9);
        if (next != null) App.Main.OpenVideo(next, $"related:{Video.VideoId}|{TitleText.Text}");
    }

    private void Autoplay_Toggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.Autoplay = AutoplaySwitch.IsOn;
        AppSettings.Current.Save();
    }

    // ---------------- actions ----------------

    private void BuildMenus()
    {
        foreach (var q in AppSettings.Qualities)
            QualityMenu.Items.Add(MainWindow.Item(VideoMenu.QualityName(q), "", () => { DownloadQ(q); }));

        foreach (var sp in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0 })
        {
            var label = sp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
            SpeedMenu.Items.Add(MainWindow.Item(label, "", () =>
            {
                MpvPlayer.Instance.SetSpeed(sp);
                SpeedText.Text = label;
            }));
        }

        foreach (var q in AppSettings.Qualities.Where(q => q != "audio"))
            PlayQualityMenu.Items.Add(MainWindow.Item(VideoMenu.QualityName(q), "", () =>
            {
                AppSettings.Current.PlayerQuality = q;
                AppSettings.Current.Save();
                PlayQualityText.Text = VideoMenu.QualityName(q);
                MpvPlayer.Instance.ApplySettings();
                Resolver.Instance.Invalidate();
                // reload at the same position with the new format
                PlaybackTracker.Instance.Flush();
                PlaybackTracker.Instance.Play(Video, _source);
            }));
    }

    private void UpdateDownloadState()
    {
        DownloadText.Text = Downloader.Instance.IsDownloaded(Video.VideoId, out _) ? "Скачано" : "Скачать";
    }

    private void Download_Click(SplitButton sender, SplitButtonClickEventArgs args) => DownloadQ(null);

    private void DownloadQ(string? q)
    {
        var v = _info != null
            ? new VideoItem { VideoId = Video.VideoId, Title = _info.Title, Channel = _info.Channel, ChannelId = _info.ChannelId }
            : Video;
        VideoMenu.Download(v, q);
        DownloadText.Text = "В загрузках";
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var v = new VideoItem { VideoId = Video.VideoId, Title = TitleText.Text, Channel = _info?.Channel ?? Video.Channel, ChannelId = _info?.ChannelId ?? Video.ChannelId };
        var m = VideoMenu.Build(v);
        m.Items.Insert(0, MainWindow.Item("Копировать ссылку с текущего момента", "", () =>
            VideoMenu.Copy($"https://youtu.be/{Video.VideoId}?t={(int)MpvPlayer.Instance.TimePos}")));
        m.Items.Insert(1, MainWindow.Item("Начать сначала", "", () => MpvPlayer.Instance.SeekAbsolute(0)));
        m.ShowAt((FrameworkElement)sender);
    }
}
