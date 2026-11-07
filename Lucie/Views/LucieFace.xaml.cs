using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lucie.Views;

public enum FaceMood { Idle, Wow, Listening, Done, Music, Sleep }

/// <summary>
/// Le petit visage de Lucie.
/// Tout bouge avec des « ressorts » : la tête se décale, penche et revient avec un léger rebond,
/// les yeux s'étirent quand ils bougent vite, clignent au hasard, et se ferment de plaisir (^ ^)
/// quand on la caresse avec la souris.
/// </summary>
public sealed partial class LucieFace : UserControl
{
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly UIElement[] _layers;
    private readonly Dictionary<UIElement, double> _targets = new();

    private FaceMood _mood = FaceMood.Idle;
    private Vector2? _externalGaze;
    private double _last;

    // Ressorts : regard, inclinaison de la tête, taille des yeux
    private Vector2 _gaze, _gazeVel, _gazeTarget;
    private double _tilt, _tiltVel, _tiltTarget;
    private double _eyeScale = 1, _eyeScaleVel;

    // Petits comportements spontanés
    private double _nextAction = 1.2;
    private double _actionUntil = -1;
    private Vector2 _actionGaze;
    private double _actionTilt;

    // Clignements
    private double _nextBlink = 1.5;
    private double _blinkStart = -1;
    private bool _doubleBlink;

    // En musique : de temps en temps elle ferme les yeux pour savourer
    private double _nextSquint = 4;
    private double _squintUntil = -1;

