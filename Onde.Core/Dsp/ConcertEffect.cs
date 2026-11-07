using System;

namespace Onde.Core.Dsp;

/// <summary>Réglages du mode concert.</summary>
public sealed record ConcertSettings
{
    /// <summary>Taille de la salle 0..1 (durée de la réverbération).</summary>
    public double RoomSize { get; init; } = 0.6;
    /// <summary>Amortissement des aigus 0..1 (salle "mate" ou "brillante").</summary>
    public double Damping { get; init; } = 0.4;
    /// <summary>Quantité de réverbération 0..1.</summary>
    public double ReverbMix { get; init; } = 0.3;
    /// <summary>Délai de l'écho en millisecondes (0 = pas d'écho).</summary>
    public double EchoDelayMs { get; init; } = 120;
    /// <summary>Répétitions de l'écho 0..0.9.</summary>
    public double EchoFeedback { get; init; } = 0.25;
    /// <summary>Volume de l'écho 0..1.</summary>
    public double EchoMix { get; init; } = 0.2;
    /// <summary>Largeur stéréo 0..1.</summary>
    public double Width { get; init; } = 1.0;
}

public static class ConcertPresets
{
    public static readonly (string Name, ConcertSettings Settings)[] All =
    {
        ("Club",          new ConcertSettings { RoomSize = 0.35, Damping = 0.55, ReverbMix = 0.20, EchoDelayMs = 60,  EchoFeedback = 0.15, EchoMix = 0.10, Width = 0.8 }),
        ("Salle de concert", new ConcertSettings { RoomSize = 0.70, Damping = 0.40, ReverbMix = 0.32, EchoDelayMs = 110, EchoFeedback = 0.25, EchoMix = 0.15, Width = 1.0 }),
        ("Stade",         new ConcertSettings { RoomSize = 0.88, Damping = 0.30, ReverbMix = 0.38, EchoDelayMs = 320, EchoFeedback = 0.40, EchoMix = 0.28, Width = 1.0 }),
        ("Cathédrale",    new ConcertSettings { RoomSize = 0.96, Damping = 0.20, ReverbMix = 0.45, EchoDelayMs = 0,   EchoFeedback = 0.0,  EchoMix = 0.0,  Width = 1.0 }),
        ("Live acoustique", new ConcertSettings { RoomSize = 0.50, Damping = 0.60, ReverbMix = 0.22, EchoDelayMs = 80,  EchoFeedback = 0.10, EchoMix = 0.08, Width = 0.9 }),
        ("Canyon",        new ConcertSettings { RoomSize = 0.60, Damping = 0.35, ReverbMix = 0.20, EchoDelayMs = 450, EchoFeedback = 0.55, EchoMix = 0.40, Width = 1.0 }),
    };
}

/// <summary>
/// Mode concert : réverbération type Freeverb (Schroeder/Moorer) + écho stéréo à rebonds.
/// </summary>
public sealed class ConcertEffect
{
    private const float FixedGain = 0.015f, ScaleDamp = 0.4f, ScaleRoom = 0.28f, OffsetRoom = 0.7f;
    private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassTunings = { 556, 441, 341, 225 };
    private const int StereoSpread = 23;

    private readonly Comb[] _combL, _combR;
    private readonly Allpass[] _apL, _apR;
    private readonly float[] _echoL, _echoR;
    private readonly int _fs;
    private int _echoPos;
    private volatile ConcertSettings _s = ConcertPresets.All[1].Settings;
    private readonly object _lock = new();

    public ConcertEffect(int sampleRate)
    {
        _fs = sampleRate;
        double scale = sampleRate / 44100.0;
        _combL = new Comb[8]; _combR = new Comb[8];
        for (int i = 0; i < 8; i++)
        {
            _combL[i] = new Comb((int)(CombTunings[i] * scale));
            _combR[i] = new Comb((int)((CombTunings[i] + StereoSpread) * scale));
        }
        _apL = new Allpass[4]; _apR = new Allpass[4];
        for (int i = 0; i < 4; i++)
        {
            _apL[i] = new Allpass((int)(AllpassTunings[i] * scale));
            _apR[i] = new Allpass((int)((AllpassTunings[i] + StereoSpread) * scale));
        }
        _echoL = new float[sampleRate]; // jusqu'à 1 s d'écho
        _echoR = new float[sampleRate];
        Apply(_s);
    }

