namespace Lucie.Services;

/// <summary>Retrouve une appli installée à partir de son nom, via les raccourcis du menu Démarrer.</summary>
public static class AppLauncher
{
    public static string? FindStartMenuApp(string spokenName)
    {
        string query = CommandInterpreter.Normalize(spokenName);
        if (query.Length < 2) return null;

        string[] roots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };

        string? best = null;
        int bestScore = 0;

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                string name = CommandInterpreter.Normalize(Path.GetFileNameWithoutExtension(file));
                if (name.Contains("uninstall") || name.Contains("desinstall")) continue;

                int score = name == query ? 3
                          : name.StartsWith(query) ? 2
                          : name.Contains(query) || query.Contains(name) ? 1
                          : 0;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = file;
                    if (score == 3) return best;
                }
            }
        }

        return best;
    }
}
