using System.Numerics;
using Lucie.Models;
using Lucie.Native;
using Microsoft.UI;
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
using WinRT.Interop;

namespace Lucie.Views;

/// <summary>
/// Le widget flottant : toujours au premier plan, sans bordure, transparent autour.
/// </summary>
public sealed partial class WidgetWindow : Window
{
    private const double EdgeMargin = 24;      // marge transparente autour du widget (DIP)
    private const double ExpandedWidth = 500;
    private const double ExpandedHeight = 112;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly SolidColorBrush _idleBorder = new(ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    private Storyboard? _hostStory;
    private IRandomAccessStreamReference? _shownThumbnail;

    private bool _listening;
    private bool _expanded;
    private bool _near;
    private bool _sleeping;
    private bool _pointerInside;
    private DateTime _doneUntil;
    private FaceMood _appliedMood = FaceMood.Idle;

    public WidgetWindow()
    {
        InitializeComponent();
        Title = "Lucie";

        _hwnd = WindowNative.GetWindowHandle(this);
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        SetupChrome();
        SetupTransitions();
        ApplySettings();

        // Interactions souris
        PillHost.PointerPressed += OnPointerPressed;
        PillHost.RightTapped += (_, e) => { ShowMenu(); e.Handled = true; };

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
        Closed += (_, _) => _tick.Stop();
    }

    // ───────────────────────── Fenêtre ─────────────────────────

    private void SetupChrome()
    {
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop { TintColor = Colors.Transparent };

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

    private void SetupTransitions()
    {
        Player.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        PillHost.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(400) };
        PillHost.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        PillHost.SizeChanged += (_, _) =>
            PillHost.CenterPoint = new Vector3((float)(PillHost.ActualWidth / 2), (float)(PillHost.ActualHeight / 2), 0);
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
        return ((_listening ? 124 : 96) * k, 64 * k);
    }

    private void ApplySettings()
    {
        var s = App.Settings.Current;
        double k = SizeFactor;

        // Transparence du fond du widget
        byte alpha = (byte)Math.Clamp(255 * (1 - s.WidgetTransparency / 100.0), 25, 255);
        Pill.Background = new SolidColorBrush(ColorHelper.FromArgb(alpha, 0x1E, 0x21, 0x2B));

        Pill.CornerRadius = new CornerRadius(_expanded ? 28 : 22 * k);
        Ring.CornerRadius = Pill.CornerRadius;

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
        PillHost.Width = w;
        PillHost.Height = h;
        PlaceWindow(w, h);
    }

    private void ApplyLayout()
    {
        double k = SizeFactor;
        if (_expanded)
        {
            Face.Width = 64; Face.Height = 46;
            Grid.SetColumnSpan(LeftStack, 1);
            LeftStack.HorizontalAlignment = HorizontalAlignment.Left;
            Pill.CornerRadius = new CornerRadius(28);
        }
        else
        {
            Face.Width = 56 * k; Face.Height = 40 * k;
            Grid.SetColumnSpan(LeftStack, 2);
            LeftStack.HorizontalAlignment = HorizontalAlignment.Center;
            Pill.CornerRadius = new CornerRadius(22 * k);
        }
        Ring.CornerRadius = Pill.CornerRadius;
        Bars.Visibility = _listening && !_expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Place et dimensionne la fenêtre selon la position choisie (pixels physiques).</summary>
    private void PlaceWindow(double widthDip, double heightDip)
    {
        var s = App.Settings.Current;
        double scale = Win32.ScaleFor(_hwnd);
        int w = (int)Math.Ceiling((widthDip + 2 * EdgeMargin) * scale);
        int h = (int)Math.Ceiling((heightDip + 2 * EdgeMargin) * scale);

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
            x = col switch { 0 => area.X, 1 => area.X + (area.Width - w) / 2, _ => area.X + area.Width - w };
            y = row switch { 0 => area.Y, 1 => area.Y + (area.Height - h) / 2, _ => area.Y + area.Height - h };
        }

        x = Math.Max(area.X, Math.Min(x, area.X + area.Width - w));
        y = Math.Max(area.Y, Math.Min(y, area.Y + area.Height - h));
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void AnimateHost(double w, double h, Action? done = null)
    {
        _hostStory?.Stop();

        double fromW = double.IsNaN(PillHost.Width) ? w : PillHost.ActualWidth;
        double fromH = double.IsNaN(PillHost.Height) ? h : PillHost.ActualHeight;
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
            AlbumArt.Source = null;
            return;
        }
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (_shownThumbnail == thumbnail) AlbumArt.Source = bitmap;
        }
        catch
        {
            AlbumArt.Source = null;
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await App.Media.PreviousAsync();
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await App.Media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await App.Media.NextAsync();

    // ───────────────────────── Voix ─────────────────────────

    private void SetListening(bool on)
    {
        if (_listening == on) return;
        _listening = on;

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

        var command = App.Interpreter.Match(heard, App.Settings.Current.Commands)
                      ?? App.Interpreter.FallbackOpen(heard);

        if (command is null)
        {
            App.Sounds.PlayError();
            return;
        }

        bool ok;
        if (command.Action == CommandAction.Screenshot)
        {
            // On se cache le temps de la capture pour ne pas apparaître dessus.
            AppWindow.Hide();
            await Task.Delay(180);
            ok = await App.Actions.ExecuteAsync(command);
            AppWindow.Show(false);
        }
        else
        {
            ok = await App.Actions.ExecuteAsync(command);
        }

        if (ok)
        {
            App.Sounds.PlayDone();
            ShowDone();
        }
        else
        {
            App.Sounds.PlayError();
        }
    }

    private void ShowDone()
    {
        _doneUntil = DateTime.Now.AddSeconds(1.6);
        Badge.Visibility = Visibility.Visible;
    }

    // ───────────────────────── Vie du widget ─────────────────────────

    private void OnTick(object? sender, object e)
    {
        var s = App.Settings.Current;

        if (Win32.GetCursorPos(out var cursor) && Face.ActualWidth > 0)
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

        if (Badge.Visibility == Visibility.Visible && DateTime.Now >= _doneUntil)
            Badge.Visibility = Visibility.Collapsed;

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
        if (_near) return FaceMood.Wow;
        return FaceMood.Idle;
    }

    private void ApplyMood(FaceMood mood)
    {
        _appliedMood = mood;
        Face.Mood = mood;

        var sleep = (Storyboard)Root.Resources["SleepStory"];
        if (mood == FaceMood.Sleep)
        {
            Zs.Visibility = Visibility.Visible;
            sleep.Begin();
            PillHost.Opacity = 0.75;
        }
        else
        {
            sleep.Stop();
            Zs.Visibility = Visibility.Collapsed;
            PillHost.Opacity = 1;
        }

        bool wow = mood == FaceMood.Wow;
        PillHost.Scale = wow ? new Vector3(1.07f, 1.07f, 1f) : Vector3.One;
        Pill.BorderBrush = wow ? App.Accent : _idleBorder;
    }

    // ───────────────────────── Souris ─────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PillHost);
        if (!point.Properties.IsLeftButtonPressed) return;

        // Glisser = déplacer le widget. Simple clic = parler à Lucie.
        var before = AppWindow.Position;
        Win32.ReleaseCapture();
        Win32.SendMessage(_hwnd, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
        var after = AppWindow.Position;

        if (Math.Abs(after.X - before.X) > 4 || Math.Abs(after.Y - before.Y) > 4)
        {
            var s = App.Settings.Current;
            var size = AppWindow.Size;
            s.Position = WidgetPosition.Custom;
            s.CustomX = after.X + size.Width / 2.0;
            s.CustomY = after.Y + size.Height / 2.0;
            App.Settings.Save();
        }
        else
        {
            App.Speech.ListenNow();
        }
        e.Handled = true;
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
