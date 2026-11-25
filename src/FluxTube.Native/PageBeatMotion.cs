using System.Numerics;
using FluxTube.Services;

namespace FluxTube;

/// <summary>Page-local motion with distance falloff and inverse pointer mapping.</summary>
public sealed class PageBeatMotion : IDisposable
{
    private readonly Matrix3x2 _transform = Gfx.DC.Transform;
    private readonly float _mx = UI.MX, _my = UI.MY, _sourceX, _sourceY, _radius;
    private readonly bool _enabled;
    private readonly float _energy, _phase;
    private static float _heldEnergy, _heldPhase;
    public static PageBeatMotion? Current { get; private set; }
    public static bool Active { get; private set; }
    public PageBeatMotion(R video, bool playingHere)
    {
        _sourceX = video.X + video.W / 2;
        _sourceY = video.Y + video.H / 2;
        _radius = MathF.Max(320, video.H * 0.8f);
        var player = FluxTube.Player.MpvPlayer.Instance;
        _enabled = playingHere && player.HasMedia && !player.Paused && !player.Muted && player.Volume > 0 && (AppSettings.Current.AudioOnlyMode
            ? AppSettings.Current.MusicBassShake : AppSettings.Current.VideoBeatShake);
        if (UI.Pressed) { _heldEnergy = AudioAnalyzer.BeatMotionStrength; _heldPhase = AudioAnalyzer.BeatPhase; }
        _energy = UI.Down || UI.Released ? _heldEnergy : AudioAnalyzer.BeatMotionStrength;
        _phase = UI.Down || UI.Released ? _heldPhase : AudioAnalyzer.BeatPhase;
        Active = true; Current = this;
    }
    public void At(float x, float y, bool move = true)
    {
        float distance = MathF.Sqrt((x - _sourceX) * (x - _sourceX) + (y - _sourceY) * (y - _sourceY));
        float attenuation = 1 / MathF.Pow(1 + distance / _radius, 2);
        float energy = _enabled && move ? _energy * attenuation : 0;
        float drive = AppSettings.Current.AudioOnlyMode && AppSettings.Current.MusicImpactEffects ? 1.35f : 1;
        float dx = MathF.Sin(_phase) * energy * 23 * drive;
        float dy = MathF.Cos(_phase * 1.3f) * energy * 16 * drive;
        Gfx.DC.Transform = Matrix3x2.CreateTranslation(dx, dy) * _transform;
        UI.MX = _mx - dx; UI.MY = _my - dy;
    }
    public void Dispose()
    {
        Gfx.DC.Transform = _transform;
        UI.MX = _mx; UI.MY = _my;
        Active = false; Current = null;
    }
}
