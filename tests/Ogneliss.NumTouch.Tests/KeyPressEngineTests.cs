using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Layouts;
using static Ogneliss.NumTouch.Tests.TestLayouts;

namespace Ogneliss.NumTouch.Tests;

/// <summary>Сценарии 1–12, 15–17 из ТЗ (раздел 13). Каждый тест проверяет и нажатие, и отпускание.</summary>
public class KeyPressEngineTests
{
    [Fact] // 1
    public void SingleTap_PressesAndReleasesOnce()
    {
        var r = new Rig();
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Kb.AssertBalanced();
        Assert.Equal(0, r.Engine.PressedKeyCount);
        Assert.Equal(0, r.Engine.ActiveContactCount);
    }

    [Fact] // 2 — главный дефект старого проекта: повтор той же клавиши при тех же координатах и том же Contact ID
    public void RepeatedTapOnSameSpot_WithSameContactId_PressesTwice()
    {
        var r = new Rig();
        r.Pad.Tap(0, Seven.X, Seven.Y);
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7", "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Kb.AssertBalanced();
    }

    [Fact] // 3
    public void TenFastTaps_NoneLost()
    {
        var r = new Rig();
        for (int i = 0; i < 10; i++)
        {
            r.Pad.Touch(0, Seven.X, Seven.Y);
            r.Pad.Advance(8);   // один отчёт ~8 мс
            r.Pad.Lift(0);
            r.Pad.Advance(8);
        }
        Assert.Equal(10, r.Kb.Count("down:Numpad7"));
        Assert.Equal(10, r.Kb.Count("up:Numpad7"));
        r.Kb.AssertBalanced();
    }

    [Fact] // 3b — отрыв без отчёта Tip=0 (контакт исчезает из полного кадра)
    public void FastTaps_WhenDeviceOmitsTipZeroReport_StillCounted()
    {
        var r = new Rig();
        for (int i = 0; i < 10; i++)
        {
            r.Pad.Touch(0, Seven.X, Seven.Y);
            r.Pad.Advance(8);
            r.Pad.Vanish(0);
            r.Pad.Advance(8);
        }
        Assert.Equal(10, r.Kb.Count("down:Numpad7"));
        Assert.Equal(10, r.Kb.Count("up:Numpad7"));
        r.Kb.AssertBalanced();
    }

    [Fact] // 4
    public void DifferentKeysInSequence()
    {
        var r = new Rig();
        r.Pad.Tap(0, Seven.X, Seven.Y);
        r.Pad.Tap(1, Eight.X, Eight.Y);
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7", "down:Numpad8", "up:Numpad8", "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Kb.AssertBalanced();
    }

    [Fact] // 5
    public void Hold_KeyStaysDownUntilLift_WithAutoRepeat()
    {
        var r = new Rig(options: new EngineOptions { AutoRepeat = true, RepeatDelayMs = 500, RepeatIntervalMs = 50 });
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Pad.Hold(400, tick: now => r.Engine.Tick(now));
        Assert.Equal(1, r.Kb.Count("down:Numpad7"));       // до задержки автоповтора — только одно нажатие
        Assert.Contains("Numpad7", r.Kb.Down);
        r.Pad.Hold(400, tick: now => r.Engine.Tick(now));  // ещё 400 мс: автоповтор каждые 50 мс
        int downs = r.Kb.Count("down:Numpad7");
        Assert.InRange(downs, 5, 9);
        Assert.Equal(0, r.Kb.Count("up:Numpad7"));         // typematic: повторный down без up
        r.Pad.Lift(0);
        Assert.Equal(1, r.Kb.Count("up:Numpad7"));
        r.Kb.AssertBalanced();
    }

