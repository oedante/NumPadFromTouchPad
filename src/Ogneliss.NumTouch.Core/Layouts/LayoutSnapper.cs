using Ogneliss.NumTouch.Core.Geometry;

namespace Ogneliss.NumTouch.Core.Layouts;

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public enum SnapKind { Border, Edge, Gap, Center, Size, Grid }

/// <summary>
/// Направляющая для отрисовки. Vertical = true: вертикальная линия X = <see cref="Position"/> от Y = From до Y = To;
/// иначе горизонтальная. Для <see cref="SnapKind.Gap"/> — отрезок промежутка между соседними клавишами.
/// </summary>
public readonly record struct SnapGuide(bool Vertical, double Position, double From, double To, SnapKind Kind);

public sealed record SnapResult(RectD Rect, IReadOnlyList<SnapGuide> Guides, IReadOnlyList<string> SameWidthIds, IReadOnlyList<string> SameHeightIds);

/// <summary>Параметры привязки. Все величины — в долях ширины (X) и высоты (Y) области наклейки.</summary>
public sealed class SnapOptions
{
    /// <summary>Радиус «магнита» (обычно несколько пикселей экрана, пересчитанных в доли).</summary>
    public double ThresholdX { get; init; } = 0.01;
    public double ThresholdY { get; init; } = 0.01;
    /// <summary>Промежуток между клавишами раскладки по горизонтали и вертикали.</summary>
    public double GapX { get; init; }
    public double GapY { get; init; }
    /// <summary>Шаг сетки; 0 — без сетки. Сетка действует, только если рядом нет клавиш и краёв.</summary>
    public double GridX { get; init; }
    public double GridY { get; init; }
    public double MinSize { get; init; } = LayoutValidator.MinKeySize;
}

/// <summary>
/// «Умная» привязка при перемещении и изменении размера клавиш в редакторе: к краям и центрам других клавиш,
/// к промежутку раскладки между соседями, к краям области, к размеру других клавиш и, в последнюю очередь, к сетке.
/// Чистые функции без UI — покрыты тестами.
/// </summary>
public static class LayoutSnapper
{
    private const double Eps = 1e-7;

    private enum Role { Start, End, Mid }

    private readonly record struct Candidate(double Value, Role Role, SnapKind Kind, RectD? Owner);

    public static SnapResult Move(RectD r, IReadOnlyList<(string Id, RectD Rect)> others, SnapOptions o)
    {
        var cx = Candidates(others, o, horizontal: true, r);
        var cy = Candidates(others, o, horizontal: false, r);

        double dx = Best(new[] { (r.Left, Role.Start), (r.Right, Role.End), (r.Center.X, Role.Mid) }, cx, o.ThresholdX, out bool sx);
        if (!sx && o.GridX > 0) dx = Math.Round(r.Left / o.GridX) * o.GridX - r.Left;
        double dy = Best(new[] { (r.Top, Role.Start), (r.Bottom, Role.End), (r.Center.Y, Role.Mid) }, cy, o.ThresholdY, out bool sy);
        if (!sy && o.GridY > 0) dy = Math.Round(r.Top / o.GridY) * o.GridY - r.Top;

        var res = new RectD(Math.Clamp(r.X + dx, 0, Math.Max(0, 1 - r.Width)), Math.Clamp(r.Y + dy, 0, Math.Max(0, 1 - r.Height)), r.Width, r.Height);
        return Finish(res, others, o, ResizeEdges.None);
    }

    public static SnapResult Resize(RectD r, ResizeEdges edges, IReadOnlyList<(string Id, RectD Rect)> others, SnapOptions o)
    {
        double left = r.Left, right = r.Right, top = r.Top, bottom = r.Bottom;
        var cx = Candidates(others, o, horizontal: true, r);
        var cy = Candidates(others, o, horizontal: false, r);

        if (edges.HasFlag(ResizeEdges.Left))
            left = SnapEdge(left, Role.Start, cx, others.Select(x => right - x.Rect.Width), o.ThresholdX, o.GridX);
        else if (edges.HasFlag(ResizeEdges.Right))
            right = SnapEdge(right, Role.End, cx, others.Select(x => left + x.Rect.Width), o.ThresholdX, o.GridX);
        if (edges.HasFlag(ResizeEdges.Top))
            top = SnapEdge(top, Role.Start, cy, others.Select(x => bottom - x.Rect.Height), o.ThresholdY, o.GridY);
        else if (edges.HasFlag(ResizeEdges.Bottom))
            bottom = SnapEdge(bottom, Role.End, cy, others.Select(x => top + x.Rect.Height), o.ThresholdY, o.GridY);

        left = Math.Max(0, left); top = Math.Max(0, top); right = Math.Min(1, right); bottom = Math.Min(1, bottom);
        // Минимальный размер: двигается тот край, который тянут.
        if (right - left < o.MinSize)
        {
            if (edges.HasFlag(ResizeEdges.Left)) left = right - o.MinSize; else right = left + o.MinSize;
        }
        if (bottom - top < o.MinSize)
        {
            if (edges.HasFlag(ResizeEdges.Top)) top = bottom - o.MinSize; else bottom = top + o.MinSize;
        }
        return Finish(RectD.FromEdges(left, top, right, bottom), others, o, edges);
    }

    private static double SnapEdge(double value, Role role, List<Candidate> candidates, IEnumerable<double> sizeMatches, double threshold, double grid)
    {
        var all = candidates.Where(c => c.Role == role || c.Kind == SnapKind.Edge)
            .Select(c => c.Value)
            .Concat(sizeMatches);
        double best = value, bestDist = threshold;
        bool found = false;
        foreach (var c in all)
        {
            double d = Math.Abs(c - value);
            if (d <= bestDist) { best = c; bestDist = d; found = true; }
        }
        if (!found && grid > 0) best = Math.Round(value / grid) * grid;
        return best;
    }

