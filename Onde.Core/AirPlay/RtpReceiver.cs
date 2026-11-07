using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Onde.Core.Audio;

namespace Onde.Core.AirPlay;

/// <summary>
/// Réception du flux audio RTP AirPlay 1 : port audio, port de contrôle (renvois de paquets
/// perdus) et port de synchronisation. Déchiffre (AES-128-CBC), décode (ALAC) et pousse le PCM
/// dans le <see cref="AudioPipeline"/> en remettant les paquets dans l'ordre.
/// </summary>
internal sealed class RtpReceiver : IDisposable
{
    private const int MaxPending = 24;          // fenêtre de réordonnancement (~190 ms)

    private readonly Socket _audio, _control, _timing;
    private readonly AudioPipeline _pipeline;
    private readonly AlacDecoder _decoder;
    private readonly Aes? _aes;
    private readonly byte[]? _iv;
    private readonly short[] _pcm;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<ushort, short[]?> _pending = new();
    private readonly HashSet<ushort> _resendAsked = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IPEndPoint? _clientControl;
    private bool _started;
    private ushort _expected;
    private ushort _resendSeq;
    private bool _flushing;

    public RtpReceiver(AudioPipeline pipeline, AlacConfig cfg, byte[]? aesKey, byte[]? aesIv)
    {
        _pipeline = pipeline;
        _decoder = new AlacDecoder(cfg);
        _pcm = new short[Math.Max(cfg.FrameLength, 4096) * 2];
        if (aesKey != null && aesIv != null)
        {
            _aes = Aes.Create();
            _aes.Key = aesKey;
            _iv = aesIv;
        }
        _audio = CreateUdp();
        _control = CreateUdp();
        _timing = CreateUdp();
    }

    public int AudioPort => ((IPEndPoint)_audio.LocalEndPoint!).Port;
    public int ControlPort => ((IPEndPoint)_control.LocalEndPoint!).Port;
    public int TimingPort => ((IPEndPoint)_timing.LocalEndPoint!).Port;

    /// <summary>Dernier horodatage RTP reçu (pour calculer la progression).</summary>
    public uint LastTimestamp { get; private set; }

    private static Socket CreateUdp()
    {
        var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        s.ReceiveBufferSize = 1 << 20;
        s.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return s;
    }

    public void Start(IPAddress client, int clientControlPort)
    {
        if (clientControlPort > 0) _clientControl = new IPEndPoint(client, clientControlPort);
        _ = Task.Run(() => Loop(_audio, HandleAudio));
        _ = Task.Run(() => Loop(_control, HandleControl));
        _ = Task.Run(() => Loop(_timing, HandleTiming));
    }

