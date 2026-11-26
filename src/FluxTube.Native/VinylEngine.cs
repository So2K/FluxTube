using System.Runtime.InteropServices;

namespace FluxTube;

/// <summary>
/// Zero-latency Reaper / Serato / Mixxx-grade Vinyl Scratch &amp; Reverse Backspin Engine:
/// - Records the last 10 seconds of FluxTube's live 48kHz stereo WASAPI loopback into a lock-free float32 RAM ring buffer
///   (allocated lazily ONLY while the DJ Deck is open -> 0 bytes RAM &amp; 0% CPU otherwise).
/// - On platter grab or Backspin, mutes mpv (zero network seek stutters!) and drives a triple-buffered 10.6ms WinMM
///   48kHz stereo IEEE-float output with 4-point Catmull-Rom cubic Hermite interpolation in BOTH forward (+4.5x) and
///   TRUE REVERSE (-4.5x) directions with sample-accurate inertia smoothing (zero zipper noise, zero clicks!).
/// </summary>
public static unsafe class VinylEngine
{
    private const int SampleRate = 48000;
    private const int RingFrames = SampleRate * 10; // 10 seconds of stereo float32 (~3.84 MB)
    private const int BufFrames = 512;              // 10.67 ms per buffer -> < 12ms tactile vinyl latency!
    private const int NumBufs = 3;

    private static float[]? _ringL, _ringR;
    private static int _writePos;
    private static int _validFrames;

    private static volatile bool _wantCapture;
    private static volatile bool _active;
    private static volatile bool _stopping;
    private static float _targetVel;
    private static double _startReadPos, _curReadPos;
    private static int _snapWritePos, _snapValid;
    private static float _smoothVel;
    private static float _envGain;
    private static Thread? _renderThread;
    private static readonly object _ctrlLock = new();

    public static bool WantCapture
    {
        get => _wantCapture;
        set
        {
            _wantCapture = value;
            if (value && _ringL == null)
            {
                lock (_ctrlLock)
                {
                    _ringL ??= new float[RingFrames];
                    _ringR ??= new float[RingFrames];
                }
            }
        }
    }

    public static bool Active => _active;

    /// <summary>Called on the 7ms WASAPI capture thread while DJ Deck is open &amp; not actively scratching (zero feedback!).</summary>
    public static void FeedStereo(float l, float r)
    {
        if (!_wantCapture || _active) return;
        var rl = _ringL;
        var rr = _ringR;
        if (rl == null || rr == null) return;
        int w = _writePos;
        rl[w] = l;
        rr[w] = r;
        w++;
        if (w >= RingFrames) w = 0;
        _writePos = w;
        if (_validFrames < RingFrames) _validFrames++;
    }

