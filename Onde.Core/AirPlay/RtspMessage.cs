using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Onde.Core.AirPlay;

internal sealed class RtspRequest
{
    public string Method { get; init; } = "";
    public string Uri { get; init; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
    public string BodyText => Encoding.UTF8.GetString(Body);
}

internal sealed class RtspResponse
{
    public int Status { get; set; } = 200;
    public string Reason { get; set; } = "OK";
    public List<KeyValuePair<string, string>> Headers { get; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public RtspResponse Add(string k, string v) { Headers.Add(new(k, v)); return this; }

    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("RTSP/1.0 ").Append(Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason).Append("\r\n");
        foreach (var h in Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        if (Body.Length > 0) sb.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        if (Body.Length == 0) return head;
        var all = new byte[head.Length + Body.Length];
        Buffer.BlockCopy(head, 0, all, 0, head.Length);
        Buffer.BlockCopy(Body, 0, all, head.Length, Body.Length);
        return all;
    }
}

/// <summary>Lit des requêtes RTSP (en-têtes texte + corps binaire) depuis un flux TCP.</summary>
internal sealed class RtspReader
{
    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _start, _end;

    public RtspReader(Stream stream) => _stream = stream;

    public async Task<RtspRequest?> ReadAsync(CancellationToken ct)
    {
        // 1) chercher la fin des en-têtes
        int headerEnd;
        while ((headerEnd = FindHeaderEnd()) < 0)
        {
            if (!await FillAsync(ct)) return null;
        }

        string head = Encoding.UTF8.GetString(_buf, _start, headerEnd - _start);
        _start = headerEnd + 4;

        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var req = new RtspRequest { Method = first[0], Uri = first[1] };
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) req.Headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
        }

        // 2) corps
        if (int.TryParse(req.Header("Content-Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int len) && len > 0)
        {
            if (len > 8 * 1024 * 1024) return null;
            var body = new byte[len];
            int copied = 0;
            while (copied < len)
            {
                if (_end == _start && !await FillAsync(ct)) return null;
                int n = Math.Min(len - copied, _end - _start);
                Buffer.BlockCopy(_buf, _start, body, copied, n);
                _start += n; copied += n;
            }
            req.Body = body;
        }
        return req;
    }

    private int FindHeaderEnd()
    {
        for (int i = _start; i + 3 < _end; i++)
            if (_buf[i] == '\r' && _buf[i + 1] == '\n' && _buf[i + 2] == '\r' && _buf[i + 3] == '\n') return i;
        return -1;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start; _start = 0;
        }
        if (_end == _buf.Length) return false; // en-têtes trop longs
        int n = await _stream.ReadAsync(_buf.AsMemory(_end, _buf.Length - _end), ct).ConfigureAwait(false);
        if (n <= 0) return false;
        _end += n;
        return true;
    }
}
