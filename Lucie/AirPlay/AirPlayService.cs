using System.Diagnostics;
using Onde.Core.AirPlay;
using Onde.Core.Audio;
using Onde.Core.Dsp;

namespace Lucie.AirPlay;

/// <summary>
/// L'enceinte AirPlay de Lucie (le moteur d'Onde) : réception depuis l'iPhone, sortie son,
/// égaliseur, mode concert, et partage du morceau avec Windows.
/// Les événements arrivent sur des fils d'arrière-plan : les fenêtres les renvoient vers leur interface.
/// </summary>
public sealed class AirPlayService : IDisposable
{
    private Timer? _saveTimer;
    private Timer? _statusTimer;
    private bool? _lastPlaying;
    private bool _lastConnected;
    private DateTime _lastTick = DateTime.UtcNow;

    public AirPlaySettings Settings { get; } = AirPlaySettings.Load();
    public AudioPipeline Pipeline { get; } = new();
    public AirPlayReceiver? Receiver { get; private set; }
    public AudioOutput? Output { get; private set; }
    public MediaSession? Media { get; private set; }
    public string? StartupError { get; private set; }

    public bool IsRunning => Receiver is not null;
    public bool IsConnected => Receiver is { State: not ReceiverState.Waiting };
    public bool IsPlaying => IsConnected && Pipeline.IsPlaying;
    public TrackInfo Track => Receiver?.Track ?? TrackInfo.Empty;
    public byte[]? Artwork { get; private set; }

    /// <summary>Position et durée du morceau (secondes), avancées en continu pendant la lecture.</summary>
    public double Position { get; private set; }
    public double Duration { get; private set; }

    // Interface
    public event Action? RunningChanged;
    public event Action? ReceiverStateChanged;
    public event Action<TrackInfo>? TrackChanged;
    public event Action<byte[]?>? ArtworkChanged;
    public event Action? AppearanceChanged;
    public event Action? EffectsChanged;
    /// <summary>La taille du widget a changé à la souris : les paramètres se mettent à jour.</summary>
    public event Action? SizeChangedByWidget;
    /// <summary>Les paramètres demandent une nouvelle taille de widget.</summary>
    public event Action? ResizeRequested;

    public AirPlayService()
    {
        ApplyAudioSettings();
    }

    // ───────────────────────── Allumer / éteindre ─────────────────────────

    public void Start()
    {
        if (IsRunning) return;
        StartupError = null;
        ApplyAudioSettings();

        Output = new AudioOutput(Pipeline);
        Output.Start(Settings.OutputDeviceId);

        var receiver = new AirPlayReceiver(Pipeline, Settings.ReceiverName, Settings.GetHardwareAddress());
        receiver.StateChanged += (_, _) =>
        {
            if (!IsConnected) { Position = Duration = 0; }
            ReceiverStateChanged?.Invoke();
        };
        receiver.TrackChanged += (_, t) =>
        {
            Media?.UpdateTrack(t);
            TrackChanged?.Invoke(t);
        };
        receiver.ProgressChanged += (_, p) =>
        {
            Position = p.Position;
            Duration = p.Duration;
            Media?.UpdateTimeline(Position, Duration);
        };
        receiver.ArtworkChanged += (_, img) =>
        {
            Artwork = img;
            _ = Media?.UpdateArtworkAsync(img);
            ArtworkChanged?.Invoke(img);
        };

        Receiver = receiver;
        try
        {
            receiver.Start();
        }
        catch (Exception ex)
        {
            StartupError = "Réseau indisponible : " + ex.Message;
        }

        try
        {
            Media = new MediaSession { Enabled = Settings.SystemMediaIntegration };
            Media.CommandRequested += cmd => _ = Receiver?.SendCommandAsync(cmd);
        }
        catch
        {
            Media = null;   // Windows trop ancien : pas grave
        }

        _statusTimer = new Timer(_ => OnStatusTick(), null, 250, 250);
        RunningChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _statusTimer?.Dispose();
        _statusTimer = null;
        try { Media?.Dispose(); } catch { }
        try { Receiver?.Dispose(); } catch { }
        try { Output?.Dispose(); } catch { }
        Media = null;
        Receiver = null;
        Output = null;
        Pipeline.Flush();
        Position = Duration = 0;
        Artwork = null;
        _lastPlaying = null;
        _lastConnected = false;
        RunningChanged?.Invoke();
    }

    /// <summary>Allume ou éteint l'enceinte AirPlay (et s'en souvient).</summary>
    public void SetEnabled(bool on)
    {
        Settings.Enabled = on;
        SaveSoon();
        if (on) Start(); else Stop();
    }

    /// <summary>Avance la position du morceau et informe Windows quand la lecture démarre / s'arrête.</summary>
    private void OnStatusTick()
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        bool playing = Pipeline.IsPlaying;
        if (playing && Duration > 0) Position = Math.Min(Duration, Position + dt);

