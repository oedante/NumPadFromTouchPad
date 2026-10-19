using Ogneliss.NumTouch.Core.Layouts;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Keys;
using Ogneliss.NumTouch.Platform;
using Ogneliss.NumTouch.Platform.Linux;

namespace Ogneliss.NumTouch.Tests;

/// <summary>Платформенная логика без ввода-вывода: сборка кадров PTP, декодер evdev, таблицы кодов клавиш.</summary>
public class HidFrameAssemblerTests
{
    private static HidSlot S(int id, bool tip = true) => new(id, 0.1 * id, 0.2, tip, true);

    [Fact]
    public void ParallelMode_OneReportOneFrame_IgnoresUnusedSlots()
    {
        var a = new HidFrameAssembler();
        var frames = a.Push(new[] { S(0), S(1), S(7, tip: false), S(9, tip: false), S(3, tip: false) }, 2, 10);
        var f = Assert.Single(frames);
        Assert.True(f.IsComplete);
        Assert.Equal(new[] { 0, 1 }, f.Contacts.Select(c => c.Id));
    }

    [Fact]
    public void HybridMode_ContactsAcrossReports()
    {
        var a = new HidFrameAssembler();
        Assert.Empty(a.Push(new[] { S(0), S(1) }, 3, 10));         // первый отчёт: Contact Count = 3
        var f = Assert.Single(a.Push(new[] { S(2), S(5) }, 0, 11)); // продолжение: Contact Count = 0, действителен 1 слот
        Assert.Equal(new[] { 0, 1, 2 }, f.Contacts.Select(c => c.Id));
    }

    [Fact]
    public void LostContinuationReport_EmitsIncompleteFrameThenNew()
    {
        var a = new HidFrameAssembler();
        a.Push(new[] { S(0), S(1) }, 3, 10);
        var frames = a.Push(new[] { S(4) }, 1, 20);
        Assert.Equal(2, frames.Count);
        Assert.False(frames[0].IsComplete);
        Assert.True(frames[1].IsComplete);
        Assert.Equal(4, frames[1].Contacts.Single().Id);
    }

    [Fact]
    public void ZeroCountWithNothingPending_IsEmptyCompleteFrame()
    {
        var a = new HidFrameAssembler();
        var f = Assert.Single(a.Push(Array.Empty<HidSlot>(), 0, 5));
        Assert.True(f.IsComplete);
        Assert.Empty(f.Contacts);
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_06CB&PID_CE26&Col02#5&abc#{4d1e55b2}", "VID_06CB&PID_CE26")]
    [InlineData(@"\\?\HID#ELAN0001&Col02#4&2d1#{4d1e55b2}", "ELAN0001")]
    public void WindowsDeviceKey(string path, string expected) =>
        Assert.Equal(expected, Ogneliss.NumTouch.Platform.Windows.PrecisionTouchpad.MakeDeviceKey(path, 0, 0));

    [Theory]
    [InlineData("VID_05AC&PID_0340", "Apple Precision Touchpad")]
    [InlineData("VID_06CB&PID_CE26", "Synaptics Precision Touchpad")]
    [InlineData("ELAN0001", "ELAN Precision Touchpad")]
    [InlineData("VID_1234&PID_0001", "Precision Touchpad (VID_1234&PID_0001)")]
    public void WindowsFallbackDeviceName(string key, string expected) =>
        Assert.Equal(expected, Ogneliss.NumTouch.Platform.Windows.PrecisionTouchpad.FallbackName(key));
}

public class MultitouchDecoderTests
{
    private const ushort SYN = 0, ABS = 3, REPORT = 0, DROPPED = 3, SLOT = 0x2f, TRACK = 0x39, PX = 0x35, PY = 0x36, TOOL = 0x37;
    private static readonly AxisRange Ax = new(0, 1000, 10), Ay = new(0, 500, 10);

    private static TouchFrame? Syn(MultitouchDecoder d, long t = 0) => d.Feed(SYN, REPORT, 0, t);

    [Fact]
    public void SingleTouch_DownMoveUp()
    {
        var d = new MultitouchDecoder(5, Ax, Ay);
        d.Feed(ABS, TRACK, 17, 0); d.Feed(ABS, PX, 250, 0); d.Feed(ABS, PY, 250, 0);
        var f1 = Syn(d)!;
        var c = Assert.Single(f1.Contacts);
        Assert.Equal(17, c.Id); Assert.True(c.Tip);
        Assert.Equal(0.25, c.X, 6); Assert.Equal(0.5, c.Y, 6);

        d.Feed(ABS, PX, 500, 0);
        Assert.Equal(0.5, Syn(d)!.Contacts.Single().X, 6);

        d.Feed(ABS, TRACK, -1, 0);
        var up = Syn(d)!.Contacts.Single();
        Assert.Equal(17, up.Id); Assert.False(up.Tip);
        Assert.Empty(Syn(d)!.Contacts);
    }

