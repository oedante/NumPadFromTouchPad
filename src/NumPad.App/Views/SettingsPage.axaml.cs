using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using NumPad.App.Services;
using NumPad.Core.Config;
using NumPad.Core.Engine;
using NumPad.Core.Hotkeys;
using NumPad.Core.Keys;

namespace NumPad.App.Views;

public partial class SettingsPage : UserControl, IPage
{
    private readonly AppController _app;

    public SettingsPage() : this(null!) { }

    public SettingsPage(AppController app)
    {
        _app = app;
        InitializeComponent();
        if (app is null) return;
        SaveButton.Click += (_, _) => Save();
        ApplyHotkeyButton.Click += async (_, _) => await ApplyHotkey();
        DefaultHotkeyButton.Click += async (_, _) => { HotkeyBox.Text = Hotkey.Default.ToString(); await ApplyHotkey(); };
        HotkeyBox.AddHandler(KeyDownEvent, OnHotkeyKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        OpenFolderButton.Click += (_, _) => OpenFolder(_app.Store.RootDir);
        ResetButton.Click += async (_, _) =>
        {
            if (!await Dialogs.ConfirmAsync(this, "Настройки", "Сбросить все настройки к значениям по умолчанию? Раскладки и калибровки сохранятся.")) return;
            _app.ResetSettings();
            Load();
        };
        foreach (var s in new[] { DelaySlider, IntervalSlider, TailSlider })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) UpdateSliderTexts(); };
    }

    public void OnShown() => Load();
    public Task<bool> CanLeaveAsync() => Task.FromResult(true);
    public void OnHidden() { }

    private void Load()
    {
        var s = _app.Settings;
        HotkeyBox.Text = _app.CurrentHotkeyText;
        ShowHotkeyStatus();

        Select(TriggerBox, s.Engine.PressTrigger.ToString());
        Select(SlideBox, s.Engine.SlideBehavior.ToString());
        RepeatBox.IsChecked = s.Engine.AutoRepeat;
        DelaySlider.Value = s.Engine.RepeatDelayMs;
        IntervalSlider.Value = s.Engine.RepeatIntervalMs;
        NumLockBox.IsChecked = s.Engine.FollowNumLock;
        NumLockBox.IsVisible = !OperatingSystem.IsMacOS(); // на Mac нет Num Lock
        PalmBox.IsChecked = s.Engine.IgnoreLowConfidence;

        Select(SuppressBox, s.PointerSuppression.ToString());
        SuppressNote.Text = $"Механизм: {_app.Platform.Suppressor.Mechanism}." +
            (OperatingSystem.IsLinux() ? " В Linux блокируется только тачпад — внешняя мышь работает всегда." : " Во время касания тачпада на мгновение блокируется и внешняя мышь.");
        TailSlider.Value = s.SuppressionTailMs;
        GesturesBox.IsChecked = s.DisableTouchpadGestures;
        GesturesBox.IsEnabled = _app.Platform.Gestures.IsSupported;
        GesturesNote.Text = _app.Platform.Gestures.IsSupported
            ? "Исходные настройки тачпада восстанавливаются при выключении режима, при выходе и после сбоя."
            : "Недоступно на этой системе (нужна Windows 11 24H2 или новее).";

        bool autostart;
        try { autostart = _app.Platform.Autostart.IsEnabled(); } catch { autostart = s.StartWithWindows; }
        AutostartBox.IsChecked = autostart;
        AutostartBox.IsEnabled = _app.Platform.Autostart.IsSupported;
        MinimizedBox.IsChecked = s.StartMinimized;
        CloseToTrayBox.IsChecked = s.CloseToTray;
        RestoreModeBox.IsChecked = s.RestoreModeOnStart;
        LogBox.IsChecked = s.DiagnosticsLogging;
        UpdateSliderTexts();
        SavedMark.IsVisible = false;
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == tag) ?? box.Items.OfType<ComboBoxItem>().First();

    private static T ReadTag<T>(ComboBox box, T fallback) where T : struct, Enum =>
        box.SelectedItem is ComboBoxItem { Tag: string t } && Enum.TryParse<T>(t, out var v) ? v : fallback;

    private void Save()
    {
        var s = _app.Settings;
        s.Engine.PressTrigger = ReadTag(TriggerBox, PressTrigger.OnTouchDown);
        s.Engine.SlideBehavior = ReadTag(SlideBox, SlideBehavior.KeepKey);
        s.Engine.AutoRepeat = RepeatBox.IsChecked == true;
        s.Engine.RepeatDelayMs = (int)DelaySlider.Value;
        s.Engine.RepeatIntervalMs = (int)IntervalSlider.Value;
        s.Engine.FollowNumLock = NumLockBox.IsChecked == true;
        s.Engine.IgnoreLowConfidence = PalmBox.IsChecked == true;
        s.PointerSuppression = ReadTag(SuppressBox, PointerSuppression.WhileTouching);
        s.SuppressionTailMs = (int)TailSlider.Value;
        s.DisableTouchpadGestures = GesturesBox.IsChecked == true;
        s.StartWithWindows = AutostartBox.IsChecked == true;
        s.StartMinimized = MinimizedBox.IsChecked == true;
        s.CloseToTray = CloseToTrayBox.IsChecked == true;
        s.RestoreModeOnStart = RestoreModeBox.IsChecked == true;
        s.DiagnosticsLogging = LogBox.IsChecked == true;
        _app.ApplySettings();
        Load();
        SavedMark.IsVisible = true;
    }

    // ---------------------------------------------------------------- горячая клавиша

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None) return; // переход фокуса
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            HotkeyBox.Text = ModifierText(e.KeyModifiers) + "…";
            return;
        }
        var name = KeyNameFromAvalonia(e.Key);
        if (name is null) { HotkeyStatus.Text = "Эту клавишу нельзя использовать в сочетании."; return; }
        HotkeyBox.Text = ModifierText(e.KeyModifiers) + name;
        if (Hotkey.TryParse(HotkeyBox.Text, out var hk, out var err))
        {
            var warn = hk.GetConflictWarning();
            HotkeyStatus.Text = warn is null ? "Нажмите «Назначить», чтобы применить." : "⚠ " + warn;
            HotkeyStatus.Foreground = Ui.Brush(warn is null ? "SubtleTextBrush" : "WarningBrush");
        }
        else
        {
            HotkeyStatus.Text = err;
            HotkeyStatus.Foreground = Ui.Brush("ErrorBrush");
        }
    }

    private static string ModifierText(KeyModifiers m)
    {
        var parts = new List<string>();
        if (m.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    /// <summary>Клавиша Avalonia → имя из каталога (основная клавиатура, а не цифровой блок для Enter).</summary>
    internal static string? KeyNameFromAvalonia(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key >= Key.D0 && key <= Key.D9) return "D" + (key - Key.D0);
        if (key >= Key.F1 && key <= Key.F24) return "F" + (key - Key.F1 + 1);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Numpad" + (key - Key.NumPad0);
        return key switch
        {
            Key.Enter => "Enter",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            Key.Scroll => "ScrollLock",
            Key.Add => "NumpadAdd",
            Key.Subtract => "NumpadSubtract",
            Key.Multiply => "NumpadMultiply",
            Key.Divide => "NumpadDivide",
            Key.Decimal => "NumpadDecimal",
            Key.OemMinus => "Minus",
            Key.OemPlus => "Equals",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            _ => null,
        } is { } n && KeyCatalog.IsKnown(n) ? n : null;
    }

    private async Task ApplyHotkey()
    {
        if (!Hotkey.TryParse(HotkeyBox.Text, out var hk, out var err))
        {
            HotkeyStatus.Text = err;
            HotkeyStatus.Foreground = Ui.Brush("ErrorBrush");
            return;
        }
        var warn = hk.GetConflictWarning();
        if (warn is not null && !await Dialogs.ConfirmAsync(this, "Горячая клавиша", $"{hk}: {warn}\n\nВсё равно назначить?")) return;
        _app.ApplyHotkey(hk.ToString(), out _);
        HotkeyBox.Text = _app.CurrentHotkeyText;
        ShowHotkeyStatus();
    }

    private void ShowHotkeyStatus()
    {
        if (_app.HotkeyError is not null)
        {
            HotkeyStatus.Text = "✕ " + _app.HotkeyError + (_app.HotkeyRegistered ? $" Сейчас действует: {_app.CurrentHotkeyText}." : "");
            HotkeyStatus.Foreground = Ui.Brush("ErrorBrush");
        }
        else
        {
            HotkeyStatus.Text = _app.HotkeyRegistered
                ? $"✓ {_app.CurrentHotkeyText} работает в любой программе." + (_app.Platform.Hotkey.Limitation is { } l ? " " + l : "")
                : "Горячая клавиша не назначена.";
            HotkeyStatus.Foreground = Ui.Brush("OnBrush");
        }
    }

    // ---------------------------------------------------------------- прочее

    private void UpdateSliderTexts()
    {
        if (DelayText is null) return;
        DelayText.Text = $"{DelaySlider.Value:0} мс";
        IntervalText.Text = $"{IntervalSlider.Value:0} мс ({1000 / Math.Max(1, IntervalSlider.Value):0}/с)";
        TailText.Text = $"{TailSlider.Value:0} мс";
    }

    internal static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* нет файлового менеджера — ничего страшного */ }
    }
}
