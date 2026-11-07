using System.Diagnostics;
using Lucie.Models;
using Lucie.Native;

namespace Lucie.Services;

/// <summary>
/// Ouvre une appli, ou la met au premier plan si elle tourne déjà
/// (même cachée dans la zone de notification, comme Discord).
/// </summary>
public static class AppLauncher
{
    public static async Task<bool> OpenOrShowAsync(AppEntry app)
    {
        string path = Environment.ExpandEnvironmentVariables(app.Path.Trim().Trim('"'));
        if (path.Length == 0) return false;

        var processNames = ProcessNamesFor(app, path);

        // Déjà ouverte avec une fenêtre visible : on la montre.
        var window = FindWindow(processNames);
        if (window != IntPtr.Zero)
        {
            Win32.BringToFront(window);
            return true;
        }

        // Fermée, ou cachée en arrière-plan : on (re)lance. La plupart des applis
        // déjà ouvertes réaffichent alors simplement leur fenêtre.
        Launch(path, app.Arguments);

        // On attend que sa fenêtre apparaisse pour la passer devant (jusqu'à ~12 s).
        for (int i = 0; i < 24; i++)
        {
            await Task.Delay(500);
            window = FindWindow(processNames);
            if (window != IntPtr.Zero)
            {
                await Task.Delay(250);
                Win32.BringToFront(window);
                break;
            }
        }
        return true;
    }

    public static void Launch(string target, string arguments)
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

    private static List<string> ProcessNamesFor(AppEntry app, string path)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(app.ProcessName)) names.Add(app.ProcessName.Trim());
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !path.Contains("://"))
            names.Add(Path.GetFileNameWithoutExtension(path));
        // Pour un raccourci du menu Démarrer : on devine à partir du nom (« Spotify » → Spotify.exe).
        foreach (var n in app.Name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            names.Add(n.Replace(" ", ""));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IntPtr FindWindow(List<string> processNames)
    {
        var ids = new HashSet<uint>();
        foreach (var name in processNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    ids.Add((uint)p.Id);
                    p.Dispose();
                }
            }
            catch
            {
            }
        }
        return ids.Count == 0 ? IntPtr.Zero : Win32.FindMainWindow(ids);
    }

    /// <summary>Retrouve une appli installée à partir de son nom, via les raccourcis du menu Démarrer.</summary>
    public static AppEntry? FindStartMenuApp(string spokenName)
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

                int score = name == query ? 4
                          : name.StartsWith(query) ? 3
                          : name.Contains(query) || query.Contains(name) ? 2
                          : CommandInterpreter.Levenshtein(name, query) <= Math.Max(1, query.Length / 4) ? 1
                          : 0;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = file;
                    if (score == 4) break;
                }
            }
        }

        return best is null
            ? null
            : new AppEntry { Name = Path.GetFileNameWithoutExtension(best), Path = best };
    }
}
