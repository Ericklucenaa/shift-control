namespace ShiftControl.Overlay;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>Atalho global no formato aceito por RegisterHotKey (modificadores + código de tecla virtual).</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public override string ToString() => Hotkeys.Format(this);
}

public static class Hotkeys
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74, ["F6"] = 0x75,
        ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["Space"] = 0x20,
    };

    /// <summary>
    /// Aceita "Ctrl+Shift+O", "alt + f10" etc. Exige ao menos um modificador para teclas comuns,
    /// para não sequestrar a digitação dentro dos jogos.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            HotkeyModifiers m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "shift" => HotkeyModifiers.Shift,
                "alt" => HotkeyModifiers.Alt,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (m == HotkeyModifiers.None || modifiers.HasFlag(m)) return false;
            modifiers |= m;
        }

        string key = parts[^1];
        uint vk;
        bool isFunctionLike = false;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
            vk = char.ToUpperInvariant(key[0]);
        else if (NamedKeys.TryGetValue(key, out vk))
            isFunctionLike = key.StartsWith('F') || key is "Pause" or "ScrollLock";
        else
            return false;

        if (modifiers == HotkeyModifiers.None && !isFunctionLike) return false;
        hotkey = new Hotkey(modifiers, vk);
        return true;
    }

    public static string Format(Hotkey hotkey)
    {
        var parts = new List<string>(4);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        var named = NamedKeys.FirstOrDefault(kv => kv.Value == hotkey.VirtualKey).Key;
        parts.Add(named ?? ((char)hotkey.VirtualKey).ToString());
        return string.Join('+', parts);
    }
}
