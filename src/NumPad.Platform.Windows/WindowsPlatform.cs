using NumPad.Core.Config;
using NumPad.Core.Diagnostics;
using NumPad.Core.Engine;
using NumPad.Core.Keys;
using static NumPad.Platform.Windows.NativeMethods;

namespace NumPad.Platform.Windows;

/// <summary>Платформенные сервисы Windows: Raw Input + SendInput + RegisterHotKey + WH_MOUSE_LL + SPI тачпада.</summary>
public sealed class WindowsPlatform : IPlatformServices
{
    private readonly InputThread _input = new();
    private readonly PrecisionTouchpad _touchpad;
    private readonly WindowsHotkey _hotkey;
    private readonly WindowsPointerSuppressor _suppressor;
    private readonly SendInputKeyboard _keyboard = new();
    private readonly List<string> _problems = new();

    public WindowsPlatform(ILog log, SessionGuard session)
    {
        _input.OnError = ex => log.Error("Ошибка в потоке ввода", ex);
        if (!_input.Start(out var err)) _problems.Add(err ?? "Не удалось запустить поток ввода.");
        _touchpad = new PrecisionTouchpad(_input, log);
        _hotkey = new WindowsHotkey(_input);
        _suppressor = new WindowsPointerSuppressor(_input);
        Gestures = new TouchpadGestureGuard(session, log);
        _input.Handler = OnMessage;
    }

    public string PlatformName => $"Windows {Environment.OSVersion.Version}";
    public ITouchpadBackend Touchpad => _touchpad;
    public IKeyboardOutput Keyboard => _keyboard;
    public IPointerSuppressor Suppressor => _suppressor;
    public IGlobalHotkey Hotkey => _hotkey;
    public IGestureGuard Gestures { get; }
    public IAutostart Autostart { get; } = new WindowsAutostart();
    public IReadOnlyList<string> Problems => _problems;
    public event Action<SystemEventKind>? SystemEvent;

    /// <summary>Код последней ошибки SendInput (для диагностики).</summary>
    public int LastOutputError => _keyboard.LastError;

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_touchpad.HandleMessage(msg, wParam, lParam)) return true;
        if (_hotkey.HandleMessage(msg, wParam)) return true;
        switch (msg)
        {
            case WM_WTSSESSION_CHANGE:
                if ((int)wParam == WTS_SESSION_LOCK) SystemEvent?.Invoke(SystemEventKind.SessionLocked);
                else if ((int)wParam == WTS_SESSION_UNLOCK) SystemEvent?.Invoke(SystemEventKind.SessionUnlocked);
                return true;
            case WM_POWERBROADCAST:
                int ev = (int)wParam;
                if (ev == PBT_APMSUSPEND) SystemEvent?.Invoke(SystemEventKind.Suspending);
                else if (ev is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND) SystemEvent?.Invoke(SystemEventKind.Resumed);
                return false;
        }
        return false;
    }

    public void ReleaseStuckModifiers()
    {
        foreach (var name in new[] { "LeftShift", "RightShift", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "LeftWin", "RightWin" })
            if (KeyCatalog.TryGet(name, out var k)) _keyboard.KeyUp(k);
    }

    public void Dispose()
    {
        _suppressor.Dispose();
        _hotkey.Dispose();
        _touchpad.Dispose();
        _input.Dispose();
    }
}
