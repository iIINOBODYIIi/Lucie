using System;
using System.Threading.Tasks;
using Onde.Core.AirPlay;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Lucie.AirPlay;

/// <summary>
/// Déclare Onde comme lecteur multimédia auprès de Windows (contrôles multimédias système).
/// Résultat : Wallpaper Engine, les widgets « en cours de lecture », le menu volume de Windows
/// et les touches multimédias du clavier voient le titre, l'artiste, la pochette et l'état de lecture.
/// </summary>
public sealed class MediaSession : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly SystemMediaTransportControls _smtc;
    private InMemoryRandomAccessStream? _artwork;
    private bool _enabled = true;

    /// <summary>Touche multimédia / bouton Windows pressé.</summary>
    public event Action<RemoteCommand>? CommandRequested;

    public MediaSession()
    {
        // Un MediaPlayer "vide" sert juste à obtenir des contrôles multimédias pour une appli de bureau.
        _player = new MediaPlayer();
        _player.CommandManager.IsEnabled = false;
        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsStopEnabled = false;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        _smtc.IsEnabled = false; // visible seulement quand un iPhone est connecté
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) _smtc.IsEnabled = false;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var cmd = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => RemoteCommand.Play,
            SystemMediaTransportControlsButton.Pause => RemoteCommand.Pause,
            SystemMediaTransportControlsButton.Next => RemoteCommand.Next,
            SystemMediaTransportControlsButton.Previous => RemoteCommand.Previous,
            _ => (RemoteCommand?)null,
        };
        if (cmd is { } c) CommandRequested?.Invoke(c);
    }

    /// <summary>État de lecture : aucun iPhone, en lecture ou en pause.</summary>
    public void SetStatus(bool connected, bool playing)
    {
        try
        {
            if (!_enabled || !connected)
            {
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                _smtc.IsEnabled = false;
                return;
            }
            _smtc.IsEnabled = true;
            _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch { /* l'interface multimédia de Windows peut être indisponible */ }
    }

    public void UpdateTrack(TrackInfo t)
    {
        try
        {
            var du = _smtc.DisplayUpdater;
            du.Type = MediaPlaybackType.Music;
            du.MusicProperties.Title = string.IsNullOrWhiteSpace(t.Title) ? "AirPlay" : t.Title;
            du.MusicProperties.Artist = t.Artist ?? "";
            du.MusicProperties.AlbumTitle = t.Album ?? "";
            du.AppMediaId = "Onde";
            du.Update();
        }
        catch { }
    }

    public async Task UpdateArtworkAsync(byte[]? data)
    {
        try
        {
            var du = _smtc.DisplayUpdater;
            if (data == null || data.Length == 0)
            {
                du.Thumbnail = null;
            }
            else
            {
                var ms = new InMemoryRandomAccessStream();
                using (var w = new DataWriter(ms.GetOutputStreamAt(0)))
                {
                    w.WriteBytes(data);
                    await w.StoreAsync();
                    await w.FlushAsync();
                }
                ms.Seek(0);
                du.Thumbnail = RandomAccessStreamReference.CreateFromStream(ms);
                _artwork?.Dispose();
                _artwork = ms; // garder le flux vivant tant que Windows affiche la pochette
            }
            du.Update();
        }
        catch { }
    }

    public void UpdateTimeline(double position, double duration)
    {
        if (duration <= 0) return;
        try
        {
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromSeconds(duration),
                MaxSeekTime = TimeSpan.FromSeconds(duration),
                Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)),
            });
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.IsEnabled = false;
            _player.Dispose();
            _artwork?.Dispose();
        }
        catch { }
    }
}
