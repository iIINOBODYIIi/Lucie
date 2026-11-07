using System.Runtime.InteropServices;

namespace Lucie.Services;

/// <summary>
/// Icône de Lucie dans la zone de notification (près de l'horloge),
/// avec un petit menu au clic droit.
/// Les messages de l'icône arrivent sur la fenêtre du widget : voir HandleMessage.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = 0x8000 + 0x51;   // WM_APP + 0x51
    private const uint IconId = 1;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public int uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private bool _ownsIcon;
    private string _tooltip;
    private bool _added;

    /// <summary>Clic gauche sur l'icône.</summary>
    public event Action? Clicked;
    /// <summary>Double-clic sur l'icône.</summary>
    public event Action? DoubleClicked;
    /// <summary>Clic droit : à toi de remplir le menu.</summary>
    public event Action? MenuRequested;

    public TrayIcon(IntPtr hwnd, string tooltip, string iconPath)
    {
        _hwnd = hwnd;
        _tooltip = tooltip;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        int cx = GetSystemMetrics(49 /* SM_CXSMICON */), cy = GetSystemMetrics(50 /* SM_CYSMICON */);
        if (File.Exists(iconPath))
            _icon = LoadImageW(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, cx, cy, 0x10 /* LR_LOADFROMFILE */);
        _ownsIcon = _icon != IntPtr.Zero;
        if (_icon == IntPtr.Zero) _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512 /* IDI_APPLICATION */));

        Add();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (!_added) return;
        var data = Data(NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip.Length > 120 ? _tooltip[..120] : _tooltip,
        szInfo = "",
        szInfoTitle = ""
    };

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    /// <summary>À appeler depuis la procédure de la fenêtre. Renvoie true si le message était pour l'icône.</summary>
    public bool HandleMessage(uint msg, IntPtr lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // L'explorateur Windows a redémarré : on remet l'icône.
            Add();
            return false;
        }
        if (msg != CallbackMessage) return false;

        switch (lParam.ToInt32() & 0xFFFF)
        {
            case WM_LBUTTONUP: Clicked?.Invoke(); break;
            case WM_LBUTTONDBLCLK: DoubleClicked?.Invoke(); break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU: MenuRequested?.Invoke(); break;
        }
        return true;
    }

    /// <summary>Affiche un menu natif à l'emplacement de la souris. Renvoie l'index choisi, ou -1.</summary>
    public int ShowMenu(IReadOnlyList<MenuEntry> entries)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Separator)
                    AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
                else
                    AppendMenuW(menu, e.Checked ? 0x8u /* MF_CHECKED */ : 0u, (UIntPtr)(uint)(i + 1), e.Text);
            }

            GetCursorPos(out var p);
            SetForegroundWindow(_hwnd);   // sinon le menu ne se ferme pas en cliquant ailleurs
            uint chosen = TrackPopupMenu(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */ | 0x0020 /* TPM_BOTTOMALIGN */,
                p.X, p.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            return chosen == 0 ? -1 : (int)chosen - 1;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }
}

internal readonly record struct MenuEntry(string Text, bool Checked = false, bool Separator = false)
{
    public static MenuEntry Line => new("", Separator: true);
}
