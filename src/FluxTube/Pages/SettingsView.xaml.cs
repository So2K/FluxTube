using System.Diagnostics;
using FluxTube.Player;
using FluxTube.Services;
using FluxTube.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluxTube.Pages;

public sealed partial class SettingsView : UserControl, INavPage
{
    private bool _loading;
    public string NavTag => "settings";

    public SettingsView()
    {
        InitializeComponent();
        foreach (var q in AppSettings.Qualities)
        {
            if (q != "audio") PlayQuality.Items.Add(new ComboBoxItem { Content = VideoMenu.QualityName(q), Tag = q });
            DownloadQuality.Items.Add(new ComboBoxItem { Content = VideoMenu.QualityName(q), Tag = q });
        }
    }

    public void OnShown()
    {
        _loading = true;
        var s = AppSettings.Current;
        Select(PlayQuality, s.PlayerQuality);
        Select(DownloadQuality, s.DownloadQuality);
        SponsorBlock.IsOn = s.SponsorBlock;
        SbSponsor.IsChecked = s.SponsorCategories.Contains("sponsor");
        SbSelfpromo.IsChecked = s.SponsorCategories.Contains("selfpromo");
        SbInteraction.IsChecked = s.SponsorCategories.Contains("interaction");
        SbIntro.IsChecked = s.SponsorCategories.Contains("intro");
        SbOutro.IsChecked = s.SponsorCategories.Contains("outro");
        Dislikes.IsOn = s.ReturnDislikes;
        HideShorts.IsOn = s.HideShorts;
        MarkWatched.IsOn = s.MarkWatched;
        DownloadDir.Text = s.DownloadDir;
        SbOnDownload.IsOn = s.SponsorBlockOnDownload;
        Impersonate.IsOn = s.Impersonate;
        Proxy.Text = s.Proxy;
        Hl.Text = s.Hl;
        Gl.Text = s.Gl;
        _loading = false;
        UpdateAccount();
        _ = ShowVersionAsync();
    }

    public void OnHidden() { }

    private static void Select(ComboBox box, string tag)
        => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);

    private static string TagOf(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void Any_Changed(object sender, RoutedEventArgs e) => Save();
    private void Any_Changed(object sender, SelectionChangedEventArgs e) => Save();

    private void Save()
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.PlayerQuality = TagOf(PlayQuality, s.PlayerQuality);
        s.DownloadQuality = TagOf(DownloadQuality, s.DownloadQuality);
        s.SponsorBlock = SponsorBlock.IsOn;
        var cats = new List<string>();
        if (SbSponsor.IsChecked == true) cats.Add("sponsor");
        if (SbSelfpromo.IsChecked == true) cats.Add("selfpromo");
        if (SbInteraction.IsChecked == true) cats.Add("interaction");
        if (SbIntro.IsChecked == true) cats.Add("intro");
        if (SbOutro.IsChecked == true) cats.Add("outro");
        s.SponsorCategories = [.. cats];
        s.ReturnDislikes = Dislikes.IsOn;
        s.HideShorts = HideShorts.IsOn;
        s.MarkWatched = MarkWatched.IsOn;
        if (!string.IsNullOrWhiteSpace(DownloadDir.Text)) s.DownloadDir = DownloadDir.Text.Trim();
        s.SponsorBlockOnDownload = SbOnDownload.IsOn;
        s.Impersonate = Impersonate.IsOn;
        s.Proxy = Proxy.Text.Trim();
        s.Hl = string.IsNullOrWhiteSpace(Hl.Text) ? "ru" : Hl.Text.Trim();
        s.Gl = Gl.Text.Trim().ToUpperInvariant();
        s.Save();
        MpvPlayer.Instance.ApplySettings();
    }

    private void UpdateAccount()
    {
        var on = CookieStore.IsLoggedIn;
        AccountState.Text = on ? "Вы вошли в YouTube. Подписки, «Смотреть позже» и рекомендации берутся из вашего аккаунта."
                               : "Вы не вошли. Всё работает, но без подписок и персональных рекомендаций.";
        LoginBtn.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LogoutBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var w = new LoginWindow();
        w.Closed += (_, _) => DispatcherQueue.TryEnqueue(UpdateAccount);
        w.Activate();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        CookieStore.Clear();
        MpvPlayer.Instance.ApplySettings();
        UpdateAccount();
    }

    private async void ImportCookies_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, App.Main.Hwnd);
        picker.FileTypeFilter.Add(".txt");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        ImportFrom(file.Path);
    }

    private async void Firefox_Click(object sender, RoutedEventArgs e)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fluxtube-ff-cookies.txt");
        try
        {
            var psi = new ProcessStartInfo(Paths.YtDlp) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in new[] { "--cookies-from-browser", "firefox", "--cookies", tmp, "--skip-download", "--no-warnings", "--flat-playlist", "https://www.youtube.com/feed/history" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (!File.Exists(tmp)) { App.Main.ShowToast("Firefox не найден или в нём нет входа: " + err.Trim(), InfoBarSeverity.Warning); return; }
            ImportFrom(tmp);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private void ImportFrom(string path)
    {
        // keep only Google/YouTube cookies, nothing else from the browser jar
        var keep = File.ReadAllLines(path).Where(l =>
        {
            var d = l.StartsWith("#HttpOnly_") ? l[10..] : l;
            if (d.StartsWith('#') || d.Length == 0) return false;
            var dom = d.Split('\t')[0].TrimStart('.');
            return dom.EndsWith("youtube.com") || dom.EndsWith("google.com");
        });
        File.WriteAllLines(Paths.Cookies, new[] { "# Netscape HTTP Cookie File" }.Concat(keep));
        CookieStore.Save(CookieStore.All());
        MpvPlayer.Instance.ApplySettings();
        UpdateAccount();
        App.Main.ShowToast(CookieStore.IsLoggedIn ? "Вход импортирован" : "В файле нет входа в YouTube",
            CookieStore.IsLoggedIn ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void PickDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, App.Main.Hwnd);
        picker.FileTypeFilter.Add("*");
        var f = await picker.PickSingleFolderAsync();
        if (f == null) return;
        DownloadDir.Text = f.Path;
        Save();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        AppSettings.Current.LastYtDlpUpdate = default;
        await Task.Run(YtDlp.UpdateIfStaleAsync);
        await ShowVersionAsync();
        UpdateBtn.IsEnabled = true;
    }

    private async Task ShowVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(Paths.YtDlp, "--version") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            YtDlpVersion.Text = "yt-dlp " + (await p.StandardOutput.ReadToEndAsync()).Trim();
        }
        catch (Exception ex) { YtDlpVersion.Text = "yt-dlp: " + ex.Message; }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{Paths.Data}\"");
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var log = Path.Combine(Paths.Logs, "app.log");
        if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }
}
