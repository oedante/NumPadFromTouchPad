using Ogneliss.NumTouch.Core.Calibration;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Layouts;

namespace Ogneliss.NumTouch.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(BuiltInLayouts.Num10Id)]
    [InlineData(BuiltInLayouts.Num20Id)]
    [InlineData(BuiltInLayouts.Num30Id)]
    public void BuiltInLayouts_AreValid_WithoutOverlaps(string id)
    {
        var layout = BuiltInLayouts.Get(id)!;
        var issues = LayoutValidator.Validate(layout);
        Assert.Empty(issues);
        Assert.Contains(layout.Keys, k => k.Action.Key == "Numpad7");
        Assert.Contains(layout.Keys, k => k.Action.Key == "NumpadEnter");
    }

    [Fact]
    public void Num20_HasNavigationAndArrows_Num30_HasCustomKeys()
    {
        var n20 = BuiltInLayouts.Num20();
        foreach (var key in new[] { "Home", "End", "PageUp", "PageDown", "Delete", "Up", "Down", "Left", "Right", "PrintScreen" })
            Assert.Contains(n20.Keys, k => k.Action.Key == key);
        var n30 = BuiltInLayouts.Num30();
        Assert.Contains(n30.Keys, k => k.Action.Kind == KeyActionKind.Combo && k.Action.Key == "C");
        Assert.DoesNotContain(n30.Keys, k => k.Action.Key == "Up");
    }

    [Fact] // 13 — некорректная конфигурация
    public void Validator_DetectsAllProblemKinds()
    {
        var l = new Layout
        {
            Id = "bad", Name = "Bad", AspectRatio = 1.5,
            Keys =
            {
                new KeyZone { Id = "a", Label = "", X = 0, Y = 0, Width = 0.2, Height = 0.2, Action = KeyAction.ForKey("Numpad1") },
                new KeyZone { Id = "a", Label = "dup", X = 0.3, Y = 0, Width = 0.2, Height = 0.2, Action = KeyAction.ForKey("Numpad2") },
                new KeyZone { Id = "out", Label = "out", X = 0.9, Y = 0.9, Width = 0.2, Height = 0.2, Action = KeyAction.ForKey("Numpad3") },
                new KeyZone { Id = "bogus", Label = "?", X = 0.5, Y = 0.5, Width = 0.1, Height = 0.1, Action = KeyAction.ForKey("NoSuchKey") },
                new KeyZone { Id = "nomods", Label = "c", X = 0, Y = 0.5, Width = 0.1, Height = 0.1, Action = new KeyAction { Kind = KeyActionKind.Combo, Key = "C" } },
                new KeyZone { Id = "cad", Label = "cad", X = 0.2, Y = 0.5, Width = 0.1, Height = 0.1, Action = KeyAction.ForCombo("Delete", "Ctrl", "Alt") },
                new KeyZone { Id = "tiny", Label = "t", X = 0.7, Y = 0.3, Width = 0.001, Height = 0.1, Action = KeyAction.ForKey("A") },
                new KeyZone { Id = "notext", Label = "x", X = 0.7, Y = 0.6, Width = 0.1, Height = 0.1, Action = new KeyAction { Kind = KeyActionKind.Text, Text = "" } },
                new KeyZone { Id = "ov", Label = "ov", X = 0.1, Y = 0.1, Width = 0.15, Height = 0.15, Action = KeyAction.ForKey("B") },
            },
        };
        var issues = LayoutValidator.Validate(l);
        Assert.True(LayoutValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.KeyId == "a" && i.Message.Contains("label"));
        Assert.Contains(issues, i => i.KeyId == "a" && i.Message.Contains("Duplicate"));
        Assert.Contains(issues, i => i.KeyId == "out" && i.Message.Contains("beyond"));
        Assert.Contains(issues, i => i.KeyId == "bogus" && i.Message.Contains("Invalid action"));
        Assert.Contains(issues, i => i.KeyId == "nomods" && i.Message.Contains("without modifiers"));
        Assert.Contains(issues, i => i.KeyId == "cad" && i.Message.Contains("Ctrl+Alt+Del"));
        Assert.Contains(issues, i => i.KeyId == "tiny" && i.Message.Contains("too small"));
        Assert.Contains(issues, i => i.KeyId == "notext" && i.Message.Contains("Empty text"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Overlaps"));
    }

    [Fact]
    public void Validator_EmptyLayoutIsError()
    {
        Assert.True(LayoutValidator.HasErrors(LayoutValidator.Validate(new Layout { Id = "e", Name = "E" })));
        Assert.True(LayoutValidator.HasErrors(LayoutValidator.Validate(null)));
    }

    [Fact] // 16 — пересечение границ: детерминированный выбор ближайшего центра
    public void HitTester_OverlapResolvedByNearestCenter()
    {
        var l = new Layout
        {
            Id = "o", Name = "o",
            Keys =
            {
                new KeyZone { Id = "big", Label = "b", X = 0, Y = 0, Width = 0.6, Height = 0.6, Action = KeyAction.ForKey("A") },
                new KeyZone { Id = "small", Label = "s", X = 0.4, Y = 0.4, Width = 0.2, Height = 0.2, Action = KeyAction.ForKey("B") },
            },
        };
        Assert.Equal("small", HitTester.Hit(l, new PointD(0.5, 0.5))!.Id);
        Assert.Equal("big", HitTester.Hit(l, new PointD(0.2, 0.2))!.Id);
        Assert.Null(HitTester.Hit(l, new PointD(0.9, 0.9)));
        Assert.Null(HitTester.Hit(l, new PointD(double.NaN, 0.5)));
    }

    [Fact]
    public void HitTester_IgnoresDisabledZones()
    {
        var l = TestLayouts.Grid2x2();
        l.Keys[0].Enabled = false;
        Assert.Null(HitTester.Hit(l, TestLayouts.Seven));
    }

    [Fact] // таблица «координата → ожидаемая клавиша» для NUM20
    public void HitTester_Num20_Table()
    {
        var l = BuiltInLayouts.Num20();
        // Центр каждой зоны попадает в эту же зону.
        foreach (var k in l.Keys)
            Assert.Equal(k.Id, HitTester.Hit(l, k.Bounds.Center)!.Id);
        var n7 = l.Keys.Single(k => k.Id == "n7").Bounds;
        var n8 = l.Keys.Single(k => k.Id == "n8").Bounds;
        var cases = new (PointD p, string? id)[]
        {
            (new(n7.Left + 0.001, n7.Top + 0.001), "n7"),
            (new(n7.Right - 0.001, n7.Bottom - 0.001), "n7"),
            (new((n7.Right + n8.Left) / 2, n7.Center.Y), null), // промежуток между 7 и 8
            (new(0.001, 0.001), null),                            // поле наклейки
            (new(1.2, 0.5), null),                                // вне области
        };
        foreach (var (p, id) in cases)
            Assert.Equal(id, HitTester.Hit(l, p)?.Id);
    }
}

public class CalibrationTests
{
    [Fact]
    public void Identity_ByDefault()
    {
        var p = new PointD(0.3, 0.7);
        Assert.Equal(p, AffineTransform.Identity.Apply(p));
    }

    [Theory] // 14 — калибровка с разными соотношениями сторон и смещениями
    [InlineData(0.0, 0.0, 1.0, 1.0)]      // наклейка = весь тачпад
    [InlineData(0.4, 0.05, 0.58, 0.9)]    // наклейка справа (узкая и высокая)
    [InlineData(0.1, 0.3, 0.8, 0.4)]      // широкая и низкая
    [InlineData(0.05, 0.05, 0.3, 0.3)]    // маленькая в углу
    public void Solve_RecoversDeviceRect(double left, double top, double width, double height)
    {
        var rect = new RectD(left, top, width, height);
        var truth = AffineTransform.FromDeviceRect(rect);
        var inv = truth.Inverse();
        var session = new CalibrationSession(BuiltInLayouts.Num20());
        foreach (var t in session.Targets.ToList())
            session.AddPoint(inv.Apply(t.Position));
        Assert.True(session.IsComplete);
        var res = session.Solve();
        Assert.True(res.Success, res.Message);
        Assert.True(res.MaxError < 1e-9);
        var r = res.Transform.ToDeviceRect();
        Assert.Equal(left, r.X, 6);
        Assert.Equal(top, r.Y, 6);
        Assert.Equal(width, r.Width, 6);
        Assert.Equal(height, r.Height, 6);
    }

    [Fact]
    public void Solve_HandlesRotationAndNoise()
    {
        double angle = 3 * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        var truth = new AffineTransform(1.4 * cos, -1.4 * sin, -0.2, 1.6 * sin, 1.6 * cos, -0.1);
        var inv = truth.Inverse();
        var rnd = new Random(42);
        var pts = BuiltInLayouts.Num20().Keys.Take(8)
            .Select(k => k.Bounds.Center)
            .Select(c => new CalibrationPoint(inv.Apply(c) + new PointD((rnd.NextDouble() - 0.5) * 0.004, (rnd.NextDouble() - 0.5) * 0.004), c))
            .ToList();
        var res = CalibrationSolver.Solve(pts);
        Assert.True(res.Success);
        Assert.True(res.RmsError < 0.01, $"rms={res.RmsError}");
        Assert.InRange(res.Transform.RotationDegrees, 2.0, 4.0);
    }

    [Fact]
    public void Solve_RejectsCollinearPoints()
    {
        var pts = new[]
        {
            new CalibrationPoint(new(0.1, 0.1), new(0.1, 0.1)),
            new CalibrationPoint(new(0.2, 0.2), new(0.2, 0.2)),
            new CalibrationPoint(new(0.3, 0.3), new(0.3, 0.3)),
        };
        Assert.False(CalibrationSolver.Solve(pts).Success);
        Assert.False(CalibrationSolver.Solve(pts.Take(1).ToList()).Success);
    }

    [Fact]
    public void Calibration_ChangesRealHitBoundaries()
    {
        // Наклейка занимает правую половину тачпада: касание в левой половине не должно нажимать ничего,
        // а касание в точке, где на наклейке «7», — должно нажать 7.
        var layout = TestLayouts.Grid2x2();
        var t = AffineTransform.FromDeviceRect(new RectD(0.5, 0, 0.5, 1));
        var kb = new RecordingKeyboard();
        var engine = new KeyPressEngine(kb, layout, t, new EngineOptions { AutoRepeat = false });
        engine.ProcessFrame(new TouchFrame(0, new[] { new TouchContact(0, 0.2, 0.25) }));
        engine.ProcessFrame(new TouchFrame(10, new[] { new TouchContact(0, 0.2, 0.25, Tip: false) }));
        Assert.Empty(kb.Events);
        engine.ProcessFrame(new TouchFrame(20, new[] { new TouchContact(1, 0.625, 0.25) }));
        engine.ProcessFrame(new TouchFrame(30, new[] { new TouchContact(1, 0.625, 0.25, Tip: false) }));
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, kb.Events);
    }

    [Fact]
    public void Session_CapturesMedianOfRealFrames()
    {
        var session = new CalibrationSession(BuiltInLayouts.Num10());
        var pad = new FakeTouchpad(session.OnFrame);
        var first = session.CurrentTarget!;
        pad.Touch(0, 0.70, 0.30);
        pad.Advance(10); pad.Move(0, 0.71, 0.31);
        pad.Advance(10); pad.Move(0, 0.95, 0.95); // выброс
        pad.Advance(10); pad.Move(0, 0.70, 0.30);
        pad.Advance(10); pad.Lift(0);
        Assert.Single(session.Points);
        Assert.Equal(first.Position, session.Points[0].Target);
        Assert.InRange(session.Points[0].Device.X, 0.69, 0.72);
        session.UndoLast();
        Assert.Empty(session.Points);
    }

    [Fact]
    public void PickTargets_FiveDistinctKeys()
    {
        var t = CalibrationSession.PickTargets(BuiltInLayouts.Num20());
        Assert.Equal(5, t.Count);
        Assert.Equal(5, t.Select(x => x.KeyId).Distinct().Count());
    }
}
