namespace Lucie.Services;

/// <summary>Les petites réactions de Lucie (elle n'a pas de voix : elle répond avec son visage).</summary>
public enum Reaction
{
    Wink,      // clin d'œil
    Happy,     // yeux ^ ^ et petit saut de joie
    Greet,     // coucou : yeux ^ ^, saut et tête penchée
    Nod,       // hoche la tête (oui)
    Shake,     // secoue la tête (non)
    Blush,     // gênée : yeux ^ ^ et joues roses
    Love,      // yeux en cœur
    Sleepy,    // bâille et ferme les yeux
    Surprised, // grands yeux
    Confused   // tête penchée, un œil plissé
}

/// <summary>
/// Phrases « humaines » qui ne déclenchent aucune action Windows, juste une réaction.
/// </summary>
public static class SmallTalk
{
    private static readonly (Reaction Reaction, string[] Phrases)[] Table =
    {
        (Reaction.Love,      new[] { "je t aime", "je t adore", "t es mignonne", "tu es mignonne", "trop mignonne", "t es trop belle", "tu es belle", "bisou", "bisous", "gros bisous" }),
        (Reaction.Blush,     new[] { "merci", "merci beaucoup", "t es la meilleure", "tu es la meilleure", "bien joue", "bravo", "t es geniale", "tu es geniale", "trop forte", "t es forte", "tu es forte", "c est gentil" }),
        (Reaction.Happy,     new[] { "ca va bien", "je vais bien", "tres bien", "ca va super", "super", "genial", "trop bien", "nickel", "parfait", "youpi", "je suis content", "je suis contente" }),
        (Reaction.Wink,      new[] { "comment tu vas", "comment ca va", "comment vas tu", "ca va", "tu vas bien", "t as la forme", "tu as la forme", "la forme", "quoi de neuf", "comment tu t appelles", "qui es tu", "t es qui", "tu es qui" }),
        (Reaction.Greet,     new[] { "bonjour", "salut", "coucou", "hello", "bonsoir", "hey", "yo", "re", "rebonjour", "me revoila", "je suis la", "je suis rentre", "je suis rentree" }),
        (Reaction.Nod,       new[] { "oui", "d accord", "ok", "ouais", "exactement", "tu m entends", "t es la", "tu es la", "t es reveillee", "tu es reveillee" }),
        (Reaction.Shake,     new[] { "non", "t es nulle", "tu es nulle", "mechante", "pas du tout", "n importe quoi", "laisse tomber" }),
        (Reaction.Sleepy,    new[] { "bonne nuit", "dors", "va dormir", "fais dodo", "au dodo", "a demain", "je vais dormir", "je vais me coucher" }),
        (Reaction.Surprised, new[] { "reveille toi", "debout", "surprise", "regarde", "waouh", "wow" }),
    };

    /// <summary>Trouve la réaction qui correspond à la phrase, ou null.</summary>
    public static Reaction? Match(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText)) return null;
        string padded = $" {normalizedText} ";
        int wordCount = normalizedText.Split(' ').Length;

        Reaction? best = null;
        int bestLength = 0;
        foreach (var (reaction, phrases) in Table)
        {
            foreach (var p in phrases)
            {
                // Les mots tout seuls (oui, non, salut…) doivent être toute la phrase,
                // sinon « ouvre non… » ou « salut ouvre discord » réagiraient par erreur.
                bool single = !p.Contains(' ');
                bool hit = single ? normalizedText == p || (wordCount <= 2 && padded.Contains($" {p} "))
                                  : padded.Contains($" {p} ");
                if (hit && p.Length > bestLength)
                {
                    best = reaction;
                    bestLength = p.Length;
                }
            }
        }
        return best;
    }
}
