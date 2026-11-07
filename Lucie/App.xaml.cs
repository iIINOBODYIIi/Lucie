using System.Runtime.InteropServices;
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
    private static bool _reported;
    private WidgetWindow? _widget;
    private SettingsWindow? _settingsWindow;

    public App()
    {
        // Si Lucie plante, on affiche et on enregistre la vraie raison au lieu de disparaître en silence.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, null);
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception, null); e.SetObserved(); };
        UnhandledException += (_, e) => Report(e.Exception, e.Message);

        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
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
        catch (Exception ex)
        {
            Report(ex, null);
            Exit();
        }
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

    // ───────────── Rapport de plantage ─────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void Report(Exception? ex, string? xamlMessage)
    {
        if (_reported) return;
        _reported = true;

        var text = "Lucie a rencontré une erreur au démarrage.\n\n";
        if (!string.IsNullOrWhiteSpace(xamlMessage)) text += xamlMessage + "\n\n";
        text += ex?.ToString() ?? "(aucun détail)";

        string file = Path.Combine(SettingsService.DataDir, "erreur.txt");
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            File.WriteAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{text}");
        }
        catch
        {
        }

        MessageBoxW(IntPtr.Zero, text + $"\n\n(Copie enregistrée dans {file})", "Lucie — erreur", 0x10);
    }
}
