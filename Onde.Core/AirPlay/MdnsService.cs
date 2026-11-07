using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace Onde.Core.AirPlay;

/// <summary>
/// Annonce Bonjour/mDNS du service AirPlay (_raop._tcp) pour que l'iPhone voie le PC
/// dans sa liste d'enceintes, et résolution du port DACP de l'iPhone (télécommande).
/// </summary>
internal sealed class MdnsService : IDisposable
{
    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
    private ServiceProfile? _profile;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> _dacpWaiters = new(StringComparer.OrdinalIgnoreCase);

    public MdnsService()
    {
        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);
        _mdns.AnswerReceived += OnAnswer;
    }

    public void Start() => _mdns.Start();

    public void Advertise(string displayName, byte[] hwAddr, int port)
    {
        string mac = Convert.ToHexString(hwAddr);
        string instance = $"{mac}@{Sanitize(displayName)}";
        var p = new ServiceProfile(instance, "_raop._tcp", (ushort)port);
        // ServiceProfile ajoute déjà "txtvers=1"
        p.AddProperty("ch", "2");
        p.AddProperty("cn", "0,1");
        p.AddProperty("et", "0,1");
        p.AddProperty("ek", "1");
        p.AddProperty("sv", "false");
        p.AddProperty("da", "true");
        p.AddProperty("sr", "44100");
        p.AddProperty("ss", "16");
        p.AddProperty("pw", "false");
        p.AddProperty("vn", "65537");
        p.AddProperty("tp", "UDP");
        p.AddProperty("md", "0,1,2");   // métadonnées : texte, pochette, progression
        p.AddProperty("vs", "105.1");
        p.AddProperty("am", "Onde");
        p.AddProperty("sf", "0x4");
        p.AddProperty("fv", "76400.10");
        _profile = p;
        _sd.Advertise(p);
    }

    public void Unadvertise()
    {
        if (_profile == null) return;
        try { _sd.Unadvertise(_profile); } catch { /* ignoré */ }
        _profile = null;
    }

    /// <summary>Trouve le port HTTP de télécommande (DACP) de l'iPhone.</summary>
    public async Task<int?> ResolveDacpPortAsync(string dacpId, CancellationToken ct)
    {
        string key = "iTunes_Ctrl_" + dacpId;
        var tcs = _dacpWaiters.GetOrAdd(key, _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
        for (int attempt = 0; attempt < 6 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                _mdns.SendQuery(new DomainName($"{key}._dacp._tcp.local"), DnsClass.IN, DnsType.SRV);
            }
            catch { /* réseau pas prêt */ }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(1500, ct)).ConfigureAwait(false);
            if (done == tcs.Task) return tcs.Task.Result;
        }
        return null;
    }

    private void OnAnswer(object? sender, MessageEventArgs e)
    {
        if (_dacpWaiters.IsEmpty) return;
        foreach (var rr in e.Message.Answers.Concat(e.Message.AdditionalRecords).OfType<SRVRecord>())
        {
            string name = rr.Name?.ToString() ?? "";
            foreach (var kv in _dacpWaiters)
            {
                if (name.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    kv.Value.TrySetResult(rr.Port);
            }
        }
    }

    private static string Sanitize(string s)
    {
        var clean = new string(s.Where(c => c != '.' && c != '\\' && !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "Onde" : clean[..Math.Min(clean.Length, 50)];
    }

    public void Dispose()
    {
        Unadvertise();
        _sd.Dispose();
        _mdns.Stop();
        _mdns.Dispose();
    }
}