    /// <summary>FLUSH : l'iPhone a mis en pause ou changé de morceau.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            _pending.Clear();
            _resendAsked.Clear();
            _started = false;
            _flushing = true;
        }
        _pipeline.Flush();
    }

    private async Task Loop(Socket s, Action<byte[], int, EndPoint> handler)
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                handler(buf, r.ReceivedBytes, r.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (_cts.IsCancellationRequested) break; }
            catch (Exception ex) { Debug.WriteLine($"RTP: {ex.Message}"); }
        }
    }

    private void HandleAudio(byte[] buf, int len, EndPoint from)
    {
        if (len < 12) return;
        int pt = buf[1] & 0x7F;
        if (pt != 0x60) return;
        HandleRtpAudio(buf, 0, len, isResend: false);
    }

    private void HandleControl(byte[] buf, int len, EndPoint from)
    {
        if (len < 4) return;
        int pt = buf[1] & 0x7F;
        if (pt == 0x56 && len > 4 + 12) // renvoi d'un paquet perdu : en-tête 4 octets + paquet RTP complet
            HandleRtpAudio(buf, 4, len - 4, isResend: true);
        // 0x54 = paquet de synchro : ignoré (lecture à latence fixe)
    }

    private void HandleTiming(byte[] buf, int len, EndPoint from)
    {
        // Requête de synchro horloge (0x52) → réponse (0x53) au format NTP.
        if (len < 32 || (buf[1] & 0x7F) != 0x52) return;
        var reply = new byte[32];
        reply[0] = 0x80; reply[1] = 0xD3; reply[2] = buf[2]; reply[3] = buf[3];
        Buffer.BlockCopy(buf, 24, reply, 8, 8);  // origin = transmit du client
        WriteNtp(reply, 16);                     // receive
        WriteNtp(reply, 24);                     // transmit
        try { _timing.SendTo(reply, from); } catch { /* ignoré */ }
    }

    private void WriteNtp(byte[] b, int at)
    {
        double secs = _clock.Elapsed.TotalSeconds;
        uint s = (uint)secs;
        uint f = (uint)((secs - s) * 4294967296.0);
        b[at] = (byte)(s >> 24); b[at + 1] = (byte)(s >> 16); b[at + 2] = (byte)(s >> 8); b[at + 3] = (byte)s;
        b[at + 4] = (byte)(f >> 24); b[at + 5] = (byte)(f >> 16); b[at + 6] = (byte)(f >> 8); b[at + 7] = (byte)f;
    }

    private void HandleRtpAudio(byte[] buf, int off, int len, bool isResend)
    {
        ushort seq = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
        uint ts = (uint)((buf[off + 4] << 24) | (buf[off + 5] << 16) | (buf[off + 6] << 8) | buf[off + 7]);
        var payload = new byte[len - 12];
        Buffer.BlockCopy(buf, off + 12, payload, 0, payload.Length);
        Decrypt(payload);

        short[]? pcm = null;
        int frames = 0;
        try
        {
            frames = _decoder.Decode(payload, _pcm);
            pcm = new short[frames * 2];
            Array.Copy(_pcm, pcm, pcm.Length);
        }
        catch (Exception ex) { Debug.WriteLine($"ALAC: {ex.Message}"); }

        lock (_lock)
        {
            LastTimestamp = ts;
            if (!_started)
            {
                if (isResend) return;
                _started = true;
                _flushing = false;
                _expected = seq;
            }

            short diff = (short)(seq - _expected);
            if (diff < 0) return; // déjà joué (ou remplacé par du silence)
            _pending[seq] = pcm;

            if (diff > 0 && !isResend) RequestResend(_expected, diff);

            // sortir tout ce qui est contigu
            while (_pending.TryGetValue(_expected, out var p))
            {
                _pending.Remove(_expected);
                _resendAsked.Remove(_expected);
                if (p != null) _pipeline.Write(p, p.Length / 2);
                else _pipeline.WriteSilence(_decoder.Config.FrameLength);
                _expected++;
            }

            // trop d'attente : on abandonne les paquets manquants et on comble par du silence
            while (_pending.Count > MaxPending)
            {
                _pipeline.WriteSilence(_decoder.Config.FrameLength);
                _resendAsked.Remove(_expected);
                _expected++;
                while (_pending.TryGetValue(_expected, out var p))
                {
                    _pending.Remove(_expected);
                    if (p != null) _pipeline.Write(p, p.Length / 2); else _pipeline.WriteSilence(_decoder.Config.FrameLength);
                    _expected++;
                }
            }
        }
    }

    private void RequestResend(ushort first, int count)
    {
        if (_clientControl == null || count > 64) return;
        ushort start = first; int n = 0;
        for (int i = 0; i < count; i++)
        {
            ushort s = (ushort)(first + i);
            if (_pending.ContainsKey(s) || !_resendAsked.Add(s)) { if (n > 0) Send(start, n); n = 0; start = (ushort)(s + 1); continue; }
            n++;
        }
        if (n > 0) Send(start, n);

        void Send(ushort s, int c)
        {
            var pkt = new byte[8];
            pkt[0] = 0x80; pkt[1] = 0xD5;
            _resendSeq++;
            pkt[2] = (byte)(_resendSeq >> 8); pkt[3] = (byte)_resendSeq;
            pkt[4] = (byte)(s >> 8); pkt[5] = (byte)s;
            pkt[6] = (byte)(c >> 8); pkt[7] = (byte)c;
            try { _control.SendTo(pkt, _clientControl!); } catch { /* ignoré */ }
        }
    }

    private void Decrypt(byte[] payload)
    {
        if (_aes == null || _iv == null) return;
        int blocks = payload.Length / 16 * 16;
        if (blocks == 0) return;
        // Seuls les blocs complets de 16 octets sont chiffrés ; le reste est en clair.
        var dec = _aes.DecryptCbc(payload.AsSpan(0, blocks), _iv, PaddingMode.None);
        Buffer.BlockCopy(dec, 0, payload, 0, blocks);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _audio.Dispose(); _control.Dispose(); _timing.Dispose();
        _aes?.Dispose();
    }
}
