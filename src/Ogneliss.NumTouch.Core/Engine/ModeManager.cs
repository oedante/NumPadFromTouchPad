namespace Ogneliss.NumTouch.Core.Engine;

public enum InputMode { Normal, NumPad }

/// <summary>
/// Переключение «обычный режим ↔ NumPad». Все кадры тачпада проходят через менеджер: в обычном режиме
/// он только запоминает, какие пальцы лежат на тачпаде (чтобы при включении режима они не нажали клавиши).
/// </summary>
public sealed class ModeManager
{
    private readonly KeyPressEngine _engine;
    private readonly Func<long> _clock;
    private readonly HashSet<int> _contactsDown = new();
    private long _lastToggleMs = long.MinValue / 2;

    public ModeManager(KeyPressEngine engine, Func<long> clock, int toggleDebounceMs = 400)
    {
        _engine = engine;
        _clock = clock;
        ToggleDebounceMs = toggleDebounceMs;
    }

    public InputMode Mode { get; private set; } = InputMode.Normal;
    public bool IsNumPad => Mode == InputMode.NumPad;
    public int ToggleDebounceMs { get; set; }

    /// <summary>(новый режим, причина)</summary>
    public event Action<InputMode, string>? ModeChanged;

    /// <summary>Переключение по горячей клавише. Повтор в пределах антидребезга игнорируется (защита от случайного двойного срабатывания).</summary>
    public bool Toggle(string reason = "hotkey")
    {
        long now = _clock();
        if (now - _lastToggleMs < ToggleDebounceMs) return false;
        _lastToggleMs = now;
        if (IsNumPad) Disable(reason); else Enable(reason);
        return true;
    }

    public void Enable(string reason)
    {
        if (IsNumPad) return;
        _engine.Reset(_contactsDown.ToList(), _clock());
        Mode = InputMode.NumPad;
        ModeChanged?.Invoke(Mode, reason);
    }

    /// <summary>Выключение режима. Всегда отпускает все сгенерированные клавиши, даже если режим уже выключен.</summary>
    public void Disable(string reason)
    {
        _engine.ReleaseAll(reason);
        if (!IsNumPad) return;
        Mode = InputMode.Normal;
        ModeChanged?.Invoke(Mode, reason);
    }

    public void OnFrame(TouchFrame frame)
    {
        foreach (var c in frame.Contacts)
        {
            if (c.Tip) _contactsDown.Add(c.Id); else _contactsDown.Remove(c.Id);
        }
        if (frame.IsComplete)
            _contactsDown.IntersectWith(frame.Contacts.Where(c => c.Tip).Select(c => c.Id));

        if (IsNumPad) _engine.ProcessFrame(frame);
    }

    /// <summary>Устройство потеряно: отпустить всё, забыть касания. Режим сохраняется — ввод возобновится при возврате устройства.</summary>
    public void OnDeviceLost()
    {
        _contactsDown.Clear();
        _engine.ReleaseAll("device lost");
    }

    public void Tick() => _engine.Tick(_clock());
}
