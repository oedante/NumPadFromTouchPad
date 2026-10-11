using System.Runtime.InteropServices;
using NumPad.Core.Hotkeys;
using static NumPad.App.Platform.NativeMethods;

namespace NumPad.App.Platform;

/// <summary>Hotkey Manager: RegisterHotKey с понятными сообщениями об ошибках.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x4E50; // произвольный ID в диапазоне приложения
    private IntPtr _hwnd;
    private bool _registered;

    public Hotkey? Current { get; private set; }
    public event Action? Pressed;

    /// <summary>Регистрирует сочетание. При ошибке прежнее сочетание остаётся активным.</summary>
    public bool TryRegister(IntPtr hwnd, Hotkey hotkey, out string error)
    {
        error = "";
        uint mods = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= MOD_ALT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= MOD_WIN;

        var previous = Current;
        if (_registered) { UnregisterHotKey(_hwnd, Id); _registered = false; }

        if (RegisterHotKey(hwnd, Id, mods, hotkey.Key.WinVk))
        {
            _hwnd = hwnd;
            _registered = true;
            Current = hotkey;
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        error = err == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"Сочетание {hotkey} уже занято другой программой или Windows. Выберите другое."
            : $"Не удалось зарегистрировать {hotkey} (код {err}).";

        // Вернуть прежнее сочетание, чтобы режим всегда можно было переключить.
        if (previous is not null && !previous.Equals(hotkey)) TryRestore(hwnd, previous);
        return false;
    }

    private void TryRestore(IntPtr hwnd, Hotkey previous)
    {
        if (!TryRegister(hwnd, previous, out _)) Current = null;
    }

    public bool HandleMessage(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY || (int)wParam != Id) return false;
        Pressed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_hwnd, Id);
        _registered = false;
    }
}
