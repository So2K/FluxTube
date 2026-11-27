using System.Runtime.InteropServices;

namespace FluxTube.Player;

internal static partial class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    public enum Format { None = 0, String = 1, OsdString = 2, Flag = 3, Int64 = 4, Double = 5, Node = 6, NodeArray = 7, NodeMap = 8, ByteArray = 9 }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvNode
    {
        public long u;
        public Format format;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MpvNodeList
    {
        public int num;
        public MpvNode* values;
        public byte** keys;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MpvByteArray
    {
        public byte* data;
        public nuint size;
    }

    public enum EventId
    {
        None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4, CommandReply = 5,
        StartFile = 6, EndFile = 7, FileLoaded = 8, ClientMessage = 16, VideoReconfig = 17, AudioReconfig = 18,
        Seek = 20, PlaybackRestart = 21, PropertyChange = 22, QueueOverflow = 24, Hook = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public EventId event_id;
        public int error;
        public ulong reply_userdata;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public IntPtr name;
        public Format format;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventEndFile
    {
        public int reason; // 0 eof, 2 stop, 3 quit, 4 error, 5 redirect
        public int error;
        public long playlist_entry_id;
        public long playlist_insert_id;
        public int playlist_insert_num_entries;
    }

    [LibraryImport(Lib)] public static partial IntPtr mpv_create();
    [LibraryImport(Lib)] public static partial int mpv_initialize(IntPtr ctx);
    [LibraryImport(Lib)] public static partial void mpv_terminate_destroy(IntPtr ctx);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mpv_set_option_string(IntPtr ctx, string name, string data);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mpv_set_property_string(IntPtr ctx, string name, string data);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mpv_get_property(IntPtr ctx, string name, Format format, out double data);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mpv_get_property(IntPtr ctx, string name, Format format, out long data);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr mpv_get_property_string(IntPtr ctx, string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mpv_observe_property(IntPtr ctx, ulong userdata, string name, Format format);
    [LibraryImport(Lib)] public static partial int mpv_command(IntPtr ctx, IntPtr[] args);
    [LibraryImport(Lib)] public static partial int mpv_command_async(IntPtr ctx, ulong userdata, IntPtr[] args);
    [LibraryImport(Lib)] public static unsafe partial int mpv_command_node_async(IntPtr ctx, ulong userdata, MpvNode* args);
    [LibraryImport(Lib)] public static partial IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [LibraryImport(Lib)] public static partial void mpv_wakeup(IntPtr ctx);
    [LibraryImport(Lib)] public static partial void mpv_free(IntPtr data);
    [LibraryImport(Lib)] public static partial IntPtr mpv_error_string(int error);

    public static string? PtrToUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