    private static double Best((double Value, Role Role)[] lines, List<Candidate> candidates, double threshold, out bool snapped)
    {
        double bestDelta = 0, bestDist = threshold;
        snapped = false;
        foreach (var (value, role) in lines)
            foreach (var c in candidates)
            {
                if (!Applies(c, role)) continue;
                double d = Math.Abs(c.Value - value);
                if (d <= bestDist) { bestDist = d; bestDelta = c.Value - value; snapped = true; }
            }
        return bestDelta;
    }

    private static bool Applies(Candidate c, Role role) => c.Kind switch
    {
        SnapKind.Edge => role != Role.Mid,      // край к краю: левый к левому или к правому
        SnapKind.Center => role == Role.Mid,
        _ => c.Role == role,                    // граница области и промежуток — только «свой» край
    };

    /// <summary>Кандидаты по одной оси. Промежуток (Gap) — только от клавиш того же ряда/столбца.</summary>
    private static List<Candidate> Candidates(IReadOnlyList<(string Id, RectD Rect)> others, SnapOptions o, bool horizontal, RectD self)
    {
        var list = new List<Candidate>
        {
            new(0, Role.Start, SnapKind.Border, null),
            new(1, Role.End, SnapKind.Border, null),
        };
        double gap = horizontal ? o.GapX : o.GapY;
        foreach (var (_, q) in others)
        {
            double start = horizontal ? q.Left : q.Top, end = horizontal ? q.Right : q.Bottom, mid = horizontal ? q.Center.X : q.Center.Y;
            list.Add(new(start, Role.Start, SnapKind.Edge, q));
            list.Add(new(end, Role.End, SnapKind.Edge, q));
            list.Add(new(mid, Role.Mid, SnapKind.Center, q));
            bool sameLane = horizontal
                ? q.Top < self.Bottom && q.Bottom > self.Top
                : q.Left < self.Right && q.Right > self.Left;
            if (gap > 0 && sameLane)
            {
                list.Add(new(end + gap, Role.Start, SnapKind.Gap, q));
                list.Add(new(start - gap, Role.End, SnapKind.Gap, q));
            }
        }
        return list;
    }

    private static SnapResult Finish(RectD res, IReadOnlyList<(string Id, RectD Rect)> others, SnapOptions o, ResizeEdges edges)
    {
        var guides = new List<SnapGuide>();
        AddGuides(guides, res, others, o, horizontal: true);
        AddGuides(guides, res, others, o, horizontal: false);
        var sameW = new List<string>();
        var sameH = new List<string>();
        if (edges != ResizeEdges.None)
        {
            bool w = edges.HasFlag(ResizeEdges.Left) || edges.HasFlag(ResizeEdges.Right);
            bool h = edges.HasFlag(ResizeEdges.Top) || edges.HasFlag(ResizeEdges.Bottom);
            foreach (var (id, q) in others)
            {
                if (w && Math.Abs(q.Width - res.Width) < 1e-5) sameW.Add(id);
                if (h && Math.Abs(q.Height - res.Height) < 1e-5) sameH.Add(id);
            }
        }
        return new SnapResult(res, guides, sameW, sameH);
    }

    private static void AddGuides(List<SnapGuide> guides, RectD r, IReadOnlyList<(string Id, RectD Rect)> others, SnapOptions o, bool horizontal)
    {
        double s = horizontal ? r.Left : r.Top, e = horizontal ? r.Right : r.Bottom, m = horizontal ? r.Center.X : r.Center.Y;
        double crossFrom = horizontal ? r.Top : r.Left, crossTo = horizontal ? r.Bottom : r.Right;
        double gap = horizontal ? o.GapX : o.GapY;
        var seen = new HashSet<(double, SnapKind)>();

        void Line(double pos, double from, double to, SnapKind kind)
        {
            if (!seen.Add((Math.Round(pos, 6), kind)) && kind != SnapKind.Gap) return;
            guides.Add(new SnapGuide(horizontal, pos, from, to, kind));
        }

        if (Math.Abs(s) < Eps) Line(0, 0, 1, SnapKind.Border);
        if (Math.Abs(e - 1) < Eps) Line(1, 0, 1, SnapKind.Border);
        foreach (var (_, q) in others)
        {
            double qs = horizontal ? q.Left : q.Top, qe = horizontal ? q.Right : q.Bottom, qm = horizontal ? q.Center.X : q.Center.Y;
            double qFrom = horizontal ? q.Top : q.Left, qTo = horizontal ? q.Bottom : q.Right;
            double from = Math.Min(crossFrom, qFrom), to = Math.Max(crossTo, qTo);
            foreach (var mine in new[] { s, e })
                foreach (var theirs in new[] { qs, qe })
                    if (Math.Abs(mine - theirs) < Eps) Line(mine, from, to, SnapKind.Edge);
            if (Math.Abs(m - qm) < Eps) Line(m, from, to, SnapKind.Center);
            if (gap <= 0) continue;
            double overlapFrom = Math.Max(crossFrom, qFrom), overlapTo = Math.Min(crossTo, qTo);
            if (overlapTo <= overlapFrom) continue;
            double mid = (overlapFrom + overlapTo) / 2;
            // Отрезок промежутка рисуется поперёк оси: для горизонтального промежутка — горизонтальная линия на высоте mid.
            if (Math.Abs(s - (qe + gap)) < Eps) guides.Add(new SnapGuide(!horizontal, mid, qe, s, SnapKind.Gap));
            if (Math.Abs(e - (qs - gap)) < Eps) guides.Add(new SnapGuide(!horizontal, mid, e, qs, SnapKind.Gap));
        }
    }
}
