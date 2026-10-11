using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;
using NumPad.App.Input;
using NumPad.App.Output;
using NumPad.App.Platform;
using NumPad.Core.Calibration;
using NumPad.Core.Config;
using NumPad.Core.Diagnostics;
using NumPad.Core.Engine;
using NumPad.Core.Hotkeys;
using NumPad.Core.Keys;
using NumPad.Core.Layouts;

namespace NumPad.App.Services;

/// <summary>
/// Связывает компоненты: устройство → менеджер режимов → движок → SendInput; хоткей; подавление указателя;
/// конфигурация; реакция на блокировку/сон/отключение устройства. Живёт всё время работы приложения, UI-поток.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _tickTimer;
    private readonly MessageWindow _window;
    private readonly GlobalHotkey _hotkey = new();
    private readonly PointerSuppressor _suppressor = new();
    private readonly TouchpadGestureGuard _gestures;
    private readonly SendInputKeyboard _keyboard = new();
    private bool _disposed;

    public ConfigStore Store { get; }
    public FileLog Log { get; }
    public SessionGuard Session { get; }
    public PrecisionTouchpad Touchpad { get; }
    public KeyPressEngine Engine { get; }
    public ModeManager Mode { get; }
    public AppSettings Settings { get; private set; }
    public List<Layout> Layouts { get; private set; } = new();
    public List<string> Warnings { get; } = new();
    public string? HotkeyError { get; private set; }
    public string? InputError { get; private set; }
    public long OutputErrors { get; private set; }
    public long StaleRecoveries { get; private set; }

    /// <summary>Изменилось что-то, что показывает UI (режим, устройство, раскладка, ошибки).</summary>
    public event Action? StateChanged;
    /// <summary>Кадр от любого PTP-устройства (для калибровки и диагностики) — уже после обработки движком.</summary>
    public event Action<TouchpadDeviceInfo, TouchFrame>? FrameArrived;
    public event Action<EngineEvent>? EngineEventRaised;
    public event Action<InputMode, string>? ModeChanged;

    public long Now => _clock.ElapsedMilliseconds;

    public AppController(Dispatcher dispatcher, string? rootDir = null)
    {
        _dispatcher = dispatcher;
        Store = new ConfigStore(rootDir ?? ConfigStore.DefaultRoot);
        Directory.CreateDirectory(Store.RootDir);
        Log = new FileLog(Store.LogsDir);
        Session = new SessionGuard(Store.RootDir);
        _gestures = new TouchpadGestureGuard(Session, Log);

        var sres = Store.LoadSettings();
        Settings = sres.Value;
        Warnings.AddRange(sres.Warnings);
        Log.Enabled = Settings.DiagnosticsLogging;
        Log.Info($"Запуск NumPad from TouchPad {typeof(AppController).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version}");
        foreach (var w in sres.Warnings) Log.Warn(w);

        ReloadLayouts();

        Engine = new KeyPressEngine(_keyboard, ActiveLayout, ActiveTransform, Settings.Engine);
        Engine.EventRaised += OnEngineEvent;
        Mode = new ModeManager(Engine, () => Now);
        Mode.ModeChanged += OnModeChanged;

        Touchpad = new PrecisionTouchpad(Log, _clock);
        Touchpad.DeviceFrameReceived += OnDeviceFrame;
        Touchpad.DevicesChanged += OnDevicesChanged;
        Touchpad.DeviceRemoved += OnDeviceRemoved;

        _suppressor.ShouldBlock = ShouldBlockPointer;

        _tickTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        _tickTimer.Tick += (_, _) => OnTick();

        _window = new MessageWindow { Handler = OnWindowMessage };
        _hotkey.Pressed += () => ToggleMode("hotkey");
    }

    /// <summary>Запуск: восстановление после сбоя, регистрация ввода и хоткея, подписка на системные события.</summary>
    public void Start()
    {
        if (Session.Begin())
        {
            Log.Warn("Предыдущий сеанс завершился аварийно: отпускаем модификаторы и восстанавливаем параметры тачпада.");
            Warnings.Add("Предыдущий запуск завершился аварийно. Состояние клавиш и тачпада восстановлено.");
            ReleaseAllModifiers();
        }
        if (Session.HasPendingRestore) _gestures.Restore();

        if (!Touchpad.Register(_window.Handle, out var err)) InputError = err;
        else if (!Touchpad.Devices.Any())
            InputError = TouchpadGestureGuard.LegacyTouchpadPresent
                ? "Тачпад найден, но он не является Precision Touchpad: Windows не передаёт координаты касаний."
                : "Precision Touchpad не найден.";

        ApplyHotkey(Settings.Hotkey, out _);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (Settings.RestoreModeOnStart && Settings.LastModeWasNumPad && InputError is null) Mode.Enable("restore on start");
        StateChanged?.Invoke();
    }

    // ================================================================= layouts / devices

    public Layout ActiveLayout =>
        Layouts.FirstOrDefault(l => string.Equals(l.Id, Settings.ActiveLayoutId, StringComparison.OrdinalIgnoreCase))
        ?? Layouts.FirstOrDefault() ?? BuiltInLayouts.Num20();

    public TouchpadDeviceInfo? ActiveDevice
    {
        get
        {
            var usable = Touchpad?.Devices.Where(d => d.IsUsable).ToList() ?? new List<TouchpadDeviceInfo>();
            return usable.FirstOrDefault(d => string.Equals(d.DeviceKey, Settings.SelectedDeviceKey, StringComparison.OrdinalIgnoreCase))
                   ?? usable.FirstOrDefault();
        }
    }

    public DeviceProfile? ActiveProfile =>
        ActiveDevice is { } d ? Settings.GetOrCreateProfile(d.DeviceKey, d.DisplayName) : null;

    public AffineTransform ActiveTransform
    {
        get
        {
            var t = ActiveProfile?.Calibration.ToTransform() ?? AffineTransform.Identity;
            return t.IsValid ? t : AffineTransform.Identity;
        }
    }

    public void ReloadLayouts()
    {
        var res = Store.LoadLayouts();
        Layouts = res.Value;
        foreach (var w in res.Warnings) { Warnings.Add(w); Log.Warn(w); }
    }

    public void SetActiveLayout(string id)
    {
        Settings.ActiveLayoutId = id;
        SaveSettingsQuiet();
        Reconfigure();
    }

    public void SelectDevice(string deviceKey)
    {
        Settings.SelectedDeviceKey = deviceKey;
        SaveSettingsQuiet();
        Reconfigure();
    }

    /// <summary>Сохранить раскладку (валидация внутри). Бросает InvalidOperationException при ошибках.</summary>
    public void SaveLayout(Layout layout)
    {
        Store.SaveLayout(layout);
        ReloadLayouts();
        Reconfigure();
    }

    public void DeleteLayout(string id)
    {
        Store.DeleteLayout(id);
        ReloadLayouts();
        if (!Layouts.Any(l => string.Equals(l.Id, Settings.ActiveLayoutId, StringComparison.OrdinalIgnoreCase)))
            Settings.ActiveLayoutId = BuiltInLayouts.Num20Id;
        SaveSettingsQuiet();
        Reconfigure();
    }

    public void SaveCalibration(string deviceKey, string displayName, CalibrationData data)
    {
        var p = Settings.GetOrCreateProfile(deviceKey, displayName);
        p.Calibration = data;
        p.LayoutId = data.LayoutId;
        SaveSettingsQuiet();
        Log.Info($"Калибровка сохранена для {deviceKey}" + (data.RmsError is double e ? FormattableString.Invariant($", RMS {e:0.000}") : ""));
        Reconfigure();
    }

    public void ResetCalibration(string deviceKey)
    {
        if (Settings.Devices.TryGetValue(deviceKey, out var p)) p.Calibration = new CalibrationData();
        SaveSettingsQuiet();
        Reconfigure();
    }

    /// <summary>Применить раскладку/калибровку/параметры к движку. Удерживаемые клавиши отпускаются.</summary>
    public void Reconfigure()
    {
        Engine.Configure(ActiveLayout, ActiveTransform, Settings.Engine);
        StateChanged?.Invoke();
    }

    private void OnDevicesChanged()
    {
        if (Touchpad.Devices.Any(d => d.IsUsable)) InputError = null;
        Reconfigure();
    }

    private void OnDeviceRemoved(TouchpadDeviceInfo info)
    {
        if (ActiveDevice is null || ActiveDevice.DeviceKey == info.DeviceKey)
        {
            Mode.OnDeviceLost();
            if (!Touchpad.Devices.Any(d => d.IsUsable)) InputError = "Тачпад отключён. Ввод возобновится после подключения.";
        }
        StateChanged?.Invoke();
    }

    // ================================================================= input pipeline

    private bool OnWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (Touchpad.HandleMessage(msg, wParam, lParam)) return true;
        if (_hotkey.HandleMessage(msg, wParam)) return true;
        return false;
    }

    private void OnDeviceFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        // В движок идут кадры только выбранного устройства: у разных тачпадов разная калибровка.
        if (ActiveDevice?.Handle == device.Handle)
        {
            Mode.OnFrame(frame);
            if (Engine.NeedsTick && !_tickTimer.IsEnabled) _tickTimer.Start();
        }
        try { FrameArrived?.Invoke(device, frame); }
        catch (Exception ex) { Log.Error("Ошибка обработчика кадра в интерфейсе", ex); }
    }

    private void OnTick()
    {
        try
        {
            Mode.Tick();
            if (!Engine.NeedsTick) _tickTimer.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка в цикле движка — режим NumPad отключён", ex);
            SafeDisable("engine error");
        }
    }

    private bool ShouldBlockPointer()
    {
        if (!Mode.IsNumPad) return false;
        return Settings.PointerSuppression switch
        {
            PointerSuppression.AllWhileNumPad => true,
            PointerSuppression.WhileTouching => Engine.WantsPointerSuppression(Now, Settings.SuppressionTailMs),
            _ => false,
        };
    }

    private void OnEngineEvent(EngineEvent e)
    {
        switch (e.Kind)
        {
            case EngineEventKind.OutputError:
                OutputErrors++;
                // Причина без названия клавиши — в журнал не пишется, что именно нажимал пользователь.
                if (OutputErrors <= 20) Log.Warn($"Ошибка генерации клавиатурного события (код {_keyboard.LastError}).");
                break;
            case EngineEventKind.StaleContactRecovered:
                StaleRecoveries++;
                if (StaleRecoveries <= 20) Log.Warn("Устройство не прислало отпускание контакта — контакт освобождён сторожем.");
                break;
        }
        EngineEventRaised?.Invoke(e);
    }

    // ================================================================= mode

    public void ToggleMode(string reason)
    {
        if (!Mode.IsNumPad && ActiveDevice is null)
        {
            InputError ??= "Precision Touchpad не найден.";
            StateChanged?.Invoke();
            ModeChanged?.Invoke(InputMode.Normal, "no device");
            return;
        }
        Mode.Toggle(reason);
    }

    public void EnableMode(string reason)
    {
        if (ActiveDevice is null) { ToggleMode(reason); return; }
        Mode.Enable(reason);
    }

    public void DisableMode(string reason) => Mode.Disable(reason);

    private void OnModeChanged(InputMode mode, string reason)
    {
        if (mode == InputMode.NumPad)
        {
            if (Settings.PointerSuppression != PointerSuppression.None && !_suppressor.Install(out var err))
                Log.Warn(err);
            if (Settings.DisableTouchpadGestures) _gestures.Apply();
        }
        else
        {
            _suppressor.Uninstall();
            _gestures.Restore();
        }
        Log.Info($"Режим: {(mode == InputMode.NumPad ? "NumPad" : "обычный")} ({reason})");
        Settings.LastModeWasNumPad = mode == InputMode.NumPad;
        SaveSettingsQuiet();
        ModeChanged?.Invoke(mode, reason);
        StateChanged?.Invoke();
    }

    /// <summary>Безопасное отключение режима при ошибках — никогда не бросает исключений.</summary>
    public void SafeDisable(string reason)
    {
        try { Mode.Disable(reason); } catch (Exception ex) { Log.Error("SafeDisable", ex); }
        try { Engine.ReleaseAll(reason); } catch { /* ignore */ }
        try { _suppressor.Uninstall(); } catch { /* ignore */ }
        try { _gestures.Restore(); } catch { /* ignore */ }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff)
            _dispatcher.InvokeAsync(() => { Log.Info($"Сеанс: {e.Reason}"); SafeDisable("session " + e.Reason); });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            _dispatcher.InvokeAsync(() => { Log.Info("Переход в сон"); SafeDisable("suspend"); });
        else if (e.Mode == PowerModes.Resume)
            _dispatcher.InvokeAsync(() =>
            {
                Log.Info("Выход из сна: повторная инициализация устройств");
                Touchpad.RefreshDevices();
                if (!Touchpad.IsRegistered && Touchpad.Register(_window.Handle, out _)) InputError = null;
                StateChanged?.Invoke();
            });
    }

    /// <summary>Аварийно: отпустить модификаторы, которые могли остаться «нажатыми» после сбоя.</summary>
    private void ReleaseAllModifiers()
    {
        foreach (var name in new[] { "LeftShift", "RightShift", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "LeftWin", "RightWin" })
            if (KeyCatalog.TryGet(name, out var k)) _keyboard.KeyUp(k);
    }

    // ================================================================= settings

    public bool ApplyHotkey(string text, out string error)
    {
        if (!Hotkey.TryParse(text, out var hk, out error))
        {
            HotkeyError = error;
            return false;
        }
        if (_hotkey.TryRegister(_window.Handle, hk, out error))
        {
            HotkeyError = null;
            Settings.Hotkey = hk.ToString();
            SaveSettingsQuiet();
            Log.Info($"Горячая клавиша: {hk}");
            StateChanged?.Invoke();
            return true;
        }
        HotkeyError = error;
        Log.Warn(error);
        StateChanged?.Invoke();
        return false;
    }

    public string CurrentHotkeyText => _hotkey.Current?.ToString() ?? Settings.Hotkey;
    public bool HotkeyRegistered => _hotkey.Current is not null;

    /// <summary>Применить изменённые параметры (кроме горячей клавиши — <see cref="ApplyHotkey"/>).</summary>
    public void ApplySettings()
    {
        Settings.Sanitize();
        Log.Enabled = Settings.DiagnosticsLogging;
        try { Autostart.Set(Settings.StartWithWindows); }
        catch (Exception ex) { Log.Warn("Не удалось изменить автозапуск", ex); Warnings.Add("Не удалось изменить автозапуск: " + ex.Message); }

        if (Mode.IsNumPad)
        {
            if (Settings.PointerSuppression == PointerSuppression.None) _suppressor.Uninstall();
            else _suppressor.Install(out _);
            if (Settings.DisableTouchpadGestures) _gestures.Apply(); else _gestures.Restore();
        }
        SaveSettingsQuiet();
        Reconfigure();
    }

    public void ResetSettings()
    {
        var devices = Settings.Devices;
        Settings = new AppSettings { Devices = devices }; // калибровки устройств сохраняем
        if (!ApplyHotkey(Hotkey.Default.ToString(), out _)) Settings.Hotkey = CurrentHotkeyText;
        ApplySettings();
    }

    public void SaveSettingsQuiet()
    {
        try { Store.SaveSettings(Settings); }
        catch (Exception ex) { Log.Error("Не удалось сохранить настройки", ex); }
    }

    public string DiagnosticsSummary()
    {
        var lines = new List<string>
        {
            $"Режим: {(Mode.IsNumPad ? "NumPad" : "обычный")}",
            $"Raw Input зарегистрирован: {Touchpad.IsRegistered}",
            $"Отчётов получено: {Touchpad.ReportsReceived}, ошибок разбора: {Touchpad.ParseErrors}",
            $"Подавление указателя: {Settings.PointerSuppression}, хук установлен: {_suppressor.IsInstalled}, заблокировано событий: {_suppressor.BlockedEvents}",
            $"Управление жестами (Win11 24H2+): {(TouchpadGestureGuard.IsSupported ? "доступно" : "недоступно")}, применено: {_gestures.IsApplied}",
            $"Ошибок вывода клавиш: {OutputErrors}, восстановлений по сторожу: {StaleRecoveries}",
            $"Горячая клавиша: {CurrentHotkeyText} ({(HotkeyRegistered ? "зарегистрирована" : "НЕ зарегистрирована")})",
        };
        return string.Join(Environment.NewLine, lines);
    }

    public long SuppressedPointerEvents => _suppressor.BlockedEvents;

    // ================================================================= shutdown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        bool wasNumPad = Mode.IsNumPad;
        try { Mode.Disable("application exit"); } catch { /* ignore */ }
        Settings.LastModeWasNumPad = wasNumPad;
        SaveSettingsQuiet();
        try { Engine.ReleaseAll("application exit"); } catch { /* ignore */ }
        _tickTimer.Stop();
        _suppressor.Dispose();
        _gestures.Restore();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _hotkey.Dispose();
        Touchpad.Dispose();
        _window.Dispose();
        Log.Info("Штатное завершение.");
        Session.EndClean();
    }

    /// <summary>Вызывается из обработчика необработанных исключений: отпустить всё, но не помечать сеанс как штатный.</summary>
    public void EmergencyRelease()
    {
        try { Engine.ReleaseAll("crash"); } catch { /* ignore */ }
        try { _suppressor.Uninstall(); } catch { /* ignore */ }
        try { _gestures.Restore(); } catch { /* ignore */ }
    }
}
