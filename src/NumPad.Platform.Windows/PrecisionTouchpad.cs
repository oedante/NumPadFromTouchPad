using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NumPad.Core.Diagnostics;
using NumPad.Core.Engine;
using static NumPad.Platform.Windows.NativeMethods;

namespace NumPad.Platform.Windows;

/// <summary>
/// Touchpad Input Backend для Windows: Raw Input по HID-коллекции Precision Touchpad (0x0D/0x05).
/// Работает в потоке ввода (<see cref="InputThread"/>); события <see cref="FrameReceived"/> приходят оттуда же.
/// </summary>
internal sealed class PrecisionTouchpad : ITouchpadBackend
{
    private const ushort UsagePageDigitizer = 0x0D;
    private const ushort UsageTouchPad = 0x05;

    private sealed class DeviceState : IDisposable
    {
        public required TouchpadDeviceInfo Info;
        public HidReportParser? Parser;
        public readonly HidFrameAssembler Assembler = new();
        public void Dispose() => Parser?.Dispose();
    }

    private readonly InputThread _input;
    private readonly ILog _log;
    private readonly Dictionary<IntPtr, DeviceState> _devices = new(); // только поток ввода
    private volatile TouchpadDeviceInfo[] _snapshot = Array.Empty<TouchpadDeviceInfo>();
    private readonly List<HidSlot> _slotBuffer = new();
    private IntPtr _buffer = IntPtr.Zero;
    private uint _bufferSize;
    private bool _registered;
    private long _reports, _errors;

    public PrecisionTouchpad(InputThread input, ILog log)
    {
        _input = input;
        _log = log;
    }

    public string Mechanism => "Windows Precision Touchpad (Raw Input)";
    public IReadOnlyList<TouchpadDeviceInfo> Devices => _snapshot;
    public string? UnavailableReason { get; private set; }
    public long ReportsReceived => Interlocked.Read(ref _reports);
    public long ParseErrors => Interlocked.Read(ref _errors);

    public event Action<TouchpadDeviceInfo, TouchFrame>? FrameReceived;
    public event Action? DevicesChanged;
    public event Action<TouchpadDeviceInfo>? DeviceRemoved;

