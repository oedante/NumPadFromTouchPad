using Avalonia.Threading;
using Ogneliss.NumTouch.Core.Calibration;
using Ogneliss.NumTouch.Core.Config;
using Ogneliss.NumTouch.Core.Diagnostics;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Hotkeys;
using Ogneliss.NumTouch.Core.Layouts;
using Ogneliss.NumTouch.Platform;

namespace Ogneliss.NumTouch.App.Services;

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
        // Переименование проекта: настройки прежней версии переносятся один раз (только для каталога по умолчанию).
        int migrated = 0;
        string? migrationError = null;
        if (rootDir is null)
        {
            try { migrated = ConfigStore.MigrateLegacy(ConfigStore.LegacyRoot, ConfigStore.DefaultRoot); }
            catch (Exception ex) { migrationError = ex.Message; }
        }
        Store = new ConfigStore(rootDir ?? ConfigStore.DefaultRoot);
        Directory.CreateDirectory(Store.RootDir);
        Log = new FileLog(Store.LogsDir);
        Session = new SessionGuard(Store.RootDir);

        var sres = Store.LoadSettings();
        Settings = sres.Value;
        Warnings.AddRange(sres.Warnings);
        L.Apply(Settings.Language);
        Log.Enabled = Settings.DiagnosticsLogging;
        Log.Info($"Starting Ogneliss NumTouch {typeof(AppController).Assembly.GetName().Version}, {Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        foreach (var w in sres.Warnings) Log.Warn(w);
        if (migrated > 0)
        {
            Log.Info($"Migrated {migrated} file(s) from {ConfigStore.LegacyRoot}");
            Warnings.Add(L.T("Settings and layouts of the previous version (NumPad from TouchPad) were transferred. The old folder was left untouched."));
        }
        if (migrationError is not null)
        {
            Log.Warn("Settings migration failed: " + migrationError);
            Warnings.Add(L.F("Could not transfer the settings of the previous version: {0}", migrationError));
        }

        Platform = (platformFactory ?? PlatformFactory.Create)(Log, Session);
        Log.Info($"Platform: {Platform.PlatformName}; input: {Platform.Touchpad.Mechanism}; suppression: {Platform.Suppressor.Mechanism}");

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
            Log.Warn("The previous session ended abnormally: releasing modifiers and restoring touchpad settings.");
            Warnings.Add(L.T("The previous run ended abnormally. The key and touchpad state was restored."));
            Platform.ReleaseStuckModifiers();
        }
        if (Session.HasPendingRestore) Platform.Gestures.Restore();

        if (!Platform.Touchpad.Start(out var err) && err is not null) InputError = err;
        UpdateInputError();
        foreach (var p in Platform.Problems) Log.Warn(p);

        ApplyHotkey(Settings.Hotkey, out _);
        MigrateAutostart();
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

    /// <summary>Язык сменился: встроенные раскладки и сообщения об устройстве — заново, на новом языке.</summary>
    public void OnLanguageChanged()
    {
        ReloadLayouts();
        InputError = null;
        try { Platform.Touchpad.RefreshDevices(); } catch (Exception ex) { Log.Warn("Device refresh", ex); }
        UpdateInputError();
        // Сообщение об ошибке горячей клавиши тоже на новом языке: повторная регистрация сохранённого сочетания.
        if (HotkeyError is not null) ApplyHotkey(Settings.Hotkey, out _);
        StateChanged?.Invoke();
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
        Log.Info($"Calibration saved for {deviceKey}" + (data.RmsError is double e ? FormattableString.Invariant($", RMS {e:0.000}") : ""));
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
            : (Platform.Touchpad.UnavailableReason is { } reason ? L.Retranslate(reason) : null) ?? InputError ?? L.T("Touchpad not found.");
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
        catch (Exception ex) { Log.Error("UI frame handler error", ex); }
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
            Log.Error("Engine loop error — NumPad mode turned off", ex);
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
                if (OutputErrors <= 20) Log.Warn("Failed to generate a keyboard event.");
                break;
            case EngineEventKind.StaleContactRecovered:
                StaleRecoveries++;
                if (StaleRecoveries <= 20) Log.Warn("The device did not report a contact release — the watchdog released it.");
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
        Log.Info($"Mode: {(mode == InputMode.NumPad ? "NumPad" : "normal")} ({reason})");
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
        Log.Info($"System: {e}");
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
            Log.Info($"Hotkey: {hk}");
            StateChanged?.Invoke();
            return true;
        }
        error = regError ?? L.T("Could not assign the shortcut.");
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
        if (Settings.Language != L.Selected)
        {
            L.Apply(Settings.Language);
            ReloadLayouts(); // названия встроенных раскладок — на новом языке
        }
        if (Platform.Autostart.IsSupported)
        {
            try
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException(L.T("Could not determine the program path."));
                if (Platform.Autostart.IsEnabled() != Settings.StartWithWindows) Platform.Autostart.Set(Settings.StartWithWindows, exe);
            }
            catch (Exception ex) { Log.Warn("Could not change autostart", ex); Warnings.Add(L.F("Could not change autostart: {0}", ex.Message)); }
        }
        if (Mode.IsNumPad)
        {
            if (Settings.PointerSuppression == PointerSuppression.None) Platform.Suppressor.Deactivate();
            if (Settings.DisableTouchpadGestures) Platform.Gestures.Apply(); else Platform.Gestures.Restore();
        }
        SaveSettingsQuiet();
        Reconfigure();
    }

    /// <summary>
    /// Записи автозапуска прежней версии (NumPad from TouchPad) удаляются; если автозапуск был включён,
    /// создаётся новая запись на текущий исполняемый файл.
    /// </summary>
    private void MigrateAutostart()
    {
        if (!Platform.Autostart.IsSupported) return;
        try
        {
            if (!Platform.Autostart.RemoveLegacy()) return;
            Log.Info("Legacy autostart entry removed");
            if (Settings.StartWithWindows && Environment.ProcessPath is { } exe) Platform.Autostart.Set(true, exe);
        }
        catch (Exception ex) { Log.Warn("Autostart migration", ex); }
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
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }

    /// <summary>Какой шрифт на самом деле используется интерфейсом (для проверки системного шрифта на каждой ОС).</summary>
    private static string ResolvedFontName()
    {
        try
        {
            var fm = Avalonia.Media.FontManager.Current;
            return fm.TryGetGlyphTypeface(new Avalonia.Media.Typeface(fm.DefaultFontFamily), out var gt)
                ? $"{gt.FamilyName} ({fm.DefaultFontFamily.Name})" : fm.DefaultFontFamily.Name;
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    public string DiagnosticsSummary()
    {
        var lines = new List<string>
        {
            L.F("Platform: {0}", Platform.PlatformName),
            L.F("Mode: {0}", Mode.IsNumPad ? "NumPad" : L.T("normal")),
            L.F("Input: {0}; reports: {1}, parse errors: {2}", Platform.Touchpad.Mechanism, Platform.Touchpad.ReportsReceived, Platform.Touchpad.ParseErrors),
            L.F("Suppression: {0}; active: {1}, blocked: {2}", Platform.Suppressor.Mechanism, Platform.Suppressor.IsActive ? L.T("yes") : L.T("no"), Platform.Suppressor.BlockedEvents),
            L.F("OS gesture disabling: {0}, applied: {1}", Platform.Gestures.IsSupported ? L.T("available") : L.T("not available"), Platform.Gestures.IsApplied ? L.T("yes") : L.T("no")),
            L.F("Key output errors: {0}, watchdog recoveries: {1}", OutputErrors, StaleRecoveries),
            L.F("Hotkey: {0} ({1})", CurrentHotkeyText, HotkeyRegistered ? L.T("assigned") : L.T("NOT assigned")),
            L.F("Language: {0}", L.Code),
            L.F("Font: {0}", ResolvedFontName()),
            L.F("Version: {0}", AboutInfo.Version),
        };
        lines.AddRange(Platform.Problems.Select(p => L.F("Problem: {0}", L.Retranslate(p))));
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
        try { Platform.Dispose(); } catch (Exception ex) { Log.Warn("Platform dispose", ex); }
        Log.Info("Clean shutdown.");
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
