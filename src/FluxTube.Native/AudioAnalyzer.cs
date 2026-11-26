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
    private static readonly float[] _peakHold = new float[BandCount];
    private static readonly float[] _peakVel = new float[BandCount];
    private static readonly float[] _prevBands = new float[BandCount];
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
    // Online repetition / BPM groove lock + buildup & drop structure analyzer (0 extra CPU!)
    private static readonly long[] _kickTimes = new long[12];
    private static int _kickWrite, _kickCount, _beatCount, _patternMode, _prevSection;
    private static long _lastKickRecordedAt;
    private static float _patternLock, _buildupLevel, _dropImpact, _prevCentroid = 0.35f, _centroidSlope;
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

    // Sharp transient, boom-moment, live tempo & quiet-breakdown exit detectors (for fluid fire & tempo snow)
    private static float _fluxAvg, _waveAvg, _sharpWaveHit, _sharpWaveSign = 1f;
    private static float _longEnergyAvg, _preDropLevel, _trackPeakEnergy, _boomEnergy, _dropDrive, _liveTempo;
    private static readonly long[] _hitRing = new long[16];
    private static int _hitRingIdx;
    private static long _lastTempoHitAt, _lastSharpHitAt, _dropOnsetStamp, _quietExitStamp;
    private static float _quietSec, _quietDim, _quietFlash, _quietFlashTarget, _dropOnsetStrength;
    private static bool _wasDeepQuiet;
    public static float SharpWaveHit => _sharpWaveHit;
    public static float SharpWaveSign => _sharpWaveSign;
    public static float BoomEnergy => _boomEnergy;
    public static float DropDrive => _dropDrive;
    public static float LiveTempo => _liveTempo;
    public static long DropOnsetStamp => _dropOnsetStamp;
    public static float DropOnsetStrength => _dropOnsetStrength;
    public static long QuietExitStamp => _quietExitStamp;
    public static float QuietDim => _quietDim;
    public static float QuietFlash => _quietFlash;

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
    /// <summary>0..1 periodicity lock: high when kicks repeat on a steady groove -> engages tight vertical piston pump!</summary>
    public static float PatternLock => _patternLock;
    /// <summary>0..1 buildup/riser tension before an incoming drop (lookahead waveform + live rising highs).</summary>
    public static float BuildupLevel => _buildupLevel;
    /// <summary>0..1 explosive drop detonation impulse when a drop hits after a buildup or enters a climax zone.</summary>
    public static float DropImpact => _dropImpact;
    /// <summary>Active choreography pattern: 0=Vertical Piston Pump, 1=Stereo Liquid Sway, 2=Diagonal Cross, 3=Buildup Tension, 4=Drop Detonation.</summary>
    public static int PatternMode => _patternMode;
    public static int BeatCount => _beatCount;
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

                if (next >= _peakBands[i])
                {
                    _peakBands[i] = next;
                    _peakHold[i] = 0.14f;
                    _peakVel[i] = 0f;
                }
                else if (_peakHold[i] > 0f)
                {
                    _peakHold[i] = MathF.Max(0f, _peakHold[i] - dt);
                }
                else
                {
                    _peakVel[i] = MathF.Min(1.8f, _peakVel[i] + dt * 2.1f);
                    _peakBands[i] = MathF.Max(next, _peakBands[i] - (peakFall * 0.45f + _peakVel[i] * dt));
                }

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

            // Player volume gently scales physical shake feel (capped at 100% so 150% boost never goes crazy!)
            var mpv = MpvPlayer.Instance;
            float volPct = mpv.Muted ? 0f : Math.Clamp((float)mpv.Volume, 0f, 100f);
            float volCurve = volPct <= 0.5f ? 0f : Math.Clamp(0.26f + MathF.Pow(volPct / 100f, 0.72f) * 0.74f, 0f, 1.0f);
            float combined = playing
                ? Math.Clamp((0.20f + 1.10f * MathF.Pow(_loudness, 1.25f)) * (0.58f + 0.52f * _trackDynamics) * volCurve, 0f, 1.45f)
                : 0f;
            _loudnessGain += (combined - _loudnessGain) * (1f - MathF.Exp(-dt * 14f));

            // --- Online Musical Structure, Repetition & Drop Choreographer ---
            if (playing)
            {
                // 1. Track repeating kick intervals -> detect steady repeating groove (PatternLock 0..1)
                if (_capturedBeatAt > _lastKickRecordedAt && _capturedBeatAt - _lastKickRecordedAt >= 190)
                {
                    _lastKickRecordedAt = _capturedBeatAt;
                    _kickTimes[_kickWrite] = _capturedBeatAt;
                    _kickWrite = (_kickWrite + 1) % _kickTimes.Length;
                    if (_kickCount < _kickTimes.Length) _kickCount++;
                    _beatCount++;

                    if (_kickCount >= 5)
                    {
                        Span<long> ibis = stackalloc long[11];
                        int m = 0;
                        for (int k = 1; k < _kickCount; k++)
                        {
                            int i1 = (_kickWrite - k + _kickTimes.Length) % _kickTimes.Length;
                            int i0 = (_kickWrite - k - 1 + _kickTimes.Length) % _kickTimes.Length;
                            long d = _kickTimes[i1] - _kickTimes[i0];
                            if (d >= 190 && d <= 1250) ibis[m++] = d;
                        }
                        if (m >= 3)
                        {
                            // Median IBI via simple insertion sort
                            for (int a = 1; a < m; a++)
                            {
                                long v = ibis[a]; int b = a - 1;
                                while (b >= 0 && ibis[b] > v) { ibis[b + 1] = ibis[b]; b--; }
                                ibis[b + 1] = v;
                            }
                            long med = ibis[m / 2];
                            int hits = 0;
                            for (int a = 0; a < m; a++)
                            {
                                float ratio = ibis[a] / (float)med;
                                if (MathF.Abs(ratio - 1f) < 0.16f || MathF.Abs(ratio - 2f) < 0.22f || MathF.Abs(ratio - 0.5f) < 0.12f)
                                    hits++;
                            }
                            float lockTarget = hits / (float)m;
                            _patternLock += (lockTarget - _patternLock) * 0.42f;
                        }
                    }
                }
                else if (UI.Now - _lastKickRecordedAt > 1800)
                {
                    _patternLock *= MathF.Exp(-dt * 2.5f);
                }

                // 2. Fuse Lookahead Waveform Drop/Buildup Map + Live Rising Centroid/Highs
                var (sec, mapBuildup, mapDrop) = SoundCloudBar.GetTrackCue(mpv.CurrentVideoId, mpv.TimePos, mpv.Duration);
                float centDelta = (_spectralCentroid - _prevCentroid) / MathF.Max(0.008f, dt);
                _prevCentroid = _spectralCentroid;
                _centroidSlope += (Math.Clamp(centDelta, -2f, 2f) - _centroidSlope) * (1f - MathF.Exp(-dt * 3.5f));
                float liveBuildup = (_highEnergy > 0.34f && _bassEnergy < 0.42f && _centroidSlope > 0.08f) ? Math.Clamp((_highEnergy - 0.30f) * 1.8f, 0f, 0.85f) : 0f;
                float buildTarget = MathF.Max(mapBuildup, liveBuildup);
                _buildupLevel += (buildTarget - _buildupLevel) * (1f - MathF.Exp(-dt * 6f));

                bool enteredDrop = (sec == 3 && _prevSection != 3) || (_buildupLevel > 0.45f && _subBass > 0.55f && _beatEnvelope > 0.55f);
                _prevSection = sec;
                if (enteredDrop) _dropImpact = 1.0f;
                else if (mapDrop > 0.35f && _subBass > 0.45f) _dropImpact = MathF.Max(_dropImpact, mapDrop * 0.72f);
                _dropImpact *= MathF.Exp(-dt * 1.45f);
                if (_dropImpact < 0.01f) _dropImpact = 0f;

                // 3. Select active choreography PatternMode dynamically!
                if (_dropImpact > 0.32f || (sec == 3 && _subBass > 0.48f))
                    _patternMode = 4; // DROP DETONATION!
                else if (_buildupLevel > 0.38f)
                    _patternMode = 3; // BUILDUP TENSION (Разгон!)
                else if (_patternLock >= 0.58f)
                    // Repeating rhythmic pattern -> tight VERTICAL PISTON PUMP (0) alternating with Diagonal Cross (2) on every 8-beat phrase!
                    _patternMode = ((_beatCount / 8) & 1) == 0 ? 0 : 2;
                else
                    // Melodic / freeform / stereo-rich section -> Liquid Stereo Sway (1)
                    _patternMode = 1;
            }
            else
            {
                _patternLock = 0f; _buildupLevel = 0f; _dropImpact = 0f; _patternMode = 1; _prevSection = 0;
            }

            float wavePeak = 0f;
            for (int i = 0; i < WavePoints; i++)
            {
                float target = playing ? _targetWave[i] : 0f;
                float next = _smoothWave[i] + (target - _smoothWave[i]) * waveLerp;
                if (MathF.Abs(next) < 0.001f) next = 0f;
                _smoothWave[i] = next;
                float absW = MathF.Abs(target);
                if (absW > wavePeak) wavePeak = absW;
                if (visual && MathF.Abs(next) > 0.002f) anyMotion = true;
            }

            // Sharp transient, boom-moment, live tempo & quiet-breakdown detectors
            if (!playing)
            {
                _sharpWaveHit = 0f;
                _fluxAvg = 0f;
                _waveAvg = 0f;
                _longEnergyAvg = 0f;
                _preDropLevel = 0f;
                _trackPeakEnergy = 0f;
                _boomEnergy = 0f;
                _dropDrive = 0f;
                _liveTempo *= MathF.Exp(-dt * 3f);
                if (_liveTempo < 0.005f) _liveTempo = 0f;
                _dropOnsetStrength = 0f;
                _quietSec = 0f;
                _quietDim *= MathF.Exp(-dt * 4f);
                _quietFlash *= MathF.Exp(-dt * 4f);
                _quietFlashTarget = 0f;
                _wasDeepQuiet = false;
                Array.Clear(_prevBands);
            }
            else
            {
                float flux = 0f;
                for (int i = 0; i < BandCount; i++)
                {
                    float diff = _targetBands[i] - _prevBands[i];
                    if (diff > 0f) flux += diff * (i < 8 ? 1.45f : 0.9f);
                    _prevBands[i] = _targetBands[i];
                }
                flux /= BandCount;

                float avgRate = 1f - MathF.Exp(-dt * 4.5f);
                _fluxAvg += (flux - _fluxAvg) * avgRate;
                _waveAvg += (wavePeak - _waveAvg) * avgRate;

                float fluxSpike = flux > _fluxAvg * 1.55f + 0.032f
                    ? Math.Clamp((flux - _fluxAvg * 1.35f) * 6.5f, 0f, 1f) : 0f;
                float waveSpike = wavePeak > _waveAvg * 1.35f + 0.14f
                    ? Math.Clamp((wavePeak - _waveAvg * 1.20f) * 2.2f, 0f, 1f) : 0f;
                float bassHit = _beatEnvelope > 0.20f ? Math.Clamp(MathF.Pow(_beatEnvelope, 0.68f), 0f, 1f) : 0f;

                float impulse = MathF.Max(bassHit, MathF.Max(fluxSpike, waveSpike));
                _sharpWaveHit *= MathF.Exp(-dt * 14f);
                if (impulse > _sharpWaveHit && impulse > 0.14f)
                {
                    if (UI.Now - _lastSharpHitAt > 85)
                    {
                        _sharpWaveSign = -_sharpWaveSign;
                        _lastSharpHitAt = UI.Now;
                    }
                    _sharpWaveHit = impulse;
                }
                if (_sharpWaveHit < 0.008f) _sharpWaveHit = 0f;

                float totalWave = Math.Clamp(_bassEnergy * 0.56f + _midEnergy * 0.24f + _highEnergy * 0.10f + wavePeak * 0.28f, 0f, 1.3f);
                _longEnergyAvg += (totalWave - _longEnergyAvg) * (1f - MathF.Exp(-dt * 0.45f));
                _trackPeakEnergy = MathF.Max(totalWave, _trackPeakEnergy * MathF.Exp(-dt * 0.07f));
                float relToPeak = _trackPeakEnergy > 0.18f ? Math.Clamp((totalWave - _trackPeakEnergy * 0.58f) / (_trackPeakEnergy * 0.42f), 0f, 1f) : 0f;
                float surgeAboveAvg = Math.Clamp((totalWave - _longEnergyAvg * 1.12f) * 3.0f, 0f, 1f);
                float boomTarget = Math.Clamp(relToPeak * 0.65f + surgeAboveAvg * 0.55f + (_bassEnergy > 0.52f ? (_bassEnergy - 0.52f) * 1.4f : 0f), 0f, 1f);
                if (totalWave < 0.28f) boomTarget *= totalWave / 0.28f;
                _boomEnergy += (boomTarget - _boomEnergy) * (1f - MathF.Exp(-dt * (boomTarget > _boomEnergy ? 11f : 2.6f)));
                if (_boomEnergy < 0.01f) _boomEnergy = 0f;

                float heavyKick = (_beatEnvelope > 0.45f && _bassEnergy > 0.40f) ? Math.Clamp((_beatEnvelope - 0.35f) * 1.55f, 0f, 1f) : 0f;
                float buildup = totalWave > _longEnergyAvg * 1.06f && _bassEnergy > 0.30f ? Math.Clamp((totalWave - _longEnergyAvg * 1.06f) * 2.4f, 0f, 0.55f) : 0f;
                float dropUnleash = _boomEnergy > 0.38f ? Math.Clamp((_boomEnergy - 0.32f) * 1.65f, 0f, 1f) : 0f;
                float driveTarget = Math.Clamp(MathF.Max(MathF.Max(dropUnleash, buildup), heavyKick), 0f, 1f);
                if (_quietDim > 0.1f) driveTarget *= MathF.Max(0f, 1f - _quietDim * 2.5f);
                _dropDrive += (driveTarget - _dropDrive) * (1f - MathF.Exp(-dt * (driveTarget > _dropDrive ? 14f : 4.2f)));
                if (_dropDrive < 0.015f) _dropDrive = 0f;

                bool sharpAttack = _beatEnvelope > 0.36f || _sharpWaveHit > 0.42f || _dropImpact > 0.85f;
                bool energyExplosion = totalWave > _preDropLevel * 1.22f + 0.07f && _bassEnergy > 0.30f;
                bool heavyClimaxHit = _bassEnergy > 0.44f && _sharpWaveHit > 0.46f && _boomEnergy > 0.38f;
                if ((_dropImpact > 0.85f || (sharpAttack && (energyExplosion || heavyClimaxHit))) && UI.Now - _dropOnsetStamp > 1200)
                {
                    _dropOnsetStamp = UI.Now;
                    _dropOnsetStrength = Math.Clamp(MathF.Max(_sharpWaveHit, MathF.Max(_beatEnvelope, _dropImpact)) * 0.7f + _bassEnergy * 0.5f, 0.55f, 1f);
                    _dropDrive = MathF.Max(_dropDrive, 0.92f);
                }
                _preDropLevel += (totalWave - _preDropLevel) * (1f - MathF.Exp(-dt * 0.60f));

                long nowMs = UI.Now;
                if ((impulse > 0.24f || _beatEnvelope > 0.34f) && nowMs - _lastTempoHitAt > 110)
                {
                    _lastTempoHitAt = nowMs;
                    _hitRing[_hitRingIdx] = nowMs;
                    _hitRingIdx = (_hitRingIdx + 1) & 15;
                }
                int recentHits = 0;
                for (int h = 0; h < _hitRing.Length; h++)
                {
                    long ts = _hitRing[h];
                    if (ts > 0 && nowMs - ts <= 2600) recentHits++;
                }
                float hitsPerSec = recentHits / 2.6f;
                float speedMul = (float)Math.Clamp(mpv.Speed, 0.5, 2.0);
                float paceFromHits = Math.Clamp((hitsPerSec - 0.35f) / 3.2f, 0f, 1f);
                float paceFromFlux = Math.Clamp((_fluxAvg - 0.015f) * 14f, 0f, 1f);
                float tempoTarget = Math.Clamp((paceFromHits * 0.68f + paceFromFlux * 0.32f) * speedMul, 0f, 1f);
                if (_quietDim > 0.15f) tempoTarget *= MathF.Max(0.15f, 1f - _quietDim * 0.85f);
                _liveTempo += (tempoTarget - _liveTempo) * (1f - MathF.Exp(-dt * (tempoTarget > _liveTempo ? 3.5f : 1.4f)));

                float quietThresh = _trackPeakEnergy > 0.22f ? Math.Clamp(_trackPeakEnergy * 0.34f, 0.08f, 0.24f) : 0.14f;
                float exitThresh = quietThresh * 1.32f + 0.025f;
                bool isQuiet = totalWave < quietThresh && _trackPeakEnergy > 0.16f;
                if (isQuiet)
                {
                    _quietSec = MathF.Min(12f, _quietSec + dt);
                    if (_quietSec > 0.75f)
                    {
                        float dimTarget = Math.Clamp((_quietSec - 0.75f) / 1.4f, 0f, 1f);
                        _quietDim += (dimTarget - _quietDim) * (1f - MathF.Exp(-dt * 2.2f));
                        if (_quietDim > 0.25f) _wasDeepQuiet = true;
                    }
                    if (_wasDeepQuiet && totalWave > quietThresh * 0.72f && flux > 0.015f)
                        _quietFlashTarget = MathF.Max(_quietFlashTarget, 0.55f);
                }
                else
                {
                    if (_wasDeepQuiet && (totalWave >= exitThresh || _sharpWaveHit > 0.16f))
                    {
                        _quietFlashTarget = 1f;
                        _quietExitStamp = UI.Now;
                        _dropDrive = MathF.Max(_dropDrive, 0.95f);
                        _wasDeepQuiet = false;
                    }
                    _quietSec = MathF.Max(0f, _quietSec - dt * 3f);
                    _quietDim *= MathF.Exp(-dt * 2.8f);
                    if (_quietDim < 0.005f) _quietDim = 0f;
                }

                if (_quietFlashTarget > 0.01f)
                {
                    _quietFlash += (_quietFlashTarget - _quietFlash) * (1f - MathF.Exp(-dt * 4.2f));
                    if (_quietFlash >= _quietFlashTarget - 0.06f) _quietFlashTarget = 0f;
                }
                else
                {
                    _quietFlash *= MathF.Exp(-dt * 1.9f);
                    if (_quietFlash < 0.005f) _quietFlash = 0f;
                }
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
                        Thread.Sleep(2);
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

    private static float SampleSmooth(float[] src, float pos)
    {
        float f = Math.Clamp(pos, 0f, BandCount - 1.001f);
        int lo = (int)f;
        int hi = Math.Min(lo + 1, BandCount - 1);
        float t = f - lo;
        t = t * t * (3f - 2f * t);
        float Sm(int idx) => (src[Math.Max(0, idx - 1)] + 2f * src[idx] + src[Math.Min(BandCount - 1, idx + 1)]) * 0.25f;
        return Sm(lo) + (Sm(hi) - Sm(lo)) * t;
    }

    private static (float x, float y, float nx, float ny) RoundedRectPoint(R r, float rad, float frac)
    {
        rad = Math.Clamp(rad, 2f, MathF.Min(r.W, r.H) * 0.48f);
        float topW = MathF.Max(1f, r.W - 2f * rad);
        float sideH = MathF.Max(1f, r.H - 2f * rad);
        float arc = MathF.PI * 0.5f * rad;
        float perim = 2f * (topW + sideH) + 4f * arc;
        float d = ((frac % 1f) + 1f) % 1f * perim;

        if (d < topW)
            return (r.X + rad + d, r.Y, 0f, -1f);
        d -= topW;
        if (d < arc)
        {
            float a = -MathF.PI * 0.5f + d / rad;
            float nx = MathF.Cos(a), ny = MathF.Sin(a);
            return (r.Right - rad + nx * rad, r.Y + rad + ny * rad, nx, ny);
        }
        d -= arc;
        if (d < sideH)
            return (r.Right, r.Y + rad + d, 1f, 0f);
        d -= sideH;
        if (d < arc)
        {
            float a = d / rad;
            float nx = MathF.Cos(a), ny = MathF.Sin(a);
            return (r.Right - rad + nx * rad, r.Bottom - rad + ny * rad, nx, ny);
        }
        d -= arc;
        if (d < topW)
            return (r.Right - rad - d, r.Bottom, 0f, 1f);
        d -= topW;
        if (d < arc)
        {
            float a = MathF.PI * 0.5f + d / rad;
            float nx = MathF.Cos(a), ny = MathF.Sin(a);
            return (r.X + rad + nx * rad, r.Bottom - rad + ny * rad, nx, ny);
        }
        d -= arc;
        if (d < sideH)
            return (r.X, r.Bottom - rad - d, -1f, 0f);
        d -= sideH;
        {
            float a = MathF.PI + d / rad;
            float nx = MathF.Cos(a), ny = MathF.Sin(a);
            return (r.X + rad + nx * rad, r.Y + rad + ny * rad, nx, ny);
        }
    }

    /// <summary>Frequency halo hugging the rounded cover perimeter with glowing rays &amp; orbiting peak sparks.</summary>
    public static void DrawCoverSpectrum(R art, float opacity = 1f, uint primary = 0, uint secondary = 0)
    {
        if (AppSettings.Current.VisualizerStyle == "off") return;
        RequestFrame();
        uint c1 = primary != 0 ? primary : Th.Brand;
        uint c2 = secondary != 0 ? secondary : Gfx.Mix(c1, 0xFF73D8EB, 0.52f);
        uint tip = Gfx.Mix(c1, 0xFFFFFFFF, 0.72f);

        var track = art.Inset(-4f);
        float rad = Gfx.Corner(16);
        float maxLen = MathF.Min(30f, MathF.Max(14f, art.H * 0.20f));
        float kickBoost = 1f + BeatMotionStrength * 0.35f + _bassPressure * 0.2f;

        Gfx.Stroke(art.Inset(-2f), Gfx.Fade(c1, (0.14f + _bassEnergy * 0.32f) * opacity), Gfx.Corner(15), 2f);

        const int rays = 40;
        for (int i = 0; i < rays; i++)
        {
            float frac = i / (float)rays;
            var (x, y, nx, ny) = RoundedRectPoint(track, rad, frac);
            float sym = MathF.Abs(MathF.Sin(frac * MathF.PI * 2f));
            float bandPos = sym * (BandCount - 1);
            float energy = SampleSmooth(_smoothBands, bandPos);
            float peak = SampleSmooth(_peakBands, bandPos);
            float v = Math.Clamp(MathF.Pow(energy, 0.74f) * kickBoost, 0f, 1.15f);
            float pv = Math.Clamp(MathF.Pow(peak, 0.76f) * kickBoost, 0f, 1.2f);

            float length = 2.5f + v * maxLen;
            float tipX = x + nx * length, tipY = y + ny * length;
            uint color = Gfx.Mix(c1, c2, sym);

            if (v > 0.24f)
                Gfx.Line(x, y, tipX, tipY, Gfx.Fade(color, MathF.Min(0.15f, v * 0.12f) * opacity), 6f);
            Gfx.Line(x, y, tipX, tipY, Gfx.Fade(color, (0.14f + MathF.Min(0.80f, v * 0.72f)) * opacity), 2.1f);

            if (pv > 0.26f)
            {
                float pLen = 5.5f + pv * maxLen;
                Gfx.Circle(x + nx * pLen, y + ny * pLen, 1.5f, Gfx.Fade(tip, MathF.Min(0.88f, pv * 0.85f) * opacity));
            }
        }
    }

    /// <summary>Ultra-fast GPU Direct2D stage visualizers (7 styles, zero COM brush allocations).</summary>
    public static void DrawStageVisualizer(R r, string style, float opacity = 1f, float gain = 1f, uint primary = 0, uint secondary = 0)
    {
        if (style == "off" || r.W < 40 || r.H < 24 || opacity <= 0.01f) return;
        RequestFrame();
        Gfx.PushClip(r);

        uint c1 = primary != 0 ? primary : Th.Brand;
        uint c2 = secondary != 0 ? secondary : Gfx.Mix(c1, 0xFF73D8EB, 0.52f);
        uint hot = Gfx.Mix(c2, 0xFFFFFFFF, 0.78f);

        if (style == "bars_wave")
        {
            float baseline = r.Y + r.H * 0.77f;
            float maxUp = r.H * 0.72f;
            float maxRefl = r.H * 0.21f;

            int halfBars = Math.Clamp((int)(r.W / 24f), 14, 24);
            int totalBars = halfBars * 2;
            float step = r.W / totalBars;
            float gap = Math.Clamp(step * 0.30f, 2f, 4f);
            float barW = MathF.Max(3f, step - gap);

            for (int i = 0; i < totalBars; i++)
            {
                float u = (i + 0.5f) / totalBars;
                float dist = MathF.Abs(u * 2f - 1f);
                float bandPos = MathF.Pow(dist, 0.86f) * (BandCount - 1);

                float energy = SampleSmooth(_smoothBands, bandPos);
                float peak = SampleSmooth(_peakBands, bandPos);
                float edge = MathF.Pow(MathF.Sin(u * MathF.PI), 0.20f);

                float v = Math.Clamp(MathF.Pow(energy, 0.74f) * gain, 0f, 1f);
                float pv = Math.Clamp(MathF.Pow(peak, 0.76f) * gain, 0f, 1f);

                float h = MathF.Max(3f, v * maxUp * edge);
                float ph = pv * maxUp * edge;
                float bx = r.X + i * step + gap * 0.5f;
                float topY = baseline - h;

                uint baseCol = Gfx.Mix(c1, c2, dist);
                uint topCol = Gfx.Mix(baseCol, hot, Math.Clamp(v * 0.78f, 0f, 0.88f));

                if (v > 0.12f)
                    Gfx.Fill(new R(bx - barW * 0.35f, topY - 2f, barW * 1.7f, h + 3f), Gfx.Fade(baseCol, v * 0.14f * opacity));

                Gfx.Fill(new R(bx, topY, barW, h), Gfx.Fade(baseCol, (0.34f + v * 0.64f) * opacity));
                if (h > 8f)
                    Gfx.Fill(new R(bx, topY, barW, h * 0.38f), Gfx.Fade(topCol, (0.28f + v * 0.56f) * opacity));

                float reflH = MathF.Max(1.5f, v * maxRefl * edge);
                Gfx.Fill(new R(bx, baseline + 2f, barW, reflH), Gfx.Fade(baseCol, (0.07f + v * 0.21f) * opacity));

                if (ph > 4f)
                {
                    float capY = baseline - ph - 3.5f;
                    float capAlpha = Math.Clamp((0.42f + pv * 0.58f) * opacity, 0f, 1f);
                    Gfx.Fill(new R(bx, capY, barW, 2.6f), Gfx.Fade(hot, capAlpha));
                }
            }

            Gfx.Line(r.X + r.W * 0.02f, baseline + 0.5f, r.Right - r.W * 0.02f, baseline + 0.5f,
                Gfx.Fade(Gfx.Mix(c1, hot, 0.45f), (0.18f + _bassEnergy * 0.30f) * opacity), 1.2f);
        }
        else if (style == "bars")
        {
            float baseline = r.Y + r.H * 0.78f;
            float maxUp = r.H * 0.70f;
            float maxRefl = r.H * 0.18f;

            int halfCols = Math.Clamp((int)(r.W / 26f), 12, 24);
            int totalCols = halfCols * 2;
            float step = r.W / totalCols;
            float gapX = Math.Clamp(step * 0.24f, 2f, 4f);
            float colW = MathF.Max(3f, step - gapX);

            int maxSegs = Math.Clamp((int)(maxUp / 7.5f), 5, 14);
            float segPitch = maxUp / maxSegs;
            float segH = MathF.Max(2.4f, segPitch - 2.2f);

            for (int i = 0; i < totalCols; i++)
            {
                float u = (i + 0.5f) / totalCols;
                float dist = MathF.Abs(u * 2f - 1f);
                float bandPos = MathF.Pow(dist, 0.90f) * (BandCount - 1);

                float energy = SampleSmooth(_smoothBands, bandPos);
                float peak = SampleSmooth(_peakBands, bandPos);
                float edge = MathF.Pow(MathF.Sin(u * MathF.PI), 0.22f);

                float v = Math.Clamp(MathF.Pow(energy, 0.76f) * gain, 0f, 1f);
                float pv = Math.Clamp(MathF.Pow(peak, 0.78f) * gain, 0f, 1f);
                int litSegs = Math.Clamp((int)MathF.Round(v * edge * maxSegs), 0, maxSegs);
                int peakSeg = Math.Clamp((int)MathF.Round(pv * edge * maxSegs), 0, maxSegs);

                float cx = r.X + i * step + gapX * 0.5f;
                uint colTint = Gfx.Mix(c1, c2, dist);

                for (int s = 0; s < litSegs; s++)
                {
                    float frac = s / (float)Math.Max(1, maxSegs - 1);
                    float sy = baseline - (s + 1) * segPitch + 2.2f;
                    uint segCol = frac > 0.72f ? Gfx.Mix(colTint, hot, (frac - 0.72f) / 0.28f * 0.85f)
                        : Gfx.Mix(c1, c2, Math.Clamp(dist * 0.65f + frac * 0.55f, 0f, 1f));
                    Gfx.Fill(new R(cx, sy, colW, segH), Gfx.Fade(segCol, (0.45f + frac * 0.52f) * opacity));
                }

                if (peakSeg > 0)
                {
                    float py = baseline - (peakSeg + 1) * segPitch + 2.2f;
                    Gfx.Fill(new R(cx, py, colW, segH), Gfx.Fade(hot, (0.58f + pv * 0.42f) * opacity));
                }

                float reflH = v * maxRefl * edge;
                if (reflH > 1.5f)
                    Gfx.Fill(new R(cx, baseline + 2.5f, colW, reflH), Gfx.Fade(colTint, (0.06f + v * 0.16f) * opacity));
            }
            Gfx.Line(r.X + r.W * 0.03f, baseline + 0.5f, r.Right - r.W * 0.03f, baseline + 0.5f,
                Gfx.Fade(c2, (0.14f + _bassEnergy * 0.22f) * opacity), 1f);
        }
        else if (style == "ribbon")
        {
            float baseline = r.Y + r.H * 0.72f;
            float maxH = r.H * 0.62f;
            const int count = 76;
            float px = r.X, py = baseline, pPeakY = baseline;

            for (int i = 0; i <= count; i++)
            {
                float u = i / (float)count;
                float dist = MathF.Abs(u * 2f - 1f);
                float bandPos = dist * (BandCount - 1);
                float v = Math.Clamp(MathF.Pow(SampleSmooth(_smoothBands, bandPos), 0.78f) * gain, 0f, 1f);
                float pv = Math.Clamp(MathF.Pow(SampleSmooth(_peakBands, bandPos), 0.80f) * gain, 0f, 1f);
                float edge = MathF.Pow(MathF.Sin(u * MathF.PI), 0.40f);

                float height = v * maxH * edge;
                float peakH = pv * maxH * edge;
                float x = r.X + u * r.W;
                float y = baseline - height;
                float peakY = baseline - peakH;
                uint color = Gfx.Mix(c1, c2, dist);

                if (height > 2f)
                {
                    Gfx.Line(x, baseline, x, y, Gfx.Fade(color, 0.09f * v * opacity), MathF.Max(2f, r.W / count + 0.5f));
                    Gfx.Line(x, baseline + 1.5f, x, baseline + height * 0.25f, Gfx.Fade(color, 0.06f * v * opacity), 2f);
                }

                if (i > 0)
                {
                    Gfx.Line(px, py, x, y, Gfx.Fade(color, 0.14f * v * opacity), 7f);
                    Gfx.Line(px, py, x, y, Gfx.Fade(hot, (0.22f + v * 0.72f) * opacity), 1.6f);
                    if (pv > 0.14f)
                        Gfx.Line(px, pPeakY - 2f, x, peakY - 2f, Gfx.Fade(hot, pv * 0.34f * opacity), 1.0f);
                }
                px = x; py = y; pPeakY = peakY;
            }

            DrawWaveRibbon(r, baseline - maxH * 0.20f, r.H * 0.22f, c1, c2, hot, opacity * 0.90f);
        }
        else if (style == "pulse")
        {
            float mid = r.Y + r.H * 0.50f;
            float maxHalf = r.H * 0.44f;

            int halfBars = Math.Clamp((int)(r.W / 22f), 14, 28);
            int totalBars = halfBars * 2;
            float step = r.W / totalBars;
            float gap = Math.Clamp(step * 0.30f, 1.8f, 3.4f);
            float barW = MathF.Max(2.4f, step - gap);

            for (int i = 0; i < totalBars; i++)
            {
                float u = (i + 0.5f) / totalBars;
                float dist = MathF.Abs(u * 2f - 1f);
                float bandPos = MathF.Pow(dist, 0.85f) * (BandCount - 1);

                float v = Math.Clamp(MathF.Pow(SampleSmooth(_smoothBands, bandPos), 0.76f) * gain, 0f, 1f);
                float pv = Math.Clamp(MathF.Pow(SampleSmooth(_peakBands, bandPos), 0.78f) * gain, 0f, 1f);
                float edge = MathF.Pow(MathF.Sin(u * MathF.PI), 0.30f);

                float halfH = MathF.Max(2f, v * maxHalf * edge);
                float peakH = pv * maxHalf * edge;
                float bx = r.X + i * step + gap * 0.5f;
                uint col = Gfx.Mix(c1, c2, dist);

                if (v > 0.12f)
                    Gfx.Fill(new R(bx - barW * 0.4f, mid - halfH - 2f, barW * 1.8f, halfH * 2f + 4f),
                        Gfx.Fade(col, v * 0.12f * opacity));

                Gfx.Fill(new R(bx, mid - halfH, barW, halfH * 2f), Gfx.Fade(col, (0.30f + v * 0.65f) * opacity));
                Gfx.Fill(new R(bx, mid - halfH * 0.45f, barW, halfH * 0.9f), Gfx.Fade(hot, (0.18f + v * 0.50f) * opacity));

                if (peakH > 5f)
                {
                    float pa = Math.Clamp((0.38f + pv * 0.62f) * opacity, 0f, 1f);
                    Gfx.Fill(new R(bx, mid - peakH - 4f, barW, 2.2f), Gfx.Fade(hot, pa));
                    Gfx.Fill(new R(bx, mid + peakH + 1.8f, barW, 2.2f), Gfx.Fade(hot, pa * 0.75f));
                }
            }

            DrawWaveRibbon(r, mid, r.H * 0.24f, c2, hot, 0xFFFFFFFF, opacity * 0.92f);
        }
        else if (style == "cyber")
        {
            float baseline = r.Y + r.H * 0.78f;
            float maxUp = r.H * 0.72f;
            float maxRefl = r.H * 0.20f;
            uint neonPink = Gfx.Mix(c1, 0xFFFF2E93, 0.45f);
            uint neonCyan = Gfx.Mix(c2, 0xFF00F0FF, 0.55f);

            int totalBars = Math.Clamp((int)(r.W / 16f), 24, 52);
            float step = r.W / totalBars;
            float gap = Math.Clamp(step * 0.26f, 2f, 4f);
            float barW = MathF.Max(3f, step - gap);

            for (int i = 0; i < totalBars; i++)
            {
                float u = i / (float)Math.Max(1, totalBars - 1);
                float bandPos = MathF.Pow(u, 0.92f) * (BandCount - 1);
                float v = Math.Clamp(MathF.Pow(SampleSmooth(_smoothBands, bandPos), 0.74f) * gain, 0f, 1f);
                float pv = Math.Clamp(MathF.Pow(SampleSmooth(_peakBands, bandPos), 0.76f) * gain, 0f, 1f);

                float h = MathF.Max(3f, v * maxUp);
                float ph = pv * maxUp;
                float bx = r.X + i * step + gap * 0.5f;
                float topY = baseline - h;
                uint col = Gfx.Mix(neonPink, neonCyan, u);
                uint tipCol = Gfx.Mix(col, 0xFFFFFFFF, 0.72f);

                if (v > 0.14f)
                    Gfx.Fill(new R(bx - barW * 0.35f, topY - 2f, barW * 1.7f, h + 3f), Gfx.Fade(col, v * 0.14f * opacity));

                Gfx.Fill(new R(bx, topY, barW, h), Gfx.Fade(col, (0.36f + v * 0.62f) * opacity));
                Gfx.Fill(new R(bx, topY, barW, MathF.Min(h, 4f)), Gfx.Fade(tipCol, (0.55f + v * 0.45f) * opacity));

                float reflH = MathF.Max(1.5f, v * maxRefl);
                Gfx.Fill(new R(bx, baseline + 2.5f, barW, reflH), Gfx.Fade(col, (0.07f + v * 0.19f) * opacity));

                if (ph > 5f)
                {
                    float py = baseline - ph - 4.5f;
                    Gfx.Fill(new R(bx, py, barW, 2.4f), Gfx.Fade(0xFFFFFFFF, (0.45f + pv * 0.55f) * opacity));
                }
            }

            Gfx.Line(r.X, baseline + 0.5f, r.Right, baseline + 0.5f, Gfx.Fade(neonCyan, (0.18f + _bassEnergy * 0.32f) * opacity), 3.5f);
            Gfx.Line(r.X, baseline + 0.5f, r.Right, baseline + 0.5f, Gfx.Fade(0xFFFFFFFF, (0.35f + _bassEnergy * 0.45f) * opacity), 1.1f);
        }
        else if (style == "orbit")
        {
            float mid = r.Y + r.H * 0.50f;
            float maxHalf = r.H * 0.44f;
            const int pts = 68;
            float px = r.X, pyUp = mid, pyDn = mid;

            for (int i = 0; i <= pts; i++)
            {
                float u = i / (float)pts;
                float dist = MathF.Abs(u * 2f - 1f);
                float bandPos = MathF.Pow(dist, 0.85f) * (BandCount - 1);
                float v = Math.Clamp(MathF.Pow(SampleSmooth(_smoothBands, bandPos), 0.74f) * gain, 0f, 1f);
                float pv = Math.Clamp(MathF.Pow(SampleSmooth(_peakBands, bandPos), 0.76f) * gain, 0f, 1f);
                float edge = MathF.Pow(MathF.Sin(u * MathF.PI), 0.35f);

                float h = v * maxHalf * edge;
                float ph = pv * maxHalf * edge;
                float x = r.X + u * r.W;
                float yUp = mid - h;
                float yDn = mid + h * 0.82f;
                uint col = Gfx.Mix(c1, c2, dist);

                if (h > 2f)
                    Gfx.Line(x, yUp, x, yDn, Gfx.Fade(col, (0.06f + v * 0.14f) * opacity), MathF.Max(2f, r.W / pts + 0.6f));

                if (i > 0)
                {
                    Gfx.Line(px, pyUp, x, yUp, Gfx.Fade(col, (0.14f + v * 0.24f) * opacity), 7f);
                    Gfx.Line(px, pyUp, x, yUp, Gfx.Fade(hot, (0.28f + v * 0.72f) * opacity), 1.8f);
                    Gfx.Line(px, pyDn, x, yDn, Gfx.Fade(col, (0.10f + v * 0.18f) * opacity), 5.5f);
                    Gfx.Line(px, pyDn, x, yDn, Gfx.Fade(col, (0.22f + v * 0.58f) * opacity), 1.4f);
                }

                if ((i % 3) == 0 && ph > 6f)
                    Gfx.Circle(x, mid - ph - 3.5f, 1.6f, Gfx.Fade(0xFFFFFFFF, (0.40f + pv * 0.60f) * opacity));

                px = x; pyUp = yUp; pyDn = yDn;
            }
            Gfx.Line(r.X + r.W * 0.03f, mid, r.Right - r.W * 0.03f, mid, Gfx.Fade(hot, (0.15f + _bassEnergy * 0.28f) * opacity), 1f);
        }
        else if (style == "matrix")
        {
            float baseline = r.Y + r.H * 0.80f;
            float maxUp = r.H * 0.72f;
            int halfCols = Math.Clamp((int)(r.W / 26f), 12, 24);
            int totalCols = halfCols * 2;
            float step = r.W / totalCols;
            int maxDots = Math.Clamp((int)(maxUp / 8.5f), 5, 13);
            float pitchY = maxUp / maxDots;
            float dotR = Math.Clamp(MathF.Min(step * 0.30f, pitchY * 0.34f), 1.8f, 3.6f);

            for (int i = 0; i < totalCols; i++)
            {
                float u = (i + 0.5f) / totalCols;
                float dist = MathF.Abs(u * 2f - 1f);
                float bandPos = MathF.Pow(dist, 0.88f) * (BandCount - 1);
                float v = Math.Clamp(MathF.Pow(SampleSmooth(_smoothBands, bandPos), 0.75f) * gain, 0f, 1f);
                float pv = Math.Clamp(MathF.Pow(SampleSmooth(_peakBands, bandPos), 0.77f) * gain, 0f, 1f);
                float edge = MathF.Pow(MathF.Sin(u * MathF.PI), 0.22f);

                int lit = Math.Clamp((int)MathF.Round(v * edge * maxDots), 0, maxDots);
                int peakDot = Math.Clamp((int)MathF.Round(pv * edge * maxDots), 0, maxDots);
                float cx = r.X + (i + 0.5f) * step;
                uint col = Gfx.Mix(c1, c2, dist);

                for (int d = 0; d < lit; d++)
                {
                    float frac = d / (float)Math.Max(1, maxDots - 1);
                    float cy = baseline - (d + 0.5f) * pitchY;
                    uint dc = frac > 0.70f ? Gfx.Mix(col, hot, (frac - 0.70f) / 0.30f * 0.85f) : col;
                    Gfx.Circle(cx, cy, dotR, Gfx.Fade(dc, (0.42f + frac * 0.58f) * opacity));
                }

                if (peakDot > 0)
                {
                    float py = baseline - (peakDot + 0.5f) * pitchY;
                    Gfx.Circle(cx, py, dotR * 1.35f, Gfx.Fade(col, 0.25f * opacity));
                    Gfx.Circle(cx, py, dotR * 0.85f, Gfx.Fade(0xFFFFFFFF, (0.55f + pv * 0.45f) * opacity));
                }

                if (lit > 0)
                    Gfx.Circle(cx, baseline + pitchY * 0.65f, dotR * 0.85f, Gfx.Fade(col, (0.08f + v * 0.18f) * opacity));
            }
            Gfx.Line(r.X + r.W * 0.03f, baseline + 1f, r.Right - r.W * 0.03f, baseline + 1f,
                Gfx.Fade(c2, (0.14f + _bassEnergy * 0.24f) * opacity), 1f);
        }

        Gfx.PopClip();
    }

    private static void DrawWaveRibbon(R r, float midY, float amp, uint c1, uint c2, uint core, float opacity, int phaseShift = 0)
    {
        const int pts = 48;
        for (int layer = 0; layer < 2; layer++)
        {
            float px = r.X, py = midY;
            float w = layer == 0 ? 6.5f : 1.5f;
            float baseA = layer == 0 ? 0.14f : 0.80f;
            for (int i = 0; i < pts; i++)
            {
                float u = i / (float)(pts - 1);
                float env = MathF.Sin(u * MathF.PI);
                int idx = ((int)(u * (WavePoints - 1)) + phaseShift) % WavePoints;
                float x = r.X + u * r.W;
                float y = midY + _smoothWave[idx] * amp * env;
                uint col = layer == 1 ? Gfx.Mix(Gfx.Mix(c1, c2, u), core, 0.55f) : Gfx.Mix(c1, c2, u);
                if (i > 0) Gfx.Line(px, py, x, y, Gfx.Fade(col, baseA * opacity), w);
                px = x; py = y;
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
