using FluxTube.Models;
using FluxTube.Services;
using FluxTube.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluxTube.Pages;

/// <summary>Any grid of videos: home, subscriptions, watch later, liked, search results, channel.</summary>
public sealed partial class FeedView : UserControl, INavPage
{
    private readonly string _kind;
    private readonly string? _arg;
    private readonly VideoCollection _items;
    private bool _loaded;
    private string? _contEndpoint;
    private readonly ListPerf _perf;

    public string NavTag => _kind is "search" or "channel" ? "" : _kind;
    private string Source => _kind switch
    {
        "search" => "search:" + _arg,
        "channel" => "channel:" + _arg,
        _ => _kind,
    };

    public FeedView(string kind, string? arg, string? title = null)
    {
        InitializeComponent();
        _kind = kind;
        _arg = arg;
        _contEndpoint = kind == "search" ? "search" : "browse";
        _items = new VideoCollection(tok => InnerTube.Instance.ContinueAsync(_contEndpoint!, tok));
        _items.Failed += e => Show("Не удалось подгрузить ещё: " + e.Message, InfoBarSeverity.Warning);
        Grid.ItemsSource = _items;
        VideoMenu.Attach(Grid);
        _perf = ListPerf.Attach(Grid, () => Source, decodeWidth: 318, hq: true);
        Header.Text = kind switch
        {
            "home" => "Главная",
            "subs" => "Подписки",
            "wl" => "Смотреть позже",
            "liked" => "Понравившиеся",
            "search" => $"«{arg}»",
            "channel" => title ?? "Канал",
            _ => kind,
        };
    }

    public void OnShown()
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
    }

    public void OnHidden() { }

    public void Refresh() => _ = LoadAsync();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private async Task LoadAsync()
    {
        Busy.IsActive = true;
        Message.IsOpen = false;
        try
        {
            var needsLogin = _kind is "subs" or "wl" or "liked";
            if (needsLogin && !CookieStore.IsLoggedIn)
            {
                _items.Reset();
                Show("Войдите в аккаунт YouTube (кнопка справа сверху), чтобы видеть этот раздел.", InfoBarSeverity.Informational);
                return;
            }
            var kind = _kind; var arg = _arg;
            var page = await Task.Run(async () => VideoCollection.WithProgress(kind switch
            {
                "home" => await InnerTube.Instance.HomeAsync(),
                "subs" => await InnerTube.Instance.BrowseAsync("FEsubscriptions"),
                "wl" => await InnerTube.Instance.BrowseAsync("VLWL"),
                "liked" => await InnerTube.Instance.BrowseAsync("VLLL"),
                "search" => await InnerTube.Instance.SearchAsync(arg!),
                "channel" => await InnerTube.Instance.ChannelVideosAsync(arg!),
                _ => new FeedPage(),
            }));
            if (_kind == "home" && page.Items.Count == 0)
            {
                page = await Task.Run(async () => VideoCollection.WithProgress(await ForYouAsync()));
                if (page.Items.Count == 0)
                    Show("YouTube не даёт рекомендации без входа и истории. Войдите в аккаунт или найдите что-нибудь через поиск: лента «Для вас» соберётся из ваших просмотров.", InfoBarSeverity.Informational);
                else
                    Show("Вы не вошли: лента собрана из похожих на ваши последние просмотры.", InfoBarSeverity.Informational);
            }
            _items.Reset();
            _items.AddPage(page);
            _perf.Kick();
            if (_items.Count == 0 && !Message.IsOpen) Show("Здесь пока пусто.", InfoBarSeverity.Informational);
        }
        catch (Exception e)
        {
            Paths.Log($"feed {_kind}: {e}");
            Show("Не удалось загрузить: " + e.Message, InfoBarSeverity.Error);
        }
        finally { Busy.IsActive = false; }
    }

    /// <summary>Local recommendations: related videos of the last few things you watched.</summary>
    private static async Task<FeedPage> ForYouAsync()
    {
        var ids = Db.RecentWatchedIds(4);
        var result = new FeedPage();
        if (ids.Count == 0) return result;
        var lists = await Task.WhenAll(ids.Select(async id =>
        {
            try { return (await InnerTube.Instance.WatchAsync(id)).Related.Items; }
            catch { return new List<VideoItem>(); }
        }));
        var watched = new HashSet<string>(ids);
        for (int i = 0; i < lists.Max(l => l.Count); i++)
            foreach (var l in lists)
                if (i < l.Count && watched.Add(l[i].VideoId)) result.Items.Add(l[i]);
        return result;
    }

    private void Show(string text, InfoBarSeverity sev)
    {
        Message.Message = text;
        Message.Severity = sev;
        Message.IsOpen = true;
    }

    private void Grid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoItem v) App.Main.OpenVideo(v, Source);
    }

}
