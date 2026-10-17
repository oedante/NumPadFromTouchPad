using Ogneliss.NumTouch.Core.Diagnostics;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using static Ogneliss.NumTouch.Platform.Linux.LibC;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>
/// Touchpad Input Backend для Linux: чтение /dev/input/eventN (evdev, протокол multitouch B).
/// В отличие от Windows подходит любой тачпад с драйвером ядра (не только Precision Touchpad).
/// В режиме NumPad устройство захватывается (EVIOCGRAB): система перестаёт получать касания — курсор, тапы и
/// прокрутка не срабатывают. Касания, начавшиеся в «зоне трекпада» раскладки, превращаются в движение курсора
/// через виртуальную мышь uinput.
/// </summary>
internal sealed class LinuxTouchpad : ITouchpadBackend
{
    private sealed class Device
    {
        public required EvdevDevice Evdev;
        public required TouchpadDeviceInfo Info;
        public required MultitouchDecoder Decoder;
        public Thread? Thread;
        public volatile bool Stop;
        public bool Grabbed;
    }

    private readonly ILog _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, Device> _devices = new();
    private readonly PointerForwarder _forwarder;
    private Timer? _rescan;
    private volatile bool _grabWanted;
    private volatile Func<PointD, bool>? _passThrough;
    private long _reports, _errors, _blocked;

    public LinuxTouchpad(ILog log, Func<UinputDevice?> mouseFactory)
    {
        _log = log;
        _forwarder = new PointerForwarder(mouseFactory);
    }

    public string Mechanism => "Linux evdev (multitouch B)";
    public IReadOnlyList<TouchpadDeviceInfo> Devices { get { lock (_sync) return _devices.Values.Select(d => d.Info).ToList(); } }
    public string? UnavailableReason { get; private set; }
    public long ReportsReceived => Interlocked.Read(ref _reports);
    public long ParseErrors => Interlocked.Read(ref _errors);
    public long BlockedFrames => Interlocked.Read(ref _blocked);
    public bool IsGrabbed { get { lock (_sync) return _devices.Values.Any(d => d.Grabbed); } }

    public event Action<TouchpadDeviceInfo, TouchFrame>? FrameReceived;
    public event Action? DevicesChanged;
    public event Action<TouchpadDeviceInfo>? DeviceRemoved;

