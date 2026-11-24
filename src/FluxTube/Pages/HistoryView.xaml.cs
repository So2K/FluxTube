using FluxTube.Models;
using FluxTube.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluxTube.Pages;

public sealed class TimelineRow
{
    public required VideoItem Video { get; init; }
    public required string Time { get; init; }
    public required string Detail { get; init; }
    public long WatchId { get; init; }
    public string Source { get; init; } = "";
    public DateTime At { get; init; }
}

public sealed class TimelineGroup(string key, IEnumerable<TimelineRow> rows) : List<TimelineRow>(rows)
{
    public string Key { get; } = key;
}

/// <summary>
/// Two timelines over local data: what you watched (with position and how long),
/// and every video card that was on screen, with where it appeared.
/// </summary>
public sealed partial class HistoryView : UserControl, INavPage
{
    private readonly bool _seen;
    private CancellationTokenSource? _filterCts;

    public string NavTag => _seen ? "seen" : "history";

    public HistoryView(bool seen)
    {
        InitializeComponent();
        _seen = seen;
        Header.Text = seen ? "Что я видел" : "История просмотров";
        SubHeader.Text = seen
            ? "Каждое видео, которое появлялось у вас на экране: на главной, в поиске, в похожих. С временем и местом. Ничего не потеряется, даже если лента обновилась."
            : "Все запуски видео с позицией, на которой вы остановились. Клик продолжает с того же места.";
        List.RightTapped += List_RightTapped;
    }

    public void OnShown() => _ = ReloadAsync();
    public void OnHidden() { }

    private void Reload() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        var f = Filter.Text;
        var seenMode = _seen;
        var groups = await Task.Run(() => Build(f, seenMode));
        Groups.Source = groups;
    }

    private static List<TimelineGroup> Build(string f, bool _seen)
    {
        IEnumerable<TimelineRow> rows = _seen
            ? Db.SeenList(f).Select(s => new TimelineRow
            {
                Video = s.Video, Time = s.At.ToString("HH:mm"), Source = s.Source, At = s.At,
                Detail = $"{Fmt.Source(s.Source)} · {s.Video.Meta}".TrimEnd(' ', '·'),
            })
            : Db.History(f).Select(h => new TimelineRow
            {
                Video = h.ToVideo(), Time = h.Last.ToString("HH:mm"), WatchId = h.Id, Source = h.Source, At = h.Last,
                Detail = $"Остановились на {Fmt.Time(h.Position)}{(h.Duration > 0 ? " из " + Fmt.Time(h.Duration) : "")} · смотрели {Fmt.Time(h.Watched)} · {Fmt.Source(h.Source)}",
            });
        var list = rows.ToList();
        var groups = list.GroupBy(r => Fmt.Day(r.At)).Select(g => new TimelineGroup(g.Key, g)).ToList();
        if (_seen)
        {
            var progress = Db.ProgressFor(list.Select(r => r.Video.VideoId));
            foreach (var r in list) if (progress.TryGetValue(r.Video.VideoId, out var p)) r.Video.Progress = p;
        }
        return groups;
    }

    private async void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        try { await Task.Delay(200, cts.Token); Reload(); } catch (TaskCanceledException) { }
    }

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TimelineRow r) App.Main.OpenVideo(r.Video, _seen ? r.Source : "history");
    }

    private void List_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TimelineRow r) return;
        Action? remove = !_seen ? () => { Db.DeleteWatch(r.WatchId); Reload(); } : null;
        UI.VideoMenu.Build(r.Video, remove).ShowAt(List, new FlyoutShowOptions { Position = e.GetPosition(List) });
        e.Handled = true;
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _seen ? "Очистить «Что я видел»?" : "Очистить историю просмотров?",
            Content = "Это только локальные данные FluxTube. Отменить нельзя.",
            PrimaryButtonText = "Очистить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (_seen) Db.ClearSeen(); else Db.ClearHistory();
        Reload();
    }
}
