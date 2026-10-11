using NumPad.Core.Geometry;

namespace NumPad.Core.Calibration;

/// <summary>Пара «где пользователь коснулся (тачпад) — где находится цель (наклейка)».</summary>
public sealed record CalibrationPoint(PointD Device, PointD Target, string? Label = null);

public sealed record CalibrationResult(
    bool Success,
    AffineTransform Transform,
    double RmsError,
    double MaxError,
    IReadOnlyList<double> PointErrors,
    string Message);

/// <summary>Подбор аффинного преобразования методом наименьших квадратов.</summary>
public static class CalibrationSolver
{
    /// <summary>
    /// ≥3 неколлинеарных точек → полное аффинное преобразование (6 параметров).
    /// 2 точки → масштаб и смещение по каждой оси без поворота.
    /// Погрешность считается в координатах наклейки (доли её стороны).
    /// </summary>
    public static CalibrationResult Solve(IReadOnlyList<CalibrationPoint> points)
    {
        if (points is null || points.Count < 2)
            return Fail("Нужно минимум две опорные точки.");

        if (points.Any(p => !Finite(p.Device) || !Finite(p.Target)))
            return Fail("Опорные точки содержат некорректные значения.");

        // ≥3 точек: только полное решение (коллинеарный набор — ошибка, а не «тихий» откат к упрощённой модели).
        AffineTransform? t = points.Count >= 3 ? SolveFull(points) : SolveAxisAligned(points);
        if (t is null || !t.IsValid)
            return Fail("Точки расположены на одной линии или совпадают — повторите калибровку, касаясь указанных клавиш.");

        var errors = points.Select(p => t.Apply(p.Device).DistanceTo(p.Target)).ToList();
        double rms = Math.Sqrt(errors.Sum(e => e * e) / errors.Count);
        double max = errors.Max();
        string msg = max switch
        {
            < 0.03 => "Отличная точность.",
            < 0.06 => "Хорошая точность.",
            < 0.12 => "Удовлетворительно: проверьте зоны в режиме теста.",
            _ => "Низкая точность: вероятно, касание было не по той клавише. Рекомендуется повторить.",
        };
        return new CalibrationResult(true, t, rms, max, errors, msg);
    }

    private static bool Finite(PointD p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static CalibrationResult Fail(string message) =>
        new(false, AffineTransform.Identity, double.NaN, double.NaN, Array.Empty<double>(), message);

    private static AffineTransform? SolveFull(IReadOnlyList<CalibrationPoint> pts)
    {
        // Нормальные уравнения: M = Σ [x y 1]^T [x y 1]
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, n = pts.Count;
        double sxu = 0, syu = 0, su = 0, sxv = 0, syv = 0, sv = 0;
        foreach (var p in pts)
        {
            double x = p.Device.X, y = p.Device.Y, u = p.Target.X, v = p.Target.Y;
            sxx += x * x; sxy += x * y; sx += x; syy += y * y; sy += y;
            sxu += x * u; syu += y * u; su += u;
            sxv += x * v; syv += y * v; sv += v;
        }
        var m = new double[3, 3] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        double det = Det3(m);
        // Порог относительно масштаба: детерминант ~ (разброс точек)^4 * n
        if (Math.Abs(det) < 1e-10) return null;
        var abc = Solve3(m, new[] { sxu, syu, su }, det);
        var def = Solve3(m, new[] { sxv, syv, sv }, det);
        var t = new AffineTransform(abc[0], abc[1], abc[2], def[0], def[1], def[2]);
        return t.IsValid ? t : null;
    }

    private static AffineTransform? SolveAxisAligned(IReadOnlyList<CalibrationPoint> pts)
    {
        var x = LinearFit(pts.Select(p => (p.Device.X, p.Target.X)).ToList());
        var y = LinearFit(pts.Select(p => (p.Device.Y, p.Target.Y)).ToList());
        if (x is null || y is null) return null;
        return new AffineTransform(x.Value.k, 0, x.Value.b, 0, y.Value.k, y.Value.b);
    }

    private static (double k, double b)? LinearFit(List<(double d, double t)> s)
    {
        double n = s.Count, sd = s.Sum(p => p.d), st = s.Sum(p => p.t);
        double sdd = s.Sum(p => p.d * p.d), sdt = s.Sum(p => p.d * p.t);
        double den = n * sdd - sd * sd;
        if (Math.Abs(den) < 1e-10) return null;
        double k = (n * sdt - sd * st) / den;
        double b = (st - k * sd) / n;
        return (k, b);
    }

    private static double Det3(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
        - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
        + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    /// <summary>Правило Крамера для 3×3.</summary>
    private static double[] Solve3(double[,] m, double[] r, double det)
    {
        var res = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var mc = (double[,])m.Clone();
            for (int row = 0; row < 3; row++) mc[row, c] = r[row];
            res[c] = Det3(mc) / det;
        }
        return res;
    }
}
