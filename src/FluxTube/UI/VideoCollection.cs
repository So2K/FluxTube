using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using FluxTube.Models;
using FluxTube.Services;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace FluxTube.UI;

/// <summary>Feed list that pulls the next InnerTube continuation when the grid scrolls near the end.</summary>
public sealed class VideoCollection : ObservableCollection<VideoItem>, ISupportIncrementalLoading
{
    private readonly Func<string, Task<FeedPage>> _next;
    private string? _token;
    private bool _busy;
    private readonly HashSet<string> _ids = [];

    public VideoCollection(Func<string, Task<FeedPage>> next) => _next = next;

    public event Action<Exception>? Failed;

    /// <summary>Fills resume progress from the local DB; call on a worker thread.</summary>
    public static FeedPage WithProgress(FeedPage page)
    {
        var progress = Db.ProgressFor(page.Items.Select(i => i.VideoId));
        foreach (var v in page.Items)
            if (progress.TryGetValue(v.VideoId, out var p)) v.Progress = p;
        return page;
    }

    public void AddPage(FeedPage page)
    {
        foreach (var v in page.Items)
            if (_ids.Add(v.VideoId)) Add(v);
        _token = page.Continuation;
    }

    public void Reset()
    {
        Clear();
        _ids.Clear();
        _token = null;
    }

    public bool HasMoreItems => _token != null && !_busy;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        => AsyncInfo.Run(async ct =>
        {
            if (_token == null || _busy) return new LoadMoreItemsResult { Count = 0 };
            _busy = true;
            var before = Count;
            try
            {
                var tok = _token;
                var page = await Task.Run(async () => WithProgress(await _next(tok)));
                if (page.Items.Count == 0) page.Continuation = null;
                AddPage(page);
            }
            catch (Exception e)
            {
                _token = null;
                Failed?.Invoke(e);
            }
            finally { _busy = false; }
            return new LoadMoreItemsResult { Count = (uint)(Count - before) };
        });
}
