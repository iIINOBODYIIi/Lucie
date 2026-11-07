using System.Collections.ObjectModel;
using Lucie.Models;
using Lucie.Native;
using Lucie.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Lucie.Views;

public sealed partial class SettingsWindow : Window
{
    private const string DefaultDevice = "Par défaut (Windows)";

    private readonly ObservableCollection<VoiceCommand> _commands = new();
    private readonly ObservableCollection<AppEntry> _apps = new();
    private readonly IntPtr _hwnd;
    private WidgetPosition _position;
    private string _accent = "#9AB8FF";

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Lucie — Paramètres";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        _hwnd = WindowNative.GetWindowHandle(this);
        double scale = Win32.ScaleFor(_hwnd);
        AppWindow.Resize(new SizeInt32((int)(1060 * scale), (int)(780 * scale)));
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "LucieIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        LoadValues();
        Nav.SelectedItem = Nav.MenuItems[0];

        App.Speech.LevelChanged += OnLevel;
        App.Speech.Heard += OnHeard;
        App.Settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            App.Settings.Changed -= OnSettingsChanged;
            // Rien n'est perdu : fermer la fenêtre enregistre automatiquement.
            SaveAll(fromClose: true);
            App.Speech.LevelChanged -= OnLevel;
            App.Speech.Heard -= OnHeard;
        };
    }

    private void LoadValues()
    {
        var s = App.Settings.Current;

        ApplyBackdrop(s.SettingsTransparent);

        // Général
        NameBox.Text = s.AssistantName;
        StartupSwitch.IsOn = s.StartWithWindows;
        ListenRadios.SelectedIndex = (int)s.ListenMode;
        HotkeyBox.Text = s.TalkHotkey;
        ModelInfo.IsOpen = !App.Speech.ModelAvailable;

        // Audio
        FillDevices(MicBox, AudioDevices.Inputs(), s.MicrophoneName);
        FillDevices(OutBox, AudioDevices.Outputs(), s.OutputName);

        // Widget
        _position = s.Position;
        RefreshPositionButtons();
        SizeRadios.SelectedIndex = (int)s.Size;
        TransparencySlider.Value = s.WidgetTransparency;
        AutoPlayerSwitch.IsOn = s.AutoPlayer;
        DisplayRadios.SelectedIndex = s.WidgetHidden ? 2 : s.AlwaysOnTop ? 0 : 1;
        FullscreenSwitch.IsOn = s.HideInFullscreen;
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
        if (App.Speech.LastHeard.Length > 0) LastHeardText.Text = $"« {App.Speech.LastHeard} »";

        // Applications
        _apps.Clear();
        foreach (var a in s.Apps) _apps.Add(a.Clone());
        AppList.ItemsSource = _apps;
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
        SecApps.Visibility = Show(tag == "apps");
        SecCommands.Visibility = Show(tag == "commands");
    }

    private static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    // ───────────── Général ─────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Lucie" : NameBox.Text.Trim();
        WakeHint.Text = $"Dis « Salut {name} » pour l'activer. Astuce : choisis un prénom courant, il sera mieux reconnu.";
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadText.Text = "Téléchargement en cours…";

        var progress = new Progress<double>(p =>
        {
            DownloadProgress.IsIndeterminate = p < 0;
            if (p >= 0)
            {
                DownloadProgress.Value = p;
                DownloadText.Text = $"Téléchargement… {p:P0}";
            }
            else
            {
                DownloadText.Text = "Installation…";
            }
        });

        try
        {
            await ModelDownloader.DownloadAsync(progress);
            App.Speech.Start();
            ModelInfo.IsOpen = !App.Speech.ModelAvailable;
            DownloadText.Text = App.Speech.ModelAvailable ? "C'est prêt ! Dis « Salut " + App.Settings.Current.AssistantName + " »." : "Modèle installé, mais impossible de le charger.";
        }
        catch (Exception ex)
        {
            DownloadText.Text = ex.Message + " — tu peux aussi l'installer à la main (voir README).";
            DownloadButton.IsEnabled = true;
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ───────────── Audio ─────────────

    private void OnLevel(double level) => DispatcherQueue.TryEnqueue(() => LevelBar.Value = level);

    /// <summary>Le widget a été rangé / ressorti depuis l'icône : on met l'option à jour.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        var s = App.Settings.Current;
        if (s.WidgetHidden && DisplayRadios.SelectedIndex != 2) DisplayRadios.SelectedIndex = 2;
        else if (!s.WidgetHidden && DisplayRadios.SelectedIndex == 2) DisplayRadios.SelectedIndex = s.AlwaysOnTop ? 0 : 1;
    });

    private void OnHeard(string text) => DispatcherQueue.TryEnqueue(() => LastHeardText.Text = $"« {text} »");

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
        _commands.Add(new VoiceCommand { Phrase = "", Action = CommandAction.MediaPlayPause });

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VoiceCommand c })
            _commands.Remove(c);
    }

    private async void TestCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VoiceCommand c } button) return;
        SaveAll(fromClose: false);
        await RunTestAsync(button, () => App.Actions.ExecuteAsync(c), c.Action == CommandAction.Shortcut);
    }

    // ───────────── Applications et raccourcis ─────────────

    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickExeAsync();
        if (path is null) return;
        foreach (var a in _apps) a.IsExpanded = false;
        _apps.Add(new AppEntry { Name = FriendlyName(path), Path = path, FocusBeforeShortcut = true, IsExpanded = true });
    }

    private async void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AppEntry app }) return;
        var path = await PickExeAsync();
        if (path is null) return;

        app.Path = path;
        app.Arguments = "";
        app.ProcessName = "";
        if (string.IsNullOrWhiteSpace(app.Name)) app.Name = FriendlyName(path);
    }

    private void DeleteApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppEntry app })
            _apps.Remove(app);
    }

    private void AddAppShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppEntry app })
            app.Shortcuts.Add(new AppShortcut());
    }

    private void DeleteAppShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AppShortcut sc }) return;
        OwnerOf(sc)?.Shortcuts.Remove(sc);
    }

    private async void TestAppShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppShortcut sc } button) return;
        var app = OwnerOf(sc);
        if (app is null) return;
        SaveAll(fromClose: false);
        await RunTestAsync(button, () => App.Actions.RunAppShortcutAsync(app, sc.Keys), !app.FocusBeforeShortcut);
    }

    private AppEntry? OwnerOf(AppShortcut sc) => _apps.FirstOrDefault(a => a.Shortcuts.Contains(sc));

    /// <summary>
    /// Lance un test et affiche le résultat sur le bouton. Pour un raccourci,
    /// on attend 2 secondes : le temps de lâcher la souris (et que les paramètres ne gênent pas).
    /// </summary>
    private static async Task RunTestAsync(Button button, Func<Task<bool>> action, bool countdown)
    {
        var original = button.Content;
        button.IsEnabled = false;
        try
        {
            if (countdown)
            {
                for (int i = 2; i > 0; i--)
                {
                    button.Content = $"{i}…";
                    await Task.Delay(1000);
                }
            }
            bool ok = await action();
            button.Content = ok ? "✓ Envoyé" : "✗ Échec";
            if (ok) App.Sounds.PlayDone(); else App.Sounds.PlayError();
            await Task.Delay(1500);
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = true;
        }
    }

    private async Task<string?> PickExeAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");
            InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>« C:\…\Cider.exe » → « Cider » (nom affiché par le programme quand il en a un).</summary>
    private static string FriendlyName(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var name = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= 30) return name.Trim();
        }
        catch
        {
        }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    // ───────────── Enregistrer ─────────────

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAll(fromClose: false);

    private void SaveAll(bool fromClose)
    {
        var s = App.Settings.Current;

        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Lucie" : NameBox.Text.Trim();
        string? mic = SelectedDevice(MicBox);
        var mode = (ListenMode)Math.Max(0, ListenRadios.SelectedIndex);
        bool restartSpeech = name != s.AssistantName || mic != s.MicrophoneName || mode != s.ListenMode;
        s.ListenMode = mode;

        var hotkey = HotkeyBox.Text.Trim();
        if (hotkey.Length > 0 && !KeyboardSimulator.TryParse(hotkey, out _))
        {
            if (fromClose) hotkey = s.TalkHotkey;   // on garde l'ancien
            else
            {
                HotkeyHint.Text = "Raccourci non reconnu. Exemple : Ctrl+Alt+L";
                Nav.SelectedItem = Nav.MenuItems[0];
                return;
            }
        }
        s.TalkHotkey = hotkey;

        s.AssistantName = name;
        s.StartWithWindows = StartupSwitch.IsOn;
        s.MicrophoneName = mic;
        s.OutputName = SelectedDevice(OutBox);

        // Un choix dans la grille remplace la position « glissée à la main ».
        s.Position = _position;
        s.Size = (WidgetSize)Math.Max(0, SizeRadios.SelectedIndex);
        s.WidgetTransparency = TransparencySlider.Value;
        s.AutoPlayer = AutoPlayerSwitch.IsOn;
        int display = Math.Max(0, DisplayRadios.SelectedIndex);
        s.WidgetHidden = display == 2;
        if (display != 2) s.AlwaysOnTop = display == 0;
        s.HideInFullscreen = FullscreenSwitch.IsOn;
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

        s.Apps = _apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Path))
            .Select(a =>
            {
                var copy = a.Clone();
                foreach (var sc in copy.Shortcuts.Where(x => string.IsNullOrWhiteSpace(x.Phrase) || string.IsNullOrWhiteSpace(x.Keys)).ToList())
                    copy.Shortcuts.Remove(sc);
                return copy;
            })
            .ToList();

        App.ApplyAccent();
        App.Settings.Save();
        if (restartSpeech) App.Speech.Start();

        ModelInfo.IsOpen = !App.Speech.ModelAvailable;
        if (!fromClose) SavedText.Text = $"Enregistré à {DateTime.Now:HH:mm}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
