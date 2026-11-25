using System.Numerics;
using FluxTube.Services;

namespace FluxTube;

/// <summary>Page-local rhythmic motion synced to Shell.VideoBeatGlow/Phase with distance falloff and inverse pointer mapping.</summary>
public sealed class PageBeatMotion : IDisposable
{
    private readonly Matrix3x2 _transform = Gfx.DC.Transform;
    private readonly float _mx = UI.MX, _my = UI.MY, _sourceX, _sourceY, _radius;
    private readonly bool _enabled;
    private readonly float _energy, _phase;
    private bool _transformed;
    private static float _heldEnergy, _heldPhase;
    public static PageBeatMotion? Current { get; private set; }
    public static bool Active { get; private set; }

    public PageBeatMotion(R video, bool playingHere)
    {
        _sourceX = video.X + video.W / 2;
        _sourceY = video.Y + video.H / 2;
        _radius = MathF.Max(340, video.H * 0.85f);
        var player = FluxTube.Player.MpvPlayer.Instance;
        _enabled = playingHere && player.HasMedia && !player.Paused && !player.Muted && player.Volume > 0
            && !AppSettings.Current.AudioOnlyMode && AppSettings.Current.VideoBeatShake;
        float liveE = Shell.VideoBeatGlow;
        float liveP = Shell.VideoBeatPhase;
        if (UI.Pressed) { _heldEnergy = liveE; _heldPhase = liveP; }
        _energy = UI.Down || UI.Released ? _heldEnergy : liveE;
        _phase = UI.Down || UI.Released ? _heldPhase : liveP;
        Active = _enabled && _energy > 0.004f;
        Current = Active ? this : null;
    }

    public void At(float x, float y, bool move = true)
    {
        if (!Active || !move)
        {
            if (_transformed)
            {
                Gfx.DC.Transform = _transform;
                UI.MX = _mx; UI.MY = _my;
                _transformed = false;
            }
            return;
        }
        float distance = MathF.Sqrt((x - _sourceX) * (x - _sourceX) + (y - _sourceY) * (y - _sourceY));
        float attenuation = 1f / ((1f + distance / _radius) * (1f + distance / _radius));
        float energy = _energy * attenuation;
        if (energy < 0.003f)
        {
            if (_transformed)
            {
                Gfx.DC.Transform = _transform;
                UI.MX = _mx; UI.MY = _my;
                _transformed = false;
            }
            return;
        }
        float dx = Math.Clamp(MathF.Sin(_phase * 0.94f) * energy * 5.2f, -6.5f, 6.5f);
        float dy = Math.Clamp((-0.52f + 0.48f * MathF.Cos(_phase * 1.15f)) * energy * 7.8f, -8.5f, 5.5f);
        Gfx.DC.Transform = Matrix3x2.CreateTranslation(dx, dy) * _transform;
        UI.MX = _mx - dx; UI.MY = _my - dy;
        _transformed = true;
    }

    public void Dispose()
    {
        if (_transformed) Gfx.DC.Transform = _transform;
        UI.MX = _mx; UI.MY = _my;
        Active = false; Current = null;
    }
}
