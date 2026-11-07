using System;
using System.Collections.Generic;
using System.Text;

namespace Onde.Core.AirPlay;

public enum ReceiverState { Waiting, Connected, Playing, Paused }

public sealed record TrackInfo(string Title, string Artist, string Album)
{
    public static readonly TrackInfo Empty = new("", "", "");
}

/// <summary>Position de lecture envoyée par l'iPhone (en secondes).</summary>
public sealed record PlaybackProgress(double Position, double Duration, DateTime ReceivedAt);

/// <summary>Commandes envoyées à l'iPhone via DACP.</summary>
public enum RemoteCommand { PlayPause, Play, Pause, Next, Previous, VolumeUp, VolumeDown, ShuffleToggle }

/// <summary>Parseur minimal du format DMAP (métadonnées titre / artiste / album).</summary>
internal static class Dmap
{
    private static readonly HashSet<string> Containers = new() { "mlit", "mlcl", "cmst", "mdcl" };

    public static Dictionary<string, byte[]> Parse(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, byte[]>();
        Walk(data, result);
        return result;
    }

    private static void Walk(ReadOnlySpan<byte> data, Dictionary<string, byte[]> result)
    {
        int i = 0;
        while (i + 8 <= data.Length)
        {
            string tag = Encoding.ASCII.GetString(data.Slice(i, 4));
            int len = (data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7];
            i += 8;
            if (len < 0 || i + len > data.Length) break;
            var value = data.Slice(i, len);
            if (Containers.Contains(tag)) Walk(value, result);
            else result[tag] = value.ToArray();
            i += len;
        }
    }

    public static string GetString(Dictionary<string, byte[]> d, string tag) =>
        d.TryGetValue(tag, out var v) ? Encoding.UTF8.GetString(v).Trim('\0') : "";
}
