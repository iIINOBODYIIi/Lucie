using Lucie.AirPlay;
using Onde.Core.Dsp;

namespace Lucie.Services;

public enum AirPlayVoiceAction
{
    ConcertOn, ConcertOff, ConcertPreset,
    EqOn, EqOff, EqPreset, EqFlat,
    BassUp, BassDown, TrebleUp, TrebleDown, MidUp, MidDown,
    VolumeUp, VolumeDown,
    Enable, Disable,
    ShowWidget, HideWidget
}

public sealed record AirPlayVoiceCommand(AirPlayVoiceAction Action, string? Preset = null);

/// <summary>
/// Commandes vocales de l'enceinte AirPlay : « active le mode concert », « mets l'ambiance stade »,
/// « augmente les basses », « égaliseur rock », « allume AirPlay », « cache le widget AirPlay »…
/// </summary>
public static class AirPlayVoice
{
    private static readonly string[] OnWords = { "active", "activer", "allume", "allumer", "lance", "lancer", "mets", "met", "mettre", "demarre", "branche", "passe", "ouvre" };
    private static readonly string[] OffWords = { "desactive", "desactiver", "eteins", "eteint", "eteindre", "coupe", "couper", "enleve", "enlever", "arrete", "arreter", "stop", "retire", "retirer", "sans", "debranche", "ferme" };
    private static readonly string[] UpWords = { "augmente", "augmenter", "monte", "monter", "plus", "booste", "booster", "renforce", "ajoute", "davantage", "pousse", "fort" };
    private static readonly string[] DownWords = { "baisse", "baisser", "diminue", "diminuer", "moins", "reduis", "reduire", "enleve", "attenue", "coupe" };
    private static readonly string[] ShowWords = { "affiche", "afficher", "montre", "montrer", "remets", "fais apparaitre", "sors" };
    private static readonly string[] HideWords = { "cache", "cacher", "masque", "masquer", "enleve", "retire", "range", "fais disparaitre" };

    // Lieux du mode concert (noms d'Onde) et la façon dont on les dit
    private static readonly (string Spoken, string Preset)[] ConcertPlaces =
    {
        ("salle de concert", "Salle de concert"), ("club", "Club"), ("boite", "Club"), ("stade", "Stade"),
        ("cathedrale", "Cathédrale"), ("eglise", "Cathédrale"), ("live acoustique", "Live acoustique"),
        ("acoustique", "Live acoustique"), ("canyon", "Canyon"),
    };

    // Préréglages de l'égaliseur (noms d'Onde) et la façon dont on les dit
    private static readonly (string Spoken, string Preset)[] EqStyles =
    {
        ("hip hop", "Hip-hop"), ("rap", "Hip-hop"), ("rock", "Rock"), ("pop", "Pop"), ("electro", "Électro"),
        ("techno", "Électro"), ("jazz", "Jazz"), ("classique", "Classique"), ("voix", "Voix"), ("podcast", "Voix"),
        ("nuit", "Nuit"), ("basses plus", "Basses+"), ("aigus plus", "Aigus+"), ("plat", "Plat"),
    };

    public static AirPlayVoiceCommand? Match(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = $" {text} ";
        string compact = text.Replace(" ", "");

        bool Has(params string[] words) => words.Any(w => t.Contains($" {w} "));
        bool on = Has(OnWords), off = Has(OffWords);

        // ── L'enceinte elle-même : « allume AirPlay », « coupe l'AirPlay » ──
        bool airplay = compact.Contains("airplay") || compact.Contains("airplai") || compact.Contains("errplay")
                       || t.Contains(" enceinte ") || compact.Contains("modeenceinte");
        bool widget = t.Contains(" widget ") || t.Contains(" lecteur ");

        if (airplay && widget)
        {
            if (Has(HideWords)) return new(AirPlayVoiceAction.HideWidget);
            if (Has(ShowWords) || on) return new(AirPlayVoiceAction.ShowWidget);
        }

        // ── Mode concert / studio ──
        bool concert = t.Contains(" concert ") || t.Contains(" studio ") || t.Contains(" ambiance ")
                       || t.Contains(" reverb") || t.Contains(" echo ");
        foreach (var (spoken, preset) in ConcertPlaces)
        {
            if (!t.Contains($" {spoken} ")) continue;
            if (concert || t.Contains(" mode ") || t.Contains(" son ") || t.Contains(" effet ") || t.Contains(" comme "))
            {
                if (spoken == "acoustique" && (t.Contains(" egaliseur ") || t.Contains(" style "))) break;   // « égaliseur acoustique »
                return off ? new(AirPlayVoiceAction.ConcertOff) : new(AirPlayVoiceAction.ConcertPreset, preset);
            }
        }
        if (concert && !t.Contains(" salle de concert "))
            return off ? new(AirPlayVoiceAction.ConcertOff) : new(AirPlayVoiceAction.ConcertOn);

        // ── Basses / médiums / aigus ──
        bool up = Has(UpWords), down = Has(DownWords) && !up;
        if (t.Contains(" basse ") || t.Contains(" basses ") || t.Contains(" bass "))
        {
            if (up) return new(AirPlayVoiceAction.BassUp);
            if (down) return new(AirPlayVoiceAction.BassDown);
        }
        if (t.Contains(" aigu ") || t.Contains(" aigus ") || t.Contains(" aigue ") || t.Contains(" aigues "))
        {
            if (up) return new(AirPlayVoiceAction.TrebleUp);
            if (down) return new(AirPlayVoiceAction.TrebleDown);
        }
        if (t.Contains(" medium ") || t.Contains(" mediums ") || t.Contains(" medius "))
        {
            if (up) return new(AirPlayVoiceAction.MidUp);
            if (down) return new(AirPlayVoiceAction.MidDown);
        }

        // ── Égaliseur ──
        bool eq = t.Contains(" egaliseur ") || t.Contains(" equaliseur ") || t.Contains(" equalizer ") || t.Contains(" eq ");
        bool styleHint = eq || t.Contains(" style ") || t.Contains(" son ") || t.Contains(" mode ") || t.Contains(" preset ");
        if (styleHint)
        {
            foreach (var (spoken, preset) in EqStyles)
            {
                if (!t.Contains($" {spoken} ")) continue;
                if (preset == "Plat") return new(AirPlayVoiceAction.EqFlat);
                return new(AirPlayVoiceAction.EqPreset, preset);
            }
        }
        if (eq)
        {
            if (t.Contains(" a plat ") || t.Contains(" zero ") || t.Contains(" reinitialise ") || t.Contains(" remet a zero ")) return new(AirPlayVoiceAction.EqFlat);
            if (off) return new(AirPlayVoiceAction.EqOff);
            return new(AirPlayVoiceAction.EqOn);
        }

        // ── Volume de l'enceinte ──
        if (airplay && (t.Contains(" volume ") || t.Contains(" son ")))
        {
            if (up) return new(AirPlayVoiceAction.VolumeUp);
            if (Has(DownWords)) return new(AirPlayVoiceAction.VolumeDown);
        }

        // ── Allumer / éteindre l'enceinte ──
        if (airplay)
        {
            if (off) return new(AirPlayVoiceAction.Disable);
            if (on) return new(AirPlayVoiceAction.Enable);
        }

        return null;
    }

