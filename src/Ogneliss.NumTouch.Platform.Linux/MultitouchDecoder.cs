using Ogneliss.NumTouch.Core.Engine;
using static Ogneliss.NumTouch.Platform.Linux.LibC;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>Диапазоны осей тачпада (из EVIOCGABS). Resolution — единиц на мм (0 — неизвестно).</summary>
public readonly record struct AxisRange(int Min, int Max, int Resolution)
{
    public double Normalize(int v) => Max <= Min ? 0 : Math.Clamp((double)(v - Min) / (Max - Min), 0, 1);
    public double? SizeMm => Resolution > 0 ? (double)(Max - Min) / Resolution : null;
}

/// <summary>
/// Декодер протокола multitouch B (слоты) Linux evdev → кадры <see cref="TouchFrame"/>.
/// ABS_MT_SLOT выбирает слот, ABS_MT_TRACKING_ID ≥ 0 — новое касание, −1 — отрыв, SYN_REPORT завершает кадр.
/// ABS_MT_TOOL_TYPE = MT_TOOL_PALM — ладонь (Confidence = false). SYN_DROPPED (переполнение буфера ядра):
/// все касания считаются отпущенными, состояние восстанавливается по следующим событиям.
/// Без ввода-вывода — тестируется на любой ОС.
/// </summary>
public sealed class MultitouchDecoder
{
    private sealed class Slot
    {
        public int TrackingId = -1;
        public int LiftedId = -1;  // ID, отпущенный в текущем кадре (для отчёта Tip=false)
        public int X, Y;
        public bool Palm;
    }

    private readonly Slot[] _slots;
    private readonly AxisRange _x, _y;
    private int _current;
    private bool _dropped;

    public MultitouchDecoder(int slotCount, AxisRange x, AxisRange y, int initialSlot = 0)
    {
        _slots = Enumerable.Range(0, Math.Clamp(slotCount, 1, 64)).Select(_ => new Slot()).ToArray();
        _x = x;
        _y = y;
        _current = Math.Clamp(initialSlot, 0, _slots.Length - 1);
    }

    public AxisRange X => _x;
    public AxisRange Y => _y;

    /// <summary>Есть ли сейчас касания (для логики проброса кнопок).</summary>
    public bool AnyActive => _slots.Any(s => s.TrackingId >= 0);

    /// <summary>Подать событие. Возвращает кадр на SYN_REPORT, иначе null.</summary>
    public TouchFrame? Feed(ushort type, ushort code, int value, long nowMs)
    {
        if (type == EV_SYN)
        {
            if (code == SYN_DROPPED) { _dropped = true; return null; }
            if (code != SYN_REPORT) return null;
            if (_dropped)
            {
                // Часть событий потеряна: безопасно «отпустить» всё.
                _dropped = false;
                var lifted = new List<TouchContact>();
                foreach (var s in _slots)
                {
                    if (s.TrackingId >= 0) lifted.Add(new TouchContact(s.TrackingId, _x.Normalize(s.X), _y.Normalize(s.Y), false, !s.Palm));
                    s.TrackingId = -1;
                    s.LiftedId = -1;
                    s.Palm = false;
                }
                return new TouchFrame(nowMs, lifted, true);
            }
            return BuildFrame(nowMs);
        }

        if (_dropped || type != EV_ABS) return null;

        switch (code)
        {
            case ABS_MT_SLOT:
                _current = value >= 0 && value < _slots.Length ? value : _current;
                break;
            case ABS_MT_TRACKING_ID:
            {
                var s = _slots[_current];
                if (value < 0)
                {
                    if (s.TrackingId >= 0) s.LiftedId = s.TrackingId;
                    s.TrackingId = -1;
                }
                else
                {
                    if (s.TrackingId >= 0 && s.TrackingId != value) s.LiftedId = s.TrackingId; // смена касания в слоте без −1
                    s.TrackingId = value;
                    s.Palm = false;
                }
                break;
            }
            case ABS_MT_POSITION_X: _slots[_current].X = value; break;
            case ABS_MT_POSITION_Y: _slots[_current].Y = value; break;
            case ABS_MT_TOOL_TYPE: _slots[_current].Palm = value == MT_TOOL_PALM; break;
        }
        return null;
    }

    private TouchFrame BuildFrame(long nowMs)
    {
        var contacts = new List<TouchContact>(_slots.Length);
        foreach (var s in _slots)
        {
            double nx = _x.Normalize(s.X), ny = _y.Normalize(s.Y);
            if (s.LiftedId >= 0)
            {
                contacts.Add(new TouchContact(s.LiftedId, nx, ny, false, !s.Palm));
                s.LiftedId = -1;
            }
            if (s.TrackingId >= 0) contacts.Add(new TouchContact(s.TrackingId, nx, ny, true, !s.Palm));
        }
        return new TouchFrame(nowMs, contacts, true);
    }
}
