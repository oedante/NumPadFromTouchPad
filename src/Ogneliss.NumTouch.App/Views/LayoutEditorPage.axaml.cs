using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ogneliss.NumTouch.App.Controls;
using Ogneliss.NumTouch.App.Services;
using Ogneliss.NumTouch.Core.Config;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Keys;
using Ogneliss.NumTouch.Core.Layouts;
using Ogneliss.NumTouch.Platform;

namespace Ogneliss.NumTouch.App.Views;

/// <summary>
/// Визуальный редактор раскладок: расположение и размер клавиш, их подписи и назначения (клавиша, сочетание, текст),
/// отключение зон, промежутки, импорт/экспорт. Работает с копией раскладки; сохраняет через ConfigStore с валидацией.
/// </summary>
public partial class LayoutEditorPage : UserControl, IPage, IDisposable
{
    private sealed record KeyItem(string Name, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly AppController _app;
    private readonly List<KeyItem> _keyItems = new();
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private Layout _working = BuiltInLayouts.Num20();
    private string? _sourceId;
    private bool _dirty;
    private bool _loading;
    private bool _live;
    private MenuItem _resetItem = null!;
    private MenuItem _deleteItem = null!;

    public LayoutEditorPage() : this(null!) { }

    public LayoutEditorPage(AppController app)
    {
        _app = app;
        InitializeComponent();
        if (app is null) return;

        _keyItems = KeyCatalog.All
            .OrderBy(k => k.Group).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .Select(k => new KeyItem(k.Name, $"{k.DisplayName}   ·   {GroupName(k.Group)}"))
            .ToList();
        KeyBox.ItemsSource = _keyItems;

        EditorCanvas.IsEditable = true;
        EditorCanvas.Snap = true;
        EditorCanvas.SelectionChanged += _ => LoadKeyFields();
        EditorCanvas.KeyDoubleClicked += _ => { LabelBox.Focus(); LabelBox.SelectAll(); };
        EditorCanvas.EditStarting += PushUndo;
        EditorCanvas.Editing += LoadKeyFields;
        EditorCanvas.EditCompleted += () => { MarkDirty(); Validate(); };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        LayoutBox.SelectionChanged += async (_, _) => await OnLayoutSelected();
        BuildLayoutMenu();
        // Подсказки с клавишами в обозначениях текущей ОС (⌘, ⌥ и ⌫ в macOS).
        string alt = PlatformTheme.AltKeyName, shift = PlatformTheme.ShiftKeyName;
        string del = PlatformTheme.Os == OsFamily.MacOS ? "⌫" : "Delete";
        ToolTip.SetTip(UndoButton, L.F("Undo ({0})", PlatformTheme.Shortcut("Ctrl+Z")));
        ToolTip.SetTip(RedoButton, L.F("Redo ({0})", PlatformTheme.Os == OsFamily.MacOS ? "⇧⌘Z" : "Ctrl+Y"));
        ToolTip.SetTip(DeleteKeyButton, L.F("Delete the selected key ({0})", del));
        ToolTip.SetTip(SnapToggle, L.F("Snap to neighboring keys, the layout gap, the edges and the grid. Hold {0} while dragging to place freely.", alt));
        EditorHint.Text = L.F("Drag a key to move it; drag any edge or corner to resize. Keys snap to neighbors, the layout gap and the grid; {0} places freely, {1} moves along one axis. Arrows move the selected key, {0}+arrows resize it, {2} removes it. Live touches are drawn on top.", alt, shift, del);
        SaveButton.Click += async (_, _) => await TrySave(apply: false);
        SaveApplyButton.Click += async (_, _) => await TrySave(apply: true);
        RevertButton.Click += (_, _) => Revert();
        AddKeyButton.Click += (_, _) => AddKey(below: false);
        AddKeyBelowButton.Click += (_, _) => AddKey(below: true);
        DeleteKeyButton.Click += (_, _) => DeleteSelectedKey();
        UndoButton.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        SnapToggle.Click += (_, _) => EditorCanvas.Snap = SnapToggle.IsChecked == true;
        PointerOutsideBox.Click += (_, _) => OnLayoutCheckChanged();

        foreach (var tb in new[] { NameBox, GapBox, AspectBox })
        {
            tb.LostFocus += (_, _) => ApplyLayoutFields();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyLayoutFields(); e.Handled = true; } };
        }
        foreach (var tb in new[] { LabelBox, TextValueBox, XBox, YBox, WBox, HBox })
        {
            tb.LostFocus += (_, _) => ApplyKeyFields();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyKeyFields(); e.Handled = true; } };
        }
        KindBox.SelectionChanged += (_, _) => OnKindChanged();
        KeyBox.SelectionChanged += (_, _) => OnKeyChanged();
        foreach (var cb in new[] { ModCtrl, ModShift, ModAlt, ModWin }) cb.Click += (_, _) => OnModifierChanged();
        EnabledBox.Click += (_, _) => OnKeyCheckChanged();
        RepeatBox.Click += (_, _) => OnKeyCheckChanged();

        LoadLayoutList(_app.ActiveLayout.Id);
    }

    /// <summary>Меню «Действия»: создание, копия, импорт/экспорт, возврат к стандартной, удаление.</summary>
    private void BuildLayoutMenu()
    {
        MenuItem Item(string header, Func<Task> action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += async (_, _) => await action();
            return mi;
        }
        _resetItem = Item(L.T("Restore default"), OnReset);
        _deleteItem = Item(L.T("Delete"), OnDeleteLayout);
        _deleteItem.Foreground = Ui.Brush("ErrorBrush");
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Items.Add(Item(L.T("New"), OnNew));
        flyout.Items.Add(Item(L.T("Duplicate"), OnDuplicate));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item(L.T("Import…"), OnImport));
        flyout.Items.Add(Item(L.T("Export…"), OnExport));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(_resetItem);
        flyout.Items.Add(_deleteItem);
        LayoutMenuButton.Flyout = flyout;
    }

    private static string GroupName(KeyGroup g) => g switch
    {
        KeyGroup.Numpad => L.T("numpad"),
        KeyGroup.Navigation => L.T("navigation"),
        KeyGroup.Editing => L.T("editing"),
        KeyGroup.Function => L.T("F keys"),
        KeyGroup.Modifier => L.T("modifier"),
        KeyGroup.Letter => L.T("letter"),
        KeyGroup.Digit => L.T("digit"),
        KeyGroup.Symbol => L.T("symbol"),
        KeyGroup.Media => L.T("media"),
        _ => L.T("system"),
    };

    // ================================================================= IPage

    public void OnShown()
    {
        if (!_live) { _app.FrameArrived += OnFrame; _live = true; }
        // Размер области наклейки в мм — для подсказки при изменении размера клавиши.
        var dev = _app.ActiveDevice;
        var area = _app.ActiveTransform.ToDeviceRect();
        EditorCanvas.AreaSizeMm = dev is { AspectIsPhysical: true }
            ? new Avalonia.Size(area.Width * dev.WidthMm!.Value, area.Height * dev.HeightMm!.Value)
            : null;
    }

    public async Task<bool> CanLeaveAsync()
    {
        if (!_dirty) return true;
        string save = L.T("Save"), cancel = L.T("Cancel");
        var r = await Dialogs.AskAsync(this, L.T("Layouts"), L.T("The layout has unsaved changes. Save them?"), save, L.T("Do not save"), cancel);
        if (r is null || r == cancel) return false;
        if (r == save) return await TrySave(apply: false);
        Revert();
        return true;
    }

    public void OnHidden()
    {
        if (_live) { _app.FrameArrived -= OnFrame; _live = false; }
        EditorCanvas.Contacts = Array.Empty<ActiveContactInfo>();
    }

    public void Dispose() => OnHidden();

    /// <summary>Выделить клавишу (для снимков самопроверки).</summary>
    internal void SelectKeyForPreview(string keyId)
    {
        if (_working.Keys.Any(k => k.Id == keyId)) EditorCanvas.SelectedKeyId = keyId;
    }

    /// <summary>Показать перетаскивание клавиши (для снимков самопроверки); false — завершить.</summary>
    internal void PreviewDrag(string keyId, ResizeEdges edges, double dxPx, double dyPx, bool show)
    {
        if (show) EditorCanvas.BeginPreviewDrag(keyId, edges, dxPx, dyPx);
        else EditorCanvas.EndPreviewDrag();
    }

    // ================================================================= список раскладок

    private void LoadLayoutList(string? selectId)
    {
        _loading = true;
        var items = _app.Layouts.Select(l => new Choice<Layout>(l, l.Name + (BuiltInLayouts.Get(l.Id) is not null && !l.IsBuiltIn ? " (" + L.T("modified") + ")" : ""))).ToList();
        LayoutBox.ItemsSource = items;
        var sel = items.FirstOrDefault(i => i.Value.Id == selectId) ?? items.FirstOrDefault();
        LayoutBox.SelectedItem = sel;
        _loading = false;
        if (sel is not null) Edit(sel.Value);
    }

    private void SelectInList(string? id)
    {
        _loading = true;
        LayoutBox.SelectedItem = (LayoutBox.ItemsSource as IEnumerable<Choice<Layout>>)?.FirstOrDefault(i => i.Value.Id == id);
        _loading = false;
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

    private async Task OnLayoutSelected()
    {
        if (_loading || LayoutBox.SelectedItem is not Choice<Layout> choice) return;
        var l = choice.Value;
        if (l.Id == _sourceId && !_dirty) return;
        if (_dirty)
        {
            string save = L.T("Save"), cancel = L.T("Cancel");
            var r = await Dialogs.AskAsync(this, L.T("Layouts"), L.T("Save the changes to the current layout?"), save, L.T("Do not save"), cancel);
            if (r is null || r == cancel || (r == save && !await TrySave(apply: false)))
            {
                SelectInList(_sourceId);
                return;
            }
            l = _app.Layouts.FirstOrDefault(x => x.Id == l.Id) ?? l;
            SelectInList(l.Id);
        }
        Edit(l);
    }

    private async Task OnNew()
    {
        if (!await ConfirmDiscard()) return;
        var ids = _app.Layouts.Select(l => l.Id).ToList();
        var layout = new Layout
        {
            Id = ConfigStore.UniqueId("my-layout", ids),
            Name = L.T("New layout"),
            AspectRatio = Math.Round(_app.ActiveDevice?.AspectRatio ?? 1.6, 3),
            Gap = 0.01,
            Keys = { new KeyZone { Id = "k1", Label = "1", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.25, Action = KeyAction.ForKey("Numpad1") } },
        };
        StartUnsaved(layout);
    }

    private async Task OnDuplicate()
    {
        if (!await ConfirmDiscard()) return;
        var ids = _app.Layouts.Select(l => l.Id).ToList();
        var copy = _working.Clone();
        copy.Id = ConfigStore.UniqueId(_working.Id + "-copy", ids);
        copy.Name = _working.Name + " (" + L.T("copy") + ")";
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

    private async Task OnDeleteLayout()
    {
        if (BuiltInLayouts.Get(_working.Id) is not null) { await OnReset(); return; }
        if (!_app.Layouts.Any(l => l.Id == _working.Id)) { _dirty = false; LoadLayoutList(_app.ActiveLayout.Id); return; } // ещё не сохранена
        if (!await Dialogs.ConfirmAsync(this, L.T("Layouts"), L.F("Delete the layout “{0}”?", _working.Name))) return;
        _app.DeleteLayout(_working.Id);
        _dirty = false;
        LoadLayoutList(_app.ActiveLayout.Id);
    }

    private async Task OnReset()
    {
        var builtIn = BuiltInLayouts.Get(_working.Id);
        if (builtIn is null) return;
        if (!await Dialogs.ConfirmAsync(this, L.T("Layouts"), L.F("Restore the default layout “{0}”? Your changes will be deleted.", builtIn.Name))) return;
        _app.DeleteLayout(_working.Id); // удаление пользовательской копии = возврат к встроенной
        _dirty = false;
        LoadLayoutList(builtIn.Id);
    }

    private async Task OnImport()
    {
        if (!await ConfirmDiscard()) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Import layout"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(L.T("NumTouch layout")) { Patterns = new[] { "*.json" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try { StartUnsaved(_app.Store.ImportLayout(path, _app.Layouts.Select(l => l.Id))); }
        catch (Exception ex) { await Dialogs.InfoAsync(this, L.T("Import"), L.F("Could not import the layout: {0}", ex.Message)); }
    }

    private async Task OnExport()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Export layout"),
            SuggestedFileName = _working.Id + ".json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType(L.T("NumTouch layout")) { Patterns = new[] { "*.json" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        try { _app.Store.ExportLayout(_working, path); }
        catch (Exception ex) { await Dialogs.InfoAsync(this, L.T("Export"), L.F("Could not save the file: {0}", ex.Message)); }
    }

    private async Task<bool> ConfirmDiscard() =>
        !_dirty || await Dialogs.ConfirmAsync(this, L.T("Layouts"), L.T("Unsaved changes will be lost. Continue?"));

    // ================================================================= сохранение

    private void Revert()
    {
        var src = _app.Layouts.FirstOrDefault(l => l.Id == _sourceId);
        if (src is null) LoadLayoutList(_app.ActiveLayout.Id);
        else Edit(src);
    }

    private async Task<bool> TrySave(bool apply)
    {
        ApplyLayoutFields();
        ApplyKeyFields();
        var issues = LayoutValidator.Validate(_working);
        if (LayoutValidator.HasErrors(issues))
        {
            await Dialogs.InfoAsync(this, L.T("Layouts"), L.T("The layout cannot be saved:") + "\n\n" +
                string.Join("\n", issues.Where(i => i.Severity == IssueSeverity.Error).Take(10)));
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
            await Dialogs.InfoAsync(this, L.T("Layouts"), L.F("Could not save the layout: {0}", ex.Message));
            return false;
        }
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

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    // ================================================================= клавиши

    private KeyZone? Selected => _working.Keys.FirstOrDefault(k => k.Id == EditorCanvas.SelectedKeyId);

    private void AddKey(bool below)
    {
        PushUndo();
        var sel = Selected;
        double w = sel?.Width ?? 0.15, h = sel?.Height ?? 0.2;
        double x = sel is null ? 0.05 : below ? sel.X : sel.X + sel.Width + _working.Gap;
        double y = sel is null ? 0.05 : below ? sel.Y + sel.Height + _working.Gap * _working.AspectRatio : sel.Y;
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

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { Undo(); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))) { Redo(); e.Handled = true; return; }
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is not LayoutCanvas) return;

        var sel = Selected;
        if (sel is null) return;
        if (e.Key is Key.Delete or Key.Back) { DeleteSelectedKey(); e.Handled = true; return; }

        // Стрелки — сдвиг на 0,5 % (Shift — 2,5 %); Alt+стрелки — изменение ширины/высоты.
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.025 : 0.005;
        double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return;
        PushUndo();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            sel.Width = Math.Round(Math.Clamp(sel.Width + dx, LayoutValidator.MinKeySize, 1 - sel.X), 5);
            sel.Height = Math.Round(Math.Clamp(sel.Height + dy, LayoutValidator.MinKeySize, 1 - sel.Y), 5);
        }
        else
        {
            sel.X = Math.Round(Math.Clamp(sel.X + dx, 0, 1 - sel.Width), 5);
            sel.Y = Math.Round(Math.Clamp(sel.Y + dy, 0, 1 - sel.Height), 5);
        }
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
        _resetItem.IsEnabled = builtIn;
        _deleteItem.Header = builtIn ? L.T("Delete changes") : L.T("Delete");
        _loading = false;
    }

    private void ApplyLayoutFields()
    {
        if (_loading) return;
        string name = (NameBox.Text ?? "").Trim();
        double? gap = ParsePct(GapBox.Text);
        double? aspect = ParseNum(AspectBox.Text);
        bool changed = (name.Length > 0 && name != _working.Name)
                       || gap is double g && Math.Abs(g - _working.Gap) > 1e-9
                       || aspect is double a && a > 0.1 && a < 10 && Math.Abs(a - _working.AspectRatio) > 1e-9;
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

    private void OnLayoutCheckChanged()
    {
        PushUndo();
        _working.AllowPointerOutsideKeys = PointerOutsideBox.IsChecked == true;
        EditorCanvas.Refresh();
        MarkDirty();
    }

    // ================================================================= поля клавиши

    private void LoadKeyFields()
    {
        _loading = true;
        try
        {
            var k = Selected;
            KeyPanel.IsVisible = k is not null;
            NoSelection.IsVisible = k is null;
            DeleteKeyButton.IsEnabled = k is not null;
            if (k is null) { KeyHeader.Text = L.T("Key"); return; }

            KeyHeader.Text = L.F("Key “{0}”", k.Label);
            LabelBox.Text = k.Label;
            KindBox.SelectedItem = KindBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == k.Action.Kind.ToString());
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
        KeyChoicePanel.IsVisible = kind is KeyActionKind.Key or KeyActionKind.Combo;
        ModifiersPanel.IsVisible = kind == KeyActionKind.Combo;
        TextPanel.IsVisible = kind == KeyActionKind.Text;
    }

    private void ApplyKeyFields()
    {
        if (_loading) return;
        var k = Selected;
        if (k is null) return;
        string label = LabelBox.Text ?? "";
        string text = TextValueBox.Text ?? "";
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

    private void OnKindChanged()
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

    private void OnKeyChanged()
    {
        if (_loading || Selected is not { } k || KeyBox.SelectedItem is not KeyItem item || item.Name == k.Action.Key) return;
        PushUndo();
        string? oldDisplay = KeyCatalog.TryGet(k.Action.Key, out var oldDef) ? oldDef.DisplayName.Replace("Num ", "") : null;
        k.Action.Key = item.Name;
        // Подпись следует за назначением, если пользователь её не менял.
        if (k.Label == "?" || k.Label == oldDisplay || k.Label == oldDef?.Name)
            k.Label = KeyCatalog.Get(item.Name).DisplayName.Replace("Num ", "");
        EditorCanvas.Refresh();
        LoadKeyFields();
        MarkDirty();
        Validate();
    }

    private void OnModifierChanged()
    {
        if (Selected is not { } k) return;
        PushUndo();
        k.Action.Modifiers.Clear();
        if (ModCtrl.IsChecked == true) k.Action.Modifiers.Add("Ctrl");
        if (ModShift.IsChecked == true) k.Action.Modifiers.Add("Shift");
        if (ModAlt.IsChecked == true) k.Action.Modifiers.Add("Alt");
        if (ModWin.IsChecked == true) k.Action.Modifiers.Add("Win");
        MarkDirty();
        Validate();
    }

    private void OnKeyCheckChanged()
    {
        if (Selected is not { } k) return;
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
        if (_app.ActiveDevice?.Id != device.Id) return;
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
        DirtyMark.IsVisible = _dirty;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void Validate()
    {
        var issues = LayoutValidator.Validate(_working);
        // Клавиши, которых нет на текущей платформе (например, Print Screen на Mac), — предупреждение.
        var missing = _working.Keys.Where(k => k.Action.Kind is KeyActionKind.Key or KeyActionKind.Combo && KeyCatalog.TryGet(k.Action.Key, out var d)
                                               && !IsAvailableOnThisPlatform(d)).Select(k => k.Label).ToList();
        var parts = issues.Take(6).Select(i => (i.Severity == IssueSeverity.Error ? "✕ " : "⚠ ") + (i.KeyId is null ? "" : $"[{i.KeyId}] ") + i.Message).ToList();
        if (missing.Count > 0) parts.Add("⚠ " + L.F("Not available on this OS: {0}", string.Join(", ", missing)));
        if (parts.Count == 0)
        {
            IssuesText.Text = "✓ " + L.F("No errors · keys: {0}", _working.Keys.Count);
            IssuesText.Foreground = Ui.Brush("OnBrush");
        }
        else
        {
            IssuesText.Text = string.Join("   ", parts) + (issues.Count > 6 ? "   … " + L.F("and {0} more", issues.Count - 6) : "");
            IssuesText.Foreground = Ui.Brush(LayoutValidator.HasErrors(issues) ? "ErrorBrush" : "WarningBrush");
        }
    }

    private static bool IsAvailableOnThisPlatform(KeyDefinition d) =>
        OperatingSystem.IsMacOS() ? PlatformKeyCodes.IsAvailableOnMac(d)
        : OperatingSystem.IsLinux() ? PlatformKeyCodes.Linux.ContainsKey(d.Name)
        : true;

    private static string Pct(double v) => (v * 100).ToString("0.##", CultureInfo.CurrentCulture);

    private static double? ParseNum(string? s)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    private static double? ParsePct(string? s) => ParseNum(s) is double v ? v / 100 : null;
}
