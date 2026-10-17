using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Ogneliss.NumTouch.App.Views;
using Ogneliss.NumTouch.Core.Engine;

namespace Ogneliss.NumTouch.App.Services;

public enum OsdKind { On, Off, Error }

/// <summary>
/// Экранное уведомление о смене режима (как индикатор громкости): окно без рамки поверх остальных, не забирает
/// фокус и не появляется в панели задач, само исчезает через ~1,8 с. Своё окно, а не системные уведомления:
/// одинаково работает на Windows, Linux и macOS, не попадает в центр уведомлений и не требует разрешений.
/// </summary>
public sealed class ModeNotifier : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1800);
    private readonly AppController _controller;
    private readonly DispatcherTimer _hideTimer;
    private OsdWindow? _osd;

    public ModeNotifier(AppController controller)
    {
        _controller = controller;
        _hideTimer = new DispatcherTimer { Interval = Duration };
        _hideTimer.Tick += (_, _) => Hide();
        _controller.ModeChanged += OnModeChanged;
    }

    /// <summary>Уведомление видно сейчас (для самопроверки).</summary>
    public bool IsShowing => _osd?.IsVisible == true;

    private void OnModeChanged(InputMode mode, string reason)
    {
        if (!_controller.Settings.ShowNotifications || reason == "application exit") return;
        var (title, subtitle, kind) = Describe(mode, reason);
        Show(title, subtitle, kind);
    }

    private (string Title, string? Subtitle, OsdKind Kind) Describe(InputMode mode, string reason)
    {
        string? hotkey = _controller.HotkeyRegistered ? _controller.CurrentHotkeyText : null;
        if (reason == "no device")
            return (L.T("Touchpad not found"), _controller.InputError, OsdKind.Error);
        if (mode == InputMode.NumPad)
            return (L.T("NumPad on"),
                _controller.ActiveLayout.Name + (hotkey is null ? "" : " · " + L.F("{0} — turn off", hotkey)),
                OsdKind.On);
        string? why = reason switch
        {
            "suspend" => L.T("Turned off before sleep"),
            "session lock" => L.T("Turned off: the computer was locked"),
            "engine error" => L.T("Turned off because of an error — see Diagnostics"),
            _ => hotkey is null ? null : L.F("{0} — turn on", hotkey),
        };
        return (L.T("NumPad off"), why, reason == "engine error" ? OsdKind.Error : OsdKind.Off);
    }

    public void Show(string title, string? subtitle, OsdKind kind)
    {
        try
        {
            _osd ??= new OsdWindow();
            _osd.ShowMessage(title, subtitle, kind);
            _hideTimer.Stop();
            _hideTimer.Start();
        }
        catch (Exception ex)
        {
            // Уведомление — не критичная функция: ошибка оконной системы не должна мешать переключению режима.
            _controller.Log.Warn("Mode notification", ex);
        }
    }

    public void Hide()
    {
        _hideTimer.Stop();
        try { _osd?.Hide(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        _controller.ModeChanged -= OnModeChanged;
        _hideTimer.Stop();
        try { _osd?.Close(); } catch { /* ignore */ }
        _osd = null;
    }
}

/// <summary>Окно уведомления: тёмная плашка внизу по центру основного экрана.</summary>
internal sealed class OsdWindow : Window
{
    private readonly TextBlock _title = new()
    {
        FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
    };
    private readonly TextBlock _subtitle = new()
    {
        FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
        TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
    };
    private readonly Border _dot = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };

    public OsdWindow()
    {
        Title = "Ogneliss NumTouch";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Content = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x26, 0x26)),
            Padding = new Thickness(22, 14, 26, 14),
            MinWidth = 240,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Children =
                {
                    _dot,
                    new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } },
                },
            },
        };
        PointerPressed += (_, _) => Hide(); // щелчок по плашке убирает её сразу
    }

    public void ShowMessage(string title, string? subtitle, OsdKind kind)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(subtitle);
        _dot.Background = kind switch
        {
            OsdKind.On => Ui.Brush("OnBrush"),
            OsdKind.Error => Ui.Brush("ErrorBrush"),
            _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
        };
        PlaceOnScreen();
        if (!IsVisible) Show();
    }

    /// <summary>Внизу по центру рабочей области основного экрана, над панелью задач / доком.</summary>
    private void PlaceOnScreen()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        Measure(Size.Infinity);
        double scale = screen.Scaling;
        var wa = screen.WorkingArea;
        int w = (int)Math.Ceiling(DesiredSize.Width * scale);
        int h = (int)Math.Ceiling(DesiredSize.Height * scale);
        Position = new PixelPoint(wa.X + Math.Max(0, (wa.Width - w) / 2), wa.Bottom - h - (int)(56 * scale));
    }
}
