using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Ogneliss.NumTouch.App.Services;

namespace Ogneliss.NumTouch.App.Views;

/// <summary>Страница главного окна.</summary>
public interface IPage
{
    /// <summary>Страница показана (подписаться на живые кадры, обновить данные).</summary>
    void OnShown();
    /// <summary>Можно ли уйти со страницы (несохранённые изменения → вопрос пользователю).</summary>
    Task<bool> CanLeaveAsync();
    /// <summary>Страница скрыта (отписаться от кадров).</summary>
    void OnHidden();
}

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly Dictionary<string, Control> _pages = new();
    private string _current = "";

    /// <summary>Открытая страница (для восстановления после пересоздания окна при смене языка).</summary>
    public string CurrentPage => _current;

    /// <summary>Элемент открытой страницы.</summary>
    public Control? CurrentPageControl => _pages.TryGetValue(_current, out var p) ? p : null;

    public MainWindow() : this(null!) { } // для дизайнера

    public MainWindow(AppController app, string startPage = "home")
    {
        _app = app;
        InitializeComponent();
        foreach (var b in Nav.Children.OfType<Button>())
            b.Click += async (_, _) => await NavigateAsync((string)b.Tag!);
        ExitButton.Click += (_, _) => (Avalonia.Application.Current as App)?.ExitApplication();
        if (app is null) return;
        _app.StateChanged += UpdateStatus;
        Closed += (_, _) =>
        {
            _app.StateChanged -= UpdateStatus;
            foreach (var p in _pages.Values) { (p as IPage)?.OnHidden(); (p as IDisposable)?.Dispose(); }
        };
        PropertyChanged += (_, e) =>
        {
            // Скрытое в трей окно не получает живые кадры (экономия CPU).
            if (e.Property != IsVisibleProperty || !_pages.TryGetValue(_current, out var page) || page is not IPage ip) return;
            if (IsVisible) ip.OnShown(); else ip.OnHidden();
        };
        _ = NavigateAsync(startPage);
        UpdateStatus();
    }

    public async Task NavigateAsync(string tag)
    {
        if (tag == _current) return;
        if (_pages.TryGetValue(_current, out var old) && old is IPage oldPage)
        {
            if (!await oldPage.CanLeaveAsync()) return;
            oldPage.OnHidden();
        }

        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "home" => new HomePage(_app, this),
                "layout" => new LayoutEditorPage(_app),
                "calibration" => new CalibrationPage(_app),
                "settings" => new SettingsPage(_app),
                "diagnostics" => new DiagnosticsPage(_app),
                _ => throw new ArgumentOutOfRangeException(nameof(tag)),
            };
            _pages[tag] = page;
        }
        _current = tag;
        PageHost.Content = page;
        foreach (var b in Nav.Children.OfType<Button>())
            b.Classes.Set("selected", (string)b.Tag! == tag);
        (page as IPage)?.OnShown();
    }

    private void UpdateStatus()
    {
        bool on = _app.Mode.IsNumPad;
        StatusDot.Fill = on ? Ui.Brush("OnBrush") : _app.InputError is not null ? Ui.Brush("ErrorBrush") : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        StatusText.Text = on ? L.T("NumPad on") : _app.InputError is not null ? L.T("Touchpad unavailable") : L.T("NumPad off");
        HotkeyHint.Text = _app.HotkeyRegistered ? _app.CurrentHotkeyText : L.T("No hotkey assigned");
        ToolTip.SetTip(HotkeyChip, _app.HotkeyRegistered ? L.F("{0} — toggle", _app.CurrentHotkeyText) : null);
    }
}
