using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NumPad.Core.Engine;
using NumPad.Core.Geometry;
using NumPad.Core.Layouts;

namespace NumPad.App.Controls;

/// <summary>
/// Отрисовка раскладки (наклейки) с клавишами, касаниями и целями калибровки.
/// В режиме редактирования — выделение, перемещение и изменение размера клавиш мышью.
/// Координаты модели — нормализованные 0..1 области наклейки; на экране сохраняется соотношение сторон.
/// </summary>
public sealed class LayoutCanvas : FrameworkElement
{
    private const double Pad = 12;
    private const double HandleSize = 12;

    private static readonly Brush StickerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE0)));
    private static readonly Brush KeyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush KeyDisabledBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEA)));
    private static readonly Brush KeyActiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x64)));
    private static readonly Brush KeyHighlightBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD9, 0x64)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)));
    private static readonly Brush SubtleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x86)));
    private static readonly Brush ContactBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x32, 0x32, 0x32)));
    private static readonly Brush ContactIgnoredBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xC0, 0x39, 0x2B)));
    private static readonly Brush TargetBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)));
    private static readonly Pen KeyPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC2)), 1));
    private static readonly Pen DisabledPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xAA)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen SelectedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32)), 2));
    private static readonly Pen StickerPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC2)), 1));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 0.5));
    private static readonly Pen TargetPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), 2));
    private static readonly Pen PointerAreaPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x94)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Typeface Face = new("Segoe UI");

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public LayoutCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    // ------------------------------------------------------------------ свойства

    public Layout? Layout
    {
        get => _layout;
        set { _layout = value; InvalidateVisual(); }
    }
    private Layout? _layout;

    public string? SelectedKeyId
    {
        get => _selectedKeyId;
        set { if (_selectedKeyId != value) { _selectedKeyId = value; InvalidateVisual(); SelectionChanged?.Invoke(value); } }
    }
    private string? _selectedKeyId;

    /// <summary>Касания в координатах наклейки.</summary>
    public IReadOnlyList<ActiveContactInfo> Contacts
    {
        get => _contacts;
        set { _contacts = value ?? Array.Empty<ActiveContactInfo>(); InvalidateVisual(); }
    }
    private IReadOnlyList<ActiveContactInfo> _contacts = Array.Empty<ActiveContactInfo>();

    /// <summary>Клавиши, которые нужно подсветить (нажаты / распознаны).</summary>
    public ISet<string> ActiveKeyIds { get; } = new HashSet<string>();
    /// <summary>Клавиша-цель калибровки.</summary>
    public string? TargetKeyId { get => _targetKeyId; set { _targetKeyId = value; InvalidateVisual(); } }
    private string? _targetKeyId;
    /// <summary>Отметки уже собранных опорных точек (координаты наклейки).</summary>
    public IReadOnlyList<PointD> Marks { get => _marks; set { _marks = value ?? Array.Empty<PointD>(); InvalidateVisual(); } }
    private IReadOnlyList<PointD> _marks = Array.Empty<PointD>();

    public bool IsEditable { get; set; }
    public bool ShowGrid { get => _showGrid; set { _showGrid = value; InvalidateVisual(); } }
    private bool _showGrid;
    /// <summary>Шаг сетки привязки в долях ширины/высоты.</summary>
    public double GridStep { get; set; } = 1.0 / 48;

    public event Action<string?>? SelectionChanged;
    /// <summary>Перед изменением геометрии (для снимка отмены).</summary>
    public event Action? EditStarting;
    /// <summary>После завершения перетаскивания.</summary>
    public event Action? EditCompleted;
    /// <summary>Во время перетаскивания (для обновления полей свойств).</summary>
    public event Action? Editing;

    public void Refresh() => InvalidateVisual();

    // ------------------------------------------------------------------ геометрия

    private Rect StickerRect()
    {
        double aspect = _layout?.AspectRatio is > 0 ? _layout.AspectRatio : 1.6;
        double w = Math.Max(0, ActualWidth - 2 * Pad), h = Math.Max(0, ActualHeight - 2 * Pad);
        if (w <= 0 || h <= 0) return Rect.Empty;
        double sw = w, sh = w / aspect;
        if (sh > h) { sh = h; sw = h * aspect; }
        return new Rect(Pad + (w - sw) / 2, Pad + (h - sh) / 2, sw, sh);
    }

    private static Rect ToScreen(Rect sticker, RectD r) =>
        new(sticker.X + r.X * sticker.Width, sticker.Y + r.Y * sticker.Height, r.Width * sticker.Width, r.Height * sticker.Height);

    private static Point ToScreen(Rect sticker, PointD p) => new(sticker.X + p.X * sticker.Width, sticker.Y + p.Y * sticker.Height);

    private static PointD ToModel(Rect sticker, Point p) =>
        new((p.X - sticker.X) / sticker.Width, (p.Y - sticker.Y) / sticker.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height;
        return new Size(w, h);
    }

    // ------------------------------------------------------------------ отрисовка

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // попадание мыши по всей площади
        var sticker = StickerRect();
        if (sticker.IsEmpty) return;
        dc.DrawRoundedRectangle(StickerBrush, StickerPen, sticker, 10, 10);
        if (_layout is null) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_showGrid && IsEditable)
        {
            for (double x = GridStep; x < 1; x += GridStep * 2)
                dc.DrawLine(GridPen, ToScreen(sticker, new PointD(x, 0)), ToScreen(sticker, new PointD(x, 1)));
            for (double y = GridStep; y < 1; y += GridStep * 2)
                dc.DrawLine(GridPen, ToScreen(sticker, new PointD(0, y)), ToScreen(sticker, new PointD(1, y)));
        }

        if (_layout.AllowPointerOutsideKeys && _layout.KeyBounds() is { } kb)
            dc.DrawRoundedRectangle(null, PointerAreaPen, ToScreen(sticker, kb), 6, 6);

        double unit = Math.Min(sticker.Width, sticker.Height);
        foreach (var k in _layout.Keys)
        {
            var r = ToScreen(sticker, k.Bounds);
            if (r.Width < 1 || r.Height < 1) continue;
            bool active = ActiveKeyIds.Contains(k.Id);
            bool target = k.Id == _targetKeyId;
            Brush fill = !k.Enabled ? KeyDisabledBrush : active ? KeyActiveBrush : target ? KeyHighlightBrush : KeyBrush;
            Pen pen = k.Id == _selectedKeyId && IsEditable ? SelectedPen : target ? TargetPen : k.Enabled ? KeyPen : DisabledPen;
            double radius = Math.Min(6, Math.Min(r.Width, r.Height) / 5);
            dc.DrawRoundedRectangle(fill, pen, r, radius, radius);

            string label = string.IsNullOrEmpty(k.Label) ? "?" : k.Label;
            double fontSize = Math.Clamp(Math.Min(r.Height * 0.38, unit * 0.06), 7, 22);
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, fontSize,
                k.Enabled ? TextBrush : SubtleBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, r.Width - 4),
                MaxLineCount = 2,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(ft, new Point(r.X + 2, r.Y + (r.Height - ft.Height) / 2));

            if (IsEditable && k.Id == _selectedKeyId)
                dc.DrawRectangle(Brushes.White, SelectedPen, new Rect(r.Right - HandleSize / 2, r.Bottom - HandleSize / 2, HandleSize, HandleSize));
        }

        foreach (var m in _marks)
        {
            var p = ToScreen(sticker, m);
            dc.DrawEllipse(null, TargetPen, p, 5, 5);
        }

        if (_targetKeyId is not null && _layout.Keys.FirstOrDefault(k => k.Id == _targetKeyId) is { } tk)
        {
            var c = ToScreen(sticker, tk.Bounds.Center);
            dc.DrawLine(TargetPen, new Point(c.X - 10, c.Y), new Point(c.X + 10, c.Y));
            dc.DrawLine(TargetPen, new Point(c.X, c.Y - 10), new Point(c.X, c.Y + 10));
            dc.DrawEllipse(null, TargetPen, c, 14, 14);
        }

        foreach (var c in _contacts)
        {
            var p = ToScreen(sticker, c.AreaPosition);
            dc.DrawEllipse(c.Ignored ? ContactIgnoredBrush : ContactBrush, null, p, 9, 9);
        }
    }

    // ------------------------------------------------------------------ редактирование

    private enum DragMode { None, Move, Resize }
    private DragMode _drag;
    private PointD _dragStart;
    private RectD _origin;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (!IsEditable || _layout is null) return;
        var sticker = StickerRect();
        if (sticker.IsEmpty) return;
        var pos = e.GetPosition(this);
        var model = ToModel(sticker, pos);

        // Сначала — ручка изменения размера выделенной клавиши.
        var selected = _layout.Keys.FirstOrDefault(k => k.Id == _selectedKeyId);
        if (selected is not null)
        {
            var r = ToScreen(sticker, selected.Bounds);
            if (Math.Abs(pos.X - r.Right) <= HandleSize && Math.Abs(pos.Y - r.Bottom) <= HandleSize)
            {
                BeginDrag(DragMode.Resize, selected, model);
                e.Handled = true;
                return;
            }
        }

        // Верхняя (последняя отрисованная) клавиша под курсором.
        var hit = _layout.Keys.LastOrDefault(k => k.Bounds.Contains(model));
        SelectedKeyId = hit?.Id;
        if (hit is not null) BeginDrag(DragMode.Move, hit, model);
        e.Handled = true;
    }

    private void BeginDrag(DragMode mode, KeyZone key, PointD model)
    {
        EditStarting?.Invoke();
        _drag = mode;
        _dragStart = model;
        _origin = key.Bounds;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_layout is null) return;
        var sticker = StickerRect();
        if (sticker.IsEmpty) return;
        var pos = e.GetPosition(this);
        var model = ToModel(sticker, pos);

        if (_drag == DragMode.None)
        {
            if (!IsEditable) { Cursor = null; return; }
            var sel = _layout.Keys.FirstOrDefault(k => k.Id == _selectedKeyId);
            if (sel is not null)
            {
                var r = ToScreen(sticker, sel.Bounds);
                if (Math.Abs(pos.X - r.Right) <= HandleSize && Math.Abs(pos.Y - r.Bottom) <= HandleSize) { Cursor = Cursors.SizeNWSE; return; }
            }
            Cursor = _layout.Keys.Any(k => k.Bounds.Contains(model)) ? Cursors.SizeAll : null;
            return;
        }

        var key = _layout.Keys.FirstOrDefault(k => k.Id == _selectedKeyId);
        if (key is null) return;
        double dx = model.X - _dragStart.X, dy = model.Y - _dragStart.Y;
        if (_drag == DragMode.Move)
        {
            key.X = Math.Clamp(Snap(_origin.X + dx), 0, 1 - key.Width);
            key.Y = Math.Clamp(Snap(_origin.Y + dy), 0, 1 - key.Height);
        }
        else
        {
            key.Width = Math.Clamp(Snap(_origin.Right + dx) - key.X, LayoutValidator.MinKeySize, 1 - key.X);
            key.Height = Math.Clamp(Snap(_origin.Bottom + dy) - key.Y, LayoutValidator.MinKeySize, 1 - key.Y);
        }
        key.X = Math.Round(key.X, 5); key.Y = Math.Round(key.Y, 5);
        key.Width = Math.Round(key.Width, 5); key.Height = Math.Round(key.Height, 5);
        InvalidateVisual();
        Editing?.Invoke();
    }

    private double Snap(double v) => _showGrid && GridStep > 0 ? Math.Round(v / GridStep) * GridStep : v;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        ReleaseMouseCapture();
        EditCompleted?.Invoke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragMode.None)
        {
            _drag = DragMode.None;
            EditCompleted?.Invoke();
        }
    }
}
