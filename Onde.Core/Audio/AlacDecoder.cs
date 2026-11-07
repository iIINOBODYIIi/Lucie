using System;
using System.Globalization;
using System.Numerics;

namespace Onde.Core.Audio;

/// <summary>Paramètres ALAC envoyés par l'iPhone dans le SDP (a=fmtp:96 ...).</summary>
public sealed class AlacConfig
{
    public int FrameLength { get; init; } = 352;
    public int BitDepth { get; init; } = 16;
    public int Pb { get; init; } = 40;
    public int Mb { get; init; } = 10;
    public int Kb { get; init; } = 14;
    public int NumChannels { get; init; } = 2;
    public int MaxRun { get; init; } = 255;
    public int SampleRate { get; init; } = 44100;

    /// <summary>Parse "96 352 0 16 40 10 14 2 255 0 0 44100".</summary>
    public static AlacConfig FromFmtp(string fmtp)
    {
        var p = fmtp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int I(int i, int def) => i < p.Length && int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        return new AlacConfig
        {
            FrameLength = I(1, 352),
            BitDepth = I(3, 16),
            Pb = I(4, 40),
            Mb = I(5, 10),
            Kb = I(6, 14),
            NumChannels = I(7, 2),
            MaxRun = I(8, 255),
            SampleRate = I(11, 44100),
        };
    }
}

/// <summary>
/// Décodeur Apple Lossless (16 bits, mono/stéréo), portage fidèle de l'implémentation
/// de référence d'Apple (ag_dec / dp_dec / matrix_dec). Vérifié bit à bit contre FFmpeg.
/// </summary>
public sealed class AlacDecoder
{
    private const int IdSce = 0, IdCpe = 1, IdEnd = 7;

    private readonly AlacConfig _cfg;
    private readonly int[] _predictor, _mixU, _mixV, _shiftU, _shiftV;
    private readonly short[] _coefsU = new short[32], _coefsV = new short[32];
    private byte[] _buf = new byte[4096];
    private int _len, _pos;

    public AlacDecoder(AlacConfig cfg)
    {
        _cfg = cfg;
        int n = Math.Max(cfg.FrameLength, 1);
        _predictor = new int[n]; _mixU = new int[n]; _mixV = new int[n];
        _shiftU = new int[n]; _shiftV = new int[n];
    }

    public AlacConfig Config => _cfg;

    /// <summary>Décode un paquet. Sortie : PCM 16 bits stéréo entrelacé. Retourne le nombre de trames.</summary>
    public int Decode(ReadOnlySpan<byte> packet, short[] output)
    {
        if (_buf.Length < packet.Length + 16) _buf = new byte[packet.Length + 64];
        packet.CopyTo(_buf);
        Array.Clear(_buf, packet.Length, 16);
        _len = packet.Length;
        _pos = 0;

        int framesOut = 0;
        while (true)
        {
            if (_pos + 3 > _len * 8) break;
            int tag = (int)Read(3);
            if (tag == IdEnd) break;
            if (tag != IdSce && tag != IdCpe) throw new InvalidOperationException($"ALAC: élément non géré {tag}");
            framesOut = tag == IdCpe ? DecodeElement(true, output) : DecodeElement(false, output);
        }
        return framesOut;
    }

