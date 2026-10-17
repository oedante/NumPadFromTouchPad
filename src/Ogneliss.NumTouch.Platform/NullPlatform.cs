using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Hotkeys;
using Ogneliss.NumTouch.Core.Keys;

namespace Ogneliss.NumTouch.Platform;

/// <summary>
/// Платформа-заглушка для неподдерживаемых ОС и самопроверки интерфейса: устройств нет, клавиши не отправляются.
/// Никогда не делает вид, что работает: <see cref="Problems"/> объясняет причину.
/// </summary>
public sealed class NullPlatform : IPlatformServices
{
    private sealed class NoTouchpad : ITouchpadBackend
    {
        public NoTouchpad(string reason) => UnavailableReason = reason;
        public string Mechanism => L.T("none");
        public bool Start(out string? error) { error = UnavailableReason; return false; }
        public IReadOnlyList<TouchpadDeviceInfo> Devices => Array.Empty<TouchpadDeviceInfo>();
        public string? UnavailableReason { get; }
        public void RefreshDevices() { }
        public long ReportsReceived => 0;
        public long ParseErrors => 0;
        public event Action<TouchpadDeviceInfo, TouchFrame>? FrameReceived { add { } remove { } }
        public event Action? DevicesChanged { add { } remove { } }
        public event Action<TouchpadDeviceInfo>? DeviceRemoved { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class NoKeyboard : IKeyboardOutput
    {
        public bool KeyDown(KeyDefinition key) => false;
        public bool KeyUp(KeyDefinition key) => false;
        public bool TypeText(string text) => false;
        public bool IsNumLockOn => true;
    }

    private sealed class NoSuppressor : IPointerSuppressor
    {
        public string Mechanism => L.T("none");
        public bool IsActive => false;
        public long BlockedEvents => 0;
        public bool Activate(SuppressionConfig config, out string? error) { error = L.T("Not available on this platform."); return false; }
        public void Deactivate() { }
        public void ReportTouchState(bool suppressingContactActive, long lastActivityMs) { }
        public void Dispose() { }
    }

    private sealed class NoHotkey : IGlobalHotkey
    {
        public Hotkey? Current { get; private set; }
        public string? Limitation => L.T("Global hotkeys are not available.");
        public bool TryRegister(Hotkey hotkey, out string? error) { error = Limitation; return false; }
        public event Action? Pressed { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class NoGestures : IGestureGuard
    {
        public bool IsSupported => false;
        public bool IsApplied => false;
        public void Apply() { }
        public void Restore() { }
    }

    private sealed class NoAutostart : IAutostart
    {
        public bool IsSupported => false;
        public bool IsEnabled() => false;
        public void Set(bool enabled, string executablePath) { }
        public bool RemoveLegacy() => false;
    }

    public NullPlatform(string reason)
    {
        Touchpad = new NoTouchpad(reason);
        Problems = new[] { reason };
    }

    public string PlatformName => L.T("not supported");
    public ITouchpadBackend Touchpad { get; }
    public IKeyboardOutput Keyboard { get; } = new NoKeyboard();
    public IPointerSuppressor Suppressor { get; } = new NoSuppressor();
    public IGlobalHotkey Hotkey { get; } = new NoHotkey();
    public IGestureGuard Gestures { get; } = new NoGestures();
    public IAutostart Autostart { get; } = new NoAutostart();
    public IReadOnlyList<string> Problems { get; }
    public void ReleaseStuckModifiers() { }
    public event Action<SystemEventKind>? SystemEvent { add { } remove { } }
    public void Dispose() { }
}
