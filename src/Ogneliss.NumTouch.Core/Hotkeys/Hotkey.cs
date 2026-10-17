using Ogneliss.NumTouch.Core.Keys;

namespace Ogneliss.NumTouch.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// <summary>Глобальное сочетание клавиш: «Ctrl+Alt+N». Значения флагов совпадают с MOD_* RegisterHotKey.</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, string KeyName)
{
    public static Hotkey Default { get; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "N");

    public KeyDefinition Key => KeyCatalog.Get(KeyName);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyCatalog.TryGet(KeyName, out var k) ? k.Name : KeyName);
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey, out string error)
    {
        hotkey = Default;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = L.T("No shortcut specified."); return false; }

        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win": case "windows": mods |= HotkeyModifiers.Win; break;
                default:
                    if (key is not null) { error = L.T("A shortcut can have only one main key."); return false; }
                    key = raw;
                    break;
            }
        }
        if (key is null) { error = L.T("No main key."); return false; }
        if (!KeyCatalog.TryGet(key, out var def)) { error = L.F("Unknown key “{0}”.", key); return false; }
        if (def.IsModifier) { error = L.T("The main key cannot be a modifier."); return false; }

        bool isHighF = def.Group == KeyGroup.Function && int.TryParse(def.Name.AsSpan(1), out int fn) && fn >= 13;
        if (mods == HotkeyModifiers.None && !isHighF)
        {
            error = L.T("At least one modifier (Ctrl, Alt, Shift, Win) is required, otherwise the key would stop working in applications.");
            return false;
        }
        hotkey = new Hotkey(mods, def.Name);
        return true;
    }

    /// <summary>Предупреждения о сочетаниях, занятых Windows или распространёнными приложениями. null — предупреждений нет.</summary>
    public string? GetConflictWarning()
    {
        string s = ToString();
        return s switch
        {
            "Ctrl+Space" or "Win+Space" or "Ctrl+Shift+Space" => L.T("Used to switch the input language/IME."),
            "Ctrl+Alt+Delete" => L.T("Reserved by Windows and cannot be assigned."),
            "Win+L" => L.T("Locks the computer — reserved by Windows."),
            "Alt+Tab" or "Alt+F4" or "Alt+Escape" or "Ctrl+Escape" => L.T("A Windows system shortcut."),
            "Ctrl+Shift+Escape" => L.T("Opens Task Manager."),
            "Ctrl+C" or "Ctrl+V" or "Ctrl+X" or "Ctrl+Z" or "Ctrl+A" or "Ctrl+S" or "Ctrl+N" or "Ctrl+O" or "Ctrl+P" or "Ctrl+F"
                => L.T("A standard application shortcut — it would stop working in programs."),
            _ when Modifiers == HotkeyModifiers.Win => L.T("Most Win+key shortcuts are taken by Windows."),
            _ when Modifiers == HotkeyModifiers.Alt && Key.Group == KeyGroup.Letter => L.T("Alt+letter is used for application menus."),
            _ => null,
        };
    }
}
