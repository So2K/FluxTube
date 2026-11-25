using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Ultra-lightweight Native-AOT real-time audio spectrum &amp; waveform analyzer via Win32 Per-Process WASAPI Loopback.
/// Captures ONLY FluxTube's own audio process tree (Windows 10 2004+ / Windows 11 VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK),
/// completely ignoring Discord, games, Telegram, and other system apps! Falls back to default loopback on older OSes.
/// Zero CPU/audio activity when idle, paused, minimized, or off-screen.
/// </summary>
public static unsafe class AudioAnalyzer
{
    public const int BandCount = 32;
    public const int WavePoints = 64;
    private const int FftSize = 512;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IID_IActivateAudioInterfaceCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");

    private static readonly float[] _targetBands = new float[BandCount];
    private static readonly float[] _smoothBands = new float[BandCount];
    private static readonly float[] _peakBands = new float[BandCount];
    private static readonly float[] _targetWave = new float[WavePoints];
    private static readonly float[] _smoothWave = new float[WavePoints];
    private static readonly object _dataLock = new();

    private static readonly ManualResetEventSlim _wake = new(false);
    private static Thread? _thread;
    private static volatile bool _disposed, _started;
    private static long _lastNeededAt, _lastForcedAt;
    private static bool _loggedMode;
    private static float _bassEnergy, _midEnergy, _highEnergy;
    private static long _lastStepFrame = -1;

    public static float BassEnergy => _bassEnergy;
    public static float MidEnergy => _midEnergy;
    public static float HighEnergy => _highEnergy;
    public static ReadOnlySpan<float> Bands => _smoothBands;
    public static ReadOnlySpan<float> Peaks => _peakBands;
    public static ReadOnlySpan<float> Wave => _smoothWave;

    private static bool IsPlayingAudio()
    {
        var p = MpvPlayer.Instance;
        return (p.HasMedia && !p.Paused && !p.Muted) || BgPlayers.ActiveCount > 0;
    }

    /// <summary>Called only by visible UI panels that want live spectrum/wave data this frame. Pass force=true for EQ studio / MusicBounce.</summary>
    public static void RequestFrame(bool force = false)
    {
        if (_disposed || App.Test) return;
        if (!force && AppSettings.Current.VisualizerStyle == "off") return;
        if (W.IsIconic(App.Hwnd) || Gfx.Occluded) return;

        bool playing = IsPlayingAudio();
        if (playing)
        {
            var now = Environment.TickCount64;
            Volatile.Write(ref _lastNeededAt, now);
            if (force) Volatile.Write(ref _lastForcedAt, now);
            EnsureThread();
            _wake.Set();
        }
        StepSmoothing(playing);
    }

