using System.Globalization;
using System.Text;
using Lucie.Models;

namespace Lucie.Services;

/// <summary>
/// Transforme une phrase entendue en commande.
/// Tolérant : « peux-tu couper mon micro stp » trouve « coupe mon micro ».
/// </summary>
public sealed class CommandInterpreter
{
    private static readonly HashSet<string> StopWords = new()
    {
        "le", "la", "les", "l", "de", "du", "des", "d", "un", "une", "moi", "mon", "ma", "mes", "me", "m",
        "s", "il", "te", "toi", "plait", "stp", "svp", "peux", "tu", "est", "ce", "que", "qu", "a", "au",
        "et", "en", "sur", "pour", "lucie", "vas", "y", "please", "allez", "maintenant", "vite"
    };

    private static readonly HashSet<string> OpenVerbs = new()
    {
        "ouvre", "ouvrir", "ouvres", "lance", "lancer", "lances", "demarre", "demarrer", "affiche", "afficher"
    };

    public VoiceCommand? Match(string heard, IEnumerable<VoiceCommand> commands)
    {
        string text = Normalize(heard);
        if (text.Length == 0) return null;
        var tokens = Tokens(text);

        VoiceCommand? best = null;
        double bestScore = 0;

        foreach (var cmd in commands)
        {
            string phrase = Normalize(cmd.Phrase);
            if (phrase.Length == 0) continue;

            double score;
            if ($" {text} ".Contains($" {phrase} ", StringComparison.Ordinal))
            {
                score = 1.0 + phrase.Length * 0.001; // la phrase exacte la plus longue gagne
            }
            else
            {
                var wanted = Tokens(phrase);
                if (wanted.Count == 0) continue;
                int found = wanted.Count(w => tokens.Any(t => Similar(w, t)));
                score = (double)found / wanted.Count;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = cmd;
            }
        }

        return bestScore >= 0.66 ? best : null;
    }

    /// <summary>
    /// « Ouvre Spotify » sans commande dédiée : on cherche l'appli dans le menu Démarrer.
    /// </summary>
    public VoiceCommand? FallbackOpen(string heard)
    {
        var words = Normalize(heard).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int verb = words.FindIndex(OpenVerbs.Contains);
        if (verb < 0) return null;

        var name = string.Join(' ', words.Skip(verb + 1).Where(w => !StopWords.Contains(w)));
        if (name.Length == 0) return null;

        var shortcut = AppLauncher.FindStartMenuApp(name);
        return shortcut is null ? null : new VoiceCommand { Phrase = heard, Action = CommandAction.OpenApp, Target = shortcut };
    }

    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !StopWords.Contains(w)).ToList();

    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (a.TrimEnd('s', 'x') == b.TrimEnd('s', 'x')) return true;           // pluriels
        if (a.Length >= 5 && b.Length >= 4 && (a.StartsWith(b[..4]) && b.StartsWith(a[..4])) && Levenshtein(a, b) <= 2)
            return true;                                                          // « coupe » / « couper »
        return a.Length >= 5 && Levenshtein(a, b) <= 1;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