    public bool Start(out string? error)
    {
        RefreshDevices();
        // Новые устройства (USB-тачпад, повторная загрузка драйвера после сна) — периодический пересмотр.
        _rescan = new Timer(_ => { try { RefreshDevices(); } catch (Exception ex) { _log.Warn("Device rescan", ex); } },
            null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        error = UnavailableReason;
        return true;
    }

    public void RefreshDevices()
    {
        bool changed = false;
        bool accessDenied = false;
        int nodes = 0;
        var present = new HashSet<string>();
        foreach (var path in EvdevDevice.EnumerateEventNodes())
        {
            nodes++;
            present.Add(path);
            lock (_sync) if (_devices.ContainsKey(path)) continue;
            var dev = EvdevDevice.Open(path, out int errno);
            if (dev is null) { if (errno == EACCES) accessDenied = true; continue; }
            if (!dev.IsTouchpad) { dev.Dispose(); continue; }
            AddDevice(dev);
            changed = true;
        }

        List<Device> gone;
        lock (_sync) gone = _devices.Values.Where(d => !present.Contains(d.Evdev.Path)).ToList();
        foreach (var d in gone) { RemoveDevice(d, "device node is gone"); changed = true; }

        bool any;
        lock (_sync) any = _devices.Count > 0;
        string? reason = any ? null
            : accessDenied ? L.T("No access to /dev/input. Add your user to the input group (sudo usermod -aG input $USER) and log in again.")
            : nodes == 0 ? L.T("No /dev/input — the program runs without access to input devices (container, Flatpak/Snap sandbox).")
            : L.T("No multitouch touchpad found.");
        bool reasonChanged = reason != UnavailableReason;
        UnavailableReason = reason;
        if (changed || reasonChanged) DevicesChanged?.Invoke();
    }

    private void AddDevice(EvdevDevice dev)
    {
        dev.TryGetAbs(ABS_MT_POSITION_X, out var ax);
        dev.TryGetAbs(ABS_MT_POSITION_Y, out var ay);
        int slots = 1, initial = 0;
        if (dev.HasMtSlots && dev.TryGetAbs(ABS_MT_SLOT, out var sa)) { slots = sa.Maximum + 1; initial = sa.Value; }
        var x = new AxisRange(ax.Minimum, ax.Maximum, ax.Resolution);
        var y = new AxisRange(ay.Minimum, ay.Maximum, ay.Resolution);
        string key = dev.Id.Vendor != 0 || dev.Id.Product != 0 ? $"VID_{dev.Id.Vendor:X4}&PID_{dev.Id.Product:X4}" : dev.Name;
        var info = new TouchpadDeviceInfo(dev.Path, key, string.IsNullOrWhiteSpace(dev.Name) ? key : dev.Name, slots,
            x.Max - x.Min, y.Max - y.Min, x.SizeMm, y.SizeMm, dev.HasMtSlots ? null : L.T("The device uses the legacy multitouch protocol A, which is not supported."));
        var d = new Device { Evdev = dev, Info = info, Decoder = new MultitouchDecoder(slots, x, y, initial) };
        lock (_sync) _devices[dev.Path] = d;
        _log.Info($"Touchpad connected: {info.DisplayName} ({key}, {dev.Path}), slots {slots}" +
                  (info.AspectIsPhysical ? FormattableString.Invariant($", {info.WidthMm:0.#}×{info.HeightMm:0.#} mm") : ""));
        if (!info.IsUsable) return;
        if (_grabWanted) SetGrab(d, true);
        d.Thread = new Thread(() => ReadLoop(d)) { IsBackground = true, Name = "evdev " + dev.Path };
        d.Thread.Start();
    }

    private void RemoveDevice(Device d, string reason)
    {
        lock (_sync) if (!_devices.Remove(d.Evdev.Path)) return;
        d.Stop = true;
        _log.Info($"Touchpad disconnected: {d.Info.DisplayName} ({reason})");
        if (Thread.CurrentThread != d.Thread) d.Thread?.Join(1000);
        d.Evdev.Dispose();
        DeviceRemoved?.Invoke(d.Info);
        DevicesChanged?.Invoke();
    }

    private void ReadLoop(Device d)
    {
        Span<InputEvent> buf = stackalloc InputEvent[64];
        try
        {
            while (!d.Stop)
            {
                if (!d.Evdev.WaitReadable(250)) continue;
                int n = d.Evdev.Read(buf);
                for (int i = 0; i < n; i++)
                {
                    var e = buf[i];
                    if (d.Grabbed && e.Type == EV_KEY && e.Code is BTN_LEFT or BTN_RIGHT or BTN_MIDDLE)
                    {
                        _forwarder.OnButton(e.Code, e.Value);
                        continue;
                    }
                    var frame = d.Decoder.Feed(e.Type, e.Code, e.Value, MonotonicClock.NowMs);
                    if (frame is null) continue;
                    Interlocked.Increment(ref _reports);
                    if (d.Grabbed)
                    {
                        if (!_forwarder.OnFrame(frame, d.Decoder, _passThrough)) Interlocked.Increment(ref _blocked);
                    }
                    FrameReceived?.Invoke(d.Info, frame);
                }
            }
        }
        catch (IOException ex)
        {
            if (!d.Stop) ThreadPool.QueueUserWorkItem(_ => RemoveDevice(d, ex.Message));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _log.Error("Touchpad read error", ex);
            if (!d.Stop) ThreadPool.QueueUserWorkItem(_ => RemoveDevice(d, "read error"));
        }
    }

    // ---------------------------------------------------------------- захват (используется LinuxPointerSuppressor)

    public bool SetGrab(bool grab, Func<PointD, bool>? passThrough, out string? error)
    {
        error = null;
        _grabWanted = grab;
        _passThrough = passThrough;
        _forwarder.Reset();
        List<Device> list;
        lock (_sync) list = _devices.Values.Where(d => d.Info.IsUsable).ToList();
        bool ok = true;
        foreach (var d in list)
            if (!SetGrab(d, grab)) { ok = false; error = L.F("Could not grab {0} (errno {1}).", d.Info.DisplayName, Errno); }
        return ok;
    }

    private bool SetGrab(Device d, bool grab)
    {
        if (d.Grabbed == grab) return true;
        bool ok = d.Evdev.Grab(grab);
        if (ok) d.Grabbed = grab;
        return ok;
    }

    public void Dispose()
    {
        _rescan?.Dispose();
        List<Device> list;
        lock (_sync) list = _devices.Values.ToList();
        foreach (var d in list)
        {
            d.Stop = true;
            if (d.Grabbed) d.Evdev.Grab(false);
        }
        foreach (var d in list) { d.Thread?.Join(1000); d.Evdev.Dispose(); }
        lock (_sync) _devices.Clear();
        _forwarder.Dispose();
    }
}
