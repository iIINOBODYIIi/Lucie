using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Onde.Core.AirPlay;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;

namespace Lucie.AirPlay;

/// <summary>
/// Le widget AirPlay (repris d'Onde) : pochette, titre, progression, commandes de l'iPhone,
/// égaliseur et mode concert. Petite fenêtre sans bordure, déplaçable et redimensionnable.
/// </summary>
public sealed partial class AirPlayWindow : Window
{
    private readonly AirPlayService _app = App.AirPlay;
    private bool _hiddenAuto;               // caché automatiquement (plein écran / aucun iPhone)
    private DateTime _nextVisibilityCheck;
    private readonly IntPtr _hwnd;
    private readonly OverlappedPresenter _presenter;
    private readonly DispatcherQueueTimer _timer;
    private readonly Random _rnd = new();
    private readonly double[] _bars = new double[4];
    private BackdropMode? _currentBackdrop;
    private Color _accent, _text;
    private double _pos, _dur;
    private DateTime _lastTick = DateTime.UtcNow;
    private string? _flash;
    private DateTime _flashUntil;
    private bool _hasArtwork;
    private bool? _lastPlaying;
    private bool _lastConnected;
    // redimensionnement à la souris
    private bool _resizing;
    private OndeWin32.POINT _resizeStart;
    private SizeInt32 _resizeStartSize;

    public AirPlayWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AirPlay.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Title = "AirPlay";

        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(_presenter);

        ApplyAppearance();
        PlaceWindow();

