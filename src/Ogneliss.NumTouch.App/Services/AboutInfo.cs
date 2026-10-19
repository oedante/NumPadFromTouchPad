using System.Diagnostics;
using System.Reflection;

namespace Ogneliss.NumTouch.App.Services;

/// <summary>Сведения о программе: версия, лицензия, адрес исходного кода.</summary>
public static class AboutInfo
{
    /// <summary>Репозиторий с исходным кодом (после переименования GitHub переадресует старый адрес).</summary>
    public const string RepositoryUrl = "https://github.com/oedante/NumPadFromTouchPad";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    public static string Version
    {
        get
        {
            var asm = typeof(AboutInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info.Split('+')[0]; // без хэша коммита
            return asm.GetName().Version?.ToString(3) ?? "?";
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* нет браузера по умолчанию — ничего не делаем */ }
    }
}
