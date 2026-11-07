using System.Diagnostics;
using Lucie.Models;

namespace Lucie.Services;

/// <summary>Exécute une commande reconnue.</summary>
public sealed class ActionExecutor
{
    private readonly MediaService _media;

    public ActionExecutor(MediaService media) => _media = media;

    public async Task<bool> ExecuteAsync(VoiceCommand command)
    {
        try
        {
            switch (command.Action)
            {
                case CommandAction.OpenApp:
                    Launch(command.Target, command.Arguments);
                    return true;

                case CommandAction.Shortcut:
                    return await KeyboardSimulator.SendComboAsync(command.Target);

                case CommandAction.Screenshot:
                    ScreenCapture.CaptureToPictures();
                    return true;

                case CommandAction.MediaPlayPause:
                    return await _media.TogglePlayPauseAsync();

                case CommandAction.MediaNext:
                    return await _media.NextAsync();

                case CommandAction.MediaPrevious:
                    return await _media.PreviousAsync();
            }
        }
        catch
        {
            // Appli introuvable, raccourci invalide… → son « pas compris ».
        }
        return false;
    }

    private static void Launch(string target, string arguments)
    {
        var path = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (path.Length == 0) throw new InvalidOperationException("Aucune cible");

        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(arguments))
            psi.Arguments = Environment.ExpandEnvironmentVariables(arguments);
        if (Path.IsPathRooted(path) && Path.GetDirectoryName(path) is string dir)
            psi.WorkingDirectory = dir;

        Process.Start(psi);
    }
}
