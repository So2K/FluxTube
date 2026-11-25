using System.Text.RegularExpressions;
using Vortice.DirectWrite;

namespace FluxTube;

/// <summary>A clickable piece inside a text: a link, a time code, a hashtag.</summary>
public readonly record struct TextLink(int Start, int Length, char Kind, string Value, double Seconds);

/// <summary>
/// Text that behaves like text on a web page: drag to select, double-click for a word, Ctrl+C or the right-click
/// menu to copy; links, time codes and hashtags inside it are coloured and clickable.
/// One selection exists at a time. Everything is asked from the DirectWrite layout that is drawn anyway
/// (hit-testing gives the character under the pointer and the rectangles of a range), so text that is not
/// being selected costs exactly what plain text costs.
/// </summary>
public static partial class Sel
{
    private static int _id;
    private static int _a, _b;                 // anchor and caret, UTF-16 positions in _text
    private static string _text = "";
    private static bool _dragging, _moved;
    private static long _downAt, _claimedFrame = -1;
    private static int _downPos;
    private static HitTestMetrics[] _rects = new HitTestMetrics[64];

    public static bool Has => _id != 0 && _a != _b;
    public static string Selected => Has ? _text.Substring(Math.Min(_a, _b), Math.Abs(_a - _b)) : "";
    public static bool WantIBeam { get; private set; }
    public static void RequestIBeam() => WantIBeam = true;

    public static void Clear() { _id = 0; _a = _b = 0; _dragging = false; }

    public static void BeginFrame() => WantIBeam = false;

    /// <summary>End of the frame: a press that landed on no selectable text drops the selection.</summary>
    public static void EndFrame()
    {
        if (UI.Pressed && _claimedFrame != Gfx.Frame && _id != 0) { Clear(); App.Invalidate(); }
        if (_dragging && !UI.Down) _dragging = false;
    }

    public static bool CopySelection()
    {
        if (!Has) return false;
        W.SetClipboardText(App.Hwnd, Selected);
        Shell.Toast(Services.L.T("Скопировано", "Copied"), Th.Good, 1500);
        return true;
    }

    /// <summary>A right-click menu over text: "copy selection" comes first when something is selected.</summary>
    public static void Popup(List<MenuItem> items) { AddMenu(items); Menus.Popup(items); }

    /// <summary>Menu entry for the right-click menus of whatever holds selectable text.</summary>
    public static void AddMenu(List<MenuItem> items)
    {
        if (!Has) return;
        var s = Selected;
        items.Insert(0, new(Services.L.T("Копировать выделенное", "Copy selection"), () => { W.SetClipboardText(App.Hwnd, s); Shell.Toast(Services.L.T("Скопировано", "Copied"), Th.Good, 1500); }, icon: Th.Copy, shortcut: "Ctrl+C"));
    }

    private static int PosAt(IDWriteTextLayout layout, float x, float y, int len)
    {
        layout.HitTestPoint(x, y, out var trailing, out _, out var m);
        return Math.Clamp((int)m.TextPosition + (trailing ? (int)m.Length : 0), 0, len);
    }

