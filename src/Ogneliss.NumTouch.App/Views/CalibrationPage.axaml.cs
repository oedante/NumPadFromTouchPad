using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Ogneliss.NumTouch.App.Services;
using Ogneliss.NumTouch.Core.Calibration;
using Ogneliss.NumTouch.Core.Config;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Keys;
using Ogneliss.NumTouch.Core.Layouts;
using Ogneliss.NumTouch.Platform;

namespace Ogneliss.NumTouch.App.Views;

/// <summary>
/// Мастер калибровки на реальных кадрах тачпада: опорные клавиши → МНК → аффинное преобразование, которое затем
/// проверяется «вхолостую» тем же движком (KeyPressEngine без вывода клавиш).
/// </summary>
public partial class CalibrationPage : UserControl, IPage, IDisposable
{
    /// <summary>Вывод в никуда: проверка калибровки не нажимает клавиши в других программах.</summary>
    private sealed class DryRunKeyboard : IKeyboardOutput
    {
        public bool KeyDown(KeyDefinition key) => true;
        public bool KeyUp(KeyDefinition key) => true;
        public bool TypeText(string text) => true;
        public bool IsNumLockOn => true;
    }

    private enum Step { Prepare, Capture, Test }

    private readonly AppController _app;
    private readonly List<string> _recognized = new();
    private Step _step = Step.Prepare;
    private CalibrationSession? _session;
    private CalibrationResult? _result;
    private AffineTransform? _candidate;
    private KeyPressEngine? _testEngine;
    private Layout _layout;
    private bool _loading;
    private bool _live;

    public CalibrationPage() : this(null!) { }

    public CalibrationPage(AppController app)
    {
        _app = app;
        _layout = app?.ActiveLayout ?? BuiltInLayouts.Num20();
        InitializeComponent();
        if (app is null) return;

        DeviceBox.SelectionChanged += (_, _) => { if (!_loading) LoadPrepare(); };
        LayoutBox.SelectionChanged += (_, _) => { if (!_loading && LayoutBox.SelectedItem is Choice<Layout> l) _layout = l.Value; };
        StartButton.Click += async (_, _) => await StartCapture();
        RepeatButton.Click += async (_, _) => await StartCapture();
        TestCurrentButton.Click += (_, _) => OnTestCurrent();
        ResetCalibrationButton.Click += async (_, _) => await OnResetCalibration();
        ManualButton.Click += (_, _) => OnManualApply();
        UndoPointButton.Click += (_, _) => { _session?.UndoLast(); UpdateCapture(); };
        RestartButton.Click += (_, _) => { _session?.Restart(); UpdateCapture(); };
        CancelCaptureButton.Click += (_, _) => Cancel();
        CancelTestButton.Click += (_, _) => Cancel();
        SaveCalibrationButton.Click += async (_, _) => await OnSave();
        ShowStep(Step.Prepare);
    }

