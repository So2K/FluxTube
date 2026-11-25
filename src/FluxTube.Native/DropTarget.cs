using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Native-AOT-safe Win32 OLE IDropTarget for dragging YouTube links, playlists, channels, URLs,
/// .url shortcuts, local media files, or search text straight from Telegram / browsers / Explorer into FluxTube.
/// Uses a manual unmanaged COM vtable (zero runtime COM marshaling / zero trimming issues).
/// </summary>
internal static unsafe class DropTarget
{
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_LINK = 4;
    private const uint MK_CONTROL = 0x0008;
    private const uint CF_TEXT = 1;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".ts", ".m4v",
        ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".opus", ".aac"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DropVtbl
    {
        public delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> QueryInterface;
        public delegate* unmanaged[Stdcall]<void*, uint> AddRef;
        public delegate* unmanaged[Stdcall]<void*, uint> Release;
        public delegate* unmanaged[Stdcall]<void*, void*, uint, long, uint*, int> DragEnter;
        public delegate* unmanaged[Stdcall]<void*, uint, long, uint*, int> DragOver;
        public delegate* unmanaged[Stdcall]<void*, int> DragLeave;
        public delegate* unmanaged[Stdcall]<void*, void*, uint, long, uint*, int> Drop;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropObj
    {
        public DropVtbl* Vtbl;
        public int RefCount;
        public nint Hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FORMATETC
    {
        public ushort cfFormat;
        public nint ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STGMEDIUM
    {
        public uint tymed;
        public nint hGlobal;
        public nint pUnkForRelease;
    }

    private static DropVtbl* _vtbl;
    private static readonly List<(nint Hwnd, nint Obj)> _attached = [];
    private static ushort _cfUrlW, _cfUrlA;
    private static bool _oleInit;

    private static nint _dragHwnd;
    private static string? _dragPayload;
    private static bool _dragCtrl;
    private static float _dragX, _dragY;

    public static bool IsDragging { get; private set; }
    public static bool DragToNewTab => _dragCtrl || (_dragY >= 0 && _dragY < Shell.TopH + Shell.TabH);
    public static bool DragBackground => _dragCtrl;
    public static string DragTitle { get; private set; } = "";
    public static string DragSubtitle { get; private set; } = "";

    public static void InitOle()
    {
        if (_oleInit) return;
        int hr = W.OleInitialize(0);
        // S_OK (0) or S_FALSE (1) both mean OLE is initialized on this STA thread
        _oleInit = hr >= 0;
        if (!_oleInit) Paths.Log($"ole init failed: 0x{hr:X8}");
    }

    public static void Attach(nint hwnd)
    {
        if (hwnd == 0 || !_oleInit) return;
        if (_vtbl == null)
        {
            _vtbl = (DropVtbl*)NativeMemory.AllocZeroed((nuint)sizeof(DropVtbl));
            _vtbl->QueryInterface = &QueryInterface;
            _vtbl->AddRef = &AddRef;
            _vtbl->Release = &Release;
            _vtbl->DragEnter = &DragEnter;
            _vtbl->DragOver = &DragOver;
            _vtbl->DragLeave = &DragLeave;
            _vtbl->Drop = &Drop;
            _cfUrlW = (ushort)W.RegisterClipboardFormatW("UniformResourceLocatorW");
            _cfUrlA = (ushort)W.RegisterClipboardFormatW("UniformResourceLocator");
        }

        var obj = (DropObj*)NativeMemory.AllocZeroed((nuint)sizeof(DropObj));
        obj->Vtbl = _vtbl;
        obj->RefCount = 1;
        obj->Hwnd = hwnd;

        int hr = W.RegisterDragDrop(hwnd, (nint)obj);
        if (hr >= 0)
        {
            _attached.Add((hwnd, (nint)obj));
        }
        else
        {
            Paths.Log($"RegisterDragDrop(0x{hwnd:X}) failed: 0x{hr:X8}");
            NativeMemory.Free(obj);
        }
    }

    public static void DetachAll()
    {
        IsDragging = false;
        _dragHwnd = 0;
        _dragPayload = null;
        for (int i = _attached.Count - 1; i >= 0; i--)
        {
            var (hwnd, obj) = _attached[i];
            try { W.RevokeDragDrop(hwnd); } catch { }
            ReleaseCore((DropObj*)obj);
        }
        _attached.Clear();
    }

    public static void ShutdownOle()
    {
        DetachAll();
        if (_oleInit)
        {
            _oleInit = false;
            try { W.OleUninitialize(); } catch { }
        }
        if (_vtbl != null)
        {
            NativeMemory.Free(_vtbl);
            _vtbl = null;
        }
    }

    private static uint AddRefCore(DropObj* obj) => (uint)Interlocked.Increment(ref obj->RefCount);

    private static uint ReleaseCore(DropObj* obj)
    {
        int n = Interlocked.Decrement(ref obj->RefCount);
        if (n <= 0) NativeMemory.Free(obj);
        return (uint)Math.Max(0, n);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return unchecked((int)0x80004003);
        if (riid != null && (*riid == IID_IUnknown || *riid == IID_IDropTarget))
        {
            *ppv = self;
            AddRefCore((DropObj*)self);
            return S_OK;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => AddRefCore((DropObj*)self);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => ReleaseCore((DropObj*)self);

    private static uint PickEffect(uint allowed)
    {
        if ((allowed & DROPEFFECT_LINK) != 0) return DROPEFFECT_LINK;
        if ((allowed & DROPEFFECT_COPY) != 0) return DROPEFFECT_COPY;
        return allowed;
    }

    private static void UpdatePoint(long ptPacked, uint grfKeyState)
    {
        var pt = new W.POINT { X = unchecked((int)(uint)ptPacked), Y = unchecked((int)(ptPacked >> 32)) };
        W.ScreenToClient(App.Hwnd, &pt);
        float sc = MathF.Max(0.5f, Gfx.Scale);
        _dragX = pt.X / sc;
        _dragY = pt.Y / sc;
        _dragCtrl = (grfKeyState & MK_CONTROL) != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragEnter(void* self, void* pDataObj, uint grfKeyState, long ptPacked, uint* pdwEffect)
    {
        try
        {
            var obj = (DropObj*)self;
            var payload = ExtractPayload(pDataObj);
            if (string.IsNullOrWhiteSpace(payload))
            {
                if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE;
                return S_OK;
            }
            _dragHwnd = obj->Hwnd;
            _dragPayload = payload;
            IsDragging = true;
            UpdatePoint(ptPacked, grfKeyState);
            Describe(payload);
            if (pdwEffect != null) *pdwEffect = PickEffect(*pdwEffect);
            App.Invalidate();
        }
        catch (Exception e)
        {
            Paths.Log("drop enter: " + e.Message);
            if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE;
        }
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragOver(void* self, uint grfKeyState, long ptPacked, uint* pdwEffect)
    {
        try
        {
            if (!IsDragging || string.IsNullOrWhiteSpace(_dragPayload))
            {
                if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE;
                return S_OK;
            }
            UpdatePoint(ptPacked, grfKeyState);
            Describe(_dragPayload);
            if (pdwEffect != null) *pdwEffect = PickEffect(*pdwEffect);
            App.Invalidate();
        }
        catch
        {
            if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE;
        }
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragLeave(void* self)
    {
        try
        {
            var obj = (DropObj*)self;
            if (_dragHwnd == obj->Hwnd)
            {
                IsDragging = false;
                _dragHwnd = 0;
                _dragPayload = null;
                App.Invalidate();
            }
        }
        catch { }
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Drop(void* self, void* pDataObj, uint grfKeyState, long ptPacked, uint* pdwEffect)
    {
        try
        {
            UpdatePoint(ptPacked, grfKeyState);
            var payload = ExtractPayload(pDataObj) ?? _dragPayload;
            bool newTab = DragToNewTab;
            bool bgTab = _dragCtrl;

            IsDragging = false;
            _dragHwnd = 0;
            _dragPayload = null;
            App.Invalidate();

            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (pdwEffect != null) *pdwEffect = PickEffect(*pdwEffect);
                var dropped = payload;
                App.Post(() => Shell.HandleDropped(dropped, newTab, bgTab));
            }
            else if (pdwEffect != null)
            {
                *pdwEffect = DROPEFFECT_NONE;
            }
        }
        catch (Exception e)
        {
            Paths.Log("drop: " + e.Message);
            IsDragging = false;
            _dragHwnd = 0;
            _dragPayload = null;
            if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE;
        }
        return S_OK;
    }

    private static void Describe(string payload)
    {
        bool newTab = DragToNewTab;
        if (payload.StartsWith("localfile:", StringComparison.Ordinal))
        {
            var file = payload[10..];
            DragTitle = "Открыть файл в плеере";
            DragSubtitle = Path.GetFileName(file);
            return;
        }

        var url = ExtractUrl(payload) ?? payload.Trim();
        var vid = Shell.ExtractYouTubeId(url);
        if (vid != null)
        {
            DragTitle = newTab
                ? (DragBackground ? "Открыть видео в фоновой вкладке" : "Открыть видео в новой вкладке")
                : "Открыть видео";
            DragSubtitle = TrimPreview(url);
        }
        else if (Regex.IsMatch(url, @"youtube\.com/playlist\?.*list=", RegexOptions.IgnoreCase))
        {
            DragTitle = newTab ? "Открыть плейлист в новой вкладке" : "Открыть плейлист";
            DragSubtitle = TrimPreview(url);
        }
        else if (Regex.IsMatch(url, @"youtube\.com/(?:channel/UC[\w-]+|@[\w.-]+)", RegexOptions.IgnoreCase))
        {
            DragTitle = newTab ? "Открыть канал в новой вкладке" : "Открыть канал";
            DragSubtitle = TrimPreview(url);
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            DragTitle = "Добавить ссылку в загрузки";
            DragSubtitle = TrimPreview(url);
        }
        else
        {
            DragTitle = newTab ? "Найти в новой вкладке" : "Найти в FluxTube";
            DragSubtitle = TrimPreview(payload.Trim());
        }
    }

    private static string TrimPreview(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 72 ? s[..71] + "…" : s;
    }

    public static string? ExtractUrl(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Value.TrimEnd('.', ',', ';', ')', ']', '}', '!', '?');
    }

    private static string? ExtractPayload(void* pDataObj)
    {
        if (pDataObj == null) return null;

        // 1. Wide URL format (Chrome, Edge, Firefox, Telegram link drag, Explorer URL)
        if (_cfUrlW != 0 && TryGetUnicodeText(pDataObj, _cfUrlW) is { Length: > 0 } uw) return uw.Trim();
        // 2. Standard Unicode text (Telegram, Discord, browsers, editors)
        if (TryGetUnicodeText(pDataObj, (ushort)CF_UNICODETEXT) is { Length: > 0 } ut) return ut.Trim();
        // 3. ANSI URL format
        if (_cfUrlA != 0 && TryGetAnsiText(pDataObj, _cfUrlA) is { Length: > 0 } ua) return ua.Trim();
        // 4. ANSI text fallback
        if (TryGetAnsiText(pDataObj, (ushort)CF_TEXT) is { Length: > 0 } at) return at.Trim();
        // 5. Dropped files (.url shortcut or local media file)
        if (TryGetDroppedFile(pDataObj) is { Length: > 0 } filePayload) return filePayload;

        return null;
    }

    private static bool TryGetData(void* pDataObj, ushort cf, out STGMEDIUM med)
    {
        med = default;
        var fmt = new FORMATETC { cfFormat = cf, ptd = 0, dwAspect = DVASPECT_CONTENT, lindex = -1, tymed = TYMED_HGLOBAL };
        var vtbl = *(void***)pDataObj;
        if (vtbl == null) return false;
        var getData = (delegate* unmanaged[Stdcall]<void*, FORMATETC*, STGMEDIUM*, int>)vtbl[3];
        STGMEDIUM local = default;
        if (getData(pDataObj, &fmt, &local) < 0) return false;
        if (local.tymed == TYMED_HGLOBAL && local.hGlobal != 0)
        {
            med = local;
            return true;
        }
        W.ReleaseStgMedium(&local);
        return false;
    }

    private static string? TryGetUnicodeText(void* pDataObj, ushort cf)
    {
        if (!TryGetData(pDataObj, cf, out var med)) return null;
        try
        {
            nuint bytes = W.GlobalSize(med.hGlobal);
            int maxChars = (int)Math.Min(bytes / 2, 8192);
            if (maxChars <= 0) return null;
            var p = (char*)W.GlobalLock(med.hGlobal);
            if (p == null) return null;
            try
            {
                int len = 0;
                while (len < maxChars && p[len] != '\0') len++;
                return len > 0 ? new string(p, 0, len) : null;
            }
            finally { W.GlobalUnlock(med.hGlobal); }
        }
        finally { W.ReleaseStgMedium(&med); }
    }

    private static string? TryGetAnsiText(void* pDataObj, ushort cf)
    {
        if (!TryGetData(pDataObj, cf, out var med)) return null;
        try
        {
            nuint bytes = W.GlobalSize(med.hGlobal);
            int maxLen = (int)Math.Min(bytes, 8192);
            if (maxLen <= 0) return null;
            var p = (byte*)W.GlobalLock(med.hGlobal);
            if (p == null) return null;
            try
            {
                int len = 0;
                while (len < maxLen && p[len] != 0) len++;
                return len > 0 ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(p, len)) : null;
            }
            finally { W.GlobalUnlock(med.hGlobal); }
        }
        finally { W.ReleaseStgMedium(&med); }
    }

    private static string? TryGetDroppedFile(void* pDataObj)
    {
        if (!TryGetData(pDataObj, (ushort)CF_HDROP, out var med)) return null;
        try
        {
            uint count = W.DragQueryFileW(med.hGlobal, 0xFFFFFFFF, null, 0);
            if (count == 0) return null;
            uint needed = W.DragQueryFileW(med.hGlobal, 0, null, 0);
            if (needed == 0 || needed > 4096) return null;
            var buf = stackalloc char[(int)needed + 1];
            uint got = W.DragQueryFileW(med.hGlobal, 0, buf, needed + 1);
            if (got == 0) return null;
            var path = new string(buf, 0, (int)got);
            if (!File.Exists(path)) return null;

            var ext = Path.GetExtension(path);
            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return line[4..].Trim();
                }
                return null;
            }
            if (MediaExts.Contains(ext)) return "localfile:" + path;
            return null;
        }
        catch { return null; }
        finally { W.ReleaseStgMedium(&med); }
    }
}
