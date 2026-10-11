namespace NumPad.Core.Geometry;

/// <summary>Точка с вещественными координатами (нормализованные единицы 0..1, если не указано иное).</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public double DistanceTo(PointD other)
    {
        double dx = X - other.X, dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    public override string ToString() => FormattableString.Invariant($"({X:0.####}, {Y:0.####})");
}

/// <summary>Прямоугольник: левый верхний угол + размеры. Ось Y направлена вниз.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PointD Center => new(X + Width / 2, Y + Height / 2);

    /// <summary>Проверка попадания; границы включаются (точка на общей границе двух зон попадает в обе,
    /// неоднозначность разрешает <see cref="Layouts.HitTester"/>).</summary>
    public bool Contains(PointD p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool IntersectsWith(RectD o) =>
        o.Left < Right && o.Right > Left && o.Top < Bottom && o.Bottom > Top;

    public static RectD FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    public RectD Union(RectD o) => FromEdges(
        Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    public RectD Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    public override string ToString() =>
        FormattableString.Invariant($"[{X:0.####}, {Y:0.####}, {Width:0.####} x {Height:0.####}]");
}
