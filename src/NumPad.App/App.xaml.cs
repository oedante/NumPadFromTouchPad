using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NumPad.App.Services;
using NumPad.App.Views;
using NumPad.Core.Diagnostics;

namespace NumPad.App;

public partial class App : Application
{
    private const string MutexName = "Local\\NumPadFromTouchPad.SingleInstance";
    private const string ShowEventName = "Local\\NumPadFromTouchPad.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private AppController? _controller;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private bool _selfTest;

    public static AppController Controller => ((App)Current)._controller!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _selfTest = e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

        // Один экземпляр: второй запуск просит первый показать окно и завершается (иначе два процесса
        // боролись бы за одну горячую клавишу и оба генерировали бы нажатия).
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* первый экземпляр ещё стартует */ }
            Shutdown(0);
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.InvokeAsync(ShowMainWindow), null, -1, false);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { _controller?.Log.Error("Необработанное исключение задачи", args.Exception); args.SetObserved(); };

        try
        {
            _controller = new AppController(Dispatcher);
            _controller.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить NumPad from TouchPad:\n" + ex.Message, "NumPad from TouchPad",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _controller?.Log.Error("Ошибка запуска", ex);
            _controller?.Dispose();
            Shutdown(1);
            return;
        }

        _tray = new TrayIcon(_controller);
        _tray.OpenRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;

        if (_selfTest)
        {
            Dispatcher.InvokeAsync(RunSelfTest, DispatcherPriority.ApplicationIdle);
            return;
        }

        bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) || _controller.Settings.StartMinimized;
        if (!minimized) ShowMainWindow();
    }

    /// <summary>
    /// Самопроверка для CI (--selftest): создаёт главное окно, открывает все страницы (разбор XAML, ресурсы,
    /// отрисовка), пишет результат в selftest-result.txt и завершается с кодом 0 / 2.
    /// </summary>
    private void RunSelfTest()
    {
        string resultPath = Path.Combine(_controller!.Store.RootDir, "selftest-result.txt");
        try
        {
            ShowMainWindow();
            foreach (var tag in new[] { "layout", "calibration", "settings", "diagnostics", "home" })
            {
                _window!.Navigate(tag);
                _window.UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            }
            File.WriteAllText(resultPath, "OK " + _controller.DiagnosticsSummary().Replace(Environment.NewLine, " | "));
            _controller.Log.Info("Самопроверка пройдена.");
            ExitApplication();
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "FAIL " + ex);
            _controller.Log.Error("Самопроверка не пройдена", ex);
            _exiting = true;
            Shutdown(2);
        }
    }

    public void ShowMainWindow()
    {
        if (_controller is null || _exiting) return;
        if (_window is null)
        {
            _window = new MainWindow(_controller);
            _window.Closing += (_, args) =>
            {
                if (!_exiting && _controller.Settings.CloseToTray)
                {
                    args.Cancel = true; // закрытие окна прячет его в трей; выход — через меню трея или кнопку «Выход»
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
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _tray?.Dispose();
        _controller?.Dispose(); // отпускает все клавиши, восстанавливает тачпад, снимает хук и хоткей
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* не владеем — второй экземпляр */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _controller?.Log.Error("Необработанное исключение в UI", e.Exception);
        _controller?.SafeDisable("unhandled exception");
        e.Handled = true;
        if (_selfTest)
        {
            try { File.WriteAllText(Path.Combine(_controller!.Store.RootDir, "selftest-result.txt"), "FAIL " + e.Exception); } catch { }
            _exiting = true;
            Shutdown(3);
            return;
        } // приложение продолжает работать; режим NumPad выключен ради безопасности
        MessageBox.Show("Произошла ошибка, режим NumPad выключен.\n" + e.Exception.Message + "\n\nПодробности — в журнале диагностики.",
            "NumPad from TouchPad", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Процесс завершается: успеть отпустить клавиши и вернуть настройки тачпада.
        _controller?.EmergencyRelease();
        _controller?.Log.Error("Аварийное завершение", e.ExceptionObject as Exception);
    }
}
