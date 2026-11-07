using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lucie.Views;

public enum FaceMood { Idle, Wow, Listening, Done, Music, Sleep }

/// <summary>
/// Le petit visage de Lucie. Il « vit » tout seul : respiration, clignements au hasard,
/// coups d'œil à gauche et à droite, balancement avec la musique.
/// </summary>
public sealed partial class LucieFace : UserControl
{
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private FaceMood _mood = FaceMood.Idle;
    private Vector2? _externalGaze;
    private Vector2 _gaze;
    private Vector2 _autoTarget;
    private double _last;

    private double _nextBlink = 1.5;
    private double _blinkStart = -1;
    private bool _doubleBlink;

    private double _nextLook = 3;
    private double _lookUntil = -1;

    public LucieFace()
    {
        InitializeComponent();

        // Fondus doux entre les expressions (faits à la main : les transitions implicites
        // de WinUI sont interdites sur des éléments qui ont déjà une RenderTransform).
        _layers = new UIElement[] { Blush, Headphones, NormalEyes, RoundEyes, HappyEyes, SleepEyes, MouthSmile, MouthBig, MouthO };
        ApplyMood();
        foreach (var layer in _layers) layer.Opacity = _targets[layer]; // état de départ sans fondu
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private readonly UIElement[] _layers;
    private readonly Dictionary<UIElement, double> _targets = new();

    public FaceMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            ApplyMood();
        }
    }

    /// <summary>Direction du regard (-1..1), ou null pour la laisser regarder autour d'elle.</summary>
    public void SetGaze(Vector2? gaze) => _externalGaze = gaze;

    private void ApplyMood()
    {
        void Fade(UIElement e, bool on) => _targets[e] = on ? 1 : 0;

        Fade(NormalEyes, _mood == FaceMood.Idle);
        Fade(RoundEyes, _mood is FaceMood.Wow or FaceMood.Listening);
        Fade(HappyEyes, _mood is FaceMood.Done or FaceMood.Music);
        Fade(SleepEyes, _mood == FaceMood.Sleep);
        Fade(Headphones, _mood == FaceMood.Music);
        Fade(Blush, _mood is FaceMood.Done or FaceMood.Music or FaceMood.Wow);

        Fade(MouthSmile, _mood == FaceMood.Music); // au repos : juste les yeux, comme l'icône
        Fade(MouthBig, _mood == FaceMood.Done);
        Fade(MouthO, _mood is FaceMood.Wow or FaceMood.Listening);
        // Sommeil : pas de bouche du tout.

        // Petite bouche ronde quand elle écoute, plus grande quand elle est épatée.
        bool small = _mood == FaceMood.Listening;
        MouthO.Width = small ? 4.2 : 5.2;
        MouthO.Height = small ? 4.6 : 6;
        Canvas.SetLeft(MouthO, small ? 25.9 : 25.4);
    }

    private void OnFrame(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - _last);
        _last = now;

        UpdateFades(dt);
        UpdateGaze(now, dt);
        UpdateBlink(now);
        UpdateBody(now);
    }

    private void UpdateFades(double dt)
    {
        double step = Math.Min(1, dt * 12);
        foreach (var layer in _layers)
        {
            double target = _targets[layer];
            double current = layer.Opacity;
            if (Math.Abs(target - current) < 0.01) { if (current != target) layer.Opacity = target; continue; }
            layer.Opacity = current + (target - current) * step;
        }
    }

    private void UpdateGaze(double now, double dt)
    {
        Vector2 target;

        if (_externalGaze is Vector2 g)
        {
            target = g;
        }
        else if (_mood is FaceMood.Idle or FaceMood.Listening)
        {
            // Elle regarde un coup à gauche, un coup à droite, parfois en haut, puis revient au centre.
            if (_lookUntil < 0 && now >= _nextLook)
            {
                float side = _rng.Next(2) == 0 ? -1f : 1f;
                _autoTarget = new Vector2(side * (0.6f + (float)_rng.NextDouble() * 0.4f), (float)(_rng.NextDouble() * 0.7 - 0.45));
                _lookUntil = now + 0.7 + _rng.NextDouble() * 1.6;
            }
            else if (_lookUntil > 0 && now > _lookUntil)
            {
                _autoTarget = Vector2.Zero;
                _lookUntil = -1;
                _nextLook = now + 2.5 + _rng.NextDouble() * 5.5;
            }
            target = _autoTarget;
        }
        else
        {
            target = Vector2.Zero;
        }

        _gaze += (target - _gaze) * (float)Math.Min(1, dt * 9);

        NormalEyesShift.X = _gaze.X * 3.0;
        NormalEyesShift.Y = _gaze.Y * 1.6;
        RoundEyesShift.X = _gaze.X * 1.2;
        RoundEyesShift.Y = _gaze.Y * 0.8;
        PupilShift.X = _gaze.X * 2.8;
        PupilShift.Y = _gaze.Y * 2.4;
    }

    private void UpdateBlink(double now)
    {
        double scale = 1;
        bool canBlink = _mood is FaceMood.Idle or FaceMood.Wow or FaceMood.Listening;

        if (canBlink)
        {
            if (_blinkStart < 0 && now >= _nextBlink)
                _blinkStart = now;

            if (_blinkStart >= 0)
            {
                const double duration = 0.16;
                double t = now - _blinkStart;
                if (t < 0)
                {
                    scale = 1; // petite pause entre deux clignements
                }
                else if (t < duration)
                {
                    scale = 1 - 0.9 * Math.Sin(Math.PI * t / duration);
                }
                else if (_doubleBlink)
                {
                    _doubleBlink = false;
                    _blinkStart = now + 0.09;
                }
                else
                {
                    _blinkStart = -1;
                    double factor = _mood == FaceMood.Wow ? 1.6 : 1.0; // moins de clignements quand elle est captivée
                    _nextBlink = now + (2.0 + _rng.NextDouble() * 4.5) * factor;
                    _doubleBlink = _rng.NextDouble() < 0.2;
                }
            }
        }

        NormalEyesScale.ScaleY = scale;
        RoundEyesScale.ScaleY = scale;
    }

    private void UpdateBody(double now)
    {
        switch (_mood)
        {
            case FaceMood.Music:
                FaceRootShift.Y = Math.Sin(now * 2 * Math.PI / 1.6) * 0.9;
                FaceRootRotate.Angle = Math.Sin(now * 2 * Math.PI / 3.2) * 4;
                FaceRootScale.ScaleY = 1;
                break;
            case FaceMood.Sleep:
                FaceRootShift.Y = 0.5 + Math.Sin(now * 2 * Math.PI / 4.4) * 0.6;   // respiration lente
                FaceRootRotate.Angle = -3;                                       // tête un peu penchée
                FaceRootScale.ScaleY = 1;
                break;
            default:
                // Légère respiration, pour qu'elle ait l'air vivante même immobile
                FaceRootShift.Y = 0;
                FaceRootRotate.Angle = 0;
                FaceRootScale.ScaleY = 1 + Math.Sin(now * 2 * Math.PI / 3.8) * 0.018;
                break;
        }
    }
}
