using System.Diagnostics;
using System.Numerics;
using Lucie.Models;
using Lucie.Native;
using Lucie.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Lucie.Views;

/// <summary>
/// Le widget flottant : une gélule noire toujours au premier plan.
/// Au survol, elle s'agrandit en carré arrondi et montre 3 petits boutons.
/// Toutes les tailles et formes sont animées image par image pour rester fluides.
/// </summary>
public sealed partial class WidgetWindow : Window
{
    private const double ExpandedWidth = 520;
    private const double ExpandedHeight = 112;
    /// <summary>Petite marge transparente autour de la gélule : le bord arrondi reste lisse.</summary>
    private const double Edge = 3;
    private static readonly TimeSpan ListenWatchdog = TimeSpan.FromSeconds(15);

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly HotkeyService _hotkeys;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Win32.SubclassProc _frameProc;   // gardé en mémoire tant que la fenêtre vit
    private TrayIcon? _tray;
    private bool _hidden;                              // widget masqué depuis la zone de notification

    private IRandomAccessStreamReference? _shownThumbnail;

    // État
    private bool _listening;
    private bool _expanded;          // mode lecteur
    private bool _collapsing;
    private bool _near;
    private bool _sleeping;
    private bool _pointerInside;
    private bool _hover;             // menu du survol ouvert
    private bool _flyoutOpen;
    private bool _menuOpen;
    private DateTime _doneUntil;
    private DateTime _listenStarted;
    private DateTime _collapseAt = DateTime.MaxValue;
    private FaceMood _appliedMood = FaceMood.Idle;

    // Survol / caresse
    private double _insideSince = -1, _outsideSince = -1;
    private Win32.POINT _lastCursor;
    private double _stillSince;

    // Forme animée (en DIP)
    private double _curW, _curH, _curR, _hoverP;
    private double _winW, _winH;      // taille du contenu pour laquelle la fenêtre est dimensionnée
    private double _lastFrame;

    // Déplacement en restant appuyé
    private bool _pressed;
    private bool _dragging;
    private Win32.POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;

    public WidgetWindow()
    {
        InitializeComponent();
        Title = "";   // aucun titre : rien à dessiner

        _hwnd = WindowNative.GetWindowHandle(this);
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Toute la fenêtre est « contenu » : Windows ne réserve aucun pixel pour un cadre.
        _frameProc = FrameProc;
        Win32.SetWindowSubclass(_hwnd, _frameProc, (UIntPtr)2, IntPtr.Zero);

        // Icône dans la zone de notification (près de l'horloge)
        _tray = new TrayIcon(_hwnd, App.Settings.Current.AssistantName,
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "LucieIcon.ico"));
        _tray.Clicked += () => _dispatcher.TryEnqueue(() => SetHidden(!_hidden));
        _tray.DoubleClicked += () => _dispatcher.TryEnqueue(() => App.Current.OpenSettings());
        _tray.MenuRequested += ShowTrayMenu;

