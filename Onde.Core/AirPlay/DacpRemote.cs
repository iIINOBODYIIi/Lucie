using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Onde.Core.AirPlay;

/// <summary>
/// Télécommande DACP : envoie lecture/pause/suivant/précédent à l'iPhone
/// (c'est ce qui permet de changer de musique depuis le PC).
/// </summary>
internal sealed class DacpRemote
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly MdnsService _mdns;
    private readonly IPAddress _host;
    private readonly string _dacpId, _activeRemote;
    private int? _port;
    private Task<int?>? _resolving;

    public DacpRemote(MdnsService mdns, IPAddress host, string dacpId, string activeRemote)
    {
        _mdns = mdns;
        _host = host.IsIPv4MappedToIPv6 ? host.MapToIPv4() : host;
        _dacpId = dacpId;
        _activeRemote = activeRemote;
        _resolving = _mdns.ResolveDacpPortAsync(dacpId, CancellationToken.None);
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_dacpId) && !string.IsNullOrEmpty(_activeRemote);

    public async Task<bool> SendAsync(RemoteCommand cmd)
    {
        if (!IsAvailable) return false;
        if (_port == null)
        {
            _resolving ??= _mdns.ResolveDacpPortAsync(_dacpId, CancellationToken.None);
            _port = await _resolving.ConfigureAwait(false);
            if (_port == null) { _resolving = null; return false; }
        }

        string path = cmd switch
        {
            RemoteCommand.PlayPause => "playpause",
            RemoteCommand.Play => "play",
            RemoteCommand.Pause => "pause",
            RemoteCommand.Next => "nextitem",
            RemoteCommand.Previous => "previtem",
            RemoteCommand.VolumeUp => "volumeup",
            RemoteCommand.VolumeDown => "volumedown",
            RemoteCommand.ShuffleToggle => "shuffle_songs",
            _ => "playpause",
        };

        string host = _host.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{_host.ToString().Replace("%", "%25")}]" : _host.ToString();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{_port}/ctrl-int/1/{path}");
        req.Headers.TryAddWithoutValidation("Active-Remote", _activeRemote);
        try
        {
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            _port = null; _resolving = null; // le port a pu changer : on re-résoudra
            return false;
        }
    }
}
