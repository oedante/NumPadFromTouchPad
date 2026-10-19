using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Ogneliss.NumTouch.App.Services;

/// <summary>
/// Меню программы в строке меню macOS (рядом с яблоком), целиком на языке интерфейса: «О программе», «Настройки… ⌘,»,
/// «Открыть окно», «Скрыть ⌘H», «Скрыть остальные ⌥⌘H», «Показать все», «Завершить ⌘Q». Стандартные английские
/// пункты Avalonia отключены (Program.BuildAvaloniaApp). На Windows и Linux ничего не делает — там меню в окне не принято.
/// </summary>
internal static class MacMenu
{
    public static void Install(Application app, Action showWindow, Action<string> showPage, Action showAbout, Action quit)
    {
        if (PlatformTheme.Os != OsFamily.MacOS) return;
        const string name = "Ogneliss NumTouch";

        var about = new NativeMenuItem(L.F("About {0}", name));
        about.Click += (_, _) => showAbout();
        var settings = new NativeMenuItem(L.T("Settings…")) { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
        settings.Click += (_, _) => showPage("settings");
        var open = new NativeMenuItem(L.T("Open window"));
        open.Click += (_, _) => showWindow();
        var hide = new NativeMenuItem(L.F("Hide {0}", name)) { Gesture = new KeyGesture(Key.H, KeyModifiers.Meta) };
        hide.Click += (_, _) => AppKit.Send("hide:");
        var hideOthers = new NativeMenuItem(L.T("Hide Others")) { Gesture = new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt) };
        hideOthers.Click += (_, _) => AppKit.Send("hideOtherApplications:");
        var showAll = new NativeMenuItem(L.T("Show All"));
        showAll.Click += (_, _) => AppKit.Send("unhideAllApplications:");
        var exit = new NativeMenuItem(L.F("Quit {0}", name)) { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
        exit.Click += (_, _) => quit();

        var menu = new NativeMenu();
        menu.Add(about);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(settings);
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(hide);
        menu.Add(hideOthers);
        menu.Add(showAll);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);
        NativeMenu.SetMenu(app, menu);
    }

    /// <summary>Команды NSApplication (скрыть программу и т. п.) — в Avalonia для них нет открытого API.</summary>
    private static class AppKit
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [DllImport(ObjC)] private static extern nint objc_getClass(string name);
        [DllImport(ObjC)] private static extern nint sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Msg(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Msg(nint receiver, nint selector, nint arg);

        /// <summary>[NSApp selector nil] — вызывается из главного потока (обработчики меню выполняются в нём).</summary>
        public static void Send(string selector)
        {
            try
            {
                nint app = Msg(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (app != 0) Msg(app, sel_registerName(selector), 0);
            }
            catch (Exception) { /* не критично: пункт меню просто ничего не сделает */ }
        }
    }
}
