using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NumPad.App.Services;
using NumPad.App.Views;
using NumPad.Core.Diagnostics;

namespace NumPad.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private AppController? _controller;
    private Tray? _tray;
    private MainWindow? _window;
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
            _controller?.Log.Error("Аварийное завершение", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { _controller?.Log.Error("Необработанное исключение задачи", e.Exception); e.SetObserved(); };
        desktop.Exit += (_, _) => Cleanup();

        try
        {
            _controller = new AppController();
            _controller.Start();
        }
        catch (Exception ex)
        {
            _controller?.Log.Error("Ошибка запуска", ex);
            var w = new Window { Title = "NumPad from TouchPad", Width = 520, Height = 200, Content = new TextBlock { Text = "Не удалось запустить программу:\n" + ex.Message, Margin = new Thickness(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap } };
            w.Closed += (_, _) => desktop.Shutdown(1);
            w.Show();
            return;
        }

        _tray = new Tray(this, _controller);
        _tray.OpenRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;

        if (_selfTest)
        {
            Dispatcher.UIThread.Post(() => _ = RunSelfTestAsync(), DispatcherPriority.Background);
            return;
        }

        SingleInstance.Watch(() => Dispatcher.UIThread.Post(ShowMainWindow));
        bool minimized = args.Contains("--minimized") || _controller.Settings.StartMinimized;
        if (!minimized) ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_controller is null || _exiting) return;
        if (_window is null)
        {
            _window = new MainWindow(_controller);
            _window.Closing += (_, e) =>
            {
                if (_exiting) return;
                if (_controller.Settings.CloseToTray)
                {
                    e.Cancel = true; // закрытие окна прячет его в трей; выход — через меню трея или «Выход»
                    _window.Hide();
                }
            };
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (!_exiting && !_controller.Settings.CloseToTray) ExitApplication();
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

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
        string resultPath = Environment.GetEnvironmentVariable("NUMPAD_SELFTEST_RESULT") ?? Path.Combine(controller.Store.RootDir, "selftest-result.txt");
        try
        {
            ShowMainWindow();
            foreach (var tag in new[] { "layout", "calibration", "settings", "diagnostics", "home" })
            {
                await _window!.NavigateAsync(tag);
                await Task.Delay(300);
            }
            File.WriteAllText(resultPath, "OK " + controller.DiagnosticsSummary().Replace(Environment.NewLine, " | "));
            controller.Log.Info("Самопроверка пройдена.");
            ExitApplication();
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "FAIL " + ex);
            controller.Log.Error("Самопроверка не пройдена", ex);
            _exiting = true;
            _desktop?.Shutdown(2);
        }
    }
}
