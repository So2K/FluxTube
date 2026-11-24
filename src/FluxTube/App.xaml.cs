using System.Runtime;
using FluxTube.Player;
using FluxTube.Services;
using Microsoft.UI.Xaml;

namespace FluxTube;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;

    public App()
    {
        Paths.MigrateOldData();
        // multicore JIT: the second start replays the recorded startup methods on background cores
        ProfileOptimization.SetProfileRoot(Paths.Data);
        ProfileOptimization.StartProfile("startup.jitprofile");
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Paths.Log("UNHANDLED: " + e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Started by the native shell only for what needs a browser engine: sign-in and the TikTok tab.</summary>
    public static bool Helper { get; private set; }
    private static Window? _login;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmd = Environment.GetCommandLineArgs();
        Helper = cmd.Contains("--helper") || cmd.Contains("--login");
        if (cmd.Contains("--login"))
        {
            _login = new LoginWindow();
            _login.Closed += (_, _) => Environment.Exit(0);
            _login.Activate();
            return;
        }
        // warm everything the first click needs, in parallel, while the window appears
        _ = Task.Run(() => Db.Warm());
        if (!Helper)
            _ = Task.Run(async () =>
            {
                await Resolver.ProvisionAsync();
                Resolver.Instance.Warm();
            });
        _ = Task.Run(() => InnerTube.Instance.WarmAsync());
        if (WebEnv.ChromiumMajor() is { } major) InnerTube.ChromeMajor = major;
        Main = new MainWindow();
        Main.Activate();
    }

    public static void Shutdown()
    {
        PlaybackTracker.Instance.Flush();
        MpvPlayer.Instance.Dispose();
        Resolver.Instance.Dispose();
        AppSettings.Current.Save();
    }
}
