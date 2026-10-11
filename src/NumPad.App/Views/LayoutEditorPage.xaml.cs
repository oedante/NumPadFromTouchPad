using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NumPad.App.Controls;
using NumPad.App.Input;
using NumPad.App.Services;
using NumPad.Core.Config;
using NumPad.Core.Engine;
using NumPad.Core.Keys;
using NumPad.Core.Layouts;

namespace NumPad.App.Views;

/// <summary>Визуальный редактор раскладок: работает с копией раскладки, сохраняет через ConfigStore с валидацией.</summary>
public partial class LayoutEditorPage : UserControl, IPage, IDisposable
{
    private sealed record KeyItem(string Name, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly AppController _app;
    private readonly List<KeyItem> _keyItems;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private Layout _working = BuiltInLayouts.Num20();
    private string? _sourceId;
    private bool _dirty;
    private bool _loading;
    private bool _live;

    public LayoutEditorPage(AppController app)
    {
        _app = app;
        InitializeComponent();
        _keyItems = KeyCatalog.All
            .OrderBy(k => k.Group).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .Select(k => new KeyItem(k.Name, $"{k.DisplayName}   ·   {GroupName(k.Group)}"))
            .ToList();
        KeyBox.ItemsSource = _keyItems;

        EditorCanvas.IsEditable = true;
        EditorCanvas.ShowGrid = true;
        EditorCanvas.SelectionChanged += _ => LoadKeyFields();
        EditorCanvas.EditStarting += PushUndo;
        EditorCanvas.Editing += () => { LoadKeyFields(); };
        EditorCanvas.EditCompleted += () => { MarkDirty(); Validate(); };
        PreviewKeyDown += OnPreviewKeyDown;

        LoadLayoutList(_app.ActiveLayout.Id);
    }

    private static string GroupName(KeyGroup g) => g switch
    {
        KeyGroup.Numpad => "цифровой блок",
        KeyGroup.Navigation => "навигация",
        KeyGroup.Editing => "редактирование",
        KeyGroup.Function => "F-клавиши",
        KeyGroup.Modifier => "модификатор",
        KeyGroup.Letter => "буква",
        KeyGroup.Digit => "цифра",
        KeyGroup.Symbol => "символ",
        KeyGroup.Media => "мультимедиа",
        _ => "системная",
    };

    // ================================================================= IPage

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
    }

    public bool OnLeaving()
    {
        if (_dirty && IsVisible)
        {
            var r = MessageBox.Show("Есть несохранённые изменения раскладки. Сохранить?", "Раскладки",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes && !TrySave(apply: false)) return false;
            if (r == MessageBoxResult.No) Revert();
        }
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
        EditorCanvas.Contacts = Array.Empty<ActiveContactInfo>();
        return true;
    }

