using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Onde.Core.Dsp;
using Windows.Graphics;
using Windows.UI;

namespace Lucie.AirPlay;

/// <summary>
/// L'onglet « AirPlay » des paramètres de Lucie : tous les réglages d'Onde
/// (enceinte, widget, apparence, égaliseur, mode concert). Les changements s'appliquent tout de suite.
/// </summary>
public sealed partial class AirPlaySettingsPanel : UserControl
{
    private readonly AirPlayService _app = App.AirPlay;
    private readonly Slider[] _bandSliders = new Slider[10];
    private readonly TextBlock[] _bandValues = new TextBlock[10];
    private readonly List<(Slider Slider, TextBlock Label, Func<ConcertSettings, double> Get, Func<ConcertSettings, double, ConcertSettings> Set, Func<double, string> Fmt, string Name)> _concert = new();
    private List<(string Id, string Name)> _devices = new();
    private bool _loading = true;
    private bool _selfChange;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private static readonly string[] Accents = { "#FF8B5CF6", "#FF3B82F6", "#FF06B6D4", "#FF10B981", "#FFF59E0B", "#FFF43F5E", "#FFEC4899", "#FFFFFFFF" };
    private static readonly string[] Backgrounds = { "#FF15131F", "#FF000000", "#FF1E293B", "#FF1F2937", "#FF2E1065", "#FF0C4A6E", "#FFF5F5F4", "#FFFFFFFF" };
    private static readonly string[] Texts = { "#FFFFFFFF", "#FFE5E7EB", "#FF111827", "#FF000000" };

    private readonly Action _onRunning, _onEffects, _onSize;

    public AirPlaySettingsPanel()
    {
        InitializeComponent();

        BuildEqualizer();
        BuildConcert();
        BuildSwatches();
        LoadValues();
        _loading = false;

        if (_app.Settings.ImportedFromOnde) ImportInfo.IsOpen = true;

        _onRunning = () => DispatcherQueue.TryEnqueue(UpdateStatus);
        _onEffects = () => DispatcherQueue.TryEnqueue(() => { if (!_selfChange) ReloadEffects(); });
        _onSize = () => DispatcherQueue.TryEnqueue(SyncSize);
        _app.RunningChanged += _onRunning;
        _app.ReceiverStateChanged += _onRunning;
        _app.TrackChanged += OnTrack;
        _app.EffectsChanged += _onEffects;
        _app.SizeChangedByWidget += _onSize;
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        Unloaded += (_, _) =>
        {
            _statusTimer.Stop();
            _app.RunningChanged -= _onRunning;
            _app.ReceiverStateChanged -= _onRunning;
            _app.TrackChanged -= OnTrack;
            _app.EffectsChanged -= _onEffects;
            _app.SizeChangedByWidget -= _onSize;
        };
        UpdateStatus();
    }