        VolumeSlider.Value = Math.Round(_app.Settings.LocalVolume * 100);
        UpdateVolumeIcon();

        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPositionChange)
            {
                _app.Settings.WindowX = AppWindow.Position.X;
                _app.Settings.WindowY = AppWindow.Position.Y;
                _app.SaveSoon();
            }
        };

        _onState = () => DispatcherQueue.TryEnqueue(UpdateTexts);
        _onTrack = _ => DispatcherQueue.TryEnqueue(UpdateTexts);
        _onArtwork = img => DispatcherQueue.TryEnqueue(async () => await SetArtworkAsync(img));
        _onAppearance = () => DispatcherQueue.TryEnqueue(ApplyAppearance);
        _onEffects = () => DispatcherQueue.TryEnqueue(() => { RefreshEffectButtons(); SyncVolume(); });
        _onResize = () => DispatcherQueue.TryEnqueue(() => ResizeTo(_app.Settings.WidgetWidth, _app.Settings.WidgetHeight));
        _app.ReceiverStateChanged += _onState;
        _app.RunningChanged += _onState;
        _app.TrackChanged += _onTrack;
        _app.ArtworkChanged += _onArtwork;
        _app.AppearanceChanged += _onAppearance;
        _app.EffectsChanged += _onEffects;
        _app.ResizeRequested += _onResize;
        if (_app.Artwork is { } art) _ = SetArtworkAsync(art);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.ReceiverStateChanged -= _onState;
            _app.RunningChanged -= _onState;
            _app.TrackChanged -= _onTrack;
            _app.ArtworkChanged -= _onArtwork;
            _app.AppearanceChanged -= _onAppearance;
            _app.EffectsChanged -= _onEffects;
            _app.ResizeRequested -= _onResize;
        };
        UpdateTexts();
        RefreshEffectButtons();
    }

    private readonly Action _onState, _onAppearance, _onEffects, _onResize;
    private readonly Action<TrackInfo> _onTrack;
    private readonly Action<byte[]?> _onArtwork;

    private void SyncVolume()
    {
        if (Math.Abs(VolumeSlider.Value - _app.Settings.LocalVolume * 100) > 0.5)
            VolumeSlider.Value = Math.Round(_app.Settings.LocalVolume * 100);
        UpdateVolumeIcon();
    }

    // ------------------------------------------------------------------ apparence

    public void ApplyAppearance()
    {
        var s = _app.Settings;
        _accent = AirPlaySettings.ParseColor(s.AccentColor, ColorHelper.FromArgb(255, 139, 92, 246));
        _text = AirPlaySettings.ParseColor(s.TextColor, Colors.White);
        var bg = AirPlaySettings.ParseColor(s.BackgroundColor, ColorHelper.FromArgb(255, 21, 19, 31));
        double opacity = s.Backdrop == BackdropMode.Solid ? 1 : Math.Clamp(s.BackgroundOpacity, 0, 1);
        var bgBrush = new SolidColorBrush(ColorHelper.FromArgb((byte)(opacity * 255), bg.R, bg.G, bg.B));

        // Fond de fenêtre : opaque, verre dépoli (acrylique) ou transparent
        if (_currentBackdrop != s.Backdrop)
        {
            SystemBackdrop = s.Backdrop switch
            {
                BackdropMode.Acrylic => new DesktopAcrylicBackdrop(),
                BackdropMode.Transparent => new WinUIEx.TransparentTintBackdrop(),
                _ => null,
            };
            _currentBackdrop = s.Backdrop;
        }
        OndeWin32.SetCorners(_hwnd, round: s.Backdrop != BackdropMode.Transparent);

        if (s.Backdrop == BackdropMode.Solid)
        {
            Root.Background = bgBrush;
            Card.Background = new SolidColorBrush(Colors.Transparent);
            Card.CornerRadius = new CornerRadius(0);
            Card.BorderThickness = new Thickness(0);
        }
        else
        {
            Root.Background = new SolidColorBrush(Colors.Transparent);
            Card.Background = bgBrush;
            double radius = s.Backdrop == BackdropMode.Transparent ? s.CornerRadius : 0;
            Card.CornerRadius = new CornerRadius(radius);
            Card.BorderThickness = new Thickness(s.Backdrop == BackdropMode.Transparent ? 1 : 0);
        }
        ArtFrame.CornerRadius = new CornerRadius(Math.Max(4, s.CornerRadius * 0.7));

        var textBrush = new SolidColorBrush(_text);
        foreach (var tb in new[] { TitleText, ArtistText, StatusText, ElapsedText, DurationText })
            tb.Foreground = textBrush;
        foreach (var b in new Button[] { SettingsButton, MinimizeButton, PrevButton, NextButton, VolumeButton })
            b.Foreground = textBrush;
        ArtPlaceholder.Foreground = textBrush;

        var accentBrush = new SolidColorBrush(_accent);
        PlayButton.Background = accentBrush;
        PlayButton.Foreground = new SolidColorBrush(Luminance(_accent) > 0.6 ? Colors.Black : Colors.White);
        Progress.Foreground = accentBrush;
        foreach (var bar in new[] { Bar1, Bar2, Bar3, Bar4 }) bar.Fill = accentBrush;
        Visualizer.Visibility = s.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;

        ArtBackdrop.Visibility = ArtShade.Visibility = s.ArtworkBackground && _hasArtwork ? Visibility.Visible : Visibility.Collapsed;

        // Transparence du contenu (texte, pochette, boutons)
        double content = Math.Clamp(s.ContentOpacity, 0.15, 1);
        ContentGrid.Opacity = content;
        ArtBackdrop.Opacity = 0.30 * content;
        ArtShade.Opacity = content;
        GripGlyph.Foreground = textBrush;
        ShadeStop1.Color = ColorHelper.FromArgb(0, bg.R, bg.G, bg.B);
        ShadeStop2.Color = ColorHelper.FromArgb((byte)(Math.Max(opacity, 0.5) * 230), bg.R, bg.G, bg.B);

        // Taille choisie (la mise en page s'adapte dans ApplyLayout)
        ResizeTo(s.WidgetWidth, s.WidgetHeight);

        // Mode d'affichage
        ApplyDisplayMode();
        ResizeGrip.Visibility = s.LockPosition ? Visibility.Collapsed : Visibility.Visible;
        AppWindow.IsShownInSwitchers = s.ShowInTaskbar;
        MinimizeButton.Visibility = s.ShowInTaskbar && s.Mode != WidgetMode.Desktop ? Visibility.Visible : Visibility.Collapsed;

        RefreshEffectButtons();
        UpdateTexts();
    }

    public void RefreshEffectButtons()
    {
        var s = _app.Settings;
        EqButton.Foreground = new SolidColorBrush(s.EqEnabled ? _accent : _text);
        EqButton.Opacity = s.EqEnabled ? 1 : 0.75;
        ConcertButton.Foreground = new SolidColorBrush(s.ConcertEnabled ? _accent : _text);
        ConcertButton.Opacity = s.ConcertEnabled ? 1 : 0.75;
    }

    /// <summary>Widget flottant / fenêtre normale / fixé sur le bureau.</summary>
    private void ApplyDisplayMode()
    {
        var mode = _app.Settings.Mode;
        _presenter.IsAlwaysOnTop = mode == WidgetMode.Widget;
        try { OndeWin32.PinToBottom(_hwnd, mode == WidgetMode.Desktop); } catch { }
    }

    /// <summary>Redimensionne la fenêtre (taille en pixels logiques).</summary>
    public void ResizeTo(int width, int height)
    {
        double scale = OndeWin32.GetScale(_hwnd);
        var target = new SizeInt32((int)(Math.Clamp(width, AirPlaySettings.MinWidth, AirPlaySettings.MaxWidth) * scale),
                                   (int)(Math.Clamp(height, AirPlaySettings.MinHeight, AirPlaySettings.MaxHeight) * scale));
        if (AppWindow.Size.Width != target.Width || AppWindow.Size.Height != target.Height)
            AppWindow.Resize(target);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width, e.NewSize.Height);

    /// <summary>Adapte la pochette, la police et les éléments visibles à la taille de la fenêtre.</summary>
    private void ApplyLayout(double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        double pad = h < 110 ? 8 : 14;
        ContentGrid.Padding = new Thickness(pad);
        ContentGrid.ColumnSpacing = pad;

        // pochette carrée qui suit la hauteur, sans manger plus de 45 % de la largeur
        double art = Math.Min(h - 2 * pad, w * 0.45);
        bool showArt = art >= 40 && w >= 280;
        ArtFrame.Visibility = showArt ? Visibility.Visible : Visibility.Collapsed;
        ArtFrame.Width = ArtFrame.Height = Math.Max(40, art);
        ContentGrid.ColumnDefinitions[0].Width = showArt ? GridLength.Auto : new GridLength(0);

        TitleText.FontSize = Math.Clamp(h / 9.0, 13, 30);
        ArtistText.FontSize = Math.Clamp(h / 12.5, 11, 20);
        ArtistText.Visibility = h >= 100 ? Visibility.Visible : Visibility.Collapsed;
        ProgressRow.Visibility = h >= 135 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.MaxWidth = Math.Max(80, w - (showArt ? art : 0) - 140);
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private void PlaceWindow()
    {
        var s = _app.Settings;
        var size = AppWindow.Size;
        if (s.WindowX is int x && s.WindowY is int y &&
            DisplayArea.GetFromPoint(new PointInt32(x + 20, y + 20), DisplayAreaFallback.None) != null)
        {
            AppWindow.Move(new PointInt32(x, y));
            return;
        }
        var wa = DisplayArea.Primary.WorkArea;
        int margin = (int)(24 * OndeWin32.GetScale(_hwnd));
        AppWindow.Move(new PointInt32(wa.X + wa.Width - size.Width - margin, wa.Y + wa.Height - size.Height - margin));
    }

    // ------------------------------------------------------------------ état / textes

    private void UpdateTexts()
    {
        var r = _app.Receiver;
        string name = _app.Settings.ReceiverName;

        if (!_app.IsRunning)
        {
            StatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 156, 163, 175));
            StatusText.Text = "AirPlay éteint";
            TitleText.Text = "Enceinte éteinte";
            ArtistText.Text = "Paramètres de Lucie › AirPlay";
            _pos = _dur = 0;
            return;
        }

        if (_app.StartupError != null)
        {
            StatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68));
            StatusText.Text = "Erreur";
            TitleText.Text = "AirPlay indisponible";
            ArtistText.Text = _app.StartupError;
            return;
        }

        if (r == null || r.State == ReceiverState.Waiting)
        {
            StatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 156, 163, 175));
            StatusText.Text = $"En attente · {name}";
            TitleText.Text = "Prêt à recevoir";
            ArtistText.Text = $"iPhone → AirPlay → « {name} »";
            _pos = _dur = 0;
            return;
        }

        StatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 52, 211, 153));
        StatusText.Text = $"Connecté · {(string.IsNullOrEmpty(r.ClientName) ? "iPhone" : r.ClientName)}";
        var t = r.Track;
        TitleText.Text = string.IsNullOrWhiteSpace(t.Title) ? "Lecture AirPlay" : t.Title;
        string sub = string.Join(" — ", new[] { t.Artist, t.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        ArtistText.Text = sub.Length > 0 ? sub : "Depuis ton iPhone";
    }

    private async Task SetArtworkAsync(byte[]? data)
    {
        if (data == null || data.Length == 0)
        {
            ArtImage.Source = null;
            ArtBackdrop.Source = null;
            _hasArtwork = false;
            ArtPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            try
            {
                var bmp = new BitmapImage();
                using var ms = new InMemoryRandomAccessStream();
                using (var w = new DataWriter(ms.GetOutputStreamAt(0)))
                {
                    w.WriteBytes(data);
                    await w.StoreAsync();
                    await w.FlushAsync();
                }
                ms.Seek(0);
                await bmp.SetSourceAsync(ms);
                ArtImage.Source = bmp;
                ArtBackdrop.Source = bmp;
                _hasArtwork = true;
                ArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch { /* image illisible */ }
        }
        bool show = _app.Settings.ArtworkBackground && _hasArtwork;
        ArtBackdrop.Visibility = ArtShade.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        var p = _app.Pipeline;
        bool playing = p.IsPlaying;

        PlayIcon.Glyph = playing ? "" : "";

        // progression (le service suit la position et informe Windows)
        _pos = _app.Position;
        _dur = _app.Duration;
        UpdateAutoVisibility();
        Progress.Value = _dur > 0 ? _pos / _dur : 0;
        ElapsedText.Text = Format(_pos);
        DurationText.Text = Format(_dur);

        // vumètre
        if (_app.Settings.ShowVisualizer)
        {
            var bars = new[] { Bar1, Bar2, Bar3, Bar4 };
            float peak = playing ? p.Peak : 0;
            for (int i = 0; i < 4; i++)
            {
                double target = playing ? 3 + Math.Min(1, peak * 1.4) * 11 * (0.45 + _rnd.NextDouble() * 0.55) : 3;
                _bars[i] += (target - _bars[i]) * 0.45;
                bars[i].Height = Math.Max(3, _bars[i]);
            }
        }

        if (_flash != null)
        {
            if (now < _flashUntil) StatusText.Text = _flash;
            else { _flash = null; UpdateTexts(); }
        }
    }

    /// <summary>
    /// Se cache tout seul pendant un jeu / une vidéo en plein écran, ou quand aucun iPhone
    /// n'est connecté (si cette option est choisie), puis revient.
    /// </summary>
    private void UpdateAutoVisibility()
    {
        var now = DateTime.UtcNow;
        if (now < _nextVisibilityCheck) return;
        _nextVisibilityCheck = now.AddMilliseconds(330);

        var s = _app.Settings;
        bool hide = (s.HideInFullscreen && Lucie.Native.Win32.IsFullscreenAppOnSameMonitor(_hwnd))
                    || (s.OnlyWhenConnected && !_app.IsConnected);
        if (hide == _hiddenAuto) return;
        _hiddenAuto = hide;
        if (hide) AppWindow.Hide();
        else AppWindow.Show(false);
    }

    private static string Format(double secs)
    {
        if (secs <= 0 || double.IsNaN(secs)) return "0:00";
        var t = TimeSpan.FromSeconds(secs);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>Affiche un message court dans la ligne d'état du widget.</summary>
    public void ShowMessage(string text) => Flash(text);

    private void Flash(string text)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(2.5);
        StatusText.Text = text;
    }

    // ------------------------------------------------------------------ actions

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_app.Settings.LockPosition || _resizing) return;
        if (e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            OndeWin32.BeginDrag(_hwnd);
    }

    // --- poignée de redimensionnement ---
    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ResizeGrip).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (!OndeWin32.GetCursorPos(out _resizeStart)) return;
        _resizeStartSize = AppWindow.Size;
        _resizing = ResizeGrip.CapturePointer(e.Pointer);
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing || !OndeWin32.GetCursorPos(out var p)) return;
        e.Handled = true;
        double scale = OndeWin32.GetScale(_hwnd);
        int w = (int)Math.Clamp(_resizeStartSize.Width + (p.X - _resizeStart.X), AirPlaySettings.MinWidth * scale, AirPlaySettings.MaxWidth * scale);
        int h = (int)Math.Clamp(_resizeStartSize.Height + (p.Y - _resizeStart.Y), AirPlaySettings.MinHeight * scale, AirPlaySettings.MaxHeight * scale);
        AppWindow.Resize(new SizeInt32(w, h));
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        ResizeGrip.ReleasePointerCapture(e.Pointer);
        EndResize();
        e.Handled = true;
    }

    private void Grip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndResize();

    private void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        double scale = OndeWin32.GetScale(_hwnd);
        _app.Settings.WidgetWidth = (int)Math.Round(AppWindow.Size.Width / scale);
        _app.Settings.WidgetHeight = (int)Math.Round(AppWindow.Size.Height / scale);
        _app.SaveSoon();
        _app.NotifySizeChangedByWidget();
    }

    private async Task Send(RemoteCommand cmd)
    {
        if (!_app.IsConnected) { Flash("Aucun iPhone connecté"); return; }
        if (!await _app.SendAsync(cmd)) Flash("L'iPhone ne répond pas à la commande");
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await Send(RemoteCommand.Previous);
    private async void Next_Click(object sender, RoutedEventArgs e) => await Send(RemoteCommand.Next);
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await Send(RemoteCommand.PlayPause);

    private void Eq_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        _app.SetEqEnabled(!s.EqEnabled);
        Flash(s.EqEnabled ? $"Égaliseur : {s.EqPreset}" : "Égaliseur désactivé");
    }

    private void Concert_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        _app.SetConcertEnabled(!s.ConcertEnabled);
        Flash(s.ConcertEnabled ? $"Mode concert : {s.ConcertPreset}" : "Mode concert désactivé");
    }

    private void Effects_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        App.Current.OpenSettings("airplay", sender == ConcertButton ? 4 : 3);
        e.Handled = true;
    }

    private void Volume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(_app.Settings.LocalVolume * 100 - e.NewValue) < 0.5) return;
        _app.SetVolume(e.NewValue / 100.0);
        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon()
    {
        double v = _app.Settings.LocalVolume;
        VolumeIcon.Glyph = v <= 0 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.Current.OpenSettings("airplay", 0);
    private void Minimize_Click(object sender, RoutedEventArgs e) => _presenter.Minimize();
    /// <summary>Masque le widget : l'enceinte AirPlay reste allumée.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => App.Current.SetAirPlayWidgetVisible(false);
}
