using Ogneliss.NumTouch.Core.Calibration;
using Ogneliss.NumTouch.Core.Config;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Hotkeys;
using Ogneliss.NumTouch.Core.Layouts;

namespace Ogneliss.NumTouch.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "numtouch-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class ConfigTests
{
    [Fact] // переименование проекта: перенос настроек из каталога прежней версии
    public void MigrateLegacy_CopiesSettingsAndLayouts_Once_AndKeepsOldFolder()
    {
        using var oldDir = new TempDir();
        using var newRoot = new TempDir();
        string target = Path.Combine(newRoot.Path, "OgnelissNumTouch");
        var oldStore = new ConfigStore(oldDir.Path);
        oldStore.SaveSettings(new AppSettings { Hotkey = "Ctrl+Shift+F9", Language = UiLanguage.Russian });
        var custom = BuiltInLayouts.Num10();
        custom.Id = "mine"; custom.Name = "Mine";
        oldStore.SaveLayout(custom);
        Directory.CreateDirectory(oldStore.LogsDir);
        File.WriteAllText(Path.Combine(oldStore.LogsDir, "app.log"), "x");

        Assert.Equal(2, ConfigStore.MigrateLegacy(oldDir.Path, target));
        var store = new ConfigStore(target);
        var s = store.LoadSettings().Value;
        Assert.Equal("Ctrl+Shift+F9", s.Hotkey);
        Assert.Equal(UiLanguage.Russian, s.Language);
        Assert.Contains(store.LoadLayouts().Value, l => l.Id == "mine");
        Assert.False(Directory.Exists(store.LogsDir));                     // журналы не переносятся
        Assert.True(File.Exists(oldStore.SettingsPath));                   // старая папка не тронута

        store.SaveSettings(new AppSettings { Hotkey = "Ctrl+Alt+K" });
        Assert.Equal(0, ConfigStore.MigrateLegacy(oldDir.Path, target));  // повторно не перезаписывает
        Assert.Equal("Ctrl+Alt+K", store.LoadSettings().Value.Hotkey);
        Assert.Equal(0, ConfigStore.MigrateLegacy(Path.Combine(oldDir.Path, "nope"), Path.Combine(newRoot.Path, "x")));
    }

    [Fact] // 19 — сохранение и восстановление профиля
    public void Settings_RoundTrip_WithDeviceProfile()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        var s = new AppSettings { Hotkey = "Ctrl+Shift+F9", ActiveLayoutId = BuiltInLayouts.Num30Id, StartWithWindows = true };
        s.Engine.PressTrigger = PressTrigger.OnRelease;
        s.Engine.RepeatDelayMs = 700;
        var t = AffineTransform.FromDeviceRect(new RectD(0.4, 0.1, 0.55, 0.8));
        var p = s.GetOrCreateProfile("VID_06CB&PID_CE26", "Synaptics PTP");
        p.Calibration = CalibrationData.FromTransform(t, 0.01, BuiltInLayouts.Num30Id);
        store.SaveSettings(s);

