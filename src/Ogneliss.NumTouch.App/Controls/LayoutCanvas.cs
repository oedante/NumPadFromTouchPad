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
/// перемещение и изменение размера клавиш мышью. Модель в нормализованных координатах 0..1 области наклейки,
/// на экране сохраняется соотношение сторон раскладки.
/// </summary>
public sealed class LayoutCanvas : Control
{
    private const double Pad = 12;
    private const double HandleSize = 12;

    private static readonly IBrush StickerBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE0));
    private static readonly IBrush KeyBrush = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush KeyDisabledBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEA));
    private static readonly IBrush KeyActiveBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x64));
    private static readonly IBrush KeyTargetBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD9, 0x64));
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly IBrush SubtleBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x86));
    private static readonly IBrush ContactBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x32, 0x32, 0x32));
    private static readonly IBrush ContactIgnoredBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0xC0, 0x39, 0x2B));
    private static readonly IPen KeyPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC2)), 1);
    private static readonly IPen DisabledPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xAA)), 1, new ImmutableDashStyle(new double[] { 3, 2 }, 0));
    private static readonly IPen SelectedPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32)), 2);
    private static readonly IPen StickerPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC2)), 1);
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 0.5);
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

    public bool IsEditable { get; set; }
    private bool _showGrid;
    public bool ShowGrid { get => _showGrid; set { _showGrid = value; InvalidateVisual(); } }
    /// <summary>Шаг сетки привязки в долях ширины/высоты.</summary>
    public double GridStep { get; set; } = 1.0 / 48;

    public event Action<string?>? SelectionChanged;
    /// <summary>Перед изменением геометрии (снимок для отмены).</summary>
    public event Action? EditStarting;
    /// <summary>Во время перетаскивания.</summary>
    public event Action? Editing;
    /// <summary>После завершения перетаскивания.</summary>
    public event Action? EditCompleted;

    public void Refresh() => InvalidateVisual();

    // ------------------------------------------------------------------ геометрия

    private Rect StickerRect()
    {
        double aspect = _layout is { AspectRatio: > 0 } l ? l.AspectRatio : 1.6;
        double w = Math.Max(0, Bounds.Width - 2 * Pad), h = Math.Max(0, Bounds.Height - 2 * Pad);
        if (w <= 0 || h <= 0) return default;
        double sw = w, sh = w / aspect;
        if (sh > h) { sh = h; sw = h * aspect; }
        return new Rect(Pad + (w - sw) / 2, Pad + (h - sh) / 2, sw, sh);
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

        if (_showGrid && IsEditable && GridStep > 0)
        {
            for (double x = GridStep * 2; x < 1; x += GridStep * 2)
                dc.DrawLine(GridPen, ToScreen(sticker, new PointD(x, 0)), ToScreen(sticker, new PointD(x, 1)));
            for (double y = GridStep * 2; y < 1; y += GridStep * 2)
                dc.DrawLine(GridPen, ToScreen(sticker, new PointD(0, y)), ToScreen(sticker, new PointD(1, y)));
        }

        if (_layout.AllowPointerOutsideKeys && _layout.KeyBounds() is { } kb)
            dc.DrawRectangle(null, PointerAreaPen, ToScreen(sticker, kb), 6, 6);

        double unit = Math.Min(sticker.Width, sticker.Height);
        foreach (var k in _layout.Keys)
        {
            var r = ToScreen(sticker, k.Bounds);
            if (r.Width < 1 || r.Height < 1) continue;
            bool active = ActiveKeyIds.Contains(k.Id);
            bool target = k.Id == _targetKeyId;
            IBrush fill = !k.Enabled ? KeyDisabledBrush : active ? KeyActiveBrush : target ? KeyTargetBrush : KeyBrush;
            IPen pen = k.Id == _selectedKeyId && IsEditable ? SelectedPen : target ? TargetPen : k.Enabled ? KeyPen : DisabledPen;
            double radius = Math.Min(6, Math.Min(r.Width, r.Height) / 5);
            dc.DrawRectangle(fill, pen, r, radius, radius);

            string label = string.IsNullOrEmpty(k.Label) ? "?" : k.Label;
            double fontSize = Math.Clamp(Math.Min(r.Height * 0.38, unit * 0.06), 7, 22);
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, fontSize,
                k.Enabled ? TextBrush : SubtleBrush)
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

    // ------------------------------------------------------------------ редактирование

    private enum DragMode { None, Move, Resize }
    private DragMode _drag;
    private PointD _dragStart;
    private RectD _origin;

    private bool OnHandle(Rect sticker, KeyZone key, Point pos)
    {
        var r = ToScreen(sticker, key.Bounds);
        return Math.Abs(pos.X - r.Right) <= HandleSize && Math.Abs(pos.Y - r.Bottom) <= HandleSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!IsEditable || _layout is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var sticker = StickerRect();
        if (sticker.Width <= 0) return;
        var pos = e.GetPosition(this);
        var model = ToModel(sticker, pos);

        var selected = _layout.Keys.FirstOrDefault(k => k.Id == _selectedKeyId);
        if (selected is not null && OnHandle(sticker, selected, pos))
        {
            BeginDrag(DragMode.Resize, selected, model, e);
            return;
        }
        var hit = _layout.Keys.LastOrDefault(k => k.Bounds.Contains(model));
        SelectedKeyId = hit?.Id;
        if (hit is not null) BeginDrag(DragMode.Move, hit, model, e);
        e.Handled = true;
    }

    private void BeginDrag(DragMode mode, KeyZone key, PointD model, PointerPressedEventArgs e)
    {
        EditStarting?.Invoke();
        _drag = mode;
        _dragStart = model;
        _origin = key.Bounds;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_layout is null) return;
        var sticker = StickerRect();
        if (sticker.Width <= 0) return;
        var pos = e.GetPosition(this);
        var model = ToModel(sticker, pos);

        if (_drag == DragMode.None)
        {
            if (!IsEditable) return;
            var sel = _layout.Keys.FirstOrDefault(k => k.Id == _selectedKeyId);
            if (sel is not null && OnHandle(sticker, sel, pos)) { Cursor = new Cursor(StandardCursorType.BottomRightCorner); return; }
            Cursor = _layout.Keys.Any(k => k.Bounds.Contains(model)) ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        EditCompleted?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        EditCompleted?.Invoke();
    }
}
