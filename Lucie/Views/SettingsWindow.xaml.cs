using System.Collections.ObjectModel;
using Lucie.Models;
using Lucie.Native;
using Lucie.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Lucie.Views;

public sealed partial class SettingsWindow : Window
{
    private const string DefaultDevice = "Par défaut (Windows)";

    private readonly ObservableCollection<VoiceCommand> _commands = new();
    private WidgetPosition _position;
    private string _accent = "#9AB8FF";

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Lucie — Paramètres";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        var hwnd = WindowNative.GetWindowHandle(this);
        double scale = Win32.ScaleFor(hwnd);
        AppWindow.Resize(new SizeInt32((int)(1060 * scale), (int)(780 * scale)));

        LoadValues();
        Nav.SelectedItem = Nav.MenuItems[0];

        App.Speech.LevelChanged += OnLevel;
        Closed += (_, _) => App.Speech.LevelChanged -= OnLevel;
    }

    private void LoadValues()
    {
        var s = App.Settings.Current;

        ApplyBackdrop(s.SettingsTransparent);

        // Général
        NameBox.Text = s.AssistantName;
        StartupSwitch.IsOn = s.StartWithWindows;
        ModelInfo.IsOpen = !App.Speech.ModelAvailable;
        ModelInfo.Message = $"Lucie ne peut pas encore t'entendre. Télécharge le modèle français de Vosk et décompresse-le dans : {SpeechService.PreferredModelDir}";

        // Audio
        FillDevices(MicBox, AudioDevices.Inputs(), s.MicrophoneName);
        FillDevices(OutBox, AudioDevices.Outputs(), s.OutputName);

        // Widget
        _position = s.Position;
        RefreshPositionButtons();
        SizeRadios.SelectedIndex = (int)s.Size;
        TransparencySlider.Value = s.WidgetTransparency;
        AutoPlayerSwitch.IsOn = s.AutoPlayer;
        SleepBox.Value = s.SleepMinutes;
        ProximitySlider.Value = s.ProximityPx;

        // Apparence
        TransparentSwitch.IsOn = s.SettingsTransparent;
        _accent = s.AccentColor;
        RefreshAccentButtons();

        // Sons
        SoundsSwitch.IsOn = s.SoundsEnabled;
        VolumeSlider.Value = s.SoundVolume * 100;
        ThemeRadios.SelectedIndex = (int)s.SoundTheme;

        // Commandes (on travaille sur une copie jusqu'à « Enregistrer »)
        _commands.Clear();
        foreach (var c in s.Commands) _commands.Add(c.Clone());
        CommandList.ItemsSource = _commands;
    }

    private static void FillDevices(ComboBox box, IReadOnlyList<string> devices, string? selected)
    {
        box.Items.Clear();
        box.Items.Add(DefaultDevice);
        foreach (var d in devices) box.Items.Add(d);

        int index = 0;
        for (int i = 0; i < devices.Count; i++)
            if (string.Equals(devices[i], selected, StringComparison.OrdinalIgnoreCase)) index = i + 1;
        box.SelectedIndex = index;
    }

    private static string? SelectedDevice(ComboBox box) =>
        box.SelectedIndex <= 0 ? null : box.SelectedItem as string;

    private void ApplyBackdrop(bool transparent)
    {
        SystemBackdrop = transparent ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
    }

    // ───────────── Navigation ─────────────

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "general";
        SecGeneral.Visibility = Show(tag == "general");
        SecAudio.Visibility = Show(tag == "audio");
        SecWidget.Visibility = Show(tag == "widget");
        SecLook.Visibility = Show(tag == "look");
        SecSounds.Visibility = Show(tag == "sounds");
        SecCommands.Visibility = Show(tag == "commands");
    }

    private static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    // ───────────── Général ─────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Lucie" : NameBox.Text.Trim();
        WakeHint.Text = $"Dis « Salut {name} » pour l'activer. Astuce : choisis un prénom courant, il sera mieux reconnu.";
    }

    // ───────────── Audio ─────────────

    private void OnLevel(double level) => DispatcherQueue.TryEnqueue(() => LevelBar.Value = level);

    private void TestSound_Click(object sender, RoutedEventArgs e) =>
        App.Sounds.Preview((SoundTheme)Math.Max(0, ThemeRadios.SelectedIndex), Math.Max(0.2, VolumeSlider.Value / 100), SelectedDevice(OutBox));

    // ───────────── Widget ─────────────

    private void Pos_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton b && int.TryParse(b.Tag as string, out var index))
            _position = (WidgetPosition)index;
        RefreshPositionButtons();
    }

    private void RefreshPositionButtons()
    {
        foreach (var child in PosGrid.Children.OfType<ToggleButton>())
            child.IsChecked = int.TryParse(child.Tag as string, out var i) && i == (int)_position;
    }

    // ───────────── Apparence ─────────────

    private void TransparentSwitch_Toggled(object sender, RoutedEventArgs e) => ApplyBackdrop(TransparentSwitch.IsOn);

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex)
        {
            _accent = hex;
            RefreshAccentButtons();
        }
    }

    private void RefreshAccentButtons()
    {
        foreach (var b in AccentPanel.Children.OfType<Button>())
        {
            bool selected = string.Equals(b.Tag as string, _accent, StringComparison.OrdinalIgnoreCase);
            b.BorderThickness = new Thickness(selected ? 3 : 0);
            b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
        }
    }

    // ───────────── Sons ─────────────

    private void Preview_Click(object sender, RoutedEventArgs e) => TestSound_Click(sender, e);

    // ───────────── Commandes ─────────────

    private void AddCommand_Click(object sender, RoutedEventArgs e) =>
        _commands.Add(new VoiceCommand { Phrase = "", Action = CommandAction.Shortcut });

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VoiceCommand c })
            _commands.Remove(c);
    }

    // ───────────── Enregistrer ─────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings.Current;

        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Lucie" : NameBox.Text.Trim();
        string? mic = SelectedDevice(MicBox);
        bool restartSpeech = name != s.AssistantName || mic != s.MicrophoneName;

        s.AssistantName = name;
        s.StartWithWindows = StartupSwitch.IsOn;
        s.MicrophoneName = mic;
        s.OutputName = SelectedDevice(OutBox);

        // Un choix dans la grille remplace la position « glissée à la main ».
        s.Position = _position;
        s.Size = (WidgetSize)Math.Max(0, SizeRadios.SelectedIndex);
        s.WidgetTransparency = TransparencySlider.Value;
        s.AutoPlayer = AutoPlayerSwitch.IsOn;
        s.SleepMinutes = double.IsNaN(SleepBox.Value) ? 5 : (int)Math.Clamp(SleepBox.Value, 1, 120);
        s.ProximityPx = (int)ProximitySlider.Value;

        s.SettingsTransparent = TransparentSwitch.IsOn;
        s.AccentColor = _accent;

        s.SoundsEnabled = SoundsSwitch.IsOn;
        s.SoundVolume = VolumeSlider.Value / 100;
        s.SoundTheme = (SoundTheme)Math.Max(0, ThemeRadios.SelectedIndex);

        s.Commands = _commands
            .Where(c => !string.IsNullOrWhiteSpace(c.Phrase))
            .Select(c => c.Clone())
            .ToList();

        App.ApplyAccent();
        App.Settings.Save();
        if (restartSpeech) App.Speech.Start();

        ModelInfo.IsOpen = !App.Speech.ModelAvailable;
        SavedText.Text = $"Enregistré à {DateTime.Now:HH:mm}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
