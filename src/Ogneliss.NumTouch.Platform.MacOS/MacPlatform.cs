using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ogneliss.NumTouch.Core.Config;
using Ogneliss.NumTouch.Core.Diagnostics;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Hotkeys;
using Ogneliss.NumTouch.Core.Keys;
using static Ogneliss.NumTouch.Platform.MacOS.MacNative;

namespace Ogneliss.NumTouch.Platform.MacOS;

/// <summary>Вывод клавиш через CGEventPost (нужно разрешение «Универсальный доступ»).</summary>
internal sealed unsafe class MacKeyboard : IKeyboardOutput
{
    public const long Signature = 0x4E505450; // «NPTP» в kCGEventSourceUserData — отличать свои события в тапе
    private ulong _modifiers;
    private readonly object _sync = new();

    public bool KeyDown(KeyDefinition key) => Post(key, true);
    public bool KeyUp(KeyDefinition key) => Post(key, false);
    public bool IsNumLockOn => true; // на Mac нет Num Lock

    private bool Post(KeyDefinition key, bool down)
    {
        if (!PlatformKeyCodes.TryGetMac(key, out var code))
            return PlatformKeyCodes.MacSubstitutes.TryGetValue(key.Name, out var sub) && PostSubstitute(sub, down);
        lock (_sync)
        {
            ulong flag = key.Name switch
            {
                "LeftShift" or "RightShift" => FlagShift,
                "LeftCtrl" or "RightCtrl" => FlagControl,
                "LeftAlt" or "RightAlt" => FlagAlternate,
                "LeftWin" or "RightWin" => FlagCommand,
                _ => 0,
            };
            if (flag != 0) _modifiers = down ? _modifiers | flag : _modifiers & ~flag;
            return PostRaw(code, down, _modifiers | (key.Group == KeyGroup.Numpad ? FlagNumericPad : 0));
        }
    }

    /// <summary>
    /// Системный аналог клавиши, которой нет на Mac: сочетание нажимается целиком, как на клавиатуре
    /// (⌘ и ⇧ вниз, клавиша, затем в обратном порядке), приложение запускается при нажатии.
    /// </summary>
    private bool PostSubstitute(MacSubstitute sub, bool down)
    {
        if (!sub.IsShortcut) return !down || MacSystemActions.Launch(sub);
        lock (_sync)
        {
            ulong cmd = sub.Command ? FlagCommand : 0, all = cmd | (sub.Shift ? FlagShift : 0);
            bool ok = true;
            if (down)
            {
                if (sub.Command) ok &= PostRaw(KeyCommand, true, _modifiers | cmd);
                if (sub.Shift) ok &= PostRaw(KeyShift, true, _modifiers | all);
                ok &= PostRaw(sub.Code, true, _modifiers | all);
            }
            else
            {
                ok &= PostRaw(sub.Code, false, _modifiers | all);
                if (sub.Shift) ok &= PostRaw(KeyShift, false, _modifiers | cmd);
                if (sub.Command) ok &= PostRaw(KeyCommand, false, _modifiers);
            }
            return ok;
        }
    }

    private const ushort KeyCommand = 0x37, KeyShift = 0x38; // kVK_Command, kVK_Shift

    private static bool PostRaw(ushort code, bool down, ulong flags)
    {
        nint ev = CGEventCreateKeyboardEvent(0, code, down);
        if (ev == 0) return false;
        CGEventSetFlags(ev, flags);
        CGEventSetIntegerValueField(ev, kCGEventSourceUserData, Signature);
        CGEventPost(kCGHIDEventTap, ev);
        CFRelease(ev);
        return true;
    }

    public bool TypeText(string text)
    {
        foreach (bool down in new[] { true, false })
        {
            nint ev = CGEventCreateKeyboardEvent(0, 0, down);
            if (ev == 0) return false;
            fixed (char* p = text) CGEventKeyboardSetUnicodeString(ev, (nuint)text.Length, p);
            CGEventSetIntegerValueField(ev, kCGEventSourceUserData, Signature);
            CGEventPost(kCGHIDEventTap, ev);
            CFRelease(ev);
        }
        return true;
    }
}

