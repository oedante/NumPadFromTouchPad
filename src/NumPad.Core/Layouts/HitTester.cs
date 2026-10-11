using NumPad.Core.Geometry;

namespace NumPad.Core.Layouts;

/// <summary>
/// Определение зоны по точке в координатах наклейки (0..1).
/// Правило неоднозначности: если точка попала в несколько зон (общая граница или перекрытие),
/// выбирается зона с ближайшим центром; при равенстве — первая в списке. Результат детерминирован.
/// </summary>
public sealed class HitTester
{
    private readonly KeyZone[] _zones;

    public HitTester(Layout layout)
    {
        _zones = layout.Keys.Where(k => k.Enabled && k.Width > 0 && k.Height > 0).ToArray();
    }

    public KeyZone? Hit(PointD p)
    {
        if (double.IsNaN(p.X) || double.IsNaN(p.Y)) return null;
        KeyZone? best = null;
        double bestDist = double.MaxValue;
        foreach (var z in _zones)
        {
            var r = z.Bounds;
            if (!r.Contains(p)) continue;
            double d = r.Center.DistanceTo(p);
            if (d < bestDist - 1e-12)
            {
                best = z;
                bestDist = d;
            }
        }
        return best;
    }

    public static KeyZone? Hit(Layout layout, PointD p) => new HitTester(layout).Hit(p);
}
