using System.Numerics;
using Lucie.Models;
using Lucie.Native;
using Lucie.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT;
using WinRT.Interop;

namespace Lucie.Views;

/// <summary>
/// Le widget flottant : une gélule en verre dépoli, toujours au premier plan.
/// La fenêtre est découpée exactement à la forme du widget (bouts entièrement ronds).
/// </summary>
public sealed partial class WidgetWindow : Window
{
    private const double ExpandedWidth = 520;
    private const double ExpandedHeight = 112;
    private static readonly TimeSpan ListenWatchdog = TimeSpan.FromSeconds(15);

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly HotkeyService _hotkeys;

    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;
    private Storyboard? _hostStory;
    private IRandomAccessStreamReference? _shownThumbnail;

    private bool _listening;
    private bool _expanded;
    private bool _near;
    private bool _sleeping;
    private bool _pointerInside;
    private DateTime _doneUntil;
    private DateTime _listenStarted;
    private FaceMood _appliedMood = FaceMood.Idle;

    // Déplacement en restant appuyé
    private bool _pressed;
    private bool _dragging;
    private Win32.POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;

    public WidgetWindow()
    {
        InitializeComponent();
        Title = "Lucie";

        _hwnd = WindowNative.GetWindowHandle(this);
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        SetupChrome();
        SetupBackdrop();
        SetupTransitions();

        // L'éclat « c'est fait » ne doit pas bloquer le contour une fois terminé.
        var doneStory = (Storyboard)Root.Resources["DoneStory"];
        doneStory.Completed += (_, _) => doneStory.Stop();

        _hotkeys = new HotkeyService(_hwnd);
        _hotkeys.Pressed += () => _dispatcher.TryEnqueue(ToggleListen);

        ApplySettings();

        // Souris : rester appuyé + bouger = déplacer ; simple clic = parler à Lucie ; clic droit = menu.
        PillHost.PointerPressed += OnPointerPressed;
        PillHost.PointerMoved += OnPointerMoved;
        PillHost.PointerReleased += OnPointerReleased;
        PillHost.PointerCaptureLost += OnPointerCaptureLost;
        PillHost.RightTapped += (_, e) => { ShowMenu(); e.Handled = true; };

        // La forme de la fenêtre suit la taille du widget.
        Root.SizeChanged += (_, _) => UpdateRegion();
        PillHost.SizeChanged += (_, _) => UpdateRegion();

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
        App.Settings.Changed += (_, _) => _dispatcher.TryEnqueue(ApplySettings);

        _collapseTimer.Tick += (_, _) =>
        {
            if (_pointerInside) return;
            _collapseTimer.Stop();
            Collapse();
        };

        _tick.Tick += OnTick;
        _tick.Start();

        Closed += (_, _) =>
        {
            _tick.Stop();
            _hotkeys.Dispose();
            _acrylic?.Dispose();
            _acrylic = null;
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
    }

    /// <summary>Verre dépoli (acrylique) qui reste actif même quand Lucie n'a pas le focus.</summary>
    private void SetupBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported()) return;

        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        _acrylic = new DesktopAcrylicController();
        _acrylic.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);

        Activated += (_, _) =>
        {
            if (_backdropConfig is not null) _backdropConfig.IsInputActive = true;
        };
    }

    private void SetupTransitions()
    {
        Player.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        PillHost.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(500) };
        Face.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        Face.SizeChanged += (_, _) =>
            Face.CenterPoint = new Vector3((float)(Face.ActualWidth / 2), (float)(Face.ActualHeight / 2), 0);
    }

    private double SizeFactor => App.Settings.Current.Size switch
    {
        WidgetSize.Small => 0.8,
        WidgetSize.Large => 1.25,
        _ => 1.0
    };

    private (double W, double H) CompactSize()
    {
        double k = SizeFactor;
        double w = _listening ? 132 : _appliedMood == FaceMood.Sleep ? 124 : 104;
        return (w * k, 64 * k);
    }

    private void ApplySettings()
    {
        var s = App.Settings.Current;

        // Transparence du verre
        double t = Math.Clamp(s.WidgetTransparency / 100.0, 0, 0.9);
        if (_acrylic is not null)
        {
            // Derrière le noir, un verre flouté : visible seulement si on rend le widget transparent.
            _acrylic.TintColor = Colors.Black;
            _acrylic.TintOpacity = 0.6f;
            _acrylic.LuminosityOpacity = 0.6f;
            _acrylic.FallbackColor = Colors.Black;
        }

        // Gélule noire légèrement bombée, comme l'icône
        var body = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), Opacity = 1 - t * 0.75 };
        body.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x26, 0x27, 0x2B), Offset = 0 });
        body.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x0B, 0x0B, 0x0D), Offset = 0.38 });
        body.GradientStops.Add(new GradientStop { Color = Colors.Black, Offset = 1 });
        Pill.Background = body;

        // Le widget « s'accroche » au bord choisi : il grandit vers l'intérieur de l'écran.
        if (s.Position == WidgetPosition.Custom)
        {
            PillHost.HorizontalAlignment = HorizontalAlignment.Center;
            PillHost.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            int col = (int)s.Position % 3, row = (int)s.Position / 3;
            PillHost.HorizontalAlignment = col switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right };
            PillHost.VerticalAlignment = row switch { 0 => VerticalAlignment.Top, 1 => VerticalAlignment.Center, _ => VerticalAlignment.Bottom };
        }

        ApplyLayout();
        var (w, h) = _expanded ? (ExpandedWidth, ExpandedHeight) : CompactSize();
        _hostStory?.Stop();
        _hostStory = null;
        PillHost.Width = w;
        PillHost.Height = h;
        PlaceWindow(w, h);

        _hotkeys.Register(s.TalkHotkey);
    }

    private void ApplyLayout()
    {
        double k = SizeFactor;
        if (_expanded)
        {
            Face.Width = 64; Face.Height = 46;
            Grid.SetColumnSpan(LeftStack, 1);
            LeftStack.HorizontalAlignment = HorizontalAlignment.Left;
            PillContent.Padding = new Thickness(22, 0, 18, 0);
            Pill.CornerRadius = new CornerRadius(ExpandedHeight / 2);
        }
        else
        {
            Face.Width = 56 * k; Face.Height = 40 * k;
            Grid.SetColumnSpan(LeftStack, 2);
            LeftStack.HorizontalAlignment = HorizontalAlignment.Center;
            PillContent.Padding = new Thickness(16 * k, 0, 16 * k, 0);
            Pill.CornerRadius = new CornerRadius(32 * k);
        }
        Ring.CornerRadius = Pill.CornerRadius;
        Bars.Visibility = _listening && !_expanded ? Visibility.Visible : Visibility.Collapsed;
        Zs.Visibility = _appliedMood == FaceMood.Sleep && !_expanded && !_listening ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Place et dimensionne la fenêtre selon la position choisie (pixels physiques).</summary>
    private void PlaceWindow(double widthDip, double heightDip)
    {
        var s = App.Settings.Current;
        double scale = Win32.ScaleFor(_hwnd);
        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        const int edge = 16; // petit espace avec le bord de l'écran

        int x, y;
        RectInt32 area;

        if (s.Position == WidgetPosition.Custom && s.CustomX is double cx && s.CustomY is double cy)
        {
            area = DisplayArea.GetFromPoint(new PointInt32((int)cx, (int)cy), DisplayAreaFallback.Nearest).WorkArea;
            x = (int)(cx - w / 2.0);
            y = (int)(cy - h / 2.0);
        }
        else
        {
            area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int col = (int)s.Position % 3, row = (int)s.Position / 3;
            x = col switch { 0 => area.X + edge, 1 => area.X + (area.Width - w) / 2, _ => area.X + area.Width - w - edge };
            y = row switch { 0 => area.Y + edge, 1 => area.Y + (area.Height - h) / 2, _ => area.Y + area.Height - h - edge };
        }

        x = Math.Max(area.X, Math.Min(x, area.X + area.Width - w));
        y = Math.Max(area.Y, Math.Min(y, area.Y + area.Height - h));
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        UpdateRegion();
    }

    /// <summary>Découpe la fenêtre à la forme exacte de la gélule.</summary>
    private void UpdateRegion()
    {
        double w = PillHost.ActualWidth > 0 ? PillHost.ActualWidth : PillHost.Width;
        double h = PillHost.ActualHeight > 0 ? PillHost.ActualHeight : PillHost.Height;
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;

        double scale = Win32.ScaleFor(_hwnd);
        var bounds = PillHost.ActualWidth > 0
            ? PillHost.TransformToVisual(Root).TransformBounds(new Rect(0, 0, w, h))
            : new Rect(0, 0, w, h);

        Win32.SetCapsuleRegion(_hwnd,
            (int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
    }

    private void AnimateHost(double w, double h, Action? done = null)
    {
        _hostStory?.Stop();

        double fromW = PillHost.ActualWidth > 0 ? PillHost.ActualWidth : w;
        double fromH = PillHost.ActualHeight > 0 ? PillHost.ActualHeight : h;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(280));

        var aw = new DoubleAnimation { From = fromW, To = w, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        var ah = new DoubleAnimation { From = fromH, To = h, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(aw, PillHost);
        Storyboard.SetTargetProperty(aw, "Width");
        Storyboard.SetTarget(ah, PillHost);
        Storyboard.SetTargetProperty(ah, "Height");

        var story = new Storyboard();
        story.Children.Add(aw);
        story.Children.Add(ah);
        story.Completed += (_, _) =>
        {
            if (_hostStory != story) return;
            PillHost.Width = w;
            PillHost.Height = h;
            story.Stop();
            _hostStory = null;
            done?.Invoke();
            UpdateRegion();
        };
        _hostStory = story;
        story.Begin();
    }

    private void ResizeCompact()
    {
        if (_expanded) return;
        var (w, h) = CompactSize();
        if (w >= PillHost.ActualWidth)
        {
            PlaceWindow(w, h);
            AnimateHost(w, h);
        }
        else
        {
            AnimateHost(w, h, () => PlaceWindow(w, h));
        }
    }

    // ───────────────────────── Lecteur ─────────────────────────

    private void Expand()
    {
        if (!App.Media.HasTrack) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
        if (_expanded) return;

        _expanded = true;
        UpdatePlayer();
        ApplyLayout();
        Player.Visibility = Visibility.Visible;

        PlaceWindow(ExpandedWidth, ExpandedHeight);
        AnimateHost(ExpandedWidth, ExpandedHeight, () => Player.Opacity = 1);
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        Player.Opacity = 0;

        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(180);
            if (_expanded) return;
            Player.Visibility = Visibility.Collapsed;
            ApplyLayout();
            var (w, h) = CompactSize();
            AnimateHost(w, h, () => PlaceWindow(w, h));
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
        if (on) _listenStarted = DateTime.Now;

        var story = (Storyboard)Root.Resources["ListenStory"];
        if (on) story.Begin();
        else
        {
            story.Stop();
            Ring.Opacity = 0;
        }

        ApplyLayout();
        ResizeCompact();
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
        if (intent.App is AppEntry app)
        {
            ok = await App.Actions.OpenAppAsync(app);
        }
        else if (intent.Command!.Action == CommandAction.Screenshot)
        {
            // On se cache le temps de la capture pour ne pas apparaître dessus.
            AppWindow.Hide();
            await Task.Delay(180);
            ok = await App.Actions.ExecuteAsync(intent.Command);
            AppWindow.Show(false);
        }
        else
        {
            ok = await App.Actions.ExecuteAsync(intent.Command);
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

        if (!_dragging && Win32.GetCursorPos(out var cursor) && Face.ActualWidth > 0 && PillHost.ActualWidth > 0)
        {
            double scale = Win32.ScaleFor(_hwnd);
            var pos = AppWindow.Position;

            // Rectangle du widget à l'écran
            var bounds = PillHost.TransformToVisual(Root).TransformBounds(new Rect(0, 0, PillHost.ActualWidth, PillHost.ActualHeight));
            double left = pos.X + bounds.X * scale, top = pos.Y + bounds.Y * scale;
            double right = left + bounds.Width * scale, bottom = top + bounds.Height * scale;

            double dx = Math.Max(Math.Max(left - cursor.X, 0), cursor.X - right);
            double dy = Math.Max(Math.Max(top - cursor.Y, 0), cursor.Y - bottom);
            double distance = Math.Sqrt(dx * dx + dy * dy);

            _pointerInside = distance <= 0;
            _near = distance <= s.ProximityPx * scale;

            if (_near)
            {
                // Elle te suit des yeux
                var center = Face.TransformToVisual(Root).TransformPoint(new Point(Face.ActualWidth / 2, Face.ActualHeight / 2));
                double fx = pos.X + center.X * scale, fy = pos.Y + center.Y * scale;
                double vx = cursor.X - fx, vy = cursor.Y - fy;
                double len = Math.Sqrt(vx * vx + vy * vy);
                float strength = (float)Math.Min(1, len / (120 * scale));
                Face.SetGaze(len < 1 ? Vector2.Zero : new Vector2((float)(vx / len), (float)(vy / len)) * strength);
            }
            else
            {
                Face.SetGaze(null);
            }
        }

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
            if (_pointerInside) _collapseTimer.Stop();
            else if (!_collapseTimer.IsEnabled) _collapseTimer.Start();
        }
    }

    private FaceMood ResolveMood()
    {
        if (_listening) return FaceMood.Listening;
        if (DateTime.Now < _doneUntil) return FaceMood.Done;
        if (App.Media.IsPlaying) return FaceMood.Music;
        if (_sleeping) return FaceMood.Sleep;
        if (_near || _dragging) return FaceMood.Wow;
        return FaceMood.Idle;
    }

    private void ApplyMood(FaceMood mood)
    {
        bool sleepChanged = (mood == FaceMood.Sleep) != (_appliedMood == FaceMood.Sleep);
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

        bool wow = mood == FaceMood.Wow;
        Face.Scale = wow ? new Vector3(1.1f, 1.1f, 1f) : Vector3.One;
        if (!_listening) Ring.Opacity = wow ? 0.55 : 0;

        ApplyLayout();
        if (sleepChanged) ResizeCompact();
    }

    // ───────────────────────── Souris ─────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(PillHost).Properties.IsLeftButtonPressed) return;

        _pressed = true;
        _dragging = false;
        Win32.GetCursorPos(out _dragStartCursor);
        _dragStartWindow = AppWindow.Position;
        PillHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || !Win32.GetCursorPos(out var now)) return;

        int dx = now.X - _dragStartCursor.X, dy = now.Y - _dragStartCursor.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 6) _dragging = true;
        if (_dragging) AppWindow.Move(new PointInt32(_dragStartWindow.X + dx, _dragStartWindow.Y + dy));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        PillHost.ReleasePointerCapture(e.Pointer);

        if (_dragging) SaveDraggedPosition();
        else ToggleListen();   // simple clic : parler à Lucie

        _dragging = false;
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed && _dragging) SaveDraggedPosition();
        _pressed = false;
        _dragging = false;
    }

    private void SaveDraggedPosition()
    {
        var s = App.Settings.Current;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        s.Position = WidgetPosition.Custom;
        s.CustomX = pos.X + size.Width / 2.0;
        s.CustomY = pos.Y + size.Height / 2.0;
        App.Settings.Save();
    }

    private void ShowMenu()
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };

        var talk = new MenuFlyoutItem { Text = $"Parler à {App.Settings.Current.AssistantName}", Icon = new FontIcon { Glyph = "" } };
        talk.Click += (_, _) => App.Speech.ListenNow();
        menu.Items.Add(talk);

        if (App.Media.HasTrack)
        {
            var player = new MenuFlyoutItem { Text = "Afficher le lecteur", Icon = new FontIcon { Glyph = "" } };
            player.Click += (_, _) => Expand();
            menu.Items.Add(player);
        }

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
