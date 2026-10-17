using NumPad.Core.Diagnostics;
using NumPad.Core.Engine;
using NumPad.Core.Keys;

namespace NumPad.Platform.Linux;

/// <summary>Автозапуск по стандарту XDG: ~/.config/autostart/numpad-from-touchpad.desktop.</summary>
internal sealed class LinuxAutostart : IAutostart
{
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "numpad-from-touchpad.desktop");

    public bool IsSupported => true;
    public bool IsEnabled() => File.Exists(FilePath);

    public void Set(bool enabled, string executablePath)
    {
        if (!enabled) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath,
            "[Desktop Entry]\nType=Application\nName=NumPad from TouchPad\n" +
            $"Exec=\"{executablePath}\" --minimized\nX-GNOME-Autostart-enabled=true\nTerminal=false\n");
    }
}

/// <summary>Платформенные сервисы Linux: evdev (тачпад, клавиатуры) + uinput (клавиши, курсор) + EVIOCGRAB.</summary>
public sealed class LinuxPlatform : IPlatformServices
{
    private readonly LinuxTouchpad _touchpad;
    private readonly LinuxPointerSuppressor _suppressor;
    private readonly EvdevHotkey _hotkey;
    private readonly UinputKeyboard _keyboard;
    private readonly List<string> _problems = new();
    private readonly ILog _log;

    public LinuxPlatform(ILog log)
    {
        _log = log;
        if (!Environment.Is64BitProcess) _problems.Add("Поддерживаются только 64-битные системы (структура input_event).");
        _hotkey = new EvdevHotkey(log);
        _keyboard = new UinputKeyboard(_hotkey.ReadNumLock);
        if (_keyboard.Error is not null) _problems.Add(_keyboard.Error);
        _touchpad = new LinuxTouchpad(log, () =>
        {
            var m = UinputDevice.CreateMouse(out var err);
            if (m is null) _log.Warn("Виртуальная мышь: " + err);
            return m;
        });
        _suppressor = new LinuxPointerSuppressor(_touchpad);
    }

    public string PlatformName => $"Linux {Environment.OSVersion.Version}" +
        (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is { Length: > 0 } s ? $" ({s})" : "");
    public ITouchpadBackend Touchpad => _touchpad;
    public IKeyboardOutput Keyboard => _keyboard;
    public IPointerSuppressor Suppressor => _suppressor;
    public IGlobalHotkey Hotkey => _hotkey;
    public IGestureGuard Gestures { get; } = new NoGestureGuard();
    public IAutostart Autostart { get; } = new LinuxAutostart();
    public IReadOnlyList<string> Problems
    {
        get
        {
            var list = new List<string>(_problems);
            if (_hotkey.AccessError is { } e && !list.Contains(e)) list.Add(e);
            return list;
        }
    }

    /// <summary>Блокировку и сон отдельно не отслеживаем: при засыпании чтение тачпада останавливается,
    /// а сторож движка отпускает удерживаемые клавиши.</summary>
    public event Action<SystemEventKind>? SystemEvent { add { } remove { } }

    public void ReleaseStuckModifiers()
    {
        // Виртуальная клавиатура создаётся заново при запуске — у нового устройства нет нажатых клавиш,
        // а при уничтожении старого (аварийное завершение) ядро само отпускает все его клавиши.
    }

    public void Dispose()
    {
        _suppressor.Dispose();
        _touchpad.Dispose();
        _hotkey.Dispose();
        _keyboard.Dispose();
    }

    private sealed class NoGestureGuard : IGestureGuard
    {
        public bool IsSupported => false;
        public bool IsApplied => false;
        public void Apply() { }
        public void Restore() { }
    }
}
