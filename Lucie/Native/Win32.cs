using System.Runtime.InteropServices;

namespace Lucie.Native;

/// <summary>Appels Windows bas niveau (souris, inactivité, clavier, fenêtre).</summary>
internal static class Win32
{
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public const int HTCAPTION = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Temps écoulé depuis la dernière action clavier/souris.</summary>
    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - info.dwTime));
    }

    /// <summary>Retire coins, bordure et ombre que Windows 11 ajoute aux fenêtres.</summary>
    public static void StripFrame(IntPtr hwnd)
    {
        int doNotRound = 1;                      // DWMWA_WINDOW_CORNER_PREFERENCE
        DwmSetWindowAttribute(hwnd, 33, ref doNotRound, sizeof(int));
        int noColor = unchecked((int)0xFFFFFFFE); // DWMWA_BORDER_COLOR = aucune
        DwmSetWindowAttribute(hwnd, 34, ref noColor, sizeof(int));
        int disabled = 1;                        // DWMWA_NCRENDERING_POLICY = désactivé (pas d'ombre)
        DwmSetWindowAttribute(hwnd, 2, ref disabled, sizeof(int));
    }

    /// <summary>Facteur d'échelle de l'écran (1.0 = 100 %, 1.5 = 150 %…).</summary>
    public static double ScaleFor(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}
