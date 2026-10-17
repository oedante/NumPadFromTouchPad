using System.Runtime.InteropServices;
using NumPad.Core.Hotkeys;
using static NumPad.Platform.Windows.NativeMethods;

namespace NumPad.Platform.Windows;

/// <summary>Глобальная горячая клавиша (RegisterHotKey) в окне потока ввода. Сочетание перехватывается системой.</summary>
internal sealed class WindowsHotkey : IGlobalHotkey
{
    private const int Id = 0x4E50;
    private readonly InputThread _input;
    private bool _registered;

    public WindowsHotkey(InputThread input) => _input = input;

    public Hotkey? Current { get; private set; }
    public string? Limitation => null;
    public event Action? Pressed;

    public bool TryRegister(Hotkey hotkey, out string? error)
    {
        string? err = null;
        bool ok = _input.Invoke(() => RegisterCore(hotkey, out err));
        error = err;
        return ok;
    }

    private bool RegisterCore(Hotkey hotkey, out string? error)
    {
        error = null;
        uint mods = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= MOD_ALT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= MOD_WIN;

        var previous = Current;
        if (_registered) { UnregisterHotKey(_input.Handle, Id); _registered = false; }

        if (RegisterHotKey(_input.Handle, Id, mods, hotkey.Key.WinVk))
        {
            _registered = true;
            Current = hotkey;
            return true;
        }

        int code = Marshal.GetLastWin32Error();
        error = code == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"Сочетание {hotkey} уже занято другой программой или Windows. Выберите другое."
            : $"Не удалось зарегистрировать {hotkey} (код {code}).";

        // Вернуть прежнее сочетание, чтобы режим всегда можно было переключить.
        Current = null;
        if (previous is not null && !previous.Equals(hotkey) && RegisterCore(previous, out _)) Current = previous;
        return false;
    }

    public bool HandleMessage(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY || (int)wParam != Id) return false;
        Pressed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (!_registered) return;
        try { _input.Invoke(() => UnregisterHotKey(_input.Handle, Id), 2000); } catch { /* ignore */ }
        _registered = false;
    }
}
