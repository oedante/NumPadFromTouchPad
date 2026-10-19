using Ogneliss.NumTouch.Core.Diagnostics;
using Ogneliss.NumTouch.Core.Hotkeys;
using Ogneliss.NumTouch.Core.Keys;
using static Ogneliss.NumTouch.Platform.Linux.LibC;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>
/// Глобальная горячая клавиша для Linux через чтение клавиатур evdev. Работает одинаково в X11 и Wayland
/// (у Wayland нет общего API глобальных сочетаний). Ограничение: сочетание не перехватывается — активная
/// программа тоже получает нажатие, поэтому по умолчанию выбрано редко используемое Ctrl+Alt+N.
/// Также читает состояние Num Lock (EVIOCGLED).
/// </summary>
internal sealed class EvdevHotkey : IGlobalHotkey
{
    private static readonly ushort[] CtrlCodes = { 29, 97 }, ShiftCodes = { 42, 54 }, AltCodes = { 56, 100 }, MetaCodes = { 125, 126 };

    private sealed class Kbd
    {
        public required EvdevDevice Dev;
        public Thread? Thread;
        public volatile bool Stop;
    }

    private readonly ILog _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, Kbd> _keyboards = new();
    private readonly HashSet<ushort> _down = new();
    private Timer? _rescan;
    private volatile Hotkey? _current;
    private ushort _mainCode;

    public EvdevHotkey(ILog log) => _log = log;

    public Hotkey? Current => _current;
    public string? Limitation => L.T("On Linux the shortcut is not intercepted: the active program receives the keypress too.");
    private volatile bool _accessDenied;

    /// <summary>Нет доступа ни к одной клавиатуре. Текст строится при каждом обращении — на текущем языке.</summary>
    public string? AccessError => _accessDenied
        ? L.T("No access to keyboards in /dev/input — add your user to the input group and log in again.")
        : null;
    public event Action? Pressed;

    public bool TryRegister(Hotkey hotkey, out string? error)
    {
        error = null;
        if (!PlatformKeyCodes.TryGetLinux(hotkey.Key, out var code))
        {
            error = L.F("The key {0} is not available on Linux.", hotkey.KeyName);
            return false;
        }
        Start();
        lock (_sync)
        {
            if (_keyboards.Count == 0)
            {
                error = AccessError ?? L.T("No keyboard found.");
                return false;
            }
            _mainCode = code;
            _current = hotkey;
        }
        return true;
    }

    private void Start()
    {
        if (_rescan is not null) return;
        Rescan();
        _rescan = new Timer(_ => { try { Rescan(); } catch { /* ignore */ } }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void Rescan()
    {
        bool denied = false;
        foreach (var path in EvdevDevice.EnumerateEventNodes())
        {
            lock (_sync) if (_keyboards.ContainsKey(path)) continue;
            var dev = EvdevDevice.Open(path, out int errno);
            if (dev is null) { denied |= errno == EACCES; continue; }
            if (!dev.IsKeyboard || dev.Name.StartsWith("Ogneliss NumTouch", StringComparison.Ordinal)) { dev.Dispose(); continue; }
            var k = new Kbd { Dev = dev };
            lock (_sync) _keyboards[path] = k;
            k.Thread = new Thread(() => ReadLoop(k)) { IsBackground = true, Name = "evdev kbd " + path };
            k.Thread.Start();
        }
        lock (_sync) _accessDenied = _keyboards.Count == 0 && denied;
    }

    private void ReadLoop(Kbd k)
    {
        Span<InputEvent> buf = stackalloc InputEvent[32];
        try
        {
            while (!k.Stop)
            {
                if (!k.Dev.WaitReadable(500)) continue;
                int n = k.Dev.Read(buf);
                for (int i = 0; i < n; i++)
                {
                    var e = buf[i];
                    if (e.Type != EV_KEY) continue;
                    bool fire = false;
                    lock (_sync)
                    {
                        if (e.Value == 0) { _down.Remove(e.Code); continue; }
                        if (e.Value == 2) continue; // автоповтор
                        _down.Add(e.Code);
                        fire = _current is { } hk && e.Code == _mainCode && ModifiersMatch(hk.Modifiers);
                    }
                    if (fire) Pressed?.Invoke();
                }
            }
        }
        catch (IOException)
        {
            // клавиатура отключена — удалим и подхватим заново при пересмотре
        }
        catch (Exception ex) { _log.Warn("Keyboard read", ex); }
        lock (_sync) _keyboards.Remove(k.Dev.Path);
        k.Dev.Dispose();
    }

    private bool ModifiersMatch(HotkeyModifiers m) =>
        Has(CtrlCodes) == m.HasFlag(HotkeyModifiers.Ctrl) &&
        Has(ShiftCodes) == m.HasFlag(HotkeyModifiers.Shift) &&
        Has(AltCodes) == m.HasFlag(HotkeyModifiers.Alt) &&
        Has(MetaCodes) == m.HasFlag(HotkeyModifiers.Win);

    private bool Has(ushort[] codes) => codes.Any(_down.Contains);

    /// <summary>Num Lock по светодиоду первой найденной клавиатуры (true, если узнать нельзя).</summary>
    public bool ReadNumLock()
    {
        Start();
        Kbd? k;
        lock (_sync) k = _keyboards.Values.FirstOrDefault();
        return k is null || TestBit(k.Dev.GetLeds(), LED_NUML);
    }

    public void Dispose()
    {
        _rescan?.Dispose();
        List<Kbd> list;
        lock (_sync) list = _keyboards.Values.ToList();
        foreach (var k in list) k.Stop = true;
        foreach (var k in list) k.Thread?.Join(1000);
    }
}
