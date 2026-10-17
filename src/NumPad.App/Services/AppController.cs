using Avalonia.Threading;
using NumPad.Core.Calibration;
using NumPad.Core.Config;
using NumPad.Core.Diagnostics;
using NumPad.Core.Engine;
using NumPad.Core.Geometry;
using NumPad.Core.Hotkeys;
using NumPad.Core.Layouts;
using NumPad.Platform;

namespace NumPad.App.Services;

/// <summary>
/// Связывает компоненты: устройство → менеджер режимов → движок → вывод клавиш; хоткей; подавление указателя;
/// конфигурация; блокировка/сон/отключение устройства. Не зависит от ОС — всё платформенное в <see cref="IPlatformServices"/>.
/// Все публичные члены вызываются из UI-потока Avalonia; события платформы переводятся в него через Dispatcher.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly DispatcherTimer _tickTimer;
    private bool _disposed;

    public ConfigStore Store { get; }
    public FileLog Log { get; }
    public SessionGuard Session { get; }
    public IPlatformServices Platform { get; }
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
    /// <summary>Кадр от любого тачпада (для калибровки, редактора, диагностики) — уже после обработки движком.</summary>
    public event Action<TouchpadDeviceInfo, TouchFrame>? FrameArrived;
    public event Action<EngineEvent>? EngineEventRaised;
    public event Action<InputMode, string>? ModeChanged;

    public long Now => MonotonicClock.NowMs;

    public AppController(string? rootDir = null, Func<ILog, SessionGuard, IPlatformServices>? platformFactory = null)
    {
        Store = new ConfigStore(rootDir ?? ConfigStore.DefaultRoot);
        Directory.CreateDirectory(Store.RootDir);
        Log = new FileLog(Store.LogsDir);
        Session = new SessionGuard(Store.RootDir);

        var sres = Store.LoadSettings();
        Settings = sres.Value;
        Warnings.AddRange(sres.Warnings);
        Log.Enabled = Settings.DiagnosticsLogging;
        Log.Info($"Запуск NumPad from TouchPad {typeof(AppController).Assembly.GetName().Version}, {Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        foreach (var w in sres.Warnings) Log.Warn(w);

        Platform = (platformFactory ?? PlatformFactory.Create)(Log, Session);
        Log.Info($"Платформа: {Platform.PlatformName}; ввод: {Platform.Touchpad.Mechanism}; подавление: {Platform.Suppressor.Mechanism}");

        ReloadLayouts();
        Engine = new KeyPressEngine(Platform.Keyboard, ActiveLayout, AffineTransform.Identity, Settings.Engine);
        Engine.EventRaised += OnEngineEvent;
        Mode = new ModeManager(Engine, () => Now);
        Mode.ModeChanged += OnModeChanged;

        _tickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Input, (_, _) => OnTick());
        _tickTimer.Stop();

        Platform.Touchpad.FrameReceived += (d, f) => Dispatcher.UIThread.Post(() => OnDeviceFrame(d, f), DispatcherPriority.Input);
        Platform.Touchpad.DevicesChanged += () => Dispatcher.UIThread.Post(OnDevicesChanged);
        Platform.Touchpad.DeviceRemoved += d => Dispatcher.UIThread.Post(() => OnDeviceRemoved(d));
        Platform.Hotkey.Pressed += () => Dispatcher.UIThread.Post(() => ToggleMode("hotkey"));
        Platform.SystemEvent += e => Dispatcher.UIThread.Post(() => OnSystemEvent(e));
    }

    /// <summary>Запуск: восстановление после сбоя, регистрация ввода и хоткея.</summary>
    public void Start()
    {
        if (Session.Begin())
        {
            Log.Warn("Предыдущий сеанс завершился аварийно: отпускаем модификаторы и восстанавливаем параметры тачпада.");
            Warnings.Add("Предыдущий запуск завершился аварийно. Состояние клавиш и тачпада восстановлено.");
            Platform.ReleaseStuckModifiers();
        }
        if (Session.HasPendingRestore) Platform.Gestures.Restore();

        if (!Platform.Touchpad.Start(out var err) && err is not null) InputError = err;
        UpdateInputError();
        foreach (var p in Platform.Problems) Log.Warn(p);

        ApplyHotkey(Settings.Hotkey, out _);
        Reconfigure();

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
            var usable = Platform.Touchpad.Devices.Where(d => d.IsUsable).ToList();
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
        foreach (var w in res.Warnings) { if (!Warnings.Contains(w)) Warnings.Add(w); Log.Warn(w); }
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
        if (Mode.IsNumPad) ActivateSuppression(); // новая раскладка → новая «зона трекпада» для Linux
        StateChanged?.Invoke();
    }

    private void UpdateInputError()
    {
        InputError = Platform.Touchpad.Devices.Any(d => d.IsUsable) ? null
            : Platform.Touchpad.UnavailableReason ?? InputError ?? "Тачпад не найден.";
    }

    private void OnDevicesChanged()
    {
        UpdateInputError();
        Reconfigure();
    }

    private void OnDeviceRemoved(TouchpadDeviceInfo info)
    {
        if (ActiveDevice is null || ActiveDevice.Id == info.Id) Mode.OnDeviceLost();
        UpdateInputError();
        StateChanged?.Invoke();
    }

    // ================================================================= input pipeline

    private void OnDeviceFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        if (_disposed) return;
        // В движок идут кадры только выбранного устройства: у разных тачпадов разная калибровка.
        if (ActiveDevice?.Id == device.Id)
        {
            Mode.OnFrame(frame);
            ReportSuppressionState();
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
            ReportSuppressionState();
            if (!Engine.NeedsTick) _tickTimer.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка в цикле движка — режим NumPad отключён", ex);
            SafeDisable("engine error");
        }
    }

    private void ReportSuppressionState() =>
        Platform.Suppressor.ReportTouchState(Engine.HasSuppressingContact, Engine.LastSuppressActivityMs);

    private void ActivateSuppression()
    {
        var layout = ActiveLayout;
        var transform = ActiveTransform;
        var bounds = layout.KeyBounds();
        Func<PointD, bool>? passThrough = layout.AllowPointerOutsideKeys
            ? p => !(bounds?.Contains(transform.Apply(p)) ?? false)
            : null;
        if (!Platform.Suppressor.Activate(new SuppressionConfig(Settings.PointerSuppression, Settings.SuppressionTailMs, passThrough), out var err) && err is not null)
        {
            Log.Warn(err);
            if (!Warnings.Contains(err)) Warnings.Add(err);
        }
    }

    private void OnEngineEvent(EngineEvent e)
    {
        switch (e.Kind)
        {
            case EngineEventKind.OutputError:
                OutputErrors++;
                // Причина без названия клавиши — в журнал не пишется, что именно нажимал пользователь.
                if (OutputErrors <= 20) Log.Warn("Ошибка генерации клавиатурного события.");
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
            UpdateInputError();
            StateChanged?.Invoke();
            ModeChanged?.Invoke(InputMode.Normal, "no device");
            return;
        }
        Mode.Toggle(reason);
    }

    public void DisableMode(string reason) => Mode.Disable(reason);

    private void OnModeChanged(InputMode mode, string reason)
    {
        if (mode == InputMode.NumPad)
        {
            ActivateSuppression();
            if (Settings.DisableTouchpadGestures) Platform.Gestures.Apply();
        }
        else
        {
            Platform.Suppressor.Deactivate();
            Platform.Gestures.Restore();
        }
        Log.Info($"Режим: {(mode == InputMode.NumPad ? "NumPad" : "обычный")} ({reason})");
        if (reason != "application exit")
        {
            Settings.LastModeWasNumPad = mode == InputMode.NumPad;
            SaveSettingsQuiet();
        }
        ModeChanged?.Invoke(mode, reason);
        StateChanged?.Invoke();
    }

    /// <summary>Безопасное отключение режима при ошибках — никогда не бросает исключений.</summary>
    public void SafeDisable(string reason)
    {
        try { Mode.Disable(reason); } catch (Exception ex) { Log.Error("SafeDisable", ex); }
        try { Engine.ReleaseAll(reason); } catch { /* ignore */ }
        try { Platform.Suppressor.Deactivate(); } catch { /* ignore */ }
        try { Platform.Gestures.Restore(); } catch { /* ignore */ }
    }

    private void OnSystemEvent(SystemEventKind e)
    {
        Log.Info($"Система: {e}");
        switch (e)
        {
            case SystemEventKind.SessionLocked:
            case SystemEventKind.Suspending:
                SafeDisable(e == SystemEventKind.Suspending ? "suspend" : "session lock");
                break;
            case SystemEventKind.Resumed:
                Platform.Touchpad.RefreshDevices();
                break;
        }
    }

    // ================================================================= settings

    public bool ApplyHotkey(string text, out string error)
    {
        if (!Hotkey.TryParse(text, out var hk, out error))
        {
            HotkeyError = error;
            return false;
        }
        if (Platform.Hotkey.TryRegister(hk, out var regError))
        {
            HotkeyError = null;
            Settings.Hotkey = hk.ToString();
            SaveSettingsQuiet();
            Log.Info($"Горячая клавиша: {hk}");
            StateChanged?.Invoke();
            return true;
        }
        error = regError ?? "Не удалось назначить сочетание.";
        HotkeyError = error;
        Log.Warn(error);
        StateChanged?.Invoke();
        return false;
    }

    public string CurrentHotkeyText => Platform.Hotkey.Current?.ToString() ?? Settings.Hotkey;
    public bool HotkeyRegistered => Platform.Hotkey.Current is not null;

    /// <summary>Применить изменённые параметры (кроме горячей клавиши — <see cref="ApplyHotkey"/>).</summary>
    public void ApplySettings()
    {
        Settings.Sanitize();
        Log.Enabled = Settings.DiagnosticsLogging;
        if (Platform.Autostart.IsSupported)
        {
            try
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к программе.");
                if (Platform.Autostart.IsEnabled() != Settings.StartWithWindows) Platform.Autostart.Set(Settings.StartWithWindows, exe);
            }
            catch (Exception ex) { Log.Warn("Не удалось изменить автозапуск", ex); Warnings.Add("Не удалось изменить автозапуск: " + ex.Message); }
        }
        if (Mode.IsNumPad)
        {
            if (Settings.PointerSuppression == PointerSuppression.None) Platform.Suppressor.Deactivate();
            if (Settings.DisableTouchpadGestures) Platform.Gestures.Apply(); else Platform.Gestures.Restore();
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
            $"Платформа: {Platform.PlatformName}",
            $"Режим: {(Mode.IsNumPad ? "NumPad" : "обычный")}",
            $"Ввод: {Platform.Touchpad.Mechanism}; отчётов: {Platform.Touchpad.ReportsReceived}, ошибок разбора: {Platform.Touchpad.ParseErrors}",
            $"Подавление: {Platform.Suppressor.Mechanism}; активно: {Platform.Suppressor.IsActive}, заблокировано: {Platform.Suppressor.BlockedEvents}",
            $"Отключение жестов ОС: {(Platform.Gestures.IsSupported ? "доступно" : "недоступно")}, применено: {Platform.Gestures.IsApplied}",
            $"Ошибок вывода клавиш: {OutputErrors}, восстановлений по сторожу: {StaleRecoveries}",
            $"Горячая клавиша: {CurrentHotkeyText} ({(HotkeyRegistered ? "назначена" : "НЕ назначена")})",
        };
        lines.AddRange(Platform.Problems.Select(p => "Проблема: " + p));
        return string.Join(Environment.NewLine, lines);
    }

    // ================================================================= shutdown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        bool wasNumPad = Mode.IsNumPad;
        try { Mode.Disable("application exit"); } catch { /* ignore */ }
        try { Engine.ReleaseAll("application exit"); } catch { /* ignore */ }
        Settings.LastModeWasNumPad = wasNumPad;
        SaveSettingsQuiet();
        _tickTimer.Stop();
        try { Platform.Suppressor.Deactivate(); } catch { /* ignore */ }
        try { Platform.Gestures.Restore(); } catch { /* ignore */ }
        try { Platform.Dispose(); } catch (Exception ex) { Log.Warn("Освобождение платформы", ex); }
        Log.Info("Штатное завершение.");
        Session.EndClean();
    }

    /// <summary>Из обработчика необработанных исключений: отпустить всё, но не помечать сеанс как штатный.</summary>
    public void EmergencyRelease()
    {
        try { Engine.ReleaseAll("crash"); } catch { /* ignore */ }
        try { Platform.Suppressor.Deactivate(); } catch { /* ignore */ }
        try { Platform.Gestures.Restore(); } catch { /* ignore */ }
    }
}