    public bool Start(out string? error)
    {
        string? err = null;
        bool ok = _input.Invoke(() =>
        {
            var dev = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = UsagePageDigitizer, usUsage = UsageTouchPad,
                    dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, // фоновый приём + уведомления о подключении
                    hwndTarget = _input.Handle,
                },
            };
            if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                err = $"RegisterRawInputDevices: ошибка {Marshal.GetLastWin32Error()}";
                return false;
            }
            _registered = true;
            RefreshDevicesCore();
            return true;
        });
        error = err;
        if (!ok) _log.Error(err ?? "Raw Input");
        return ok;
    }

    public void RefreshDevices() => _input.Post(RefreshDevicesCore);

    /// <summary>Обработка сообщений окна потока ввода.</summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                try { OnInput(lParam); }
                catch (Exception ex)
                {
                    long n = Interlocked.Increment(ref _errors);
                    if (n < 20) _log.Error("Ошибка разбора отчёта тачпада", ex);
                }
                return true;
            case WM_INPUT_DEVICE_CHANGE:
                if ((int)wParam == GIDC_REMOVAL && _devices.ContainsKey(lParam)) RemoveDevice(lParam);
                RefreshDevicesCore();
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- устройства

    private void RefreshDevicesCore()
    {
        var found = new HashSet<IntPtr>();
        uint count = 0;
        uint itemSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, itemSize) != 0) { _log.Warn("GetRawInputDeviceList(size) failed"); return; }
        var list = new RAWINPUTDEVICELIST[count];
        uint got = GetRawInputDeviceList(list, ref count, itemSize);
        if (got == unchecked((uint)-1)) { _log.Warn("GetRawInputDeviceList failed"); return; }

        foreach (var d in list.Take((int)got).Where(x => x.dwType == RIM_TYPEHID))
        {
            var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
            uint size = info.cbSize;
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICEINFO, ref info, ref size) == unchecked((uint)-1)) continue;
            if (info.hid.usUsagePage != UsagePageDigitizer || info.hid.usUsage != UsageTouchPad) continue;
            found.Add(d.hDevice);
            if (!_devices.ContainsKey(d.hDevice))
                AddDevice(d.hDevice, (int)info.hid.dwVendorId, (int)info.hid.dwProductId);
        }

        foreach (var gone in _devices.Keys.Where(h => !found.Contains(h)).ToList())
            RemoveDevice(gone);

        _snapshot = _devices.Values.Select(v => v.Info).ToArray();
        UnavailableReason = _snapshot.Any(d => d.IsUsable) ? null
            : TouchpadGestureGuard.LegacyTouchpadPresent
                ? "Тачпад найден, но он не является Precision Touchpad: Windows не передаёт координаты касаний."
                : "Precision Touchpad не найден.";
        DevicesChanged?.Invoke();
    }

    private void AddDevice(IntPtr h, int vid, int pid)
    {
        string path = GetDeviceName(h);
        var parser = HidReportParser.Create(h, out var err);
        string key = MakeDeviceKey(path, vid, pid);
        string name = GetProductName(path) ?? $"Precision Touchpad ({key})";
        var info = new TouchpadDeviceInfo(h.ToString(), key, name,
            parser?.MaxContacts ?? 0,
            parser is null ? 0 : parser.XAxis.LogicalMax - parser.XAxis.LogicalMin,
            parser is null ? 0 : parser.YAxis.LogicalMax - parser.YAxis.LogicalMin,
            parser?.XAxis.PhysicalMm, parser?.YAxis.PhysicalMm,
            parser is null ? err : null);
        _devices[h] = new DeviceState { Info = info, Parser = parser };
        if (parser is null) _log.Warn($"Тачпад {key}: {err}");
        else _log.Info($"Тачпад подключён: {key}, контактов {info.MaxContacts}, X {info.LogicalWidth}, Y {info.LogicalHeight}" +
                       (info.AspectIsPhysical ? FormattableString.Invariant($", {info.WidthMm:0.#}×{info.HeightMm:0.#} мм") : ""));
    }

    private void RemoveDevice(IntPtr h)
    {
        if (!_devices.Remove(h, out var st)) return;
        _log.Info($"Тачпад отключён: {st.Info.DeviceKey}");
        st.Dispose();
        _snapshot = _devices.Values.Select(v => v.Info).ToArray();
        DeviceRemoved?.Invoke(st.Info);
    }

    private static string GetDeviceName(IntPtr h)
    {
        uint chars = 0;
        GetRawInputDeviceInfoString(h, RIDI_DEVICENAME, null, ref chars);
        if (chars == 0) return "";
        var buf = new char[chars];
        if (GetRawInputDeviceInfoString(h, RIDI_DEVICENAME, buf, ref chars) == unchecked((uint)-1)) return "";
        return new string(buf).TrimEnd('\0');
    }

    internal static string MakeDeviceKey(string path, int vid, int pid)
    {
        var m = Regex.Match(path, @"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (m.Success) return $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}";
        // I2C-тачпады часто имеют путь вида HID#ELAN0001&Col02 — используем идентификатор оборудования.
        var hw = Regex.Match(path, @"HID#([^#&]+)", RegexOptions.IgnoreCase);
        if (hw.Success) return hw.Groups[1].Value.ToUpperInvariant();
        return $"VID_{vid:X4}&PID_{pid:X4}";
    }

    private static string? GetProductName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var buf = new byte[256];
            if (!HidD_GetProductString(h, buf, (uint)buf.Length)) return null;
            var s = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- отчёты

    private void OnInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0) return;
        EnsureBuffer(size);
        if (GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, headerSize) != size) return;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
        if (header.dwType != RIM_TYPEHID) return;
        Interlocked.Increment(ref _reports);

        if (!_devices.TryGetValue(header.hDevice, out var dev))
        {
            RefreshDevicesCore(); // устройство появилось раньше уведомления
            if (!_devices.TryGetValue(header.hDevice, out dev)) return;
        }
        if (dev.Parser is null) return;

        // RAWHID: DWORD dwSizeHid; DWORD dwCount; BYTE bRawData[dwSizeHid * dwCount]
        IntPtr hid = _buffer + (int)headerSize;
        uint reportSize = (uint)Marshal.ReadInt32(hid);
        uint reportCount = (uint)Marshal.ReadInt32(hid, 4);
        IntPtr data = hid + 8;
        long now = MonotonicClock.NowMs;

        for (uint i = 0; i < reportCount; i++)
        {
            IntPtr report = data + (int)(i * reportSize);
            if (!dev.Parser.TryParse(report, reportSize, _slotBuffer, out int? contactCount)) continue;
            foreach (var frame in dev.Assembler.Push(_slotBuffer, contactCount, now))
                FrameReceived?.Invoke(dev.Info, frame);
        }
    }

    private void EnsureBuffer(uint size)
    {
        if (_bufferSize >= size) return;
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _bufferSize = Math.Max(size, 256);
        _buffer = Marshal.AllocHGlobal((int)_bufferSize);
    }

    public void Dispose()
    {
        try
        {
            _input.Invoke(() =>
            {
                if (_registered)
                {
                    var dev = new[] { new RAWINPUTDEVICE { usUsagePage = UsagePageDigitizer, usUsage = UsageTouchPad, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
                    RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                    _registered = false;
                }
                foreach (var d in _devices.Values) d.Dispose();
                _devices.Clear();
            }, 2000);
        }
        catch { /* поток ввода уже остановлен */ }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _bufferSize = 0; }
    }
}
