using Avalonia;
using Ogneliss.NumTouch.App.Services;

namespace Ogneliss.NumTouch.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Один экземпляр: второй запуск просит первый показать окно и выходит
        // (иначе два процесса боролись бы за горячую клавишу и оба генерировали бы нажатия).
        if (!args.Contains("--selftest") && !SingleInstance.TryAcquire())
        {
            SingleInstance.RequestShow();
            return 0;
        }
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(PlatformTheme.FontOptions()) // системный шрифт: Segoe UI / San Francisco / шрифт Linux
            // macOS: стандартные пункты меню программы Avalonia английские — свои, на языке интерфейса, в MacMenu.
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
            .LogToTrace();
}
