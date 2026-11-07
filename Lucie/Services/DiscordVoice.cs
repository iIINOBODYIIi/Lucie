namespace Lucie.Services;

public enum DiscordVoiceAction { Mute, Unmute, ToggleMute, Deafen, Undeafen, ToggleDeafen, LeaveVoice }

/// <summary>
/// Phrases pour piloter Discord directement (quand Lucie est connectée à Discord) :
/// « coupe mon micro », « réactive mon micro », « mets-moi en sourdine », « quitte le vocal »…
/// Contrairement aux raccourcis clavier, on peut dire exactement ce qu'on veut (couper OU réactiver).
/// </summary>
public static class DiscordVoice
{
    private static readonly string[] UnmuteWords = { "active", "activer", "reactive", "reactiver", "remets", "remet", "rallume", "ouvre", "demute", "unmute", "rends", "allume" };
    private static readonly string[] MuteWords = { "coupe", "couper", "mute", "desactive", "desactiver", "ferme", "eteins", "eteint", "enleve", "bloque" };
    private static readonly string[] DeafOffWords = { "enleve", "retire", "desactive", "coupe", "sors", "stop", "arrete", "plus" };
    private static readonly string[] DeafOnWords = { "mets", "met", "active", "passe" };
    private static readonly string[] LeavePhrases =
    {
        "quitte le vocal", "quitte le salon", "quitte l appel", "quitte la vocale", "sors du vocal", "sors du salon",
        "deconnecte moi du vocal", "deconnecte moi", "deconnecte toi du vocal", "raccroche", "quitte discord vocal"
    };

    public static DiscordVoiceAction? Match(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = $" {text} ";
        bool Has(string[] words) => words.Any(w => t.Contains($" {w} "));

        foreach (var p in LeavePhrases)
            if (t.Contains($" {p} ")) return DiscordVoiceAction.LeaveVoice;

        if (t.Contains(" sourdine ") || t.Contains(" sourd ") || t.Contains(" deafen ") || t.Contains(" deaf "))
        {
            if (Has(DeafOffWords)) return DiscordVoiceAction.Undeafen;
            if (Has(DeafOnWords)) return DiscordVoiceAction.Deafen;
            return DiscordVoiceAction.ToggleDeafen;
        }

        bool mic = t.Contains(" micro ") || t.Contains(" micros ") || t.Contains(" mic ") || t.Contains(" mute ")
                   || t.Contains(" demute ") || t.Contains(" unmute ");
        if (mic)
        {
            // « désactive » contient « active » : on regarde d'abord les mots qui coupent.
            if (t.Contains(" demute ") || t.Contains(" unmute ")) return DiscordVoiceAction.Unmute;
            if (Has(MuteWords)) return DiscordVoiceAction.Mute;
            if (Has(UnmuteWords)) return DiscordVoiceAction.Unmute;
            return DiscordVoiceAction.ToggleMute;
        }
        return null;
    }

    public static Task<bool> ExecuteAsync(DiscordVoiceAction action, DiscordService discord) => action switch
    {
        DiscordVoiceAction.Mute => discord.SetMuteAsync(true),
        DiscordVoiceAction.Unmute => discord.SetMuteAsync(false),
        DiscordVoiceAction.ToggleMute => discord.SetMuteAsync(null),
        DiscordVoiceAction.Deafen => discord.SetDeafAsync(true),
        DiscordVoiceAction.Undeafen => discord.SetDeafAsync(false),
        DiscordVoiceAction.ToggleDeafen => discord.SetDeafAsync(null),
        DiscordVoiceAction.LeaveVoice => discord.LeaveVoiceAsync(),
        _ => Task.FromResult(false)
    };
}
