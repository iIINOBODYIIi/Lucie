using Lucie.Services;
using Lucie.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Lucie;

public partial class App : Application
{
    public static SettingsService Settings { get; } = new();
    public static SoundService Sounds { get; private set; } = null!;
    public static MediaService Media { get; private set; } = null!;
    public static SpeechService Speech { get; private set; } = null!;
    public static ActionExecutor Actions { get; private set; } = null!;
    public static CommandInterpreter Interpreter { get; } = new();

    public static new App Current => (App)Application.Current;
    public static SolidColorBrush Accent => (SolidColorBrush)Application.Current.Resources["LucieAccent"];

    private static Mutex? _singleInstance;
    private WidgetWindow? _widget;
    private SettingsWindow? _settingsWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Une seule Lucie à la fois.
        _singleInstance = new Mutex(true, "Lucie.Assistant.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        Settings.Load();
        ApplyAccent();

        Sounds = new SoundService(Settings);
        Media = new MediaService();
        Actions = new ActionExecutor(Media);
        Speech = new SpeechService(Settings);

        _widget = new WidgetWindow();
        _widget.Activate();

        await Media.InitializeAsync();
        Speech.Start();

        // Premier lancement sans modèle vocal : on ouvre les paramètres pour expliquer quoi faire.
        if (!Speech.ModelAvailable) OpenSettings();
    }

    public static void ApplyAccent()
    {
        if (ColorUtil.TryParse(Settings.Current.AccentColor, out var color))
            Accent.Color = color;
    }

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    public void Quit()
    {
        Speech?.Dispose();
        _settingsWindow?.Close();
        _widget?.Close();
        Exit();
    }
}
