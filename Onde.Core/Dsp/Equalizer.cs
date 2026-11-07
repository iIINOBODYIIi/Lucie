using System;

namespace Onde.Core.Dsp;

/// <summary>Filtre biquad (formules RBJ) en forme directe transposée II.</summary>
internal sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _z1L, _z2L, _z1R, _z2R;

    public void SetPeaking(double fs, double f, double q, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * Math.Min(f, fs * 0.45) / fs;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha / a;
        Set((1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0);
    }

    public void SetLowShelf(double fs, double f, double gainDb) => SetShelf(fs, f, gainDb, low: true);
    public void SetHighShelf(double fs, double f, double gainDb) => SetShelf(fs, f, gainDb, low: false);

    private void SetShelf(double fs, double f, double gainDb, bool low)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * Math.Min(f, fs * 0.45) / fs;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / 2 * Math.Sqrt(2); // S = 1
        double sa = 2 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        if (low)
        {
            b0 = a * ((a + 1) - (a - 1) * cos + sa);
            b1 = 2 * a * ((a - 1) - (a + 1) * cos);
            b2 = a * ((a + 1) - (a - 1) * cos - sa);
            a0 = (a + 1) + (a - 1) * cos + sa;
            a1 = -2 * ((a - 1) + (a + 1) * cos);
            a2 = (a + 1) + (a - 1) * cos - sa;
        }
        else
        {
            b0 = a * ((a + 1) + (a - 1) * cos + sa);
            b1 = -2 * a * ((a - 1) + (a + 1) * cos);
            b2 = a * ((a + 1) + (a - 1) * cos - sa);
            a0 = (a + 1) - (a - 1) * cos + sa;
            a1 = 2 * ((a - 1) - (a + 1) * cos);
            a2 = (a + 1) - (a - 1) * cos - sa;
        }
        Set(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    private void Set(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
    }

    public void Process(ref float l, ref float r)
    {
        double x = l, y = _b0 * x + _z1L;
        _z1L = _b1 * x - _a1 * y + _z2L;
        _z2L = _b2 * x - _a2 * y;
        l = (float)y;

        x = r; y = _b0 * x + _z1R;
        _z1R = _b1 * x - _a1 * y + _z2R;
        _z2R = _b2 * x - _a2 * y;
        r = (float)y;
    }

    public void Reset() => _z1L = _z2L = _z1R = _z2R = 0;
}

/// <summary>Égaliseur 10 bandes + préampli. Thread-safe pour les réglages.</summary>
public sealed class Equalizer
{
    public static readonly double[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public static readonly string[] Labels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
    public const double MaxGainDb = 12;

    private readonly double _fs;
    private readonly object _lock = new();
    private readonly double[] _gains = new double[10];
    private Biquad[] _filters;
    private float _preamp = 1f;

    public Equalizer(double sampleRate)
    {
        _fs = sampleRate;
        _filters = Build(_gains);
    }

    public bool Enabled { get; set; }

    public double PreampDb { get; private set; }

    public double GetGain(int band) => _gains[band];

    public double[] GetGains() { lock (_lock) return (double[])_gains.Clone(); }

    public void SetGain(int band, double db)
    {
        lock (_lock)
        {
            _gains[band] = Math.Clamp(db, -MaxGainDb, MaxGainDb);
            _filters[band].SetPeaking(_fs, Frequencies[band], Q(band), _gains[band]);
        }
    }

    public void SetGains(double[] gains)
    {
        lock (_lock)
        {
            for (int i = 0; i < 10 && i < gains.Length; i++) _gains[i] = Math.Clamp(gains[i], -MaxGainDb, MaxGainDb);
            for (int i = 0; i < 10; i++) _filters[i].SetPeaking(_fs, Frequencies[i], Q(i), _gains[i]);
        }
    }

    public void SetPreamp(double db)
    {
        PreampDb = Math.Clamp(db, -12, 12);
        _preamp = (float)Math.Pow(10, PreampDb / 20);
    }

    private static double Q(int band) => band == 0 || band == 9 ? 0.9 : 1.41;

    private Biquad[] Build(double[] gains)
    {
        var f = new Biquad[10];
        for (int i = 0; i < 10; i++) { f[i] = new Biquad(); f[i].SetPeaking(_fs, Frequencies[i], Q(i), gains[i]); }
        return f;
    }

    public void Process(float[] buf, int offset, int frames)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            float pre = _preamp;
            for (int i = 0; i < frames; i++)
            {
                int p = offset + 2 * i;
                float l = buf[p] * pre, r = buf[p + 1] * pre;
                for (int b = 0; b < _filters.Length; b++)
                    if (_gains[b] != 0) _filters[b].Process(ref l, ref r);
                buf[p] = l; buf[p + 1] = r;
            }
        }
    }

    public void Reset() { lock (_lock) foreach (var f in _filters) f.Reset(); }
}

/// <summary>Préréglages d'égaliseur (gains en dB pour 31 Hz → 16 kHz).</summary>
public static class EqPresets
{
    public static readonly (string Name, double[] Gains)[] All =
    {
        ("Plat",        new double[] {  0,  0,  0,  0,  0,  0,  0,  0,  0,  0 }),
        ("Basses+",     new double[] {  7,  6,  5,  3,  1,  0,  0,  0,  0,  0 }),
        ("Aigus+",      new double[] {  0,  0,  0,  0,  0,  1,  3,  5,  6,  7 }),
        ("Rock",        new double[] {  5,  4,  2, -1, -2, -1,  2,  4,  5,  5 }),
        ("Pop",         new double[] { -1,  1,  3,  4,  3,  0, -1, -1,  1,  2 }),
        ("Hip-hop",     new double[] {  6,  5,  3,  1, -1, -1,  1,  0,  2,  3 }),
        ("Électro",     new double[] {  5,  4,  1,  0, -2,  1,  0,  2,  4,  5 }),
        ("Jazz",        new double[] {  3,  2,  1,  2, -1, -1,  0,  1,  2,  3 }),
        ("Classique",   new double[] {  4,  3,  2,  1, -1, -1,  0,  2,  3,  4 }),
        ("Voix",        new double[] { -2, -1,  0,  2,  4,  4,  3,  1,  0, -1 }),
        ("Acoustique",  new double[] {  3,  3,  2,  1,  1,  1,  2,  2,  2,  1 }),
        ("Nuit",        new double[] { -3, -2, -1,  0,  1,  2,  1,  0, -2, -4 }),
    };
}
