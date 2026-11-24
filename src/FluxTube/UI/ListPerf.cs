using FluxTube.Models;
using FluxTube.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluxTube.UI;

/// <summary>
/// The UltraExplorer rules applied to XAML lists:
/// text first, thumbnails in a later phase decoded at display size; work only after scrolling settles;
/// visible items first; nothing heavy on the UI thread. Also drives stream prefetch (hover + visible).
/// </summary>
public sealed class ListPerf
{
    private readonly ListViewBase _list;
    private readonly Func<string> _source;
    private readonly int _decodeWidth;
    private readonly bool _hq;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private VideoItem? _hoverItem;
    private ScrollViewer? _scroll;

    public static ListPerf Attach(ListViewBase list, Func<string> source, int decodeWidth, bool hq = false)
        => new(list, source, decodeWidth, hq);

    private ListPerf(ListViewBase list, Func<string> source, int decodeWidth, bool hq)
    {
        _list = list;
        _source = source;
        _decodeWidth = decodeWidth;
        _hq = hq;
        list.ContainerContentChanging += OnContainerContentChanging;
        list.Loaded += (_, _) =>
        {
            _scroll ??= FindScrollViewer(list);
            if (_scroll != null) _scroll.ViewChanged += (_, e) => { _settle.Stop(); _settle.Start(); };
        };
        _settle.Tick += (_, _) => { _settle.Stop(); PrefetchVisible(); };
        _hover.Tick += (_, _) =>
        {
            _hover.Stop();
            if (_hoverItem != null) Resolver.Instance.Prefetch(_hoverItem.VideoId, urgent: true);
        };
    }

    /// <summary>Call after the items change so the first screen gets prefetched without a scroll.</summary>
    public void Kick() { _settle.Stop(); _settle.Start(); }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.Tag == null)
        {
            args.ItemContainer.Tag = this; // events once per recycled container
            args.ItemContainer.PointerEntered += (s, _) =>
            {
                _hoverItem = (s as ContentControl)?.Content as VideoItem;
                _hover.Stop();
                _hover.Start();
            };
            args.ItemContainer.PointerExited += (_, _) => { _hover.Stop(); _hoverItem = null; };
        }

        var img = (args.ItemContainer.ContentTemplateRoot as FrameworkElement)?.FindName("Thumb") as Image;
        if (args.InRecycleQueue)
        {
            if (img != null) img.Source = null;
            return;
        }
        if (args.Item is not VideoItem v) return;

        if (args.Phase == 0)
        {
            if (img != null) img.Source = null;
            var src = _source();
            ThreadPool.QueueUserWorkItem(_ => Db.Seen(v, src));
            args.RegisterUpdateCallback(1, OnContainerContentChanging);
        }
        else if (args.Phase == 1 && img != null)
        {
            img.Source = new BitmapImage
            {
                DecodePixelWidth = _decodeWidth,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = new Uri(_hq ? v.ThumbHq : v.Thumb),
            };
        }
    }

    private void PrefetchVisible()
    {
        if (_list.ItemsPanelRoot is not (ItemsWrapGrid or ItemsStackPanel)) return;
        int first, last;
        if (_list.ItemsPanelRoot is ItemsWrapGrid g) { first = g.FirstVisibleIndex; last = g.LastVisibleIndex; }
        else { var p = (ItemsStackPanel)_list.ItemsPanelRoot; first = p.FirstVisibleIndex; last = p.LastVisibleIndex; }
        if (first < 0 || last < first) return;
        var items = _list.ItemsSource as System.Collections.IList;
        if (items == null) return;
        // LIFO queue: push the least important first so the top-left card resolves first
        var n = Math.Min(last, first + 3); // a few, not the whole screen: each costs ~5 round trips to YouTube
        for (int i = n; i >= first; i--)
            if (i < items.Count && items[i] is VideoItem v) Resolver.Instance.Prefetch(v.VideoId);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }
}