    public LucieFace()
    {
        InitializeComponent();
        _layers = new UIElement[] { Headphones, NormalEyes, HappyEyes, SleepEyes };
        UpdateLayerTargets(0);
        foreach (var layer in _layers) layer.Opacity = _targets[layer];

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
            if (value == FaceMood.Wow) _eyeScaleVel += 4;   // petit « pop » de surprise
        }
    }

    /// <summary>Caressée par la souris : elle ferme les yeux de plaisir.</summary>
    public bool Happy { get; set; }

    /// <summary>Direction du regard (-1..1), ou null pour la laisser vivre sa vie.</summary>
    public void SetGaze(Vector2? gaze) => _externalGaze = gaze;

    /// <summary>Fait cligner des yeux tout de suite (petite réaction).</summary>
    public void Blink()
    {
        if (_blinkStart < 0) _blinkStart = _clock.Elapsed.TotalSeconds;
    }

    // ───────────────────────── Boucle d'animation ─────────────────────────

    private void OnFrame(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _last, 0, 0.1);
        _last = now;

        UpdateBehavior(now);
        UpdateSprings(dt);
        double blink = UpdateBlink(now);
        UpdateLayerTargets(now);
        Apply(now, blink);
        UpdateFades(dt);
    }

    private void UpdateBehavior(double now)
    {
        _eyeScaleTarget = _mood switch
        {
            FaceMood.Wow => 1.16,
            FaceMood.Listening => 1.1,
            _ => 1.0
        };

        if (_externalGaze is Vector2 g)
        {
            // Elle suit la souris : la tête accompagne un peu le regard.
            _gazeTarget = g;
            _tiltTarget = -g.X * 3;
            return;
        }

        switch (_mood)
        {
            case FaceMood.Sleep:
                _gazeTarget = new Vector2(0, 0.35f);
                _tiltTarget = -6;
                return;

            case FaceMood.Done:
                _gazeTarget = new Vector2(0, -0.35f);
                _tiltTarget = 0;
                return;

            case FaceMood.Music:
                // Elle se balance en rythme
                _gazeTarget = new Vector2((float)Math.Sin(now * Math.PI / 1.6) * 0.3f, 0);
                _tiltTarget = Math.Sin(now * 2 * Math.PI / 3.2) * 7;
                if (_squintUntil < 0 && now >= _nextSquint)
                {
                    _squintUntil = now + 1.2 + _rng.NextDouble() * 1.5;
                }
                else if (_squintUntil > 0 && now > _squintUntil)
                {
                    _squintUntil = -1;
                    _nextSquint = now + 4 + _rng.NextDouble() * 6;
                }
                return;
        }

        // Idle, Wow, Listening : petits comportements spontanés
        double amplitude = _mood == FaceMood.Listening ? 0.45 : 1.0;

        if (_actionUntil < 0 && now >= _nextAction)
        {
            double r = _rng.NextDouble();
            double hold;
            float side = _rng.Next(2) == 0 ? -1f : 1f;

            if (r < 0.40)
            {
                // Regarde quelque part
                _actionGaze = new Vector2(side * (0.55f + (float)_rng.NextDouble() * 0.45f), (float)(_rng.NextDouble() * 0.9 - 0.5));
                _actionTilt = -side * (1 + _rng.NextDouble() * 3);
                hold = 0.8 + _rng.NextDouble() * 1.6;
            }
            else if (r < 0.60)
            {
                // Penche la tête, curieuse
                _actionGaze = new Vector2(side * 0.15f, -0.1f);
                _actionTilt = side * (6 + _rng.NextDouble() * 4);
                hold = 1.0 + _rng.NextDouble() * 1.2;
            }
            else if (r < 0.78)
            {
                // Coup d'œil rapide
                _actionGaze = new Vector2(side * 0.9f, (float)(_rng.NextDouble() * 0.4 - 0.2));
                _actionTilt = 0;
                hold = 0.3 + _rng.NextDouble() * 0.25;
            }
            else if (r < 0.90)
            {
                // Regarde en l'air
                _actionGaze = new Vector2((float)(_rng.NextDouble() * 0.6 - 0.3), -0.85f);
                _actionTilt = side * 2;
                hold = 0.9 + _rng.NextDouble() * 0.8;
            }
            else
            {
                // Double clignement sur place
                _actionGaze = Vector2.Zero;
                _actionTilt = 0;
                hold = 0.6;
                _doubleBlink = true;
                Blink();
            }

            _actionUntil = now + hold;
        }
        else if (_actionUntil > 0 && now > _actionUntil)
        {
            _actionUntil = -1;
            _nextAction = now + 1.4 + _rng.NextDouble() * 3.6;
        }

        var target = _actionUntil > 0 ? _actionGaze : Vector2.Zero;
        // Légère dérive, pour ne jamais être parfaitement figée
        target += new Vector2((float)Math.Sin(now * 0.7) * 0.06f, (float)Math.Sin(now * 0.93 + 1) * 0.05f);
        _gazeTarget = target * (float)amplitude;
        _tiltTarget = (_actionUntil > 0 ? _actionTilt : 0) * amplitude;
    }

    private double _eyeScaleTarget = 1;

    private void UpdateSprings(double dt)
    {
        bool following = _externalGaze is not null;
        double k = following ? 170 : 110;   // raideur
        double c = following ? 17 : 12.5;   // amortissement (un peu faible = léger rebond)

        int steps = (int)Math.Ceiling(dt / 0.008);
        double h = steps > 0 ? dt / steps : 0;
        for (int i = 0; i < steps; i++)
        {
            var accel = (_gazeTarget - _gaze) * (float)k - _gazeVel * (float)c;
            _gazeVel += accel * (float)h;
            _gaze += _gazeVel * (float)h;

            double tiltAccel = (_tiltTarget - _tilt) * 60 - _tiltVel * 9;
            _tiltVel += tiltAccel * h;
            _tilt += _tiltVel * h;

            double scaleAccel = (_eyeScaleTarget - _eyeScale) * 220 - _eyeScaleVel * 14;
            _eyeScaleVel += scaleAccel * h;
            _eyeScale += _eyeScaleVel * h;
        }
    }

    /// <summary>Renvoie l'ouverture des yeux (1 = ouverts, ~0.1 = fermés).</summary>
    private double UpdateBlink(double now)
    {
        bool canBlink = _mood is FaceMood.Idle or FaceMood.Wow or FaceMood.Listening or FaceMood.Music;
        if (!canBlink)
        {
            _blinkStart = -1;
            return 1;
        }

        if (_blinkStart < 0 && now >= _nextBlink) _blinkStart = now;
        if (_blinkStart < 0) return 1;

        const double duration = 0.14;
        double t = now - _blinkStart;
        if (t < 0) return 1;                     // petite pause entre deux clignements
        if (t < duration) return 1 - 0.9 * Math.Sin(Math.PI * t / duration);

        if (_doubleBlink)
        {
            _doubleBlink = false;
            _blinkStart = now + 0.08;
        }
        else
        {
            _blinkStart = -1;
            double factor = _mood == FaceMood.Wow ? 1.5 : 1.0;   // moins de clignements quand elle est captivée
            _nextBlink = now + (1.8 + _rng.NextDouble() * 4.2) * factor;
            _doubleBlink = _rng.NextDouble() < 0.22;
        }
        return 1;
    }

    private void UpdateLayerTargets(double now)
    {
        bool squint = _squintUntil > 0 && now < _squintUntil;
        bool happy = _mood == FaceMood.Done
                     || ((Happy || squint) && _mood is FaceMood.Idle or FaceMood.Wow or FaceMood.Music);

        _targets[Headphones] = _mood == FaceMood.Music ? 1 : 0;
        _targets[SleepEyes] = _mood == FaceMood.Sleep ? 1 : 0;
        _targets[HappyEyes] = happy && _mood != FaceMood.Sleep ? 1 : 0;
        _targets[NormalEyes] = !happy && _mood != FaceMood.Sleep ? 1 : 0;
    }

    private void Apply(double now, double blink)
    {
        // La tête suit le regard, les yeux bougent un peu plus (effet de profondeur).
        double bob = 0, breath = 1;
        switch (_mood)
        {
            case FaceMood.Music:
                bob = -Math.Abs(Math.Sin(now * Math.PI / 0.85)) * 1.1;          // hoche la tête en rythme
                break;
            case FaceMood.Sleep:
                bob = 0.4 + Math.Sin(now * 2 * Math.PI / 4.4) * 0.7;            // respiration lente
                break;
            default:
                breath = 1 + Math.Sin(now * 2 * Math.PI / 3.8) * 0.016;         // respiration légère
                break;
        }

        HeadShift.X = _gaze.X * 4.2;
        HeadShift.Y = _gaze.Y * 2.6 + bob;
        HeadRotate.Angle = _tilt;
        HeadScale.ScaleY = breath;

        EyesShift.X = _gaze.X * 1.9;
        EyesShift.Y = _gaze.Y * 1.5;
        HappyShift.X = _gaze.X * 1.4;
        HappyShift.Y = _gaze.Y * 1.0;

        // Les yeux s'étirent dans le sens du mouvement quand ils bougent vite.
        double sx = 1 + Math.Min(0.22, Math.Abs(_gazeVel.X) * 0.02);
        double sy = 1 + Math.Min(0.18, Math.Abs(_gazeVel.Y) * 0.02);
        // Regarder loin sur le côté les rend un peu plus petits (perspective).
        double far = 1 - Math.Min(0.12, Math.Abs(_gaze.X) * 0.1);

        EyesScale.ScaleX = _eyeScale * sx * far / Math.Sqrt(sy);
        EyesScale.ScaleY = _eyeScale * sy * far / Math.Sqrt(sx) * blink;
    }

    private void UpdateFades(double dt)
    {
        double step = Math.Min(1, dt * 14);
        foreach (var layer in _layers)
        {
            double target = _targets[layer];
            double current = layer.Opacity;
            if (Math.Abs(target - current) < 0.01)
            {
                if (current != target) layer.Opacity = target;
                continue;
            }
            layer.Opacity = current + (target - current) * step;
        }
    }
}