        SetupChrome();
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop { TintColor = Colors.Transparent };
        PillHost.Margin = new Thickness(Edge);
        Player.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        PillHost.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(500) };

        // L'éclat « c'est fait » ne doit pas bloquer le contour une fois terminé.
        var doneStory = (Storyboard)Root.Resources["DoneStory"];
        doneStory.Completed += (_, _) => doneStory.Stop();

        _hotkeys = new HotkeyService(_hwnd);
        _hotkeys.Pressed += () => _dispatcher.TryEnqueue(ToggleListen);

        // Forme de départ
        var (w, h, r) = TargetShape();
        _curW = w; _curH = h; _curR = r;
        ApplySettings();

        // Souris : rester appuyé + bouger = déplacer ; simple clic = parler à Lucie ; clic droit = menu.
        PillHost.PointerPressed += OnPointerPressed;
        _dragTimer.Tick += OnDragTick;
        PillHost.RightTapped += (_, e) => { ShowMenu(); e.Handled = true; };

        TypeFlyout.Opened += (_, _) => _flyoutOpen = true;
        TypeFlyout.Closed += (_, _) => _flyoutOpen = false;

        // Voix
        App.Speech.WakeDetected += () => _dispatcher.TryEnqueue(() =>
        {
            App.Sounds.PlayWake();
            SetListening(true);
        });
        App.Speech.CommandHeard += text => _dispatcher.TryEnqueue(async () => await HandleCommandAsync(text));
        App.Speech.ListenTimedOut += () => _dispatcher.TryEnqueue(() => SetListening(false));
        App.Speech.Error += message => _dispatcher.TryEnqueue(() =>
        {
            ToolTipService.SetToolTip(PillHost, message + " — clic droit › Paramètres");
            if (_listening) SetListening(false);
        });
        App.Speech.Ready += () => _dispatcher.TryEnqueue(() => ToolTipService.SetToolTip(PillHost, null));

        // Musique
        App.Media.Changed += () => _dispatcher.TryEnqueue(UpdatePlayer);
        App.Media.TrackChanged += () => _dispatcher.TryEnqueue(() =>
        {
            if (App.Settings.Current.AutoPlayer) Expand();
        });

        // Paramètres
        App.Settings.Changed += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            ApplySettings();
            _tray?.SetTooltip(App.Settings.Current.AssistantName);
        });

        // Quand l'affichage est prêt (ou change d'écran / de mise à l'échelle), on recale tout.
        Root.Loaded += (_, _) =>
        {
            if (Root.XamlRoot is not null)
                Root.XamlRoot.Changed += (_, _) => _dispatcher.TryEnqueue(ApplySettings);
            ApplySettings();
        };

        _tick.Tick += OnTick;
        _tick.Start();
        CompositionTarget.Rendering += OnRender;

        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRender;
            _tick.Stop();
            _dragTimer.Stop();
            _hotkeys.Dispose();
            _tray?.Dispose();
            _tray = null;
        };
    }

    // ───────────────────────── Fenêtre ─────────────────────────

    private void SetupChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.IsShownInSwitchers = false;   // pas dans Alt+Tab ni la barre des tâches
        Win32.StripFrame(_hwnd);
        Win32.MakeFrameless(_hwnd);
    }

    private IntPtr FrameProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        // Zone cliente = fenêtre entière (aucune bordure, même invisible).
        if (msg == Win32.WM_NCCALCSIZE && wParam != IntPtr.Zero) return IntPtr.Zero;
        if (_tray?.HandleMessage(msg, lParam) == true) return IntPtr.Zero;
        // Windows ne doit jamais dessiner de cadre ni de titre par-dessus le widget
        // (sinon « Lucie » et une barre blanche apparaissent quand elle n'est pas active).
        if (msg == 0x0085 /* WM_NCPAINT */) return IntPtr.Zero;
        if (msg == 0x0086 /* WM_NCACTIVATE */) return new IntPtr(1);
        if (msg == 0x00AE /* WM_NCUAHDRAWCAPTION */ || msg == 0x00AF /* WM_NCUAHDRAWFRAME */) return IntPtr.Zero;
        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Échelle réellement utilisée pour dessiner le contenu (125 %, 150 %…),
    /// pour que fenêtre, découpe et dessin aient exactement la même taille.
    /// </summary>
    private double Scale
    {
        get
        {
            double s = Root.XamlRoot?.RasterizationScale ?? 0;
            return s > 0 ? s : Win32.ScaleFor(_hwnd);
        }
    }

    private double SizeFactor => App.Settings.Current.Size switch
    {
        WidgetSize.Small => 0.8,
        WidgetSize.Large => 1.25,
        _ => 1.0
    };

    /// <summary>Taille et arrondi que le widget doit atteindre selon son état (DIP).</summary>
    private (double W, double H, double R) TargetShape()
    {
        double k = SizeFactor;
        if (_expanded) return (ExpandedWidth, ExpandedHeight, ExpandedHeight / 2);

        double w = _listening ? 132 : _appliedMood == FaceMood.Sleep ? 124 : 104;
        double compactH = 64;
        if (_hover)
        {
            w = Math.Max(w, 136);
            return (w * k, (compactH + 44) * k, 28 * k);
        }
        return (w * k, compactH * k, compactH * k / 2);
    }

    private void ApplySettings()
    {
        var s = App.Settings.Current;

        // Gélule noire légèrement bombée, comme l'icône. La transparence laisse voir le bureau.
        double t = Math.Clamp(s.WidgetTransparency / 100.0, 0, 0.9);
        var body = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), Opacity = 1 - t * 0.6 };
        body.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x26, 0x27, 0x2B), Offset = 0 });
        body.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x0B, 0x0B, 0x0D), Offset = 0.38 });
        body.GradientStops.Add(new GradientStop { Color = Colors.Black, Offset = 1 });
        Pill.Background = body;

        // De quel côté le widget grandit (lecteur, menu du survol).
        if (s.Position == WidgetPosition.Custom)
        {
            PillHost.HorizontalAlignment = s.AnchorX < 0 ? HorizontalAlignment.Left : s.AnchorX > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            PillHost.VerticalAlignment = s.AnchorY > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        }
        else
        {
            int col = (int)s.Position % 3, row = (int)s.Position / 3;
            PillHost.HorizontalAlignment = col switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right };
            PillHost.VerticalAlignment = row == 2 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        }

        ApplyLayout();
        if (!_dragging) PlaceWindow(Math.Max(_curW, TargetShape().W), Math.Max(_curH, TargetShape().H));

        _hotkeys.Register(s.TalkHotkey);
        UpdateMicButton();
    }

    private void ApplyLayout()
    {
        double k = SizeFactor;
        if (_expanded)
        {
            TopRow.Height = new GridLength(ExpandedHeight);
            Face.Width = 64; Face.Height = 46;
            Grid.SetColumnSpan(LeftStack, 1);
            LeftStack.HorizontalAlignment = HorizontalAlignment.Left;
            PillContent.Padding = new Thickness(22, 0, 18, 0);
        }
        else
        {
            TopRow.Height = new GridLength(64 * k);
            Face.Width = 56 * k; Face.Height = 40 * k;
            Grid.SetColumnSpan(LeftStack, 2);
            LeftStack.HorizontalAlignment = HorizontalAlignment.Center;
            PillContent.Padding = new Thickness(16 * k, 0, 16 * k, 0);
        }
        Bars.Visibility = _listening && !_expanded ? Visibility.Visible : Visibility.Collapsed;
        Zs.Visibility = _appliedMood == FaceMood.Sleep && !_expanded && !_listening ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Place et dimensionne la fenêtre pour un contenu de cette taille (DIP).</summary>
    private void PlaceWindow(double widthDip, double heightDip)
    {
        var s = App.Settings.Current;
        double scale = Scale;
        int w = (int)Math.Ceiling((widthDip + 2 * Edge) * scale);
        int h = (int)Math.Ceiling((heightDip + 2 * Edge) * scale);
        const int edge = 16; // petit espace avec le bord de l'écran

        int x, y;
        RectInt32 area;

        if (s.Position == WidgetPosition.Custom && s.CustomX is double cx && s.CustomY is double cy)
        {
            area = DisplayArea.GetFromPoint(new PointInt32((int)cx, (int)cy), DisplayAreaFallback.Nearest).WorkArea;

            // (cx, cy) = centre du widget « au repos ». Le bord collé à l'écran ne bouge pas quand il grandit.
            var (bw, bh) = BaseSizePx(scale);
            x = s.AnchorX < 0 ? (int)(cx - bw / 2) : s.AnchorX > 0 ? (int)(cx + bw / 2 - w) : (int)(cx - w / 2.0);
            y = s.AnchorY > 0 ? (int)(cy + bh / 2 - h) : (int)(cy - bh / 2);
        }
        else
        {
            area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var (bw, bh) = BaseSizePx(scale);
            int col = (int)s.Position % 3, row = (int)s.Position / 3;
            x = col switch { 0 => area.X + edge, 1 => area.X + (area.Width - w) / 2, _ => area.X + area.Width - w - edge };
            y = row switch
            {
                0 => area.Y + edge,
                1 => area.Y + (area.Height - (int)bh) / 2,
                _ => area.Y + area.Height - h - edge
            };
        }

        x = Math.Max(area.X, Math.Min(x, area.X + area.Width - w));
        y = Math.Max(area.Y, Math.Min(y, area.Y + area.Height - h));
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        _winW = widthDip;
        _winH = heightDip;
        UpdateRegion();
    }

    /// <summary>Taille de la fenêtre quand le widget est au repos (pixels physiques).</summary>
    private (double W, double H) BaseSizePx(double scale)
    {
        double k = SizeFactor;
        return ((104 * k + 2 * Edge) * scale, (64 * k + 2 * Edge) * scale);
    }

    /// <summary>Découpe la fenêtre à la forme du widget (avec sa petite marge transparente).</summary>
    private void UpdateRegion()
    {
        double w = _curW, h = _curH;
        if (w <= 0 || h <= 0) return;

        double scale = Scale;
        var client = AppWindow.ClientSize;   // taille réelle de la fenêtre, en pixels
        double winW = client.Width > 0 ? client.Width / scale : _winW + 2 * Edge;
        double winH = client.Height > 0 ? client.Height / scale : _winH + 2 * Edge;

        // Position de la gélule dans la fenêtre, selon l'alignement
        double x = PillHost.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => Edge,
            HorizontalAlignment.Right => winW - Edge - w,
            _ => (winW - w) / 2
        };
        double y = PillHost.VerticalAlignment == VerticalAlignment.Bottom ? winH - Edge - h : Edge;

        Win32.SetRoundRegion(_hwnd,
            (int)Math.Floor((x - Edge) * scale), (int)Math.Floor((y - Edge) * scale),
            (int)Math.Ceiling((w + 2 * Edge) * scale), (int)Math.Ceiling((h + 2 * Edge) * scale),
            (int)Math.Round((_curR + Edge) * 2 * scale));
    }

    // ───────────────────────── Animation de la forme ─────────────────────────

    private void OnRender(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;

        var (tw, th, tr) = TargetShape();

        // La fenêtre doit toujours pouvoir contenir le widget pendant qu'il grandit.
        if (!_dragging && (Math.Max(tw, _curW) > _winW + 0.5 || Math.Max(th, _curH) > _winH + 0.5))
            PlaceWindow(Math.Max(tw, _curW), Math.Max(th, _curH));

        double a = 1 - Math.Exp(-dt * 13);
        bool changed = false;
        changed |= Approach(ref _curW, tw, a);
        changed |= Approach(ref _curH, th, a);
        changed |= Approach(ref _curR, tr, a);

        if (changed || double.IsNaN(PillHost.Width))
        {
            PillHost.Width = _curW;
            PillHost.Height = _curH;
            Pill.CornerRadius = new CornerRadius(_curR);
            Ring.CornerRadius = Pill.CornerRadius;
            UpdateRegion();
        }

        // Une fois l'animation finie, la fenêtre reprend la taille juste nécessaire.
        if (!changed && !_dragging && (_winW > tw + 0.5 || _winH > th + 0.5))
            PlaceWindow(tw, th);

        // Boutons du survol : apparaissent en glissant doucement
        double hoverTarget = _hover ? 1 : 0;
        _hoverP += (hoverTarget - _hoverP) * (1 - Math.Exp(-dt * (_hover ? 10 : 16)));
        if (Math.Abs(_hoverP - hoverTarget) < 0.005) _hoverP = hoverTarget;
        double p = _hoverP * _hoverP * (3 - 2 * _hoverP);
        Toolbar.Opacity = Math.Clamp((p - 0.25) / 0.75, 0, 1);
        ToolbarShift.Y = (1 - p) * -8;
        Toolbar.IsHitTestVisible = _hover && p > 0.5;
    }

    private static bool Approach(ref double value, double target, double a)
    {
        if (Math.Abs(target - value) < 0.15)
        {
            if (value == target) return false;
            value = target;
            return true;
        }
        value += (target - value) * a;
        return true;
    }

    // ───────────────────────── Lecteur ─────────────────────────

    private void Expand()
    {
        if (!App.Media.HasTrack) return;
        _collapseAt = DateTime.Now.AddSeconds(6);
        if (_collapsing)
        {
            _collapsing = false;
            Player.Opacity = 1;
            return;
        }
        if (_expanded) return;

        _expanded = true;
        _hover = false;
        UpdatePlayer();
        ApplyLayout();
        Player.Visibility = Visibility.Visible;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(220);
            if (_expanded) Player.Opacity = 1;
        });
    }

    private void Collapse()
    {
        if (!_expanded || _collapsing) return;
        _collapsing = true;
        Player.Opacity = 0;

        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(180);
            if (!_collapsing) return;   // ré-ouvert entre-temps
            _collapsing = false;
            _expanded = false;
            Player.Visibility = Visibility.Collapsed;
            ApplyLayout();
        });
    }

    private async void UpdatePlayer()
    {
        var media = App.Media;
        TrackTitle.Text = media.Title;
        TrackArtist.Text = media.Artist;
        PlayIcon.Glyph = media.IsPlaying ? "" : "";
        TrackProgress.Value = media.Progress;

        var thumbnail = media.Thumbnail;
        if (thumbnail == _shownThumbnail) return;
        _shownThumbnail = thumbnail;

        if (thumbnail is null)
        {
            AlbumArt.ImageSource = null;
            return;
        }
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (_shownThumbnail == thumbnail) AlbumArt.ImageSource = bitmap;
        }
        catch
        {
            AlbumArt.ImageSource = null;
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await App.Media.PreviousAsync();
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await App.Media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await App.Media.NextAsync();

    // ───────────────────────── Boutons du survol ─────────────────────────

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        App.Speech.Muted = !App.Speech.Muted;
        if (App.Speech.Muted && _listening)
        {
            App.Speech.Cancel();
            SetListening(false);
        }
        UpdateMicButton();
        Face.Blink();
    }

    private void UpdateMicButton()
    {
        bool muted = App.Speech?.Muted ?? false;
        MicSlash.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(MicButton, muted ? "Réactiver le micro" : "Couper le micro");
    }

    private void Type_Click(object sender, RoutedEventArgs e)
    {
        Activate();   // pour pouvoir taper au clavier
        CommandBox.Text = "";
        FlyoutBase.ShowAttachedFlyout(TypeButton);
        CommandBox.Focus(FocusState.Programmatic);
    }

    private async void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            TypeFlyout.Hide();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var text = CommandBox.Text.Trim();
            TypeFlyout.Hide();
            e.Handled = true;
            if (text.Length > 0) await HandleCommandAsync(text);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.Current.OpenSettings();

    // ───────────────────────── Voix ─────────────────────────

    private void ToggleListen()
    {
        if (_listening)
        {
            App.Speech.Cancel();
            SetListening(false);
        }
        else
        {
            App.Speech.ListenNow();
        }
    }

    private void SetListening(bool on)
    {
        if (_listening == on) return;
        _listening = on;
        if (on)
        {
            _listenStarted = DateTime.Now;
            _hover = false;
        }

        var story = (Storyboard)Root.Resources["ListenStory"];
        if (on) story.Begin();
        else
        {
            story.Stop();
            Ring.Opacity = 0;
        }
        ApplyLayout();
    }

    private async Task HandleCommandAsync(string heard)
    {
        SetListening(false);

        var intent = App.Interpreter.Interpret(heard, App.Settings.Current);
        if (intent is null)
        {
            App.Sounds.PlayError();
            return;
        }

        bool ok;
        if (intent.App is null && intent.ShortcutApp is null && intent.Command?.Action == CommandAction.Screenshot)
        {
            // On se cache le temps de la capture pour ne pas apparaître dessus.
            AppWindow.Hide();
            await Task.Delay(180);
            ok = await App.Actions.RunAsync(intent);
            if (!_hidden) AppWindow.Show(false);
        }
        else
        {
            ok = await App.Actions.RunAsync(intent);
        }

        if (ok)
        {
            App.Sounds.PlayDone();
            _doneUntil = DateTime.Now.AddSeconds(1.6);
            ((Storyboard)Root.Resources["DoneStory"]).Begin();
        }
        else
        {
            App.Sounds.PlayError();
        }
    }

    // ───────────────────────── Vie du widget ─────────────────────────

    private void OnTick(object? sender, object e)
    {
        var s = App.Settings.Current;
        double now = _clock.Elapsed.TotalSeconds;
        bool petting = false;

        if (!_dragging && Win32.GetCursorPos(out var cursor) && PillHost.ActualWidth > 0)
        {
            double scale = Scale;
            var pos = AppWindow.Position;

            // Rectangle du widget à l'écran
            var bounds = PillHost.TransformToVisual(Root).TransformBounds(new Rect(0, 0, PillHost.ActualWidth, PillHost.ActualHeight));
            double left = pos.X + bounds.X * scale, top = pos.Y + bounds.Y * scale;
            double right = left + bounds.Width * scale, bottom = top + bounds.Height * scale;

            double dx = Math.Max(Math.Max(left - cursor.X, 0), cursor.X - right);
            double dy = Math.Max(Math.Max(top - cursor.Y, 0), cursor.Y - bottom);
            double distance = Math.Sqrt(dx * dx + dy * dy);

            _pointerInside = distance <= 1;
            _near = distance <= s.ProximityPx * scale;

            // Souris immobile ?
            if (Math.Abs(cursor.X - _lastCursor.X) + Math.Abs(cursor.Y - _lastCursor.Y) > 3)
            {
                _stillSince = now;
                _lastCursor = cursor;
            }

            if (_near || _pointerInside)
            {
                // Elle te suit des yeux
                var center = Face.TransformToVisual(Root).TransformPoint(new Point(Face.ActualWidth / 2, Face.ActualHeight / 2));
                double fx = pos.X + center.X * scale, fy = pos.Y + center.Y * scale;
                double vx = cursor.X - fx, vy = cursor.Y - fy;
                double len = Math.Sqrt(vx * vx + vy * vy);
                float strength = (float)Math.Min(1, len / (70 * scale));
                Face.SetGaze(len < 1 ? Vector2.Zero : new Vector2((float)(vx / len), (float)(vy / len)) * strength);

                // Caresse : la souris posée sur son visage
                double faceRadius = Math.Max(Face.ActualWidth, Face.ActualHeight) * 0.65 * scale;
                petting = _pointerInside && len < faceRadius && now - _stillSince > 0.35;
            }
            else
            {
                Face.SetGaze(null);
            }

            // Menu du survol : s'ouvre après un court instant, se referme quand la souris s'éloigne
            if (_pointerInside) { if (_insideSince < 0) _insideSince = now; _outsideSince = -1; }
            else { if (_outsideSince < 0) _outsideSince = now; _insideSince = -1; }

            bool wantHover = !_expanded && !_listening && !_pressed &&
                             ((_insideSince >= 0 && now - _insideSince > 0.12) ||
                              (_hover && _outsideSince >= 0 && now - _outsideSince < 0.45) ||
                              _flyoutOpen || _menuOpen);
            if (wantHover != _hover)
            {
                _hover = wantHover;
                if (_hover) Face.Blink();
            }
        }

        Face.Happy = petting;
        _sleeping = Win32.GetIdleTime().TotalMinutes >= Math.Max(1, s.SleepMinutes);

        // Sécurité : ne jamais rester bloquée en mode écoute.
        if (_listening && DateTime.Now - _listenStarted > ListenWatchdog)
        {
            App.Speech.Cancel();
            SetListening(false);
        }

        var mood = ResolveMood();
        if (mood != _appliedMood) ApplyMood(mood);

        if (_expanded)
        {
            TrackProgress.Value = App.Media.Progress;
            if (_pointerInside) _collapseAt = DateTime.Now.AddSeconds(2);
            else if (DateTime.Now > _collapseAt) Collapse();
        }
    }

    private FaceMood ResolveMood()
    {
        if (_listening) return FaceMood.Listening;
        if (DateTime.Now < _doneUntil) return FaceMood.Done;
        if (App.Media.IsPlaying) return FaceMood.Music;
        if (_sleeping && !_near) return FaceMood.Sleep;
        if (_near || _dragging || _hover) return FaceMood.Wow;
        return FaceMood.Idle;
    }

    private void ApplyMood(FaceMood mood)
    {
        _appliedMood = mood;
        Face.Mood = mood;

        var sleep = (Storyboard)Root.Resources["SleepStory"];
        if (mood == FaceMood.Sleep)
        {
            sleep.Begin();
            PillHost.Opacity = 0.8;
        }
        else
        {
            sleep.Stop();
            PillHost.Opacity = 1;
        }

        ApplyLayout();
    }

    // ───────────────────────── Souris ─────────────────────────

    // Déplacement : on suit directement le bouton de la souris (plus fiable que les
    // événements de la fenêtre, qui se perdent quand la fenêtre bouge sous le curseur).
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(PillHost).Properties.IsLeftButtonPressed) return;

        _pressed = true;
        _dragging = false;
        Win32.GetCursorPos(out _dragStartCursor);
        _dragStartWindow = AppWindow.Position;
        _dragTimer.Start();
        e.Handled = true;
    }

    private void OnDragTick(object? sender, object e)
    {
        if (!_pressed)
        {
            _dragTimer.Stop();
            return;
        }

        if (!Win32.IsPrimaryButtonDown())
        {
            // Bouton relâché
            _dragTimer.Stop();
            _pressed = false;
            if (_dragging)
            {
                _dragging = false;
                SaveDraggedPosition();
            }
            else
            {
                ToggleListen();   // simple clic : parler à Lucie
            }
            return;
        }

        if (!Win32.GetCursorPos(out var now)) return;
        int dx = now.X - _dragStartCursor.X, dy = now.Y - _dragStartCursor.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 5) _dragging = true;
        if (_dragging) AppWindow.Move(new PointInt32(_dragStartWindow.X + dx, _dragStartWindow.Y + dy));
    }

    /// <summary>Retient où tu as posé le widget, et de quel côté il doit grandir.</summary>
    private void SaveDraggedPosition()
    {
        var s = App.Settings.Current;
        double scale = Scale;
        var (bw, bh) = BaseSizePx(scale);
        var pos = AppWindow.Position;

        // Rectangle réel de la gélule à l'écran (la fenêtre peut être plus grande qu'elle).
        var bounds = PillHost.TransformToVisual(Root).TransformBounds(new Rect(0, 0, PillHost.ActualWidth, PillHost.ActualHeight));
        double left = pos.X + (bounds.X - Edge) * scale, top = pos.Y + (bounds.Y - Edge) * scale;
        double width = (bounds.Width + 2 * Edge) * scale, height = (bounds.Height + 2 * Edge) * scale;

        double cx = left + width / 2, cy = top + height / 2;
        var area = DisplayArea.GetFromPoint(new PointInt32((int)cx, (int)cy), DisplayAreaFallback.Nearest).WorkArea;

        // À gauche il grandit vers la droite, à droite vers la gauche, au milieu des deux côtés.
        // En haut de l'écran il s'ouvre vers le bas, en bas vers le haut.
        s.AnchorX = cx < area.X + area.Width / 3.0 ? -1 : cx > area.X + area.Width * 2 / 3.0 ? 1 : 0;
        s.AnchorY = cy > area.Y + area.Height / 2.0 ? 1 : -1;

        s.Position = WidgetPosition.Custom;
        s.CustomX = s.AnchorX < 0 ? left + bw / 2 : s.AnchorX > 0 ? left + width - bw / 2 : cx;
        s.CustomY = s.AnchorY > 0 ? top + height - bh / 2 : top + bh / 2;
        App.Settings.Save();
    }

    // ───────────────────────── Zone de notification ─────────────────────────

    /// <summary>Masque ou réaffiche le widget (Lucie continue d'écouter quand il est masqué).</summary>
    private void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden)
        {
            _hover = false;
            AppWindow.Hide();
        }
        else
        {
            AppWindow.Show(false);
            Face.Blink();
        }
    }

    private void ShowTrayMenu()
    {
        var name = App.Settings.Current.AssistantName;
        var entries = new List<MenuEntry>
        {
            new($"Parler à {name}"),
            new(_hidden ? "Afficher le widget" : "Masquer le widget"),
            new("Couper le micro", Checked: App.Speech.Muted),
            MenuEntry.Line,
            new("Paramètres…"),
            new("Quitter"),
        };

        int chosen = _tray?.ShowMenu(entries) ?? -1;
        _dispatcher.TryEnqueue(() =>
        {
            switch (chosen)
            {
                case 0: App.Speech.ListenNow(); break;
                case 1: SetHidden(!_hidden); break;
                case 2:
                    App.Speech.Muted = !App.Speech.Muted;
                    UpdateMicButton();
                    break;
                case 4: App.Current.OpenSettings(); break;
                case 5: App.Current.Quit(); break;
            }
        });
    }

    private void ShowMenu()
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;

        var talk = new MenuFlyoutItem { Text = $"Parler à {App.Settings.Current.AssistantName}", Icon = new FontIcon { Glyph = "" } };
        talk.Click += (_, _) => App.Speech.ListenNow();
        menu.Items.Add(talk);

        if (App.Media.HasTrack)
        {
            var player = new MenuFlyoutItem { Text = "Afficher le lecteur", Icon = new FontIcon { Glyph = "" } };
            player.Click += (_, _) => Expand();
            menu.Items.Add(player);
        }

        var hide = new MenuFlyoutItem { Text = "Masquer le widget", Icon = new FontIcon { Glyph = "\uED1A" } };
        hide.Click += (_, _) => SetHidden(true);
        menu.Items.Add(hide);

        var settings = new MenuFlyoutItem { Text = "Paramètres…", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => App.Current.OpenSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new MenuFlyoutSeparator());

        var quit = new MenuFlyoutItem { Text = "Quitter", Icon = new FontIcon { Glyph = "" } };
        quit.Click += (_, _) => App.Current.Quit();
        menu.Items.Add(quit);

        menu.ShowAt(PillHost);
    }
}