    private void OnTrack(Onde.Core.AirPlay.TrackInfo _) => DispatcherQueue.TryEnqueue(UpdateStatus);

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
    }

    private AirPlaySettings S => _app.Settings;

    private void Changed()
    {
        _app.ApplyAudioSettings();
        _selfChange = true;
        _app.NotifyEffectsChanged();
        _selfChange = false;
        _app.SaveSoon();
    }

    /// <summary>Un réglage d'effet a changé ailleurs (voix, widget) : on met les curseurs à jour.</summary>
    private void ReloadEffects()
    {
        bool was = _loading; _loading = true;
        EqToggle.IsOn = S.EqEnabled;
        EqPresetBox.SelectedItem = EqPresetBox.Items.Contains(S.EqPreset) ? S.EqPreset : "Personnalisé";
        SetBandSliders();
        for (int i = 0; i < 10; i++) _bandValues[i].Text = FormatDb(S.EqGains[i]);
        PreampSlider.Value = S.EqPreamp;
        PreampLabel.Text = $"Préampli : {FormatDb(S.EqPreamp)} dB";
        ConcertToggle.IsOn = S.ConcertEnabled;
        ConcertPresetBox.SelectedItem = ConcertPresetBox.Items.Contains(S.ConcertPreset) ? S.ConcertPreset : "Personnalisé";
        SetConcertSliders();
        _loading = was;
    }

    // ================================================================ ÉGALISEUR

    private void BuildEqualizer()
    {
        for (int i = 0; i < 10; i++)
        {
            EqBands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var col = new Grid { RowSpacing = 4 };
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var value = new TextBlock { FontSize = 10, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, Text = "0" };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -Equalizer.MaxGainDb,
                Maximum = Equalizer.MaxGainDb,
                StepFrequency = 0.5,
                TickFrequency = 6,
                TickPlacement = TickPlacement.Outside,
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = i,
            };
            slider.ValueChanged += Band_Changed;
            var label = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Text = Equalizer.Labels[i] };

            Grid.SetRow(slider, 1);
            Grid.SetRow(label, 2);
            col.Children.Add(value); col.Children.Add(slider); col.Children.Add(label);
            Grid.SetColumn(col, i);
            EqBands.Children.Add(col);
            _bandSliders[i] = slider;
            _bandValues[i] = value;
        }

        foreach (var p in EqPresets.All) EqPresetBox.Items.Add(p.Name);
        EqPresetBox.Items.Add("Personnalisé");
    }

    private void Band_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        int band = (int)((Slider)sender).Tag;
        _bandValues[band].Text = FormatDb(e.NewValue);
        if (_loading) return;
        S.EqGains[band] = e.NewValue;
        if (!S.EqEnabled) { S.EqEnabled = true; _loading = true; EqToggle.IsOn = true; _loading = false; }
        S.EqPreset = "Personnalisé";
        _loading = true; EqPresetBox.SelectedItem = "Personnalisé"; _loading = false;
        Changed();
    }

    private void EqPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EqPresetBox.SelectedItem is not string name) return;
        var preset = EqPresets.All.FirstOrDefault(p => p.Name == name);
        if (preset.Gains == null) return;
        S.EqPreset = name;
        S.EqGains = (double[])preset.Gains.Clone();
        if (!S.EqEnabled) { S.EqEnabled = true; EqToggle.IsOn = true; }
        SetBandSliders();
        Changed();
    }

    private void EqReset_Click(object sender, RoutedEventArgs e)
    {
        S.EqGains = new double[10];
        S.EqPreamp = 0;
        S.EqPreset = "Plat";
        _loading = true;
        EqPresetBox.SelectedItem = "Plat";
        PreampSlider.Value = 0;
        _loading = false;
        SetBandSliders();
        Changed();
    }

    private void SetBandSliders()
    {
        bool was = _loading; _loading = true;
        for (int i = 0; i < 10; i++) _bandSliders[i].Value = S.EqGains[i];
        _loading = was;
    }

    private void EqToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.EqEnabled = EqToggle.IsOn;
        Changed();
    }

    private void Preamp_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        SetText(PreampLabel, $"Préampli : {FormatDb(e.NewValue)} dB");
        if (_loading) return;
        S.EqPreamp = e.NewValue;
        Changed();
    }

    /// <summary>Les curseurs déclenchent leurs événements pendant la création de la fenêtre,
    /// parfois avant que leur étiquette existe : on ignore alors la mise à jour.</summary>
    private static void SetText(TextBlock? t, string text)
    {
        if (t != null) t.Text = text;
    }

    private static string FormatDb(double v) => v > 0 ? $"+{v:0.#}" : $"{v:0.#}";

    // ================================================================ CONCERT

    private void BuildConcert()
    {
        AddConcert("Taille de la salle", 0, 100, s => s.RoomSize * 100, (s, v) => s with { RoomSize = v / 100 }, v => $"{v:0} %");
        AddConcert("Amortissement (salle mate ↔ brillante)", 0, 100, s => s.Damping * 100, (s, v) => s with { Damping = v / 100 }, v => $"{v:0} %");
        AddConcert("Réverbération", 0, 100, s => s.ReverbMix * 100, (s, v) => s with { ReverbMix = v / 100 }, v => $"{v:0} %");
        AddConcert("Délai de l'écho", 0, 900, s => s.EchoDelayMs, (s, v) => s with { EchoDelayMs = v }, v => v < 1 ? "désactivé" : $"{v:0} ms");
        AddConcert("Répétitions de l'écho", 0, 90, s => s.EchoFeedback * 100, (s, v) => s with { EchoFeedback = v / 100 }, v => $"{v:0} %");
        AddConcert("Volume de l'écho", 0, 100, s => s.EchoMix * 100, (s, v) => s with { EchoMix = v / 100 }, v => $"{v:0} %");
        AddConcert("Largeur stéréo", 0, 100, s => s.Width * 100, (s, v) => s with { Width = v / 100 }, v => $"{v:0} %");

        foreach (var p in ConcertPresets.All) ConcertPresetBox.Items.Add(p.Name);
        ConcertPresetBox.Items.Add("Personnalisé");
    }

    private void AddConcert(string name, double min, double max, Func<ConcertSettings, double> get,
        Func<ConcertSettings, double, ConcertSettings> set, Func<double, string> fmt)
    {
        var label = new TextBlock { Text = name };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 1 };
        int index = _concert.Count;
        slider.ValueChanged += (_, e) =>
        {
            var c = _concert[index];
            c.Label.Text = $"{c.Name} : {c.Fmt(e.NewValue)}";
            if (_loading) return;
            S.Concert = c.Set(S.Concert, e.NewValue);
            S.ConcertPreset = "Personnalisé";
            if (!S.ConcertEnabled) { S.ConcertEnabled = true; _loading = true; ConcertToggle.IsOn = true; _loading = false; }
            _loading = true; ConcertPresetBox.SelectedItem = "Personnalisé"; _loading = false;
            Changed();
        };
        ConcertSliders.Children.Add(label);
        ConcertSliders.Children.Add(slider);
        _concert.Add((slider, label, get, set, fmt, name));
    }

    private void SetConcertSliders()
    {
        bool was = _loading; _loading = true;
        foreach (var c in _concert)
        {
            c.Slider.Value = c.Get(S.Concert);
            c.Label.Text = $"{c.Name} : {c.Fmt(c.Slider.Value)}";
        }
        _loading = was;
    }

    private void ConcertPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ConcertPresetBox.SelectedItem is not string name) return;
        var preset = ConcertPresets.All.FirstOrDefault(p => p.Name == name);
        if (preset.Settings == null) return;
        S.ConcertPreset = name;
        S.Concert = preset.Settings;
        if (!S.ConcertEnabled) { S.ConcertEnabled = true; ConcertToggle.IsOn = true; }
        SetConcertSliders();
        Changed();
    }

    private void ConcertToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ConcertEnabled = ConcertToggle.IsOn;
        Changed();
    }

    // ================================================================ APPARENCE

    private void BuildSwatches()
    {
        foreach (var c in Accents) AccentSwatches.Children.Add(Swatch(c, v => S.AccentColor = v));
        foreach (var c in Backgrounds) BackgroundSwatches.Children.Add(Swatch(c, v => S.BackgroundColor = v));
        foreach (var c in Texts) TextSwatches.Children.Add(Swatch(c, v => S.TextColor = v));
    }

    private Button Swatch(string hex, Action<string> apply)
    {
        var b = new Button
        {
            Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(AirPlaySettings.ParseColor(hex, Colors.Gray)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(80, 128, 128, 128)),
            BorderThickness = new Thickness(1),
        };
        ToolTipService.SetToolTip(b, hex);
        b.Click += (_, _) =>
        {
            apply(hex);
            AppearanceChanged();
            SyncPicker();
        };
        return b;
    }

    private void CustomTarget_Changed(object sender, SelectionChangedEventArgs e) => SyncPicker();

    private void SyncPicker()
    {
        if (Picker == null || CustomTarget == null) return; // fenêtre encore en construction
        bool was = _loading; _loading = true;
        string hex = CustomTarget.SelectedIndex switch { 1 => S.BackgroundColor, 2 => S.TextColor, _ => S.AccentColor };
        Picker.Color = AirPlaySettings.ParseColor(hex, Colors.White);
        _loading = was;
    }

    private void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;
        string hex = AirPlaySettings.ToHex(args.NewColor);
        switch (CustomTarget.SelectedIndex)
        {
            case 1: S.BackgroundColor = hex; break;
            case 2: S.TextColor = hex; break;
            default: S.AccentColor = hex; break;
        }
        AppearanceChanged();
    }

    private void Backdrop_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackdropBox.SelectedIndex < 0) return;
        S.Backdrop = (BackdropMode)BackdropBox.SelectedIndex;
        OpacitySlider.IsEnabled = S.Backdrop != BackdropMode.Solid;
        AppearanceChanged();
    }

    private void Opacity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        SetText(OpacityLabel, $"Opacité du fond : {e.NewValue:0} %");
        if (_loading) return;
        S.BackgroundOpacity = e.NewValue / 100;
        AppearanceChanged();
    }

    private void Radius_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        SetText(RadiusLabel, $"Arrondi des coins : {e.NewValue:0} px");
        if (_loading) return;
        S.CornerRadius = e.NewValue;
        AppearanceChanged();
    }

    private void ContentOpacity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        SetText(ContentOpacityLabel, $"Opacité du contenu : {e.NewValue:0} %");
        if (_loading) return;
        S.ContentOpacity = e.NewValue / 100;
        AppearanceChanged();
    }

    /// <summary>Fond transparent à 0 % + contenu légèrement translucide.</summary>
    private void MakeTransparent_Click(object sender, RoutedEventArgs e)
    {
        S.Backdrop = BackdropMode.Transparent;
        S.BackgroundOpacity = 0;
        S.ContentOpacity = 0.85;
        S.ArtworkBackground = false;
        _loading = true;
        BackdropBox.SelectedIndex = (int)BackdropMode.Transparent;
        OpacitySlider.IsEnabled = true;
        OpacitySlider.Value = 0;
        ContentOpacitySlider.Value = 85;
        ArtworkToggle.IsOn = false;
        _loading = false;
        AppearanceChanged();
    }

    private void SizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        S.WidgetWidth = int.Parse(parts[0]);
        S.WidgetHeight = int.Parse(parts[1]);
        SyncSize();
        _app.RequestResize();
        _app.SaveSoon();
    }

    private void Size_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (WidthSlider == null || HeightSlider == null) return; // fenêtre encore en construction
        SetText(WidthLabel, $"Largeur : {WidthSlider.Value:0} px");
        SetText(HeightLabel, $"Hauteur : {HeightSlider.Value:0} px");
        if (_loading) return;
        S.WidgetWidth = (int)WidthSlider.Value;
        S.WidgetHeight = (int)HeightSlider.Value;
        _app.RequestResize();
        _app.SaveSoon();
    }

    /// <summary>Met les curseurs à jour quand la taille change depuis le widget.</summary>
    public void SyncSize()
    {
        bool was = _loading; _loading = true;
        WidthSlider.Value = S.WidgetWidth;
        HeightSlider.Value = S.WidgetHeight;
        WidthLabel.Text = $"Largeur : {S.WidgetWidth} px";
        HeightLabel.Text = $"Hauteur : {S.WidgetHeight} px";
        _loading = was;
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeBox.SelectedIndex < 0) return;
        S.Mode = (WidgetMode)ModeBox.SelectedIndex;
        if (S.Mode == WidgetMode.Desktop && S.ShowInTaskbar)
        {
            // un élément du bureau n'a rien à faire dans la barre des tâches
            S.ShowInTaskbar = false;
            _loading = true; TaskbarToggle.IsOn = false; _loading = false;
        }
        AppearanceChanged();
    }

    private void Appearance_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ArtworkBackground = ArtworkToggle.IsOn;
        S.ShowVisualizer = VisualizerToggle.IsOn;
        S.LockPosition = LockToggle.IsOn;
        S.ShowInTaskbar = TaskbarToggle.IsOn;
        AppearanceChanged();
    }

    private void AppearanceChanged()
    {
        _app.NotifyAppearanceChanged();
        _app.SaveSoon();
    }

    // ================================================================ GÉNÉRAL

    private void ApplyName_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        _app.Rename(name);
        UpdateStatus();
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeviceBox.SelectedIndex < 0) return;
        _app.SetOutputDevice(DeviceBox.SelectedIndex == 0 ? null : _devices[DeviceBox.SelectedIndex - 1].Id);
    }

    private void Buffer_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        SetText(BufferLabel, $"Mémoire tampon : {e.NewValue:0} ms");
        if (_loading) return;
        S.BufferMs = (int)e.NewValue;
        Changed();
    }

    private void Media_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetMediaIntegration(MediaToggle.IsOn);
    }

    // ================================================================ ENCEINTE / WIDGET (Lucie)

    private void Enable_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (EnableToggle.IsOn && AirPlayService.IsOndeRunning()) AirPlayService.CloseOnde();
        _app.SetEnabled(EnableToggle.IsOn);
        UpdateStatus();
    }

    private void CloseOnde_Click(object sender, RoutedEventArgs e)
    {
        AirPlayService.CloseOnde();
        // Onde parti : on relance l'enceinte de Lucie pour récupérer le nom sur le réseau
        if (_app.IsRunning)
        {
            _app.Stop();
            _app.Start();
        }
        UpdateStatus();
    }

    private async void Firewall_Click(object sender, RoutedEventArgs e)
    {
        FirewallText.Text = "Windows va te demander l'autorisation…";
        bool ok = await Task.Run(AirPlayService.AddFirewallRule);
        FirewallText.Text = ok ? "C'est fait ✓" : "Pas autorisé (refusé ou annulé).";
    }

    private void ShowWidget_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Current.SetAirPlayWidgetVisible(ShowWidgetToggle.IsOn);
    }

    private void WidgetOptions_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.HideInFullscreen = FullscreenToggle.IsOn;
        S.OnlyWhenConnected = OnlyConnectedToggle.IsOn;
        _app.SaveSoon();
    }

    /// <summary>Ligne d'état : éteinte, en attente, connectée, en lecture…</summary>
    private void UpdateStatus()
    {
        bool was = _loading; _loading = true;
        EnableToggle.IsOn = _app.IsRunning;
        ShowWidgetToggle.IsOn = S.ShowWidget;
        _loading = was;

        OndeWarning.IsOpen = AirPlayService.IsOndeRunning();

        Windows.UI.Color dot;
        string text;
        if (!_app.IsRunning)
        {
            dot = ColorHelper.FromArgb(255, 156, 163, 175);
            text = "Éteinte — allume-la pour que ton iPhone voie le PC";
        }
        else if (_app.StartupError is { } err)
        {
            dot = ColorHelper.FromArgb(255, 239, 68, 68);
            text = err;
        }
        else if (!_app.IsConnected)
        {
            dot = ColorHelper.FromArgb(255, 245, 158, 11);
            text = $"Prête · visible sur l'iPhone sous « {S.ReceiverName} »";
        }
        else
        {
            dot = ColorHelper.FromArgb(255, 52, 211, 153);
            var client = string.IsNullOrEmpty(_app.Receiver?.ClientName) ? "iPhone" : _app.Receiver!.ClientName;
            var t = _app.Track;
            text = string.IsNullOrWhiteSpace(t.Title)
                ? $"Connectée à {client}"
                : $"Connectée à {client} · {t.Title}" + (string.IsNullOrWhiteSpace(t.Artist) ? "" : $" — {t.Artist}");
        }
        StatusDot.Fill = new SolidColorBrush(dot);
        StatusLine.Text = text;

        var ver = typeof(App).Assembly.GetName().Version;
        InfoText.Text = $"Lucie {ver?.ToString(3)} · port AirPlay {(_app.IsRunning ? _app.Receiver?.Port.ToString() : "—")}"
            + (_app.Output?.LastError is { } audioErr ? $" · Audio : {audioErr}" : "");
    }

    // ================================================================ chargement

    private void LoadValues()
    {
        EqToggle.IsOn = S.EqEnabled;
        EqPresetBox.SelectedItem = EqPresetBox.Items.Contains(S.EqPreset) ? S.EqPreset : "Personnalisé";
        SetBandSliders();
        for (int i = 0; i < 10; i++) _bandValues[i].Text = FormatDb(S.EqGains[i]);
        PreampSlider.Value = S.EqPreamp;
        PreampLabel.Text = $"Préampli : {FormatDb(S.EqPreamp)} dB";

        ConcertToggle.IsOn = S.ConcertEnabled;
        ConcertPresetBox.SelectedItem = ConcertPresetBox.Items.Contains(S.ConcertPreset) ? S.ConcertPreset : "Personnalisé";
        SetConcertSliders();

        ShowWidgetToggle.IsOn = S.ShowWidget;
        FullscreenToggle.IsOn = S.HideInFullscreen;
        OnlyConnectedToggle.IsOn = S.OnlyWhenConnected;
        BackdropBox.SelectedIndex = (int)S.Backdrop;
        OpacitySlider.Value = Math.Round(S.BackgroundOpacity * 100);
        OpacitySlider.IsEnabled = S.Backdrop != BackdropMode.Solid;
        OpacityLabel.Text = $"Opacité du fond : {OpacitySlider.Value:0} %";
        RadiusSlider.Value = S.CornerRadius;
        RadiusLabel.Text = $"Arrondi des coins : {S.CornerRadius:0} px";
        ContentOpacitySlider.Value = Math.Round(S.ContentOpacity * 100);
        ContentOpacityLabel.Text = $"Opacité du contenu : {ContentOpacitySlider.Value:0} %";
        SyncSize();
        ModeBox.SelectedIndex = (int)S.Mode;
        LockToggle.IsOn = S.LockPosition;
        ArtworkToggle.IsOn = S.ArtworkBackground;
        VisualizerToggle.IsOn = S.ShowVisualizer;
        TaskbarToggle.IsOn = S.ShowInTaskbar;
        SyncPicker();

        NameBox.Text = S.ReceiverName;
        _devices = AudioOutput.ListDevices();
        DeviceBox.Items.Add("Par défaut (Windows)");
        foreach (var d in _devices) DeviceBox.Items.Add(d.Name);
        int idx = _devices.FindIndex(d => d.Id == S.OutputDeviceId);
        DeviceBox.SelectedIndex = idx >= 0 ? idx + 1 : 0;
        BufferSlider.Value = S.BufferMs;
        BufferLabel.Text = $"Mémoire tampon : {S.BufferMs} ms";
        MediaToggle.IsOn = S.SystemMediaIntegration;

    }
}
