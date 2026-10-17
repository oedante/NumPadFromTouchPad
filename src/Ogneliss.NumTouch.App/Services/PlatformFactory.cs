using Ogneliss.NumTouch.Core.Config;
using Ogneliss.NumTouch.Core.Diagnostics;
using Ogneliss.NumTouch.Platform;
using Ogneliss.NumTouch.Platform.Linux;
using Ogneliss.NumTouch.Platform.MacOS;
using Ogneliss.NumTouch.Platform.Windows;

namespace Ogneliss.NumTouch.App.Services;

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
            return new NullPlatform(L.T("This operating system is not supported."));
        }
        catch (Exception ex)
        {
            log.Error("Platform services initialization failed", ex);
            return new NullPlatform(L.F("Could not initialize access to input devices: {0}", ex.Message));
        }
    }
}
