using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Onde.Core.Audio;

namespace Onde.Core.AirPlay;

/// <summary>
/// Récepteur AirPlay (audio, protocole RAOP / AirTunes v2).
/// Le PC apparaît comme une enceinte AirPlay sur l'iPhone.
/// </summary>
public sealed class AirPlayReceiver : IDisposable
{
    private readonly AudioPipeline _pipeline;
    private readonly byte[] _hwAddr;
    private readonly MdnsService _mdns = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Session? _current;
    private readonly object _lock = new();

    public AirPlayReceiver(AudioPipeline pipeline, string name, byte[] hardwareAddress)
    {
        if (hardwareAddress.Length != 6) throw new ArgumentException("6 octets attendus", nameof(hardwareAddress));
        _pipeline = pipeline;
        _hwAddr = hardwareAddress;
        Name = name;
    }

    public string Name { get; private set; }
    public int Port { get; private set; }
    public ReceiverState State { get; private set; } = ReceiverState.Waiting;
    public TrackInfo Track { get; private set; } = TrackInfo.Empty;
    public PlaybackProgress? Progress { get; private set; }
    public string ClientName { get; private set; } = "";
    public bool CanRemoteControl => _current?.Remote?.IsAvailable == true;

    public event EventHandler<ReceiverState>? StateChanged;
    public event EventHandler<TrackInfo>? TrackChanged;
    public event EventHandler<byte[]?>? ArtworkChanged;
    public event EventHandler<PlaybackProgress>? ProgressChanged;
    /// <summary>Volume réglé depuis l'iPhone (0..1).</summary>
    public event EventHandler<float>? RemoteVolumeChanged;
    public event EventHandler<string>? Error;

    public void Start(int preferredPort = 5000)
    {
        _cts = new CancellationTokenSource();
        _listener = CreateListener(preferredPort) ?? CreateListener(0)
            ?? throw new InvalidOperationException("Impossible d'ouvrir un port réseau.");
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop(_cts.Token);

        _mdns.Start();
        _mdns.Advertise(Name, _hwAddr, Port);
    }

