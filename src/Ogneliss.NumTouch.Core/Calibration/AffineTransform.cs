using Ogneliss.NumTouch.Core.Geometry;

namespace Ogneliss.NumTouch.Core.Calibration;

/// <summary>
/// Аффинное преобразование «нормализованные координаты тачпада → координаты наклейки»:
/// u = A·x + B·y + C;  v = D·x + E·y + F.
/// Учитывает смещение, масштаб по осям, небольшой поворот и перекос наклейки.
/// </summary>
public sealed record AffineTransform(double A, double B, double C, double D, double E, double F)
{
    public static AffineTransform Identity { get; } = new(1, 0, 0, 0, 1, 0);

    public PointD Apply(PointD p) => new(A * p.X + B * p.Y + C, D * p.X + E * p.Y + F);

    public double Determinant => A * E - B * D;

    public bool IsValid =>
        !(double.IsNaN(A) || double.IsNaN(B) || double.IsNaN(C) || double.IsNaN(D) || double.IsNaN(E) || double.IsNaN(F))
        && !(double.IsInfinity(A) || double.IsInfinity(B) || double.IsInfinity(C) || double.IsInfinity(D) || double.IsInfinity(E) || double.IsInfinity(F))
        && Math.Abs(Determinant) > 1e-9;

    public AffineTransform Inverse()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("The transform is degenerate.");
        double ia = E / det, ib = -B / det, id = -D / det, ie = A / det;
        double ic = -(ia * C + ib * F);
        double iff = -(id * C + ie * F);
        return new AffineTransform(ia, ib, ic, id, ie, iff);
    }

    /// <summary>Наклейка занимает прямоугольник <paramref name="deviceRect"/> тачпада (без поворота).</summary>
    public static AffineTransform FromDeviceRect(RectD deviceRect)
    {
        if (deviceRect.Width <= 1e-6 || deviceRect.Height <= 1e-6)
            throw new ArgumentException("The area rectangle must have a positive size.", nameof(deviceRect));
        return new AffineTransform(
            1 / deviceRect.Width, 0, -deviceRect.X / deviceRect.Width,
            0, 1 / deviceRect.Height, -deviceRect.Y / deviceRect.Height);
    }

    /// <summary>Описывающий прямоугольник области наклейки в координатах тачпада (для ручной правки границ).</summary>
    public RectD ToDeviceRect()
    {
        var inv = Inverse();
        var pts = new[] { inv.Apply(new(0, 0)), inv.Apply(new(1, 0)), inv.Apply(new(0, 1)), inv.Apply(new(1, 1)) };
        return RectD.FromEdges(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    /// <summary>Угол поворота наклейки относительно осей тачпада, градусы (для диагностики).</summary>
    public double RotationDegrees => Math.Atan2(D, A) * 180 / Math.PI;
}
