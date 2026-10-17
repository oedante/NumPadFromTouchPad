using System.Diagnostics;
using Ogneliss.NumTouch.Core.Config;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Hotkeys;

namespace Ogneliss.NumTouch.Platform;

/// <summary>Монотонные часы, общие для бэкендов (метки кадров) и контроллера (сторож, хвост подавления).</summary>
public static class MonotonicClock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static long NowMs => Sw.ElapsedMilliseconds;
}

/// <summary>Сведения о тачпаде.</summary>
/// <param name="Id">Идентификатор на время работы процесса (хэндл / путь устройства).</param>
/// <param name="DeviceKey">Устойчивый ключ для профиля калибровки (VID/PID или имя).</param>
public sealed record TouchpadDeviceInfo(
    string Id,
    string DeviceKey,
    string DisplayName,
    int MaxContacts,
    int LogicalWidth,
    int LogicalHeight,
    double? WidthMm,
    double? HeightMm,
    string? Error)
{
    public bool IsUsable => Error is null;
    public double AspectRatio => WidthMm is > 0 && HeightMm is > 0 ? WidthMm.Value / HeightMm.Value
        : LogicalHeight > 0 ? (double)LogicalWidth / LogicalHeight : 1.6;
    public bool AspectIsPhysical => WidthMm is > 0 && HeightMm is > 0;
    public override string ToString() => DisplayName;
}

/// <summary>Источник касаний физического тачпада. События могут приходить из фонового потока.</summary>
public interface ITouchpadBackend : IDisposable
{
    string Mechanism { get; }
    bool Start(out string? error);
    IReadOnlyList<TouchpadDeviceInfo> Devices { get; }
    /// <summary>Почему устройств нет (не PTP, нет прав и т. п.) — для понятного сообщения.</summary>
    string? UnavailableReason { get; }
    void RefreshDevices();
    long ReportsReceived { get; }
    long ParseErrors { get; }
    event Action<TouchpadDeviceInfo, TouchFrame>? FrameReceived;
    event Action? DevicesChanged;
    event Action<TouchpadDeviceInfo>? DeviceRemoved;
}

/// <summary>Параметры подавления указателя в режиме NumPad.</summary>
/// <param name="PointerPassThrough">
/// Для раскладок с «зоной трекпада»: true, если касание, начавшееся в этой точке (координаты тачпада 0..1),
/// должно управлять курсором. Вызывается из потока бэкенда; функция должна быть чистой.
/// </param>
public sealed record SuppressionConfig(PointerSuppression Mode, int TailMs, Func<PointD, bool>? PointerPassThrough);

public interface IPointerSuppressor : IDisposable
{
    string Mechanism { get; }
    bool IsActive { get; }
    long BlockedEvents { get; }
    bool Activate(SuppressionConfig config, out string? error);
    void Deactivate();
    /// <summary>Состояние касаний от движка (UI-поток): есть ли «блокирующий» контакт и время последней активности.</summary>
    void ReportTouchState(bool suppressingContactActive, long lastActivityMs);
}

public interface IGlobalHotkey : IDisposable
{
    Hotkey? Current { get; }
    /// <summary>Особенности платформы (например, Linux: сочетание не перехватывается и доходит до приложений).</summary>
    string? Limitation { get; }
    bool TryRegister(Hotkey hotkey, out string? error);
    /// <summary>Может вызываться из фонового потока.</summary>
    event Action? Pressed;
}

/// <summary>Отключение жестов тачпада средствами ОС (Windows 11 24H2+). На других платформах — недоступно.</summary>
public interface IGestureGuard
{
    bool IsSupported { get; }
    bool IsApplied { get; }
    void Apply();
    void Restore();
}

public interface IAutostart
{
    bool IsSupported { get; }
    bool IsEnabled();
    void Set(bool enabled, string executablePath);
    /// <summary>Удалить запись автозапуска прежнего названия программы. true — запись была.</summary>
    bool RemoveLegacy();
}

public enum SystemEventKind { SessionLocked, SessionUnlocked, Suspending, Resumed }

/// <summary>Набор платформенных сервисов. Реализации: Windows, Linux, macOS, Null (неподдерживаемая ОС).</summary>
public interface IPlatformServices : IDisposable
{
    string PlatformName { get; }
    ITouchpadBackend Touchpad { get; }
    IKeyboardOutput Keyboard { get; }
    IPointerSuppressor Suppressor { get; }
    IGlobalHotkey Hotkey { get; }
    IGestureGuard Gestures { get; }
    IAutostart Autostart { get; }
    /// <summary>Проблемы окружения (нет прав, нет разрешения Accessibility) с подсказкой, как исправить.</summary>
    IReadOnlyList<string> Problems { get; }
    /// <summary>Отпустить модификаторы после аварийного завершения прошлого сеанса.</summary>
    void ReleaseStuckModifiers();
    /// <summary>Блокировка/сон — может вызываться из фонового потока.</summary>
    event Action<SystemEventKind>? SystemEvent;
}
