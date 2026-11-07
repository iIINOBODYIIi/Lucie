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

public enum CommandAction
{
    OpenApp,        // ouvrir une appli, un fichier ou un lien
    Shortcut,       // « taper » un raccourci clavier (Discord, Game Bar…)
    Screenshot,     // capture d'écran enregistrée dans Images\Lucie
    MediaPlayPause,
    MediaNext,
    MediaPrevious
}

/// <summary>Une commande vocale : ce que tu dis → ce que Lucie fait.</summary>
public sealed class VoiceCommand
{
    public string Phrase { get; set; } = "";
    public CommandAction Action { get; set; }
    /// <summary>Raccourci (« Ctrl+Maj+M »), chemin d'appli ou lien selon l'action.</summary>
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

public sealed class AppSettings
{
    // Général
    public string AssistantName { get; set; } = "Lucie";
    public bool StartWithWindows { get; set; }

    // Audio (null = périphérique par défaut de Windows)
    public string? MicrophoneName { get; set; }
    public string? OutputName { get; set; }

    // Widget
    public WidgetPosition Position { get; set; } = WidgetPosition.TopRight;
    public double? CustomX { get; set; }   // centre du widget en pixels écran
    public double? CustomY { get; set; }
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

    public static List<VoiceCommand> DefaultCommands() => new()
    {
        new() { Phrase = "coupe mon micro", Action = CommandAction.Shortcut, Target = "Ctrl+Maj+M" },
        new() { Phrase = "mets-moi en sourdine", Action = CommandAction.Shortcut, Target = "Ctrl+Maj+D" },
        new() { Phrase = "fais une capture", Action = CommandAction.Screenshot },
        new() { Phrase = "lance l'enregistrement", Action = CommandAction.Shortcut, Target = "Win+Alt+R" },
        new() { Phrase = "ouvre discord", Action = CommandAction.OpenApp, Target = @"%LocalAppData%\Discord\Update.exe", Arguments = "--processStart Discord.exe" },
        new() { Phrase = "ouvre le navigateur", Action = CommandAction.OpenApp, Target = "https://www.google.com" },
        new() { Phrase = "mets pause", Action = CommandAction.MediaPlayPause },
        new() { Phrase = "musique suivante", Action = CommandAction.MediaNext },
        new() { Phrase = "musique précédente", Action = CommandAction.MediaPrevious },
    };

    public static AppSettings CreateDefault() => new() { Commands = DefaultCommands() };
}