    public void Dispose()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
    }

    // ================================================================= список раскладок

    private void LoadLayoutList(string? selectId)
    {
        _loading = true;
        LayoutBox.ItemsSource = null;
        LayoutBox.ItemsSource = _app.Layouts;
        LayoutBox.SelectedItem = _app.Layouts.FirstOrDefault(l => l.Id == selectId) ?? _app.Layouts.FirstOrDefault();
        _loading = false;
        if (LayoutBox.SelectedItem is Layout l) Edit(l);
    }

    private void Edit(Layout source)
    {
        _working = source.Clone();
        _sourceId = source.Id;
        _undo.Clear();
        _redo.Clear();
        _dirty = false;
        EditorCanvas.Layout = _working;
        EditorCanvas.SelectedKeyId = null;
        LoadLayoutFields();
        LoadKeyFields();
        Validate();
        UpdateButtons();
    }

    private void OnLayoutSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LayoutBox.SelectedItem is not Layout l || l.Id == _sourceId && !_dirty) return;
        if (_dirty)
        {
            var r = MessageBox.Show("Сохранить изменения текущей раскладки?", "Раскладки", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !TrySave(apply: false)))
            {
                _loading = true;
                LayoutBox.SelectedItem = _app.Layouts.FirstOrDefault(x => x.Id == _sourceId);
                _loading = false;
                return;
            }
            // при сохранении список перезагружен — найти выбранную заново
            l = _app.Layouts.FirstOrDefault(x => x.Id == l.Id) ?? l;
        }
        Edit(l);
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var ids = _app.Layouts.Select(l => l.Id).ToList();
        var layout = new Layout
        {
            Id = ConfigStore.UniqueId("my-layout", ids),
            Name = "Новая раскладка",
            AspectRatio = _app.ActiveDevice?.AspectRatio ?? 1.6,
            Gap = 0.01,
            Keys = { new KeyZone { Id = "k1", Label = "1", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.25, Action = KeyAction.ForKey("Numpad1") } },
        };
        StartUnsaved(layout);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var ids = _app.Layouts.Select(l => l.Id).ToList();
        var copy = _working.Clone();
        copy.Id = ConfigStore.UniqueId(_working.Id + "-copy", ids);
        copy.Name = _working.Name + " (копия)";
        copy.IsBuiltIn = false;
        StartUnsaved(copy);
    }

    /// <summary>Новая раскладка, ещё не сохранённая на диск.</summary>
    private void StartUnsaved(Layout layout)
    {
        _working = layout;
        _sourceId = layout.Id;
        _undo.Clear();
        _redo.Clear();
        EditorCanvas.Layout = _working;
        EditorCanvas.SelectedKeyId = _working.Keys.FirstOrDefault()?.Id;
        LoadLayoutFields();
        LoadKeyFields();
        MarkDirty();
        Validate();
    }

    private void OnDeleteLayout(object sender, RoutedEventArgs e)
    {
        bool builtIn = BuiltInLayouts.Get(_working.Id) is not null;
        if (builtIn) { OnReset(sender, e); return; }
        if (MessageBox.Show($"Удалить раскладку «{_working.Name}»?", "Раскладки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _app.DeleteLayout(_working.Id);
        _dirty = false;
        LoadLayoutList(_app.ActiveLayout.Id);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var builtIn = BuiltInLayouts.Get(_working.Id);
        if (builtIn is null) return;
        if (MessageBox.Show($"Вернуть стандартную раскладку «{builtIn.Name}»? Ваши изменения будут удалены.", "Раскладки",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.DeleteLayout(_working.Id); // удаление пользовательской копии = возврат к встроенной
        _dirty = false;
        LoadLayoutList(builtIn.Id);
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "Раскладка NumPad (*.json)|*.json", Title = "Импорт раскладки" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var layout = _app.Store.ImportLayout(dlg.FileName, _app.Layouts.Select(l => l.Id));
            StartUnsaved(layout);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось импортировать раскладку: " + ex.Message, "Импорт", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Раскладка NumPad (*.json)|*.json", FileName = _working.Id + ".json", Title = "Экспорт раскладки" };
        if (dlg.ShowDialog() != true) return;
        try { _app.Store.ExportLayout(_working, dlg.FileName); }
        catch (Exception ex) { MessageBox.Show("Не удалось сохранить файл: " + ex.Message, "Экспорт", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        return MessageBox.Show("Несохранённые изменения будут потеряны. Продолжить?", "Раскладки",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    // ================================================================= сохранение

    private void OnSave(object sender, RoutedEventArgs e) => TrySave(apply: false);
    private void OnSaveApply(object sender, RoutedEventArgs e) => TrySave(apply: true);
    private void OnRevert(object sender, RoutedEventArgs e) => Revert();

    private void Revert()
    {
        var src = _app.Layouts.FirstOrDefault(l => l.Id == _sourceId);
        if (src is null) LoadLayoutList(_app.ActiveLayout.Id);
        else Edit(src);
    }

    private bool TrySave(bool apply)
    {
        CommitFocusedField();
        var issues = LayoutValidator.Validate(_working);
        if (LayoutValidator.HasErrors(issues))
        {
            MessageBox.Show("Раскладку нельзя сохранить:\n\n" + string.Join("\n", issues.Where(i => i.Severity == IssueSeverity.Error).Take(10)),
                "Раскладки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try
        {
            string id = _working.Id;
            _app.SaveLayout(_working);
            if (apply) _app.SetActiveLayout(id);
            _dirty = false;
            LoadLayoutList(id);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось сохранить раскладку: " + ex.Message, "Раскладки", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void CommitFocusedField()
    {
        if (Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ApplyLayoutFields();
        ApplyKeyFields();
    }

    // ================================================================= undo / redo

    private string Snapshot() => ConfigStore.SerializeLayout(_working);

    private void PushUndo()
    {
        _undo.Push(Snapshot());
        if (_undo.Count > 200)
        {
            var keep = _undo.Take(150).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep) _undo.Push(s);
        }
        _redo.Clear();
        UpdateButtons();
    }

    private void Restore(string snapshot)
    {
        string? selected = EditorCanvas.SelectedKeyId;
        var restored = ConfigStore.ParseLayout(snapshot);
        restored.IsBuiltIn = _working.IsBuiltIn;
        _working = restored;
        EditorCanvas.Layout = _working;
        EditorCanvas.SelectedKeyId = _working.Keys.Any(k => k.Id == selected) ? selected : null;
        LoadLayoutFields();
        LoadKeyFields();
        MarkDirty();
        Validate();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    // ================================================================= клавиши

    private KeyZone? Selected => _working.Keys.FirstOrDefault(k => k.Id == EditorCanvas.SelectedKeyId);

    private void OnAddKey(object sender, RoutedEventArgs e) => AddKey(below: false);
    private void OnAddKeyBelow(object sender, RoutedEventArgs e) => AddKey(below: true);

    private void AddKey(bool below)
    {
        PushUndo();
        var sel = Selected;
        double w = sel?.Width ?? 0.15, h = sel?.Height ?? 0.2;
        double x = sel is null ? 0.05 : below ? sel.X : sel.X + sel.Width + _working.Gap;
        double y = sel is null ? 0.05 : below ? sel.Y + sel.Height + _working.Gap * _working.AspectRatio : sel.Y;
        // Если не помещается — ставим в левый верхний угол; пользователь передвинет.
        if (x + w > 1) x = Math.Max(0, 1 - w);
        if (y + h > 1) y = Math.Max(0, 1 - h);
        int n = 1;
        while (_working.Keys.Any(k => k.Id == $"key{n}")) n++;
        var key = new KeyZone
        {
            Id = $"key{n}", Label = "?", X = Math.Round(x, 5), Y = Math.Round(y, 5), Width = w, Height = h,
            Action = KeyAction.ForKey("Numpad0"),
        };
        _working.Keys.Add(key);
        EditorCanvas.SelectedKeyId = key.Id;
        EditorCanvas.Refresh();
        MarkDirty();
        Validate();
        LabelBox.Focus();
        LabelBox.SelectAll();
    }

    private void OnDeleteKey(object sender, RoutedEventArgs e) => DeleteSelectedKey();

    private void DeleteSelectedKey()
    {
        var sel = Selected;
        if (sel is null) return;
        PushUndo();
        _working.Keys.Remove(sel);
        EditorCanvas.SelectedKeyId = null;
        EditorCanvas.Refresh();
        MarkDirty();
        Validate();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.Z) { OnUndo(this, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { OnRedo(this, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is not LayoutCanvas) return;

        var sel = Selected;
        if (sel is null) return;
        if (e.Key == Key.Delete) { DeleteSelectedKey(); e.Handled = true; return; }

        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.02 : 0.004;
        double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return;
        PushUndo();
        sel.X = Math.Round(Math.Clamp(sel.X + dx, 0, 1 - sel.Width), 5);
        sel.Y = Math.Round(Math.Clamp(sel.Y + dy, 0, 1 - sel.Height), 5);
        EditorCanvas.Refresh();
        LoadKeyFields();
        MarkDirty();
        Validate();
        e.Handled = true;
    }

    // ================================================================= поля раскладки

    private void LoadLayoutFields()
    {
        _loading = true;
        NameBox.Text = _working.Name;
        GapBox.Text = Pct(_working.Gap);
        AspectBox.Text = _working.AspectRatio.ToString("0.###", CultureInfo.CurrentCulture);
        PointerOutsideBox.IsChecked = _working.AllowPointerOutsideKeys;
        bool builtIn = BuiltInLayouts.Get(_working.Id) is not null;
        ResetButton.IsEnabled = builtIn;
        DeleteLayoutButton.Content = builtIn ? "Удалить изменения" : "Удалить";
        _loading = false;
    }

    private void ApplyLayoutFields()
    {
        if (_loading) return;
        string name = NameBox.Text.Trim();
        double? gap = ParsePct(GapBox.Text);
        double? aspect = ParseNum(AspectBox.Text);
        bool changed = name != _working.Name
                       || gap is double g && Math.Abs(g - _working.Gap) > 1e-9
                       || aspect is double a && Math.Abs(a - _working.AspectRatio) > 1e-9;
        if (!changed) { LoadLayoutFields(); return; }
        PushUndo();
        if (name.Length > 0) _working.Name = name;
        if (gap is double g2) _working.Gap = Math.Clamp(g2, 0, 0.2);
        if (aspect is double a2 && a2 > 0.1 && a2 < 10) _working.AspectRatio = Math.Round(a2, 4);
        EditorCanvas.Refresh();
        LoadLayoutFields();
        MarkDirty();
        Validate();
    }

    private void OnLayoutFieldCommit(object sender, RoutedEventArgs e) => ApplyLayoutFields();

    private void OnLayoutCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        PushUndo();
        _working.AllowPointerOutsideKeys = PointerOutsideBox.IsChecked == true;
        EditorCanvas.Refresh();
        MarkDirty();
    }

    private void OnSnapChanged(object sender, RoutedEventArgs e)
    {
        if (EditorCanvas is not null) EditorCanvas.ShowGrid = SnapBox.IsChecked == true;
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == NameBox || sender == GapBox || sender == AspectBox) ApplyLayoutFields();
        else ApplyKeyFields();
        e.Handled = true;
    }

    // ================================================================= поля клавиши

    private void LoadKeyFields()
    {
        _loading = true;
        try
        {
            var k = Selected;
            KeyPanel.Visibility = k is null ? Visibility.Collapsed : Visibility.Visible;
            NoSelection.Visibility = k is null ? Visibility.Visible : Visibility.Collapsed;
            DeleteKeyButton.IsEnabled = k is not null;
            if (k is null) { KeyHeader.Text = "Клавиша"; return; }

            KeyHeader.Text = $"Клавиша «{k.Label}»";
            LabelBox.Text = k.Label;
            KindBox.SelectedItem = KindBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == k.Action.Kind.ToString());
            KeyBox.SelectedItem = KeyCatalog.TryGet(k.Action.Key, out var def) ? _keyItems.FirstOrDefault(i => i.Name == def.Name) : null;
            var mods = k.Action.Modifiers.Select(m => KeyCatalog.TryGet(m, out var md) ? md.Name : m).ToList();
            ModCtrl.IsChecked = mods.Any(m => m.Contains("Ctrl"));
            ModShift.IsChecked = mods.Any(m => m.Contains("Shift"));
            ModAlt.IsChecked = mods.Any(m => m.Contains("Alt"));
            ModWin.IsChecked = mods.Any(m => m.Contains("Win"));
            TextValueBox.Text = k.Action.Text ?? "";
            XBox.Text = Pct(k.X);
            YBox.Text = Pct(k.Y);
            WBox.Text = Pct(k.Width);
            HBox.Text = Pct(k.Height);
            EnabledBox.IsChecked = k.Enabled;
            RepeatBox.IsChecked = k.Repeat;
            UpdateActionPanels(k.Action.Kind);
        }
        finally { _loading = false; }
    }

    private void UpdateActionPanels(KeyActionKind kind)
    {
        KeyChoicePanel.Visibility = kind is KeyActionKind.Key or KeyActionKind.Combo ? Visibility.Visible : Visibility.Collapsed;
        ModifiersPanel.Visibility = kind == KeyActionKind.Combo ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = kind == KeyActionKind.Text ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyKeyFields()
    {
        if (_loading) return;
        var k = Selected;
        if (k is null) return;
        string label = LabelBox.Text;
        string text = TextValueBox.Text;
        double? x = ParsePct(XBox.Text), y = ParsePct(YBox.Text), w = ParsePct(WBox.Text), h = ParsePct(HBox.Text);
        bool changed = label != k.Label
                       || (k.Action.Kind == KeyActionKind.Text && text != (k.Action.Text ?? ""))
                       || Diff(x, k.X) || Diff(y, k.Y) || Diff(w, k.Width) || Diff(h, k.Height);
        if (!changed) { LoadKeyFields(); return; }
        PushUndo();
        k.Label = label;
        if (k.Action.Kind == KeyActionKind.Text) k.Action.Text = text;
        if (w is double wv) k.Width = Math.Round(Math.Clamp(wv, LayoutValidator.MinKeySize, 1), 5);
        if (h is double hv) k.Height = Math.Round(Math.Clamp(hv, LayoutValidator.MinKeySize, 1), 5);
        if (x is double xv) k.X = Math.Round(Math.Clamp(xv, 0, 1 - k.Width), 5);
        if (y is double yv) k.Y = Math.Round(Math.Clamp(yv, 0, 1 - k.Height), 5);
        EditorCanvas.Refresh();
        LoadKeyFields();
        MarkDirty();
        Validate();
    }

    private static bool Diff(double? v, double current) => v is double d && Math.Abs(d - current) > 1e-6;

    private void OnKeyFieldCommit(object sender, RoutedEventArgs e) => ApplyKeyFields();

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } k || KindBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (!Enum.TryParse<KeyActionKind>(tag, out var kind) || kind == k.Action.Kind) return;
        PushUndo();
        k.Action.Kind = kind;
        if (kind is KeyActionKind.Key or KeyActionKind.Combo && !KeyCatalog.IsKnown(k.Action.Key)) k.Action.Key = "Numpad0";
        if (kind == KeyActionKind.Combo && k.Action.Modifiers.Count == 0) k.Action.Modifiers.Add("Ctrl");
        if (kind != KeyActionKind.Combo) k.Action.Modifiers.Clear();
        if (kind == KeyActionKind.Text && string.IsNullOrEmpty(k.Action.Text)) k.Action.Text = k.Label;
        LoadKeyFields();
        MarkDirty();
        Validate();
    }

    private void OnKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } k || KeyBox.SelectedItem is not KeyItem item || item.Name == k.Action.Key) return;
        PushUndo();
        string? oldDisplay = KeyCatalog.TryGet(k.Action.Key, out var oldDef) ? oldDef.DisplayName : null;
        k.Action.Key = item.Name;
        // Подпись по умолчанию следует за назначением, если пользователь её не менял.
        if (k.Label == "?" || k.Label == oldDisplay || k.Label == k.Action.Key)
            k.Label = KeyCatalog.Get(item.Name).DisplayName.Replace("Num ", "");
        EditorCanvas.Refresh();
        LoadKeyFields();
        MarkDirty();
        Validate();
    }

    private void OnModifierChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Selected is not { } k) return;
        PushUndo();
        k.Action.Modifiers.Clear();
        if (ModCtrl.IsChecked == true) k.Action.Modifiers.Add("Ctrl");
        if (ModShift.IsChecked == true) k.Action.Modifiers.Add("Shift");
        if (ModAlt.IsChecked == true) k.Action.Modifiers.Add("Alt");
        if (ModWin.IsChecked == true) k.Action.Modifiers.Add("Win");
        MarkDirty();
        Validate();
    }

    private void OnKeyCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Selected is not { } k) return;
        PushUndo();
        k.Enabled = EnabledBox.IsChecked == true;
        k.Repeat = RepeatBox.IsChecked == true;
        EditorCanvas.Refresh();
        MarkDirty();
        Validate();
    }

    // ================================================================= живой просмотр

    private void OnFrame(TouchpadDeviceInfo device, TouchFrame frame)
    {
        if (_app.ActiveDevice?.Handle != device.Handle) return;
        var hit = new HitTester(_working);
        EditorCanvas.ActiveKeyIds.Clear();
        var list = new List<ActiveContactInfo>();
        foreach (var c in frame.Contacts.Where(c => c.Tip))
        {
            var p = _app.Engine.ToArea(c.Position);
            var z = hit.Hit(p);
            if (z is not null) EditorCanvas.ActiveKeyIds.Add(z.Id);
            list.Add(new ActiveContactInfo(c.Id, p, z?.Id, false, false));
        }
        EditorCanvas.Contacts = list;
    }

    // ================================================================= служебное

    private void MarkDirty()
    {
        _dirty = true;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        DirtyMark.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void Validate()
    {
        var issues = LayoutValidator.Validate(_working);
        if (issues.Count == 0)
        {
            IssuesText.Text = $"✓ Ошибок нет · клавиш: {_working.Keys.Count}";
            IssuesText.Foreground = (Brush)FindResource("OnBrush");
        }
        else
        {
            IssuesText.Text = string.Join("   ", issues.Take(6).Select(i => (i.Severity == IssueSeverity.Error ? "✕ " : "⚠ ") +
                                                                       (i.KeyId is null ? "" : $"[{i.KeyId}] ") + i.Message))
                              + (issues.Count > 6 ? $"   … и ещё {issues.Count - 6}" : "");
            IssuesText.Foreground = LayoutValidator.HasErrors(issues) ? (Brush)FindResource("ErrorBrush") : (Brush)FindResource("WarningBrush");
        }
    }

    private static string Pct(double v) => (v * 100).ToString("0.##", CultureInfo.CurrentCulture);

    private static double? ParseNum(string s)
    {
        s = s.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    private static double? ParsePct(string s) => ParseNum(s) is double v ? v / 100 : null;
}
