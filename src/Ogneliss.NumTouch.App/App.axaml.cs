using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
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
        _controller.OnLanguageChanged();
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
    private static async Task SaveScreenshotAsync(Window window, string path)
    {
        await Task.Delay(350); // дождаться раскладки и отрисовки
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width)), Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
    }

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
            // NUMTOUCH_SCREENSHOTS=<папка> — снимки всех страниц на обоих языках (CI выкладывает их для проверки вёрстки).
            string? shots = Environment.GetEnvironmentVariable("NUMTOUCH_SCREENSHOTS") is { Length: > 0 } sd ? sd : null;
            if (shots is not null) Directory.CreateDirectory(shots);
            foreach (var lang in new[] { UiLanguage.Russian, UiLanguage.English })
            {
                L.Apply(lang);
                await Task.Delay(500);
                if (_window is null || !_window.IsVisible) throw new InvalidOperationException($"Main window was not recreated for {lang}.");
                string code = L.Code;
                foreach (var tag in new[] { "layout", "calibration", "settings", "diagnostics", "home" })
                {
                    await _window.NavigateAsync(tag);
                    await Task.Delay(150);
                    if (shots is null) continue;
                    await SaveScreenshotAsync(_window, Path.Combine(shots, $"{code}-{tag}.png"));
                    if (tag == "layout" && _window.CurrentPageControl is LayoutEditorPage editor)
                    {
                        editor.SelectKeyForPreview("n7");
                        await SaveScreenshotAsync(_window, Path.Combine(shots, $"{code}-layout-selected.png"));
                        // Перетаскивание: клавиша «8» рядом с «7» — привязка к промежутку и краям, подсказка с положением.
                        editor.PreviewDrag("n8", Ogneliss.NumTouch.Core.Layouts.ResizeEdges.None, 9, 23, show: true);
                        await SaveScreenshotAsync(_window, Path.Combine(shots, $"{code}-layout-drag-move.png"));
                        editor.PreviewDrag("n8", Ogneliss.NumTouch.Core.Layouts.ResizeEdges.None, 0, 0, show: false);
                        editor.PreviewDrag("n5", Ogneliss.NumTouch.Core.Layouts.ResizeEdges.Right, 23, 0, show: true);
                        await SaveScreenshotAsync(_window, Path.Combine(shots, $"{code}-layout-drag-resize.png"));
                        editor.PreviewDrag("n5", Ogneliss.NumTouch.Core.Layouts.ResizeEdges.Right, 0, 0, show: false);
                    }
                }
                if (shots is null) continue;
                // Минимальный размер окна — проверка переносов и обрезки.
                var (w0, h0) = (_window.Width, _window.Height);
                _window.Width = _window.MinWidth; _window.Height = _window.MinHeight;
                await Task.Delay(800);
                foreach (var tag in new[] { "home", "layout", "settings" })
                {
                    await _window.NavigateAsync(tag);
                    await SaveScreenshotAsync(_window, Path.Combine(shots, $"{code}-min-{tag}.png"));
                }
                _window.Width = w0; _window.Height = h0;
                await _window.NavigateAsync("home");
                _notifier.Show(L.T("NumPad on"), "NUM20 · Ctrl+Alt+N", OsdKind.On);
                await Task.Delay(300);
                if (_notifier.OsdWindow is { } osd) await SaveScreenshotAsync(osd, Path.Combine(shots, $"{code}-osd.png"));
                _notifier.Hide();
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