        bool connected = IsConnected;
        if (_lastPlaying != playing || _lastConnected != connected)
        {
            _lastPlaying = playing;
            _lastConnected = connected;
            Media?.SetStatus(connected, playing);
            if (connected) Media?.UpdateTimeline(Position, Duration);
        }
    }

    // ───────────────────────── Réglages ─────────────────────────

    /// <summary>Pousse les réglages audio vers le moteur.</summary>
    public void ApplyAudioSettings()
    {
        var s = Settings;
        Pipeline.LocalVolume = (float)Math.Clamp(s.LocalVolume, 0, 1);
        Pipeline.BufferMs = Math.Clamp(s.BufferMs, 100, 2000);
        Pipeline.Equalizer.SetGains(s.EqGains);
        Pipeline.Equalizer.SetPreamp(s.EqPreamp);
        Pipeline.Equalizer.Enabled = s.EqEnabled;
        Pipeline.Concert.Settings = s.Concert;
        Pipeline.Concert.Enabled = s.ConcertEnabled;
    }

    /// <summary>Sauvegarde différée (évite d'écrire le fichier à chaque mouvement de curseur).</summary>
    public void SaveSoon()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Settings.Save(), null, 600, Timeout.Infinite);
    }

    public void Rename(string name)
    {
        Settings.ReceiverName = name;
        try { Receiver?.Rename(name); } catch { }
        SaveSoon();
        NotifyAppearanceChanged();
    }

    public void SetOutputDevice(string? deviceId)
    {
        Settings.OutputDeviceId = deviceId;
        Output?.Start(deviceId);
        SaveSoon();
    }

    public void SetMediaIntegration(bool on)
    {
        Settings.SystemMediaIntegration = on;
        if (Media is not null)
        {
            Media.Enabled = on;
            Media.SetStatus(IsConnected, Pipeline.IsPlaying);
            if (Receiver is not null) Media.UpdateTrack(Receiver.Track);
        }
        SaveSoon();
    }

    public void NotifyAppearanceChanged() => AppearanceChanged?.Invoke();
    public void NotifyEffectsChanged() => EffectsChanged?.Invoke();
    public void NotifySizeChangedByWidget() => SizeChangedByWidget?.Invoke();
    public void RequestResize() => ResizeRequested?.Invoke();

    // ───────────────────────── Effets (aussi utilisés par la voix) ─────────────────────────

    public void SetEqEnabled(bool on)
    {
        Settings.EqEnabled = on;
        AfterEffects();
    }

    public bool ApplyEqPreset(string name)
    {
        var preset = EqPresets.All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset.Gains is null) return false;
        Settings.EqPreset = preset.Name;
        Settings.EqGains = (double[])preset.Gains.Clone();
        Settings.EqEnabled = true;
        AfterEffects();
        return true;
    }

    public void ResetEq()
    {
        Settings.EqGains = new double[10];
        Settings.EqPreamp = 0;
        Settings.EqPreset = "Plat";
        AfterEffects();
    }

    /// <summary>Monte ou baisse un groupe de bandes (basses, médiums, aigus).</summary>
    public void AdjustBands(int[] bands, double deltaDb)
    {
        var gains = (double[])Settings.EqGains.Clone();
        foreach (var b in bands)
            gains[b] = Math.Clamp(gains[b] + deltaDb, -Equalizer.MaxGainDb, Equalizer.MaxGainDb);
        Settings.EqGains = gains;
        Settings.EqPreset = "Personnalisé";
        Settings.EqEnabled = true;
        // Garde une marge pour ne pas saturer quand on monte beaucoup
        double max = gains.Max();
        if (max > 6 && Settings.EqPreamp > -(max - 6)) Settings.EqPreamp = -(max - 6);
        AfterEffects();
    }

    public void SetConcertEnabled(bool on)
    {
        Settings.ConcertEnabled = on;
        AfterEffects();
    }

    public bool ApplyConcertPreset(string name)
    {
        var preset = ConcertPresets.All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset.Settings is null) return false;
        Settings.ConcertPreset = preset.Name;
        Settings.Concert = preset.Settings;
        Settings.ConcertEnabled = true;
        AfterEffects();
        return true;
    }

    public void SetVolume(double volume)
    {
        Settings.LocalVolume = Math.Clamp(volume, 0, 1);
        Pipeline.LocalVolume = (float)Settings.LocalVolume;
        SaveSoon();
        NotifyEffectsChanged();
    }

    private void AfterEffects()
    {
        ApplyAudioSettings();
        SaveSoon();
        NotifyEffectsChanged();
    }

    // ───────────────────────── iPhone ─────────────────────────

    public async Task<bool> SendAsync(RemoteCommand cmd)
    {
        var r = Receiver;
        if (r is null || r.State == ReceiverState.Waiting) return false;
        try { return await r.SendCommandAsync(cmd); }
        catch { return false; }
    }

    // ───────────────────────── Onde & pare-feu ─────────────────────────

    /// <summary>L'ancienne appli Onde tourne-t-elle encore ? (les deux se gêneraient)</summary>
    public static bool IsOndeRunning()
    {
        try
        {
            var list = Process.GetProcessesByName("Onde");
            bool any = list.Length > 0;
            foreach (var p in list) p.Dispose();
            return any;
        }
        catch
        {
            return false;
        }
    }

    public static void CloseOnde()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Onde"))
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
        }
        catch { }
    }

    /// <summary>
    /// Ajoute la règle de pare-feu pour que l'iPhone voie le PC (demande l'autorisation administrateur).
    /// </summary>
    public static bool AddFirewallRule()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        string args = "/c netsh advfirewall firewall delete rule name=\"Lucie (AirPlay)\" & " +
                      $"netsh advfirewall firewall add rule name=\"Lucie (AirPlay)\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,public";
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            p?.WaitForExit(15000);
            return p is not null && p.ExitCode == 0;
        }
        catch
        {
            return false;   // l'utilisateur a refusé
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        Settings.Save();
        Stop();
    }
}