    // ================================================================= IPage

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
        if (_step == Step.Prepare) LoadPrepare();
    }

    public Task<bool> CanLeaveAsync() => Task.FromResult(true);

    public void OnHidden()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
        if (_step != Step.Prepare) Cancel();
    }

    public void Dispose()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
    }

    // ================================================================= шаг 1

    private TouchpadDeviceInfo? SelectedDevice => (DeviceBox.SelectedItem as Choice<TouchpadDeviceInfo>)?.Value ?? _app.ActiveDevice;

    private void LoadPrepare()
    {
        _loading = true;
        var devices = _app.Platform.Touchpad.Devices.Where(d => d.IsUsable).Select(d => new Choice<TouchpadDeviceInfo>(d, d.DisplayName)).ToList();
        var prevId = (DeviceBox.SelectedItem as Choice<TouchpadDeviceInfo>)?.Value.Id ?? _app.ActiveDevice?.Id;
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Value.Id == prevId) ?? devices.FirstOrDefault();
        var layouts = _app.Layouts.Select(l => new Choice<Layout>(l, l.Name)).ToList();
        LayoutBox.ItemsSource = layouts;
        var sel = layouts.FirstOrDefault(l => l.Value.Id == _layout.Id) ?? layouts.FirstOrDefault(l => l.Value.Id == _app.ActiveLayout.Id);
        LayoutBox.SelectedItem = sel;
        if (sel is not null) _layout = sel.Value;
        _loading = false;

        var dev = SelectedDevice;
        StartButton.IsEnabled = TestCurrentButton.IsEnabled = ResetCalibrationButton.IsEnabled = ManualButton.IsEnabled = dev is not null;
        if (dev is null)
        {
            CurrentCalibration.Text = _app.InputError ?? L.T("Touchpad not found — calibration is not possible.");
            return;
        }
        var cal = _app.Settings.Devices.TryGetValue(dev.DeviceKey, out var p) ? p.Calibration : new CalibrationData();
        var t = cal.ToTransform();
        var r = t.IsValid ? t.ToDeviceRect() : new RectD(0, 0, 1, 1);
        CurrentCalibration.Text = cal.IsIdentity
            ? L.T("No calibration yet: the sticker is assumed to cover the whole touchpad.")
            : L.F("Current calibration: the sticker covers {0:0}% × {1:0}% of the touchpad, offset {2:0}% / {3:0}%, rotation {4:0.0}°", r.Width * 100, r.Height * 100, r.X * 100, r.Y * 100, t.RotationDegrees)
              + (cal.CalibratedAtUtc is DateTime at ? L.F(", done {0:g}.", at.ToLocalTime()) : ".");
        LeftBox.Text = Num(r.Left * 100);
        TopBox.Text = Num(r.Top * 100);
        RightBox.Text = Num(r.Right * 100);
        BottomBox.Text = Num(r.Bottom * 100);
        ManualError.Text = "";
    }

    private async Task StartCapture()
    {
        if (SelectedDevice is null) return;
        _app.DisableMode("calibration");
        var session = new CalibrationSession(_layout);
        if (session.Targets.Count < 3)
        {
            await Dialogs.InfoAsync(this, L.T("Calibration"), L.T("The layout has too few keys for calibration (at least 3 are needed)."));
            return;
        }
        session.PointCaptured += _ => Dispatcher.UIThread.Post(UpdateCapture);
        _session = session;
        _result = null;
        CaptureCanvas.Layout = _layout;
        ShowStep(Step.Capture);
        UpdateCapture();
    }

    private void OnTestCurrent()
    {
        var dev = SelectedDevice;
        if (dev is null) return;
        var t = _app.Settings.Devices.TryGetValue(dev.DeviceKey, out var p) ? p.Calibration.ToTransform() : AffineTransform.Identity;
        _result = null;
        _session = null;
        StartTest(t.IsValid ? t : AffineTransform.Identity, L.T("Checking the current calibration"), "");
    }

    private async Task OnResetCalibration()
    {
        var dev = SelectedDevice;
        if (dev is null) return;
        if (!await Dialogs.ConfirmAsync(this, L.T("Calibration"), L.T("Reset the calibration? The sticker will be assumed to cover the whole touchpad."))) return;
        _app.ResetCalibration(dev.DeviceKey);
        LoadPrepare();
    }

    private void OnManualApply()
    {
        double? l = Parse(LeftBox.Text), t = Parse(TopBox.Text), r = Parse(RightBox.Text), b = Parse(BottomBox.Text);
        if (l is null || t is null || r is null || b is null) { ManualError.Text = L.T("Enter four numbers."); return; }
        if (r <= l + 5 || b <= t + 5) { ManualError.Text = L.T("The right edge must be greater than the left and the bottom greater than the top (by at least 5%)."); return; }
        if (l < -20 || t < -20 || r > 120 || b > 120) { ManualError.Text = L.T("Values are out of the allowed range (−20…120%)."); return; }
        ManualError.Text = "";
        _app.DisableMode("calibration");
        var tr = AffineTransform.FromDeviceRect(RectD.FromEdges(l.Value / 100, t.Value / 100, r.Value / 100, b.Value / 100));
        _result = null;
        _session = null;
        StartTest(tr, L.T("Checking the manually set bounds"), L.T("If the keys are recognized correctly, save."));
    }

    // ================================================================= шаг 2

    private void UpdateCapture()
    {
        if (_session is null || _step != Step.Capture) return;
        if (_session.IsComplete)
        {
            _result = _session.Solve();
            if (!_result.Success)
            {
                CaptureProgress.Text = _result.Message;
                _session.Restart();
                UpdateCaptureTarget();
                return;
            }
            string details = L.F("Mean error {0:0.0}%, maximum {1:0.0}% of the sticker size. Rotation {2:0.0}°.",
                _result.RmsError * 100, _result.MaxError * 100, _result.Transform.RotationDegrees);
            StartTest(_result.Transform, _result.Message, details);
            return;
        }
        UpdateCaptureTarget();
        CaptureProgress.Text = L.F("Point {0} of {1}", _session.CurrentIndex + 1, _session.Targets.Count);
    }

    private void UpdateCaptureTarget()
    {
        var target = _session!.CurrentTarget!;
        CaptureInstruction.Text = L.F("Touch the “{0}” key on the sticker and lift your finger", target.Label);
        CaptureCanvas.TargetKeyId = target.KeyId;
        CaptureCanvas.Marks = _session.Points.Select(p => p.Target).ToList();
    }

    private void Cancel()
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
            _recognized.Add(_layout.Keys.FirstOrDefault(k => k.Id == ev.ZoneId)?.Label ?? ev.ZoneId);
            if (_recognized.Count > 16) _recognized.RemoveAt(0);
        };
        _recognized.Clear();
        TestCanvas.Layout = _layout;
        ResultTitle.Text = title;
        ResultDetails.Text = details;
        ResultTitle.Foreground = _result is { MaxError: > 0.12 } ? Res("WarningBrush") : Res("TextBrush");
        TestLog.Text = L.F("Recognized: {0}", "—");
        ShowStep(Step.Test);
    }

    private async Task OnSave()
    {
        var dev = SelectedDevice;
        if (dev is null || _candidate is null) return;
        var data = CalibrationData.FromTransform(_candidate, _result?.RmsError, _layout.Id, _session?.Points);
        _app.SaveCalibration(dev.DeviceKey, dev.DisplayName, data);
        if (_app.ActiveLayout.Id != _layout.Id && await Dialogs.ConfirmAsync(this, L.T("Calibration"), L.F("Make the layout “{0}” active?", _layout.Name)))
            _app.SetActiveLayout(_layout.Id);
        if (dev.DeviceKey != _app.ActiveDevice?.DeviceKey) _app.SelectDevice(dev.DeviceKey);
        Cancel();
        await Dialogs.InfoAsync(this, L.T("Calibration"), L.T("Calibration saved and applied."));
    }

    // ================================================================= кадры

    private void OnFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        if (SelectedDevice?.Id != device.Id) return;
        switch (_step)
        {
            case Step.Capture when _session is not null:
                _session.OnFrame(frame);
                // Во время сбора точек новой калибровки ещё нет — касание показываем через текущую (приблизительно).
                CaptureCanvas.Contacts = frame.Contacts.Where(c => c.Tip)
                    .Select(c => new ActiveContactInfo(c.Id, _app.Engine.ToArea(c.Position), null, false, false)).ToList();
                break;
            case Step.Test when _testEngine is not null:
                _testEngine.ProcessFrame(frame);
                _testEngine.Tick(frame.TimestampMs);
                TestCanvas.ActiveKeyIds.Clear();
                foreach (var c in _testEngine.ActiveContacts.Where(c => c.ZoneId is not null)) TestCanvas.ActiveKeyIds.Add(c.ZoneId!);
                TestCanvas.Contacts = _testEngine.ActiveContacts;
                TestLog.Text = L.F("Recognized: {0}", _recognized.Count == 0 ? "—" : string.Join("  ", _recognized));
                break;
        }
    }

    // ================================================================= служебное

    private static IBrush Res(string key) => Ui.Brush(key);

    private void ShowStep(Step step)
    {
        _step = step;
        PrepareStep.IsVisible = step == Step.Prepare;
        CaptureStep.IsVisible = step == Step.Capture;
        TestStep.IsVisible = step == Step.Test;
        Step1Badge.Background = step == Step.Prepare ? Res("SystemControlHighlightAccentBrush") : Res("BadgeOffBrush");
        Step2Badge.Background = step == Step.Capture ? Res("SystemControlHighlightAccentBrush") : Res("BadgeOffBrush");
        Step3Badge.Background = step == Step.Test ? Res("SystemControlHighlightAccentBrush") : Res("BadgeOffBrush");
        foreach (var (badge, s) in new[] { (Step1Badge, Step.Prepare), (Step2Badge, Step.Capture), (Step3Badge, Step.Test) })
            badge.SetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty, step == s ? Avalonia.Media.Brushes.White : Res("TextBrush"));
        if (step != Step.Capture) { CaptureCanvas.Contacts = Array.Empty<ActiveContactInfo>(); CaptureCanvas.TargetKeyId = null; }
        if (step != Step.Test) { TestCanvas.Contacts = Array.Empty<ActiveContactInfo>(); TestCanvas.ActiveKeyIds.Clear(); }
    }

    private static string Num(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);

    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}
