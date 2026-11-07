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

    // ───── Forme de la fenêtre ─────

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    /// <summary>Découpe la fenêtre en rectangle aux coins arrondis (diamètre des coins en pixels).</summary>
    public static void SetRoundRegion(IntPtr hwnd, int x, int y, int width, int height, int diameter)
    {
        if (width <= 0 || height <= 0) return;
        diameter = Math.Clamp(diameter, 0, Math.Min(width, height));
        IntPtr region = CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, diameter, diameter);
        if (SetWindowRgn(hwnd, region, true) == 0)
            DeleteObject(region); // sinon Windows en devient propriétaire
    }

    // ───── Mettre une fenêtre au premier plan ─────

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>Fenêtre principale visible d'un des processus donnés (0 si aucune).</summary>
    public static IntPtr FindMainWindow(HashSet<uint> processIds)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!processIds.Contains(pid)) return true;
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if (GetWindowTextLength(h) == 0) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Restaure la fenêtre si elle est réduite et la place devant tout le reste.</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        ShowWindow(hwnd, IsIconic(hwnd) ? 9 /* SW_RESTORE */ : 5 /* SW_SHOW */);

        // Windows n'autorise pas toujours une appli à passer une autre fenêtre devant :
        // on « s'accroche » au fil de la fenêtre active le temps de l'échange.
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint me = GetCurrentThreadId();
        bool attached = foregroundThread != me && AttachThreadInput(me, foregroundThread, true);

        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);

        if (attached) AttachThreadInput(me, foregroundThread, false);
    }

    // ───── Raccourci clavier global ─────

    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Le bouton principal de la souris est-il enfoncé (gère les souris de gaucher) ?</summary>
    public static bool IsPrimaryButtonDown()
    {
        int vk = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01;
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    // ───── Plein écran (jeux, vidéos) ─────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow"
    };

    /// <summary>
    /// Une appli est-elle en plein écran (jeu, vidéo…) sur le même écran que cette fenêtre ?
    /// Une fenêtre simplement agrandie ne compte pas.
    /// </summary>
    public static bool IsFullscreenAppOnSameMonitor(IntPtr ownWindow)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownWindow || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        if (!IsWindowVisible(fg)) return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;   // nos propres fenêtres (paramètres…)

        var cls = new System.Text.StringBuilder(256);
        GetClassNameW(fg, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return false;

        IntPtr monitor = MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor != MonitorFromWindow(ownWindow, 2)) return false;

        // Jeu en plein écran exclusif / mode présentation : Windows le sait directement.
        if (SHQueryUserNotificationState(out int state) == 0 && (state == 3 /* D3D plein écran */ || state == 4 /* présentation */))
            return true;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(fg, out var r)) return false;

        bool covers = r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top &&
                      r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
        if (!covers) return false;

        // Une fenêtre normale agrandie (avec barre de titre) n'est pas du plein écran.
        long style = GetWindowLongPtr(fg, -16).ToInt64();
        bool hasCaption = (style & 0x00C00000) == 0x00C00000;
        return !(hasCaption && IsZoomed(fg));
    }

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
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint WM_NCCALCSIZE = 0x0083;

    /// <summary>
    /// Transforme la fenêtre en simple « calque » sans aucun cadre (ni bordure, ni barre de titre,
    /// ni trait d'un pixel) : tout ce qui est affiché, c'est le contenu.
    /// </summary>
    public static void MakeFrameless(IntPtr hwnd)
    {
        const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
                   WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_BORDER = 0x00800000,
                   WS_DLGFRAME = 0x00400000;
        const long WS_POPUP = unchecked((long)0x80000000);
        const long WS_EX_DLGMODALFRAME = 0x1, WS_EX_WINDOWEDGE = 0x100, WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000;

        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_BORDER | WS_DLGFRAME);
        style |= WS_POPUP;
        SetWindowLongPtr(hwnd, -16, new IntPtr(style));

        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        ex &= ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
        SetWindowLongPtr(hwnd, -20, new IntPtr(ex));

        // SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0004 | 0x0010 | 0x0020);
    }

    /// <summary>Facteur d'échelle de l'écran (1.0 = 100 %, 1.5 = 150 %…).</summary>
    public static double ScaleFor(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}
