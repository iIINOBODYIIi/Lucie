using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Lucie.Services;

/// <summary>
/// Lit la musique en cours via les contrôles média de Windows
/// (Spotify, Apple Music, navigateur, VLC…). Événements sur un fil d'arrière-plan.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _lastKey = "";
    private DateTimeOffset _positionUpdated;

    public event Action? Changed;
    public event Action? TrackChanged;

    public bool IsPlaying { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    /// <summary>Identifiant Windows de l'appli qui joue (ex. « Spotify.exe », « AppleInc.AppleMusicWin_… »).</summary>
    public string SourceApp { get; private set; } = "";
    public IRandomAccessStreamReference? Thumbnail { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }

    public bool HasTrack => Title.Length > 0;

    /// <summary>Position estimée maintenant (la dernière position connue + le temps écoulé depuis).</summary>
    public TimeSpan EstimatedPosition
    {
        get
        {
            var position = Position;
            if (IsPlaying && _positionUpdated.Year > 2000)
                position += DateTimeOffset.Now - _positionUpdated;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
            return position;
        }
    }

    /// <summary>Avancement du morceau entre 0 et 1, estimé entre deux mises à jour.</summary>
    public double Progress
    {
        get
        {
            if (Duration <= TimeSpan.Zero) return 0;
            var position = Position;
            if (IsPlaying && _positionUpdated.Year > 2000)
                position += DateTimeOffset.Now - _positionUpdated;
            return Math.Clamp(position / Duration, 0, 1);
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ = AttachAsync();
            await AttachAsync();
        }
        catch
        {
            // Contrôles média indisponibles : Lucie fonctionne quand même.
        }
    }

    private async Task AttachAsync()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnProperties;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = _manager?.GetCurrentSession();
        if (_session is null)
        {
            IsPlaying = false;
            Title = Artist = Album = SourceApp = "";
            Thumbnail = null;
            Changed?.Invoke();
            return;
        }

        try { SourceApp = _session.SourceAppUserModelId ?? ""; } catch { SourceApp = ""; }
        _session.MediaPropertiesChanged += OnProperties;
        _session.PlaybackInfoChanged += OnPlayback;
        _session.TimelinePropertiesChanged += OnTimeline;

        await RefreshPropertiesAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    private void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshPropertiesAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => RefreshPlayback();
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => RefreshTimeline();

    private async Task RefreshPropertiesAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            Title = props.Title ?? "";
            Artist = props.Artist ?? "";
            Album = props.AlbumTitle ?? "";
            Thumbnail = props.Thumbnail;

            string key = Title + "|" + Artist;
            bool isNew = key != _lastKey && Title.Length > 0;
            _lastKey = key;

            Changed?.Invoke();
            if (isNew) TrackChanged?.Invoke();
        }
        catch
        {
        }
    }

    private void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Changed?.Invoke();
        }
        catch
        {
        }
    }

    private void RefreshTimeline()
    {
        try
        {
            if (_session is null) return;
            var t = _session.GetTimelineProperties();
            Position = t.Position;
            Duration = t.EndTime - t.StartTime;
            _positionUpdated = t.LastUpdatedTime;
            Changed?.Invoke();
        }
        catch
        {
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        if (_session is not null) return await _session.TryTogglePlayPauseAsync();
        KeyboardSimulator.Tap(KeyboardSimulator.VK_MEDIA_PLAY_PAUSE);
        return true;
    }

    public async Task<bool> PlayAsync()
    {
        if (_session is not null) return await _session.TryPlayAsync() || IsPlaying;
        KeyboardSimulator.Tap(KeyboardSimulator.VK_MEDIA_PLAY_PAUSE);
        return true;
    }

    public async Task<bool> PauseAsync()
    {
        if (_session is not null) return await _session.TryPauseAsync() || !IsPlaying;
        KeyboardSimulator.Tap(KeyboardSimulator.VK_MEDIA_PLAY_PAUSE);
        return true;
    }

    public async Task<bool> NextAsync()
    {
        if (_session is not null) return await _session.TrySkipNextAsync();
        KeyboardSimulator.Tap(KeyboardSimulator.VK_MEDIA_NEXT);
        return true;
    }

    public async Task<bool> PreviousAsync()
    {
        if (_session is not null) return await _session.TrySkipPreviousAsync();
        KeyboardSimulator.Tap(KeyboardSimulator.VK_MEDIA_PREV);
        return true;
    }
}
