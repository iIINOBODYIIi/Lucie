using System.Runtime.InteropServices;
using Lucie.AirPlay;
using Lucie.Services;
using Lucie.Views;
using Microsoft.UI.Dispatching;
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
    /// <summary>L'enceinte AirPlay (le moteur d'Onde).</summary>
    public static AirPlayService AirPlay { get; private set; } = null!;
    /// <summary>Connexion directe à Discord (micro, sourdine, vocal).</summary>
    public static DiscordService Discord { get; private set; } = null!;
    /// <summary>Statut « Écoute » sur le profil Discord.</summary>
    public static DiscordPresence Presence { get; private set; } = null!;

    public static new App Current => (App)Application.Current;
    public static SolidColorBrush Accent => (SolidColorBrush)Application.Current.Resources["LucieAccent"];

    private static Mutex? _singleInstance;
    private static bool _reported;
    private WidgetWindow? _widget;
    private SettingsWindow? _settingsWindow;
    private AirPlayWindow? _airPlayWindow;
    private DispatcherQueue? _dispatcher;

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
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            Discord = new DiscordService();
            if (Discord.Enabled && Discord.IsAuthorized) _ = Discord.EnsureConnectedAsync();   // en arrière-plan

            // Enceinte AirPlay : allumée seulement si on l'a activée dans les paramètres
            AirPlay = new AirPlayService();
            AirPlay.RunningChanged += () => _dispatcher.TryEnqueue(UpdateAirPlayWidget);
            if (AirPlay.Settings.Enabled)
            {
                // L'ancienne appli Onde lancée avec Windows gênerait l'enceinte de Lucie.
                if (AirPlayService.IsOndeRunning()) AirPlayService.CloseOnde();
                AirPlay.Start();
            }

            _widget = new WidgetWindow();
            _widget.Activate();

            UpdateAirPlayWidget();

            await Media.InitializeAsync();
            Presence = new DiscordPresence(Media, Discord);
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

    public void OpenSettings() => OpenSettings(null);

    /// <summary>Ouvre les paramètres, éventuellement sur une section (« airplay ») et un onglet AirPlay.</summary>
    public void OpenSettings(string? section, int airPlayTab = 0)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        if (section is not null) _settingsWindow.ShowSection(section, airPlayTab);
        _settingsWindow.Activate();
    }

    // ───────────── Widget AirPlay ─────────────

    /// <summary>Affiche le widget AirPlay si l'enceinte est allumée et le widget demandé, sinon le ferme.</summary>
    public void UpdateAirPlayWidget()
    {
        bool want = AirPlay.IsRunning && AirPlay.Settings.ShowWidget;
        if (want && _airPlayWindow is null)
        {
            _airPlayWindow = new AirPlayWindow();
            _airPlayWindow.Closed += (_, _) => _airPlayWindow = null;
            _airPlayWindow.Activate();
        }
        else if (!want && _airPlayWindow is not null)
        {
            var w = _airPlayWindow;
            _airPlayWindow = null;
            w.Close();
        }
    }

    public void SetAirPlayWidgetVisible(bool visible)
    {
        AirPlay.Settings.ShowWidget = visible;
        AirPlay.SaveSoon();
        UpdateAirPlayWidget();
    }

    public void Quit()
    {
        Speech?.Dispose();
        Presence?.Dispose();
        Discord?.Dispose();   // fermer le tube efface aussi le statut « Écoute »
        try { _airPlayWindow?.Close(); } catch { }
        AirPlay?.Dispose();
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
