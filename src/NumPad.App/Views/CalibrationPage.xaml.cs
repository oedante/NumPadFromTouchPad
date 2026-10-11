using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NumPad.App.Input;
using NumPad.App.Services;
using NumPad.Core.Calibration;
using NumPad.Core.Config;
using NumPad.Core.Engine;
using NumPad.Core.Geometry;
using NumPad.Core.Keys;
using NumPad.Core.Layouts;

namespace NumPad.App.Views;

/// <summary>
/// Мастер калибровки. Работает с реальными кадрами тачпада: опорные точки → МНК → аффинное преобразование,
/// которое затем проверяется «сухим» экземпляром того же движка (KeyPressEngine без вывода клавиш).
/// </summary>
public partial class CalibrationPage : UserControl, IPage, IDisposable
{
    /// <summary>Вывод в никуда: проверка калибровки не должна нажимать клавиши в других программах.</summary>
    private sealed class DryRunKeyboard : IKeyboardOutput
    {
        public bool KeyDown(KeyDefinition key) => true;
        public bool KeyUp(KeyDefinition key) => true;
        public bool TypeText(string text) => true;
        public bool IsNumLockOn => true;
    }

    private enum Step { Prepare, Capture, Test }

    private readonly AppController _app;
    private Step _step = Step.Prepare;
    private CalibrationSession? _session;
    private CalibrationResult? _result;
    private AffineTransform? _candidate;
    private KeyPressEngine? _testEngine;
    private Layout _layout;
    private bool _loading;
    private bool _live;
    private readonly List<string> _recognized = new();

    public CalibrationPage(AppController app)
    {
        _app = app;
        _layout = app.ActiveLayout;
        InitializeComponent();
        ShowStep(Step.Prepare);
    }

