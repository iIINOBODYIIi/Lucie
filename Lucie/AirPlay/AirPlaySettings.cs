using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lucie.Services;
using Onde.Core.Dsp;

namespace Lucie.AirPlay;

public enum BackdropMode { Solid, Acrylic, Transparent }

/// <summary>Widget attaché (toujours au premier plan), fenêtre normale, ou fixé sur le bureau (derrière tout).</summary>
public enum WidgetMode { Widget, Window, Desktop }

/// <summary>
/// Tous les réglages AirPlay (ceux d'Onde), enregistrés dans %LocalAppData%\Lucie\airplay.json.
/// Au premier lancement, les réglages d'Onde (%LocalAppData%\Onde\settings.json) sont repris.
/// </summary>
public sealed class AirPlaySettings
{
    // --- Activation ---
    /// <summary>L'enceinte AirPlay est-elle allumée ?</summary>
    public bool Enabled { get; set; }
    /// <summary>Le widget AirPlay est-il affiché ?</summary>
    public bool ShowWidget { get; set; } = true;
    /// <summary>Se cacher quand un jeu ou une vidéo est en plein écran.</summary>
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>N'afficher le widget que lorsqu'un iPhone est connecté.</summary>
    public bool OnlyWhenConnected { get; set; }

    // --- AirPlay ---
    public string ReceiverName { get; set; } = Environment.MachineName;
    public string HardwareId { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

    // --- Apparence ---
    public string AccentColor { get; set; } = "#FF8B5CF6";
    public string BackgroundColor { get; set; } = "#FF15131F";
    public string TextColor { get; set; } = "#FFFFFFFF";
    public double BackgroundOpacity { get; set; } = 0.78;
    public BackdropMode Backdrop { get; set; } = BackdropMode.Acrylic;
    public double CornerRadius { get; set; } = 20;
    /// <summary>Opacité du contenu (texte, pochette, boutons) : 0.15 à 1.</summary>
    public double ContentOpacity { get; set; } = 1.0;
    /// <summary>Taille de la fenêtre en pixels logiques (indépendants de la mise à l'échelle Windows).</summary>
    public int WidgetWidth { get; set; } = 390;
    public int WidgetHeight { get; set; } = 152;
    public WidgetMode Mode { get; set; } = WidgetMode.Widget;
    public bool LockPosition { get; set; }
    public bool ArtworkBackground { get; set; } = true;
    public bool ShowVisualizer { get; set; } = true;
    public bool ShowInTaskbar { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // --- Audio ---
    public double LocalVolume { get; set; } = 0.8;
    public string? OutputDeviceId { get; set; }
    public int BufferMs { get; set; } = 350;

    public bool EqEnabled { get; set; }
    public string EqPreset { get; set; } = "Plat";
    public double[] EqGains { get; set; } = new double[10];
    public double EqPreamp { get; set; }

    public bool ConcertEnabled { get; set; }
    public string ConcertPreset { get; set; } = "Salle de concert";
    public ConcertSettings Concert { get; set; } = new();

    // --- Système ---
    /// <summary>Partage titre / pochette / état avec Windows (Wallpaper Engine, touches multimédias…).</summary>
    public bool SystemMediaIntegration { get; set; } = true;

    public const int MinWidth = 240, MaxWidth = 1400, MinHeight = 80, MaxHeight = 900;

    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(SettingsService.DataDir, "airplay.json");
    private static string OndeFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Onde", "settings.json");

    /// <summary>Vrai si les réglages viennent d'être repris depuis Onde.</summary>
    [JsonIgnore]
    public bool ImportedFromOnde { get; private set; }

    public static AirPlaySettings Load()
    {
        var s = TryRead(FilePath);
        if (s is null)
        {
            // Premier lancement : on reprend les réglages d'Onde (nom, égaliseur, couleurs…)
            s = TryRead(OndeFilePath);
            if (s is not null)
            {
                s.Enabled = false;   // à allumer depuis Lucie, pour ne pas entrer en conflit avec Onde
                s.ShowWidget = true;
                s.ImportedFromOnde = true;
                s.Save();
            }
        }
        return s ?? new AirPlaySettings();
    }

    private static AirPlaySettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<AirPlaySettings>(File.ReadAllText(path), Json);
            if (s is null) return null;
            if (s.EqGains is not { Length: 10 }) s.EqGains = new double[10];
            s.Concert ??= new ConcertSettings();
            if (s.HardwareId is not { Length: 12 }) s.HardwareId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            if (string.IsNullOrWhiteSpace(s.ReceiverName)) s.ReceiverName = Environment.MachineName;
            s.WidgetWidth = Math.Clamp(s.WidgetWidth, MinWidth, MaxWidth);
            s.WidgetHeight = Math.Clamp(s.WidgetHeight, MinHeight, MaxHeight);
            s.ContentOpacity = Math.Clamp(s.ContentOpacity, 0.15, 1);
            return s;
        }
        catch
        {
            return null;   // fichier abîmé : on repart des valeurs par défaut
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // pas bloquant
        }
    }

    public byte[] GetHardwareAddress() => Convert.FromHexString(HardwareId);

    public static Windows.UI.Color ParseColor(string hex, Windows.UI.Color fallback)
    {
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length == 6) h = "FF" + h;
            if (h.Length != 8) return fallback;
            uint v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Microsoft.UI.ColorHelper.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        catch
        {
            return fallback;
        }
    }

    public static string ToHex(Windows.UI.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
