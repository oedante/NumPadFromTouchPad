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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        PlatformTheme.Apply(this); // кисти и размеры текущей ОС для светлой и тёмной темы
    }

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
        // Завершение по запросу системы: ⌘Q и «Завершить» в macOS (меню программы и Dock), выход из сеанса в Windows.
        // Отмечаем выход заранее, чтобы закрытие окна не спрятало его в трей и не отменило завершение.
        desktop.ShutdownRequested += (_, e) => { if (!e.Cancel) _exiting = true; };

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
        // Пользователь переключил светлую/тёмную тему системы — окно перестраивается с новыми цветами.
        ActualThemeVariantChanged += (_, _) => Dispatcher.UIThread.Post(() => RebuildUi(languageChanged: false));
        InstallMacMenu();
        // macOS: щелчок по значку в Dock открывает окно, даже если оно спрятано в строку меню или программа скрыта (⌘H).
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) Dispatcher.UIThread.Post(ShowMainWindow);
            };

        if (_selfTest)
        {
            Dispatcher.UIThread.Post(() => _ = RunSelfTestAsync(), DispatcherPriority.Background);
            return;
        }

        SingleInstance.Watch(() => Dispatcher.UIThread.Post(ShowMainWindow));
        bool minimized = args.Contains("--minimized") || _controller.Settings.StartMinimized;
        if (!minimized) ShowMainWindow();
    }

    public void ShowMainWindow() => ShowMainWindow(null);

    private void InstallMacMenu() =>
        MacMenu.Install(this, ShowMainWindow, page => ShowMainWindow(page), () => _ = ShowAboutAsync(), ExitApplication);

    /// <summary>Показать окно; <paramref name="page"/> — открыть эту страницу (null — оставить текущую).</summary>
    private void ShowMainWindow(string? page)
    {
        if (_controller is null || _exiting) return;
        bool created = false;
        if (_window is null)
        {
            created = true;
            var win = new MainWindow(_controller, page ?? "home");
            _window = win;
            win.Closing += (_, e) =>
            {
                if (_exiting || ReferenceEquals(win, _retiring)) return;
                if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                {
                    _exiting = true; // завершение программы: окно закрывается, а не прячется
                    return;
                }
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
        if (!created && page is not null) _ = _window.NavigateAsync(page);
    }

    /// <summary>«О программе»: версия, лицензия, ссылка на исходный код.</summary>
    private async Task ShowAboutAsync()
    {
        ShowMainWindow(null);
        string github = "GitHub";
        var r = await Dialogs.AskAsync(_window, L.F("About {0}", "Ogneliss NumTouch"),
            $"Ogneliss NumTouch {AboutInfo.Version}\n\n" + L.T("Free and open source software under the MIT license.") + "\n" + AboutInfo.RepositoryUrl,
            "OK", github);
        if (r == github) AboutInfo.OpenUrl(AboutInfo.RepositoryUrl);
    }

    /// <summary>Смена языка: меню трея обновляется, главное окно пересоздаётся на той же странице.</summary>
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() => RebuildUi(languageChanged: true));

    private void RebuildUi(bool languageChanged)
    {
        if (_controller is null || _exiting) return;
        if (languageChanged)
        {
            _controller.OnLanguageChanged();
            InstallMacMenu();
        }
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
        window.UpdateLayout();
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
                await _window.NavigateAsync("home");
                _notifier.Show(L.T("NumPad on"), "NUM20 · Ctrl+Alt+N", OsdKind.On);
                await Task.Delay(300);
                if (_notifier.OsdWindow is { } osd) await SaveScreenshotAsync(osd, Path.Combine(shots, $"{code}-osd.png"));
                _notifier.Hide();
            }
            // Тёмная тема: окно перестраивается с тёмными кистями (как при переключении темы в системе).
            if (shots is not null)
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                await Task.Delay(900);
                if (_window is null) throw new InvalidOperationException("Main window was not recreated for the dark theme.");
                foreach (var tag in new[] { "home", "layout", "settings" })
                {
                    await _window.NavigateAsync(tag);
                    if (tag == "layout" && _window.CurrentPageControl is LayoutEditorPage ed) ed.SelectKeyForPreview("n7");
                    await SaveScreenshotAsync(_window, Path.Combine(shots, $"dark-{L.Code}-{tag}.png"));
                }
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
                await Task.Delay(600);
            }
            // macOS: меню программы со своими пунктами «Завершить ⌘Q» и т. п.; щелчок по значку в Dock обрабатывается.
            if (PlatformTheme.Os == OsFamily.MacOS)
            {
                if (NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().Any(i => i.Gesture?.Key == Avalonia.Input.Key.Q) != true)
                    throw new InvalidOperationException("The macOS application menu has no Quit item.");
                if (TryGetFeature(typeof(IActivatableLifetime)) is null)
                    throw new InvalidOperationException("IActivatableLifetime is not available: the Dock icon would not reopen the window.");
            }

            // Завершение по запросу системы (⌘Q, «Завершить» в Dock, выход из сеанса), когда окно спрятано в трей:
            // закрытие окна в трей не должно отменять завершение.
            string summary = "OK " + controller.DiagnosticsSummary().Replace(Environment.NewLine, " | ");
            controller.Settings.CloseToTray = true; // только в памяти, без сохранения
            _window?.Hide();
            controller.Log.Info("Self-test passed; requesting shutdown as the OS would.");
            if (_desktop is null || !_desktop.TryShutdown())
                throw new InvalidOperationException("A shutdown request from the OS was cancelled (window hidden to the tray).");
            File.WriteAllText(resultPath, summary);
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
