using Avalonia.Controls;
using Avalonia.Media;
using NumPad.App.Services;
using NumPad.Core.Engine;
using NumPad.Core.Layouts;
using NumPad.Platform;

namespace NumPad.App.Views;

/// <summary>Элемент выпадающего списка с подписью (ComboBox показывает ToString()).</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public partial class HomePage : UserControl, IPage, IDisposable
{
    private readonly AppController _app;
    private readonly MainWindow _shell;
    private bool _updating;
    private bool _live;

    public HomePage() : this(null!, null!) { }

    public HomePage(AppController app, MainWindow shell)
    {
        _app = app;
        _shell = shell;
        InitializeComponent();
        if (app is null) return;
        _app.StateChanged += Refresh;
        ModeButton.Click += (_, _) => _app.ToggleMode("button");
        OpenCalibration.Click += async (_, _) => await _shell.NavigateAsync("calibration");
        OpenEditor.Click += async (_, _) => await _shell.NavigateAsync("layout");
        DismissWarnings.Click += (_, _) => { _app.Warnings.Clear(); Refresh(); };
        DeviceBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && DeviceBox.SelectedItem is Choice<TouchpadDeviceInfo> d) _app.SelectDevice(d.Value.DeviceKey);
        };
        LayoutBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && LayoutBox.SelectedItem is Choice<Layout> l) _app.SetActiveLayout(l.Value.Id);
        };
    }

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
        Refresh();
    }

    public Task<bool> CanLeaveAsync() => Task.FromResult(true);

    public void OnHidden()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
    }

    public void Dispose()
    {
        OnHidden();
        _app.StateChanged -= Refresh;
    }

    private static IBrush Res(string key) => Ui.Brush(key);

    private void Refresh()
    {
        _updating = true;
        try
        {
            bool on = _app.Mode.IsNumPad;
            ModeBadge.Background = on ? Res("AccentBrush") : Res("BadgeOffBrush");
            ModeBadgeText.Text = on ? "ВКЛ" : "ВЫКЛ";
            ModeTitle.Text = on ? "Режим NumPad включён" : "Режим NumPad выключен";
            ModeHint.Text = on
                ? "Касания зон тачпада генерируют нажатия клавиш; курсор в зоне клавиш заблокирован."
                : "Тачпад работает как обычно.";
            ModeButton.Content = on ? "Выключить NumPad" : "Включить NumPad";

            var problems = new List<string>();
            if (_app.InputError is not null) problems.Add(_app.InputError);
            problems.AddRange(_app.Platform.Problems.Where(p => p != _app.InputError));
            ErrorCard.IsVisible = problems.Count > 0;
            ErrorText.Text = string.Join(Environment.NewLine + Environment.NewLine, problems);

            var devices = _app.Platform.Touchpad.Devices.Select(d => new Choice<TouchpadDeviceInfo>(d, d.DisplayName)).ToList();
            DeviceBox.ItemsSource = devices;
            var active = _app.ActiveDevice;
            DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Value.Id == active?.Id);
            DeviceStatus.Text = active is null
                ? "Тачпад не найден"
                : $"{active.DeviceKey} · контактов: {active.MaxContacts}" + (active.AspectIsPhysical
                    ? FormattableString.Invariant($" · {active.WidthMm:0}×{active.HeightMm:0} мм") : "");
            foreach (var bad in devices.Where(d => !d.Value.IsUsable))
                DeviceStatus.Text += $"\n{bad.Value.DisplayName}: {bad.Value.Error}";

            var layouts = _app.Layouts.Select(l => new Choice<Layout>(l, l.Name)).ToList();
            LayoutBox.ItemsSource = layouts;
            LayoutBox.SelectedItem = layouts.FirstOrDefault(l => l.Value.Id == _app.ActiveLayout.Id);
            var cal = _app.ActiveProfile?.Calibration;
            CalibrationStatus.Text = cal is null || cal.IsIdentity
                ? "Калибровка не выполнена: наклейка = весь тачпад"
                : cal.RmsError is double e
                    ? FormattableString.Invariant($"Откалибровано, погрешность {e * 100:0.0}% размера")
                    : "Границы заданы вручную";

            HotkeyText.Text = _app.CurrentHotkeyText;
            HotkeyStatus.Text = _app.HotkeyError ?? (_app.HotkeyRegistered ? _app.Platform.Hotkey.Limitation ?? "Работает в любой программе" : "Не назначена");
            HotkeyStatus.Foreground = _app.HotkeyError is null ? Res("SubtleTextBrush") : Res("ErrorBrush");

            Preview.Layout = _app.ActiveLayout;
            WarningsText.Text = string.Join(Environment.NewLine, _app.Warnings);
            WarningsCard.IsVisible = _app.Warnings.Count > 0;
        }
        finally { _updating = false; }
    }

    private void OnFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        if (_app.ActiveDevice?.Id != device.Id) return;
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
            Preview.Contacts = frame.Contacts.Where(c => c.Tip).Select(c =>
            {
                var p = _app.Engine.ToArea(c.Position);
                return new ActiveContactInfo(c.Id, p, hit.Hit(p)?.Id, false, true);
            }).ToList();
        }
    }
}
