using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NumPad.Core.Diagnostics;
using NumPad.Core.Engine;
using static NumPad.App.Platform.NativeMethods;

namespace NumPad.App.Input;

public sealed record TouchpadDeviceInfo(
    IntPtr Handle,
    string DeviceKey,
    string DisplayName,
    string DevicePath,
    int VendorId,
    int ProductId,
    int MaxContacts,
    int LogicalWidth,
    int LogicalHeight,
    double? WidthMm,
    double? HeightMm,
    string? ParserError)
{
    public bool IsUsable => ParserError is null;
    /// <summary>Соотношение сторон поверхности: по физическим размерам, иначе по логическим диапазонам.</summary>
    public double AspectRatio => WidthMm is > 0 && HeightMm is > 0 ? WidthMm.Value / HeightMm.Value
        : LogicalHeight > 0 ? (double)LogicalWidth / LogicalHeight : 1.6;
    public bool AspectIsPhysical => WidthMm is > 0 && HeightMm is > 0;
}

/// <summary>
/// Touchpad Input Backend для Windows: Raw Input по HID-коллекции Precision Touchpad (0x0D/0x05).
/// Собирает кадры из отчётов (параллельный и гибридный режимы PTP), нормализует координаты,
/// следит за подключением/отключением устройств (RIDEV_DEVNOTIFY).
/// Все методы вызываются из UI-потока (WM_INPUT приходит в окно этого потока).
/// </summary>
public sealed class PrecisionTouchpad : ITouchSource, IDisposable
{
    private const ushort UsagePageDigitizer = 0x0D;
    private const ushort UsageTouchPad = 0x05;

    private sealed class DeviceState : IDisposable
    {
        public required TouchpadDeviceInfo Info;
        public HidReportParser? Parser;
        // Сборка кадра (гибридный режим: Contact Count только в первом отчёте кадра).
        public int Expected;
        public readonly List<TouchContact> Pending = new();
        public void Dispose() => Parser?.Dispose();
    }

    private readonly ILog _log;
    private readonly Dictionary<IntPtr, DeviceState> _devices = new();
    private readonly List<RawSlot> _slotBuffer = new();
    private readonly Stopwatch _clock;
    private IntPtr _buffer = IntPtr.Zero;
    private uint _bufferSize;
    private IntPtr _hwnd;
    private bool _registered;

    public PrecisionTouchpad(ILog log, Stopwatch clock)
    {
        _log = log;
        _clock = clock;
    }

    public event Action<TouchFrame>? FrameReceived;
    /// <summary>Источник кадра (устройство) — для выбора профиля калибровки.</summary>
    public event Action<TouchpadDeviceInfo, TouchFrame>? DeviceFrameReceived;
    public event Action? DevicesChanged;
    public event Action<TouchpadDeviceInfo>? DeviceRemoved;

    public bool IsRegistered => _registered;
    public IReadOnlyList<TouchpadDeviceInfo> Devices => _devices.Values.Select(d => d.Info).ToList();
    public long ReportsReceived { get; private set; }
    public long ParseErrors { get; private set; }

    // ---------------------------------------------------------------- registration

