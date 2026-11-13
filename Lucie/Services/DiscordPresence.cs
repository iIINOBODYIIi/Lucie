using System.Net.Http;
using System.Text.Json.Nodes;

namespace Lucie.Services;

/// <summary>
/// Statut « Écoute » sur le profil Discord : titre, artiste, album, pochette,
/// barre de progression et bouton pour écouter le morceau.
/// Suit la musique de n'importe quel lecteur Windows (Apple Music, navigateur, VLC…)
/// et celle de l'iPhone via AirPlay.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly MediaService _media;
    private readonly DiscordService _discord;
    private readonly Timer _debounce;
    private readonly Timer _retry;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Dictionary<string, Artwork> _artworks = new();

    private string _sentKey = "";
    private bool _shown;
    private bool _disposed;

    private sealed record Artwork(string? ImageUrl, string? TrackUrl);

    public DiscordPresence(MediaService media, DiscordService discord)
    {
        _media = media;
        _discord = discord;

        // On attend un peu après chaque changement : Windows envoie souvent plusieurs
        // événements d'affilée, et Discord limite le nombre de mises à jour.
        _debounce = new Timer(_ => _ = UpdateAsync(), null, Timeout.Infinite, Timeout.Infinite);
        // Si Discord était fermé, on réessaie de temps en temps.
        _retry = new Timer(_ => _ = UpdateAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        _media.Changed += Schedule;
        _discord.PresenceSettingsChanged += Refresh;
        _discord.StatusChanged += OnDiscordStatus;
        Schedule();
    }

    /// <summary>Renvoie le statut même s'il n'a pas changé (après un réglage modifié).</summary>
    public void Refresh()
    {
        _sentKey = "";
        Schedule();
    }

    /// <summary>Discord fermé ou redémarré : son statut est perdu, il faudra le renvoyer.</summary>
    private void OnDiscordStatus()
    {
        if (_discord.IsPipeConnected) return;
        _shown = false;
        _sentKey = "";
        Schedule();
    }

    private void Schedule()
    {
        if (!_disposed) _debounce.Change(1500, Timeout.Infinite);
    }

    private async Task UpdateAsync()
    {
        if (_disposed || !await _busy.WaitAsync(0)) return;
        try
        {
            if (!_discord.HasClientId) return;

            if (!ShouldShow())
            {
                if (_shown && await _discord.SetActivityAsync(null))
                {
                    _shown = false;
                    _sentKey = "";
                }
                return;
            }

            string title = _media.Title.Trim();
            string artist = _media.Artist.Trim();
            string album = _media.Album.Trim();
            var duration = _media.Duration;

            // Début et fin du morceau en heure Discord : c'est ce qui dessine la barre de progression.
            var start = DateTimeOffset.UtcNow - _media.EstimatedPosition;
            long startMs = start.ToUnixTimeMilliseconds();

            // Arrondi à 3 s : une petite dérive ne renvoie rien, un saut dans le morceau oui.
            string key = $"{title}|{artist}|{album}|{_discord.PresenceShowTitle}|{startMs / 3000}";
            if (_shown && key == _sentKey) return;

            var art = await FindArtworkAsync(title, artist);

            var activity = new JsonObject
            {
                ["type"] = 2,                                              // « Écoute »
                ["status_display_type"] = _discord.PresenceShowTitle ? 2 : 0, // 2 = titre, 0 = nom de l'appli (Lucie)
                ["details"] = Fit(title),
            };
            if (art.TrackUrl is { } trackUrl) activity["details_url"] = trackUrl;
            if (artist.Length > 0) activity["state"] = Fit(artist);

            var timestamps = new JsonObject { ["start"] = startMs };
            if (duration > TimeSpan.Zero)
                timestamps["end"] = (start + duration).ToUnixTimeMilliseconds();
            activity["timestamps"] = timestamps;

            var assets = new JsonObject
            {
                // Pochette trouvée en ligne, sinon l'image « lucie » envoyée sur le portail Discord.
                ["large_image"] = art.ImageUrl ?? "lucie",
                ["large_text"] = Fit(album.Length > 0 ? album : title),
            };
            if (art.ImageUrl is not null)
            {
                assets["small_image"] = "lucie";
                assets["small_text"] = SourceName() is { } source ? $"Lucie · {source}" : "Lucie";
            }
            activity["assets"] = assets;

            if (art.TrackUrl is { } url)
                activity["buttons"] = new JsonArray(new JsonObject { ["label"] = "Écouter sur Apple Music", ["url"] = url });

            if (await _discord.SetActivityAsync(activity))
            {
                _shown = true;
                _sentKey = key;
            }
        }
        catch
        {
            // Le statut Discord ne doit jamais gêner Lucie.
        }
        finally
        {
            _busy.Release();
        }
    }

    private bool ShouldShow()
    {
        if (!_discord.PresenceEnabled || !_media.IsPlaying || !_media.HasTrack) return false;
        // Spotify affiche déjà « Écoute Spotify » tout seul : pas de doublon.
        if (_discord.PresenceSkipSpotify && _media.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Nom lisible du lecteur qui joue la musique.</summary>
    private string? SourceName()
    {
        var app = _media.SourceApp;
        if (app.Length == 0) return null;
        if (app.Contains("Lucie", StringComparison.OrdinalIgnoreCase)) return "AirPlay";
        if (app.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) || app.Contains("iTunes", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (app.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (app.Contains("Deezer", StringComparison.OrdinalIgnoreCase)) return "Deezer";
        if (app.Contains("TIDAL", StringComparison.OrdinalIgnoreCase)) return "TIDAL";
        if (app.Contains("vlc", StringComparison.OrdinalIgnoreCase)) return "VLC";
        if (app.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (app.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (app.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (app.Contains("opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
        return null;
    }

    /// <summary>
    /// Discord n'accepte que des images en ligne : on cherche la pochette (et le lien du morceau)
    /// dans le catalogue Apple Music, gratuit et sans clé.
    /// </summary>
    private async Task<Artwork> FindArtworkAsync(string title, string artist)
    {
        string key = (artist + "|" + title).ToLowerInvariant();
        lock (_artworks)
            if (_artworks.TryGetValue(key, out var cached)) return cached;

        var result = new Artwork(null, null);
        try
        {
            string term = Uri.EscapeDataString($"{artist} {title}".Trim());
            var json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=1&country=FR");
            var results = JsonNode.Parse(json)?["results"] as JsonArray;
            var hit = results is { Count: > 0 } ? results[0] : null;
            if (hit is not null)
            {
                var image = hit["artworkUrl100"]?.GetValue<string>()?.Replace("100x100bb", "600x600bb");
                var url = hit["trackViewUrl"]?.GetValue<string>();
                result = new Artwork(
                    image is { Length: > 0 } && image.Length <= 256 ? image : null,
                    url is { Length: > 0 } && url.Length <= 512 ? url : null);
            }
        }
        catch
        {
            // Pas d'internet ou rien trouvé : le statut s'affiche quand même, sans pochette.
            return result;   // pas mis en cache, on réessaiera au prochain morceau
        }

        lock (_artworks)
        {
            if (_artworks.Count > 300) _artworks.Clear();
            _artworks[key] = result;
        }
        return result;
    }

    /// <summary>Discord veut entre 2 et 128 caractères.</summary>
    private static string Fit(string text)
    {
        text = text.Trim();
        if (text.Length > 128) text = text[..127] + "…";
        if (text.Length < 2) text = text.PadRight(2, ' ');
        return text;
    }

    public void Dispose()
    {
        _disposed = true;
        _media.Changed -= Schedule;
        _discord.PresenceSettingsChanged -= Refresh;
        _discord.StatusChanged -= OnDiscordStatus;
        _debounce.Dispose();
        _retry.Dispose();
    }
}