    [Fact]
    public void TwoSlots_Independent()
    {
        var d = new MultitouchDecoder(5, Ax, Ay);
        d.Feed(ABS, SLOT, 0, 0); d.Feed(ABS, TRACK, 1, 0); d.Feed(ABS, PX, 100, 0);
        d.Feed(ABS, SLOT, 1, 0); d.Feed(ABS, TRACK, 2, 0); d.Feed(ABS, PX, 900, 0);
        var f = Syn(d)!;
        Assert.Equal(new[] { 1, 2 }, f.Contacts.Select(c => c.Id));
        d.Feed(ABS, SLOT, 0, 0); d.Feed(ABS, TRACK, -1, 0);
        var f2 = Syn(d)!;
        Assert.Contains(f2.Contacts, c => c.Id == 1 && !c.Tip);
        Assert.Contains(f2.Contacts, c => c.Id == 2 && c.Tip);
    }

    [Fact]
    public void RepeatedTapSameSpot_GivesTwoPresses_EndToEnd()
    {
        // Повторное нажатие (главный дефект старого проекта) — через реальный декодер evdev и движок.
        var kb = new RecordingKeyboard();
        var engine = new KeyPressEngine(kb, TestLayouts.Grid2x2(), null, new EngineOptions { AutoRepeat = false });
        var d = new MultitouchDecoder(5, Ax, Ay);
        int id = 40;
        for (int i = 0; i < 2; i++)
        {
            d.Feed(ABS, TRACK, id++, 0); d.Feed(ABS, PX, 250, 0); d.Feed(ABS, PY, 125, 0);
            engine.ProcessFrame(Syn(d, i * 100)!);
            d.Feed(ABS, TRACK, -1, 0);
            engine.ProcessFrame(Syn(d, i * 100 + 50)!);
        }
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7", "down:Numpad7", "up:Numpad7" }, kb.Events);
    }

    [Fact]
    public void Palm_HasNoConfidence()
    {
        var d = new MultitouchDecoder(2, Ax, Ay);
        d.Feed(ABS, TRACK, 5, 0); d.Feed(ABS, TOOL, 2, 0);
        Assert.False(Syn(d)!.Contacts.Single().Confidence);
    }

    [Fact]
    public void SynDropped_ReleasesAllAndRecovers()
    {
        var d = new MultitouchDecoder(2, Ax, Ay);
        d.Feed(ABS, TRACK, 5, 0);
        Syn(d);
        d.Feed(SYN, DROPPED, 0, 0);
        d.Feed(ABS, PX, 10, 0); // игнорируется до SYN_REPORT
        var f = Syn(d)!;
        Assert.False(f.Contacts.Single().Tip);
        d.Feed(ABS, TRACK, 6, 0);
        Assert.Equal(6, Syn(d)!.Contacts.Single().Id);
    }

    [Fact]
    public void AxisRange_SizeFromResolution() => Assert.Equal(100, new AxisRange(0, 1000, 10).SizeMm);
}

public class KeyCodeTableTests
{
    [Fact]
    public void EveryCatalogKey_HasLinuxCode()
    {
        var missing = KeyCatalog.All.Where(k => !PlatformKeyCodes.Linux.ContainsKey(k.Name)).Select(k => k.Name).ToList();
        Assert.True(missing.Count == 0, "Нет кода Linux: " + string.Join(", ", missing));
    }

    [Fact]
    public void NumpadAndBuiltInLayouts_HaveMacCodes()
    {
        foreach (var layout in BuiltInLayouts.All)
            foreach (var z in layout.Keys.Where(z => z.Action.Kind is KeyActionKind.Key or KeyActionKind.Combo))
            {
                var k = KeyCatalog.Get(z.Action.Key!);
                if (k.Group is KeyGroup.Media or KeyGroup.System) continue; // на Mac нет PrintScreen/калькулятора — документировано
                Assert.True(PlatformKeyCodes.Mac.ContainsKey(k.Name), $"{layout.Id}/{z.Id}: нет кода Mac для {k.Name}");
            }
    }

    [Fact]
    public void LinuxLetterCodes_MatchInputEventCodes()
    {
        Assert.Equal(30, PlatformKeyCodes.Linux["A"]);
        Assert.Equal(46, PlatformKeyCodes.Linux["C"]);
        Assert.Equal(11, PlatformKeyCodes.Linux["D0"]);
        Assert.Equal(96, PlatformKeyCodes.Linux["NumpadEnter"]);
    }
}
