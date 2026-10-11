using System.Windows;
using NumPad.Core.Engine;
using WinForms = System.Windows.Forms;

namespace NumPad.App.Services;

/// <summary>Значок в области уведомлений: состояние режима, быстрое переключение, выбор раскладки, выход.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly WinForms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _iconOn;
    private readonly System.Drawing.Icon _iconOff;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private readonly WinForms.ToolStripMenuItem _layoutsItem;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon(AppController app)
    {
        _app = app;
        _iconOn = LoadIcon("Assets/tray_on.ico");
        _iconOff = LoadIcon("Assets/tray_off.ico");

        var menu = new WinForms.ContextMenuStrip();
        _toggleItem = new WinForms.ToolStripMenuItem("Включить NumPad", null, (_, _) => _app.ToggleMode("tray"));
        _toggleItem.Font = new System.Drawing.Font(_toggleItem.Font, System.Drawing.FontStyle.Bold);
        _layoutsItem = new WinForms.ToolStripMenuItem("Раскладка");
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_layoutsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Открыть окно", null, (_, _) => OpenRequested?.Invoke()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke()));
        menu.Opening += (_, _) => RebuildLayouts();

        _icon = new WinForms.NotifyIcon
        {
            Icon = _iconOff,
            Visible = true,
            ContextMenuStrip = menu,
            Text = "NumPad from TouchPad",
        };
        // Одиночный щелчок открывает окно, а не переключает режим — защита от случайного переключения.
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };

        _app.StateChanged += Refresh;
        _app.ModeChanged += OnModeChanged;
        Refresh();
    }

    private static System.Drawing.Icon LoadIcon(string relative)
    {
        // Иконки встроены как ресурсы WPF (pack URI) — не зависят от текущего каталога (ошибка старого проекта).
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/{relative}", UriKind.Absolute));
        if (info is null) return System.Drawing.SystemIcons.Application;
        using var s = info.Stream;
        return new System.Drawing.Icon(s);
    }

    private void RebuildLayouts()
    {
        _layoutsItem.DropDownItems.Clear();
        foreach (var l in _app.Layouts)
        {
            var id = l.Id;
            var item = new WinForms.ToolStripMenuItem(l.Name, null, (_, _) => _app.SetActiveLayout(id))
            {
                Checked = string.Equals(id, _app.ActiveLayout.Id, StringComparison.OrdinalIgnoreCase),
            };
            _layoutsItem.DropDownItems.Add(item);
        }
    }

    private void Refresh()
    {
        bool on = _app.Mode.IsNumPad;
        _icon.Icon = on ? _iconOn : _iconOff;
        _toggleItem.Text = on ? $"Выключить NumPad ({_app.CurrentHotkeyText})" : $"Включить NumPad ({_app.CurrentHotkeyText})";
        string text = on ? "NumPad: включён" : "NumPad: выключен";
        if (_app.InputError is not null) text = "NumPad: тачпад недоступен";
        text += $" — {_app.ActiveLayout.Name}";
        _icon.Text = text.Length > 63 ? text[..63] : text; // ограничение NotifyIcon.Text
    }

    private void OnModeChanged(InputMode mode, string reason)
    {
        if (!_app.Settings.ShowNotifications) return;
        if (reason == "no device")
        {
            _icon.ShowBalloonTip(2000, "NumPad from TouchPad", _app.InputError ?? "Тачпад недоступен.", WinForms.ToolTipIcon.Warning);
            return;
        }
        if (reason is "application exit") return;
        string body = mode == InputMode.NumPad
            ? $"Тачпад работает как цифровой блок ({_app.ActiveLayout.Name}). {_app.CurrentHotkeyText} — выключить."
            : reason.StartsWith("session") || reason == "suspend" ? "Режим NumPad выключен (блокировка/сон)." : "Тачпад снова работает как обычно.";
        _icon.ShowBalloonTip(1500, mode == InputMode.NumPad ? "NumPad включён" : "NumPad выключен", body, WinForms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _app.StateChanged -= Refresh;
        _app.ModeChanged -= OnModeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
    }
}