    private static readonly int[] Bass = { 0, 1, 2 }, Mid = { 4, 5, 6 }, Treble = { 7, 8, 9 };

    /// <summary>Exécute la commande. Renvoie false si elle n'a pas de sens maintenant.</summary>
    public static bool Execute(AirPlayVoiceCommand cmd, AirPlayService ap)
    {
        switch (cmd.Action)
        {
            case AirPlayVoiceAction.ConcertOn: ap.SetConcertEnabled(true); return true;
            case AirPlayVoiceAction.ConcertOff: ap.SetConcertEnabled(false); return true;
            case AirPlayVoiceAction.ConcertPreset: return cmd.Preset is not null && ap.ApplyConcertPreset(cmd.Preset);

            case AirPlayVoiceAction.EqOn: ap.SetEqEnabled(true); return true;
            case AirPlayVoiceAction.EqOff: ap.SetEqEnabled(false); return true;
            case AirPlayVoiceAction.EqFlat: ap.ResetEq(); return true;
            case AirPlayVoiceAction.EqPreset: return cmd.Preset is not null && ap.ApplyEqPreset(cmd.Preset);

            case AirPlayVoiceAction.BassUp: ap.AdjustBands(Bass, +3); return true;
            case AirPlayVoiceAction.BassDown: ap.AdjustBands(Bass, -3); return true;
            case AirPlayVoiceAction.MidUp: ap.AdjustBands(Mid, +3); return true;
            case AirPlayVoiceAction.MidDown: ap.AdjustBands(Mid, -3); return true;
            case AirPlayVoiceAction.TrebleUp: ap.AdjustBands(Treble, +3); return true;
            case AirPlayVoiceAction.TrebleDown: ap.AdjustBands(Treble, -3); return true;

            case AirPlayVoiceAction.VolumeUp: ap.SetVolume(ap.Settings.LocalVolume + 0.1); return true;
            case AirPlayVoiceAction.VolumeDown: ap.SetVolume(ap.Settings.LocalVolume - 0.1); return true;

            case AirPlayVoiceAction.Enable:
                if (AirPlayService.IsOndeRunning()) AirPlayService.CloseOnde();
                ap.SetEnabled(true);
                App.Current.UpdateAirPlayWidget();
                return true;
            case AirPlayVoiceAction.Disable:
                ap.SetEnabled(false);
                App.Current.UpdateAirPlayWidget();
                return true;

            case AirPlayVoiceAction.ShowWidget:
                if (!ap.IsRunning) ap.SetEnabled(true);
                App.Current.SetAirPlayWidgetVisible(true);
                return true;
            case AirPlayVoiceAction.HideWidget:
                App.Current.SetAirPlayWidgetVisible(false);
                return true;
        }
        return false;
    }

    /// <summary>Petit message pour l'utilisateur (affiché nulle part pour l'instant, utile au débogage).</summary>
    public static string Describe(AirPlayVoiceCommand cmd) => cmd.Action switch
    {
        AirPlayVoiceAction.ConcertPreset => $"Mode concert : {cmd.Preset}",
        AirPlayVoiceAction.EqPreset => $"Égaliseur : {cmd.Preset}",
        _ => cmd.Action.ToString()
    };
}
