using Lucie.Native;

namespace Lucie.Services;

/// <summary>Raccourci clavier global (ex. Ctrl+Alt+L) qui marche même quand Lucie n'est pas au premier plan.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4C55; // « LU »
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly Win32.SubclassProc _proc; // gardé en mémoire tant que la fenêtre vit
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        Win32.SetWindowSubclass(_hwnd, _proc, (UIntPtr)1, IntPtr.Zero);
    }

    /// <summary>Enregistre le raccourci. Renvoie false s'il est invalide ou déjà pris par une autre appli.</summary>
    public bool Register(string combo)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(combo) || !KeyboardSimulator.TryParse(combo, out var keys)) return false;

        uint modifiers = MOD_NOREPEAT;
        uint key = 0;
        foreach (var k in keys)
        {
            switch (k)
            {
                case 0x11: modifiers |= MOD_CONTROL; break;
                case 0x10: modifiers |= MOD_SHIFT; break;
                case 0x12: modifiers |= MOD_ALT; break;
                case 0x5B: modifiers |= MOD_WIN; break;
                default: key = k; break;
            }
        }
        if (key == 0) return false;

        _registered = Win32.RegisterHotKey(_hwnd, HotkeyId, modifiers, key);
        return _registered;
    }

    private void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            return IntPtr.Zero;
        }
        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose() => Unregister();
}