    public bool Enabled { get; set; }

    public ConcertSettings Settings
    {
        get => _s;
        set { _s = value; Apply(value); }
    }

    private void Apply(ConcertSettings s)
    {
        lock (_lock)
        {
            float room = (float)Math.Clamp(s.RoomSize, 0, 1) * ScaleRoom + OffsetRoom;
            float damp = (float)Math.Clamp(s.Damping, 0, 1) * ScaleDamp;
            foreach (var c in _combL) { c.Feedback = room; c.Damp = damp; }
            foreach (var c in _combR) { c.Feedback = room; c.Damp = damp; }
        }
    }

    public void Process(float[] buf, int offset, int frames)
    {
        if (!Enabled) return;
        var s = _s;
        float wet = (float)Math.Clamp(s.ReverbMix, 0, 1) * 3f;
        float dry = 1f - (float)Math.Clamp(s.ReverbMix, 0, 1) * 0.5f;
        float width = (float)Math.Clamp(s.Width, 0, 1);
        float wet1 = wet * (width / 2 + 0.5f), wet2 = wet * ((1 - width) / 2);
        int echoLen = (int)(Math.Clamp(s.EchoDelayMs, 0, 999) * _fs / 1000.0);
        float fb = (float)Math.Clamp(s.EchoFeedback, 0, 0.9);
        float echoMix = (float)Math.Clamp(s.EchoMix, 0, 1);

        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                int p = offset + 2 * i;
                float inL = buf[p], inR = buf[p + 1];
                float input = (inL + inR) * FixedGain;
                float outL = 0, outR = 0;
                for (int c = 0; c < 8; c++) { outL += _combL[c].Process(input); outR += _combR[c].Process(input); }
                for (int a = 0; a < 4; a++) { outL = _apL[a].Process(outL); outR = _apR[a].Process(outR); }

                float l = inL * dry + outL * wet1 + outR * wet2;
                float r = inR * dry + outR * wet1 + outL * wet2;

                if (echoLen > 0 && echoMix > 0)
                {
                    // écho "ping-pong" : les rebonds alternent gauche/droite
                    int rp = _echoPos - echoLen; if (rp < 0) rp += _echoL.Length;
                    float eL = _echoL[rp], eR = _echoR[rp];
                    _echoL[_echoPos] = inL + eR * fb;
                    _echoR[_echoPos] = inR + eL * fb;
                    l += eL * echoMix; r += eR * echoMix;
                }
                else
                {
                    _echoL[_echoPos] = 0; _echoR[_echoPos] = 0;
                }
                if (++_echoPos >= _echoL.Length) _echoPos = 0;

                buf[p] = l; buf[p + 1] = r;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            foreach (var c in _combL) c.Clear();
            foreach (var c in _combR) c.Clear();
            foreach (var a in _apL) a.Clear();
            foreach (var a in _apR) a.Clear();
            Array.Clear(_echoL); Array.Clear(_echoR);
        }
    }

    private sealed class Comb
    {
        private readonly float[] _b; private int _i; private float _store;
        public float Feedback, Damp;
        public Comb(int size) => _b = new float[Math.Max(1, size)];
        public float Process(float x)
        {
            float o = _b[_i];
            _store = o * (1 - Damp) + _store * Damp;
            if (_store is > -1e-15f and < 1e-15f) _store = 0; // évite les nombres dénormalisés
            _b[_i] = x + _store * Feedback;
            if (++_i >= _b.Length) _i = 0;
            return o;
        }
        public void Clear() { Array.Clear(_b); _store = 0; }
    }

    private sealed class Allpass
    {
        private readonly float[] _b; private int _i;
        public Allpass(int size) => _b = new float[Math.Max(1, size)];
        public float Process(float x)
        {
            float bo = _b[_i];
            float o = -x + bo;
            _b[_i] = x + bo * 0.5f;
            if (++_i >= _b.Length) _i = 0;
            return o;
        }
        public void Clear() => Array.Clear(_b);
    }
}
