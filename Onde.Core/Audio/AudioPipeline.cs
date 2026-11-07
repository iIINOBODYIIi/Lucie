using System;
using Onde.Core.Dsp;

namespace Onde.Core.Audio;

/// <summary>
/// Tampon PCM + chaîne d'effets (volume → égaliseur → concert → limiteur).
/// Le récepteur AirPlay écrit dedans, la carte son lit dedans (float stéréo 44,1 kHz).
/// </summary>
public sealed class AudioPipeline
{
    public const int SampleRate = 44100;
    public const int Channels = 2;

    private readonly float[] _ring = new float[SampleRate * Channels * 6]; // 6 s
    private int _write, _read, _count; // en échantillons (pas en trames)
    private readonly object _lock = new();
    private bool _primed;
    private int _driftCounter;
    private float _currentGain;

    public AudioPipeline()
    {
        Equalizer = new Equalizer(SampleRate);
        Concert = new ConcertEffect(SampleRate);
    }

    public Equalizer Equalizer { get; }
    public ConcertEffect Concert { get; }

    /// <summary>Latence de mise en mémoire tampon, en millisecondes.</summary>
    public int BufferMs { get; set; } = 350;

    /// <summary>Volume envoyé par l'iPhone (0..1).</summary>
    public float RemoteVolume { get; set; } = 1f;

    /// <summary>Volume local du widget (0..1).</summary>
    public float LocalVolume { get; set; } = 0.8f;

    public bool Muted { get; set; }

    /// <summary>Niveau crête de sortie récent (0..1), utile pour un vumètre.</summary>
    public float Peak { get; private set; }

    public bool IsPlaying => _primed;

    public int BufferedFrames { get { lock (_lock) return _count / Channels; } }

    /// <summary>Ajoute du PCM 16 bits entrelacé stéréo.</summary>
    public void Write(short[] pcm, int frames)
    {
        lock (_lock)
        {
            int n = frames * Channels;
            for (int i = 0; i < n; i++)
            {
                _ring[_write] = pcm[i] / 32768f;
                if (++_write == _ring.Length) _write = 0;
            }
            _count += n;
            if (_count > _ring.Length)
            {
                // débordement : on jette le plus ancien
                int drop = _count - _ring.Length;
                _read = (_read + drop) % _ring.Length;
                _count = _ring.Length;
            }
        }
    }

    public void WriteSilence(int frames)
    {
        lock (_lock)
        {
            int n = Math.Min(frames * Channels, _ring.Length - _count);
            for (int i = 0; i < n; i++)
            {
                _ring[_write] = 0;
                if (++_write == _ring.Length) _write = 0;
            }
            _count += n;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            _read = _write = _count = 0;
            _primed = false;
        }
        Equalizer.Reset();
        Concert.Reset();
    }

    /// <summary>Appelé par la sortie audio. Remplit toujours <paramref name="count"/> échantillons.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / Channels;
        int target = BufferMs * SampleRate / 1000;
        int got;
        lock (_lock)
        {
            int available = _count / Channels;
            if (!_primed && available >= target) _primed = true;
            if (_primed && available == 0) _primed = false;

            got = 0;
            if (_primed)
            {
                // correction de dérive d'horloge : on saute / répète une trame de temps en temps
                bool tooMuch = available > target + SampleRate / 5;   // > +200 ms
                bool tooLittle = available < target / 3;
                for (int f = 0; f < frames && _count >= Channels; f++)
                {
                    int dst = offset + 2 * f;
                    buffer[dst] = _ring[_read];
                    buffer[dst + 1] = _ring[(_read + 1) % _ring.Length];
                    if (++_driftCounter >= 300 && (tooMuch || tooLittle))
                    {
                        _driftCounter = 0;
                        if (tooLittle) { got++; continue; }    // répète la trame (on n'avance pas)
                        Advance(); if (_count < Channels) { got++; break; } // saute une trame
                    }
                    Advance();
                    got++;
                }
            }
        }

        for (int i = offset + got * Channels; i < offset + count; i++) buffer[i] = 0;

        // volume (avec lissage pour éviter les clics)
        float targetGain = Muted ? 0 : RemoteVolume * LocalVolume;
        float peak = 0;
        for (int f = 0; f < frames; f++)
        {
            _currentGain += (targetGain - _currentGain) * 0.002f;
            buffer[offset + 2 * f] *= _currentGain;
            buffer[offset + 2 * f + 1] *= _currentGain;
        }

        Equalizer.Process(buffer, offset, frames);
        Concert.Process(buffer, offset, frames);

        // limiteur doux : évite la saturation quand on pousse les basses
        for (int i = offset; i < offset + frames * Channels; i++)
        {
            float x = buffer[i], a = Math.Abs(x);
            if (a > 0.85f)
            {
                float y = 0.85f + 0.15f * MathF.Tanh((a - 0.85f) / 0.15f);
                x = x < 0 ? -y : y;
                buffer[i] = x;
            }
            if (a > peak) peak = a;
        }
        Peak = Math.Max(peak, Peak * 0.9f);
        return count;
    }

    private void Advance()
    {
        _read = (_read + Channels) % _ring.Length;
        _count -= Channels;
    }
}
