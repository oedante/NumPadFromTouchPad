namespace NumPad.Core.Engine;

public enum PressTrigger
{
    /// <summary>KeyDown при касании, KeyUp при отпускании — как настоящая клавиша (удержание, автоповтор).</summary>
    OnTouchDown,
    /// <summary>Клавиша срабатывает (down+up) при отпускании пальца, если он остался в исходной зоне.
    /// Защищает от случайных касаний; удержание недоступно (кроме модификаторов).</summary>
    OnRelease,
}

public enum SlideBehavior
{
    /// <summary>Палец, съехавший в другую зону, продолжает держать исходную клавишу до отпускания.</summary>
    KeepKey,
    /// <summary>Выход пальца из исходной зоны отпускает клавишу; повторного нажатия до отрыва пальца нет.</summary>
    ReleaseOnExit,
}

public sealed class EngineOptions
{
    public PressTrigger PressTrigger { get; set; } = PressTrigger.OnTouchDown;
    public SlideBehavior SlideBehavior { get; set; } = SlideBehavior.KeepKey;
    public bool AutoRepeat { get; set; } = true;
    public int RepeatDelayMs { get; set; } = 500;
    public int RepeatIntervalMs { get; set; } = 40;
    /// <summary>Контакт без обновлений дольше этого времени считается отпущенным (потерян отчёт об отрыве).</summary>
    public int StaleContactTimeoutMs { get; set; } = 500;
    public bool IgnoreLowConfidence { get; set; } = true;
    /// <summary>Учитывать Num Lock: при выключенном цифры шлют навигационные клавиши, как настоящий цифровой блок.</summary>
    public bool FollowNumLock { get; set; }
    /// <summary>Защитный предел одновременно нажатых зон.</summary>
    public int MaxSimultaneousKeys { get; set; } = 5;

    public EngineOptions Clone() => (EngineOptions)MemberwiseClone();

    /// <summary>Приводит значения к допустимым диапазонам (защита от повреждённых настроек).</summary>
    public EngineOptions Sanitized()
    {
        var o = Clone();
        o.RepeatDelayMs = Math.Clamp(o.RepeatDelayMs, 150, 2000);
        o.RepeatIntervalMs = Math.Clamp(o.RepeatIntervalMs, 15, 500);
        o.StaleContactTimeoutMs = Math.Clamp(o.StaleContactTimeoutMs, 100, 5000);
        o.MaxSimultaneousKeys = Math.Clamp(o.MaxSimultaneousKeys, 1, 10);
        if (!Enum.IsDefined(o.PressTrigger)) o.PressTrigger = PressTrigger.OnTouchDown;
        if (!Enum.IsDefined(o.SlideBehavior)) o.SlideBehavior = SlideBehavior.KeepKey;
        return o;
    }
}
