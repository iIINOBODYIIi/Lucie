using System.IO.Compression;

namespace Lucie.Services;

/// <summary>
/// Télécharge et installe tout seul le modèle vocal français (Vosk, ~40 Mo)
/// dans %LocalAppData%\Lucie\VoiceModel\vosk-model-fr.
/// </summary>
public static class ModelDownloader
{
    private static readonly string[] Urls =
    {
        "https://alphacephei.com/vosk/models/vosk-model-small-fr-0.22.zip",
        // Copie de secours si le site officiel ne répond pas
        "https://huggingface.co/rhasspy/vosk-models/resolve/main/fr/vosk-model-small-fr-0.22.zip",
    };

    /// <param name="progress">Avancement entre 0 et 1 (ou -1 si la taille est inconnue).</param>
    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken token = default)
    {
        Directory.CreateDirectory(SettingsService.DataDir);
        string zip = Path.Combine(SettingsService.DataDir, "vosk-model-fr.zip");
        string extract = Path.Combine(SettingsService.DataDir, "vosk-extract");

        Exception? last = null;
        foreach (var url in Urls)
        {
            try
            {
                await DownloadFileAsync(url, zip, progress, token);
                last = null;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (last is not null) throw new InvalidOperationException("Téléchargement impossible : " + last.Message, last);

        try
        {
            progress.Report(-1);
            if (Directory.Exists(extract)) Directory.Delete(extract, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, extract), token);

            // Le zip contient un dossier « vosk-model-small-fr-0.22 » : on le renomme.
            string source = Directory.GetDirectories(extract).FirstOrDefault() ?? extract;
            string target = SpeechService.PreferredModelDir;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(source, target);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
        }
    }

    private static async Task DownloadFileAsync(string url, string destination, IProgress<double> progress, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(destination);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;
            progress.Report(total is > 0 ? (double)done / total.Value : -1);
        }
    }
}
