using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using NumPad.App.Services;
using NumPad.Core.Engine;
using NumPad.Core.Layouts;
using NumPad.Platform;

namespace NumPad.App.Views;

public partial class DiagnosticsPage : UserControl, IPage, IDisposable
{
    private readonly AppController _app;
    private readonly DispatcherTimer _timer;
    private TouchFrame? _lastFrame;
    private TouchpadDeviceInfo? _lastDevice;
    private long _framesSinceTick;
    private bool _live;

    public DiagnosticsPage() : this(null!) { }

    public DiagnosticsPage(AppController app)
    {
        _app = app;
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        if (app is null) return;
        _timer.Tick += (_, _) => RefreshLive();
        RefreshButton.Click += (_, _) => { _app.Platform.Touchpad.RefreshDevices(); RefreshStatic(); };
        CopyButton.Click += async (_, _) =>
        {
            RefreshStatic();
            var text = "NumPad from TouchPad — диагностика" + Environment.NewLine +
                       $"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}" + Environment.NewLine + Environment.NewLine +
                       DevicesText.Text + Environment.NewLine + Environment.NewLine + _app.DiagnosticsSummary() + Environment.NewLine + Environment.NewLine +
                       LogText.Text;
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is not null) await clip.SetTextAsync(text);
        };
        OpenLogButton.Click += (_, _) => SettingsPage.OpenFolder(_app.Log.Directory);
    }

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
        _timer.Start();
        RefreshStatic();
        RefreshLive();
    }

    public Task<bool> CanLeaveAsync() => Task.FromResult(true);

    public void OnHidden()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
        _timer.Stop();
    }

    public void Dispose() => OnHidden();

    private void OnFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        _lastFrame = frame;
        _lastDevice = device;
        _framesSinceTick++;
    }

    private void RefreshStatic()
    {
        var sb = new StringBuilder();
        var devices = _app.Platform.Touchpad.Devices;
        if (devices.Count == 0) sb.AppendLine(_app.InputError ?? "Тачпад не найден.");
        foreach (var d in devices)
        {
            sb.AppendLine($"{d.DisplayName}{(d.Id == _app.ActiveDevice?.Id ? "  [активный]" : "")}");
            sb.AppendLine($"  ключ: {d.DeviceKey}");
            if (!d.IsUsable) { sb.AppendLine($"  ОШИБКА: {d.Error}"); continue; }
            sb.AppendLine($"  контактов: {d.MaxContacts}, логический диапазон: {d.LogicalWidth} × {d.LogicalHeight}");
            sb.AppendLine(d.AspectIsPhysical
                ? string.Format(CultureInfo.CurrentCulture, "  размер: {0:0.0} × {1:0.0} мм, пропорции {2:0.00}", d.WidthMm, d.HeightMm, d.AspectRatio)
                : string.Format(CultureInfo.CurrentCulture, "  физический размер не сообщается, пропорции ≈ {0:0.00}", d.AspectRatio));
        }
        DevicesText.Text = sb.ToString().TrimEnd();
        LogText.Text = string.Join(Environment.NewLine, _app.Log.Recent.Reverse().Take(200));
    }

    private void RefreshLive()
    {
        double fps = _framesSinceTick * 1000.0 / _timer.Interval.TotalMilliseconds;
        _framesSinceTick = 0;
        SummaryText.Text = _app.DiagnosticsSummary() + Environment.NewLine +
                           string.Format(CultureInfo.CurrentCulture, "Кадров в секунду: {0:0}", fps) + Environment.NewLine +
                           $"Удерживается клавиш: {_app.Engine.PressedKeyCount}, активных касаний: {_app.Engine.ActiveContactCount}";

        var f = _lastFrame;
        if (f is null) { ContactsText.Text = "Коснитесь тачпада…"; return; }
        var hit = new HitTester(_app.ActiveLayout);
        var sb = new StringBuilder();
        sb.AppendLine($"Устройство: {_lastDevice?.DeviceKey}   кадр {(f.IsComplete ? "полный" : "частичный")}, контактов: {f.Contacts.Count}");
        sb.AppendLine("ID   X тачп.   Y тачп.   X накл.  Y накл.  Tip  Conf  Зона");
        foreach (var c in f.Contacts)
        {
            var a = _app.Engine.ToArea(c.Position);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-4} {1,7:0.000}   {2,7:0.000}   {3,7:0.000}  {4,7:0.000}  {5,-4} {6,-5} {7}",
                c.Id, c.X, c.Y, a.X, a.Y, c.Tip ? "да" : "нет", c.Confidence ? "да" : "нет", hit.Hit(a)?.Label ?? "—"));
        }
        ContactsText.Text = sb.ToString().TrimEnd();
    }
}
