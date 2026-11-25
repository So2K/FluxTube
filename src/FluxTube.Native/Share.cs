using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// YouTube's share panel: the link in a box you can select, a copy button and a "start at …" checkbox.
/// The moment is fixed when the panel opens, so the link does not drift while it is on screen.
/// </summary>
public static class SharePanel
{
    public const float W = 470, H = 162;

    private static Edit? _box;
    private static string _id = "";
    private static int _at;
    private static bool _withTime;
    private static long _openedAt;
    private static bool _syncNeeded;

    public static bool Open { get; private set; }
    public static R Rect { get; private set; }

    public static void Show(string videoId, double atSeconds)
    {
        _id = videoId;
        _at = (int)Math.Max(0, atSeconds);
        _withTime = false;
        _openedAt = UI.Now;
        _syncNeeded = true;
        Open = true;
        App.Invalidate();
    }

    public static void Close()
    {
        if (!Open) return;
        Open = false;
        Rect = default;
        _box?.Hide();
        App.Invalidate();
    }

    private static string Url => _withTime && _at > 0 ? $"https://youtu.be/{_id}?t={_at}" : $"https://youtu.be/{_id}";

    private static void Sync()
    {
        if (_box != null && _syncNeeded)
        {
            _syncNeeded = false;
            _box.Text = Url;
            if (!_box.Focused) _box.ShowStart();
        }
    }

    /// <summary>Draws the panel at r (W x H). Call after the page content, outside any scroll clip.</summary>
    public static void Draw(R r)
    {
        if (!Open) return;
        if (_box == null)
        {
            _box = new Edit("");
            _box.Escape = Close;
            _box.Enter = () => { Copy(); Close(); };
        }
        // rises into place
        var t = Math.Clamp((UI.Now - _openedAt) / 150f, 0, 1);
        if (t < 1) UI.Animate();
        var ease = 1 - (1 - t) * (1 - t);
        r = new R(r.X, r.Y + (1 - ease) * 10, r.W, r.H);
        Rect = r;

        Gfx.Fill(new R(r.X + 2, r.Y + 4, r.W - 4, r.H), Gfx.Fade(0xFF000000, 0.35f * ease), 16);
        Gfx.Fill(r, Gfx.Fade(Th.Menu, ease), 16);
        Gfx.Stroke(r, Gfx.Fade(Th.A(0.15f), ease), 16);

        Gfx.Draw(L.T("Поделиться", "Share"), r.X + 20, r.Y + 16, 16, Gfx.Fade(Th.Text, ease), 600);
        if (UI.IconButton(UI.Id("share:x"), new R(r.Right - 44, r.Y + 10, 34, 34), Th.Close, 13, Gfx.Fade(Th.Sub, ease))) { Close(); return; }

        // link box, selectable, with a copy button
        var copyLbl = L.T("Копировать", "Copy");
        var copyW = UI.PillWidth(null, copyLbl);
        var box = new R(r.X + 20, r.Y + 52, r.W - 40 - copyW - 10, 40);
        Gfx.Fill(box, Th.Field, 10);
        Gfx.Stroke(box, Th.A(0.1f), 10);
        Sync();
        _box.Place(box, 14, 14);
        if (UI.Pill(UI.Id("share:copy"), box.Right + 10, box.Y + 2, 36, null, copyLbl, out _, accent: true))
        {
            Copy();
            Close();
            return;
        }

        var label = _at > 0 ? L.T("Начать с ", "Start at ") + Fmt.Time(_at) : L.T("Видео в начале", "Video at start");
        var check = new R(r.X + 20, r.Y + 106, r.W - 40, 30);
        if (UI.Check(UI.Id("share:t"), check, _withTime && _at > 0, label, enabled: _at > 0) && _at > 0)
        {
            _withTime = !_withTime;
            _box.Text = Url; // the box may have been edited: put the current link back
            if (!_box.Focused) _box.ShowStart();
        }

        // a click anywhere else closes it (the rest of the page is blocked while this is open)
        if (UI.Pressed && !UI.Hover(r)) Close();
    }

    private static void Copy()
    {
        var text = _box?.Text is { Length: > 0 } s ? s : Url;
        W32Clipboard(text);
        Shell.Toast(_withTime && _at > 0 ? L.T($"Ссылка с меткой {Fmt.Time(_at)} скопирована", $"Link at {Fmt.Time(_at)} copied") : L.T("Ссылка скопирована", "Link copied"), Th.Good);
    }

    private static void W32Clipboard(string text) => FluxTube.W.SetClipboardText(App.Hwnd, text);

    /// <summary>The link the rest of the app copies, so every "copy link" means the same thing.</summary>
    public static string LinkFor(string videoId, double atSeconds = 0)
        => atSeconds > 1 ? $"https://youtu.be/{videoId}?t={(int)atSeconds}" : $"https://youtu.be/{videoId}";
}
