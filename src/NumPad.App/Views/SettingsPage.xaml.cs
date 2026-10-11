using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NumPad.App.Platform;
using NumPad.App.Services;
using NumPad.Core.Config;
using NumPad.Core.Engine;
using NumPad.Core.Hotkeys;
using NumPad.Core.Keys;

namespace NumPad.App.Views;

public partial class SettingsPage : UserControl, IPage
{
    private readonly AppController _app;

    public SettingsPage(AppController app)
    {
        _app = app;
        InitializeComponent();
    }

    public void OnShown() => Load();
    public bool OnLeaving() => true;

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
        PalmBox.IsChecked = s.Engine.IgnoreLowConfidence;

        Select(SuppressBox, s.PointerSuppression.ToString());
        TailSlider.Value = s.SuppressionTailMs;
        GesturesBox.IsChecked = s.DisableTouchpadGestures;
        GesturesNote.Text = TouchpadGestureGuard.IsSupported
            ? "Исходные настройки тачпада восстанавливаются при выключении режима, при выходе и после сбоя."
            : "Недоступно в этой версии Windows (нужна Windows 11 24H2 или новее) — работает только блокировка курсора.";

        bool autostart;
        try { autostart = Autostart.IsEnabled(); } catch { autostart = s.StartWithWindows; }
        AutostartBox.IsChecked = autostart;
        MinimizedBox.IsChecked = s.StartMinimized;
        CloseToTrayBox.IsChecked = s.CloseToTray;
        NotifyBox.IsChecked = s.ShowNotifications;
        RestoreModeBox.IsChecked = s.RestoreModeOnStart;
        LogBox.IsChecked = s.DiagnosticsLogging;
        UpdateSliderTexts();
        SavedMark.Visibility = Visibility.Collapsed;
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private static T ReadTag<T>(ComboBox box, T fallback) where T : struct, Enum =>
        box.SelectedItem is ComboBoxItem { Tag: string t } && Enum.TryParse<T>(t, out var v) ? v : fallback;

    private void OnSave(object sender, RoutedEventArgs e)
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
        s.ShowNotifications = NotifyBox.IsChecked == true;
        s.RestoreModeOnStart = RestoreModeBox.IsChecked == true;
        s.DiagnosticsLogging = LogBox.IsChecked == true;
        _app.ApplySettings();
        Load();
        SavedMark.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- горячая клавиша

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool modifierOnly = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
        if (modifierOnly)
        {
            HotkeyBox.Text = ModifierText(Keyboard.Modifiers) + "…";
            return;
        }
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false; // Tab без модификаторов — переход фокуса
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        var def = KeyCatalog.All
            .Where(k => k.WinVk == vk)
            .OrderBy(k => k.Group == KeyGroup.Numpad ? 1 : 0) // Enter основной клавиатуры, а не цифрового блока
            .FirstOrDefault();
        if (def is null) { HotkeyStatus.Text = "Эту клавишу нельзя использовать в сочетании."; return; }
        HotkeyBox.Text = ModifierText(Keyboard.Modifiers) + def.Name;
        if (Hotkey.TryParse(HotkeyBox.Text, out var hk, out var err))
        {
            var warn = hk.GetConflictWarning();
            HotkeyStatus.Text = warn is null ? "Нажмите «Назначить», чтобы применить." : "⚠ " + warn;
            HotkeyStatus.Foreground = warn is null ? (Brush)FindResource("SubtleTextBrush") : (Brush)FindResource("WarningBrush");
        }
        else
        {
            HotkeyStatus.Text = err;
            HotkeyStatus.Foreground = (Brush)FindResource("ErrorBrush");
        }
    }

    private static string ModifierText(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    private void OnApplyHotkey(object sender, RoutedEventArgs e)
    {
        if (!Hotkey.TryParse(HotkeyBox.Text, out var hk, out var err))
        {
            HotkeyStatus.Text = err;
            HotkeyStatus.Foreground = (Brush)FindResource("ErrorBrush");
            return;
        }
        var warn = hk.GetConflictWarning();
        if (warn is not null && MessageBox.Show($"{hk}: {warn}\n\nВсё равно назначить?", "Горячая клавиша",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _app.ApplyHotkey(hk.ToString(), out _);
        HotkeyBox.Text = _app.CurrentHotkeyText;
        ShowHotkeyStatus();
    }

    private void OnDefaultHotkey(object sender, RoutedEventArgs e)
    {
        HotkeyBox.Text = Hotkey.Default.ToString();
        OnApplyHotkey(sender, e);
    }

    private void ShowHotkeyStatus()
    {
        if (_app.HotkeyError is not null)
        {
            HotkeyStatus.Text = "✕ " + _app.HotkeyError + (_app.HotkeyRegistered ? $" Сейчас действует: {_app.CurrentHotkeyText}." : "");
            HotkeyStatus.Foreground = (Brush)FindResource("ErrorBrush");
        }
        else
        {
            HotkeyStatus.Text = _app.HotkeyRegistered ? $"✓ {_app.CurrentHotkeyText} работает в любой программе." : "Горячая клавиша не назначена.";
            HotkeyStatus.Foreground = (Brush)FindResource("OnBrush");
        }
    }

    // ---------------------------------------------------------------- прочее

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderTexts();

    private void UpdateSliderTexts()
    {
        if (DelayText is null || IntervalText is null || TailText is null) return;
        DelayText.Text = $"{DelaySlider.Value:0} мс";
        IntervalText.Text = $"{IntervalSlider.Value:0} мс ({1000 / Math.Max(1, IntervalSlider.Value):0}/с)";
        TailText.Text = $"{TailSlider.Value:0} мс";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Store.RootDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Папка настроек"); }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Сбросить все настройки к значениям по умолчанию? Раскладки и калибровки сохранятся.", "Настройки",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.ResetSettings();
        Load();
    }
}
