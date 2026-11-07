using System.Text.Json;
using System.Text.Json.Serialization;
using Lucie.Models;
using Microsoft.Win32;

namespace Lucie.Services;

/// <summary>Charge et enregistre les paramètres dans %LocalAppData%\Lucie\settings.json.</summary>
public sealed class SettingsService
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucie");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Current { get; private set; } = AppSettings.CreateDefault();

    /// <summary>Déclenché après chaque enregistrement.</summary>
    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (loaded is not null)
                {
                    if (loaded.Commands is null || loaded.Commands.Count == 0)
                        loaded.Commands = AppSettings.DefaultCommands();
                    Current = loaded;
                    return;
                }
            }
        }
        catch
        {
            // Fichier abîmé : on repart des valeurs par défaut.
        }

        Current = AppSettings.CreateDefault();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
            ApplyStartup();
        }
        catch
        {
            // Pas bloquant : on garde les réglages en mémoire.
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;
        if (Current.StartWithWindows && Environment.ProcessPath is string exe)
            key.SetValue("Lucie", $"\"{exe}\"");
        else
            key.DeleteValue("Lucie", throwOnMissingValue: false);
    }
}