    // ================================================================= IPage

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
        LoadPrepare();
    }

    public bool OnLeaving()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
        if (_step != Step.Prepare) ShowStep(Step.Prepare);
        return true;
    }

    public void Dispose()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
    }

    // ================================================================= шаг 1

    private TouchpadDeviceInfo? SelectedDevice => DeviceBox.SelectedItem as TouchpadDeviceInfo ?? _app.ActiveDevice;

    private void LoadPrepare()
    {
        _loading = true;
        var devices = _app.Touchpad.Devices.Where(d => d.IsUsable).ToList();
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.DeviceKey == _app.ActiveDevice?.DeviceKey) ?? devices.FirstOrDefault();
        LayoutBox.ItemsSource = _app.Layouts;
        LayoutBox.SelectedItem = _app.Layouts.FirstOrDefault(l => l.Id == _layout.Id) ?? _app.Layouts.FirstOrDefault(l => l.Id == _app.ActiveLayout.Id);
        if (LayoutBox.SelectedItem is Layout l) _layout = l;
        _loading = false;

        StartButton.IsEnabled = SelectedDevice is not null;
        var dev = SelectedDevice;
        if (dev is null)
        {
            CurrentCalibration.Text = _app.InputError ?? "Precision Touchpad не найден — калибровка невозможна.";
            return;
        }
        var cal = _app.Settings.Devices.TryGetValue(dev.DeviceKey, out var p) ? p.Calibration : new CalibrationData();
        var t = cal.ToTransform();
        var r = t.IsValid ? t.ToDeviceRect() : new RectD(0, 0, 1, 1);
        CurrentCalibration.Text = cal.IsIdentity
            ? "Сейчас калибровки нет: считается, что наклейка занимает весь тачпад."
            : FormattableString.CurrentCulture($"Текущая калибровка: наклейка занимает {r.Width * 100:0}% × {r.Height * 100:0}% тачпада, смещение {r.X * 100:0}% / {r.Y * 100:0}%, поворот {t.RotationDegrees:0.0}°")
              + (cal.CalibratedAtUtc is DateTime at ? $", выполнена {at.ToLocalTime():g}." : ".");
        LeftBox.Text = Num(r.Left * 100);
        TopBox.Text = Num(r.Top * 100);
        RightBox.Text = Num(r.Right * 100);
        BottomBox.Text = Num(r.Bottom * 100);
        ManualError.Text = "";
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) LoadPrepare(); }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LayoutBox.SelectedItem is not Layout l) return;
        _layout = l;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is null) return;
        _app.DisableMode("calibration");
        _session = new CalibrationSession(_layout);
        if (_session.Targets.Count < 3)
        {
            MessageBox.Show("В раскладке слишком мало клавиш для калибровки (нужно минимум 3).", "Калибровка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _session.PointCaptured += _ => Dispatcher.InvokeAsync(UpdateCapture);
        CaptureCanvas.Layout = _layout;
        ShowStep(Step.Capture);
        UpdateCapture();
    }

    private void OnTestCurrent(object sender, RoutedEventArgs e)
    {
        var dev = SelectedDevice;
        if (dev is null) return;
        var t = _app.Settings.Devices.TryGetValue(dev.DeviceKey, out var p) ? p.Calibration.ToTransform() : AffineTransform.Identity;
        _result = null;
        StartTest(t.IsValid ? t : AffineTransform.Identity, "Проверка текущей калибровки", "");
    }

    private void OnResetCalibration(object sender, RoutedEventArgs e)
    {
        var dev = SelectedDevice;
        if (dev is null) return;
        if (MessageBox.Show("Сбросить калибровку? Наклейка будет считаться занимающей весь тачпад.", "Калибровка",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.ResetCalibration(dev.DeviceKey);
        LoadPrepare();
    }

    private void OnManualApply(object sender, RoutedEventArgs e)
    {
        double? l = Parse(LeftBox.Text), t = Parse(TopBox.Text), r = Parse(RightBox.Text), b = Parse(BottomBox.Text);
        if (l is null || t is null || r is null || b is null) { ManualError.Text = "Введите четыре числа."; return; }
        if (r <= l + 5 || b <= t + 5) { ManualError.Text = "Правая граница должна быть больше левой, нижняя — больше верхней (минимум на 5%)."; return; }
        if (l < -20 || t < -20 || r > 120 || b > 120) { ManualError.Text = "Значения вне допустимого диапазона (−20…120%)."; return; }
        ManualError.Text = "";
        _app.DisableMode("calibration");
        var tr = AffineTransform.FromDeviceRect(RectD.FromEdges(l.Value / 100, t.Value / 100, r.Value / 100, b.Value / 100));
        _result = null;
        StartTest(tr, "Проверка заданных вручную границ", "Если клавиши распознаются верно — сохраните.");
    }

    // ================================================================= шаг 2

    private void UpdateCapture()
    {
        if (_session is null) return;
        if (_session.IsComplete)
        {
            _result = _session.Solve();
            if (!_result.Success)
            {
                MessageBox.Show(_result.Message, "Калибровка", MessageBoxButton.OK, MessageBoxImage.Warning);
                _session.Restart();
                UpdateCapture();
                return;
            }
            string details = FormattableString.CurrentCulture(
                $"Средняя погрешность {_result.RmsError * 100:0.0}%, максимальная {_result.MaxError * 100:0.0}% размера наклейки. Поворот {_result.Transform.RotationDegrees:0.0}°.");
            StartTest(_result.Transform, _result.Message, details);
            return;
        }
        var target = _session.CurrentTarget!;
        CaptureInstruction.Text = $"Коснитесь клавиши «{target.Label}» на наклейке и отпустите";
        CaptureProgress.Text = $"Точка {_session.CurrentIndex + 1} из {_session.Targets.Count}";
        CaptureCanvas.TargetKeyId = target.KeyId;
        CaptureCanvas.Marks = _session.Points.Select(p => p.Target).ToList();
    }

    private void OnUndoPoint(object sender, RoutedEventArgs e) { _session?.UndoLast(); UpdateCapture(); }
    private void OnRestart(object sender, RoutedEventArgs e) { _session?.Restart(); UpdateCapture(); }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _session = null;
        _testEngine = null;
        ShowStep(Step.Prepare);
        LoadPrepare();
    }

    // ================================================================= шаг 3

    private void StartTest(AffineTransform transform, string title, string details)
    {
        _candidate = transform;
        _testEngine = new KeyPressEngine(new DryRunKeyboard(), _layout, transform,
            new EngineOptions { AutoRepeat = false, PressTrigger = PressTrigger.OnTouchDown });
        _testEngine.EventRaised += ev =>
        {
            if (ev.Kind != EngineEventKind.KeyPressed || ev.ZoneId is null) return;
            var label = _layout.Keys.FirstOrDefault(k => k.Id == ev.ZoneId)?.Label ?? ev.ZoneId;
            _recognized.Add(label);
            if (_recognized.Count > 16) _recognized.RemoveAt(0);
        };
        _recognized.Clear();
        TestCanvas.Layout = _layout;
        ResultTitle.Text = title;
        ResultDetails.Text = details;
        bool poor = _result is { MaxError: > 0.12 };
        ResultTitle.Foreground = poor ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("TextBrush");
        TestLog.Text = "Распознано: —";
        ShowStep(Step.Test);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dev = SelectedDevice;
        if (dev is null || _candidate is null) return;
        var data = CalibrationData.FromTransform(_candidate, _result?.RmsError, _layout.Id, _session?.Points);
        _app.SaveCalibration(dev.DeviceKey, dev.DisplayName, data);
        if (_app.ActiveLayout.Id != _layout.Id &&
            MessageBox.Show($"Сделать раскладку «{_layout.Name}» активной?", "Калибровка", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _app.SetActiveLayout(_layout.Id);
        if (dev.DeviceKey != _app.ActiveDevice?.DeviceKey) _app.SelectDevice(dev.DeviceKey);
        _session = null;
        _testEngine = null;
        ShowStep(Step.Prepare);
        LoadPrepare();
        MessageBox.Show("Калибровка сохранена и применена.", "Калибровка", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ================================================================= кадры

    private void OnFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        if (SelectedDevice?.Handle != device.Handle) return;
        switch (_step)
        {
            case Step.Capture when _session is not null:
                _session.OnFrame(frame);
                CaptureCanvas.Contacts = frame.Contacts.Where(c => c.Tip)
                    .Select(c => new ActiveContactInfo(c.Id, Approx(c.Position), null, false, false)).ToList();
                break;
            case Step.Test when _testEngine is not null:
                _testEngine.ProcessFrame(frame);
                _testEngine.Tick(frame.TimestampMs);
                TestCanvas.ActiveKeyIds.Clear();
                foreach (var c in _testEngine.ActiveContacts.Where(c => c.ZoneId is not null)) TestCanvas.ActiveKeyIds.Add(c.ZoneId!);
                TestCanvas.Contacts = _testEngine.ActiveContacts;
                TestLog.Text = _recognized.Count == 0 ? "Распознано: —" : "Распознано: " + string.Join("  ", _recognized);
                break;
        }
    }

    /// <summary>Во время сбора точек калибровки ещё нет — показываем касание через текущую калибровку (приблизительно).</summary>
    private PointD Approx(PointD device) => _app.Engine.ToArea(device);

    // ================================================================= служебное

    private void ShowStep(Step step)
    {
        _step = step;
        PrepareStep.Visibility = step == Step.Prepare ? Visibility.Visible : Visibility.Collapsed;
        CaptureStep.Visibility = step == Step.Capture ? Visibility.Visible : Visibility.Collapsed;
        TestStep.Visibility = step == Step.Test ? Visibility.Visible : Visibility.Collapsed;
        var on = (Brush)FindResource("AccentBrush");
        var off = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE0));
        Step1Badge.Background = step == Step.Prepare ? on : off;
        Step2Badge.Background = step == Step.Capture ? on : off;
        Step3Badge.Background = step == Step.Test ? on : off;
        if (step != Step.Capture) { CaptureCanvas.Contacts = Array.Empty<ActiveContactInfo>(); }
        if (step != Step.Test) { TestCanvas.Contacts = Array.Empty<ActiveContactInfo>(); TestCanvas.ActiveKeyIds.Clear(); }
    }

    private static string Num(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);

    private static double? Parse(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}
