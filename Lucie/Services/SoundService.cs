using Lucie.Models;
using NAudio.Wave;

namespace Lucie.Services;

/// <summary>
/// Petits sons doux synthétisés à la volée (aucun fichier audio nécessaire).
/// </summary>
public sealed class SoundService
{
    private const int SampleRate = 44100;
    private readonly SettingsService _settings;

    private static readonly (double Freq, double Start, double Dur)[] WakeNotes =
        { (659.25, 0.00, 0.09), (880.00, 0.085, 0.16) };          // petit bip montant

    private static readonly (double Freq, double Start, double Dur)[] DoneNotes =
        { (880.00, 0.00, 0.10), (659.25, 0.11, 0.20) };           // deux notes descendantes

    private static readonly (double Freq, double Start, double Dur)[] ErrorNotes =
        { (392.00, 0.00, 0.16), (349.23, 0.15, 0.22) };           // « pas compris », très bas

    public SoundService(SettingsService settings) => _settings = settings;

    public void PlayWake() => Play(WakeNotes);
    public void PlayDone() => Play(DoneNotes);
    public void PlayError() => Play(ErrorNotes);

    /// <summary>Aperçu depuis les paramètres, même si les sons sont coupés.</summary>
    public void Preview(SoundTheme theme, double volume, string? outputName)
    {
        var notes = WakeNotes
            .Concat(DoneNotes.Select(n => (Freq: n.Freq, Start: n.Start + 0.45, Dur: n.Dur)))
            .ToArray();
        PlayRaw(Synth(notes, theme, volume), outputName);
    }

    private void Play((double, double, double)[] notes)
    {
        var s = _settings.Current;
        if (!s.SoundsEnabled || s.SoundVolume <= 0) return;
        PlayRaw(Synth(notes, s.SoundTheme, s.SoundVolume), s.OutputName);
    }

    private static void PlayRaw(float[] samples, string? outputName)
    {
        Task.Run(() =>
        {
            try
            {
                using var output = new WaveOutEvent
                {
                    DeviceNumber = AudioDevices.FindOutput(outputName),
                    DesiredLatency = 120
                };
                using var finished = new ManualResetEventSlim();
                output.PlaybackStopped += (_, _) => finished.Set();
                output.Init(new FloatArrayProvider(samples, SampleRate));
                output.Play();
                finished.Wait(TimeSpan.FromSeconds(4));
            }
            catch
            {
                // Pas de sortie audio : on reste silencieux.
            }
        });
    }

    private static float[] Synth((double Freq, double Start, double Dur)[] notes, SoundTheme theme, double volume)
    {
        const double attack = 0.012;
        const double release = 0.35;
        double decay = theme switch { SoundTheme.Cristal => 4.0, SoundTheme.Bulle => 9.0, _ => 7.0 };

        double total = notes.Max(n => n.Start + n.Dur) + release;
        var buffer = new float[(int)(total * SampleRate)];

        foreach (var (freq, start, dur) in notes)
        {
            int first = (int)(start * SampleRate);
            int count = (int)((dur + release) * SampleRate);
            double phase = 0;

            for (int i = 0; i < count && first + i < buffer.Length; i++)
            {
                double t = (double)i / SampleRate;

                // « Bulle » : la note glisse rapidement vers le haut.
                double f = theme == SoundTheme.Bulle ? freq * (0.8 + 0.2 * Math.Min(1, t / 0.06)) : freq;
                phase += 2 * Math.PI * f / SampleRate;

                double wave = Math.Sin(phase);
                if (theme == SoundTheme.Cristal)
                    wave += 0.22 * Math.Sin(3 * phase) + 0.08 * Math.Sin(5 * phase);

                double env = t < attack ? t / attack : Math.Exp(-(t - attack) * decay);
                if (t > dur) env *= Math.Exp(-(t - dur) * 30);

                buffer[first + i] += (float)(wave * env);
            }
        }

        // Volume volontairement bas : ça doit rester discret.
        float gain = (float)(Math.Clamp(volume, 0, 1) * 0.3);
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = Math.Clamp(buffer[i] * gain, -1f, 1f);

        return buffer;
    }

    private sealed class FloatArrayProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _position;

        public FloatArrayProvider(float[] data, int sampleRate)
        {
            _data = data;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