/// <summary>Глобальное сочетание через Carbon RegisterEventHotKey (работает в Cocoa-приложениях, вызывать из главного потока).</summary>
internal sealed unsafe class MacHotkey : IGlobalHotkey
{
    private static MacHotkey? _instance;
    private nint _handler, _hotKey;

    public MacHotkey() => _instance = this;

    public Hotkey? Current { get; private set; }
    public string? Limitation => null;
    public event Action? Pressed;

    public bool TryRegister(Hotkey hotkey, out string? error)
    {
        error = null;
        if (!PlatformKeyCodes.TryGetMac(hotkey.Key, out var code)) { error = L.F("The key {0} is not available on Mac.", hotkey.KeyName); return false; }
        try
        {
            if (_handler == 0)
            {
                var spec = new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyPressed };
                nint h;
                int st = InstallEventHandler(GetApplicationEventTarget(), &OnHotKey, (nuint)1, &spec, 0, &h);
                if (st != 0) { error = $"InstallEventHandler: {st}"; return false; }
                _handler = h;
            }
            if (_hotKey != 0) { UnregisterEventHotKey(_hotKey); _hotKey = 0; }
            uint mods = 0;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= controlKey;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= optionKey;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= shiftKey;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= cmdKey;
            nint r;
            int status = RegisterEventHotKey(code, mods, new EventHotKeyID { Signature = 0x4E505450, Id = 1 }, GetApplicationEventTarget(), 0, &r);
            if (status != 0) { error = L.F("The shortcut {0} is taken (code {1}).", hotkey, status); Current = null; return false; }
            _hotKey = r;
            Current = hotkey;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnHotKey(nint callRef, nint ev, nint userData)
    {
        try { _instance?.Pressed?.Invoke(); } catch { /* ignore */ }
        return 0;
    }

    public void Dispose()
    {
        if (_hotKey != 0) UnregisterEventHotKey(_hotKey);
        if (_handler != 0) RemoveEventHandler(_handler);
        _hotKey = _handler = 0;
    }
}

/// <summary>
/// Подавление указателя через CGEventTap (нужно разрешение «Универсальный доступ»). Как и в Windows, тап
/// не различает трекпад и внешнюю мышь — блокирует, пока палец на клавишах, плюс «хвост».
/// </summary>
internal sealed unsafe class MacPointerSuppressor : IPointerSuppressor
{
    private static MacPointerSuppressor? _instance;
    private nint _tap, _source;
    private volatile bool _contact;
    private long _last = long.MinValue / 2;
    private volatile PointerSuppression _mode;
    private volatile int _tail;
    private long _blocked;

    public MacPointerSuppressor() => _instance = this;

    public string Mechanism => "CGEventTap";
    public bool IsActive => _tap != 0;
    public long BlockedEvents => Interlocked.Read(ref _blocked);

    public bool Activate(SuppressionConfig config, out string? error)
    {
        error = null;
        _mode = config.Mode;
        _tail = config.TailMs;
        if (config.Mode == PointerSuppression.None) { Deactivate(); return true; }
        if (_tap != 0) return true;
        ulong mask = 0;
        foreach (int t in new[] { 1, 2, 3, 4, 5, 6, 7, 22, 25, 26, 27 }) mask |= 1UL << t;
        _tap = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionDefault, mask, &OnEvent, 0);
        if (_tap == 0) { error = L.T("CGEventTap is not available: allow Accessibility in the privacy settings."); return false; }
        _source = CFMachPortCreateRunLoopSource(0, _tap, 0);
        CFRunLoopAddSource(CFRunLoopGetMain(), _source, CommonModes);
        CGEventTapEnable(_tap, true);
        return true;
    }

    public void Deactivate()
    {
        if (_tap == 0) return;
        CGEventTapEnable(_tap, false);
        if (_source != 0) { CFRunLoopRemoveSource(CFRunLoopGetMain(), _source, CommonModes); CFRelease(_source); }
        CFMachPortInvalidate(_tap);
        CFRelease(_tap);
        _tap = _source = 0;
    }

