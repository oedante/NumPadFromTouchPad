using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using NumPad.Core.Engine;

namespace NumPad.App.Services;

/// <summary>Значок в трее (Windows), строке меню (macOS) или области уведомлений (Linux, через StatusNotifier/AppIndicator).</summary>
public sealed class Tray : IDisposable
{
    private readonly Application _app;
    private readonly AppController _controller;
    private readonly TrayIcon _icon;
    private readonly WindowIcon _iconOn;
    private readonly WindowIcon _iconOff;
    private readonly NativeMenuItem _toggle;
    private readonly NativeMenuItem _layouts;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public Tray(Application app, AppController controller)
    {
        _app = app;
        _controller = controller;
        _iconOn = new WindowIcon(AssetLoader.Open(new Uri("avares://NumPadFromTouchPad/Assets/tray_on.png")));
        _iconOff = new WindowIcon(AssetLoader.Open(new Uri("avares://NumPadFromTouchPad/Assets/tray_off.png")));

        _toggle = new NativeMenuItem("Включить NumPad");
        _toggle.Click += (_, _) => _controller.ToggleMode("tray");
        _layouts = new NativeMenuItem("Раскладка") { Menu = new NativeMenu() };
        var open = new NativeMenuItem("Открыть окно");
        open.Click += (_, _) => OpenRequested?.Invoke();
        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new NativeMenu();
        menu.Add(_toggle);
        menu.Add(_layouts);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(open);
        menu.Add(exit);

        _icon = new TrayIcon { Icon = _iconOff, ToolTipText = "NumPad from TouchPad", Menu = menu, IsVisible = true };
        // Щелчок открывает окно, а не переключает режим — защита от случайного переключения.
        _icon.Clicked += (_, _) => OpenRequested?.Invoke();
        TrayIcon.SetIcons(app, new TrayIcons { _icon });

        _controller.StateChanged += Refresh;
        _controller.ModeChanged += OnModeChanged;
        Refresh();
    }

    private void Refresh()
    {
        bool on = _controller.Mode.IsNumPad;
        _icon.Icon = on ? _iconOn : _iconOff;
        _toggle.Header = (on ? "Выключить NumPad" : "Включить NumPad") + $" ({_controller.CurrentHotkeyText})";
        string text = _controller.InputError is not null ? "NumPad: тачпад недоступен"
            : on ? "NumPad: включён" : "NumPad: выключен";
        _icon.ToolTipText = $"{text} — {_controller.ActiveLayout.Name}";

        var menu = _layouts.Menu!;
        menu.Items.Clear();
        foreach (var l in _controller.Layouts)
        {
            string id = l.Id;
            var item = new NativeMenuItem(l.Name)
            {
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = string.Equals(id, _controller.ActiveLayout.Id, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => _controller.SetActiveLayout(id);
            menu.Add(item);
        }
    }

    private void OnModeChanged(InputMode mode, string reason)
    {
        // Системные всплывающие уведомления в Avalonia не кроссплатформенны: состояние видно по значку и подсказке.
        Refresh();
    }

    public void Dispose()
    {
        _controller.StateChanged -= Refresh;
        _controller.ModeChanged -= OnModeChanged;
        _icon.IsVisible = false;
        TrayIcon.SetIcons(_app, new TrayIcons());
        _icon.Dispose();
    }
}