    private int DecodeElement(bool stereo, short[] output)
    {
        int bitDepth = _cfg.BitDepth;
        Read(4);  // element instance tag
        Read(12); // unused
        int hb = (int)Read(4);
        int partial = hb >> 3;
        int bytesShifted = (hb >> 1) & 3;
        bool escape = (hb & 1) != 0;
        int nch = stereo ? 2 : 1;
        int chanBits = bitDepth - bytesShifted * 8 + (stereo ? 1 : 0);
        int n = _cfg.FrameLength;
        if (partial != 0) n = (int)((Read(16) << 16) | Read(16));
        if (n > _predictor.Length || n <= 0) throw new InvalidOperationException("ALAC: taille de trame invalide");

        int mixBits = 0, mixRes = 0;
        if (!escape)
        {
            if (stereo) { mixBits = (int)Read(8); mixRes = (sbyte)Read(8); }

            int modeU = 0, denU = 0, pbfU = 0, numU = 0, modeV = 0, denV = 0, pbfV = 0, numV = 0;
            int h = (int)Read(8); modeU = h >> 4; denU = h & 15;
            h = (int)Read(8); pbfU = h >> 5; numU = h & 31;
            for (int i = 0; i < numU; i++) _coefsU[i] = (short)Read(16);
            if (stereo)
            {
                h = (int)Read(8); modeV = h >> 4; denV = h & 15;
                h = (int)Read(8); pbfV = h >> 5; numV = h & 31;
                for (int i = 0; i < numV; i++) _coefsV[i] = (short)Read(16);
            }

            int shiftPos = 0;
            if (bytesShifted != 0) { shiftPos = _pos; _pos += bytesShifted * 8 * nch * n; }

            DynDecomp(_predictor, n, chanBits, _cfg.Mb, (_cfg.Pb * pbfU) / 4, _cfg.Kb);
            Predict(modeU, _mixU, n, _coefsU, numU, chanBits, denU);

            if (stereo)
            {
                DynDecomp(_predictor, n, chanBits, _cfg.Mb, (_cfg.Pb * pbfV) / 4, _cfg.Kb);
                Predict(modeV, _mixV, n, _coefsV, numV, chanBits, denV);
            }

            if (bytesShifted != 0)
            {
                int save = _pos; _pos = shiftPos;
                int sb = bytesShifted * 8;
                for (int i = 0; i < n; i++)
                {
                    _shiftU[i] = (int)Read(sb);
                    if (stereo) _shiftV[i] = (int)Read(sb);
                }
                _pos = save;
            }
        }
        else
        {
            int shift = 32 - bitDepth;
            for (int i = 0; i < n; i++)
            {
                _mixU[i] = ((int)Read(bitDepth) << shift) >> shift;
                if (stereo) _mixV[i] = ((int)Read(bitDepth) << shift) >> shift;
            }
            bytesShifted = 0;
        }

        int s = bytesShifted * 8;
        for (int j = 0; j < n; j++)
        {
            int l, r;
            if (stereo)
            {
                if (mixRes != 0) { l = _mixU[j] + _mixV[j] - ((mixRes * _mixV[j]) >> mixBits); r = l - _mixV[j]; }
                else { l = _mixU[j]; r = _mixV[j]; }
            }
            else { l = r = _mixU[j]; }
            if (s != 0)
            {
                l = (l << s) | _shiftU[j];
                r = stereo ? (r << s) | _shiftV[j] : l;
            }
            output[2 * j] = (short)l;
            output[2 * j + 1] = (short)r;
        }
        return n;
    }

    private void Predict(int mode, int[] mix, int n, short[] coefs, int num, int chanBits, int den)
    {
        if (mode == 0)
        {
            UnpcBlock(_predictor, mix, n, coefs, num, chanBits, den);
        }
        else
        {
            UnpcBlock(_predictor, _predictor, n, null, 31, chanBits, 0);
            UnpcBlock(_predictor, mix, n, coefs, num, chanBits, den);
        }
    }

    // ---------- lecture de bits (MSB d'abord) ----------
    private uint Read(int n)
    {
        if (n == 0) return 0;
        uint v = ReadBitsAt(_pos, n);
        _pos += n;
        return v;
    }

    private uint ReadBitsAt(int bitPos, int n)
    {
        int i = bitPos >> 3;
        ulong v = 0;
        for (int b = 0; b < 8; b++) v = (v << 8) | _buf[i + b];
        v <<= bitPos & 7;
        return (uint)(v >> (64 - n));
    }

    private uint Read32At(int bitPos)
    {
        int i = bitPos >> 3;
        uint v = ((uint)_buf[i] << 24) | ((uint)_buf[i + 1] << 16) | ((uint)_buf[i + 2] << 8) | _buf[i + 3];
        return v << (bitPos & 7);
    }

    private static int Lead(uint x) => BitOperations.LeadingZeroCount(x);

    // ---------- Golomb adaptatif (ag_dec) ----------
    private const int QbShift = 9, Qb = 1 << QbShift, MaxPrefix = 9;

    private uint DynGet(ref int bp, uint m, int k)
    {
        uint sl = Read32At(bp);
        int pre = Lead(~sl);
        if (pre >= MaxPrefix)
        {
            sl <<= MaxPrefix;
            bp += MaxPrefix + 16;
            return sl >> 16;
        }
        bp += pre + 1;
        sl <<= pre + 1;
        uint v = k == 0 ? 0u : sl >> (32 - k);
        bp += k;
        if (v < 2) { bp -= 1; return (uint)pre * m; }
        return (uint)pre * m + v - 1;
    }

