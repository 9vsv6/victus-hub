using System.Windows.Input;

namespace HpVictusControl;

/// <summary>
/// A global keyboard shortcut: a key plus modifiers, stored in settings as text like "Ctrl+Alt+F11".
/// </summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key) {

    // RegisterHotKey modifier flags. MOD_NOREPEAT stops a held key from firing over and over.
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;

    public uint NativeModifiers =>
        ModNoRepeat
        | (Modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
        | (Modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
        | (Modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
        | (Modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>
    /// A shortcut needs Ctrl, Alt or Win: with only Shift (or nothing) it would swallow ordinary
    /// typing everywhere on the PC, since a registered hotkey never reaches other apps.
    /// </summary>
    public bool HasRequiredModifier => (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public override string ToString() {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Text shown for modifiers held so far while a shortcut is being recorded.</summary>
    public static string DescribePartial(ModifierKeys modifiers) {
        string text = new Hotkey(modifiers, Key.None).ToString();
        return text.EndsWith("+None", StringComparison.Ordinal) ? text[..^4] + "…" : "…";
    }

    public static bool TryParse(string? text, out Hotkey hotkey) {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        ModifierKeys modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            switch (raw.ToLowerInvariant()) {
                case "ctrl": case "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": case "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None || !TryParseKey(raw, out key)) return false;
                    break;
            }
        }

        if (key == Key.None || IsModifierKey(key)) return false;
        hotkey = new Hotkey(modifiers, key);
        return hotkey.HasRequiredModifier;
    }

    // Digits are shown as "1" rather than WPF's "D1"; everything else uses the key's own name.
    private static string KeyName(Key key) => key switch {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString()
    };

    private static bool TryParseKey(string text, out Key key) {
        if (text.Length == 1 && char.IsDigit(text[0])) {
            key = Key.D0 + (text[0] - '0');
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None;
    }
}
