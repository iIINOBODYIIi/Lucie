using NAudio.Wave;

namespace Lucie.Services;

/// <summary>Liste les micros et sorties audio. -1 = périphérique par défaut de Windows.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<string> Inputs()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return list;
    }

    public static IReadOnlyList<string> Outputs()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
            list.Add(WaveOut.GetCapabilities(i).ProductName);
        return list;
    }

    public static int FindInput(string? name) => IndexOf(Inputs(), name);

    public static int FindOutput(string? name) => IndexOf(Outputs(), name);

    private static int IndexOf(IReadOnlyList<string> names, string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
