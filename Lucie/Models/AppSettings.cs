using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

/// <summary>Petite base pour que l'écran des paramètres se mette à jour tout seul.</summary>
public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (var other in also) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(other));
        return true;
    }
}

/// <summary>
/// Une commande générale : ce que tu dis → ce que Lucie fait.
/// Plusieurs façons de le dire peuvent être séparées par « / ».
/// </summary>
public sealed class VoiceCommand : Notifier
{
    private string _phrase = "";
    private CommandAction _action;
    private string _target = "";

    public string Phrase { get => _phrase; set => Set(ref _phrase, value ?? ""); }

    public CommandAction Action
    {
        get => _action;
        set => Set(ref _action, value, nameof(Action), nameof(ActionIndex), nameof(ShowKeys), nameof(ShowTarget));
    }

    /// <summary>Raccourci (« Ctrl+Maj+M ») ou lien / fichier selon l'action.</summary>
    public string Target { get => _target; set => Set(ref _target, value ?? ""); }
    public string Arguments { get; set; } = "";

    [JsonIgnore]
    public int ActionIndex
    {
        get => (int)Action;
        set => Action = value < 0 ? CommandAction.OpenApp : (CommandAction)value;
    }

    [JsonIgnore] public bool ShowKeys => Action == CommandAction.Shortcut;
    [JsonIgnore] public bool ShowTarget => Action == CommandAction.OpenApp;

    public VoiceCommand Clone() => new() { Phrase = Phrase, Action = Action, Target = Target, Arguments = Arguments };
}

/// <summary>Un raccourci clavier propre à une appli : « coupe mon micro » → Ctrl+Maj+M dans Discord.</summary>
public sealed class AppShortcut : Notifier
{
    private string _phrase = "";
    private string _keys = "";

    public string Phrase { get => _phrase; set => Set(ref _phrase, value ?? ""); }
    public string Keys { get => _keys; set => Set(ref _keys, value ?? ""); }

    public AppShortcut Clone() => new() { Phrase = Phrase, Keys = Keys };
}

/// <summary>
/// Une appli que Lucie sait ouvrir, mettre au premier plan, et piloter avec ses raccourcis :
/// « Ouvre Cider », « Montre-moi Discord », « Coupe mon micro »…
/// </summary>
public sealed class AppEntry : Notifier
{
    private string _name = "";
    private string _path = "";
    private bool _focusBeforeShortcut = true;
    private bool _isExpanded;

    /// <summary>Nom à dire. Variantes possibles séparées par « / » (ex. « Cider / cidre »).</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? "", nameof(Name), nameof(DisplayName)); }
    public string Path { get => _path; set => Set(ref _path, value ?? ""); }
    public string Arguments { get; set; } = "";
    /// <summary>Nom du processus à chercher s'il est déjà ouvert (vide = nom du .exe).</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>
    /// Mettre l'appli devant avant d'envoyer un de ses raccourcis.
    /// Inutile pour les raccourcis « globaux » (ceux de Discord marchent même en arrière-plan).
    /// </summary>
    public bool FocusBeforeShortcut { get => _focusBeforeShortcut; set => Set(ref _focusBeforeShortcut, value); }

    private ObservableCollection<AppShortcut> _shortcuts = new();

    public ObservableCollection<AppShortcut> Shortcuts
    {
        get => _shortcuts;
        set
        {
            _shortcuts.CollectionChanged -= OnShortcutsChanged;
            _shortcuts = value ?? new();
            _shortcuts.CollectionChanged += OnShortcutsChanged;
            OnShortcutsChanged(null, null);
        }
    }

    public AppEntry() => _shortcuts.CollectionChanged += OnShortcutsChanged;

    private void OnShortcutsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e) =>
        Raise(nameof(ShortcutSummary));

    [JsonIgnore]
    public string ShortcutSummary => Shortcuts.Count switch
    {
        0 => "Aucun raccourci — « Ouvre » et « Montre-moi » marchent déjà",
        1 => "1 raccourci",
        _ => $"{Shortcuts.Count} raccourcis"
    };

    [JsonIgnore] public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var first = Name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? "Nouvelle application" : first;
        }
    }

    public AppEntry Clone() => new()
    {
        Name = Name, Path = Path, Arguments = Arguments, ProcessName = ProcessName,
        FocusBeforeShortcut = FocusBeforeShortcut,
        Shortcuts = new ObservableCollection<AppShortcut>(Shortcuts.Select(x => x.Clone()))
    };
}

public sealed class AppSettings
{
    public const int CurrentVersion = 3;
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
        new() { Phrase = "ouvre le navigateur / ouvre internet / ouvre google", Action = CommandAction.OpenApp, Target = "https://www.google.com" },
    };

    public static List<AppEntry> DefaultApps() => new()
    {
        DefaultDiscord(),
    };

    private static AppEntry DefaultDiscord() => new()
    {
        Name = "Discord",
        Path = @"%LocalAppData%\Discord\Update.exe",
        Arguments = "--processStart Discord.exe",
        ProcessName = "Discord",
        FocusBeforeShortcut = false,   // les raccourcis de Discord marchent même quand il est derrière
        Shortcuts = new()
        {
            new() { Phrase = "coupe mon micro / active mon micro / remets mon micro", Keys = "Ctrl+Maj+M" },
            new() { Phrase = "mets-moi en sourdine / enlève la sourdine", Keys = "Ctrl+Maj+D" },
        }
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
            Version = 2;
        }

        Commands ??= DefaultCommands();
        Apps ??= DefaultApps();

        if (Version < 3)
        {
            // Les raccourcis Discord passent dans la carte de l'appli Discord.
            var discord = Apps.FirstOrDefault(a => a.Name.Contains("discord", StringComparison.OrdinalIgnoreCase));
            if (discord is null)
            {
                discord = DefaultDiscord();
                discord.Shortcuts.Clear();
                Apps.Insert(0, discord);
            }
            discord.FocusBeforeShortcut = false;

            var moved = Commands.Where(c => c.Action == CommandAction.Shortcut &&
                (c.Phrase.Contains("micro", StringComparison.OrdinalIgnoreCase) ||
                 c.Phrase.Contains("sourdine", StringComparison.OrdinalIgnoreCase) ||
                 c.Phrase.Contains("discord", StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var c in moved)
            {
                Commands.Remove(c);
                if (!discord.Shortcuts.Any(x => x.Keys.Equals(c.Target, StringComparison.OrdinalIgnoreCase)))
                    discord.Shortcuts.Add(new AppShortcut { Phrase = c.Phrase, Keys = c.Target });
            }
            if (discord.Shortcuts.Count == 0)
                foreach (var x in DefaultDiscord().Shortcuts) discord.Shortcuts.Add(x);

            // Les anciennes applis : on les met devant avant leurs raccourcis (sauf Discord).
            foreach (var a in Apps.Where(a => a != discord)) a.FocusBeforeShortcut = true;
            Version = CurrentVersion;
        }

        foreach (var a in Apps) a.Shortcuts ??= new();
        if (string.IsNullOrWhiteSpace(TalkHotkey)) TalkHotkey = "Ctrl+Alt+L";
    }
}