    private static int Ranges(IDWriteTextLayout layout, int start, int length, float x, float y)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var hr = layout.HitTestTextRange((uint)start, (uint)length, x, y, _rects, out var n);
            if (hr.Success) return (int)n;
            _rects = new HitTestMetrics[Math.Max((int)n, _rects.Length * 2)];
        }
        return 0;
    }

    /// <summary>
    /// Draws text at (x, y) and makes it selectable. With links, the clickable pieces are coloured and
    /// onLink is called when one is clicked (a click, not a drag).
    /// </summary>
    public static TextRun Text(int id, string text, float x, float y, float size, uint color, int weight = 400, float maxW = 0, int lines = 1,
        bool links = false, Action<TextLink>? onLink = null)
    {
        var run = Gfx.Text(text, size, weight, maxW, lines);
        var layout = run.Layout;
        if (layout == null || text.Length == 0) return run; // not shaped yet: next frame

        var rect = new R(x, y, run.W, run.H);
        var hover = UI.Hover(rect);
        TextLink[]? found = links ? LinksOf(text) : null;
        if (found is { Length: > 0 }) Colour(run, found);

        // which link is under the pointer
        int hot = -1;
        if (hover && found is { Length: > 0 } && !(_dragging && _moved))
        {
            layout.HitTestPoint(UI.MX - x, UI.MY - y, out _, out var inside, out var hm);
            if (inside)
                for (int i = 0; i < found.Length; i++)
                    if (hm.TextPosition >= found[i].Start && hm.TextPosition < found[i].Start + found[i].Length) { hot = i; break; }
        }
        if (hover) { if (hot >= 0) UI.Hand(); else WantIBeam = true; }

        if (UI.Pressed && hover)
        {
            _claimedFrame = Gfx.Frame;
            var pos = PosAt(layout, UI.MX - x, UI.MY - y, text.Length);
            var dbl = _id == id && UI.Now - _downAt < 400 && Math.Abs(pos - _downPos) <= 1;
            _id = id; _text = text; _downAt = UI.Now; _downPos = pos; _moved = false;
            if (dbl)
            {
                // the word under the pointer
                int s = pos, e = pos;
                while (s > 0 && IsWord(text[s - 1])) s--;
                while (e < text.Length && IsWord(text[e])) e++;
                _a = s; _b = e; _dragging = false;
            }
            else { _a = _b = pos; _dragging = true; }
        }
        else if (_dragging && _id == id && UI.Down)
        {
            var pos = PosAt(layout, UI.MX - x, UI.MY - y, text.Length);
            if (pos != _b) { _b = pos; _moved = true; }
        }
        // a click that did not turn into a selection activates the link under it
        if (UI.Released && _id == id && !_moved && hot >= 0 && _a == _b && found != null)
        {
            var l = found[hot];
            App.Post(() => onLink?.Invoke(l));
        }

        if (_id == id && _a != _b && _text == text)
        {
            int s = Math.Min(_a, _b), n = Ranges(layout, Math.Min(_a, _b), Math.Abs(_a - _b), x, y);
            var hl = Gfx.Fade(Th.Brand, Th.Light ? 0.28f : 0.42f);
            for (int i = 0; i < n; i++) Gfx.Fill(new R(_rects[i].Left, _rects[i].Top, _rects[i].Width, _rects[i].Height), hl);
        }
        Gfx.Draw(run, x, y, color);
        if (hot >= 0)
        {
            int n = Ranges(layout, found![hot].Start, found[hot].Length, x, y);
            for (int i = 0; i < n; i++) Gfx.Fill(new R(_rects[i].Left, _rects[i].Top + _rects[i].Height - 2, _rects[i].Width, 1), LinkColor);
        }
        return run;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '-';

    // ---------------- links ----------------

    public static uint LinkColor => Th.Light ? 0xFF065FD4 : 0xFF3EA6FF;

    [GeneratedRegex(@"https?://[^\s<>""]+|(?<![\w/])#[\p{L}\p{Nd}_]{2,}|(?<![0-9:])(?:[0-9]{1,2}:)?[0-9]{1,2}:[0-9]{2}(?![0-9:])")]
    private static partial Regex LinkRx();

    private static readonly Dictionary<string, TextLink[]> _links = new();

    /// <summary>The clickable pieces of a text, found once per text.</summary>
    public static TextLink[] LinksOf(string text)
    {
        if (_links.TryGetValue(text, out var known)) return known;
        if (_links.Count > 400) _links.Clear();
        var list = new List<TextLink>();
        foreach (Match m in LinkRx().Matches(text))
        {
            var v = m.Value;
            if (v[0] == 'h')
            {
                v = v.TrimEnd('.', ',', ')', '!', '?', ';', ':', '»', '"', '\'');
                list.Add(new TextLink(m.Index, v.Length, 'u', v, 0));
            }
            else if (v[0] == '#') list.Add(new TextLink(m.Index, v.Length, '#', v, 0));
            else
            {
                double sec = 0;
                bool ok = true;
                var parts = v.Split(':');
                foreach (var part in parts)
                {
                    if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var num))
                    { ok = false; break; }
                    sec = sec * 60 + num;
                }
                if (ok) list.Add(new TextLink(m.Index, v.Length, 't', v, sec));
            }
        }
        return _links[text] = list.ToArray();
    }

    // layouts are shared through the text cache: remember which ones already carry link colours, and for which theme + device generation
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextRun, object> _coloured = new();

    private static void Colour(TextRun run, TextLink[] links)
    {
        var stamp = (Th.Light, Gfx.DeviceGeneration);
        if (_coloured.TryGetValue(run, out var have) && have is ValueTuple<bool, int> prev && prev == stamp) return;
        var brush = Gfx.Brush(LinkColor);
        foreach (var l in links) run.Layout!.SetDrawingEffect(brush, new TextRange { StartPosition = (uint)l.Start, Length = (uint)l.Length });
        _coloured.AddOrUpdate(run, stamp);
    }
}
