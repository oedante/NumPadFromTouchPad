using NumPad.Core.Engine;
using NumPad.Core.Geometry;
using NumPad.Core.Layouts;

namespace NumPad.Core.Calibration;

public sealed record CalibrationTarget(string KeyId, string Label, PointD Position);

/// <summary>
/// Мастер калибровки по опорным точкам. Цели — центры клавиш раскладки, ближайших к четырём углам и
/// центру наклейки (пользователь касается напечатанной клавиши — это точнее, чем «угол тачпада»).
/// Каждое касание даёт медиану сырых координат за время касания. Работает с реальными кадрами тачпада.
/// </summary>
public sealed class CalibrationSession
{
    private readonly List<CalibrationTarget> _targets;
    private readonly List<CalibrationPoint> _points = new();
    private readonly List<PointD> _samples = new();
    private int? _trackedContact;
    private long _touchStartMs;

    public int MinTouchMs { get; set; } = 40;

    public CalibrationSession(Layout layout)
    {
        _targets = PickTargets(layout);
    }

    public CalibrationSession(IEnumerable<CalibrationTarget> targets)
    {
        _targets = targets.ToList();
    }

    public IReadOnlyList<CalibrationTarget> Targets => _targets;
    public IReadOnlyList<CalibrationPoint> Points => _points;
    public int CurrentIndex => _points.Count;
    public CalibrationTarget? CurrentTarget => _points.Count < _targets.Count ? _targets[_points.Count] : null;
    public bool IsComplete => _points.Count >= _targets.Count;
    public bool IsTouching => _trackedContact is not null;

    public event Action<CalibrationPoint>? PointCaptured;

    public static List<CalibrationTarget> PickTargets(Layout layout)
    {
        var keys = layout.Keys.Where(k => k.Enabled).ToList();
        var result = new List<CalibrationTarget>();
        if (keys.Count == 0) return result;
        var anchors = new[] { new PointD(0, 0), new PointD(1, 0), new PointD(1, 1), new PointD(0, 1), new PointD(0.5, 0.5) };
        foreach (var a in anchors)
        {
            var best = keys
                .Where(k => result.All(r => r.KeyId != k.Id))
                .OrderBy(k => k.Bounds.Center.DistanceTo(a))
                .FirstOrDefault();
            if (best is not null) result.Add(new CalibrationTarget(best.Id, best.Label, best.Bounds.Center));
        }
        return result;
    }

    /// <summary>Подать кадр тачпада (координаты устройства, 0..1).</summary>
    public void OnFrame(TouchFrame frame)
    {
        if (IsComplete) return;

        if (_trackedContact is null)
        {
            var first = frame.Contacts.FirstOrDefault(c => c.Tip && c.Confidence);
            if (first.Tip)
            {
                _trackedContact = first.Id;
                _touchStartMs = frame.TimestampMs;
                _samples.Clear();
                _samples.Add(first.Position);
            }
            return;
        }

        int id = _trackedContact.Value;
        var match = frame.Contacts.Where(c => c.Id == id).ToList();
        bool lifted;
        if (match.Count == 0) lifted = frame.IsComplete;
        else
        {
            var c = match[0];
            if (c.Tip) { _samples.Add(c.Position); lifted = false; }
            else lifted = true;
        }

        if (lifted) Finish(frame.TimestampMs);
    }

    private void Finish(long nowMs)
    {
        _trackedContact = null;
        if (_samples.Count == 0 || nowMs - _touchStartMs < MinTouchMs && _samples.Count < 2) return;
        var target = CurrentTarget;
        if (target is null) return;
        var device = new PointD(Median(_samples.Select(s => s.X)), Median(_samples.Select(s => s.Y)));
        var point = new CalibrationPoint(device, target.Position, target.Label);
        _points.Add(point);
        _samples.Clear();
        PointCaptured?.Invoke(point);
    }

    public void UndoLast()
    {
        _trackedContact = null;
        _samples.Clear();
        if (_points.Count > 0) _points.RemoveAt(_points.Count - 1);
    }

    public void Restart()
    {
        _trackedContact = null;
        _samples.Clear();
        _points.Clear();
    }

    /// <summary>Добавить точку напрямую (тесты, ручной ввод).</summary>
    public void AddPoint(PointD device)
    {
        var t = CurrentTarget ?? throw new InvalidOperationException("Все точки уже собраны.");
        _points.Add(new CalibrationPoint(device, t.Position, t.Label));
    }

    public CalibrationResult Solve() => CalibrationSolver.Solve(_points);

    private static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        if (a.Length == 0) return double.NaN;
        int m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : (a[m - 1] + a[m]) / 2;
    }
}
