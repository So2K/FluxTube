using FluxTube.Models;
using FluxTube.Pages;
using FluxTube.Player;
using FluxTube.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace FluxTube;

public interface INavPage
{
    string NavTag { get; }
    void OnShown();
    void OnHidden();
}

public sealed partial class MainWindow : Window
{
    private readonly MpvPlayer _player = MpvPlayer.Instance;
    private readonly List<FrameworkElement> _backStack = [];
    private readonly Dictionary<string, FrameworkElement> _roots = [];
    private FrameworkElement? _current;
    private FrameworkElement? _videoHost;
    private bool _miniEnabled = true;
    private bool _fullscreen;
    private bool _syncingNav;
    private RectInt32 _lastBounds;
    private CancellationTokenSource? _suggestCts;
    public IntPtr Hwnd { get; }
    // DispatcherQueueTimer is collected if nothing references it, and then never fires
    private readonly HashSet<Microsoft.UI.Dispatching.DispatcherQueueTimer> _timers = [];

    public void After(TimeSpan delay, Action a)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = delay;
        t.IsRepeating = false;
        _timers.Add(t);
        t.Tick += (_, _) => { _timers.Remove(t); a(); };
        t.Start();
    }

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var s = AppSettings.Current;
        AppWindow.Resize(new SizeInt32((int)(s.WindowWidth * Scale0()), (int)(s.WindowHeight * Scale0())));
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        Root.Loaded += Root_Loaded;
        AppWindow.Closing += (_, _) =>
        {
            var sc = Root.XamlRoot?.RasterizationScale ?? 1;
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                s.WindowWidth = AppWindow.Size.Width / sc;
                s.WindowHeight = AppWindow.Size.Height / sc;
            }
            App.Shutdown();
        };

        Root.KeyboardAccelerators.Add(Accel(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu, () => GoBack()));
        Root.KeyboardAccelerators.Add(Accel(Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, () => SearchBox.Focus(FocusState.Keyboard)));
        Root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(Root).Properties.IsXButton1Pressed) GoBack();
        };
    }

    private static double Scale0()
    {
        // before XamlRoot exists: use the system DPI
        return Win32Dpi.GetDpiForSystem() / 96.0;
    }

    private static KeyboardAccelerator Accel(Windows.System.VirtualKey k, Windows.System.VirtualKeyModifiers m, Action a)
    {
        var ka = new KeyboardAccelerator { Key = k, Modifiers = m };
        ka.Invoked += (_, e) => { a(); e.Handled = true; };
        return ka;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            UiThread.Post = a => DispatcherQueue.TryEnqueue(() => a());
            if (App.Helper)
            {
                // browser-only window: no player, no navigation chrome
                Nav.IsPaneVisible = false;
                SearchBox.Visibility = AccountButton.Visibility = BackButton.Visibility = Visibility.Collapsed;
            }
            else _player.Init(MpvPlayer.CreateHostWindow(Hwnd));
        }
        catch (Exception ex)
        {
            Paths.Log("mpv init: " + ex);
            ShowToast("Плеер не запустился: " + ex.Message, InfoBarSeverity.Error);
        }
        _player.FullscreenChanged += SetFullscreen;
        _player.TimeChanged += _ => UpdateNowPlaying();
        _player.PauseChanged += p => NpPauseIcon.Glyph = p ? "" : "";
        _player.LoadFailed += msg => ShowToast("Видео не открылось (" + msg + "). Возможно, YouTube что-то поменял: обновите yt-dlp в настройках.", InfoBarSeverity.Error);
        PlaybackTracker.Instance.CurrentChanged += UpdateNowPlaying;
        CookieStore.Changed += () => DispatcherQueue.TryEnqueue(UpdateAccount);
        Downloader.Instance.Init();
        UpdateAccount();
        UpdateTitleBarRegions();
        Root.LayoutUpdated += (_, _) => SyncVideoBounds();
        ((NavigationViewItem)Nav.MenuItems[0]).IsSelected = true;
        _ = Task.Run(YtDlp.UpdateIfStaleAsync);
        RunCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    /// <summary>FluxTube.exe [--watch ID|URL] [--search text] [--page tag]; also used for scripted checks.</summary>
    public void RunCommandLine(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            var v = args[i + 1];
            switch (args[i])
            {
                case "--watch": OpenVideo(new VideoItem { VideoId = ExtractYouTubeId(v) ?? v }, "link"); break;
                case "--search": OpenSearch(v); break;
                case "--page": OpenRoot(v); break;
                case "--test-next": // after N seconds open the first related video (checks the prefetched path)
                    After(TimeSpan.FromSeconds(double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)),
                        () => { if (CurrentWatch?.FirstRelated is { } r) OpenVideo(r, "test"); });
                    break;
            }
        }
    }

    // ---------------- navigation ----------------

    public void Navigate(FrameworkElement page, bool push = true)
    {
        if (_current == page) return;
        if (_current != null)
        {
            (_current as INavPage)?.OnHidden();
            if (push)
            {
                _backStack.Remove(_current);
                _backStack.Add(_current);
                if (_backStack.Count > 40) _backStack.RemoveAt(0);
            }
        }
        _current = page;
        Host.Content = page;
        (page as INavPage)?.OnShown();
        BackButton.IsEnabled = _backStack.Count > 0;
        SyncNavSelection();
        UpdateVideoPlacement();
    }

    public void GoBack()
    {
        if (_fullscreen) { _player.SetFullscreenFlag(false); return; }
        if (_backStack.Count == 0) return;
        var prev = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        Navigate(prev, push: false);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private FrameworkElement Root_(string tag)
    {
        if (_roots.TryGetValue(tag, out var p)) return p;
        p = tag switch
        {
            "home" => new FeedView("home", null),
            "subs" => new FeedView("subs", null),
            "wl" => new FeedView("wl", null),
            "liked" => new FeedView("liked", null),
            "history" => new HistoryView(false),
            "seen" => new HistoryView(true),
            "downloads" => new DownloadsView(),
            "tiktok" => new WebView("https://www.tiktok.com/", "tiktok"),
            "settings" => new SettingsView(),
            _ => new FeedView("home", null),
        };
        _roots[tag] = p;
        return p;
    }

    public void OpenRoot(string tag) => Navigate(Root_(tag));

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNav) return;
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag != null) OpenRoot(tag);
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // clicking the already selected root again: refresh it
        var tag = args.IsSettingsInvoked ? "settings" : (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        if (tag != null && _current is INavPage np && np.NavTag == tag && _current is FeedView fv) fv.Refresh();
        else if (tag != null && _roots.TryGetValue(tag, out var r) && _current != r) OpenRoot(tag);
    }

    private void SyncNavSelection()
    {
        var tag = (_current as INavPage)?.NavTag;
        _syncingNav = true;
        try
        {
            if (tag == "settings") Nav.SelectedItem = Nav.SettingsItem;
            else Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        }
        finally { _syncingNav = false; }
    }

    public void OpenVideo(VideoItem v, string source)
    {
        // reuse the page if this video is already somewhere in the back stack: it keeps its state
        var existing = _backStack.OfType<WatchView>().LastOrDefault(w => w.Video.VideoId == v.VideoId);
        Navigate(existing ?? new WatchView(v, source));
    }

    public void OpenChannel(string channelId, string name) => Navigate(new FeedView("channel", channelId, name));

    public void OpenSearch(string q) => Navigate(new FeedView("search", q));

    // ---------------- video placement ----------------

    public void SetVideoHost(FrameworkElement? host)
    {
        _videoHost = host;
        UpdateVideoPlacement();
    }

    private WatchView? CurrentWatch => _current as WatchView;

    private void UpdateVideoPlacement()
    {
        var onWatch = CurrentWatch != null && CurrentWatch.Video.VideoId == PlaybackTracker.Instance.Current?.VideoId;
        if (onWatch && _videoHost == null) _videoHost = CurrentWatch!.VideoHost;
        var useMini = !onWatch && _player.HasMedia && _miniEnabled && PlaybackTracker.Instance.Current != null;
        MiniHost.Visibility = useMini ? Visibility.Visible : Visibility.Collapsed;
        if (onWatch) _videoHost = CurrentWatch!.VideoHost;
        else _videoHost = useMini ? MiniHost : null;
        _lastBounds = default;
        _player.ShowVideo(_videoHost != null && !_searchHidesVideo);
        SyncVideoBounds();
        UpdateNowPlaying();
    }

    private void SyncVideoBounds()
    {
        if (_videoHost == null || Root.XamlRoot == null || _videoHost.ActualWidth < 1) return;
        var scale = Root.XamlRoot.RasterizationScale;
        var pt = _videoHost.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var r = new RectInt32((int)Math.Round(pt.X * scale), (int)Math.Round(pt.Y * scale),
            (int)Math.Round(_videoHost.ActualWidth * scale), (int)Math.Round(_videoHost.ActualHeight * scale));
        if (r.Equals(_lastBounds)) return;
        _lastBounds = r;
        _player.SetBounds(r.X, r.Y, r.Width, r.Height);
    }

    private void SetFullscreen(bool fs)
    {
        if (fs && CurrentWatch == null)
        {
            var v = PlaybackTracker.Instance.Current;
            if (v != null) OpenVideo(v, PlaybackTracker.Instance.CurrentSource ?? "home");
        }
        _fullscreen = fs;
        AppWindow.SetPresenter(fs ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TitleRow.Height = fs ? new GridLength(0) : new GridLength(52);
        TitleBar.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
        Nav.IsPaneVisible = !fs;
        CurrentWatch?.SetFullscreen(fs);
        UpdateNowPlaying();
        _lastBounds = default;
        _player.FocusVideo();
    }

    // ---------------- now playing bar ----------------

    private string? _npVideo;

    private void UpdateNowPlaying()
    {
        var cur = PlaybackTracker.Instance.Current;
        var onWatch = CurrentWatch != null && CurrentWatch.Video.VideoId == cur?.VideoId;
        var show = cur != null && _player.HasMedia && !onWatch && !_fullscreen;
        NowPlaying.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        if (_npVideo != cur!.VideoId)
        {
            _npVideo = cur.VideoId;
            NpThumb.Source = PlaybackTracker.Instance.IsLocal && cur.VideoId.Length != 11 ? null
                : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(cur.Thumb)) { DecodePixelWidth = 160 };
        }
        NpTitle.Text = string.IsNullOrEmpty(cur.Title) ? "Видео" : cur.Title;
        NpChannel.Text = cur.Channel;
        var d = _player.Duration;
        NpProgress.Value = d > 0 ? _player.TimePos / d : 0;
        NpTime.Text = $"{Fmt.Time(_player.TimePos)} / {Fmt.Time(d)}";
    }

    private void NpPause_Click(object sender, RoutedEventArgs e) => _player.TogglePause();
    private void NpBack10_Click(object sender, RoutedEventArgs e) => _player.Command("seek", "-10");
    private void NpFwd10_Click(object sender, RoutedEventArgs e) => _player.Command("seek", "10");
    private void NpClose_Click(object sender, RoutedEventArgs e)
    {
        PlaybackTracker.Instance.Stop();
        UpdateVideoPlacement();
    }
    private void NpMini_Click(object sender, RoutedEventArgs e)
    {
        _miniEnabled = NpMiniToggle.IsChecked == true;
        UpdateVideoPlacement();
    }
    private void NpOpen_Click(object sender, RoutedEventArgs e)
    {
        var v = PlaybackTracker.Instance.Current;
        if (v != null) OpenVideo(v, PlaybackTracker.Instance.CurrentSource ?? "home");
    }

    // ---------------- search ----------------

    private bool _searchHidesVideo;

    private void Search_GotFocus(object sender, RoutedEventArgs e)
    {
        // the suggestion list would open under the native video window
        if (CurrentWatch != null) { _searchHidesVideo = true; _player.ShowVideo(false); }
    }

    private void Search_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_searchHidesVideo) { _searchHidesVideo = false; _player.ShowVideo(_videoHost != null); }
    }

    private async void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        var q = sender.Text.Trim();
        if (q.Length == 0 || q.StartsWith("http")) { sender.ItemsSource = null; return; }
        try
        {
            await Task.Delay(120, cts.Token);
            var list = await InnerTube.Instance.SuggestAsync(q, cts.Token);
            if (!cts.IsCancellationRequested) sender.ItemsSource = list;
        }
        catch { }
    }

    private void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var q = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        if (q.Length == 0) return;
        var vid = ExtractYouTubeId(q);
        if (vid != null)
        {
            OpenVideo(new VideoItem { VideoId = vid }, "link");
        }
        else if (Uri.TryCreate(q, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http"))
        {
            Downloader.Instance.Enqueue(q);
            OpenRoot("downloads");
            ShowToast("Ссылка добавлена в загрузки", InfoBarSeverity.Success);
        }
        else OpenSearch(q);
        _suggestCts?.Cancel();
        sender.ItemsSource = null;
        Host.Focus(FocusState.Programmatic);
    }

    public static string? ExtractYouTubeId(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s,
            @"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|live/|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------------- account ----------------

    private void UpdateAccount()
    {
        var logged = CookieStore.IsLoggedIn;
        AccountText.Text = logged ? "Аккаунт" : "Войти";
        AccountIcon.Glyph = logged ? "" : "";
    }

    private void AccountMenu_Opening(object sender, object e)
    {
        AccountMenu.Items.Clear();
        if (CookieStore.IsLoggedIn)
        {
            AccountMenu.Items.Add(Item("Выйти из аккаунта", "", () =>
            {
                CookieStore.Clear();
                _player.ApplySettings();
                ShowToast("Вы вышли. Локальная история сохранена.", InfoBarSeverity.Informational);
            }));
        }
        else
        {
            AccountMenu.Items.Add(Item("Войти в YouTube", "", () => new LoginWindow().Activate()));
        }
        AccountMenu.Items.Add(Item("Настройки", "", () => OpenRoot("settings")));
    }

    public static MenuFlyoutItem Item(string text, string glyph, Action a)
    {
        var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        i.Click += (_, _) => a();
        return i;
    }

    // ---------------- misc ----------------

    public void ShowToast(string text, InfoBarSeverity sev = InfoBarSeverity.Informational)
    {
        Toast.Message = text;
        Toast.Severity = sev;
        Toast.IsOpen = true;
        After(TimeSpan.FromSeconds(sev == InfoBarSeverity.Error ? 9 : 4), () => { if (Toast.Message == text) Toast.IsOpen = false; });
    }

    private void UpdateTitleBarRegions()
    {
        if (Root.XamlRoot == null) return;
        var scale = Root.XamlRoot.RasterizationScale;
        RectInt32 R(FrameworkElement el)
        {
            var p = el.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
            return new RectInt32((int)(p.X * scale), (int)(p.Y * scale), (int)(el.ActualWidth * scale), (int)(el.ActualHeight * scale));
        }
        var src = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        src.SetRegionRects(NonClientRegionKind.Passthrough, [R(BackButton), R(SearchBox), R(AccountButton)]);
    }
}

internal static class Win32Dpi
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetDpiForSystem();
}
