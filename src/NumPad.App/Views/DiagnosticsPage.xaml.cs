using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NumPad.App.Input;
using NumPad.App.Services;
using NumPad.Core.Engine;
using NumPad.Core.Layouts;

namespace NumPad.App.Views;

public partial class DiagnosticsPage : UserControl, IPage, IDisposable
{
    private readonly AppController _app;
    private readonly DispatcherTimer _timer;
    private TouchFrame? _lastFrame;
    private TouchpadDeviceInfo? _lastDevice;
    private long _framesSinceTick;
    private double _fps;
    private bool _live;

    public DiagnosticsPage(AppController app)
    {
        _app = app;
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshLive();
    }

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
        _timer.Start();
        RefreshStatic();
        RefreshLive();
    }

    public bool OnLeaving()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
        _timer.Stop();
        return true;
    }

    public void Dispose() => OnLeaving();

    private void OnFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        _lastFrame = frame;
        _lastDevice = device;
        _framesSinceTick++;
    }

    private void RefreshStatic()
    {
        var sb = new StringBuilder();
        var devices = _app.Touchpad.Devices;
        if (devices.Count == 0) sb.AppendLine(_app.InputError ?? "Precision Touchpad не найден.");
        foreach (var d in devices)
        {
            sb.AppendLine($"{d.DisplayName}{(d.DeviceKey == _app.ActiveDevice?.DeviceKey ? "  [активный]" : "")}");
            sb.AppendLine($"  ключ: {d.DeviceKey}");
            if (!d.IsUsable) { sb.AppendLine($"  ОШИБКА: {d.ParserError}"); continue; }
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
        _fps = _framesSinceTick * 1000.0 / _timer.Interval.TotalMilliseconds;
        _framesSinceTick = 0;
        SummaryText.Text = _app.DiagnosticsSummary() + Environment.NewLine +
                           string.Format(CultureInfo.CurrentCulture, "Кадров в секунду: {0:0}", _fps) + Environment.NewLine +
                           $"Удерживается клавиш: {_app.Engine.PressedKeyCount}, активных касаний: {_app.Engine.ActiveContactCount}";

        var f = _lastFrame;
        if (f is null) { ContactsText.Text = "Коснитесь тачпада…"; return; }
        var hit = new HitTester(_app.ActiveLayout);
        var sb = new StringBuilder();
        sb.AppendLine($"Устройство: {_lastDevice?.DeviceKey}   кадр {(f.IsComplete ? "полный" : "частичный")}, контактов: {f.Contacts.Count}");
        sb.AppendLine("ID   X тачпада  Y тачпада   X накл.  Y накл.  Tip  Conf  Зона");
        foreach (var c in f.Contacts)
        {
            var a = _app.Engine.ToArea(c.Position);
            var zone = hit.Hit(a);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-4} {1,9:0.000}  {2,9:0.000}   {3,7:0.000}  {4,7:0.000}  {5,-4} {6,-5} {7}",
                c.Id, c.X, c.Y, a.X, a.Y, c.Tip ? "да" : "нет", c.Confidence ? "да" : "нет", zone?.Label ?? "—"));
        }
        ContactsText.Text = sb.ToString().TrimEnd();
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        _app.Touchpad.RefreshDevices();
        RefreshStatic();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        RefreshStatic();
        var text = "NumPad from TouchPad — диагностика" + Environment.NewLine +
                   $"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}" + Environment.NewLine + Environment.NewLine +
                   DevicesText.Text + Environment.NewLine + Environment.NewLine + _app.DiagnosticsSummary() + Environment.NewLine + Environment.NewLine +
                   LogText.Text;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { MessageBox.Show("Буфер обмена занят: " + ex.Message, "Диагностика"); }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_app.Log.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Log.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Журнал"); }
    }
}
