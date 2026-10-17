using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static Ogneliss.NumTouch.Platform.Windows.NativeMethods;

namespace Ogneliss.NumTouch.Platform.Windows;

/// <summary>
/// Отдельный поток ввода со скрытым окном верхнего уровня и собственным циклом сообщений.
/// Здесь живут Raw Input (WM_INPUT), горячая клавиша (WM_HOTKEY), низкоуровневый хук мыши и уведомления
/// о блокировке/сне. Хук не зависит от загрузки UI-потока: Windows снимает LL-хук, если поток, в котором
/// он установлен, не отвечает дольше LowLevelHooksTimeout.
/// Окно не message-only: такие окна не получают широковещательный WM_POWERBROADCAST.
/// </summary>
internal sealed class InputThread : IDisposable
{
    private const uint WM_INVOKE = WM_APP + 1;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProcDelegate _proc;
    private readonly string _className = "OgnelissNumTouch.Input." + Environment.ProcessId;
    private Thread? _thread;
    private IntPtr _hwnd;
    private int _threadId;
    private string? _startError;

    public InputThread()
    {
        _proc = WndProc; // держим ссылку, чтобы GC не собрал делегат
    }

    public IntPtr Handle => _hwnd;
    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>(msg, wParam, lParam) → true, если обработано. WM_INPUT всегда уходит и в DefWindowProc.</summary>
    public Func<int, IntPtr, IntPtr, bool>? Handler { get; set; }
    public Action<Exception>? OnError { get; set; }

    public bool Start(out string? error)
    {
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "NumPad input" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) { error = L.T("The input thread did not start."); return false; }
        error = _startError;
        return _startError is null;
    }

    private void ThreadProc()
    {
        _threadId = Environment.CurrentManagedThreadId;
        IntPtr hInstance = GetModuleHandle(null);
        try
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInstance,
                lpszClassName = _className,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                _startError = L.F("{0}: error {1}", "RegisterClassEx", Marshal.GetLastWin32Error());
                return;
            }
            _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, _className, "Ogneliss NumTouch input", WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _startError = L.F("{0}: error {1}", "CreateWindowEx", Marshal.GetLastWin32Error());
                return;
            }
            WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);
        }
        finally
        {
            _ready.Set();
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        WTSUnRegisterSessionNotification(_hwnd);
        DestroyWindow(_hwnd);
        UnregisterClass(_className, hInstance);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_INVOKE)
            {
                while (_queue.TryDequeue(out var a)) a();
                return IntPtr.Zero;
            }
            bool handled = Handler?.Invoke((int)msg, wParam, lParam) ?? false;
            if (handled && msg != WM_INPUT) return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            // Исключение не должно уйти в нативный код (это обрушит процесс).
            try { OnError?.Invoke(ex); } catch { /* ignore */ }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Выполнить действие в потоке ввода и дождаться результата.</summary>
    public T Invoke<T>(Func<T> func, int timeoutMs = 5000)
    {
        if (IsCurrentThread || _hwnd == IntPtr.Zero) return func();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
        if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException("The input thread did not respond.");
        return tcs.Task.Result;
    }

    public void Invoke(Action action, int timeoutMs = 5000) => Invoke(() => { action(); return true; }, timeoutMs);

    /// <summary>Выполнить в потоке ввода без ожидания.</summary>
    public void Post(Action action)
    {
        if (_hwnd == IntPtr.Zero) return;
        _queue.Enqueue(action);
        PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_thread is null) return;
        if (_hwnd != IntPtr.Zero) Post(() => PostQuitMessage(0));
        _thread.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _ready.Dispose();
    }
}