    private static TcpListener? CreateListener(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            l.Start();
            return l;
        }
        catch (SocketException) { return null; }
    }

    /// <summary>Change le nom affiché sur l'iPhone.</summary>
    public void Rename(string name)
    {
        Name = name;
        _mdns.Unadvertise();
        _mdns.Advertise(name, _hwAddr, Port);
    }

    /// <summary>Envoie une commande à l'iPhone (suivant, précédent, pause…).</summary>
    public Task<bool> SendCommandAsync(RemoteCommand cmd)
    {
        var remote = _current?.Remote;
        return remote == null ? Task.FromResult(false) : remote.SendAsync(cmd);
    }

    /// <summary>Coupe la connexion en cours.</summary>
    public void Disconnect()
    {
        Session? s;
        lock (_lock) { s = _current; _current = null; }
        s?.Dispose();
        _pipeline.Flush();
        SetState(ReceiverState.Waiting);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Error?.Invoke(this, ex.Message); continue; }

            client.NoDelay = true;
            _ = Task.Run(() => HandleClient(client, ct), ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        var session = new Session(this, client);
        try
        {
            var stream = client.GetStream();
            var reader = new RtspReader(stream);
            while (!ct.IsCancellationRequested)
            {
                var req = await reader.ReadAsync(ct).ConfigureAwait(false);
                if (req == null) break;
                var resp = session.Handle(req);
                var bytes = resp.Serialize();
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                if (req.Method == "TEARDOWN") break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { Error?.Invoke(this, ex.Message); }
        finally
        {
            bool wasCurrent;
            lock (_lock) { wasCurrent = ReferenceEquals(_current, session); if (wasCurrent) _current = null; }
            session.Dispose();
            client.Dispose();
            if (wasCurrent)
            {
                _pipeline.Flush();
                Track = TrackInfo.Empty;
                TrackChanged?.Invoke(this, Track);
                ArtworkChanged?.Invoke(this, null);
                SetState(ReceiverState.Waiting);
            }
        }
    }

    private void SetState(ReceiverState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(this, s);
    }

    /// <summary>Une connexion RTSP (un iPhone).</summary>
    private sealed class Session : IDisposable
    {
        private readonly AirPlayReceiver _owner;
        private readonly IPAddress _localIp, _remoteIp;
        private AlacConfig _alac = new();
        private byte[]? _aesKey, _aesIv;
        private RtpReceiver? _rtp;
        private uint _progressStart;

        public DacpRemote? Remote { get; private set; }

        public Session(AirPlayReceiver owner, TcpClient client)
        {
            _owner = owner;
            _localIp = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
            _remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        }

        public RtspResponse Handle(RtspRequest req)
        {
            var resp = new RtspResponse();
            resp.Add("CSeq", req.Header("CSeq"));
            resp.Add("Server", "AirTunes/105.1");
            resp.Add("Audio-Jack-Status", "connected; type=analog");

            string challenge = req.Header("Apple-Challenge");
            if (challenge.Length > 0) resp.Add("Apple-Response", ChallengeResponse(challenge));

            try
            {
                switch (req.Method)
                {
                    case "OPTIONS":
                        resp.Add("Public", "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER");
                        break;
                    case "ANNOUNCE": Announce(req); break;
                    case "SETUP": Setup(req, resp); break;
                    case "RECORD":
                        resp.Add("Audio-Latency", "11025");
                        _owner.SetState(ReceiverState.Playing);
                        break;
                    case "FLUSH":
                        _rtp?.Flush();
                        _owner.SetState(ReceiverState.Paused);
                        break;
                    case "SET_PARAMETER": SetParameter(req); break;
                    case "GET_PARAMETER":
                        resp.Add("Content-Type", "text/parameters");
                        resp.Body = Encoding.ASCII.GetBytes($"volume: {VolumeToDb(_owner._pipeline.RemoteVolume).ToString("0.000000", CultureInfo.InvariantCulture)}\r\n");
                        break;
                    case "TEARDOWN":
                        _rtp?.Dispose(); _rtp = null;
                        break;
                    default:
                        // POST /feedback, etc.
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RTSP {req.Method}: {ex}");
                resp.Status = 500; resp.Reason = "Internal Server Error";
            }
            return resp;
        }

        private void Announce(RtspRequest req)
        {
            // Une nouvelle connexion prend la main sur l'ancienne (comme une vraie enceinte AirPlay).
            Session? previous;
            lock (_owner._lock) { previous = _owner._current; _owner._current = this; }
            if (previous != null && !ReferenceEquals(previous, this)) previous.Dispose();

            _aesKey = null; _aesIv = null;
            foreach (var raw in req.BodyText.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("a=fmtp:", StringComparison.Ordinal)) _alac = AlacConfig.FromFmtp(line[7..]);
                else if (line.StartsWith("a=rsaaeskey:", StringComparison.Ordinal)) _aesKey = AirPortKey.DecryptAesKey(FromB64(line[12..]));
                else if (line.StartsWith("a=aesiv:", StringComparison.Ordinal)) _aesIv = FromB64(line[8..]);
                else if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal) && !line.Contains("AppleLossless", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Codec non pris en charge : " + line);
            }

            string dacpId = req.Header("DACP-ID"), activeRemote = req.Header("Active-Remote");
            Remote = dacpId.Length > 0 ? new DacpRemote(_owner._mdns, _remoteIp, dacpId, activeRemote) : null;

            string agent = req.Header("User-Agent");
            _owner.ClientName = agent.StartsWith("AirPlay", StringComparison.OrdinalIgnoreCase) ? "iPhone" : (agent.Length > 0 ? agent : "iPhone");
            _owner.SetState(ReceiverState.Connected);
        }

        private void Setup(RtspRequest req, RtspResponse resp)
        {
            int clientControl = 0;
            foreach (var part in req.Header("Transport").Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0] == "control_port") int.TryParse(kv[1], out clientControl);
            }

            _rtp?.Dispose();
            _rtp = new RtpReceiver(_owner._pipeline, _alac, _aesKey, _aesIv);
            _rtp.Start(_remoteIp, clientControl);
            _owner._pipeline.Flush();

            resp.Add("Transport", $"RTP/AVP/UDP;unicast;mode=record;server_port={_rtp.AudioPort};control_port={_rtp.ControlPort};timing_port={_rtp.TimingPort}");
            resp.Add("Session", "1");
        }

        private void SetParameter(RtspRequest req)
        {
            string type = req.Header("Content-Type");
            if (type.StartsWith("text/parameters", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var raw in req.BodyText.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("volume:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(line[7..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
                        {
                            float v = DbToVolume(db);
                            _owner._pipeline.RemoteVolume = v;
                            _owner.RemoteVolumeChanged?.Invoke(_owner, v);
                        }
                    }
                    else if (line.StartsWith("progress:", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = line[9..].Trim().Split('/');
                        if (p.Length == 3 && uint.TryParse(p[0], out var start) && uint.TryParse(p[1], out var cur) && uint.TryParse(p[2], out var end))
                        {
                            _progressStart = start;
                            double sr = _alac.SampleRate;
                            var prog = new PlaybackProgress(Math.Max(0, (int)(cur - start) / sr), Math.Max(0, (int)(end - start) / sr), DateTime.UtcNow);
                            _owner.Progress = prog;
                            _owner.ProgressChanged?.Invoke(_owner, prog);
                        }
                    }
                }
            }
            else if (type.StartsWith("application/x-dmap-tagged", StringComparison.OrdinalIgnoreCase))
            {
                var d = Dmap.Parse(req.Body);
                var t = new TrackInfo(Dmap.GetString(d, "minm"), Dmap.GetString(d, "asar"), Dmap.GetString(d, "asal"));
                _owner.Track = t;
                _owner.TrackChanged?.Invoke(_owner, t);
                _owner.SetState(ReceiverState.Playing);
            }
            else if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _owner.ArtworkChanged?.Invoke(_owner, req.Body.Length > 0 && !type.Contains("none") ? req.Body : null);
            }
        }

        private string ChallengeResponse(string challengeB64)
        {
            var challenge = FromB64(challengeB64);
            var ip = _localIp.IsIPv4MappedToIPv6 ? _localIp.MapToIPv4() : _localIp;
            var ipBytes = ip.GetAddressBytes();
            var data = new byte[Math.Max(32, challenge.Length + ipBytes.Length + 6)];
            Buffer.BlockCopy(challenge, 0, data, 0, challenge.Length);
            Buffer.BlockCopy(ipBytes, 0, data, challenge.Length, ipBytes.Length);
            Buffer.BlockCopy(_owner._hwAddr, 0, data, challenge.Length + ipBytes.Length, 6);
            return Convert.ToBase64String(AirPortKey.SignRaw(data)).TrimEnd('=');
        }

        private static byte[] FromB64(string s)
        {
            s = s.Trim();
            int pad = (4 - s.Length % 4) % 4;
            return Convert.FromBase64String(s + new string('=', pad));
        }

        public void Dispose()
        {
            _rtp?.Dispose();
            _rtp = null;
        }
    }

    // L'iPhone envoie le volume en dB : -30 (min) à 0 (max), -144 = muet.
    internal static float DbToVolume(double db)
    {
        if (db <= -30) return 0f;
        if (db >= 0) return 1f;
        double lin = (db + 30) / 30;          // 0..1
        return (float)Math.Pow(lin, 2.0);     // courbe proche de celle de l'iPhone
    }

    internal static double VolumeToDb(float v) => v <= 0 ? -144 : Math.Sqrt(v) * 30 - 30;

    public void Dispose()
    {
        _cts?.Cancel();
        Disconnect();
        _listener?.Stop();
        _mdns.Dispose();
    }
}