    private uint DynGet32(ref int bp, uint m, int k, int maxBits)
    {
        uint sl = Read32At(bp);
        int pre = Lead(~sl);
        if (pre >= MaxPrefix)
        {
            uint r = ReadBitsAt(bp + MaxPrefix, maxBits);
            bp += MaxPrefix + maxBits;
            return r;
        }
        bp += pre + 1;
        uint res = (uint)pre;
        if (k != 1)
        {
            sl <<= pre + 1;
            uint v = sl >> (32 - k);
            bp += k - 1;
            res = (uint)pre * m;
            if (v >= 2) { res += v - 1; bp += 1; }
        }
        return res;
    }

    private void DynDecomp(int[] pc, int n, int maxSize, int mb0, int pb, int kb)
    {
        uint mb = (uint)mb0, upb = (uint)pb;
        uint wb = (uint)((1 << kb) - 1);
        uint zmode = 0;
        int c = 0, bp = _pos, maxPos = _len * 8;
        while (c < n)
        {
            if (bp >= maxPos) throw new InvalidOperationException("ALAC: dépassement du flux");
            uint m = mb >> QbShift;
            int k = 31 - Lead(m + 3);
            if (k > kb) k = kb;
            m = (uint)((1 << k) - 1);
            uint nn = DynGet32(ref bp, m, k, maxSize);
            uint nd = nn + zmode;
            int mult = (-(int)(nd & 1)) | 1;
            pc[c++] = (int)((nd + 1) >> 1) * mult;

            mb = upb * (nn + zmode) + mb - ((upb * mb) >> QbShift);
            if (nn > 0xffff) mb = 0xffff;
            zmode = 0;

            if (((mb << 2) < Qb) && c < n)
            {
                zmode = 1;
                int k2 = Lead(mb) - 24 + (int)((mb + 16) >> 6);
                uint mz = (uint)((1 << k2) - 1) & wb;
                uint run = DynGet(ref bp, mz, k2);
                if (c + run > n) throw new InvalidOperationException("ALAC: série de zéros invalide");
                for (uint j = 0; j < run; j++) pc[c++] = 0;
                if (run >= 65535) zmode = 0;
                mb = 0;
            }
        }
        _pos = bp;
    }

    // ---------- prédicteur adaptatif (dp_dec) ----------
    private static void UnpcBlock(int[] pc, int[] o, int num, short[]? coefs, int na, int chanBits, int den)
    {
        int sh = 32 - chanBits;
        o[0] = pc[0];
        if (na == 0)
        {
            if (!ReferenceEquals(pc, o)) Array.Copy(pc, 1, o, 1, num - 1);
            return;
        }
        if (na == 31)
        {
            for (int j = 1; j < num; j++) { int d = pc[j] + o[j - 1]; o[j] = (d << sh) >> sh; }
            return;
        }
        for (int j = 1; j <= na && j < num; j++) { int d = pc[j] + o[j - 1]; o[j] = (d << sh) >> sh; }

        int lim = na + 1;
        int denHalf = den > 0 ? 1 << (den - 1) : 0;
        for (int j = lim; j < num; j++)
        {
            int top = o[j - lim];
            int sum = 0;
            for (int k = 0; k < na; k++) sum += coefs![k] * (o[j - 1 - k] - top);
            int del = pc[j], del0 = del;
            int sg = Math.Sign(del);
            del += top + ((sum + denHalf) >> den);
            o[j] = (del << sh) >> sh;
            if (sg > 0)
            {
                for (int k = na - 1; k >= 0; k--)
                {
                    int dd = top - o[j - 1 - k];
                    int sgn = Math.Sign(dd);
                    coefs![k] = (short)(coefs[k] - sgn);
                    del0 -= (na - k) * ((sgn * dd) >> den);
                    if (del0 <= 0) break;
                }
            }
            else if (sg < 0)
            {
                for (int k = na - 1; k >= 0; k--)
                {
                    int dd = top - o[j - 1 - k];
                    int sgn = Math.Sign(dd);
                    coefs![k] = (short)(coefs[k] + sgn);
                    del0 -= (na - k) * ((-sgn * dd) >> den);
                    if (del0 >= 0) break;
                }
            }
        }
    }
}
