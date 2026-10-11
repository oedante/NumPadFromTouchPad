using NumPad.Core.Keys;

namespace NumPad.Core.Hotkeys;

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
        if (string.IsNullOrWhiteSpace(text)) { error = "Сочетание не задано."; return false; }

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
                    if (key is not null) { error = "В сочетании может быть только одна основная клавиша."; return false; }
                    key = raw;
                    break;
            }
        }
        if (key is null) { error = "Нет основной клавиши."; return false; }
        if (!KeyCatalog.TryGet(key, out var def)) { error = $"Неизвестная клавиша «{key}»."; return false; }
        if (def.IsModifier) { error = "Основная клавиша не может быть модификатором."; return false; }

        bool isHighF = def.Group == KeyGroup.Function && int.TryParse(def.Name.AsSpan(1), out int fn) && fn >= 13;
        if (mods == HotkeyModifiers.None && !isHighF)
        {
            error = "Нужен хотя бы один модификатор (Ctrl, Alt, Shift, Win), иначе клавиша перестанет работать в приложениях.";
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
            "Ctrl+Space" or "Win+Space" or "Ctrl+Shift+Space" =>"Используется для переключения языка/IME.",
            "Ctrl+Alt+Delete" => "Зарезервировано Windows и не может быть назначено.",
            "Win+L" => "Блокировка компьютера — зарезервировано Windows.",
            "Alt+Tab" or "Alt+F4" or "Alt+Escape" or "Ctrl+Escape" => "Системное сочетание Windows.",
            "Ctrl+Shift+Escape" => "Открывает диспетчер задач.",
            "Ctrl+C" or "Ctrl+V" or "Ctrl+X" or "Ctrl+Z" or "Ctrl+A" or "Ctrl+S" or "Ctrl+N" or "Ctrl+O" or "Ctrl+P" or "Ctrl+F"
                => "Стандартное сочетание приложений — оно перестанет работать в программах.",
            _ when Modifiers == HotkeyModifiers.Win => "Большинство сочетаний Win+клавиша заняты Windows.",
            _ when Modifiers == HotkeyModifiers.Alt && Key.Group == KeyGroup.Letter => "Alt+буква используется для меню приложений.",
            _ => null,
        };
    }
}
