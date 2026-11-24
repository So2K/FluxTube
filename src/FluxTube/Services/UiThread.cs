namespace FluxTube.Services;

/// <summary>How background code gets back to the UI thread; each shell (native, WinUI helper) plugs in its own.</summary>
public static class UiThread
{
    public static Action<Action> Post = a => a();
}