    public bool Register(IntPtr hwnd, out string error)
    {
        error = "";
        _hwnd = hwnd;
        var dev = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = UsagePageDigitizer, usUsage = UsageTouchPad,
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, // фоновый приём + уведомления о (dis)connect
                hwndTarget = hwnd,
            },
        };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            int err = Marshal.GetLastWin32Error();
            error = $"RegisterRawInputDevices: ошибка {err}";
            _log.Error(error);
            return false;
        }
        _registered = true;
        RefreshDevices();
        return true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        var dev = new[] { new RAWINPUTDEVICE { usUsagePage = UsagePageDigitizer, usUsage = UsageTouchPad, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
        RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _registered = false;
    }

    // ---------------------------------------------------------------- devices

    /// <summary>Перечисляет PTP-устройства. Вызывается при старте и при WM_INPUT_DEVICE_CHANGE.</summary>
    public void RefreshDevices()
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

        DevicesChanged?.Invoke();
    }

    private void AddDevice(IntPtr h, int vid, int pid)
    {
        string path = GetDeviceName(h);
        var parser = HidReportParser.Create(h, out var err);
        string key = MakeDeviceKey(path, vid, pid);
        string name = GetProductName(path) ?? $"Precision Touchpad ({key})";
        var info = new TouchpadDeviceInfo(h, key, name, path, vid, pid,
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

    // ---------------------------------------------------------------- messages

    /// <summary>Обработка сообщений окна. Возвращает true, если сообщение относится к тачпаду.</summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                try { OnInput(lParam); }
                catch (Exception ex) { ParseErrors++; if (ParseErrors < 20) _log.Error("Ошибка разбора отчёта тачпада", ex); }
                return true;
            case WM_INPUT_DEVICE_CHANGE:
                if ((int)wParam == GIDC_REMOVAL && _devices.ContainsKey(lParam)) RemoveDevice(lParam);
                RefreshDevices();
                return true;
        }
        return false;
    }

    private void OnInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0) return;
        EnsureBuffer(size);
        if (GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, headerSize) != size) return;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
        if (header.dwType != RIM_TYPEHID) return;
        ReportsReceived++;

        if (!_devices.TryGetValue(header.hDevice, out var dev))
        {
            RefreshDevices(); // устройство появилось раньше уведомления
            if (!_devices.TryGetValue(header.hDevice, out dev)) return;
        }
        if (dev.Parser is null) return;

        // RAWHID: DWORD dwSizeHid; DWORD dwCount; BYTE bRawData[dwSizeHid * dwCount]
        IntPtr hid = _buffer + (int)headerSize;
        uint reportSize = (uint)Marshal.ReadInt32(hid);
        uint reportCount = (uint)Marshal.ReadInt32(hid, 4);
        IntPtr data = hid + 8;
        long now = _clock.ElapsedMilliseconds;

        for (uint i = 0; i < reportCount; i++)
        {
            IntPtr report = data + (int)(i * reportSize);
            if (!dev.Parser.TryParse(report, reportSize, _slotBuffer, out int? contactCount)) continue;
            Assemble(dev, contactCount, now);
        }
    }

    /// <summary>
    /// Сборка кадра. Отчёт с Contact Count &gt; 0 начинает кадр; в гибридном режиме следующие отчёты несут
    /// Contact Count = 0 и добавляют оставшиеся контакты. Действительны только первые (ожидаемое − собранное) слоты отчёта.
    /// </summary>
    private void Assemble(DeviceState dev, int? contactCount, long now)
    {
        int count = contactCount ?? _slotBuffer.Count; // устройство без Contact Count: все слоты действительны
        if (count > 0 || dev.Expected == 0)
        {
            if (dev.Pending.Count > 0) Emit(dev, now, complete: false); // прошлый кадр не дособран
            dev.Pending.Clear();
            dev.Expected = count;
        }

        int take = Math.Min(_slotBuffer.Count, Math.Max(0, dev.Expected - dev.Pending.Count));
        for (int i = 0; i < take; i++)
        {
            var s = _slotBuffer[i];
            dev.Pending.Add(new TouchContact(s.ContactId, s.X, s.Y, s.Tip, s.Confidence));
        }

        if (dev.Pending.Count >= dev.Expected)
        {
            Emit(dev, now, complete: true);
            dev.Pending.Clear();
            dev.Expected = 0;
        }
    }

    private void Emit(DeviceState dev, long now, bool complete)
    {
        var frame = new TouchFrame(now, dev.Pending.ToArray(), complete);
        DeviceFrameReceived?.Invoke(dev.Info, frame);
        FrameReceived?.Invoke(frame);
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
        Unregister();
        foreach (var d in _devices.Values) d.Dispose();
        _devices.Clear();
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _bufferSize = 0; }
    }
}
