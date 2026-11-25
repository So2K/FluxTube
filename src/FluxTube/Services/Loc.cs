namespace FluxTube.Services;

/// <summary>
/// Instant, zero-overhead RU/EN localization for the entire FluxTube UI (Native-AOT safe, live switch without restart).
/// </summary>
public static class L
{
    public static bool En => AppSettings.Current.UiLanguage.Equals("en", StringComparison.OrdinalIgnoreCase);
    public static string Code => En ? "en" : "ru";

    /// <summary>Returns the localized string for the current UI language.</summary>
    public static string T(string ru, string en) => En ? en : ru;
}
