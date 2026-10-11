using NumPad.Core.Engine;
using NumPad.Core.Geometry;
using NumPad.Core.Keys;
using NumPad.Core.Layouts;

namespace NumPad.Core.Tests;

/// <summary>Регистратор системных событий: хранит последовательность и множество «нажатых» клавиш.</summary>
public sealed class RecordingKeyboard : IKeyboardOutput
{
    public readonly List<string> Events = new();
    public readonly HashSet<string> Down = new();
    public bool FailDowns { get; set; }
    public int FailUpsRemaining { get; set; }
    public bool NumLock { get; set; } = true;

    public bool IsNumLockOn => NumLock;

    public bool KeyDown(KeyDefinition key)
    {
        if (FailDowns) { Events.Add($"!down:{key.Name}"); return false; }
        Events.Add($"down:{key.Name}");
        Down.Add(key.Name);
        return true;
    }

    public bool KeyUp(KeyDefinition key)
    {
        if (FailUpsRemaining > 0) { FailUpsRemaining--; Events.Add($"!up:{key.Name}"); return false; }
        Events.Add($"up:{key.Name}");
        Down.Remove(key.Name);
        return true;
    }

    public bool TypeText(string text) { Events.Add($"text:{text}"); return true; }

    public int Count(string evt) => Events.Count(e => e == evt);
    public IEnumerable<string> Clean => Events.Where(e => !e.StartsWith('!'));
    public void Clear() => Events.Clear();

    /// <summary>Каждый down имеет парный up, ничего не зажато.</summary>
    public void AssertBalanced()
    {
        Assert.Empty(Down);
        foreach (var g in Clean.Select(e => e.Split(':')).Where(p => p[0] is "down" or "up").GroupBy(p => p[1]))
        {
            int downs = g.Count(p => p[0] == "down");
            int ups = g.Count(p => p[0] == "up");
            Assert.True(downs >= 1 && ups >= 1, $"Key {g.Key}: downs={downs}, ups={ups}");
        }
    }
}

/// <summary>Тестовый источник касаний: строит кадры с монотонным временем. Подменяет только устройство, не движок.</summary>
public sealed class FakeTouchpad
{
    private readonly Action<TouchFrame> _sink;
    private readonly Dictionary<int, TouchContact> _down = new();
    public long Now { get; private set; } = 1000;

    public FakeTouchpad(Action<TouchFrame> sink) { _sink = sink; }

    public void Advance(long ms) => Now += ms;

    public void Touch(int id, double x, double y, bool confidence = true)
    {
        _down[id] = new TouchContact(id, x, y, true, confidence);
        Emit();
    }

    public void Move(int id, double x, double y)
    {
        var c = _down[id];
        _down[id] = c with { X = x, Y = y };
        Emit();
    }

    /// <summary>Отрыв с отчётом Tip=0 (стандарт PTP).</summary>
    public void Lift(int id)
    {
        var c = _down[id];
        _down.Remove(id);
        var contacts = _down.Values.ToList();
        contacts.Add(c with { Tip = false });
        _sink(new TouchFrame(Now, contacts));
    }

    /// <summary>Отрыв без отчёта Tip=0: контакт просто исчезает из следующего полного кадра.</summary>
    public void Vanish(int id)
    {
        _down.Remove(id);
        Emit();
    }

    /// <summary>Устройство замолчало (потерян отчёт): кадров нет вовсе.</summary>
    public void Forget(int id) => _down.Remove(id);

    public void Hold(long ms, long reportEveryMs = 8, Action<long>? tick = null)
    {
        long end = Now + ms;
        while (Now < end)
        {
            Advance(reportEveryMs);
            Emit();
            tick?.Invoke(Now);
        }
    }

    public void Emit() => _sink(new TouchFrame(Now, _down.Values.ToList()));

    public void Tap(int id, double x, double y, long durationMs = 60)
    {
        Touch(id, x, y);
        Advance(durationMs);
        Emit();
        Lift(id);
        Advance(30);
    }
}

public static class TestLayouts
{
    /// <summary>2×2: «7» (0..0.5, 0..0.5), «8» (0.5..1, 0..0.5), Shift (0..0.5, 0.5..1), Ctrl+C (0.5..1, 0.5..1).</summary>
    public static Layout Grid2x2() => new()
    {
        Id = "test", Name = "Test", AspectRatio = 1, AllowPointerOutsideKeys = false,
        Keys =
        {
            new KeyZone { Id = "n7", Label = "7", X = 0, Y = 0, Width = 0.5, Height = 0.5, Action = KeyAction.ForKey("Numpad7") },
            new KeyZone { Id = "n8", Label = "8", X = 0.5, Y = 0, Width = 0.5, Height = 0.5, Action = KeyAction.ForKey("Numpad8") },
            new KeyZone { Id = "shift", Label = "Shift", X = 0, Y = 0.5, Width = 0.5, Height = 0.5, Action = KeyAction.ForKey("Shift"), Repeat = false },
            new KeyZone { Id = "copy", Label = "Copy", X = 0.5, Y = 0.5, Width = 0.5, Height = 0.5, Action = KeyAction.ForCombo("C", "Ctrl"), Repeat = false },
        },
    };

    public static readonly PointD Seven = new(0.25, 0.25);
    public static readonly PointD Eight = new(0.75, 0.25);
    public static readonly PointD Shift = new(0.25, 0.75);
    public static readonly PointD Copy = new(0.75, 0.75);
}

public sealed class Rig
{
    public readonly RecordingKeyboard Kb = new();
    public readonly KeyPressEngine Engine;
    public readonly ModeManager Mode;
    public readonly FakeTouchpad Pad;

    public Rig(Layout? layout = null, EngineOptions? options = null)
    {
        Engine = new KeyPressEngine(Kb, layout ?? TestLayouts.Grid2x2(), null, options ?? new EngineOptions { AutoRepeat = false });
        FakeTouchpad? pad = null;
        Mode = new ModeManager(Engine, () => pad!.Now);
        Pad = new FakeTouchpad(f => Mode.OnFrame(f));
        pad = Pad;
        Mode.Enable("test");
    }
}