    [Fact] // 5b — удержание без автоповтора
    public void Hold_WithoutRepeat_SingleDown()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Pad.Hold(1500, tick: now => r.Engine.Tick(now));
        Assert.Equal(new[] { "down:Numpad7" }, r.Kb.Events);
        r.Pad.Lift(0);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
    }

    [Fact] // 6, 7 — перемещение пальца между зонами (KeepKey): клавиша не меняется, нового нажатия нет
    public void SlideToOtherZone_KeepKey_HoldsOriginalKey()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Pad.Move(0, 0.45, 0.25);
        r.Pad.Move(0, Eight.X, Eight.Y);
        r.Pad.Move(0, 0.9, 0.3);
        Assert.Equal(new[] { "down:Numpad7" }, r.Kb.Events);
        r.Pad.Lift(0);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Kb.AssertBalanced();
    }

    [Fact] // 7b — ReleaseOnExit: выход из зоны отпускает, вход в новую зону не нажимает
    public void SlideToOtherZone_ReleaseOnExit()
    {
        var r = new Rig(options: new EngineOptions { AutoRepeat = false, SlideBehavior = SlideBehavior.ReleaseOnExit });
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Pad.Move(0, Eight.X, Eight.Y);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Pad.Move(0, Seven.X, Seven.Y); // вернулся — повторного нажатия до отрыва нет
        r.Pad.Lift(0);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Kb.AssertBalanced();
    }

    [Fact] // 7c — OnRelease: отрыв в другой зоне отменяет нажатие
    public void OnRelease_LiftOutsideOriginalZone_Cancels()
    {
        var r = new Rig(options: new EngineOptions { AutoRepeat = false, PressTrigger = PressTrigger.OnRelease });
        r.Pad.Touch(0, Seven.X, Seven.Y);
        Assert.Empty(r.Kb.Events);
        r.Pad.Move(0, Eight.X, Eight.Y);
        r.Pad.Lift(0);
        Assert.Empty(r.Kb.Events);
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
    }

    [Fact] // 8 — одновременное касание нескольких зон
    public void MultiTouch_IndependentKeys()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Pad.Touch(1, Eight.X, Eight.Y);
        Assert.Equal(new[] { "down:Numpad7", "down:Numpad8" }, r.Kb.Events);
        r.Pad.Lift(0);
        Assert.Equal("up:Numpad7", r.Kb.Events[^1]);
        Assert.Contains("Numpad8", r.Kb.Down);
        r.Pad.Lift(1);
        r.Kb.AssertBalanced();
    }

    [Fact] // 8b — два пальца на одной зоне: одно системное нажатие, отпускание после последнего пальца
    public void TwoFingersSameZone_RefCounted()
    {
        var r = new Rig();
        r.Pad.Touch(0, 0.2, 0.2);
        r.Pad.Touch(1, 0.3, 0.3);
        r.Pad.Lift(0);
        Assert.Equal(new[] { "down:Numpad7" }, r.Kb.Events);
        r.Pad.Lift(1);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
    }

    [Fact] // 9 — переключение режима во время нажатия
    public void ModeSwitchWhileHolding_ReleasesKey_AndFingerDoesNotRepressAfterReEnable()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Pad.Advance(500);
        Assert.True(r.Mode.Toggle());
        Assert.False(r.Mode.IsNumPad);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Kb.AssertBalanced();

        r.Pad.Emit();                 // палец всё ещё лежит, режим выключен — ничего
        r.Pad.Advance(500);
        Assert.True(r.Mode.Toggle()); // снова включили, палец всё ещё на тачпаде
        r.Pad.Emit();
        Assert.Equal(2, r.Kb.Events.Count); // лежащий палец не нажимает
        r.Pad.Lift(0);
        r.Pad.Tap(0, Seven.X, Seven.Y);     // новое касание работает
        Assert.Equal(2, r.Kb.Count("down:Numpad7"));
        r.Kb.AssertBalanced();
    }

    [Fact] // 9b — защита от случайного двойного переключения
    public void Toggle_IsDebounced()
    {
        var r = new Rig();
        r.Pad.Advance(1000);
        Assert.True(r.Mode.Toggle());
        r.Pad.Advance(100);
        Assert.False(r.Mode.Toggle());
        Assert.False(r.Mode.IsNumPad);
    }

    [Fact] // 10 — завершение приложения во время удержания (ReleaseAll из обработчика выхода)
    public void ShutdownWhileHolding_ReleasesEverything()
    {
        var r = new Rig();
        r.Pad.Touch(0, Shift.X, Shift.Y);
        r.Pad.Touch(1, Seven.X, Seven.Y);
        r.Mode.Disable("application exit");
        r.Kb.AssertBalanced();
        Assert.Equal(0, r.Engine.PressedKeyCount);
    }

    [Fact] // 11 — ошибка генерации события
    public void KeyDownFailure_DoesNotLeaveStateAndNextPressWorks()
    {
        var r = new Rig();
        var events = new List<EngineEvent>();
        r.Engine.EventRaised += events.Add;
        r.Kb.FailDowns = true;
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(0, r.Engine.PressedKeyCount);
        Assert.DoesNotContain(r.Kb.Events, e => e.StartsWith("up:"));
        Assert.Contains(events, e => e.Kind == EngineEventKind.OutputError);

        r.Kb.FailDowns = false;
        r.Kb.Clear();
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
    }

    [Fact] // 11b — сбой KeyUp: повтор, клавиша не остаётся зажатой
    public void KeyUpFailure_IsRetriedUntilDelivered()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Kb.FailUpsRemaining = 3; // 2 немедленные попытки провалятся, третья — в Tick
        r.Pad.Lift(0);
        Assert.Contains("Numpad7", r.Kb.Down);
        Assert.Equal(1, r.Engine.PendingReleaseCount);
        Assert.True(r.Engine.NeedsTick);
        r.Engine.Tick(r.Pad.Now + 10);  // третья попытка тоже провалится
        r.Engine.Tick(r.Pad.Now + 20);
        Assert.DoesNotContain("Numpad7", r.Kb.Down);
        Assert.Equal(0, r.Engine.PendingReleaseCount);
    }

    [Fact] // 11c — сбой в середине сочетания откатывает уже нажатые модификаторы
    public void ComboFailure_RollsBackModifiers()
    {
        var kb = new FailOnKey("C");
        var engine = new KeyPressEngine(kb, Grid2x2(), null, new EngineOptions { AutoRepeat = false });
        engine.ProcessFrame(new TouchFrame(0, new[] { new TouchContact(0, Copy.X, Copy.Y) }));
        Assert.Equal(new[] { "down:LeftCtrl", "!down:C", "up:LeftCtrl" }, kb.Events);
        Assert.Equal(0, engine.PressedKeyCount);
    }

    private sealed class FailOnKey : IKeyboardOutput
    {
        private readonly string _fail;
        public readonly List<string> Events = new();
        public FailOnKey(string fail) => _fail = fail;
        public bool KeyDown(Keys.KeyDefinition key) { if (key.Name == _fail) { Events.Add($"!down:{key.Name}"); return false; } Events.Add($"down:{key.Name}"); return true; }
        public bool KeyUp(Keys.KeyDefinition key) { Events.Add($"up:{key.Name}"); return true; }
        public bool TypeText(string text) => true;
        public bool IsNumLockOn => true;
    }

    [Fact] // 12 — потеря устройства / потерянный отчёт об отрыве
    public void StaleContact_IsReleasedByWatchdog()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Pad.Forget(0);                         // устройство больше не шлёт кадров
        r.Engine.Tick(r.Pad.Now + 300);
        Assert.Contains("Numpad7", r.Kb.Down);   // ещё в пределах таймаута
        r.Engine.Tick(r.Pad.Now + 600);
        r.Kb.AssertBalanced();
        Assert.Equal(0, r.Engine.ActiveContactCount);
        r.Pad.Advance(700);
        r.Pad.Tap(0, Seven.X, Seven.Y);          // после восстановления всё работает
        Assert.Equal(2, r.Kb.Count("down:Numpad7"));
    }

    [Fact] // 12b
    public void DeviceLost_ReleasesAll_ModeStays()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Mode.OnDeviceLost();
        r.Kb.AssertBalanced();
        Assert.True(r.Mode.IsNumPad);
    }

    [Fact] // 15 — касание вне активной области
    public void TouchOutsideArea_DoesNothing()
    {
        var layout = Grid2x2();
        foreach (var k in layout.Keys) { k.Width *= 0.5; k.Height *= 0.5; } // клавиши занимают только часть области
        var r = new Rig(layout);
        r.Pad.Tap(0, 0.9, 0.9);
        r.Pad.Tap(1, 0.3, 0.3);   // зазор между зонами
        Assert.Empty(r.Kb.Events);
        r.Pad.Touch(2, 0.9, 0.9);
        r.Pad.Move(2, 0.1, 0.1);  // въезд в зону после касания — не нажимает
        r.Pad.Lift(2);
        Assert.Empty(r.Kb.Events);
    }

    [Fact] // 16 — точка на общей границе двух зон: ровно одна клавиша
    public void TouchOnSharedBorder_ExactlyOneKey()
    {
        var r = new Rig();
        r.Pad.Tap(0, 0.5, 0.25);
        Assert.Equal(1, r.Kb.Clean.Count(e => e.StartsWith("down:")));
        r.Kb.AssertBalanced();
    }

    [Fact] // 17 — модификаторы: Shift удерживается пальцем и применяется к клавише другого пальца
    public void ModifierHeldByOneFinger_AppliesToOtherFinger()
    {
        var r = new Rig();
        r.Pad.Touch(0, Shift.X, Shift.Y);
        r.Pad.Tap(1, Seven.X, Seven.Y);
        r.Pad.Tap(1, Eight.X, Eight.Y);
        r.Pad.Lift(0);
        Assert.Equal(new[] { "down:LeftShift", "down:Numpad7", "up:Numpad7", "down:Numpad8", "up:Numpad8", "up:LeftShift" }, r.Kb.Events);
        r.Kb.AssertBalanced();
    }

    [Fact] // 17b — сочетание Ctrl+C: порядок нажатия и обратный порядок отпускания
    public void Combo_PressOrderAndReverseRelease()
    {
        var r = new Rig();
        r.Pad.Tap(0, Copy.X, Copy.Y);
        Assert.Equal(new[] { "down:LeftCtrl", "down:C", "up:C", "up:LeftCtrl" }, r.Kb.Events);
    }

    [Fact] // 17c — модификатор удерживается и в режиме OnRelease
    public void Modifier_HoldsEvenInOnReleaseMode()
    {
        var r = new Rig(options: new EngineOptions { AutoRepeat = false, PressTrigger = PressTrigger.OnRelease });
        r.Pad.Touch(0, Shift.X, Shift.Y);
        Assert.Equal(new[] { "down:LeftShift" }, r.Kb.Events);
        r.Pad.Tap(1, Seven.X, Seven.Y);
        r.Pad.Lift(0);
        Assert.Equal(new[] { "down:LeftShift", "down:Numpad7", "up:Numpad7", "up:LeftShift" }, r.Kb.Events);
    }

    [Fact]
    public void PalmContact_IsIgnored()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y, confidence: false);
        r.Pad.Lift(0);
        Assert.Empty(r.Kb.Events);
    }

    [Fact]
    public void NumLockOff_FollowNumLock_SendsNavigationKey()
    {
        var r = new Rig(options: new EngineOptions { AutoRepeat = false, FollowNumLock = true });
        r.Kb.NumLock = false;
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "down:NumpadHome", "up:NumpadHome" }, r.Kb.Events);
        r.Kb.NumLock = true;
        r.Kb.Clear();
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
    }

    [Fact]
    public void TextAction_TypesText()
    {
        var layout = Grid2x2();
        layout.Keys[0].Action = KeyAction.ForText("=");
        var r = new Rig(layout);
        r.Pad.Tap(0, Seven.X, Seven.Y);
        Assert.Equal(new[] { "text:=" }, r.Kb.Events);
    }

    [Fact]
    public void ConfigureWhileHolding_ReleasesAndIgnoresHeldFinger()
    {
        var r = new Rig();
        r.Pad.Touch(0, Seven.X, Seven.Y);
        r.Engine.Configure(Grid2x2(), Calibration.AffineTransform.Identity, new EngineOptions { AutoRepeat = false });
        Assert.Equal(new[] { "down:Numpad7", "up:Numpad7" }, r.Kb.Events);
        r.Pad.Move(0, Seven.X, Seven.Y + 0.01);
        r.Pad.Lift(0);
        Assert.Equal(2, r.Kb.Events.Count);
    }

    [Fact]
    public void PointerSuppression_ActiveDuringTouchAndTail()
    {
        var r = new Rig();
        Assert.False(r.Engine.WantsPointerSuppression(r.Pad.Now, 300));
        r.Pad.Touch(0, Seven.X, Seven.Y);
        Assert.True(r.Engine.WantsPointerSuppression(r.Pad.Now, 300));
        r.Pad.Lift(0);
        Assert.True(r.Engine.WantsPointerSuppression(r.Pad.Now + 200, 300));
        Assert.False(r.Engine.WantsPointerSuppression(r.Pad.Now + 400, 300));
    }

    [Fact]
    public void PointerAllowedOutsideKeys_WhenLayoutAllowsIt()
    {
        var layout = BuiltInLayouts.Num10();
        var r = new Rig(layout);
        r.Pad.Touch(0, 0.1, 0.5); // левая «зона трекпада»
        Assert.False(r.Engine.WantsPointerSuppression(r.Pad.Now + 1000, 300));
        r.Pad.Lift(0);
        Assert.Empty(r.Kb.Events);
    }
}
