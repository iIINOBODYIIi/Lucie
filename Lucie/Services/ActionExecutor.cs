using Lucie.Models;
using Onde.Core.AirPlay;

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
                    // Un programme (.exe) : on l'ouvre ou on le met devant. Un lien / fichier : on l'ouvre.
                    if (command.Target.Trim().Trim('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return await AppLauncher.OpenOrShowAsync(new AppEntry { Name = "", Path = command.Target, Arguments = command.Arguments });
                    AppLauncher.Launch(command.Target, command.Arguments);
                    return true;

                case CommandAction.Shortcut:
                    return await KeyboardSimulator.SendComboAsync(command.Target);

                case CommandAction.Screenshot:
                    ScreenCapture.CaptureToPictures();
                    return true;

                // Si un iPhone joue sur l'enceinte AirPlay, la musique se pilote sur l'iPhone.
                case CommandAction.MediaPlayPause:
                    return AirPlayConnected ? await App.AirPlay.SendAsync(RemoteCommand.PlayPause) : await _media.TogglePlayPauseAsync();

                case CommandAction.MediaPlay:
                    return AirPlayConnected ? await App.AirPlay.SendAsync(RemoteCommand.Play) : await _media.PlayAsync();

                case CommandAction.MediaPause:
                    return AirPlayConnected ? await App.AirPlay.SendAsync(RemoteCommand.Pause) : await _media.PauseAsync();

                case CommandAction.MediaNext:
                    return AirPlayConnected ? await App.AirPlay.SendAsync(RemoteCommand.Next) : await _media.NextAsync();

                case CommandAction.MediaPrevious:
                    return AirPlayConnected ? await App.AirPlay.SendAsync(RemoteCommand.Previous) : await _media.PreviousAsync();

                case CommandAction.VolumeUp:
                    return await TapAsync(KeyboardSimulator.VK_VOLUME_UP, 5);

                case CommandAction.VolumeDown:
                    return await TapAsync(KeyboardSimulator.VK_VOLUME_DOWN, 5);

                case CommandAction.VolumeMute:
                    KeyboardSimulator.Tap(KeyboardSimulator.VK_VOLUME_MUTE);
                    return true;
            }
        }
        catch
        {
            // Appli introuvable, raccourci invalide… → son « pas compris ».
        }
        return false;
    }

    /// <summary>Exécute tout ce que Lucie a compris.</summary>
    public async Task<bool> RunAsync(Intent intent)
    {
        if (intent.AirPlay is AirPlayVoiceCommand airPlay) return AirPlayVoice.Execute(airPlay, App.AirPlay);
        if (intent.App is AppEntry app) return await OpenAppAsync(app);
        if (intent.Command is null) return false;
        if (intent.ShortcutApp is AppEntry owner) return await RunAppShortcutAsync(owner, intent.Command.Target);
        return await ExecuteAsync(intent.Command);
    }

    /// <summary>
    /// Raccourci d'une appli : on la met devant si besoin (raccourcis « locaux »),
    /// puis on tape les touches.
    /// </summary>
    public async Task<bool> RunAppShortcutAsync(AppEntry app, string keys)
    {
        try
        {
            if (app.FocusBeforeShortcut && !string.IsNullOrWhiteSpace(app.Path))
            {
                await AppLauncher.OpenOrShowAsync(app);
                await Task.Delay(350);   // le temps que la fenêtre soit vraiment active
            }
            return await KeyboardSimulator.SendComboAsync(keys);
        }
        catch
        {
            return false;
        }
    }

    private static bool AirPlayConnected => App.AirPlay?.IsConnected == true;

    public async Task<bool> OpenAppAsync(AppEntry app)
    {
        try
        {
            return await AppLauncher.OpenOrShowAsync(app);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TapAsync(ushort key, int times)
    {
        for (int i = 0; i < times; i++)
        {
            KeyboardSimulator.Tap(key);
            await Task.Delay(30);
        }
        return true;
    }
}
