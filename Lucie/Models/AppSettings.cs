using System.Text.Json.Serialization;

namespace Lucie.Models;

public enum WidgetPosition
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
    Custom
}

public enum WidgetSize { Small, Medium, Large }

public enum SoundTheme { Doux, Cristal, Bulle }

public enum ListenMode
{
    Always,        // écoute en permanence le mot d'activation (« Salut Lucie »)
    OnActivation   // micro coupé : seulement après un clic ou le raccourci clavier
}

// L'ordre doit correspondre à la liste déroulante des paramètres.
public enum CommandAction
{
    OpenApp,        // ouvrir un lien, un fichier ou un programme
    Shortcut,       // « taper » un raccourci clavier (Discord, Game Bar…)
    Screenshot,     // capture d'écran enregistrée dans Images\Lucie
    MediaPlayPause,
    MediaNext,
    MediaPrevious,
    MediaPlay,
    MediaPause,
    VolumeUp,
    VolumeDown,
    VolumeMute
}

/// <summary>
/// Une commande vocale : ce que tu dis → ce que Lucie fait.
/// Plusieurs façons de le dire peuvent être séparées par « / ».
/// </summary>
public sealed class VoiceCommand
{
    public string Phrase { get; set; } = "";
    public CommandAction Action { get; set; }
    /// <summary>Raccourci (« Ctrl+Maj+M »), chemin ou lien selon l'action.</summary>
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";

    [JsonIgnore]
    public int ActionIndex
    {
        get => (int)Action;
        set => Action = value < 0 ? CommandAction.OpenApp : (CommandAction)value;
    }

    public VoiceCommand Clone() => (VoiceCommand)MemberwiseClone();
}

/// <summary>
/// Une appli que Lucie sait ouvrir ou mettre au premier plan :
/// « Ouvre Cider », « Montre-moi Discord »…
/// </summary>
public sealed class AppEntry
{
    /// <summary>Nom à dire. Variantes possibles séparées par « / » (ex. « Cider / cidre »).</summary>
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Nom du processus à chercher s'il est déjà ouvert (vide = nom du .exe).</summary>
    public string ProcessName { get; set; } = "";

    public AppEntry Clone() => (AppEntry)MemberwiseClone();
}

public sealed class AppSettings
{
    public const int CurrentVersion = 2;
    public int Version { get; set; }   // 0 = fichier d'une ancienne version

    // Général
    public string AssistantName { get; set; } = "Lucie";
    public bool StartWithWindows { get; set; }
    public ListenMode ListenMode { get; set; } = ListenMode.Always;
    public string TalkHotkey { get; set; } = "Ctrl+Alt+L";

    // Audio (null = périphérique par défaut de Windows)
    public string? MicrophoneName { get; set; }
    public string? OutputName { get; set; }

    // Widget
    public WidgetPosition Position { get; set; } = WidgetPosition.TopRight;
    public double? CustomX { get; set; }   // centre du widget en pixels écran
    public double? CustomY { get; set; }
    /// <summary>Côté vers lequel le widget grandit quand il est posé librement : -1 gauche/haut, 0 centre, 1 droite/bas.</summary>
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public WidgetSize Size { get; set; } = WidgetSize.Medium;
    public double WidgetTransparency { get; set; } = 45;   // 0 = opaque, 90 = très transparent
    public bool AutoPlayer { get; set; } = true;
    public int SleepMinutes { get; set; } = 5;
    public int ProximityPx { get; set; } = 120;

    // Apparence
    public bool SettingsTransparent { get; set; } = true;
    public string AccentColor { get; set; } = "#9AB8FF";

    // Sons
    public bool SoundsEnabled { get; set; } = true;
    public double SoundVolume { get; set; } = 0.35;
    public SoundTheme SoundTheme { get; set; } = SoundTheme.Doux;

    public List<VoiceCommand> Commands { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();

    public static List<VoiceCommand> DefaultCommands() => new()
    {
        new() { Phrase = "lance la musique / mets la musique / reprends la musique / lecture / play", Action = CommandAction.MediaPlay },
        new() { Phrase = "mets pause / pause / arrête la musique / stop la musique", Action = CommandAction.MediaPause },
        new() { Phrase = "musique suivante / chanson suivante / suivant / passe la musique", Action = CommandAction.MediaNext },
        new() { Phrase = "musique précédente / chanson précédente / précédent", Action = CommandAction.MediaPrevious },
        new() { Phrase = "monte le son / plus fort", Action = CommandAction.VolumeUp },
        new() { Phrase = "baisse le son / moins fort", Action = CommandAction.VolumeDown },
        new() { Phrase = "coupe le son / remets le son", Action = CommandAction.VolumeMute },
        new() { Phrase = "fais une capture / capture d'écran / prends une capture", Action = CommandAction.Screenshot },
        new() { Phrase = "lance l'enregistrement / enregistre l'écran / arrête l'enregistrement", Action = CommandAction.Shortcut, Target = "Win+Alt+R" },
        new() { Phrase = "coupe mon micro / active mon micro / remets mon micro", Action = CommandAction.Shortcut, Target = "Ctrl+Maj+M" },
        new() { Phrase = "mets-moi en sourdine / enlève la sourdine", Action = CommandAction.Shortcut, Target = "Ctrl+Maj+D" },
        new() { Phrase = "ouvre le navigateur / ouvre internet / ouvre google", Action = CommandAction.OpenApp, Target = "https://www.google.com" },
    };

    public static List<AppEntry> DefaultApps() => new()
    {
        new() { Name = "Discord", Path = @"%LocalAppData%\Discord\Update.exe", Arguments = "--processStart Discord.exe", ProcessName = "Discord" },
    };

    public static AppSettings CreateDefault() => new() { Version = CurrentVersion, Commands = DefaultCommands(), Apps = DefaultApps() };

    // Phrases de la toute première version, remplacées par les nouvelles commandes.
    private static readonly HashSet<string> V1Phrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "coupe mon micro", "mets-moi en sourdine", "fais une capture", "lance l'enregistrement",
        "ouvre discord", "ouvre le navigateur", "mets pause", "musique suivante", "musique précédente"
    };

    /// <summary>Met à jour des réglages enregistrés par une version plus ancienne.</summary>
    public void Upgrade()
    {
        if (Version < 2)
        {
            var custom = (Commands ?? new()).Where(c => !V1Phrases.Contains(c.Phrase.Trim())).ToList();
            Commands = DefaultCommands();
            Commands.AddRange(custom);
            if (Apps is null || Apps.Count == 0) Apps = DefaultApps();
            Version = CurrentVersion;
        }

        Commands ??= DefaultCommands();
        Apps ??= DefaultApps();
        if (string.IsNullOrWhiteSpace(TalkHotkey)) TalkHotkey = "Ctrl+Alt+L";
    }
}
