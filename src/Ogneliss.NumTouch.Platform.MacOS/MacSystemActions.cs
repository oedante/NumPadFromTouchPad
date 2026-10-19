using System.Diagnostics;
using System.Text;
using Ogneliss.NumTouch.Core.Keys;
using static Ogneliss.NumTouch.Platform.MacOS.MacNative;

namespace Ogneliss.NumTouch.Platform.MacOS;

/// <summary>
/// Запуск приложений вместо клавиш, которых нет на Mac: «Калькулятор» — по идентификатору пакета, «Почта» и «Браузер» —
/// программа, выбранная в системе по умолчанию для mailto: и https: (Launch Services), а не обязательно Mail и Safari.
/// </summary>
public static unsafe class MacSystemActions
{
    public static bool Launch(MacSubstitute sub)
    {
        if (sub.BundleId is { } bundle) return Open("-b", bundle);
        if (sub.DefaultAppForUrl is { } url && DefaultAppFor(url) is { } app) return Open("-a", app);
        return false;
    }

    /// <summary>Путь к приложению, которое система открывает для адреса (например, почтовая программа для mailto:).</summary>
    public static string? DefaultAppFor(string url)
    {
        nint str = CFStringCreateWithCString(0, url, kCFStringEncodingUTF8);
        if (str == 0) return null;
        nint cfUrl = 0, appUrl = 0, path = 0;
        try
        {
            cfUrl = CFURLCreateWithString(0, str, 0);
            if (cfUrl == 0) return null;
            appUrl = LSCopyDefaultApplicationURLForURL(cfUrl, 0xFFFFFFFF, 0);
            if (appUrl == 0) return null;
            path = CFURLCopyFileSystemPath(appUrl, 0);
            if (path == 0) return null;
            var buffer = new byte[4096];
            fixed (byte* p = buffer)
                if (!CFStringGetCString(path, p, buffer.Length, kCFStringEncodingUTF8)) return null;
            int len = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
        }
        finally
        {
            if (path != 0) CFRelease(path);
            if (appUrl != 0) CFRelease(appUrl);
            if (cfUrl != 0) CFRelease(cfUrl);
            CFRelease(str);
        }
    }

    private static bool Open(string option, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(option);
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception) { return false; }
    }
}
