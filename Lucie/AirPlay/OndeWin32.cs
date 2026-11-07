using System;
using System.Runtime.InteropServices;

namespace Lucie.AirPlay;

/// <summary>Petits appels Windows du widget AirPlay (repris d'Onde).</summary>
internal static class OndeWin32
{
    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr refData);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // gardé en champ statique pour que le ramasse-miettes ne le libère pas
    private static SubclassProc? _proc;
    private static IntPtr _subclassed;
    private static volatile bool _pinBottom;

    /// <summary>Déplace la fenêtre comme si on tenait la barre de titre.</summary>
    public static void BeginDrag(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    public static double GetScale(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>Coins arrondis Windows 11 (ignoré sur Windows 10) et bordure DWM masquée.</summary>
    public static void SetCorners(IntPtr hwnd, bool round)
    {
        try
        {
            int pref = round ? 2 : 1; // 2 = ROUND, 1 = DONOTROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            uint none = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint));
        }
        catch { /* Windows 10 */ }
    }

    /// <summary>
    /// Mode « fixé sur le bureau » : la fenêtre reste derrière toutes les autres, même quand on clique dessus.
    /// </summary>
    public static void PinToBottom(IntPtr hwnd, bool pin)
    {
        if (_subclassed != hwnd)
        {
            _proc ??= WndProc;
            SetWindowSubclass(hwnd, _proc, (UIntPtr)7, UIntPtr.Zero);
            _subclassed = hwnd;
        }
        _pinBottom = pin;
        if (pin) SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == WM_WINDOWPOSCHANGING && _pinBottom && lParam != IntPtr.Zero)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            wp.hwndInsertAfter = HWND_BOTTOM;
            wp.flags &= ~SWP_NOZORDER;
            Marshal.StructureToPtr(wp, lParam, false);
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
