using Microsoft.Win32;

namespace Ogneliss.NumTouch.Platform.Windows;

/// <summary>Автозапуск через HKCU\Software\Microsoft\Windows\CurrentVersion\Run (без прав администратора).</summary>
internal sealed class WindowsAutostart : IAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OgnelissNumTouch";
    private const string LegacyValueName = "NumPadFromTouchPad";

    public bool IsSupported => true;

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
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key?.GetValue(LegacyValueName) is null) return false;
        key.DeleteValue(LegacyValueName, false);
        return true;
    }
}
