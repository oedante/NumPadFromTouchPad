using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Ogneliss.NumTouch.App.Services;

public enum OsFamily { Windows, MacOS, Linux }

/// <summary>
/// Родной вид на каждой ОС: системный шрифт, цвета светлой и тёмной темы в духе системы (Windows 11, macOS,
/// GNOME/libadwaita), привычные размеры и порядок кнопок. Тема (светлая/тёмная) и акцентный цвет берутся из
/// системы через FluentTheme; здесь — только собственные кисти программы для обеих тем.
/// Стили, зависящие от ОС, выбираются классом окна: <c>Window.win</c>, <c>Window.mac</c>, <c>Window.linux</c>.
/// </summary>
public static class PlatformTheme
{
    public static OsFamily Os { get; } =
        OperatingSystem.IsMacOS() ? OsFamily.MacOS : OperatingSystem.IsLinux() ? OsFamily.Linux : OsFamily.Windows;

    /// <summary>Класс окна для стилей конкретной ОС.</summary>
    public static string OsClass => Os switch { OsFamily.MacOS => "mac", OsFamily.Linux => "linux", _ => "win" };

    /// <summary>Системный шрифт интерфейса и запасные варианты (для Linux — шрифт по умолчанию fontconfig).</summary>
    public static FontManagerOptions FontOptions() => Os switch
    {
        OsFamily.Windows => new FontManagerOptions
        {
            DefaultFamilyName = "Segoe UI Variable Text",
            FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily("Segoe UI") } },
        },
        OsFamily.MacOS => new FontManagerOptions
        {
            DefaultFamilyName = ".AppleSystemUIFont",
            FontFallbacks = new[]
            {
                new FontFallback { FontFamily = new FontFamily("SF Pro Text") },
                new FontFallback { FontFamily = new FontFamily("Helvetica Neue") },
            },
        },
        _ => new FontManagerOptions(), // Linux: системный шрифт (Cantarell, Ubuntu, Noto Sans…) через fontconfig
    };

    /// <summary>Моноширинный шрифт системы (диагностика).</summary>
    public static string MonoFont => Os switch
    {
        OsFamily.MacOS => "SF Mono, Menlo, Monaco",
        OsFamily.Linux => "monospace, DejaVu Sans Mono, Liberation Mono",
        _ => "Cascadia Mono, Consolas, Courier New",
    };

    /// <summary>Порядок кнопок диалога: в Windows утвердительная кнопка первой, в macOS и GNOME — последней (справа).</summary>
    public static bool AffirmativeLast => Os != OsFamily.Windows;

    /// <summary>Записать кисти и размеры текущей ОС в ресурсы приложения (до создания окон).</summary>
    public static void Apply(Application app)
    {
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();

        void Brush(string key, string l, string d)
        {
            light[key] = new SolidColorBrush(Color.Parse(l));
            dark[key] = new SolidColorBrush(Color.Parse(d));
        }

        // Фон окна, боковая панель, карточки — по мотивам системных приложений каждой ОС.
        switch (Os)
        {
            case OsFamily.MacOS:
                Brush("SidebarBrush", "#E8E8E8", "#2A2A2A");
                Brush("PageBrush", "#F5F5F5", "#1E1E1E");
                Brush("CardBrush", "#FFFFFF", "#2C2C2C");
                Brush("LineBrush", "#E0E0E0", "#3A3A3A");
                Brush("NavHoverBrush", "#14000000", "#14FFFFFF");
                Brush("NavSelectedBrush", "#1F000000", "#26FFFFFF");
                break;
            case OsFamily.Linux:
                Brush("SidebarBrush", "#EBEBED", "#2E2E32");
                Brush("PageBrush", "#FAFAFA", "#222226");
                Brush("CardBrush", "#FFFFFF", "#303034");
                Brush("LineBrush", "#DEDEE0", "#3D3D42");
                Brush("NavHoverBrush", "#0F000000", "#12FFFFFF");
                Brush("NavSelectedBrush", "#1A000000", "#1FFFFFFF");
                break;
            default: // Windows 11
                Brush("SidebarBrush", "#F3F3F3", "#202020");
                Brush("PageBrush", "#F3F3F3", "#202020");
                Brush("CardBrush", "#FFFFFF", "#2B2B2B");
                Brush("LineBrush", "#E5E5E5", "#383838");
                Brush("NavHoverBrush", "#0A000000", "#0FFFFFFF");
                Brush("NavSelectedBrush", "#0F000000", "#15FFFFFF");
                break;
        }

        Brush("TextBrush", "#1B1B1B", "#F2F2F2");
        Brush("SubtleTextBrush", "#616161", "#A8A8A8");
        Brush("OnBrush", "#1E8E4E", "#4CC37A");
        Brush("ErrorBrush", "#C42B1C", "#FF8A80");
        Brush("ErrorBackgroundBrush", "#FDF0EF", "#3B2422");
        Brush("WarningBrush", "#9D5D00", "#F2B14C");
        Brush("HintBrush", "#FFF4CE", "#3A3220");
        Brush("HintBorderBrush", "#E8C85A", "#6B5A2A");
        Brush("BadgeOffBrush", "#E6E6E6", "#3A3A3A");
        Brush("KeycapBrush", "#F6F6F6", "#383838");
        Brush("KeycapBorderBrush", "#D0D0D0", "#4E4E4E");
        Brush("StatusBoxBrush", "#FFFFFF", "#2B2B2B");
        Brush("StatusDotOffBrush", "#9A9A9A", "#7A7A7A");

        // Холст раскладки (редактор, предпросмотр, калибровка).
        Brush("Canvas.Sticker", "#E4E4E0", "#3A3A3D");
        Brush("Canvas.StickerBorder", "#C8C8C2", "#55555A");
        Brush("Canvas.Key", "#FFFFFF", "#505055");
        Brush("Canvas.KeyDisabled", "#EEEEEA", "#45454A");
        Brush("Canvas.KeyBorder", "#C8C8C2", "#66666C");
        Brush("Canvas.Text", "#2A2A2A", "#F0F0F0");
        Brush("Canvas.TextSubtle", "#8A8A86", "#A0A0A6");
        Brush("Canvas.Selected", "#323232", "#F5F5F5");
        Brush("Canvas.Ruler", "#F7F7F5", "#2E2E31");
        Brush("Canvas.RulerLine", "#B4B4AE", "#5E5E64");
        Brush("Canvas.GridMinor", "#1C000000", "#1EFFFFFF");
        Brush("Canvas.GridMajor", "#38000000", "#3CFFFFFF");
        Brush("Canvas.Contact", "#B0323232", "#C0F0F0F0");

        app.Resources.ThemeDictionaries[ThemeVariant.Light] = light;
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = dark;

        // Размеры по ОС: в macOS основной кегль 13, заголовки страниц скромнее.
        bool mac = Os == OsFamily.MacOS;
        app.Resources["ControlContentThemeFontSize"] = mac ? 13.0 : 14.0;
        app.Resources["PageTitleFontSize"] = mac ? 22.0 : Os == OsFamily.Linux ? 24.0 : 28.0;
        app.Resources["SectionFontSize"] = mac ? 14.0 : 16.0;
        app.Resources["MonoFontFamily"] = new FontFamily(MonoFont);
    }

    /// <summary>Подписи клавиш-модификаторов в подсказках: в macOS — символы ⌘ ⌥ ⌃ ⇧.</summary>
    public static string Shortcut(string text)
    {
        if (Os != OsFamily.MacOS) return text;
        // Ctrl+Z → ⌘Z (основной модификатор в macOS — Command); Alt → ⌥; Shift → ⇧.
        return text.Replace("Ctrl+", "⌘").Replace("Alt+", "⌥").Replace("Shift+", "⇧").Replace("Win+", "⌘");
    }

    /// <summary>
    /// Глобальная горячая клавиша для показа: в macOS — символами, как в меню (⌃⌥N), иначе как есть (Ctrl+Alt+N).
    /// Хранится и вводится по-прежнему в виде «Ctrl+Alt+N».
    /// </summary>
    public static string HotkeyText(string hotkey)
    {
        if (Os != OsFamily.MacOS || string.IsNullOrEmpty(hotkey)) return hotkey;
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts.Take(parts.Length - 1))
            sb.Append(p switch { "Ctrl" => "⌃", "Alt" => "⌥", "Shift" => "⇧", "Win" => "⌘", _ => p + "+" });
        string key = parts.Length > 0 ? parts[^1] : "";
        return sb + (key.StartsWith('D') && key.Length == 2 && char.IsDigit(key[1]) ? key[1..] : key);
    }

    /// <summary>Название клавиши-модификатора для текста подсказок.</summary>
    public static string AltKeyName => Os == OsFamily.MacOS ? "Option (⌥)" : "Alt";
    public static string ShiftKeyName => Os == OsFamily.MacOS ? "Shift (⇧)" : "Shift";
}