        var loaded = new ConfigStore(dir.Path).LoadSettings();
        Assert.Empty(loaded.Warnings);
        var l = loaded.Value;
        Assert.Equal("Ctrl+Shift+F9", l.Hotkey);
        Assert.Equal(BuiltInLayouts.Num30Id, l.ActiveLayoutId);
        Assert.Equal(PressTrigger.OnRelease, l.Engine.PressTrigger);
        Assert.Equal(700, l.Engine.RepeatDelayMs);
        Assert.True(l.StartWithWindows);
        var lp = l.Devices["vid_06cb&pid_ce26"]; // ключ без учёта регистра
        Assert.Equal(t, lp.Calibration.ToTransform());
        Assert.Equal(0.01, lp.Calibration.RmsError);
    }

    [Fact] // устойчивость к повреждённым настройкам
    public void CorruptSettings_AreQuarantined_AndDefaultsLoaded()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        File.WriteAllText(store.SettingsPath, "{ \"Hotkey\": \"Ctrl+Alt+N\", \"Engine\": { \"PressTrigger\": ");
        var res = store.LoadSettings();
        Assert.Single(res.Warnings);
        Assert.Equal(Hotkey.Default.ToString(), res.Value.Hotkey);
        Assert.False(File.Exists(store.SettingsPath));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.corrupt-*"));
    }

    [Fact]
    public void OutOfRangeSettings_AreSanitized()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        File.WriteAllText(store.SettingsPath,
            "{ \"Hotkey\": \"Q\", \"SuppressionTailMs\": 999999, \"Engine\": { \"RepeatDelayMs\": 1, \"RepeatIntervalMs\": 0 }, " +
            "\"Devices\": { \"x\": { \"Calibration\": { \"A\": 0, \"B\": 0, \"E\": 0 } } } }");
        var s = store.LoadSettings().Value;
        Assert.Equal(Hotkey.Default.ToString(), s.Hotkey);
        Assert.Equal(2000, s.SuppressionTailMs);
        Assert.Equal(150, s.Engine.RepeatDelayMs);
        Assert.Equal(15, s.Engine.RepeatIntervalMs);
        Assert.True(s.Devices["x"].Calibration.ToTransform().IsValid);
    }

    [Fact]
    public void Layouts_SaveLoadOverrideAndReset()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        Assert.Equal(3, store.LoadLayouts().Value.Count);

        var edited = BuiltInLayouts.Num20();
        edited.Keys.Single(k => k.Id == "n7").Label = "VII";
        store.SaveLayout(edited);
        var custom = BuiltInLayouts.Num10();
        custom.Id = "my-layout";
        custom.Name = "Моя";
        store.SaveLayout(custom);

        var loaded = store.LoadLayouts();
        Assert.Empty(loaded.Warnings);
        Assert.Equal(4, loaded.Value.Count);
        var n20 = loaded.Value.Single(l => l.Id == BuiltInLayouts.Num20Id);
        Assert.Equal("VII", n20.Keys.Single(k => k.Id == "n7").Label);
        Assert.False(n20.IsBuiltIn);

        var reset = store.ResetToBuiltIn(BuiltInLayouts.Num20Id)!;
        Assert.Equal("7", reset.Keys.Single(k => k.Id == "n7").Label);
        Assert.Equal("7", store.LoadLayouts().Value.Single(l => l.Id == BuiltInLayouts.Num20Id).Keys.Single(k => k.Id == "n7").Label);
    }

    [Fact] // 13 — некорректный файл раскладки не ломает загрузку
    public void InvalidLayoutFiles_AreSkippedWithWarning()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        Directory.CreateDirectory(store.LayoutsDir);
        File.WriteAllText(Path.Combine(store.LayoutsDir, "broken.json"), "not json at all");
        var invalid = BuiltInLayouts.Num10();
        invalid.Id = "invalid";
        invalid.Keys[0].Label = "";
        File.WriteAllText(Path.Combine(store.LayoutsDir, "invalid.json"), ConfigStore.SerializeLayout(invalid));

        var res = store.LoadLayouts();
        Assert.Equal(3, res.Value.Count);
        Assert.Equal(2, res.Warnings.Count);
        Assert.Throws<InvalidOperationException>(() => store.SaveLayout(invalid));
    }

    [Fact]
    public void Import_AssignsUniqueId()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        var path = Path.Combine(dir.Path, "export.json");
        store.ExportLayout(BuiltInLayouts.Num20(), path);
        var imported = store.ImportLayout(path, new[] { "num10", "num20", "num30" });
        Assert.Equal("num20-2", imported.Id);
        Assert.DoesNotContain(LayoutValidator.Validate(imported), i => i.Severity == IssueSeverity.Error);
    }

    [Fact] // 20 — повторный запуск после аварийного завершения
    public void SessionGuard_DetectsCrash_AndKeepsPendingRestore()
    {
        using var dir = new TempDir();
        var g1 = new SessionGuard(dir.Path);
        Assert.False(g1.Begin());
        g1.SavePendingRestore("{\"tap\":true}");
        // процесс «упал»: EndClean не вызван
        var g2 = new SessionGuard(dir.Path);
        Assert.True(g2.Begin());
        Assert.Equal("{\"tap\":true}", g2.LoadPendingRestore());
        g2.ClearPendingRestore();
        g2.EndClean();
        var g3 = new SessionGuard(dir.Path);
        Assert.False(g3.Begin());
        Assert.Null(g3.LoadPendingRestore());
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+N", "Ctrl+Alt+N")]
    [InlineData("alt + ctrl + n", "Ctrl+Alt+N")]
    [InlineData("Shift+Ctrl+F9", "Ctrl+Shift+F9")]
    [InlineData("F13", "F13")]
    [InlineData("Win+Shift+PgUp", "Shift+Win+PageUp")]
    public void Parse_Normalizes(string input, string expected)
    {
        Assert.True(Hotkey.TryParse(input, out var hk, out var err), err);
        Assert.Equal(expected, hk.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("N")]
    [InlineData("Ctrl+N+M")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Bogus")]
    public void Parse_RejectsInvalid(string input)
    {
        Assert.False(Hotkey.TryParse(input, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory] // 18 — конфликт горячих клавиш
    [InlineData("Ctrl+Space", true)]
    [InlineData("Ctrl+C", true)]
    [InlineData("Win+E", true)]
    [InlineData("Alt+F4", true)]
    [InlineData("Ctrl+Alt+N", false)]
    [InlineData("Ctrl+Shift+F9", false)]
    public void ConflictWarnings(string input, bool expectWarning)
    {
        Assert.True(Hotkey.TryParse(input, out var hk, out _));
        Assert.Equal(expectWarning, hk.GetConflictWarning() is not null);
    }
}
