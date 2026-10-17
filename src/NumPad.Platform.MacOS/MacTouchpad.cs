using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NumPad.Core.Diagnostics;
using NumPad.Core.Engine;
using static NumPad.Platform.MacOS.MacNative;

namespace NumPad.Platform.MacOS;

/// <summary>
/// Touchpad Input Backend для macOS: приватный MultitouchSupport.framework.
/// Даёт нормализованные координаты пальцев 0..1 в фоне без особых разрешений. Приватный API: не для Mac App Store,
/// структура MTTouch может измениться в новых версиях macOS. СТАТУС: не проверено на устройстве.
/// </summary>
internal sealed unsafe class MacTouchpad : ITouchpadBackend
{
    [StructLayout(LayoutKind.Sequential)] private struct MTPoint { public float X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MTVector { public MTPoint Position, Velocity; }

    /// <summary>Раскладка по открытым реверс-инжинирингам (OpenMultitouchSupport), 96 байт на 64-битной системе.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MTTouch
    {
        public int Frame;
        public double Timestamp;
        public int Identifier;
        public int State;          // 3 MakeTouch, 4 Touching, 5 BreakTouch, 7 OutOfRange
        public int FingerId;
        public int HandId;
        public MTVector Normalized; // 0..1, ось Y вверх
        public float ZTotal;
        public int Field9;
        public float Angle, MajorAxis, MinorAxis;
        public MTVector Absolute;
        public int Field14, Field15;
        public float ZDensity;
    }

    private static MacTouchpad? _instance;

    private readonly ILog _log;
    private readonly Dictionary<nint, TouchpadDeviceInfo> _devices = new();
    private readonly object _sync = new();
    private nint _lib;
    private nint _list;
    private delegate* unmanaged[Cdecl]<nint, void> _stop;
    private delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, MTTouch*, int, double, int, int>, void> _unregister;
    private long _reports;

    public MacTouchpad(ILog log)
    {
        _log = log;
        _instance = this;
    }

    public string Mechanism => "MultitouchSupport.framework (приватный)";
    public IReadOnlyList<TouchpadDeviceInfo> Devices { get { lock (_sync) return _devices.Values.ToList(); } }
    public string? UnavailableReason { get; private set; }
    public long ReportsReceived => Interlocked.Read(ref _reports);
    public long ParseErrors => 0;
    public event Action<TouchpadDeviceInfo, TouchFrame>? FrameReceived;
    public event Action? DevicesChanged;
    public event Action<TouchpadDeviceInfo>? DeviceRemoved { add { } remove { } }

    public bool Start(out string? error)
    {
        error = null;
        if (!NativeLibrary.TryLoad(MultitouchSupport, out _lib))
        {
            error = UnavailableReason = "MultitouchSupport.framework недоступен в этой версии macOS.";
            return false;
        }
        try
        {
            var createList = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(_lib, "MTDeviceCreateList");
            var register = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, MTTouch*, int, double, int, int>, void>)
                NativeLibrary.GetExport(_lib, "MTRegisterContactFrameCallback");
            var start = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(_lib, "MTDeviceStart");
            _stop = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_lib, "MTDeviceStop");
            _unregister = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, MTTouch*, int, double, int, int>, void>)
                NativeLibrary.GetExport(_lib, "MTUnregisterContactFrameCallback");
            NativeLibrary.TryGetExport(_lib, "MTDeviceGetSensorSurfaceDimensions", out var dimsPtr);
            var dims = (delegate* unmanaged[Cdecl]<nint, int*, int*, int>)dimsPtr;

            _list = createList();
            long count = _list == 0 ? 0 : CFArrayGetCount(_list);
            for (nint i = 0; i < count; i++)
            {
                nint dev = CFArrayGetValueAtIndex(_list, i);
                double? wMm = null, hMm = null;
                if (dims != null)
                {
                    int w = 0, h = 0;
                    dims(dev, &w, &h);
                    if (w > 0 && h > 0) { wMm = w / 100.0; hMm = h / 100.0; } // гипотеза: сотые доли мм
                }
                var info = new TouchpadDeviceInfo(dev.ToString(), $"MT-{i}", i == 0 ? "Встроенный трекпад" : $"Трекпад {i + 1}",
                    11, 1000, 1000, wMm, hMm, null);
                lock (_sync) _devices[dev] = info;
                register(dev, &OnFrame);
                start(dev, 0);
                _log.Info($"Трекпад: {info.DisplayName}" + (info.AspectIsPhysical ? FormattableString.Invariant($", {wMm:0.#}×{hMm:0.#} мм") : ""));
            }
            UnavailableReason = count == 0 ? "Трекпад не найден." : null;
            DevicesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            error = UnavailableReason = "MultitouchSupport: " + ex.Message;
            _log.Error("MultitouchSupport", ex);
            return false;
        }
    }

    public void RefreshDevices() { /* список устройств MultitouchSupport фиксируется при запуске */ }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnFrame(nint device, MTTouch* touches, int count, double timestamp, int frame)
    {
        try { _instance?.Handle(device, touches, count); }
        catch { /* исключение не должно уйти в нативный код */ }
        return 0;
    }

    private void Handle(nint device, MTTouch* touches, int count)
    {
        TouchpadDeviceInfo? info;
        lock (_sync) _devices.TryGetValue(device, out info);
        if (info is null) return;
        var contacts = new TouchContact[Math.Max(0, count)];
        for (int i = 0; i < count; i++)
        {
            var t = touches[i];
            bool tip = t.State is 3 or 4;
            contacts[i] = new TouchContact(t.Identifier, Math.Clamp(t.Normalized.Position.X, 0, 1), Math.Clamp(1 - t.Normalized.Position.Y, 0, 1), tip, true);
        }
        Interlocked.Increment(ref _reports);
        FrameReceived?.Invoke(info, new TouchFrame(MonotonicClock.NowMs, contacts, true));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var dev in _devices.Keys)
            {
                if (_unregister != null) _unregister(dev, &OnFrame);
                if (_stop != null) _stop(dev);
            }
            _devices.Clear();
        }
        _instance = null;
    }
}