    public void ReportTouchState(bool suppressingContactActive, long lastActivityMs)
    {
        _contact = suppressingContactActive;
        Interlocked.Exchange(ref _last, lastActivityMs);
    }

    private bool ShouldBlock() => _mode switch
    {
        PointerSuppression.AllWhileNumPad => true,
        PointerSuppression.WhileTouching => _contact || MonotonicClock.NowMs - Interlocked.Read(ref _last) <= _tail,
        _ => false,
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint OnEvent(nint proxy, uint type, nint ev, nint refcon)
    {
        var self = _instance;
        if (self is null) return ev;
        try
        {
            if (type is kCGEventTapDisabledByTimeout or kCGEventTapDisabledByUserInput)
            {
                if (self._tap != 0) CGEventTapEnable(self._tap, true);
                return ev;
            }
            if (self.ShouldBlock()) { Interlocked.Increment(ref self._blocked); return 0; }
        }
        catch { /* пропустить событие */ }
        return ev;
    }

    public void Dispose() => Deactivate();
}

/// <summary>Автозапуск через LaunchAgent (~/Library/LaunchAgents).</summary>
internal sealed class MacAutostart : IAutostart
{
    private static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "ru.ogneliss.numtouch.plist");
    private static string LegacyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.numpadfromtouchpad.app.plist");

    public bool RemoveLegacy()
    {
        if (!File.Exists(LegacyPath)) return false;
        File.Delete(LegacyPath);
        return true;
    }

    public bool IsSupported => true;
    public bool IsEnabled() => File.Exists(PlistPath);

    public void Set(bool enabled, string executablePath)
    {
        if (!enabled) { if (File.Exists(PlistPath)) File.Delete(PlistPath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        string exe = System.Security.SecurityElement.Escape(executablePath) ?? executablePath;
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>ru.ogneliss.numtouch</string>
              <key>ProgramArguments</key><array><string>{exe}</string><string>--minimized</string></array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }
}

/// <summary>Платформенные сервисы macOS. СТАТУС: прототип, не проверен на устройстве.</summary>
public sealed class MacPlatform : IPlatformServices
{
    private readonly MacTouchpad _touchpad;
    private readonly MacPointerSuppressor _suppressor = new();
    private readonly MacHotkey _hotkey = new();
    private readonly MacKeyboard _keyboard = new();

    public MacPlatform(ILog log) => _touchpad = new MacTouchpad(log);

    public string PlatformName => $"macOS {Environment.OSVersion.Version}";
    public ITouchpadBackend Touchpad => _touchpad;
    public IKeyboardOutput Keyboard => _keyboard;
    public IPointerSuppressor Suppressor => _suppressor;
    public IGlobalHotkey Hotkey => _hotkey;
    public IGestureGuard Gestures { get; } = new NoGestures();
    public IAutostart Autostart { get; } = new MacAutostart();

    public IReadOnlyList<string> Problems
    {
        get
        {
            var list = new List<string> { L.T("macOS support is a prototype that has not been tested on a device.") };
            bool trusted;
            try { trusted = AXIsProcessTrusted(); } catch { trusted = false; }
            if (!trusted)
                list.Add(L.T("No Accessibility permission: System Settings → Privacy & Security → Accessibility → enable Ogneliss NumTouch and restart the program."));
            return list;
        }
    }

    public event Action<SystemEventKind>? SystemEvent { add { } remove { } }

    public void ReleaseStuckModifiers()
    {
        foreach (var name in new[] { "LeftShift", "LeftCtrl", "LeftAlt", "LeftWin" })
            if (KeyCatalog.TryGet(name, out var k)) _keyboard.KeyUp(k);
    }

    public void Dispose()
    {
        _suppressor.Dispose();
        _hotkey.Dispose();
        _touchpad.Dispose();
    }

    private sealed class NoGestures : IGestureGuard
    {
        public bool IsSupported => false;
        public bool IsApplied => false;
        public void Apply() { }
        public void Restore() { }
    }
}