    /// <summary>Begins zero-latency RAM-buffer vinyl scratching right at the live playback head.</summary>
    public static bool BeginScratch(float initialVel = 0f)
    {
        lock (_ctrlLock)
        {
            if (_ringL == null || _ringR == null || _validFrames < SampleRate / 4) return false;
            if (_active)
            {
                Volatile.Write(ref _targetVel, Math.Clamp(initialVel, -4.5f, 4.5f));
                return true;
            }
            _snapWritePos = _writePos;
            _snapValid = Math.Min(_validFrames, RingFrames - 16);
            // Start 48 samples behind the live write head so Catmull-Rom 4-point stencil stays strictly in-bounds
            _startReadPos = _snapValid - 48;
            _curReadPos = _startReadPos;
            _smoothVel = Math.Clamp(initialVel, -4.5f, 4.5f);
            Volatile.Write(ref _targetVel, _smoothVel);
            _envGain = 0f;
            _stopping = false;
            _active = true;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "flux-vinyl", Priority = ThreadPriority.Highest };
            _renderThread.Start();
            return true;
        }
    }

    /// <summary>Updates target platter velocity (-4.5x reverse .. 0.0 hold .. +4.5x forward) smoothly at audio rate.</summary>
    public static void SetVelocity(float vel)
    {
        Volatile.Write(ref _targetVel, Math.Clamp(vel, -4.5f, 4.5f));
    }

    /// <summary>Stops vinyl scratch cleanly and returns the net time offset (seconds, negative = rewound) to sync mpv ONCE.</summary>
    public static double EndScratch()
    {
        double deltaSec = 0;
        Thread? t;
        lock (_ctrlLock)
        {
            if (!_active) return 0;
            deltaSec = (_curReadPos - _startReadPos) / SampleRate;
            _stopping = true;
            t = _renderThread;
        }
        try { t?.Join(180); } catch { }
        lock (_ctrlLock)
        {
            _active = false;
            _stopping = false;
            _renderThread = null;
        }
        return deltaSec;
    }

    public static void Shutdown()
    {
        _wantCapture = false;
        EndScratch();
        lock (_ctrlLock)
        {
            _ringL = null;
            _ringR = null;
            _validFrames = 0;
            _writePos = 0;
        }
    }

    /// <summary>4-point Catmull-Rom cubic Hermite interpolation — exact Reaper / Mixxx scratch resampling kernel.</summary>
    private static float CatmullRom(float y0, float y1, float y2, float y3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * ((2f * y1)
            + (-y0 + y2) * t
            + (2f * y0 - 5f * y1 + 4f * y2 - y3) * t2
            + (-y0 + 3f * y1 - 3f * y2 + y3) * t3);
    }

    /// <summary>Renders one stereo float32 block from the frozen ring buffer at live velocity (used by waveOut &amp; self-test).</summary>
    internal static void RenderScratchBlock(float* outInterleaved, int frames, float targetVel, bool fadingOut)
    {
        var rl = _ringL;
        var rr = _ringR;
        if (rl == null || rr == null || _snapValid < 16)
        {
            for (int i = 0; i < frames * 2; i++) outInterleaved[i] = 0f;
            return;
        }

        int baseStart = (_snapWritePos - _snapValid + RingFrames) % RingFrames;
        double minPos = 4.0;
        double maxPos = _snapValid - 4.0;

        for (int i = 0; i < frames; i++)
        {
            // Smooth angular velocity sample-by-sample (~8ms analog platter inertia -> zero zipper artifacts!)
            _smoothVel += (targetVel - _smoothVel) * 0.0026f;

            // Smooth 5ms fade-in/out envelope + silent hold when platter is stopped (|vel| < 0.025)
            float wantGain = fadingOut ? 0f : Math.Clamp((MathF.Abs(_smoothVel) - 0.015f) * 14f, 0f, 1f);
            _envGain += (wantGain - _envGain) * 0.0065f;

            _curReadPos += _smoothVel;
            float edgeFade = 1f;
            if (_curReadPos <= minPos)
            {
                _curReadPos = minPos;
                edgeFade = 0f;
            }
            else if (_curReadPos >= maxPos)
            {
                _curReadPos = maxPos;
                edgeFade = 0f;
            }
            else if (_curReadPos - minPos < 256.0)
                edgeFade = (float)((_curReadPos - minPos) / 256.0);
            else if (maxPos - _curReadPos < 256.0)
                edgeFade = (float)((maxPos - _curReadPos) / 256.0);

            int idx = (int)_curReadPos;
            float frac = (float)(_curReadPos - idx);

            int i0 = (baseStart + idx - 1) % RingFrames;
            int i1 = (baseStart + idx) % RingFrames;
            int i2 = (baseStart + idx + 1) % RingFrames;
            int i3 = (baseStart + idx + 2) % RingFrames;

            float sL = CatmullRom(rl[i0], rl[i1], rl[i2], rl[i3], frac);
            float sR = CatmullRom(rr[i0], rr[i1], rr[i2], rr[i3], frac);

            float g = _envGain * edgeFade;
            outInterleaved[i * 2]     = Math.Clamp(sL * g, -0.98f, 0.98f);
            outInterleaved[i * 2 + 1] = Math.Clamp(sR * g, -0.98f, 0.98f);
        }
    }

    // ---------------- Native WinMM waveOut 48kHz Stereo Float32 Output ----------------

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public nint lpNext;
        public nint reserved;
    }

    private const uint WHDR_DONE = 0x00000001;
    private const uint WAVE_MAPPER = unchecked((uint)-1);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutOpen(out nint phwo, uint uDeviceID, WAVEFORMATEX* pwfx, nint dwCallback, nint dwInstance, uint fdwOpen);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutPrepareHeader(nint hwo, WAVEHDR* pwh, uint cbwh);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutUnprepareHeader(nint hwo, WAVEHDR* pwh, uint cbwh);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutWrite(nint hwo, WAVEHDR* pwh, uint cbwh);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutReset(nint hwo);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutClose(nint hwo);

    private static void RenderLoop()
    {
        nint hwo = 0;
        var bufs = new float*[NumBufs];
        var hdrs = (WAVEHDR*)NativeMemory.AllocZeroed(NumBufs, (nuint)sizeof(WAVEHDR));
        try
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                nChannels = 2,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = 8,
                nAvgBytesPerSec = SampleRate * 8,
                cbSize = 0,
            };
            if (waveOutOpen(out hwo, WAVE_MAPPER, &fmt, 0, 0, 0) != 0 || hwo == 0)
                return;

            uint byteLen = BufFrames * 2 * sizeof(float);
            for (int i = 0; i < NumBufs; i++)
            {
                bufs[i] = (float*)NativeMemory.AllocZeroed(BufFrames * 2, sizeof(float));
                hdrs[i].lpData = (nint)bufs[i];
                hdrs[i].dwBufferLength = byteLen;
                waveOutPrepareHeader(hwo, &hdrs[i], (uint)sizeof(WAVEHDR));
                RenderScratchBlock(bufs[i], BufFrames, Volatile.Read(ref _targetVel), fadingOut: false);
                waveOutWrite(hwo, &hdrs[i], (uint)sizeof(WAVEHDR));
            }

            int fadeBlocksLeft = 2;
            while (true)
            {
                bool stopping = _stopping;
                bool wrote = false;
                for (int i = 0; i < NumBufs; i++)
                {
                    if ((hdrs[i].dwFlags & WHDR_DONE) == 0) continue;
                    if (stopping)
                    {
                        if (fadeBlocksLeft-- <= 0) goto Done;
                        RenderScratchBlock(bufs[i], BufFrames, 0f, fadingOut: true);
                    }
                    else
                    {
                        RenderScratchBlock(bufs[i], BufFrames, Volatile.Read(ref _targetVel), fadingOut: false);
                    }
                    waveOutWrite(hwo, &hdrs[i], (uint)sizeof(WAVEHDR));
                    wrote = true;
                }
                if (!wrote) Thread.Sleep(3);
            }
        Done:
            waveOutReset(hwo);
            for (int i = 0; i < NumBufs; i++)
                waveOutUnprepareHeader(hwo, &hdrs[i], (uint)sizeof(WAVEHDR));
        }
        catch { }
        finally
        {
            if (hwo != 0) try { waveOutClose(hwo); } catch { }
            for (int i = 0; i < NumBufs; i++)
                if (bufs[i] != null) NativeMemory.Free(bufs[i]);
            NativeMemory.Free(hdrs);
            _active = false;
        }
    }

    /// <summary>Headless unit test: verifies Catmull-Rom forward (+1.5x) &amp; true reverse (-2.0x) scratch synthesis.</summary>
    internal static (bool Ok, double FwdDelta, double RevDelta, float Peak) SelfTestScratch()
    {
        WantCapture = true;
        _active = false;
        _writePos = 0;
        _validFrames = 0;
        for (int i = 0; i < SampleRate; i++)
        {
            float v = MathF.Sin(2f * MathF.PI * 440f * i / SampleRate) * 0.7f;
            FeedStereo(v, v * 0.8f);
        }
        _snapWritePos = _writePos;
        _snapValid = Math.Min(_validFrames, RingFrames - 16);
        _curReadPos = _snapValid * 0.5;
        _smoothVel = 0f;
        _envGain = 1f;

        var tmp = stackalloc float[BufFrames * 2];
        double p0 = _curReadPos;
        for (int k = 0; k < 4; k++) RenderScratchBlock(tmp, BufFrames, +1.5f, false);
        double fwdDelta = _curReadPos - p0;

        double p1 = _curReadPos;
        float peak = 0f;
        for (int k = 0; k < 6; k++)
        {
            RenderScratchBlock(tmp, BufFrames, -2.0f, false);
            for (int j = 0; j < BufFrames * 2; j++)
            {
                if (float.IsNaN(tmp[j]) || float.IsInfinity(tmp[j])) return (false, fwdDelta, 0, 0);
                float a = MathF.Abs(tmp[j]);
                if (a > peak) peak = a;
            }
        }
        double revDelta = _curReadPos - p1;
        WantCapture = false;
        return (fwdDelta > 500 && revDelta < -500 && peak > 0.35f && peak <= 0.99f, fwdDelta, revDelta, peak);
    }
}
