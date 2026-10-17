namespace Ogneliss.NumTouch.Core.Keys;

public enum KeyGroup { Numpad, Navigation, Editing, Function, Modifier, Letter, Digit, Symbol, Media, System }

/// <summary>
/// Описание клавиши. Имя (<see cref="Name"/>) — платформенно-независимый идентификатор, который хранится
/// в JSON-раскладке. Коды Windows (VK + скан-код + extended) — данные для бэкенда SendInput.
/// <see cref="EnglishName"/> — подпись на английском (ключ перевода), <see cref="DisplayName"/> — на языке интерфейса.
/// </summary>
public sealed record KeyDefinition(
    string Name,
    string EnglishName,
    KeyGroup Group,
    ushort WinVk,
    ushort WinScan,
    bool WinExtended,
    bool IsModifier = false,
    string? NumLockOffName = null)
{
    public string DisplayName => L.T(EnglishName);
    public override string ToString() => Name;
}

/// <summary>Справочник поддерживаемых клавиш. Коды сверены с таблицей скан-кодов Set 1 / документацией Virtual-Key Codes.</summary>
public static class KeyCatalog
{
    private static readonly Dictionary<string, KeyDefinition> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Shift"] = "LeftShift", ["Ctrl"] = "LeftCtrl", ["Control"] = "LeftCtrl", ["Alt"] = "LeftAlt",
        ["Win"] = "LeftWin", ["Windows"] = "LeftWin", ["Return"] = "Enter", ["Del"] = "Delete",
        ["Ins"] = "Insert", ["PgUp"] = "PageUp", ["PgDn"] = "PageDown", ["Esc"] = "Escape",
        ["PrtSc"] = "PrintScreen", ["PrintScr"] = "PrintScreen", ["Snapshot"] = "PrintScreen",
        ["Back"] = "Backspace", ["Calculator"] = "LaunchCalculator",
    };

    public static IReadOnlyCollection<KeyDefinition> All => ByName.Values;

    static KeyCatalog()
    {
        // --- Цифровой блок (VK_NUMPADx + скан-коды цифрового блока). При выключенном Num Lock
        //     настоящий цифровой блок даёт навигационные клавиши — поле NumLockOffName.
        Add("Numpad0", "Num 0", KeyGroup.Numpad, 0x60, 0x52, false, numLockOff: "NumpadInsert");
        Add("Numpad1", "Num 1", KeyGroup.Numpad, 0x61, 0x4F, false, numLockOff: "NumpadEnd");
        Add("Numpad2", "Num 2", KeyGroup.Numpad, 0x62, 0x50, false, numLockOff: "NumpadDown");
        Add("Numpad3", "Num 3", KeyGroup.Numpad, 0x63, 0x51, false, numLockOff: "NumpadPageDown");
        Add("Numpad4", "Num 4", KeyGroup.Numpad, 0x64, 0x4B, false, numLockOff: "NumpadLeft");
        Add("Numpad5", "Num 5", KeyGroup.Numpad, 0x65, 0x4C, false, numLockOff: "NumpadClear");
        Add("Numpad6", "Num 6", KeyGroup.Numpad, 0x66, 0x4D, false, numLockOff: "NumpadRight");
        Add("Numpad7", "Num 7", KeyGroup.Numpad, 0x67, 0x47, false, numLockOff: "NumpadHome");
        Add("Numpad8", "Num 8", KeyGroup.Numpad, 0x68, 0x48, false, numLockOff: "NumpadUp");
        Add("Numpad9", "Num 9", KeyGroup.Numpad, 0x69, 0x49, false, numLockOff: "NumpadPageUp");
        Add("NumpadDecimal", "Num .", KeyGroup.Numpad, 0x6E, 0x53, false, numLockOff: "NumpadDelete");
        Add("NumpadAdd", "Num +", KeyGroup.Numpad, 0x6B, 0x4E, false);
        Add("NumpadSubtract", "Num −", KeyGroup.Numpad, 0x6D, 0x4A, false);
        Add("NumpadMultiply", "Num *", KeyGroup.Numpad, 0x6A, 0x37, false);
        Add("NumpadDivide", "Num /", KeyGroup.Numpad, 0x6F, 0x35, true);
        Add("NumpadEnter", "Num Enter", KeyGroup.Numpad, 0x0D, 0x1C, true);
        Add("NumLock", "Num Lock", KeyGroup.Numpad, 0x90, 0x45, true);

        // Навигация «из цифрового блока» (без флага extended) — то, что шлёт цифровой блок при Num Lock off.
        Add("NumpadInsert", "Num Ins", KeyGroup.Numpad, 0x2D, 0x52, false);
        Add("NumpadEnd", "Num End", KeyGroup.Numpad, 0x23, 0x4F, false);
        Add("NumpadDown", "Num ↓", KeyGroup.Numpad, 0x28, 0x50, false);
        Add("NumpadPageDown", "Num PgDn", KeyGroup.Numpad, 0x22, 0x51, false);
        Add("NumpadLeft", "Num ←", KeyGroup.Numpad, 0x25, 0x4B, false);
        Add("NumpadClear", "Num Clear", KeyGroup.Numpad, 0x0C, 0x4C, false);
        Add("NumpadRight", "Num →", KeyGroup.Numpad, 0x27, 0x4D, false);
        Add("NumpadHome", "Num Home", KeyGroup.Numpad, 0x24, 0x47, false);
        Add("NumpadUp", "Num ↑", KeyGroup.Numpad, 0x26, 0x48, false);
        Add("NumpadPageUp", "Num PgUp", KeyGroup.Numpad, 0x21, 0x49, false);
        Add("NumpadDelete", "Num Del", KeyGroup.Numpad, 0x2E, 0x53, false);

        // --- Навигация и редактирование (флаг extended обязателен, иначе это клавиши цифрового блока).
        Add("Insert", "Insert", KeyGroup.Navigation, 0x2D, 0x52, true);
        Add("Delete", "Delete", KeyGroup.Navigation, 0x2E, 0x53, true);
        Add("Home", "Home", KeyGroup.Navigation, 0x24, 0x47, true);
        Add("End", "End", KeyGroup.Navigation, 0x23, 0x4F, true);
        Add("PageUp", "Page Up", KeyGroup.Navigation, 0x21, 0x49, true);
        Add("PageDown", "Page Down", KeyGroup.Navigation, 0x22, 0x51, true);
        Add("Up", "↑", KeyGroup.Navigation, 0x26, 0x48, true);
        Add("Down", "↓", KeyGroup.Navigation, 0x28, 0x50, true);
        Add("Left", "←", KeyGroup.Navigation, 0x25, 0x4B, true);
        Add("Right", "→", KeyGroup.Navigation, 0x27, 0x4D, true);

        Add("Enter", "Enter", KeyGroup.Editing, 0x0D, 0x1C, false);
        Add("Tab", "Tab", KeyGroup.Editing, 0x09, 0x0F, false);
        Add("Escape", "Esc", KeyGroup.Editing, 0x1B, 0x01, false);
        Add("Backspace", "Backspace", KeyGroup.Editing, 0x08, 0x0E, false);
        Add("Space", L.N("Space"), KeyGroup.Editing, 0x20, 0x39, false);

        Add("PrintScreen", "Print Screen", KeyGroup.System, 0x2C, 0x37, true);
        Add("ScrollLock", "Scroll Lock", KeyGroup.System, 0x91, 0x46, false);
        Add("Pause", "Pause", KeyGroup.System, 0x13, 0x45, false);
        Add("CapsLock", "Caps Lock", KeyGroup.System, 0x14, 0x3A, false);
        Add("Apps", L.N("Menu"), KeyGroup.System, 0x5D, 0x5D, true);

        // --- Модификаторы.
        Add("LeftShift", "Shift", KeyGroup.Modifier, 0xA0, 0x2A, false, isModifier: true);
        Add("RightShift", "Right Shift", KeyGroup.Modifier, 0xA1, 0x36, false, isModifier: true);
        Add("LeftCtrl", "Ctrl", KeyGroup.Modifier, 0xA2, 0x1D, false, isModifier: true);
        Add("RightCtrl", "Right Ctrl", KeyGroup.Modifier, 0xA3, 0x1D, true, isModifier: true);
        Add("LeftAlt", "Alt", KeyGroup.Modifier, 0xA4, 0x38, false, isModifier: true);
        Add("RightAlt", "Right Alt", KeyGroup.Modifier, 0xA5, 0x38, true, isModifier: true);
        Add("LeftWin", "Win", KeyGroup.Modifier, 0x5B, 0x5B, true, isModifier: true);
        Add("RightWin", "Right Win", KeyGroup.Modifier, 0x5C, 0x5C, true, isModifier: true);

        // --- F1..F24.
        ushort[] fScans = { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58,
                            0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76 };
        for (int i = 0; i < 24; i++)
            Add($"F{i + 1}", $"F{i + 1}", KeyGroup.Function, (ushort)(0x70 + i), fScans[i], false);

        // --- Буквы и цифры основного блока.
        ushort[] letterScans = { 0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
                                 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C };
        for (int i = 0; i < 26; i++)
        {
            string letter = ((char)('A' + i)).ToString();
            Add(letter, letter, KeyGroup.Letter, (ushort)(0x41 + i), letterScans[i], false);
        }
        for (int d = 0; d <= 9; d++)
            Add($"D{d}", d.ToString(), KeyGroup.Digit, (ushort)(0x30 + d), (ushort)(d == 0 ? 0x0B : 0x01 + d), false);

        Add("Minus", "-", KeyGroup.Symbol, 0xBD, 0x0C, false);
        Add("Equals", "=", KeyGroup.Symbol, 0xBB, 0x0D, false);
        Add("Comma", ",", KeyGroup.Symbol, 0xBC, 0x33, false);
        Add("Period", ".", KeyGroup.Symbol, 0xBE, 0x34, false);

        // --- Мультимедиа и запуск приложений.
        Add("LaunchCalculator", L.N("Calculator"), KeyGroup.Media, 0xB7, 0x21, true);
        Add("LaunchMail", L.N("Mail"), KeyGroup.Media, 0xB4, 0x6C, true);
        Add("BrowserHome", L.N("Browser"), KeyGroup.Media, 0xAC, 0x32, true);
        Add("VolumeMute", L.N("Mute"), KeyGroup.Media, 0xAD, 0x20, true);
        Add("VolumeDown", L.N("Volume −"), KeyGroup.Media, 0xAE, 0x2E, true);
        Add("VolumeUp", L.N("Volume +"), KeyGroup.Media, 0xAF, 0x30, true);
        Add("MediaPlayPause", L.N("Play/Pause"), KeyGroup.Media, 0xB3, 0x22, true);
    }

    private static void Add(string name, string display, KeyGroup group, ushort vk, ushort scan, bool ext,
        bool isModifier = false, string? numLockOff = null)
        => ByName[name] = new KeyDefinition(name, display, group, vk, scan, ext, isModifier, numLockOff);

    public static bool TryGet(string? name, out KeyDefinition key)
    {
        key = null!;
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (Aliases.TryGetValue(name, out var real)) name = real;
        if (ByName.TryGetValue(name, out var found)) { key = found; return true; }
        return false;
    }

    public static KeyDefinition Get(string name) =>
        TryGet(name, out var k) ? k : throw new KeyNotFoundException($"Unknown key: {name}");

    public static bool IsKnown(string? name) => TryGet(name, out _);
}
