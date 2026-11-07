using System.Runtime.InteropServices;
using Lucie.Native;

namespace Lucie.Services;

/// <summary>
/// « Tape » des raccourcis clavier comme si tu appuyais toi-même dessus.
/// Format accepté : "Ctrl+Maj+M", "Win+Alt+R", "Ctrl+Shift+F5"…
/// </summary>
internal static class KeyboardSimulator
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
    private const uint KEYEVENTF_KEYUP = 0x2;

    public const ushort VK_MEDIA_NEXT = 0xB0;
    public const ushort VK_MEDIA_PREV = 0xB1;
    public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;
    public const ushort VK_VOLUME_MUTE = 0xAD;
    public const ushort VK_VOLUME_DOWN = 0xAE;
    public const ushort VK_VOLUME_UP = 0xAF;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["CTL"] = 0x11,
        ["SHIFT"] = 0x10, ["MAJ"] = 0x10, ["MAJUSCULE"] = 0x10,
        ["ALT"] = 0x12,
        ["WIN"] = 0x5B, ["WINDOWS"] = 0x5B,
        ["ESPACE"] = 0x20, ["SPACE"] = 0x20,
        ["ENTREE"] = 0x0D, ["ENTRÉE"] = 0x0D, ["ENTER"] = 0x0D,
        ["TAB"] = 0x09,
        ["ECHAP"] = 0x1B, ["ÉCHAP"] = 0x1B, ["ESC"] = 0x1B,
        ["SUPPR"] = 0x2E, ["DELETE"] = 0x2E, ["DEL"] = 0x2E,
        ["INSER"] = 0x2D, ["INSERT"] = 0x2D,
        ["DEBUT"] = 0x24, ["DÉBUT"] = 0x24, ["HOME"] = 0x24,
        ["FIN"] = 0x23, ["END"] = 0x23,
        ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22,
        ["HAUT"] = 0x26, ["UP"] = 0x26,
        ["BAS"] = 0x28, ["DOWN"] = 0x28,
        ["GAUCHE"] = 0x25, ["LEFT"] = 0x25,
        ["DROITE"] = 0x27, ["RIGHT"] = 0x27,
        ["IMPRECRAN"] = 0x2C, ["IMPRÉCRAN"] = 0x2C, ["PRINTSCREEN"] = 0x2C,
        ["PAUSE"] = 0x13,
        ["PLAYPAUSE"] = VK_MEDIA_PLAY_PAUSE, ["NEXT"] = VK_MEDIA_NEXT, ["PREV"] = VK_MEDIA_PREV,
        ["MUTE"] = 0xAD, ["VOLDOWN"] = 0xAE, ["VOLUP"] = 0xAF,
    };

    private static readonly HashSet<ushort> Extended = new()
    {
        0x5B, 0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28,
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB3
    };

    public static bool TryParse(string combo, out List<ushort> keys)
    {
        keys = new List<ushort>();
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var k = raw.Replace(" ", "").ToUpperInvariant();
            if (Named.TryGetValue(k, out var vk))
                keys.Add(vk);
            else if (k.Length == 1 && k[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                keys.Add(k[0]);
            else if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24)
                keys.Add((ushort)(0x6F + n));
            else
                return false;
        }
        return keys.Count > 0;
    }

    /// <summary>Appuie sur toutes les touches, attend un instant, puis les relâche.</summary>
    public static async Task<bool> SendComboAsync(string combo)
    {
        if (!TryParse(combo, out var keys)) return false;

        // Comme une vraie main : chaque touche enfoncée l'une après l'autre, maintenue un instant,
        // puis relâchées dans l'ordre inverse. Certaines applis (Discord, jeux) ignorent les
        // raccourcis « instantanés ».
        foreach (var k in keys)
        {
            Send(new[] { Make(k, up: false) });
            await Task.Delay(25);
        }
        await Task.Delay(90);
        foreach (var k in keys.AsEnumerable().Reverse())
        {
            Send(new[] { Make(k, up: true) });
            await Task.Delay(15);
        }
        return true;
    }

    public static void Tap(ushort vk) => Send(new[] { Make(vk, false), Make(vk, true) });

    private static Win32.INPUT Make(ushort vk, bool up)
    {
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return new Win32.INPUT
        {
            type = INPUT_KEYBOARD,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)Win32.MapVirtualKey(vk, 0),
                    dwFlags = flags
                }
            }
        };
    }

    private static void Send(Win32.INPUT[] inputs) =>
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
}
