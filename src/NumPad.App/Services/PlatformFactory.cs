using NumPad.Core.Config;
using NumPad.Core.Diagnostics;
using NumPad.Platform;
using NumPad.Platform.Linux;
using NumPad.Platform.MacOS;
using NumPad.Platform.Windows;

namespace NumPad.App.Services;

/// <summary>Выбор реализации платформенных сервисов во время выполнения: один и тот же exe-код на всех ОС.</summary>
public static class PlatformFactory
{
    public static IPlatformServices Create(ILog log, SessionGuard session)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new WindowsPlatform(log, session);
            if (OperatingSystem.IsLinux()) return new LinuxPlatform(log);
            if (OperatingSystem.IsMacOS()) return new MacPlatform(log);
            return new NullPlatform("Операционная система не поддерживается.");
        }
        catch (Exception ex)
        {
            log.Error("Не удалось инициализировать платформенные сервисы", ex);
            return new NullPlatform("Не удалось инициализировать доступ к устройствам ввода: " + ex.Message);
        }
    }
}
