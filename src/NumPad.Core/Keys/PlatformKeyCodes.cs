namespace NumPad.Core.Keys;

/// <summary>
/// Коды клавиш для Linux (input-event-codes.h, KEY_*) и macOS (Carbon kVK_*).
/// Коды Windows хранятся прямо в <see cref="KeyDefinition"/>. Отсутствие имени в таблице = клавиша
/// недоступна на платформе (валидатор и бэкенд сообщают об этом, а не отправляют «что-то похожее»).
/// </summary>
public static class PlatformKeyCodes
{
    public static readonly IReadOnlyDictionary<string, ushort> Linux = BuildLinux();
    public static readonly IReadOnlyDictionary<string, ushort> Mac = BuildMac();

    public static bool TryGetLinux(KeyDefinition key, out ushort code) => Linux.TryGetValue(key.Name, out code);
    public static bool TryGetMac(KeyDefinition key, out ushort code) => Mac.TryGetValue(key.Name, out code);

    private static Dictionary<string, ushort> BuildLinux()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            // цифровой блок
            ["Numpad7"] = 71, ["Numpad8"] = 72, ["Numpad9"] = 73, ["NumpadSubtract"] = 74,
            ["Numpad4"] = 75, ["Numpad5"] = 76, ["Numpad6"] = 77, ["NumpadAdd"] = 78,
            ["Numpad1"] = 79, ["Numpad2"] = 80, ["Numpad3"] = 81, ["Numpad0"] = 82, ["NumpadDecimal"] = 83,
            ["NumpadMultiply"] = 55, ["NumpadDivide"] = 98, ["NumpadEnter"] = 96, ["NumLock"] = 69,
            // В Linux цифровой блок с выключенным Num Lock даёт навигацию через те же коды KP*.
            ["NumpadInsert"] = 82, ["NumpadEnd"] = 79, ["NumpadDown"] = 80, ["NumpadPageDown"] = 81,
            ["NumpadLeft"] = 75, ["NumpadClear"] = 76, ["NumpadRight"] = 77, ["NumpadHome"] = 71,
            ["NumpadUp"] = 72, ["NumpadPageUp"] = 73, ["NumpadDelete"] = 83,
            // навигация
            ["Insert"] = 110, ["Delete"] = 111, ["Home"] = 102, ["End"] = 107, ["PageUp"] = 104, ["PageDown"] = 109,
            ["Up"] = 103, ["Down"] = 108, ["Left"] = 105, ["Right"] = 106,
            ["Enter"] = 28, ["Tab"] = 15, ["Escape"] = 1, ["Backspace"] = 14, ["Space"] = 57,
            ["PrintScreen"] = 99, ["ScrollLock"] = 70, ["Pause"] = 119, ["CapsLock"] = 58, ["Apps"] = 127,
            // модификаторы
            ["LeftShift"] = 42, ["RightShift"] = 54, ["LeftCtrl"] = 29, ["RightCtrl"] = 97,
            ["LeftAlt"] = 56, ["RightAlt"] = 100, ["LeftWin"] = 125, ["RightWin"] = 126,
            // символы
            ["Minus"] = 12, ["Equals"] = 13, ["Comma"] = 51, ["Period"] = 52,
            // мультимедиа
            ["LaunchCalculator"] = 140, ["LaunchMail"] = 155, ["BrowserHome"] = 172,
            ["VolumeMute"] = 113, ["VolumeDown"] = 114, ["VolumeUp"] = 115, ["MediaPlayPause"] = 164,
        };
        ushort[] f = { 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 87, 88, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194 };
        for (int i = 0; i < 24; i++) d[$"F{i + 1}"] = f[i];
        // Буквы: коды Linux 1..88 совпадают со скан-кодами Set 1 — берём их из каталога.
        foreach (var k in KeyCatalog.All.Where(k => k.Group is KeyGroup.Letter or KeyGroup.Digit))
            d[k.Name] = k.WinScan;
        return d;
    }

    private static Dictionary<string, ushort> BuildMac()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Numpad0"] = 0x52, ["Numpad1"] = 0x53, ["Numpad2"] = 0x54, ["Numpad3"] = 0x55, ["Numpad4"] = 0x56,
            ["Numpad5"] = 0x57, ["Numpad6"] = 0x58, ["Numpad7"] = 0x59, ["Numpad8"] = 0x5B, ["Numpad9"] = 0x5C,
            ["NumpadDecimal"] = 0x41, ["NumpadAdd"] = 0x45, ["NumpadSubtract"] = 0x4E, ["NumpadMultiply"] = 0x43,
            ["NumpadDivide"] = 0x4B, ["NumpadEnter"] = 0x4C, ["NumLock"] = 0x47 /* Clear на Mac */,
            ["Insert"] = 0x72 /* Help */, ["Delete"] = 0x75, ["Home"] = 0x73, ["End"] = 0x77, ["PageUp"] = 0x74, ["PageDown"] = 0x79,
            ["Up"] = 0x7E, ["Down"] = 0x7D, ["Left"] = 0x7B, ["Right"] = 0x7C,
            ["Enter"] = 0x24, ["Tab"] = 0x30, ["Escape"] = 0x35, ["Backspace"] = 0x33, ["Space"] = 0x31,
            ["CapsLock"] = 0x39,
            ["LeftShift"] = 0x38, ["RightShift"] = 0x3C, ["LeftCtrl"] = 0x3B, ["RightCtrl"] = 0x3E,
            ["LeftAlt"] = 0x3A, ["RightAlt"] = 0x3D, ["LeftWin"] = 0x37, ["RightWin"] = 0x36,
            ["Minus"] = 0x1B, ["Equals"] = 0x18, ["Comma"] = 0x2B, ["Period"] = 0x2F,
            ["VolumeMute"] = 0x4A, ["VolumeDown"] = 0x49, ["VolumeUp"] = 0x48,
            ["A"] = 0x00, ["S"] = 0x01, ["D"] = 0x02, ["F"] = 0x03, ["H"] = 0x04, ["G"] = 0x05, ["Z"] = 0x06, ["X"] = 0x07,
            ["C"] = 0x08, ["V"] = 0x09, ["B"] = 0x0B, ["Q"] = 0x0C, ["W"] = 0x0D, ["E"] = 0x0E, ["R"] = 0x0F, ["Y"] = 0x10,
            ["T"] = 0x11, ["O"] = 0x1F, ["U"] = 0x20, ["I"] = 0x22, ["P"] = 0x23, ["L"] = 0x25, ["J"] = 0x26, ["K"] = 0x28,
            ["N"] = 0x2D, ["M"] = 0x2E,
            ["D1"] = 0x12, ["D2"] = 0x13, ["D3"] = 0x14, ["D4"] = 0x15, ["D6"] = 0x16, ["D5"] = 0x17, ["D9"] = 0x19,
            ["D7"] = 0x1A, ["D8"] = 0x1C, ["D0"] = 0x1D,
            ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60, ["F6"] = 0x61, ["F7"] = 0x62, ["F8"] = 0x64,
            ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F, ["F13"] = 0x69, ["F14"] = 0x6B, ["F15"] = 0x71,
            ["F16"] = 0x6A, ["F17"] = 0x40, ["F18"] = 0x4F, ["F19"] = 0x50, ["F20"] = 0x5A,
        };
        // Навигация «из цифрового блока»: на Mac нет Num Lock — те же keypad-коды.
        foreach (var (alias, target) in new[] { ("NumpadInsert", "Numpad0"), ("NumpadEnd", "Numpad1"), ("NumpadDown", "Numpad2"),
                     ("NumpadPageDown", "Numpad3"), ("NumpadLeft", "Numpad4"), ("NumpadClear", "Numpad5"), ("NumpadRight", "Numpad6"),
                     ("NumpadHome", "Numpad7"), ("NumpadUp", "Numpad8"), ("NumpadPageUp", "Numpad9"), ("NumpadDelete", "NumpadDecimal") })
            d[alias] = d[target];
        return d;
    }
}