    private static void EnsureThread()
    {
        if (_started || _disposed) return;
        lock (_dataLock)
        {
            if (_started || _disposed) return;
            _started = true;
            _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "flux-wasapi-fft", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    private static void StepSmoothing(bool playing)
    {
        if (_lastStepFrame == Gfx.Frame) return;
        _lastStepFrame = Gfx.Frame;

        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.05);
        float rise = 1f - MathF.Exp(-dt * 28f);
        float fall = 1f - MathF.Exp(-dt * 11f);
        float peakFall = dt * 0.42f;
        float waveLerp = 1f - MathF.Exp(-dt * 24f);
        bool anyMotion = playing;

        lock (_dataLock)
        {
            float bass = 0, mid = 0, high = 0;
            for (int i = 0; i < BandCount; i++)
            {
                float target = playing ? _targetBands[i] : 0f;
                float cur = _smoothBands[i];
                float next = target > cur ? cur + (target - cur) * rise : cur + (target - cur) * fall;
                if (next < 0.002f) next = 0f;
                _smoothBands[i] = next;

                if (next >= _peakBands[i]) _peakBands[i] = next;
                else _peakBands[i] = MathF.Max(0f, _peakBands[i] - peakFall);

                if (next > 0.003f || _peakBands[i] > 0.003f) anyMotion = true;
                if (i < 6) bass += next;
                else if (i < 18) mid += next;
                else high += next;
            }
            _bassEnergy = Math.Clamp(bass / 6f, 0f, 1f);
            _midEnergy = Math.Clamp(mid / 12f, 0f, 1f);
            _highEnergy = Math.Clamp(high / (BandCount - 18f), 0f, 1f);

            for (int i = 0; i < WavePoints; i++)
            {
                float target = playing ? _targetWave[i] : 0f;
                float next = _smoothWave[i] + (target - _smoothWave[i]) * waveLerp;
                if (MathF.Abs(next) < 0.001f) next = 0f;
                _smoothWave[i] = next;
                if (MathF.Abs(next) > 0.002f) anyMotion = true;
            }
        }
        if (anyMotion) UI.Animate();
    }

    public static void Shutdown()
    {
        _disposed = true;
        _wake.Set();
    }

    // ---------------- Native-AOT Per-Process WASAPI Loopback + 512-pt Radix-2 FFT ----------------

    [DllImport("mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, Guid* riid, PropVariantBlob* activationParams, void* completionHandler, void** activationOperation);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint CreateEventW(nint attrs, int manualReset, int initialState, nint name);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int SetEvent(nint hEvent);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint ms);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AudioClientActivationParams
    {
        public uint ActivationType;      // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        public uint TargetProcessId;     // Environment.ProcessId
        public uint ProcessLoopbackMode; // 0 = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;                // 65 = VT_BLOB
        public ushort R1, R2, R3;
        public uint CbSize;
        public uint Pad;
        public void* PBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationHandler
    {
        public void** Vtbl;
        public int RefCount;
        public int ActivateHr;
        public void* Activated;
        public nint DoneEvent;
    }

    private static readonly void** _handlerVtbl = InitHandlerVtbl();

    private static void** InitHandlerVtbl()
    {
        var v = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[Stdcall]<ActivationHandler*, Guid*, void**, int>)&HandlerQueryInterface;
        v[1] = (delegate* unmanaged[Stdcall]<ActivationHandler*, uint>)&HandlerAddRef;
        v[2] = (delegate* unmanaged[Stdcall]<ActivationHandler*, uint>)&HandlerRelease;
        v[3] = (delegate* unmanaged[Stdcall]<ActivationHandler*, void*, int>)&HandlerActivateCompleted;
        return v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandlerQueryInterface(ActivationHandler* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return unchecked((int)0x80004003);
        var g = *riid;
        if (g == IID_IUnknown || g == IID_IAgileObject || g == IID_IActivateAudioInterfaceCompletionHandler)
        {
            Interlocked.Increment(ref self->RefCount);
            *ppv = self;
            return 0;
        }
        *ppv = null;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerAddRef(ActivationHandler* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerRelease(ActivationHandler* self)
    {
        int rc = Interlocked.Decrement(ref self->RefCount);
        if (rc == 0)
        {
            if (self->Activated != null) { ReleaseCom(self->Activated); self->Activated = null; }
            if (self->DoneEvent != 0) { CloseHandle(self->DoneEvent); self->DoneEvent = 0; }
            NativeMemory.Free(self);
        }
        return (uint)Math.Max(0, rc);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandlerActivateCompleted(ActivationHandler* self, void* asyncOp)
    {
        try
        {
            if (asyncOp != null)
            {
                var vtbl = *(void***)asyncOp;
                var getResult = (delegate* unmanaged[Stdcall]<void*, int*, void**, int>)vtbl[3];
                int hrRes = unchecked((int)0x80004005);
                void* unk = null;
                if (getResult(asyncOp, &hrRes, &unk) >= 0)
                {
                    self->ActivateHr = hrRes;
                    self->Activated = unk;
                }
            }
        }
        catch { }
        if (self->DoneEvent != 0) SetEvent(self->DoneEvent);
        return 0;
    }

    /// <summary>Opens a Windows 10/11 per-process WASAPI loopback IAudioClient capturing ONLY FluxTube's process tree.</summary>
    private static bool TryOpenProcessLoopback(out void* client, out ushort channels, out uint sampleRate, out ushort bits, out bool isFloat)
    {
        client = null; channels = 2; sampleRate = 48000; bits = 32; isFloat = true;
        var handler = (ActivationHandler*)NativeMemory.AllocZeroed(1, (nuint)sizeof(ActivationHandler));
        handler->Vtbl = _handlerVtbl;
        handler->RefCount = 1;
        handler->ActivateHr = unchecked((int)0x80004005);
        handler->DoneEvent = CreateEventW(0, 1, 0, 0);
        void* asyncOp = null;
        try
        {
            var actParams = new AudioClientActivationParams
            {
                ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = (uint)Environment.ProcessId,
                ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            };
            var pv = new PropVariantBlob
            {
                Vt = 65, // VT_BLOB
                CbSize = (uint)sizeof(AudioClientActivationParams),
                PBlobData = &actParams,
            };
            var iid = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", &iid, &pv, handler, &asyncOp);
            if (hr < 0) return false;
            if (WaitForSingleObject(handler->DoneEvent, 2500) != 0) return false;
            if (handler->ActivateHr < 0 || handler->Activated == null) return false;

            // QueryInterface for IID_IAudioClient (GetActivateResult returns IUnknown*)
            void* unk = handler->Activated;
            handler->Activated = null;
            void* cli = null;
            var unkVtbl = *(void***)unk;
            var qiFn = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)unkVtbl[0];
            var iidCli = IID_IAudioClient;
            int qhr = qiFn(unk, &iidCli, &cli);
            ReleaseCom(unk);
            if (qhr < 0 || cli == null) return false;

            var cliVtbl = *(void***)cli;
            var initFn = (delegate* unmanaged[Stdcall]<void*, int, uint, long, long, void*, Guid*, int>)cliVtbl[3];
            var wfxF = new WaveFormatEx
            {
                FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                Channels = 2,
                SamplesPerSec = 48000,
                BitsPerSample = 32,
                BlockAlign = 8,
                AvgBytesPerSec = 48000 * 8,
                CbSize = 0,
            };
            // Per-process loopback: try 32-bit float (AUTOCONVERTPCM then plain LOOPBACK), then 16-bit PCM fallback
            int ihr = initFn(cli, 0, 0x88020000u, 500_000, 0, &wfxF, null);
            if (ihr < 0) ihr = initFn(cli, 0, 0x00020000u, 500_000, 0, &wfxF, null);
            if (ihr < 0)
            {
                var wfxP = new WaveFormatEx
                {
                    FormatTag = 1, // WAVE_FORMAT_PCM
                    Channels = 2,
                    SamplesPerSec = 48000,
                    BitsPerSample = 16,
                    BlockAlign = 4,
                    AvgBytesPerSec = 48000 * 4,
                    CbSize = 0,
                };
                ihr = initFn(cli, 0, 0x88020000u, 500_000, 0, &wfxP, null);
                if (ihr < 0) ihr = initFn(cli, 0, 0x00020000u, 500_000, 0, &wfxP, null);
                if (ihr >= 0) { bits = 16; isFloat = false; }
            }
            if (ihr < 0)
            {
                ReleaseCom(cli);
                return false;
            }
            client = cli;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseCom(asyncOp);
            ReleaseCom(handler);
        }
    }

    /// <summary>Headless self-test for Windows 10/11 per-process WASAPI loopback activation (zero audio, zero UI).</summary>
    public static int SelfTestProcessLoopback()
    {
        int hrInit = W.CoInitializeEx(0, 0 /*COINIT_MULTITHREADED*/);
        void* client = null, capture = null;
        try
        {
            if (!TryOpenProcessLoopback(out client, out var ch, out var sr, out var bits, out var isFloat) || client == null)
            {
                Console.WriteLine("PROCESS_LOOPBACK_FAIL: TryOpenProcessLoopback returned false");
                return 2;
            }
            var cliVtbl = *(void***)client;
            var getSvc = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)cliVtbl[14];
            var iidCap = IID_IAudioCaptureClient;
            int ghr = getSvc(client, &iidCap, &capture);
            if (ghr < 0 || capture == null)
            {
                Console.WriteLine($"PROCESS_LOOPBACK_FAIL: GetService hr=0x{ghr:X8}");
                return 3;
            }
            var startFn = (delegate* unmanaged[Stdcall]<void*, int>)cliVtbl[10];
            int shr = startFn(client);
            if (shr < 0)
            {
                Console.WriteLine($"PROCESS_LOOPBACK_FAIL: Start hr=0x{shr:X8}");
                return 4;
            }
            var stopFn = (delegate* unmanaged[Stdcall]<void*, int>)cliVtbl[11];
            stopFn(client);
            Console.WriteLine($"PROCESS_LOOPBACK_OK pid={Environment.ProcessId} ch={ch} sr={sr} bits={bits} float={isFloat}");
            return 0;
        }
        finally
        {
            ReleaseCom(capture);
            ReleaseCom(client);
            if (hrInit >= 0) W.CoUninitialize();
        }
    }

    private static void ReleaseCom(void* p)
    {
        if (p == null) return;
        var vtbl = *(void***)p;
        if (vtbl == null) return;
        ((delegate* unmanaged[Stdcall]<void*, uint>)vtbl[2])(p);
    }

    private static void CaptureLoop()
    {
        int hrInit = W.CoInitializeEx(0, 0 /*COINIT_MULTITHREADED*/);
        var ring = new float[FftSize];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var hann = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            hann[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
        int ringPos = 0;
        var bandScratch = new float[BandCount];
        var waveScratch = new float[WavePoints];

        try
        {
            while (!_disposed)
            {
                if (!ShouldCaptureNow())
                {
                    _wake.Reset();
                    _wake.Wait(1500);
                    continue;
                }

                void* enumerator = null, device = null, client = null, capture = null, fmtPtr = null;
                bool started = false;
                try
                {
                    ushort channels, bits;
                    uint sampleRate;
                    bool isFloat;

                    // 1. Prefer Windows 10/11 per-process loopback (captures ONLY FluxTube audio, zero Discord/game bleed!)
                    if (TryOpenProcessLoopback(out client, out channels, out sampleRate, out bits, out isFloat))
                    {
                        if (!_loggedMode) { _loggedMode = true; Paths.Log($"audio analyzer: per-process WASAPI loopback active (pid={Environment.ProcessId})"); }
                    }
                    else
                    {
                        // 2. Fallback on older Windows: default render loopback
                        var clsid = CLSID_MMDeviceEnumerator;
                        var iidEnum = IID_IMMDeviceEnumerator;
                        if (W.CoCreateInstance(&clsid, 0, 23 /*CLSCTX_ALL*/, &iidEnum, &enumerator) < 0 || enumerator == null)
                        {
                            Thread.Sleep(1500);
                            continue;
                        }
                        var enumVtbl = *(void***)enumerator;
                        var getDefault = (delegate* unmanaged[Stdcall]<void*, int, int, void**, int>)enumVtbl[4];
                        if (getDefault(enumerator, 0 /*eRender*/, 0 /*eConsole*/, &device) < 0 || device == null)
                        {
                            Thread.Sleep(1500);
                            continue;
                        }
                        var devVtbl = *(void***)device;
                        var activate = (delegate* unmanaged[Stdcall]<void*, Guid*, uint, void*, void**, int>)devVtbl[3];
                        var iidClient = IID_IAudioClient;
                        if (activate(device, &iidClient, 23, null, &client) < 0 || client == null)
                        {
                            Thread.Sleep(1500);
                            continue;
                        }
                        var cliVtbl0 = *(void***)client;
                        var getMix = (delegate* unmanaged[Stdcall]<void*, void**, int>)cliVtbl0[8];
                        if (getMix(client, &fmtPtr) < 0 || fmtPtr == null)
                        {
                            Thread.Sleep(1500);
                            continue;
                        }
                        ushort fmtTag = *(ushort*)fmtPtr;
                        channels = *(ushort*)((byte*)fmtPtr + 2);
                        sampleRate = *(uint*)((byte*)fmtPtr + 4);
                        bits = *(ushort*)((byte*)fmtPtr + 14);
                        ushort cbSize = *(ushort*)((byte*)fmtPtr + 16);
                        isFloat = fmtTag == 3 || (fmtTag == 0xFFFE && cbSize >= 22 && *(uint*)((byte*)fmtPtr + 24) == 3);
                        if (channels == 0 || sampleRate < 8000) { Thread.Sleep(1500); continue; }
                        var initFn = (delegate* unmanaged[Stdcall]<void*, int, uint, long, long, void*, Guid*, int>)cliVtbl0[3];
                        if (initFn(client, 0, 0x00020000u, 500_000 /*50ms*/, 0, fmtPtr, null) < 0)
                        {
                            Thread.Sleep(1500);
                            continue;
                        }
                        if (!_loggedMode) { _loggedMode = true; Paths.Log("audio analyzer: fallback system WASAPI loopback"); }
                    }

                    var cliVtbl = *(void***)client;

                    // IAudioClient::GetService (vtable[14]) -> IAudioCaptureClient
                    var getSvc = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)cliVtbl[14];
                    var iidCap = IID_IAudioCaptureClient;
                    if (getSvc(client, &iidCap, &capture) < 0 || capture == null)
                    {
                        Thread.Sleep(1500);
                        continue;
                    }

                    // IAudioClient::Start (vtable[10])
                    var startFn = (delegate* unmanaged[Stdcall]<void*, int>)cliVtbl[10];
                    if (startFn(client) < 0) { Thread.Sleep(1500); continue; }
                    started = true;

                    var capVtbl = *(void***)capture;
                    var getNextPkt = (delegate* unmanaged[Stdcall]<void*, uint*, int>)capVtbl[5];
                    var getBuf = (delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, ulong*, ulong*, int>)capVtbl[3];
                    var relBuf = (delegate* unmanaged[Stdcall]<void*, uint, int>)capVtbl[4];

                    while (!_disposed && ShouldCaptureNow())
                    {
                        bool gotSamples = false;
                        while (true)
                        {
                            uint pkt = 0;
                            if (getNextPkt(capture, &pkt) < 0) goto Reopen;
                            if (pkt == 0) break;
                            byte* data = null;
                            uint frames = 0, flags = 0;
                            ulong devPos = 0, qpc = 0;
                            int hr = getBuf(capture, &data, &frames, &flags, &devPos, &qpc);
                            if (hr < 0) goto Reopen;
                            try
                            {
                                bool silent = (flags & 0x2) != 0 || data == null;
                                if (silent)
                                {
                                    for (uint f = 0; f < frames; f++) { ring[ringPos] = 0f; ringPos = (ringPos + 1) & (FftSize - 1); }
                                }
                                else if (isFloat && bits == 32)
                                {
                                    var fp = (float*)data;
                                    float invCh = 1f / channels;
                                    for (uint f = 0; f < frames; f++)
                                    {
                                        float sum = 0f;
                                        int baseIdx = (int)(f * channels);
                                        for (int c = 0; c < channels; c++) sum += fp[baseIdx + c];
                                        ring[ringPos] = sum * invCh;
                                        ringPos = (ringPos + 1) & (FftSize - 1);
                                    }
                                    gotSamples = true;
                                }
                                else if (bits == 16)
                                {
                                    var sp = (short*)data;
                                    float scale = 1f / (32768f * channels);
                                    for (uint f = 0; f < frames; f++)
                                    {
                                        float sum = 0f;
                                        int baseIdx = (int)(f * channels);
                                        for (int c = 0; c < channels; c++) sum += sp[baseIdx + c];
                                        ring[ringPos] = sum * scale;
                                        ringPos = (ringPos + 1) & (FftSize - 1);
                                    }
                                    gotSamples = true;
                                }
                            }
                            finally
                            {
                                relBuf(capture, frames);
                            }
                        }

                        if (gotSamples)
                        {
                            ComputeSpectrum(ring, ringPos, hann, re, im, sampleRate, bandScratch, waveScratch);
                            lock (_dataLock)
                            {
                                Array.Copy(bandScratch, _targetBands, BandCount);
                                Array.Copy(waveScratch, _targetWave, WavePoints);
                            }
                        }
                        Thread.Sleep(14);
                    }

                Reopen:;
                }
                catch { Thread.Sleep(1000); }
                finally
                {
                    if (started && client != null)
                    {
                        try { var cliVtbl = *(void***)client; ((delegate* unmanaged[Stdcall]<void*, int>)cliVtbl[11])(client); } catch { }
                    }
                    if (fmtPtr != null) W.CoTaskMemFree(fmtPtr);
                    ReleaseCom(capture);
                    ReleaseCom(client);
                    ReleaseCom(device);
                    ReleaseCom(enumerator);
                }
            }
        }
        finally
        {
            if (hrInit >= 0) W.CoUninitialize();
        }
    }

    private static bool ShouldCaptureNow()
    {
        if (_disposed) return false;
        var now = Environment.TickCount64;
        bool forced = now - Volatile.Read(ref _lastForcedAt) < 1200;
        if (!forced && AppSettings.Current.VisualizerStyle == "off") return false;
        if (!IsPlayingAudio()) return false;
        return now - Volatile.Read(ref _lastNeededAt) < 1200;
    }

    private static void ComputeSpectrum(float[] ring, int ringPos, float[] hann, float[] re, float[] im, uint sampleRate, float[] outBands, float[] outWave)
    {
        // Copy windowed time-domain samples & downsample 64 wave points
        int step = FftSize / WavePoints;
        for (int i = 0; i < WavePoints; i++)
            outWave[i] = Math.Clamp(ring[(ringPos + i * step) & (FftSize - 1)] * 2.2f, -1f, 1f);

        for (int i = 0; i < FftSize; i++)
        {
            re[i] = ring[(ringPos + i) & (FftSize - 1)] * hann[i];
            im[i] = 0f;
        }

        // In-place radix-2 Cooley-Tukey FFT (512 samples -> ~0.04ms)
        int j = 0;
        for (int i = 1; i < FftSize; i++)
        {
            int bit = FftSize >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (re[i], re[j]) = (re[j], re[i]);
        }
        for (int len = 2; len <= FftSize; len <<= 1)
        {
            float ang = -2f * MathF.PI / len;
            float wLenRe = MathF.Cos(ang), wLenIm = MathF.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < FftSize; i += len)
            {
                float wRe = 1f, wIm = 0f;
                for (int k = 0; k < half; k++)
                {
                    int u = i + k, v = u + half;
                    float tRe = re[v] * wRe - im[v] * wIm;
                    float tIm = re[v] * wIm + im[v] * wRe;
                    re[v] = re[u] - tRe; im[v] = im[u] - tIm;
                    re[u] += tRe; im[u] += tIm;
                    float nextRe = wRe * wLenRe - wIm * wLenIm;
                    wIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextRe;
                }
            }
        }

        // Map FFT bins logarithmically (40 Hz .. 15 kHz) to 32 perceptual bands with gentle treble tilt
        float binHz = sampleRate / (float)FftSize;
        int maxBin = FftSize / 2 - 1;
        const float minHz = 36f, maxHz = 14800f;
        float logSpan = MathF.Log(maxHz / minHz);

        for (int b = 0; b < BandCount; b++)
        {
            float f0 = minHz * MathF.Exp(logSpan * b / BandCount);
            float f1 = minHz * MathF.Exp(logSpan * (b + 1) / BandCount);
            int i0 = Math.Clamp((int)MathF.Floor(f0 / binHz), 1, maxBin);
            int i1 = Math.Clamp((int)MathF.Ceiling(f1 / binHz), i0, maxBin);
            float sum = 0f;
            for (int k = i0; k <= i1; k++)
                sum += MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
            float avg = sum / Math.Max(1, i1 - i0 + 1);
            // Gentle perceptual A-tilt so bass & sparkling highs look balanced and lively
            float tilt = 0.72f + 1.55f * (b / (float)(BandCount - 1));
            float norm = MathF.Log10(1f + avg * tilt * 0.42f) * 1.15f;
            outBands[b] = Math.Clamp(norm, 0f, 1f);
        }
    }

    // ---------------- GPU Direct2D Visualizer Renderers (ultra-cheap!) ----------------

    /// <summary>Draws lush glowing multi-layered audio waves and/or studio spectrum bars inside r.</summary>
    public static void DrawStageVisualizer(R r, string style, float opacity = 1f)
    {
        if (style == "off" || r.W < 40 || r.H < 24 || opacity <= 0.01f) return;
        RequestFrame();

        uint c1 = Th.Brand;
        // Complementary prismatic crystal highlight
        uint c2 = Gfx.Mix(Th.Brand, Th.Light ? 0xFF4361EE : 0xFF72EFDD, 0.55f);
        uint c3 = Gfx.Mix(Th.Brand, 0xFFFFFFFF, 0.45f);

        bool drawBars = style is "bars_wave" or "bars";
        bool drawWaves = style is "bars_wave" or "ribbon";

        if (drawBars)
        {
            float padX = MathF.Min(28, r.W * 0.05f);
            float usableW = r.W - padX * 2;
            float gap = MathF.Max(2.5f, usableW / BandCount * 0.24f);
            float barW = MathF.Max(3f, (usableW - gap * (BandCount - 1)) / BandCount);
            float baseY = r.Bottom - 6f;
            float maxH = r.H * (drawWaves ? 0.54f : 0.76f);

            for (int i = 0; i < BandCount; i++)
            {
                float t = i / (float)(BandCount - 1);
                float v = _smoothBands[i];
                float bh = MathF.Max(3f, v * maxH);
                float bx = r.X + padX + i * (barW + gap);
                uint col = Gfx.Mix(c1, c2, t);
                // Soft vertical glow behind energetic bars
                if (v > 0.14f)
                    Gfx.Fill(new R(bx - 1.5f, baseY - bh - 3f, barW + 3f, bh + 3f), Gfx.Fade(col, 0.16f * v * opacity), barW * 0.6f);
                Gfx.Fill(new R(bx, baseY - bh, barW, bh), Gfx.Fade(col, (0.35f + 0.58f * v) * opacity), MathF.Min(barW * 0.5f, 4f));
                // Floating studio peak cap
                float pk = _peakBands[i];
                if (pk > 0.04f)
                {
                    float py = baseY - pk * maxH - 3f;
                    Gfx.Fill(new R(bx, py, barW, 2.2f), Gfx.Fade(c3, 0.85f * opacity), 1.1f);
                }
            }
        }

        if (drawWaves)
        {
            // Two layered silky oscilloscope waves (glowing wide ribbon + crisp specular core)
            float midY = drawBars ? r.Y + r.H * 0.36f : r.Y + r.H * 0.50f;
            float amp = r.H * (drawBars ? 0.24f : 0.34f);
            float time = (UI.Now - UI.StartTick) / 1000f;
            float idleAmp = 2.5f + _bassEnergy * 10f;
            float stepX = r.W / (WavePoints - 1);

            for (int layer = 0; layer < 2; layer++)
            {
                float prevX = r.X, prevY = midY;
                uint col = layer == 0 ? Gfx.Fade(c1, (0.30f + 0.26f * _bassEnergy) * opacity) : Gfx.Fade(c3, (0.65f + 0.30f * _midEnergy) * opacity);
                float lw = layer == 0 ? 5.0f + _bassEnergy * 4.0f : 1.9f;
                float phaseShift = layer == 0 ? 0.0f : 0.85f;
                for (int i = 0; i < WavePoints; i++)
                {
                    float u = i / (float)(WavePoints - 1);
                    float env = MathF.Sin(u * MathF.PI); // taper neatly to 0 at edges
                    float wVal = _smoothWave[i] * (layer == 0 ? 1.12f : 0.86f);
                    float silk = MathF.Sin(u * 9.5f + time * (2.6f + _bassEnergy * 2.5f) + phaseShift) * idleAmp;
                    float x = r.X + i * stepX;
                    float y = midY + (wVal * amp + silk) * env;
                    if (i > 0) Gfx.Line(prevX, prevY, x, y, col, lw);
                    prevX = x; prevY = y;
                }
            }
        }
    }

    /// <summary>Compact 8-bar live audio wave indicator for the NowPlaying mini-bar (0% CPU when paused).</summary>
    public static void DrawMiniWaves(R r)
    {
        if (AppSettings.Current.VisualizerStyle == "off") return;
        RequestFrame();
        const int bars = 7;
        float gap = 2f;
        float bw = MathF.Max(2f, (r.W - gap * (bars - 1)) / bars);
        for (int i = 0; i < bars; i++)
        {
            float v = _smoothBands[Math.Clamp(i * 4 + 1, 0, BandCount - 1)];
            float h = MathF.Max(2.5f, v * r.H);
            float y = r.Y + (r.H - h) * 0.5f;
            Gfx.Fill(new R(r.X + i * (bw + gap), y, bw, h), Gfx.Fade(Th.Brand, 0.45f + 0.55f * v), bw * 0.5f);
        }
    }
}
