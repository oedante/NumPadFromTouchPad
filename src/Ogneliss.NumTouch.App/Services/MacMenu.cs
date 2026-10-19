using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Ogneliss.NumTouch.App.Services;

/// <summary>
/// Меню программы в строке меню macOS (рядом с яблоком): «О программе», «Настройки… ⌘,». Пункты «Скрыть» и
/// «Завершить ⌘Q» Avalonia добавляет сама. На Windows и Linux ничего не делает — там меню в окне не принято.
/// </summary>
internal static class MacMenu
{
    public static void Install(Application app, Action showWindow, Action<string> showPage, Action showAbout)
    {
        if (PlatformTheme.Os != OsFamily.MacOS) return;
        var about = new NativeMenuItem(L.F("About {0}", "Ogneliss NumTouch"));
        about.Click += (_, _) => showAbout();
        var settings = new NativeMenuItem(L.T("Settings…")) { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
        settings.Click += (_, _) => showPage("settings");
        var open = new NativeMenuItem(L.T("Open window"));
        open.Click += (_, _) => showWindow();
        var menu = new NativeMenu();
        menu.Add(about);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(settings);
        menu.Add(open);
        NativeMenu.SetMenu(app, menu);
    }
}
