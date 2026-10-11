using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NumPad.App.Services;

namespace NumPad.App.Views;

public interface IPage
{
    /// <summary>Страница показана.</summary>
    void OnShown();
    /// <summary>Страница скрыта. false — уход со страницы отменён (несохранённые изменения).</summary>
    bool OnLeaving();
}

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private string _current = "";
    private bool _revertingNav;

    public MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        _app.StateChanged += UpdateStatus;
        Closed += (_, _) =>
        {
            _app.StateChanged -= UpdateStatus;
            foreach (var p in _pages.Values.OfType<IDisposable>()) p.Dispose();
        };
        IsVisibleChanged += (_, _) =>
        {
            // Скрытое в трей окно не должно получать живые кадры (экономия CPU).
            if (_pages.TryGetValue(_current, out var page) && page is IPage ip)
            {
                if (IsVisible) ip.OnShown(); else ip.OnLeaving();
            }
        };
        Navigate("home");
        UpdateStatus();
    }

    public void Navigate(string tag)
    {
        if (tag == _current) return;
        if (_pages.TryGetValue(_current, out var old) && old is IPage oldPage && !oldPage.OnLeaving())
        {
            // Пользователь отменил уход — вернуть выделение навигации.
            _revertingNav = true;
            foreach (var rb in Nav.Children.OfType<RadioButton>())
                rb.IsChecked = (string)rb.Tag == _current;
            _revertingNav = false;
            return;
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
        foreach (var rb in Nav.Children.OfType<RadioButton>())
            if ((string)rb.Tag == tag && rb.IsChecked != true) { _revertingNav = true; rb.IsChecked = true; _revertingNav = false; }
        if (page is IPage p) p.OnShown();
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (_revertingNav || !IsInitialized || PageHost is null) return;
        if (sender is RadioButton { Tag: string tag }) Navigate(tag);
    }

    private void UpdateStatus()
    {
        bool on = _app.Mode.IsNumPad;
        StatusDot.Fill = on ? (Brush)FindResource("OnBrush") : _app.InputError is not null ? (Brush)FindResource("ErrorBrush") : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        StatusText.Text = on ? "NumPad включён" : _app.InputError is not null ? "Тачпад недоступен" : "NumPad выключен";
        HotkeyHint.Text = _app.HotkeyRegistered ? $"{_app.CurrentHotkeyText} — переключить" : "Горячая клавиша не назначена";
    }
}
