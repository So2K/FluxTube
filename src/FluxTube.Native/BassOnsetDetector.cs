namespace FluxTube;

/// <summary>Sample-domain 35–180 Hz onset detector; independent of FFT/render cadence.</summary>
public sealed class BassOnsetDetector
{
    private struct Filter
    {
        public double B0, B1, B2, A1, A2, Z1, Z2;
        public float Run(float x)
        {
            double y = B0 * x + Z1;
            Z1 = B1 * x - A1 * y + Z2;
            Z2 = B2 * x - A2 * y;
            return (float)y;
        }
    }
    private Filter _highPass, _lowPass;
    private readonly float _fastCoefficient, _slowCoefficient, _levelCoefficient;
    private readonly int _minimumGap;
    private float _fast, _slow, _wide, _level, _crest, _valley;
    private bool _hasValley;
    private long _sample, _lastHit = -1000000;
    public float AttackStrength { get; private set; }
    public float Pressure => Math.Clamp(MathF.Sqrt(MathF.Max(0, _level)) * 4f, 0, 1);
    public BassOnsetDetector(int sampleRate)
    {
        _highPass = Make(35, sampleRate, true);
        _lowPass = Make(180, sampleRate, false);
        _fastCoefficient = 1 - MathF.Exp(-1f / (sampleRate * 0.004f));
        _slowCoefficient = 1 - MathF.Exp(-1f / (sampleRate * 0.090f));
        _levelCoefficient = 1 - MathF.Exp(-1f / (sampleRate * 0.020f));
        _minimumGap = (int)(sampleRate * 0.10f);
    }
    private static Filter Make(double frequency, int rate, bool highPass)
    {
        double omega = 2 * Math.PI * frequency / rate;
        double cosine = Math.Cos(omega), alpha = Math.Sin(omega) / Math.Sqrt(2), a0 = 1 + alpha;
        double b0 = highPass ? (1 + cosine) / 2 : (1 - cosine) / 2;
        return new Filter { B0 = b0 / a0, B1 = (highPass ? -2 : 2) * b0 / a0, B2 = b0 / a0,
            A1 = -2 * cosine / a0, A2 = (1 - alpha) / a0 };
    }
    public float Process(float sample)
    {
        _sample++;
        if (!float.IsFinite(sample)) sample = 0;
        float filtered = _lowPass.Run(_highPass.Run(float.IsFinite(sample) ? sample : 0));
        _wide += (sample * sample - _wide) * _fastCoefficient;
        float power = filtered * filtered;
        _fast += (power - _fast) * _fastCoefficient;
        _slow += (power - _slow) * _slowCoefficient;
        _level += (power - _level) * _levelCoefficient;
        if (_sample - _lastHit < _minimumGap)
        {
            // Start the impulse immediately, then measure its real punch during the first 25 ms.
            if (_sample - _lastHit < _minimumGap / 4)
            {
                float contrast = Math.Clamp((_fast - _slow) / (_slow + 0.0001f), 0, 1);
                AttackStrength = MathF.Max(AttackStrength, Math.Clamp(MathF.Sqrt(MathF.Max(0, _fast)) * 1.55f + contrast * 0.25f, 0.15f, 1));
            }
            _crest = _valley = _level;
            _hasValley = false;
            return 0;
        }
        if (!_hasValley)
        {
            if (_level >= _crest) _crest = _valley = _level;
            else if (_level < _crest * 0.98f) { _hasValley = true; _valley = _level; }
        }
        else _valley = MathF.Min(_valley, _level);
        bool firstAttack = _lastHit < 0 && _fast > _slow * 1.65f + 0.000015f;
        // A new rise from a local trough is enough; the bass need not fall below its long-term floor.
        bool freshRise = _level > _valley * 1.30f + 0.000015f && _fast > _level * 1.10f;
        if (_sample - _lastHit < _minimumGap || _fast < 0.00004f || _fast < _wide * 0.12f || (!firstAttack && !freshRise)) return 0;
        float rise = firstAttack ? (_fast - _slow) / (_slow + 0.0001f)
            : (_level - _valley) / (_valley + 0.0001f);
        _lastHit = _sample;
        _crest = _valley = _level;
        _hasValley = false;
        AttackStrength = Math.Clamp(MathF.Sqrt(MathF.Max(0, _fast)) * 1.55f + Math.Clamp(rise, 0, 1) * 0.25f, 0.15f, 1);
        return AttackStrength;
    }
}
