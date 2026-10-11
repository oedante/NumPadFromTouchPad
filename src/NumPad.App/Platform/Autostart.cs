using Microsoft.Win32;

namespace NumPad.App.Platform;

/// <summary>Автозапуск через HKCU\Software\Microsoft\Windows\CurrentVersion\Run (без прав администратора).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NumPadFromTouchPad";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к программе.");
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
