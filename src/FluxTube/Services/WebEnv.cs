using Microsoft.Web.WebView2.Core;

namespace FluxTube.Services;

/// <summary>One shared WebView2 profile for the login window and the TikTok tab (logins persist there).</summary>
public static class WebEnv
{
    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> GetAsync()
        => _env ??= CoreWebView2Environment.CreateWithOptionsAsync(null, Paths.WebViewData,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection" }).AsTask();

    /// <summary>Current Chromium major on this machine, so our User-Agent stays in step with real browsers.</summary>
    public static int? ChromiumMajor()
    {
        try
        {
            var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return int.TryParse(v?.Split('.')[0], out var m) ? m : null;
        }
        catch { return null; }
    }
}
