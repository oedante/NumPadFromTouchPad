using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Ogneliss.NumTouch.App.Services;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Layouts;
using Ogneliss.NumTouch.Platform;

namespace Ogneliss.NumTouch.App.Views;

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
            var text = "Ogneliss NumTouch — " + L.T("diagnostics") + Environment.NewLine +
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
        if (devices.Count == 0) sb.AppendLine(_app.InputError ?? L.T("Touchpad not found."));
        foreach (var d in devices)
        {
            sb.AppendLine(d.DisplayName + (d.Id == _app.ActiveDevice?.Id ? "  [" + L.T("active") + "]" : ""));
            sb.AppendLine("  " + L.F("key: {0}", d.DeviceKey));
            if (!d.IsUsable) { sb.AppendLine("  " + L.F("ERROR: {0}", d.Error)); continue; }
            sb.AppendLine("  " + L.F("contacts: {0}, logical range: {1} × {2}", d.MaxContacts, d.LogicalWidth, d.LogicalHeight));
            sb.AppendLine(d.AspectIsPhysical
                ? "  " + L.F("size: {0:0.0} × {1:0.0} mm, aspect {2:0.00}", d.WidthMm, d.HeightMm, d.AspectRatio)
                : "  " + L.F("physical size not reported, aspect ≈ {0:0.00}", d.AspectRatio));
        }
        DevicesText.Text = sb.ToString().TrimEnd();
        LogText.Text = string.Join(Environment.NewLine, _app.Log.Recent.Reverse().Take(200));
    }

    private void RefreshLive()
    {
        double fps = _framesSinceTick * 1000.0 / _timer.Interval.TotalMilliseconds;
        _framesSinceTick = 0;
        SummaryText.Text = _app.DiagnosticsSummary() + Environment.NewLine +
                           L.F("Frames per second: {0:0}", fps) + Environment.NewLine +
                           L.F("Keys held: {0}, active contacts: {1}", _app.Engine.PressedKeyCount, _app.Engine.ActiveContactCount);

        var f = _lastFrame;
        if (f is null) { ContactsText.Text = L.T("Touch the touchpad…"); return; }
        var hit = new HitTester(_app.ActiveLayout);
        var sb = new StringBuilder();
        sb.AppendLine(L.F("Device: {0}   frame {1}, contacts: {2}", _lastDevice?.DeviceKey, f.IsComplete ? L.T("complete") : L.T("partial"), f.Contacts.Count));
        sb.AppendLine(L.T("ID   X pad     Y pad     X stk.   Y stk.   Tip  Conf  Zone"));
        foreach (var c in f.Contacts)
        {
            var a = _app.Engine.ToArea(c.Position);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-4} {1,7:0.000}   {2,7:0.000}   {3,7:0.000}  {4,7:0.000}  {5,-4} {6,-5} {7}",
                c.Id, c.X, c.Y, a.X, a.Y, c.Tip ? L.T("yes") : L.T("no"), c.Confidence ? L.T("yes") : L.T("no"), hit.Hit(a)?.Label ?? "—"));
        }
        ContactsText.Text = sb.ToString().TrimEnd();
    }
}
