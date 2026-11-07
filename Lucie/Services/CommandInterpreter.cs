using System.Globalization;
using System.Text;
using Lucie.Models;

namespace Lucie.Services;

/// <summary>Ce que Lucie a compris : une commande, ou une appli à ouvrir / montrer.</summary>
public sealed record Intent(VoiceCommand? Command, AppEntry? App);

/// <summary>
/// Transforme une phrase entendue en action.
/// Tolérant : « peux-tu couper mon micro stp » trouve « coupe mon micro ».
/// </summary>
public sealed class CommandInterpreter
{
    private static readonly HashSet<string> StopWords = new()
    {
        "le", "la", "les", "l", "de", "du", "des", "d", "un", "une", "moi", "mon", "ma", "mes", "me", "m",
        "s", "il", "te", "toi", "plait", "stp", "svp", "peux", "tu", "est", "ce", "que", "qu", "a", "au",
        "et", "en", "sur", "pour", "y", "please", "allez", "maintenant", "vite", "salut", "hey", "ok", "coucou"
    };

    // « Ouvre X », « Lance X », « Montre-moi X », « Affiche X »…
    private static readonly HashSet<string> OpenVerbs = new()
    {
        "ouvre", "ouvrir", "ouvres", "lance", "lancer", "lances", "demarre", "demarrer",
        "montre", "montrer", "montres", "affiche", "afficher", "affiches", "va", "vas", "passe"
    };

    private static readonly HashSet<string> AppNoise = new()
    {
        "application", "appli", "app", "logiciel", "programme", "jeu", "page", "fenetre", "client"
    };

    public Intent? Interpret(string heard, AppSettings settings)
    {
        string text = Normalize(heard);
        if (text.Length == 0) return null;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // 1. Une commande de la liste
        var (command, score) = BestCommand(text, words, settings.Commands);
        if (command is not null && score >= 1.0) return new Intent(command, null);

        // 2. « Ouvre / montre-moi » + une appli enregistrée
        int verb = words.FindIndex(OpenVerbs.Contains);
        if (verb >= 0)
        {
            var name = string.Join(' ', words.Skip(verb + 1).Where(w => !StopWords.Contains(w) && !AppNoise.Contains(w)));
            if (name.Length > 0)
            {
                var app = BestApp(name, settings.Apps);
                if (app is not null) return new Intent(null, app);

                // 3. Une commande approchante, sinon le menu Démarrer
                if (command is not null && score >= 0.66) return new Intent(command, null);
                var startMenu = AppLauncher.FindStartMenuApp(name);
                if (startMenu is not null) return new Intent(null, startMenu);
            }
        }

        return command is not null && score >= 0.66 ? new Intent(command, null) : null;
    }

    private static (VoiceCommand? Command, double Score) BestCommand(string text, List<string> words, IEnumerable<VoiceCommand> commands)
    {
        var tokens = words.Where(w => !StopWords.Contains(w)).ToList();
        VoiceCommand? best = null;
        double bestScore = 0;

        foreach (var cmd in commands)
        {
            foreach (var variant in Variants(cmd.Phrase))
            {
                double score;
                if ($" {text} ".Contains($" {variant} ", StringComparison.Ordinal))
                {
                    score = 1.0 + variant.Length * 0.001; // la phrase exacte la plus longue gagne
                }
                else
                {
                    var wanted = variant.Split(' ').Where(w => !StopWords.Contains(w)).ToList();
                    if (wanted.Count == 0) continue;
                    int found = wanted.Count(w => tokens.Any(t => Similar(w, t)));
                    score = (double)found / wanted.Count;
                    // Une seule paire de mots trouvés sur deux phrases courtes ne suffit pas.
                    if (wanted.Count == 1 && tokens.Count > 2) score *= 0.6;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = cmd;
                }
            }
        }
        return (best, bestScore);
    }

    private static AppEntry? BestApp(string spoken, IEnumerable<AppEntry> apps)
    {
        AppEntry? best = null;
        int bestScore = 0;
        string compact = spoken.Replace(" ", "");

        foreach (var app in apps)
        {
            foreach (var alias in Variants(app.Name))
            {
                string a = alias.Replace(" ", "");
                int score = a == compact ? 4
                          : $" {spoken} ".Contains($" {alias} ") ? 3
                          : compact.Contains(a) || (a.Length >= 4 && a.Contains(compact) && compact.Length >= 3) ? 2
                          : Levenshtein(a, compact) <= Math.Max(1, a.Length / 4) ? 1
                          : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = app;
                }
            }
        }
        return best;
    }

    /// <summary>« lance la musique / lecture » → ["lance la musique", "lecture"] (normalisés).</summary>
    public static IEnumerable<string> Variants(string phrase) =>
        phrase.Split(new[] { '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
              .Select(Normalize)
              .Where(v => v.Length > 0);

    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (a.TrimEnd('s', 'x') == b.TrimEnd('s', 'x')) return true;           // pluriels
        if (a.Length >= 5 && b.Length >= 4 && a[..4] == b[..4] && Levenshtein(a, b) <= 2)
            return true;                                                          // « coupe » / « couper »
        return a.Length >= 5 && Levenshtein(a, b) <= 1;
    }

    public static int Levenshtein(string a, string b)
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
