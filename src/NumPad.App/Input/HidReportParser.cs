using System.Runtime.InteropServices;
using NumPad.App.Platform;
using static NumPad.App.Platform.NativeMethods;

namespace NumPad.App.Input;

/// <summary>Сырые данные одного слота контакта из HID-отчёта (до нормализации).</summary>
internal readonly record struct RawSlot(int ContactId, double X, double Y, bool Tip, bool Confidence);

internal sealed record AxisInfo(int LogicalMin, int LogicalMax, double? PhysicalMm);

/// <summary>
/// Разбор входных отчётов Windows Precision Touchpad по preparsed data устройства.
/// Исправления относительно старого TouchpadHelper.ParseInput:
///  • Tip Switch (0x42) и Confidence (0x47) читаются через HidP_GetUsages — без них нельзя узнать об отрыве пальца;
///  • caps разбираются один раз на устройство, а не на каждый отчёт;
///  • каждый слот (логическая коллекция пальца) разбирается отдельно, порядок слотов сохраняется;
///  • X/Y нормализуются по LogicalMin/Max конкретного устройства.
/// Usage: Digitizer 0x0D — Tip Switch 0x42, Confidence 0x47, Contact ID 0x51, Contact Count 0x54, Scan Time 0x56;
///        Generic Desktop 0x01 — X 0x30, Y 0x31.
/// </summary>
internal sealed class HidReportParser : IDisposable
{
    private const ushort PageDigitizer = 0x0D, PageGeneric = 0x01;
    private const ushort UsageTip = 0x42, UsageConfidence = 0x47, UsageContactId = 0x51, UsageContactCount = 0x54, UsageScanTime = 0x56;
    private const ushort UsageX = 0x30, UsageY = 0x31;

    private sealed class Slot
    {
        public ushort LinkCollection;
        public bool HasX, HasY, HasId, HasTip, HasConfidence;
        public int XMin, XMax, YMin, YMax;
    }

    private IntPtr _preparsed;
    private readonly List<Slot> _slots = new();
    private readonly ushort _contactCountCollection;
    private readonly bool _hasContactCount;
    private readonly ushort[] _usageBuffer = new ushort[32];

    public AxisInfo XAxis { get; }
    public AxisInfo YAxis { get; }
    public int SlotCount => _slots.Count;
    public int MaxContacts { get; }

    private HidReportParser(IntPtr preparsed, List<Slot> slots, bool hasCount, ushort countCollection, AxisInfo x, AxisInfo y)
    {
        _preparsed = preparsed;
        _slots = slots;
        _hasContactCount = hasCount;
        _contactCountCollection = countCollection;
        XAxis = x;
        YAxis = y;
        MaxContacts = slots.Count;
    }

