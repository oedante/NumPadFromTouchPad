using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ogneliss.NumTouch.App.Services;
using Ogneliss.NumTouch.App.Views;
using Ogneliss.NumTouch.Core.Diagnostics;

namespace Ogneliss.NumTouch.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private AppController? _controller;
    private Tray? _tray;
    private ModeNotifier? _notifier;
    private MainWindow? _window;
    /// <summary>Окно, закрываемое для пересоздания (смена языка): его закрытие не завершает программу.</summary>
    private MainWindow? _retiring;
    private bool _exiting;
    private bool _selfTest;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;
        var args = desktop.Args ?? Array.Empty<string>();
        _selfTest = args.Contains("--selftest");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Процесс завершается: успеть отпустить клавиши и вернуть настройки тачпада.
            _controller?.EmergencyRelease();
            _controller?.Log.Error("Fatal crash", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { _controller?.Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
        desktop.Exit += (_, _) => Cleanup();

        try
        {
            _controller = new AppController();
            _controller.Start();
        }
        catch (Exception ex)
        {
            _controller?.Log.Error("Startup error", ex);
            var w = new Window { Title = "Ogneliss NumTouch", Width = 520, Height = 200, Content = new TextBlock { Text = L.T("The program could not start:") + "\n" + ex.Message, Margin = new Thickness(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap } };
            w.Closed += (_, _) => desktop.Shutdown(1);
            w.Show();
            return;
        }

        _tray = new Tray(this, _controller);
        _tray.OpenRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;
        _notifier = new ModeNotifier(_controller);
        L.Changed += OnLanguageChanged;

        if (_selfTest)
        {
            Dispatcher.UIThread.Post(() => _ = RunSelfTestAsync(), DispatcherPriority.Background);
            return;
        }

        SingleInstance.Watch(() => Dispatcher.UIThread.Post(ShowMainWindow));
        bool minimized = args.Contains("--minimized") || _controller.Settings.StartMinimized;
        if (!minimized) ShowMainWindow();
    }

    public void ShowMainWindow() => ShowMainWindow("home");

    private void ShowMainWindow(string page)
    {
        if (_controller is null || _exiting) return;
        if (_window is null)
        {
            var win = new MainWindow(_controller, page);
            _window = win;
            win.Closing += (_, e) =>
            {
                if (_exiting || ReferenceEquals(win, _retiring)) return;
                if (_controller.Settings.CloseToTray)
                {
                    e.Cancel = true; // закрытие окна прячет его в трей; выход — через меню трея или «Выход»
                    win.Hide();
                }
            };
            win.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, win)) _window = null;
                if (ReferenceEquals(win, _retiring)) { _retiring = null; return; }
                if (!_exiting && !_controller.Settings.CloseToTray) ExitApplication();
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Смена языка: меню трея обновляется, главное окно пересоздаётся на той же странице.</summary>
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_controller is null || _exiting) return;
        _tray?.Refresh();
        if (_window is not { } old) return;
        string page = old.CurrentPage;
        bool visible = old.IsVisible;
        var pos = old.Position;
        _retiring = old;
        _window = null;
        old.Close();
        if (!visible) return;
        ShowMainWindow(page);
        if (_window is { } w) w.Position = pos;
    });

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.Close();
        _desktop?.Shutdown(0);
    }

    private void Cleanup()
    {
        _exiting = true;
        L.Changed -= OnLanguageChanged;
        _notifier?.Dispose();
        _notifier = null;
        _tray?.Dispose();
        _tray = null;
        _controller?.Dispose(); // отпускает клавиши, восстанавливает тачпад, снимает хук и захват
        _controller = null;
    }

    /// <summary>
    /// Самопроверка для CI (--selftest): главное окно и все страницы (разбор XAML, ресурсы, отрисовка), результат в
    /// selftest-result.txt, код выхода 0 / 2.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        var controller = _controller!;
        string resultPath = Environment.GetEnvironmentVariable("NUMTOUCH_SELFTEST_RESULT") ?? Path.Combine(controller.Store.RootDir, "selftest-result.txt");
        try
        {
            ShowMainWindow();
            foreach (var tag in new[] { "layout", "calibration", "settings", "diagnostics", "home" })
            {
                await _window!.NavigateAsync(tag);
                await Task.Delay(300);
            }
            // Уведомление о смене режима: окно создаётся и показывается на этой ОС.
            _notifier!.Show(L.T("NumPad on"), "self-test", OsdKind.On);
            await Task.Delay(300);
            if (!_notifier.IsShowing) throw new InvalidOperationException("Mode notification window is not visible.");
            _notifier.Hide();

            // Смена языка: окно пересоздаётся на текущей странице, обе локали загружаются без ошибок XAML.
            foreach (var lang in new[] { UiLanguage.Russian, UiLanguage.English })
            {
                L.Apply(lang);
                await Task.Delay(500);
                if (_window is null || !_window.IsVisible) throw new InvalidOperationException($"Main window was not recreated for {lang}.");
                foreach (var tag in new[] { "layout", "calibration", "settings", "diagnostics", "home" })
                {
                    await _window.NavigateAsync(tag);
                    await Task.Delay(150);
                }
            }
            File.WriteAllText(resultPath, "OK " + controller.DiagnosticsSummary().Replace(Environment.NewLine, " | "));
            controller.Log.Info("Self-test passed.");
            ExitApplication();
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "FAIL " + ex);
            controller.Log.Error("Self-test failed", ex);
            _exiting = true;
            _desktop?.Shutdown(2);
        }
    }
}
