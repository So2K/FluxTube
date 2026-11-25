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
    private static float _capturedTrackRms, _capturedRawRms, _capturedStereoPan;
    private static float _loudness, _slowTrackRms = 0.08f, _trackDynamics = 1f, _loudnessGain = 1f;
    private static float _stereoPan, _spectralCentroid = 0.35f, _dominantAngle, _subBass, _punchBass, _airTreble;
    private static long _lastStepFrame = -1;

    private static float _beatEnvelope, _beatPhase, _capturedBeatStrength;
    private static long _capturedBeatAt, _capturedBassAt;
    private static float _capturedBassPressure, _bassPressure;
    public static float BassPressure => _bassPressure;
    private static long _lastBeatAt;
    public static long BeatStamp => _lastBeatAt;
    public static float BeatEnvelope => _beatEnvelope;
    public static float BeatAttackStrength => _capturedBeatStrength;
    // Amplify the visible kick, not detector sensitivity or effect frequency.
    public static float BeatMotionStrength => _beatEnvelope > 0 ? MathF.Pow(_beatEnvelope, 0.65f) : 0;
    public static float BeatPhase => _beatPhase;

    public static float BassEnergy => _bassEnergy;
    public static float MidEnergy => _midEnergy;
    public static float HighEnergy => _highEnergy;
    public static float SubBass => _subBass;
    public static float PunchBass => _punchBass;
    public static float AirTreble => _airTreble;
    /// <summary>Real-time stereo L/R balance from WASAPI channels (-1 = full Left, 0 = Center, +1 = full Right).</summary>
    public static float StereoPan => _stereoPan;
    /// <summary>Energy-weighted spectral centroid (0 = dark sub-heavy, 1 = bright treble-heavy).</summary>
    public static float SpectralCentroid => _spectralCentroid;
    /// <summary>Live directional vector angle (radians) driven by the dominant transient frequency bin each instant.</summary>
    public static float DominantAngle => _dominantAngle;
    /// <summary>Perceptual track loudness (0..1, -44..-5 dBFS) reflecting real quiet vs loud sections.</summary>
    public static float Loudness => _loudness;
    /// <summary>Short-term loudness relative to the track's 5s baseline (0.35 quiet intro .. 1.55 big drop).</summary>
    public static float TrackDynamics => _trackDynamics;
    /// <summary>Combined shake multiplier driven by track loudness, drop dynamics, and player volume (0..1.65).</summary>
    public static float LoudnessGain => _loudnessGain;
    public static ReadOnlySpan<float> Bands => _smoothBands;
    public static ReadOnlySpan<float> Peaks => _peakBands;
    public static ReadOnlySpan<float> Wave => _smoothWave;

    private static bool IsPlayingAudio()
    {
        var p = MpvPlayer.Instance;
        return (p.HasMedia && !p.Paused && !p.Muted) || BgPlayers.ActiveCount > 0;
    }

    /// <summary>Called only by visible UI panels that want live spectrum/wave data this frame.</summary>
    private static readonly bool SpectrumPreview = Environment.GetCommandLineArgs().Contains("--test-spectrum");
    public static void RequestFrame(bool force = false, bool visual = true)
    {
        if (_disposed || (!force && AppSettings.Current.VisualizerStyle == "off")) return;
        if (App.Test)
        {
            if (SpectrumPreview)
            {
                // Deterministic silent visual QA; never used during normal playback.
                float time = (UI.Now - UI.StartTick) / 1000f;
                lock (_dataLock)
                {
                    for (int i = 0; i < BandCount; i++)
                        _targetBands[i] = (0.25f + 0.5f * MathF.Pow(MathF.Sin(i * 0.22f + time * 2), 2)) * (1 - i / 45f);
                    for (int i = 0; i < WavePoints; i++) _targetWave[i] = MathF.Sin(i * 0.35f + time * 3) * 0.4f;
                }
                StepSmoothing(true, visual: true);
            }
            return;
        }
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
        StepSmoothing(playing, visual);
    }

    /// <summary>Keeps WASAPI beat capture alive without waking D2D rendering on silent/speech frames.</summary>
    public static void KeepAlive()
    {
        if (_disposed || App.Test || W.IsIconic(App.Hwnd) || Gfx.Occluded || !IsPlayingAudio()) return;
        var now = Environment.TickCount64;
        Volatile.Write(ref _lastNeededAt, now);
        Volatile.Write(ref _lastForcedAt, now);
        EnsureThread();
        _wake.Set();
    }

    private static void EnsureThread()
    {
        if (_started || _disposed) return;
        lock (_dataLock)
        {
            if (_started || _disposed) return;
            _started = true;
            _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "flux-wasapi-fft", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    private static void StepSmoothing(bool playing, bool visual = true)
    {
        if (_lastStepFrame == Gfx.Frame) return;
        _lastStepFrame = Gfx.Frame;

        float dt = (float)Math.Clamp(UI.Dt, 0.004, 0.05);
        float rise = 1f - MathF.Exp(-dt * 28f);
        float fall = 1f - MathF.Exp(-dt * 11f);
        float peakFall = dt * 0.42f;
        float waveLerp = 1f - MathF.Exp(-dt * 24f);
        bool anyMotion = false;

        lock (_dataLock)
        {
            // The capture thread already detected the low-frequency attack, before FFT or UI smoothing.
            long ageMs = Math.Max(0, UI.Now - _capturedBeatAt);
            if (playing && _capturedBeatAt > _lastBeatAt && ageMs < 120)
                _lastBeatAt = _capturedBeatAt;
            float age = Math.Max(0, UI.Now - _lastBeatAt) / 1000f;
            _beatEnvelope = playing && age < 0.20f ? _capturedBeatStrength * MathF.Exp(-age * 21) : 0;
            _beatPhase = MathF.PI * 0.5f + age * 22;
            if (_beatEnvelope < 0.015f) _beatEnvelope = 0;
            if (!playing) { _beatEnvelope = 0; _lastBeatAt = _capturedBeatAt; }
            float pressureTarget = playing && UI.Now - _capturedBassAt < 150 ? _capturedBassPressure : 0;
            _bassPressure += (pressureTarget - _bassPressure) * (1 - MathF.Exp(-dt * (pressureTarget > _bassPressure ? 35 : 10)));
            if (!playing) _bassPressure = 0;
            if (_beatEnvelope > 0.002f) anyMotion = true;
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

                if (visual && (next > 0.003f || _peakBands[i] > 0.003f)) anyMotion = true;
                if (i < 6) bass += next;
                else if (i < 18) mid += next;
                else high += next;
            }
            _bassEnergy = Math.Clamp(bass / 6f, 0f, 1f);
            _midEnergy = Math.Clamp(mid / 12f, 0f, 1f);
            _highEnergy = Math.Clamp(high / (BandCount - 18f), 0f, 1f);

            // Fine sub-band breakdown + real-time spectral centroid & dominant-frequency directional vector!
            _subBass = Math.Clamp((_smoothBands[0] + _smoothBands[1] + _smoothBands[2]) / 3f, 0f, 1f);
            _punchBass = Math.Clamp((_smoothBands[3] + _smoothBands[4] + _smoothBands[5] + _smoothBands[6]) / 4f, 0f, 1f);
            float airSum = 0f;
            for (int i = 20; i < BandCount; i++) airSum += _smoothBands[i];
            _airTreble = Math.Clamp(airSum / (BandCount - 20f), 0f, 1f);

            float wSum = 0f, idxSum = 0f, bestBinVal = 0.04f;
            int domBin = 0;
            for (int i = 0; i < BandCount; i++)
            {
                float bVal = _smoothBands[i];
                wSum += bVal;
                idxSum += bVal * i;
                // Weight transient contrast so a sudden 4kHz synth or 120Hz kick steers the directional angle immediately!
                float weighted = bVal * (0.85f + 0.35f * (i / (float)(BandCount - 1)));
                if (weighted > bestBinVal) { bestBinVal = weighted; domBin = i; }
            }
            float centTarget = wSum > 0.02f ? Math.Clamp(idxSum / (wSum * (BandCount - 1)), 0f, 1f) : 0.35f;
            _spectralCentroid += (centTarget - _spectralCentroid) * (1f - MathF.Exp(-dt * 12f));

            // Map dominant frequency bin (0..31) + stereo pan to a distinct kinetic direction angle!
            float panTarget = playing ? Math.Clamp(_capturedStereoPan, -1f, 1f) : 0f;
            _stereoPan += (panTarget - _stereoPan) * (1f - MathF.Exp(-dt * 20f));
            float targetAng = (domBin * 0.6180339f * MathF.Tau) + _stereoPan * 1.15f;
            // Shortest angular interpolation so direction snaps organically without sudden flips
            float angDiff = MathF.Atan2(MathF.Sin(targetAng - _dominantAngle), MathF.Cos(targetAng - _dominantAngle));
            _dominantAngle += angDiff * (1f - MathF.Exp(-dt * 11f));

            // Real track loudness (dBFS) + 5-second dynamic drop tracker + player volume curve
            float trackRms = playing ? _capturedTrackRms : 0f;
            float db = trackRms > 0.0002f ? 20f * MathF.Log10(trackRms) : -60f;
            float loudTarget = playing ? Math.Clamp((db + 42f) / 36f, 0f, 1f) : 0f; // -42 dBFS -> 0, -6 dBFS -> 1
            float loudAlpha = 1f - MathF.Exp(-dt * (loudTarget > _loudness ? 18f : 7.5f));
            _loudness += (loudTarget - _loudness) * loudAlpha;

            if (playing && trackRms > 0.003f)
                _slowTrackRms += (trackRms - _slowTrackRms) * (1f - MathF.Exp(-dt * 0.28f));
            else if (!playing)
                _slowTrackRms = 0.08f;

            float dynTarget = playing ? Math.Clamp(trackRms / MathF.Max(0.025f, _slowTrackRms), 0.35f, 1.55f) : 0f;
            _trackDynamics += (dynTarget - _trackDynamics) * (1f - MathF.Exp(-dt * 9.5f));

            // Player volume gently scales physical shake feel (0% = 0, 25% = 0.56, 70% = 0.96, 100% = 1.18, 130% = 1.36)
            var mpv = MpvPlayer.Instance;
            float volPct = mpv.Muted ? 0f : Math.Clamp((float)mpv.Volume, 0f, 150f);
            float volCurve = volPct <= 0.5f ? 0f : Math.Clamp(0.24f + MathF.Pow(volPct / 100f, 0.72f) * 0.94f, 0f, 1.42f);
            float combined = playing
                ? Math.Clamp((0.20f + 1.10f * MathF.Pow(_loudness, 1.25f)) * (0.58f + 0.52f * _trackDynamics) * volCurve, 0f, 1.68f)
                : 0f;
            _loudnessGain += (combined - _loudnessGain) * (1f - MathF.Exp(-dt * 14f));

            for (int i = 0; i < WavePoints; i++)
            {
                float target = playing ? _targetWave[i] : 0f;
                float next = _smoothWave[i] + (target - _smoothWave[i]) * waveLerp;
                if (MathF.Abs(next) < 0.001f) next = 0f;
                _smoothWave[i] = next;
                if (visual && MathF.Abs(next) > 0.002f) anyMotion = true;
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

                    var bassDetector = new BassOnsetDetector((int)sampleRate);
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
                                long packetStart = Environment.TickCount64 - (long)(frames * 1000.0 / sampleRate);
                                bool beatHit = false;
                                float bestStrength = 0f;
                                long bestBeatAt = 0;
                                void DetectBass(float sample, uint frame)
                                {
                                    float strength = bassDetector.Process(sample);
                                    if (strength <= 0) return;
                                    if (strength >= bestStrength)
                                    {
                                        bestStrength = strength;
                                        bestBeatAt = packetStart + (long)(frame * 1000.0 / sampleRate);
                                        beatHit = true;
                                    }
                                }
                                bool silent = (flags & 0x2) != 0 || data == null;
                                // Compensate player volume for spectrum visibility while also tracking true raw, track RMS & stereo L/R pan!
                                float volGain = Math.Clamp(100f / MathF.Max(14f, (float)MpvPlayer.Instance.Volume), 0.72f, 4.2f);
                                float sumSqComp = 0f, sumSqRaw = 0f, sumSqL = 0f, sumSqR = 0f;
                                uint rmsCount = 0;
                                if (silent)
                                {
                                    for (uint f = 0; f < frames; f++) { ring[ringPos] = 0f; DetectBass(0, f); ringPos = (ringPos + 1) & (FftSize - 1); }
                                    gotSamples = true;
                                }
                                else if (isFloat && bits == 32)
                                {
                                    var fp = (float*)data;
                                    float invRaw = 1f / channels;
                                    for (uint f = 0; f < frames; f++)
                                    {
                                        float sum = 0f;
                                        int baseIdx = (int)(f * channels);
                                        if (channels >= 2)
                                        {
                                            float sL = fp[baseIdx], sR = fp[baseIdx + 1];
                                            sumSqL += sL * sL;
                                            sumSqR += sR * sR;
                                        }
                                        for (int c = 0; c < channels; c++) sum += fp[baseIdx + c];
                                        float raw = sum * invRaw;
                                        float sample = Math.Clamp(raw * volGain, -1.6f, 1.6f);
                                        sumSqRaw += raw * raw;
                                        sumSqComp += sample * sample;
                                        rmsCount++;
                                        ring[ringPos] = sample;
                                        DetectBass(sample, f);
                                        ringPos = (ringPos + 1) & (FftSize - 1);
                                    }
                                    gotSamples = true;
                                }
                                else if (bits == 16)
                                {
                                    var sp = (short*)data;
                                    float rawScale = 1f / (32768f * channels);
                                    for (uint f = 0; f < frames; f++)
                                    {
                                        float sum = 0f;
                                        int baseIdx = (int)(f * channels);
                                        if (channels >= 2)
                                        {
                                            float sL = sp[baseIdx] / 32768f, sR = sp[baseIdx + 1] / 32768f;
                                            sumSqL += sL * sL;
                                            sumSqR += sR * sR;
                                        }
                                        for (int c = 0; c < channels; c++) sum += sp[baseIdx + c];
                                        float raw = sum * rawScale;
                                        float sample = Math.Clamp(raw * volGain, -1.6f, 1.6f);
                                        sumSqRaw += raw * raw;
                                        sumSqComp += sample * sample;
                                        rmsCount++;
                                        ring[ringPos] = sample;
                                        DetectBass(sample, f);
                                        ringPos = (ringPos + 1) & (FftSize - 1);
                                    }
                                    gotSamples = true;
                                }
                                if (rmsCount > 0)
                                {
                                    float pktTrackRms = MathF.Sqrt(sumSqComp / rmsCount);
                                    float pktRawRms = MathF.Sqrt(sumSqRaw / rmsCount);
                                    float rmsL = MathF.Sqrt(sumSqL / rmsCount);
                                    float rmsR = MathF.Sqrt(sumSqR / rmsCount);
                                    float pktPan = channels >= 2 ? Math.Clamp((rmsR - rmsL) / MathF.Max(0.004f, rmsR + rmsL) * 2.2f, -1f, 1f) : 0f;
                                    lock (_dataLock)
                                    {
                                        _capturedTrackRms = _capturedTrackRms * 0.55f + pktTrackRms * 0.45f;
                                        _capturedRawRms = _capturedRawRms * 0.55f + pktRawRms * 0.45f;
                                        _capturedStereoPan = _capturedStereoPan * 0.50f + pktPan * 0.50f;
                                    }
                                }
                                else if (silent)
                                {
                                    lock (_dataLock) { _capturedTrackRms *= 0.7f; _capturedRawRms *= 0.7f; _capturedStereoPan *= 0.7f; }
                                }
                                if (beatHit)
                                {
                                    lock (_dataLock)
                                    {
                                        _capturedBeatStrength = bestStrength;
                                        _capturedBeatAt = bestBeatAt;
                                    }
                                    App.WakeUi();
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
                                _capturedBeatStrength = MathF.Max(_capturedBeatStrength, bassDetector.AttackStrength);
                                _capturedBassPressure = bassDetector.Pressure;
                                _capturedBassAt = Environment.TickCount64;
                                Array.Copy(bandScratch, _targetBands, BandCount);
                                Array.Copy(waveScratch, _targetWave, WavePoints);
                            }
                        }
                        Thread.Sleep(7);
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

    /// <summary>Frequency halo around the cover; energy changes the rays, never the layout.</summary>
    public static void DrawCoverSpectrum(R art, float opacity = 1f)
    {
        if (AppSettings.Current.VisualizerStyle == "off") return;
        RequestFrame();
        float cx = art.X + art.W / 2, cy = art.Y + art.H / 2;
        float rx = art.W * 0.66f, ry = art.H * 0.75f;
        uint ice = Gfx.Mix(Th.Brand, 0xFF73D8EB, 0.48f);
        const int rays = 48;
        for (int i = 0; i < rays; i++)
        {
            float angle = i * MathF.Tau / rays;
            float band = MathF.Abs(MathF.Sin(angle * 0.5f)) * (BandCount - 1);
            int lo = (int)band, hi = Math.Min(lo + 1, BandCount - 1);
            float energy = _smoothBands[lo] + (_smoothBands[hi] - _smoothBands[lo]) * (band - lo);
            float v = MathF.Pow(energy, 0.72f);
            float dx = MathF.Cos(angle), dy = MathF.Sin(angle);
            float length = 3 + v * MathF.Min(24, art.H * 0.19f);
            float x = cx + dx * rx, y = cy + dy * ry;
            uint color = Gfx.Mix(Th.Brand, ice, MathF.Abs(dy));
            if (v > 0.15f)
                Gfx.Line(x, y, x + dx * length, y + dy * length, Gfx.Fade(color, v * 0.08f * opacity), 6.5f);
            Gfx.Line(x, y, x + dx * length, y + dy * length,
                Gfx.Fade(color, (0.06f + v * 0.40f) * opacity), 2f);
            if (v > 0.35f)
                Gfx.Circle(x + dx * length, y + dy * length, 1.4f, Gfx.Fade(Th.Text, v * 0.8f * opacity));
        }
    }

    /// <summary>Mirrored spectrum with floating peaks, a fading reflection and an audio-driven ribbon (4.5x fewer GPU draw calls).</summary>
    public static void DrawStageVisualizer(R r, string style, float opacity = 1f, float gain = 1f)
    {
        if (style == "off" || r.W < 40 || r.H < 24 || opacity <= 0.01f) return;
        RequestFrame();
        Gfx.PushClip(r);
        uint ice = Gfx.Mix(Th.Brand, 0xFF73D8EB, 0.48f);
        uint highlight = Gfx.Mix(Th.Brand, Th.Text, 0.65f);
        bool bars = style is "bars_wave" or "bars";
        bool waves = style == "ribbon";
        float baseline = r.Y + r.H * 0.72f;
        float maxH = r.H * 0.62f;
        if (bars)
        {
            // Smooth frequency terrain: bass in the center, treble fading toward the edges (~270 draw calls vs 1,285!)
            const int count = 90;
            float colW = MathF.Max(1.8f, r.W / count * 0.66f);
            float px = r.X, py = baseline;
            for (int i = 0; i <= count; i++)
            {
                float u = i / (float)count;
                float f = MathF.Abs(u * 2 - 1) * (BandCount - 1);
                int lo = (int)f, hi = Math.Min(lo + 1, BandCount - 1);
                float t = f - lo; t = t * t * (3 - 2 * t);
                float Smooth(int index) => (_smoothBands[Math.Max(0, index - 1)] + 2 * _smoothBands[index] + _smoothBands[Math.Min(BandCount - 1, index + 1)]) * 0.25f;
                float sample = Smooth(lo) + (Smooth(hi) - Smooth(lo)) * t;
                float v = Math.Clamp(MathF.Pow(sample, 0.8f) * gain, 0, 1);
                float edge = MathF.Pow(MathF.Sin(u * MathF.PI), 0.45f);
                float height = v * maxH * edge;
                float x = r.X + u * r.W, y = baseline - height;
                uint color = Gfx.Mix(Th.Brand, ice, MathF.Abs(u * 2 - 1));
                if (height > 1.2f)
                    Gfx.Fill(new R(x - colW * 0.5f, y, colW, height), Gfx.Fade(color, (0.04f + 0.14f * v) * opacity));
                if (i > 0)
                {
                    Gfx.Line(px, py, x, y, Gfx.Fade(color, 0.10f * v * opacity), 6.5f);
                    Gfx.Line(px, py, x, y, Gfx.Fade(highlight, (0.16f + v * 0.68f) * opacity), 1.6f);
                }
                px = x; py = y;
            }
        }
        if (waves)
        {
            float mid = bars ? baseline : r.Y + r.H * 0.5f;
            float amp = r.H * (bars ? 0.17f : 0.4f);
            for (int layer = 0; layer < 2; layer++)
            {
                float px = r.X, py = mid;
                for (int i = 0; i < WavePoints; i++)
                {
                    float u = i / (float)(WavePoints - 1);
                    float envelope = MathF.Sin(u * MathF.PI);
                    float x = r.X + u * r.W;
                    float y = mid + _smoothWave[i] * amp * envelope;
                    uint color = Gfx.Mix(Th.Brand, ice, u);
                    float alpha = layer == 0 ? 0.16f : 0.75f;
                    if (i > 0) Gfx.Line(px, py, x, y, Gfx.Fade(color, alpha * opacity), layer == 0 ? 7f : 1.5f);
                    px = x; py = y;
                }
            }
        }
        Gfx.PopClip();
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
