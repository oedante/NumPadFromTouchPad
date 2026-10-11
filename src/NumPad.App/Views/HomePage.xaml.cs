using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NumPad.App.Input;
using NumPad.App.Services;
using NumPad.Core.Engine;
using NumPad.Core.Layouts;

namespace NumPad.App.Views;

public partial class HomePage : UserControl, IPage, IDisposable
{
    private readonly AppController _app;
    private readonly MainWindow _shell;
    private bool _updating;
    private bool _live;

    public HomePage(AppController app, MainWindow shell)
    {
        _app = app;
        _shell = shell;
        InitializeComponent();
        _app.StateChanged += Refresh;
    }

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
        Refresh();
    }

    public bool OnLeaving()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
        return true;
    }

    public void Dispose()
    {
        OnLeaving();
        _app.StateChanged -= Refresh;
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            bool on = _app.Mode.IsNumPad;
            ModeBadge.Background = on ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE0));
            ModeBadgeText.Text = on ? "ВКЛ" : "ВЫКЛ";
            ModeTitle.Text = on ? "Режим NumPad включён" : "Режим NumPad выключен";
            ModeHint.Text = on
                ? "Касания зон тачпада генерируют нажатия клавиш. Курсор в зоне клавиш заблокирован."
                : "Тачпад работает как обычно.";
            ModeButton.Content = on ? "Выключить NumPad" : "Включить NumPad";

            ErrorCard.Visibility = _app.InputError is null ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Text = _app.InputError ?? "";

            var devices = _app.Touchpad.Devices.ToList();
            DeviceBox.ItemsSource = devices;
            var active = _app.ActiveDevice;
            DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.DeviceKey == active?.DeviceKey);
            DeviceStatus.Text = active is null
                ? "Precision Touchpad не найден"
                : $"{active.DeviceKey} · контактов: {active.MaxContacts}" + (active.AspectIsPhysical
                    ? FormattableString.Invariant($" · {active.WidthMm:0}×{active.HeightMm:0} мм") : "");
            foreach (var bad in devices.Where(d => !d.IsUsable))
                DeviceStatus.Text += $"\n{bad.DisplayName}: {bad.ParserError}";

            LayoutBox.ItemsSource = _app.Layouts;
            LayoutBox.SelectedItem = _app.Layouts.FirstOrDefault(l => l.Id == _app.ActiveLayout.Id);
            var cal = _app.ActiveProfile?.Calibration;
            CalibrationStatus.Text = cal is null || cal.IsIdentity
                ? "Калибровка не выполнена: наклейка = весь тачпад"
                : cal.RmsError is double e
                    ? FormattableString.Invariant($"Откалибровано, погрешность {e * 100:0.0}% ширины")
                    : "Границы заданы вручную";

            HotkeyText.Text = _app.CurrentHotkeyText;
            HotkeyStatus.Text = _app.HotkeyError ?? (_app.HotkeyRegistered ? "Работает в любой программе" : "Не зарегистрирована");
            HotkeyStatus.Foreground = _app.HotkeyError is null ? (Brush)FindResource("SubtleTextBrush") : (Brush)FindResource("ErrorBrush");

            Preview.Layout = _app.ActiveLayout;

            WarningsList.ItemsSource = _app.Warnings.ToList();
            WarningsCard.Visibility = _app.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updating = false; }
    }

    private void OnFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        if (_app.ActiveDevice?.Handle != device.Handle) return;
        Preview.ActiveKeyIds.Clear();
        if (_app.Mode.IsNumPad)
        {
            var contacts = _app.Engine.ActiveContacts;
            foreach (var c in contacts.Where(c => c.ZoneId is not null && !c.Ignored)) Preview.ActiveKeyIds.Add(c.ZoneId!);
            Preview.Contacts = contacts;
        }
        else
        {
            // В обычном режиме — только положение пальцев (через текущую калибровку), без нажатий.
            var hit = new HitTester(_app.ActiveLayout);
            var list = frame.Contacts.Where(c => c.Tip).Select(c =>
            {
                var p = _app.Engine.ToArea(c.Position);
                return new ActiveContactInfo(c.Id, p, hit.Hit(p)?.Id, false, true);
            }).ToList();
            Preview.Contacts = list;
        }
    }

    private void OnToggle(object sender, RoutedEventArgs e) => _app.ToggleMode("button");

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || DeviceBox.SelectedItem is not TouchpadDeviceInfo d) return;
        _app.SelectDevice(d.DeviceKey);
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || LayoutBox.SelectedItem is not Layout l) return;
        _app.SetActiveLayout(l.Id);
    }

    private void OnOpenCalibration(object sender, RoutedEventArgs e) => _shell.Navigate("calibration");
    private void OnOpenEditor(object sender, RoutedEventArgs e) => _shell.Navigate("layout");

    private void OnDismissWarnings(object sender, RoutedEventArgs e)
    {
        _app.Warnings.Clear();
        Refresh();
    }
}
