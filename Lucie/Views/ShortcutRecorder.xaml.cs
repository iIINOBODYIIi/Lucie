using Lucie.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Lucie.Views;

/// <summary>
/// Champ pour saisir un raccourci clavier : on l'écrit à la main (« Ctrl+Maj+M »),
/// ou on clique sur le rond rouge puis on appuie directement sur les touches.
/// </summary>
public sealed partial class ShortcutRecorder : UserControl
{
    public static readonly DependencyProperty KeysProperty = DependencyProperty.Register(
        nameof(Keys), typeof(string), typeof(ShortcutRecorder),
        new PropertyMetadata("", (d, _) => ((ShortcutRecorder)d).OnKeysChanged()));

    public string Keys
    {
        get => (string)GetValue(KeysProperty);
        set => SetValue(KeysProperty, value);
    }

    private bool _recording;
    private bool _updating;

    public ShortcutRecorder()
    {
        InitializeComponent();
        KeysBox.PreviewKeyDown += OnPreviewKeyDown;
        KeysBox.LostFocus += (_, _) => StopRecording();
        OnKeysChanged();
    }

    private void OnKeysChanged()
    {
        if (!_updating && KeysBox.Text != (Keys ?? ""))
        {
            _updating = true;
            KeysBox.Text = Keys ?? "";
            _updating = false;
        }
        Validate();
    }

    private void KeysBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || _recording) return;
        _updating = true;
        Keys = KeysBox.Text;
        _updating = false;
        Validate();
    }

    private void Validate()
    {
        if (_recording) return;
        var text = (Keys ?? "").Trim();
        bool bad = text.Length > 0 && !KeyboardSimulator.TryParse(text, out _);
        Hint.Text = bad ? "Raccourci non reconnu (ex. Ctrl+Maj+M)" : "";
        Hint.Visibility = bad ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────── Enregistrement des touches ─────────────

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording();
            return;
        }
        _recording = true;
        RecordIcon.Glyph = "";   // carré « stop »
        RecordIcon.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x8A, 0x8A));
        KeysBox.Text = "";
        KeysBox.PlaceholderText = "Appuie sur les touches…";
        Hint.Text = "Échap pour annuler. Les raccourcis avec la touche Windows s'écrivent à la main.";
        Hint.Visibility = Visibility.Visible;
        KeysBox.Focus(FocusState.Programmatic);
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        RecordIcon.Glyph = "";
        RecordIcon.ClearValue(FontIcon.ForegroundProperty);
        KeysBox.PlaceholderText = "Ctrl+Maj+M";
        _updating = true;
        KeysBox.Text = Keys ?? "";
        _updating = false;
        Validate();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }

        var parts = new List<string>();
        if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsDown(VirtualKey.Shift)) parts.Add("Maj");
        if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");

        if (IsModifier(key))
        {
            // Juste les touches spéciales pour l'instant : on affiche, on attend la touche principale.
            KeysBox.Text = string.Join("+", parts) + "+…";
            return;
        }

        var name = KeyName(key);
        if (name is null)
        {
            Hint.Text = "Cette touche n'est pas prise en charge, essaie-en une autre.";
            return;
        }

        parts.Add(name);
        StopRecording();
        Keys = string.Join("+", parts);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static string? KeyName(VirtualKey key)
    {
        int v = (int)key;
        if (v is >= 0x41 and <= 0x5A) return ((char)v).ToString();                 // A–Z
        if (v is >= 0x30 and <= 0x39) return ((char)v).ToString();                 // 0–9
        if (v is >= 0x60 and <= 0x69) return ((char)('0' + v - 0x60)).ToString(); // pavé numérique
        if (v is >= 0x70 and <= 0x87) return "F" + (v - 0x6F);                     // F1–F24

        return key switch
        {
            VirtualKey.Space => "Espace",
            VirtualKey.Enter => "Entrée",
            VirtualKey.Tab => "Tab",
            VirtualKey.Delete => "Suppr",
            VirtualKey.Insert => "Inser",
            VirtualKey.Home => "Début",
            VirtualKey.End => "Fin",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Up => "Haut",
            VirtualKey.Down => "Bas",
            VirtualKey.Left => "Gauche",
            VirtualKey.Right => "Droite",
            VirtualKey.Snapshot => "ImprÉcran",
            VirtualKey.Pause => "Pause",
            _ => null
        };
    }
}
