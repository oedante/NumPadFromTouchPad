using NumPad.Core.Engine;

namespace NumPad.Platform;

/// <summary>Слот контакта из HID-отчёта Precision Touchpad (координаты уже нормализованы 0..1).</summary>
public readonly record struct HidSlot(int ContactId, double X, double Y, bool Tip, bool Confidence);

/// <summary>
/// Сборка кадров из отчётов Windows Precision Touchpad (вынесена из бэкенда, чтобы тестироваться на любой ОС).
/// Параллельный режим: все контакты в одном отчёте, Contact Count = числу действительных слотов.
/// Гибридный режим: Contact Count только в первом отчёте кадра, в следующих — 0; действительны первые
/// (ожидаемое − собранное) слоты каждого отчёта.
/// </summary>
public sealed class HidFrameAssembler
{
    private int _expected;
    private readonly List<TouchContact> _pending = new();

    /// <summary>Подать отчёт. Возвращает 0–2 кадра (недособранный прошлый + текущий).</summary>
    public IReadOnlyList<TouchFrame> Push(IReadOnlyList<HidSlot> slots, int? contactCount, long nowMs)
    {
        List<TouchFrame>? result = null;
        int count = contactCount ?? slots.Count; // устройство без Contact Count: все слоты действительны
        if (count > 0 || _expected == 0)
        {
            if (_pending.Count > 0) (result ??= new()).Add(new TouchFrame(nowMs, _pending.ToArray(), false));
            _pending.Clear();
            _expected = count;
        }

        int take = Math.Min(slots.Count, Math.Max(0, _expected - _pending.Count));
        for (int i = 0; i < take; i++)
        {
            var s = slots[i];
            _pending.Add(new TouchContact(s.ContactId, s.X, s.Y, s.Tip, s.Confidence));
        }

        if (_pending.Count >= _expected)
        {
            (result ??= new()).Add(new TouchFrame(nowMs, _pending.ToArray(), true));
            _pending.Clear();
            _expected = 0;
        }
        return (IReadOnlyList<TouchFrame>?)result ?? Array.Empty<TouchFrame>();
    }
}
