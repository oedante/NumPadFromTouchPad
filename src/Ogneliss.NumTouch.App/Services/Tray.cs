using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Ogneliss.NumTouch.App.Services;

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
    private readonly NativeMenuItem _open;
    private readonly NativeMenuItem _exit;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public Tray(Application app, AppController controller)
    {
        _app = app;
        _controller = controller;
        // Windows: ICO с размерами 16…48 — система берёт точный размер под масштаб экрана (без размытия);
        // Linux/macOS: PNG 64 px.
        string ext = OperatingSystem.IsWindows() ? "ico" : "png";
        _iconOn = new WindowIcon(AssetLoader.Open(new Uri($"avares://OgnelissNumTouch/Assets/tray_on.{ext}")));
        _iconOff = new WindowIcon(AssetLoader.Open(new Uri($"avares://OgnelissNumTouch/Assets/tray_off.{ext}")));

        _toggle = new NativeMenuItem();
        _toggle.Click += (_, _) => _controller.ToggleMode("tray");
        _layouts = new NativeMenuItem { Menu = new NativeMenu() };
        _open = new NativeMenuItem();
        _open.Click += (_, _) => OpenRequested?.Invoke();
        _exit = new NativeMenuItem();
        _exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new NativeMenu();
        menu.Add(_toggle);
        menu.Add(_layouts);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_open);
        menu.Add(_exit);

        _icon = new TrayIcon { Icon = _iconOff, ToolTipText = "Ogneliss NumTouch", Menu = menu, IsVisible = true };
        // Щелчок открывает окно, а не переключает режим — защита от случайного переключения.
        _icon.Clicked += (_, _) => OpenRequested?.Invoke();
        TrayIcon.SetIcons(app, new TrayIcons { _icon });

        _controller.StateChanged += Refresh;
        Refresh();
    }

    /// <summary>Обновить значок и меню (в том числе после смены языка).</summary>
    public void Refresh()
    {
        bool on = _controller.Mode.IsNumPad;
        _icon.Icon = on ? _iconOn : _iconOff;
        _toggle.Header = (on ? L.T("Turn NumPad off") : L.T("Turn NumPad on")) + $" ({_controller.CurrentHotkeyText})";
        _layouts.Header = L.T("Layout");
        _open.Header = L.T("Open window");
        _exit.Header = L.T("Exit");
        string text = _controller.InputError is not null ? L.T("NumPad: touchpad unavailable")
            : on ? L.T("NumPad: on") : L.T("NumPad: off");
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

    
    public void Dispose()
    {
        _controller.StateChanged -= Refresh;
                _icon.IsVisible = false;
        TrayIcon.SetIcons(_app, new TrayIcons());
        _icon.Dispose();
    }
}
