using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lucie.Views;

public enum FaceMood { Idle, Wow, Listening, Done, Music, Sleep }

/// <summary>
/// Le petit visage de Lucie. Il « vit » tout seul : clignements au hasard,
/// coups d'œil à gauche et à droite, léger balancement avec la musique.
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
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

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
        static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

        NormalEyes.Visibility = Show(_mood == FaceMood.Idle);
        RoundEyes.Visibility = Show(_mood is FaceMood.Wow or FaceMood.Listening);
        HappyEyes.Visibility = Show(_mood is FaceMood.Done or FaceMood.Music);
        ClosedEyes.Visibility = Show(_mood == FaceMood.Sleep);
        Headphones.Visibility = Show(_mood == FaceMood.Music);

        MouthSmile.Visibility = Show(_mood is FaceMood.Idle or FaceMood.Music);
        MouthBig.Visibility = Show(_mood == FaceMood.Done);
        MouthO.Visibility = Show(_mood is FaceMood.Wow or FaceMood.Listening);
        MouthFlat.Visibility = Show(_mood == FaceMood.Sleep);

        // Bouche en « o » un peu plus petite quand elle écoute.
        MouthO.Width = _mood == FaceMood.Listening ? 4.0 : 4.8;
        MouthO.Height = _mood == FaceMood.Listening ? 4.0 : 5.6;
    }

    private void OnFrame(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - _last);
        _last = now;

        UpdateGaze(now, dt);
        UpdateBlink(now);
        UpdateBody(now);
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
            // Elle regarde un coup à gauche, un coup à droite, puis revient au centre.
            if (_lookUntil < 0 && now >= _nextLook)
            {
                float side = _rng.Next(2) == 0 ? -1f : 1f;
                _autoTarget = new Vector2(side * (0.6f + (float)_rng.NextDouble() * 0.4f), (float)(_rng.NextDouble() * 0.6 - 0.3));
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
        NormalEyesShift.Y = _gaze.Y * 1.5;
        RoundEyesShift.X = _gaze.X * 1.2;
        RoundEyesShift.Y = _gaze.Y * 0.8;
        PupilShift.X = _gaze.X * 2.6;
        PupilShift.Y = _gaze.Y * 2.2;
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
                const double duration = 0.15;
                double t = now - _blinkStart;
                if (t < 0)
                {
                    scale = 1; // petite pause entre deux clignements
                }
                else if (t < duration)
                {
                    scale = 1 - 0.88 * Math.Sin(Math.PI * t / duration);
                }
                else if (_doubleBlink)
                {
                    _doubleBlink = false;
                    _blinkStart = now + 0.08;
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
                FaceRootRotate.Angle = Math.Sin(now * 2 * Math.PI / 3.2) * 3;
                break;
            case FaceMood.Sleep:
                FaceRootShift.Y = Math.Sin(now * 2 * Math.PI / 4.2) * 0.6;   // respiration lente
                FaceRootRotate.Angle = 0;
                break;
            default:
                FaceRootShift.Y = 0;
                FaceRootRotate.Angle = 0;
                break;
        }
    }
}
