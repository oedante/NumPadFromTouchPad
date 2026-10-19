using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Layouts;

namespace Ogneliss.NumTouch.App.Controls;

/// <summary>
/// Отрисовка раскладки (наклейки): клавиши, касания, цели калибровки. В режиме редактирования — выделение,
/// перемещение и изменение размера клавиш мышью за любой край или угол, «умная» привязка к соседним клавишам,
/// промежутку раскладки, краям области, размеру других клавиш и сетке (<see cref="LayoutSnapper"/>),
/// направляющие, линейки и подсказка с размером во время перетаскивания.
/// Модель — в нормализованных координатах 0..1 области наклейки; на экране сохраняется соотношение сторон.
/// </summary>
public sealed class LayoutCanvas : Control
{
    private const double Pad = 12;
    private const double RulerTop = 20, RulerLeft = 30;
    private const double HandleSize = 8;
    private const double EdgeHit = 7;      // «ловля» края, px
    private const double CornerHit = 11;   // «ловля» угла, px
    private const double SnapPx = 8;       // радиус привязки, px
    private const double DragStartPx = 3;  // до этого сдвига щелчок не считается перетаскиванием

    private static readonly IBrush StickerBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE0));
    private static readonly IBrush KeyBrush = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush KeyDisabledBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEA));
    private static readonly IBrush KeyActiveBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x64));
    private static readonly IBrush KeyTargetBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD9, 0x64));
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly IBrush SubtleBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x86));
    private static readonly IBrush ContactBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x32, 0x32, 0x32));
    private static readonly IBrush ContactIgnoredBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0xC0, 0x39, 0x2B));
    private static readonly IBrush RulerBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF5));
    private static readonly IBrush RulerTextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x86));
    private static readonly IBrush RulerBandBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD9, 0x64));
    private static readonly IBrush ReadoutBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xE8, 0x26, 0x26, 0x26));
    private static readonly IBrush GuideBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE0, 0x3E, 0x6E));
    private static readonly IPen KeyPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC2)), 1);
    private static readonly IPen DisabledPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xAA)), 1, new ImmutableDashStyle(new double[] { 3, 2 }, 0));
    private static readonly IPen SelectedPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32)), 2);
    private static readonly IPen HandlePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32)), 1.2);
    private static readonly IPen HoverPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x94)), 1.5);
    private static readonly IPen StickerPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC2)), 1);
    private static readonly IPen GridMinorPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x1C, 0, 0, 0)), 1);
    private static readonly IPen GridMajorPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 1);
    private static readonly IPen RulerPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xAE)), 1);
    private static readonly IPen GuidePen = new ImmutablePen(GuideBrush, 1);
    private static readonly IPen SameSizePen = new ImmutablePen(GuideBrush, 1.5, new ImmutableDashStyle(new double[] { 3, 2 }, 0));
    private static readonly IPen TargetPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), 2);
    private static readonly IPen PointerAreaPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x94)), 1, new ImmutableDashStyle(new double[] { 1, 2 }, 0));

    public LayoutCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // ------------------------------------------------------------------ свойства

    private Layout? _layout;
    public Layout? Layout { get => _layout; set { _layout = value; InvalidateVisual(); } }

    private string? _selectedKeyId;
    public string? SelectedKeyId
    {
        get => _selectedKeyId;
        set { if (_selectedKeyId != value) { _selectedKeyId = value; InvalidateVisual(); SelectionChanged?.Invoke(value); } }
    }

    private IReadOnlyList<ActiveContactInfo> _contacts = Array.Empty<ActiveContactInfo>();
    /// <summary>Касания в координатах наклейки.</summary>
    public IReadOnlyList<ActiveContactInfo> Contacts { get => _contacts; set { _contacts = value ?? Array.Empty<ActiveContactInfo>(); InvalidateVisual(); } }

    /// <summary>Подсвеченные (нажатые/распознанные) клавиши.</summary>
    public HashSet<string> ActiveKeyIds { get; } = new();

    private string? _targetKeyId;
    /// <summary>Клавиша-цель калибровки.</summary>
    public string? TargetKeyId { get => _targetKeyId; set { _targetKeyId = value; InvalidateVisual(); } }

    private IReadOnlyList<PointD> _marks = Array.Empty<PointD>();
    /// <summary>Уже собранные опорные точки (координаты наклейки).</summary>
    public IReadOnlyList<PointD> Marks { get => _marks; set { _marks = value ?? Array.Empty<PointD>(); InvalidateVisual(); } }

    /// <summary>Режим редактора: выделение, перетаскивание, линейки.</summary>
    public bool IsEditable { get; set; }

    private bool _snap;
    /// <summary>Привязка (к клавишам, промежутку, краям, сетке) и отображение сетки.</summary>
    public bool Snap { get => _snap; set { _snap = value; InvalidateVisual(); } }

    /// <summary>Шаг сетки привязки в долях ширины/высоты.</summary>
    public double GridStep { get; set; } = 0.025;

    /// <summary>Физический размер области наклейки, мм (для подсказки при изменении размера); null — неизвестен.</summary>
    public Size? AreaSizeMm { get; set; }

    public event Action<string?>? SelectionChanged;
    /// <summary>Перед первым изменением геометрии при перетаскивании (снимок для отмены).</summary>
    public event Action? EditStarting;
    /// <summary>Во время перетаскивания.</summary>
    public event Action? Editing;
    /// <summary>После завершения перетаскивания.</summary>
    public event Action? EditCompleted;
    /// <summary>Двойной щелчок по клавише.</summary>
    public event Action<string>? KeyDoubleClicked;

    public void Refresh() => InvalidateVisual();

    // ------------------------------------------------------------------ геометрия

    private bool Rulers => IsEditable;

    private Rect StickerRect()
    {
        double aspect = _layout is { AspectRatio: > 0 } l ? l.AspectRatio : 1.6;
        double left = Pad + (Rulers ? RulerLeft : 0), top = Pad + (Rulers ? RulerTop : 0);
        double w = Math.Max(0, Bounds.Width - left - Pad), h = Math.Max(0, Bounds.Height - top - Pad);
        if (w <= 0 || h <= 0) return default;
        double sw = w, sh = w / aspect;
        if (sh > h) { sh = h; sw = h * aspect; }
        return new Rect(left + (w - sw) / 2, top + (h - sh) / 2, sw, sh);
    }

    private static Rect ToScreen(Rect s, RectD r) => new(s.X + r.X * s.Width, s.Y + r.Y * s.Height, r.Width * s.Width, r.Height * s.Height);
    private static Point ToScreen(Rect s, PointD p) => new(s.X + p.X * s.Width, s.Y + p.Y * s.Height);
    private static PointD ToModel(Rect s, Point p) => new((p.X - s.X) / s.Width, (p.Y - s.Y) / s.Height);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    // ------------------------------------------------------------------ отрисовка

    public override void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // попадание мыши по всей площади
        var sticker = StickerRect();
        if (sticker.Width <= 0) return;
        dc.DrawRectangle(StickerBrush, StickerPen, sticker, 10, 10);
        if (_layout is null) return;

        if (_snap && IsEditable && GridStep > 0) DrawGrid(dc, sticker);

        if (_layout.AllowPointerOutsideKeys && _layout.KeyBounds() is { } kb)
            dc.DrawRectangle(null, PointerAreaPen, ToScreen(sticker, kb), 6, 6);

        double unit = Math.Min(sticker.Width, sticker.Height);
        KeyZone? selected = null;
        foreach (var k in _layout.Keys)
        {
            var r = ToScreen(sticker, k.Bounds);
            if (r.Width < 1 || r.Height < 1) continue;
            bool isSelected = IsEditable && k.Id == _selectedKeyId;
            if (isSelected) selected = k;
            bool active = ActiveKeyIds.Contains(k.Id);
            bool target = k.Id == _targetKeyId;
            IBrush fill = !k.Enabled ? KeyDisabledBrush : active ? KeyActiveBrush : target ? KeyTargetBrush : KeyBrush;
            IPen pen = isSelected ? SelectedPen
                : target ? TargetPen
                : IsEditable && k.Id == _hoverKeyId && _drag == DragMode.None ? HoverPen
                : k.Enabled ? KeyPen : DisabledPen;
            double radius = Math.Min(6, Math.Min(r.Width, r.Height) / 5);
            dc.DrawRectangle(fill, pen, r, radius, radius);
            if (_sameSize.Contains(k.Id)) dc.DrawRectangle(null, SameSizePen, r.Deflate(2), radius, radius);
            DrawLabel(dc, string.IsNullOrEmpty(k.Label) ? "?" : k.Label, r, unit, k.Enabled ? TextBrush : SubtleBrush);
        }

        if (selected is not null) DrawHandles(dc, ToScreen(sticker, selected.Bounds));
        foreach (var g in _guides) DrawGuide(dc, sticker, g);
        if (Rulers) DrawRulers(dc, sticker, selected);
        if (_drag != DragMode.None && _dragging && selected is not null) DrawReadout(dc, sticker, selected);

        foreach (var m in _marks)
            dc.DrawEllipse(null, TargetPen, ToScreen(sticker, m), 5, 5);

        if (_targetKeyId is not null && _layout.Keys.FirstOrDefault(k => k.Id == _targetKeyId) is { } tk)
        {
            var c = ToScreen(sticker, tk.Bounds.Center);
            dc.DrawLine(TargetPen, new Point(c.X - 10, c.Y), new Point(c.X + 10, c.Y));
            dc.DrawLine(TargetPen, new Point(c.X, c.Y - 10), new Point(c.X, c.Y + 10));
            dc.DrawEllipse(null, TargetPen, c, 14, 14);
        }

        foreach (var c in _contacts)
            dc.DrawEllipse(c.Ignored ? ContactIgnoredBrush : ContactBrush, null, ToScreen(sticker, c.AreaPosition), 9, 9);
    }

    private void DrawGrid(DrawingContext dc, Rect s)
    {
        int n = (int)Math.Round(1 / GridStep);
        int major = Math.Max(1, (int)Math.Round(0.1 / GridStep)); // линия каждые 10 %
        for (int i = 1; i < n; i++)
        {
            var pen = i % major == 0 ? GridMajorPen : GridMinorPen;
            double x = Math.Round(s.X + s.Width * i / n) + 0.5, y = Math.Round(s.Y + s.Height * i / n) + 0.5;
            dc.DrawLine(pen, new Point(x, s.Top + 1), new Point(x, s.Bottom - 1));
            dc.DrawLine(pen, new Point(s.Left + 1, y), new Point(s.Right - 1, y));
        }
    }

    private static void DrawHandles(DrawingContext dc, Rect r)
    {
        foreach (var p in new[]
                 {
                     r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight,
                     new Point(r.Center.X, r.Top), new Point(r.Center.X, r.Bottom),
                     new Point(r.Left, r.Center.Y), new Point(r.Right, r.Center.Y),
                 })
            dc.DrawRectangle(Brushes.White, HandlePen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize), 1.5, 1.5);
    }

    private static void DrawGuide(DrawingContext dc, Rect s, SnapGuide g)
    {
        if (g.Kind == SnapKind.Gap)
        {
            // Отрезок промежутка с засечками на концах.
            if (g.Vertical)
            {
                double x = s.X + g.Position * s.Width, y1 = s.Y + g.From * s.Height, y2 = s.Y + g.To * s.Height;
                dc.DrawLine(GuidePen, new Point(x, y1), new Point(x, y2));
                dc.DrawLine(GuidePen, new Point(x - 4, y1), new Point(x + 4, y1));
                dc.DrawLine(GuidePen, new Point(x - 4, y2), new Point(x + 4, y2));
            }
            else
            {
                double y = s.Y + g.Position * s.Height, x1 = s.X + g.From * s.Width, x2 = s.X + g.To * s.Width;
                dc.DrawLine(GuidePen, new Point(x1, y), new Point(x2, y));
                dc.DrawLine(GuidePen, new Point(x1, y - 4), new Point(x1, y + 4));
                dc.DrawLine(GuidePen, new Point(x2, y - 4), new Point(x2, y + 4));
            }
            return;
        }
        if (g.Vertical)
        {
            double x = Math.Round(s.X + g.Position * s.Width) + 0.5;
            dc.DrawLine(GuidePen, new Point(x, s.Y + g.From * s.Height - 6), new Point(x, s.Y + g.To * s.Height + 6));
        }
        else
        {
            double y = Math.Round(s.Y + g.Position * s.Height) + 0.5;
            dc.DrawLine(GuidePen, new Point(s.X + g.From * s.Width - 6, y), new Point(s.X + g.To * s.Width + 6, y));
        }
    }

    /// <summary>Линейки сверху и слева: деления каждые 5 %, подписи каждые 10 % (или 20 %, если тесно); выделенная клавиша — полосой.</summary>
    private void DrawRulers(DrawingContext dc, Rect s, KeyZone? selected)
    {
        var top = new Rect(s.X, s.Y - RulerTop - 4, s.Width, RulerTop);
        var left = new Rect(s.X - RulerLeft - 4, s.Y, RulerLeft, s.Height);
        dc.DrawRectangle(RulerBrush, RulerPen, top, 3, 3);
        dc.DrawRectangle(RulerBrush, RulerPen, left, 3, 3);
        if (selected is not null)
        {
            dc.DrawRectangle(RulerBandBrush, null, new Rect(s.X + selected.X * s.Width, top.Y + 1, selected.Width * s.Width, top.Height - 2));
            dc.DrawRectangle(RulerBandBrush, null, new Rect(left.X + 1, s.Y + selected.Y * s.Height, left.Width - 2, selected.Height * s.Height));
        }
        int labelEveryX = s.Width / 10 >= 30 ? 10 : 20;
        int labelEveryY = s.Height / 10 >= 18 ? 10 : 20;
        for (int p = 0; p <= 100; p += 5)
        {
            double x = Math.Round(s.X + s.Width * p / 100) + 0.5;
            double len = p % 10 == 0 ? 7 : 4;
            dc.DrawLine(RulerPen, new Point(x, top.Bottom - len), new Point(x, top.Bottom));
            if (p % labelEveryX == 0 && p is > 0 and < 100)
            {
                var ft = Text(p.ToString(CultureInfo.InvariantCulture), 9.5, RulerTextBrush);
                dc.DrawText(ft, new Point(x - ft.Width / 2, top.Y + 1));
            }
            double y = Math.Round(s.Y + s.Height * p / 100) + 0.5;
            dc.DrawLine(RulerPen, new Point(left.Right - len, y), new Point(left.Right, y));
            if (p % labelEveryY == 0 && p is > 0 and < 100)
            {
                var ft = Text(p.ToString(CultureInfo.InvariantCulture), 9.5, RulerTextBrush);
                dc.DrawText(ft, new Point(left.Right - 9 - ft.Width, y - ft.Height / 2));
            }
        }
    }

    /// <summary>Подсказка во время перетаскивания: положение или размер в % области (и в мм, если известен размер тачпада).</summary>
    private void DrawReadout(DrawingContext dc, Rect s, KeyZone k)
    {
        var c = CultureInfo.CurrentCulture;
        string text = _drag == DragMode.Move
            ? string.Format(c, "X {0:0.#}%   Y {1:0.#}%", k.X * 100, k.Y * 100)
            : string.Format(c, "{0:0.#}% × {1:0.#}%", k.Width * 100, k.Height * 100);
        if (_drag == DragMode.Resize && AreaSizeMm is { } mm)
            text += "   " + L.F("{0}×{1} mm", (k.Width * mm.Width).ToString("0.#", c), (k.Height * mm.Height).ToString("0.#", c));
        var ft = Text(text, 12, Brushes.White);
        var r = ToScreen(s, k.Bounds);
        double w = ft.Width + 16, h = ft.Height + 8;
        double x = Math.Clamp(r.Center.X - w / 2, 2, Bounds.Width - w - 2);
        double y = r.Top - h - 8;
        if (y < 2) y = r.Bottom + 8;
        var box = new Rect(x, y, w, h);
        dc.DrawRectangle(ReadoutBrush, null, box, 5, 5);
        dc.DrawText(ft, new Point(box.X + 8, box.Y + 4));
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);

    /// <summary>
    /// Подпись клавиши — по центру, в одну строку с уменьшением шрифта под ширину; если в подписи есть пробел и
    /// так шрифт выходит крупнее — в две строки по пробелу («Page Up»). Слово никогда не разрывается посередине.
    /// </summary>
    private static void DrawLabel(DrawingContext dc, string label, Rect r, double unit, IBrush brush)
    {
        const double MinFont = 6.5, LineHeight = 1.22;
        double maxW = Math.Max(1, r.Width - 6), maxH = Math.Max(1, r.Height - 4);
        double fs = Math.Clamp(Math.Min(r.Height * 0.42, unit * 0.065), MinFont, 22);

        // Одна строка.
        double w1 = Text(label, 10, brush).Width / 10; // ширина при кегле 1
        double fs1 = Math.Min(fs, w1 > 0 ? maxW / w1 : fs);
        string text = label;
        double best = fs1;

        // Две строки по пробелу, ближайшему к середине.
        int split = -1;
        for (int i = 0; i < label.Length; i++)
            if (label[i] == ' ' && (split < 0 || Math.Abs(i - label.Length / 2.0) < Math.Abs(split - label.Length / 2.0))) split = i;
        if (split > 0 && label.Contains(' ') && !label.Contains('\n'))
        {
            string a = label[..split].Trim(), b = label[(split + 1)..].Trim();
            double w2 = Math.Max(Text(a, 10, brush).Width, Text(b, 10, brush).Width) / 10;
            double fs2 = Math.Min(Math.Min(fs, w2 > 0 ? maxW / w2 : fs), maxH / (2 * LineHeight));
            if (fs2 > best * 1.08) { best = fs2; text = a + "\n" + b; }
        }

        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, Math.Max(MinFont, best), brush)
        {
            MaxTextWidth = Math.Max(1, r.Width - 4),
            MaxLineCount = text.Contains('\n') ? 2 : 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(ft, new Point(r.X + 2, r.Y + (r.Height - ft.Height) / 2));
    }

    // ------------------------------------------------------------------ редактирование

    private enum DragMode { None, Move, Resize }
    private DragMode _drag;
    private ResizeEdges _edges;
    private bool _dragging;              // сдвиг превысил порог — идёт перетаскивание
    private Point _pressPos;
    private RectD _origin;
    private string? _hoverKeyId;
    private IReadOnlyList<SnapGuide> _guides = Array.Empty<SnapGuide>();
    private readonly HashSet<string> _sameSize = new();

    /// <summary>Какой край/угол клавиши под указателем (с допуском); для маленьких клавиш допуск уменьшается.</summary>
    private static ResizeEdges EdgesAt(Rect r, Point p)
    {
        double tolX = Math.Min(EdgeHit, r.Width / 3), tolY = Math.Min(EdgeHit, r.Height / 3);
        double cornX = Math.Min(CornerHit, r.Width / 3), cornY = Math.Min(CornerHit, r.Height / 3);
        if (p.X < r.Left - tolX || p.X > r.Right + tolX || p.Y < r.Top - tolY || p.Y > r.Bottom + tolY) return ResizeEdges.None;
        double dl = Math.Abs(p.X - r.Left), dr = Math.Abs(p.X - r.Right), dt = Math.Abs(p.Y - r.Top), db = Math.Abs(p.Y - r.Bottom);
        // Угол — с увеличенным допуском.
        if ((dl <= cornX || dr <= cornX) && (dt <= cornY || db <= cornY))
            return (dl <= dr ? ResizeEdges.Left : ResizeEdges.Right) | (dt <= db ? ResizeEdges.Top : ResizeEdges.Bottom);
        var e = ResizeEdges.None;
        if (dl <= tolX && dl <= dr) e |= ResizeEdges.Left;
        else if (dr <= tolX) e |= ResizeEdges.Right;
        if (dt <= tolY && dt <= db) e |= ResizeEdges.Top;
        else if (db <= tolY) e |= ResizeEdges.Bottom;
        return e;
    }

    /// <summary>Попадание: сначала края выделенной клавиши, затем клавиша под указателем (внутри или у края), затем ближайший край.</summary>
    private (KeyZone? Key, ResizeEdges Edges) HitTest(Rect sticker, Point pos)
    {
        if (_layout is null) return (null, ResizeEdges.None);
        var model = ToModel(sticker, pos);
        if (_layout.Keys.FirstOrDefault(k => k.Id == _selectedKeyId) is { } sel)
        {
            var e = EdgesAt(ToScreen(sticker, sel.Bounds), pos);
            if (e != ResizeEdges.None) return (sel, e);
        }
        var inside = _layout.Keys.LastOrDefault(k => k.Bounds.Contains(model));
        if (inside is not null) return (inside, EdgesAt(ToScreen(sticker, inside.Bounds), pos));
        foreach (var k in _layout.Keys.AsEnumerable().Reverse())
        {
            var e = EdgesAt(ToScreen(sticker, k.Bounds), pos);
            if (e != ResizeEdges.None) return (k, e);
        }
        return (null, ResizeEdges.None);
    }

    private static Cursor CursorFor(ResizeEdges e) => new(e switch
    {
        ResizeEdges.Left | ResizeEdges.Top => StandardCursorType.TopLeftCorner,
        ResizeEdges.Right | ResizeEdges.Bottom => StandardCursorType.BottomRightCorner,
        ResizeEdges.Right | ResizeEdges.Top => StandardCursorType.TopRightCorner,
        ResizeEdges.Left | ResizeEdges.Bottom => StandardCursorType.BottomLeftCorner,
        ResizeEdges.Left or ResizeEdges.Right => StandardCursorType.SizeWestEast,
        ResizeEdges.Top or ResizeEdges.Bottom => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.SizeAll,
    });

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!IsEditable || _layout is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var sticker = StickerRect();
        if (sticker.Width <= 0) return;
        var pos = e.GetPosition(this);
        var (key, edges) = HitTest(sticker, pos);
        SelectedKeyId = key?.Id;
        e.Handled = true;
        if (key is null) return;
        if (e.ClickCount >= 2 && edges == ResizeEdges.None) { KeyDoubleClicked?.Invoke(key.Id); return; }
        _drag = edges == ResizeEdges.None ? DragMode.Move : DragMode.Resize;
        _edges = edges;
        _dragging = false;
        _pressPos = pos;
        _origin = key.Bounds;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_layout is null || !IsEditable) return;
        var sticker = StickerRect();
        if (sticker.Width <= 0) return;
        var pos = e.GetPosition(this);

        if (_drag == DragMode.None)
        {
            var (hk, he) = HitTest(sticker, pos);
            Cursor = hk is null ? Cursor.Default : CursorFor(he);
            if (_hoverKeyId != hk?.Id) { _hoverKeyId = hk?.Id; InvalidateVisual(); }
            return;
        }

        var key = _layout.Keys.FirstOrDefault(k => k.Id == _selectedKeyId);
        if (key is null) return;
        double dxPx = pos.X - _pressPos.X, dyPx = pos.Y - _pressPos.Y;
        if (!_dragging)
        {
            if (Math.Abs(dxPx) < DragStartPx && Math.Abs(dyPx) < DragStartPx) return;
            _dragging = true;
            EditStarting?.Invoke();
        }
        ApplyDrag(key, sticker, dxPx, dyPx, e.KeyModifiers);
    }

    private void ApplyDrag(KeyZone key, Rect sticker, double dxPx, double dyPx, KeyModifiers mods)
    {
        if (_layout is null) return;
        if (_drag == DragMode.Move && mods.HasFlag(KeyModifiers.Shift))
        {
            if (Math.Abs(dxPx) >= Math.Abs(dyPx)) dyPx = 0; else dxPx = 0; // Shift — по одной оси
        }
        double dx = dxPx / sticker.Width, dy = dyPx / sticker.Height;

        bool snap = _snap && !mods.HasFlag(KeyModifiers.Alt); // Alt — временно без привязки
        var others = _layout.Keys.Where(k => k.Id != key.Id).Select(k => (k.Id, k.Bounds)).ToList();
        var options = new SnapOptions
        {
            ThresholdX = snap ? SnapPx / sticker.Width : 0,
            ThresholdY = snap ? SnapPx / sticker.Height : 0,
            GapX = _layout.Gap,
            GapY = _layout.Gap * _layout.AspectRatio,
            GridX = snap ? GridStep : 0,
            GridY = snap ? GridStep : 0,
        };

        SnapResult result;
        if (_drag == DragMode.Move)
        {
            result = LayoutSnapper.Move(new RectD(_origin.X + dx, _origin.Y + dy, _origin.Width, _origin.Height), others, options);
        }
        else
        {
            double l = _origin.Left, t = _origin.Top, r = _origin.Right, b = _origin.Bottom;
            if (_edges.HasFlag(ResizeEdges.Left)) l += dx;
            if (_edges.HasFlag(ResizeEdges.Right)) r += dx;
            if (_edges.HasFlag(ResizeEdges.Top)) t += dy;
            if (_edges.HasFlag(ResizeEdges.Bottom)) b += dy;
            // Тянут край за противоположный — не переворачиваем клавишу, а упираемся в минимальный размер.
            result = LayoutSnapper.Resize(RectD.FromEdges(l, t, Math.Max(r, l), Math.Max(b, t)), _edges, others, options);
        }

        var nr = result.Rect;
        key.X = Math.Round(nr.X, 5); key.Y = Math.Round(nr.Y, 5);
        key.Width = Math.Round(nr.Width, 5); key.Height = Math.Round(nr.Height, 5);
        _guides = snap ? result.Guides : Array.Empty<SnapGuide>();
        _sameSize.Clear();
        if (snap) { _sameSize.UnionWith(result.SameWidthIds); _sameSize.UnionWith(result.SameHeightIds); }
        InvalidateVisual();
        Editing?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None) return;
        e.Pointer.Capture(null);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverKeyId is not null && _drag == DragMode.None) { _hoverKeyId = null; InvalidateVisual(); }
    }

    /// <summary>Самопроверка: показать перетаскивание (направляющие, подсказку) без мыши. Геометрию потом вернуть <see cref="EndPreviewDrag"/>.</summary>
    internal void BeginPreviewDrag(string keyId, ResizeEdges edges, double dxPx, double dyPx)
    {
        var key = _layout?.Keys.FirstOrDefault(k => k.Id == keyId);
        var sticker = StickerRect();
        if (key is null || sticker.Width <= 0) return;
        SelectedKeyId = keyId;
        _drag = edges == ResizeEdges.None ? DragMode.Move : DragMode.Resize;
        _edges = edges;
        _origin = key.Bounds;
        _dragging = true;
        ApplyDrag(key, sticker, dxPx, dyPx, KeyModifiers.None);
    }

    internal void EndPreviewDrag()
    {
        if (_drag == DragMode.None || _layout?.Keys.FirstOrDefault(k => k.Id == _selectedKeyId) is not { } key) return;
        (key.X, key.Y, key.Width, key.Height) = (_origin.X, _origin.Y, _origin.Width, _origin.Height);
        _dragging = false; // без EditCompleted: изменений нет
        EndDrag();
    }

    private void EndDrag()
    {
        if (_drag == DragMode.None) return;
        bool changed = _dragging;
        _drag = DragMode.None;
        _dragging = false;
        _guides = Array.Empty<SnapGuide>();
        _sameSize.Clear();
        InvalidateVisual();
        if (changed) EditCompleted?.Invoke();
    }
}
