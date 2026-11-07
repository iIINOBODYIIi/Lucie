using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Onde.Core.Audio;

namespace Lucie.AirPlay;

/// <summary>Sortie son Windows (WASAPI partagé) qui lit le <see cref="AudioPipeline"/>.</summary>
public sealed class AudioOutput : IDisposable, IMMNotificationClient
{
    private readonly AudioPipeline _pipeline;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _lock = new();
    private WasapiOut? _out;
    private string? _deviceId;
    private bool _disposed;
    private Timer? _restartTimer;

    public AudioOutput(AudioPipeline pipeline)
    {
        _pipeline = pipeline;
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { /* facultatif */ }
    }

    public string? LastError { get; private set; }

    /// <summary>Démarre la sortie. <paramref name="deviceId"/> null = périphérique par défaut de Windows.</summary>
    public void Start(string? deviceId)
    {
        lock (_lock)
        {
            if (_disposed) return;
            StopInternal();
            _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            try
            {
                MMDevice? dev = null;
                if (_deviceId != null)
                {
                    try { dev = _enumerator.GetDevice(_deviceId); if (dev.State != DeviceState.Active) dev = null; }
                    catch { dev = null; }
                }
                dev ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
                o.PlaybackStopped += OnPlaybackStopped;
                o.Init(new PipelineProvider(_pipeline));
                o.Play();
                _out = o;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ScheduleRestart(3000);
            }
        }
    }

    public static List<(string Id, string Name)> ListDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch { /* aucun périphérique */ }
        return list;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // périphérique débranché, changement de format… on relance
        if (!_disposed && e.Exception != null) ScheduleRestart(1000);
    }

    private void ScheduleRestart(int delayMs)
    {
        _restartTimer?.Dispose();
        _restartTimer = new Timer(_ => Start(_deviceId), null, delayMs, Timeout.Infinite);
    }

    private void StopInternal()
    {
        if (_out == null) return;
        _out.PlaybackStopped -= OnPlaybackStopped;
        try { _out.Stop(); } catch { }
        _out.Dispose();
        _out = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _restartTimer?.Dispose();
            StopInternal();
        }
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }

    // --- suivi du périphérique par défaut de Windows ---
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia && _deviceId == null)
            ScheduleRestart(300);
    }
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    private sealed class PipelineProvider : ISampleProvider
    {
        private readonly AudioPipeline _p;
        public PipelineProvider(AudioPipeline p) => _p = p;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioPipeline.SampleRate, AudioPipeline.Channels);
        public int Read(float[] buffer, int offset, int count) => _p.Read(buffer, offset, count);
    }
}