    /// <summary>Создаёт парсер для устройства. Возвращает null, если устройство не похоже на PTP (нет X/Y/Contact ID).</summary>
    public static HidReportParser? Create(IntPtr hDevice, out string error)
    {
        error = "";
        uint size = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0)
        {
            error = "Не удалось получить размер preparsed data.";
            return null;
        }
        IntPtr pp = Marshal.AllocHGlobal((int)size);
        bool keep = false;
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, pp, ref size) == unchecked((uint)-1))
            {
                error = "Не удалось прочитать preparsed data.";
                return null;
            }
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS)
            {
                error = "HidP_GetCaps завершилась ошибкой.";
                return null;
            }

            var slots = new Dictionary<ushort, Slot>();
            Slot GetSlot(ushort lc)
            {
                if (!slots.TryGetValue(lc, out var s)) { s = new Slot { LinkCollection = lc }; slots[lc] = s; }
                return s;
            }

            bool hasCount = false;
            ushort countCollection = 0;

            ushort valueLen = caps.NumberInputValueCaps;
            if (valueLen > 0)
            {
                var values = new HIDP_VALUE_CAPS[valueLen];
                if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, values, ref valueLen, pp) != HIDP_STATUS_SUCCESS)
                {
                    error = "HidP_GetValueCaps завершилась ошибкой.";
                    return null;
                }
                foreach (var v in values.Take(valueLen))
                {
                    ushort usageMin = v.UsageMin, usageMax = v.IsRange ? v.UsageMax : v.UsageMin;
                    for (int u = usageMin; u <= usageMax; u++)
                    {
                        switch (v.UsagePage, u)
                        {
                            case (PageGeneric, UsageX):
                            {
                                var s = GetSlot(v.LinkCollection);
                                s.HasX = true; s.XMin = v.LogicalMin; s.XMax = v.LogicalMax;
                                break;
                            }
                            case (PageGeneric, UsageY):
                            {
                                var s = GetSlot(v.LinkCollection);
                                s.HasY = true; s.YMin = v.LogicalMin; s.YMax = v.LogicalMax;
                                break;
                            }
                            case (PageDigitizer, UsageContactId):
                                GetSlot(v.LinkCollection).HasId = true;
                                break;
                            case (PageDigitizer, UsageContactCount):
                                hasCount = true; countCollection = v.LinkCollection;
                                break;
                        }
                    }
                }
            }

            ushort buttonLen = caps.NumberInputButtonCaps;
            if (buttonLen > 0)
            {
                var buttons = new HIDP_BUTTON_CAPS[buttonLen];
                if (HidP_GetButtonCaps(HIDP_REPORT_TYPE.HidP_Input, buttons, ref buttonLen, pp) == HIDP_STATUS_SUCCESS)
                {
                    foreach (var b in buttons.Take(buttonLen))
                    {
                        if (b.UsagePage != PageDigitizer) continue;
                        ushort min = b.UsageMin, max = b.IsRange ? b.UsageMax : b.UsageMin;
                        if (!slots.TryGetValue(b.LinkCollection, out var s)) continue;
                        if (UsageTip >= min && UsageTip <= max) s.HasTip = true;
                        if (UsageConfidence >= min && UsageConfidence <= max) s.HasConfidence = true;
                    }
                }
            }

            var fingerSlots = slots.Values.Where(s => s.HasX && s.HasY && s.HasId).OrderBy(s => s.LinkCollection).ToList();
            if (fingerSlots.Count == 0)
            {
                error = "В описании устройства нет коллекций пальцев (X, Y, Contact ID).";
                return null;
            }

            var first = fingerSlots[0];
            var x = new AxisInfo(first.XMin, first.XMax, PhysicalSizeMm(pp, first.LinkCollection, UsageX));
            var y = new AxisInfo(first.YMin, first.YMax, PhysicalSizeMm(pp, first.LinkCollection, UsageY));
            keep = true;
            return new HidReportParser(pp, fingerSlots, hasCount, countCollection, x, y);
        }
        finally
        {
            if (!keep) Marshal.FreeHGlobal(pp);
        }
    }

    /// <summary>Физический размер оси в мм по Units/UnitsExp (если устройство их сообщает).</summary>
    private static double? PhysicalSizeMm(IntPtr pp, ushort linkCollection, ushort usage)
    {
        try
        {
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) return null;
            ushort len = caps.NumberInputValueCaps;
            var values = new HIDP_VALUE_CAPS[len];
            if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, values, ref len, pp) != HIDP_STATUS_SUCCESS) return null;
            var v = values.Take(len).FirstOrDefault(c => c.UsagePage == PageGeneric && c.LinkCollection == linkCollection && c.UsageMin == usage);
            if (v.PhysicalMax <= v.PhysicalMin) return null;
            // Units: младший полубайт — система (1 = SI linear → см, 3 = English linear → дюйм); полубайт длины — экспонента 1.
            uint system = v.Units & 0xF;
            uint lengthExp = (v.Units >> 4) & 0xF;
            if (lengthExp != 1) return null;
            int exp = (int)(v.UnitsExp & 0xF);
            if (exp > 7) exp -= 16; // 4-битное знаковое
            double span = (v.PhysicalMax - v.PhysicalMin) * Math.Pow(10, exp);
            return system switch
            {
                1 => span * 10.0,      // см → мм
                3 => span * 25.4,      // дюйм → мм
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>Разбор одного входного отчёта. Возвращает false, если отчёт не относится к касаниям (другой Report ID).</summary>
    public bool TryParse(IntPtr report, uint length, List<RawSlot> slotsOut, out int? contactCount)
    {
        slotsOut.Clear();
        contactCount = null;
        if (_preparsed == IntPtr.Zero) return false;

        if (_hasContactCount &&
            HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, PageDigitizer, _contactCountCollection, UsageContactCount,
                out uint count, _preparsed, report, length) == HIDP_STATUS_SUCCESS)
            contactCount = (int)count;

        bool any = false;
        foreach (var s in _slots)
        {
            if (HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, PageGeneric, s.LinkCollection, UsageX, out uint rx, _preparsed, report, length) != HIDP_STATUS_SUCCESS)
                continue;
            if (HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, PageGeneric, s.LinkCollection, UsageY, out uint ry, _preparsed, report, length) != HIDP_STATUS_SUCCESS)
                continue;
            if (HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, PageDigitizer, s.LinkCollection, UsageContactId, out uint id, _preparsed, report, length) != HIDP_STATUS_SUCCESS)
                continue;

            bool tip = true, confidence = true;
            if (s.HasTip || s.HasConfidence)
            {
                uint n = (uint)_usageBuffer.Length;
                if (HidP_GetUsages(HIDP_REPORT_TYPE.HidP_Input, PageDigitizer, s.LinkCollection, _usageBuffer, ref n, _preparsed, report, length) == HIDP_STATUS_SUCCESS)
                {
                    bool foundTip = false, foundConf = false;
                    for (int i = 0; i < n; i++)
                    {
                        if (_usageBuffer[i] == UsageTip) foundTip = true;
                        else if (_usageBuffer[i] == UsageConfidence) foundConf = true;
                    }
                    if (s.HasTip) tip = foundTip;
                    if (s.HasConfidence) confidence = foundConf;
                }
            }

            double nx = Normalize(rx, s.XMin, s.XMax);
            double ny = Normalize(ry, s.YMin, s.YMax);
            slotsOut.Add(new RawSlot((int)id, nx, ny, tip, confidence));
            any = true;
        }
        return any || contactCount is not null;
    }

    private static double Normalize(uint raw, int min, int max)
    {
        if (max <= min) return 0;
        return Math.Clamp(((double)raw - min) / (max - min), 0, 1);
    }

    public void Dispose()
    {
        if (_preparsed != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_preparsed);
            _preparsed = IntPtr.Zero;
        }
    }
}
