namespace PegLine.Core;

/// <summary>
/// Win32 RegisterHotKey modifier flags. Values match MOD_ALT, MOD_CONTROL, MOD_SHIFT, and MOD_WIN.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>
/// A global hotkey stored as text such as "Ctrl+Alt+T". Virtual-key codes match the Win32 VK_* values.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey, string KeyName)
{
    private static readonly Dictionary<string, (int Vk, string Name)> Keys = BuildKeys();
    private static readonly Dictionary<int, string> NamesByVirtualKey = BuildReverse();

    public string ToStorageString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName);
        return string.Join("+", parts);
    }

    public string ToDisplayString() => ToStorageString().Replace("+", " + ", StringComparison.Ordinal);

    public static HotkeyGesture Default => Parse(AppSettings.DefaultHotkey);

    public static HotkeyGesture Parse(string text)
    {
        if (!TryParse(text, out var gesture))
        {
            throw new FormatException($"Invalid hotkey '{text}'.");
        }

        return gesture;
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var part in parts)
        {
            if (IsModifier(part, "ctrl", "control", "ctl"))
            {
                modifiers |= HotkeyModifiers.Control;
            }
            else if (IsModifier(part, "alt", "menu"))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (IsModifier(part, "shift"))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else if (IsModifier(part, "win", "windows", "meta"))
            {
                modifiers |= HotkeyModifiers.Windows;
            }
            else if (key is null)
            {
                key = part;
            }
            else
            {
                return false;
            }
        }

        if (key is null || modifiers == HotkeyModifiers.None || !Keys.TryGetValue(key, out var mapped))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, mapped.Vk, mapped.Name);
        return true;
    }

    public static bool TryFromVirtualKey(HotkeyModifiers modifiers, int virtualKey, out HotkeyGesture gesture)
    {
        gesture = default;
        if (modifiers == HotkeyModifiers.None || !NamesByVirtualKey.TryGetValue(virtualKey, out var name))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, name);
        return true;
    }

    private static bool IsModifier(string part, params string[] names)
    {
        foreach (var name in names)
        {
            if (part.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, (int Vk, string Name)> BuildKeys()
    {
        var keys = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            keys[letter.ToString()] = (letter, letter.ToString());
        }

        for (var digit = '0'; digit <= '9'; digit++)
        {
            keys[digit.ToString()] = (digit, digit.ToString());
        }

        for (var function = 1; function <= 24; function++)
        {
            keys["F" + function] = (0x70 + function - 1, "F" + function);
        }

        keys["Space"] = (0x20, "Space");
        keys["Esc"] = (0x1B, "Esc");
        keys["Escape"] = (0x1B, "Esc");
        keys["Left"] = (0x25, "Left");
        keys["Up"] = (0x26, "Up");
        keys["Right"] = (0x27, "Right");
        keys["Down"] = (0x28, "Down");
        keys["Insert"] = (0x2D, "Insert");
        keys["Delete"] = (0x2E, "Delete");
        keys["Home"] = (0x24, "Home");
        keys["End"] = (0x23, "End");
        keys["PageUp"] = (0x21, "PageUp");
        keys["PageDown"] = (0x22, "PageDown");
        return keys;
    }

    private static Dictionary<int, string> BuildReverse()
    {
        var reverse = new Dictionary<int, string>();
        foreach (var (vk, name) in Keys.Values)
        {
            reverse.TryAdd(vk, name);
        }

        return reverse;
    }
}
