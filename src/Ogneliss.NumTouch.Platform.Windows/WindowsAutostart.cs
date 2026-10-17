using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ogneliss.NumTouch.Platform.Windows;

/// <summary>
/// Автозапуск через HKCU\Software\Microsoft\Windows\CurrentVersion\Run (без прав администратора).
/// В пакете MSIX (Microsoft Store) запись в Run не действует: там автозапуск — задача StartupTask из манифеста,
/// которую пользователь включает в параметрах Windows.
/// </summary>
internal sealed class WindowsAutostart : IAutostart
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    /// <summary>Программа запущена из пакета MSIX.</summary>
    public static bool IsPackaged
    {
        get
        {
            try { int len = 0; return GetCurrentPackageFullName(ref len, null) != AppModelErrorNoPackage; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    private readonly bool _packaged = IsPackaged;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OgnelissNumTouch";
    private const string LegacyValueName = "NumPadFromTouchPad";

    public bool IsSupported => !_packaged;

    public string? Note => _packaged
        ? L.T("In the Microsoft Store version, startup is turned on in Windows Settings → Apps → Startup.")
        : null;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string;
    }

    public void Set(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\" --minimized");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, false);
    }

    public bool RemoveLegacy()
    {
        if (_packaged) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key?.GetValue(LegacyValueName) is null) return false;
        key.DeleteValue(LegacyValueName, false);
        return true;
    }
}
