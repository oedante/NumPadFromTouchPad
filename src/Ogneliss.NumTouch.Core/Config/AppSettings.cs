using System.Text.Json.Serialization;
using Ogneliss.NumTouch.Core.Calibration;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Layouts;

namespace Ogneliss.NumTouch.Core.Config;

public enum PointerSuppression
{
    /// <summary>Не блокировать указатель (только генерация клавиш).</summary>
    None,
    /// <summary>Блокировать события мыши, пока палец на зоне клавиш тачпада, и короткое время после отрыва (по умолчанию).</summary>
    WhileTouching,
    /// <summary>Блокировать все события мыши, пока включён режим NumPad (внешняя мышь тоже не работает).</summary>
    AllWhileNumPad,
}

/// <summary>Сохранённая калибровка устройства.</summary>
public sealed class CalibrationData
{
    public double A { get; set; } = 1;
    public double B { get; set; }
    public double C { get; set; }
    public double D { get; set; }
    public double E { get; set; } = 1;
    public double F { get; set; }
    public double? RmsError { get; set; }
    public DateTime? CalibratedAtUtc { get; set; }
    /// <summary>Раскладка, по которой выполнена калибровка (её соотношение сторон).</summary>
    public string? LayoutId { get; set; }
    public List<double[]> Points { get; set; } = new();

    public AffineTransform ToTransform() => new(A, B, C, D, E, F);

    public static CalibrationData FromTransform(AffineTransform t, double? rms = null, string? layoutId = null,
        IEnumerable<CalibrationPoint>? points = null) => new()
    {
        A = t.A, B = t.B, C = t.C, D = t.D, E = t.E, F = t.F, RmsError = rms, LayoutId = layoutId,
        CalibratedAtUtc = DateTime.UtcNow,
        Points = points?.Select(p => new[] { p.Device.X, p.Device.Y, p.Target.X, p.Target.Y }).ToList() ?? new(),
    };

    [JsonIgnore]
    public bool IsIdentity => ToTransform() == AffineTransform.Identity;
}

/// <summary>Профиль конкретного тачпада: калибровка и выбранная раскладка.</summary>
public sealed class DeviceProfile
{
    /// <summary>Устойчивый ключ устройства: «VID_xxxx&PID_xxxx» (или имя устройства, если VID/PID недоступны).</summary>
    public string DeviceKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? LayoutId { get; set; }
    public CalibrationData Calibration { get; set; } = new();
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Hotkey { get; set; } = Hotkeys.Hotkey.Default.ToString();
    public string ActiveLayoutId { get; set; } = BuiltInLayouts.Num20Id;
    public string? SelectedDeviceKey { get; set; }
    public Dictionary<string, DeviceProfile> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public EngineOptions Engine { get; set; } = new();

    public PointerSuppression PointerSuppression { get; set; } = PointerSuppression.WhileTouching;
    public int SuppressionTailMs { get; set; } = 350;
    /// <summary>Отключать тапы и жесты тачпада на время режима NumPad (Windows 11 24H2+, с восстановлением).</summary>
    public bool DisableTouchpadGestures { get; set; } = true;

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>Экранное уведомление при включении/выключении режима NumPad.</summary>
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Язык интерфейса (Auto — по языку системы).</summary>
    public UiLanguage Language { get; set; } = UiLanguage.Auto;
    /// <summary>Вернуть режим NumPad, если он был включён при выходе.</summary>
    public bool RestoreModeOnStart { get; set; }
    public bool LastModeWasNumPad { get; set; }
    public bool DiagnosticsLogging { get; set; } = true;

    public DeviceProfile GetOrCreateProfile(string deviceKey, string displayName)
    {
        if (!Devices.TryGetValue(deviceKey, out var p))
        {
            p = new DeviceProfile { DeviceKey = deviceKey, DisplayName = displayName };
            Devices[deviceKey] = p;
        }
        else if (!string.IsNullOrEmpty(displayName)) p.DisplayName = displayName;
        return p;
    }

    /// <summary>Исправляет недопустимые значения после загрузки (повреждённый или старый файл).</summary>
    public AppSettings Sanitize()
    {
        if (!Hotkeys.Hotkey.TryParse(Hotkey, out _, out _)) Hotkey = Hotkeys.Hotkey.Default.ToString();
        if (string.IsNullOrWhiteSpace(ActiveLayoutId)) ActiveLayoutId = BuiltInLayouts.Num20Id;
        Engine = (Engine ?? new EngineOptions()).Sanitized();
        if (!Enum.IsDefined(PointerSuppression)) PointerSuppression = PointerSuppression.WhileTouching;
        if (!Enum.IsDefined(Language)) Language = UiLanguage.Auto;
        SuppressionTailMs = Math.Clamp(SuppressionTailMs, 0, 2000);
        var fixedDevices = new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Devices ?? new())
        {
            if (kv.Value is null || string.IsNullOrWhiteSpace(kv.Key)) continue;
            kv.Value.DeviceKey = kv.Key;
            kv.Value.Calibration ??= new CalibrationData();
            if (!kv.Value.Calibration.ToTransform().IsValid) kv.Value.Calibration = new CalibrationData();
            fixedDevices[kv.Key] = kv.Value;
        }
        Devices = fixedDevices;
        return this;
    }

}
