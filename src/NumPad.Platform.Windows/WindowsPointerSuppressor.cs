using System.Runtime.InteropServices;
using NumPad.Core.Config;
using static NumPad.Platform.Windows.NativeMethods;

namespace NumPad.Platform.Windows;

/// <summary>
/// Подавление указателя в режиме NumPad через низкоуровневый хук мыши (WH_MOUSE_LL) в потоке ввода.
/// Хук не различает тачпад и внешнюю мышь, поэтому в режиме WhileTouching блокирует события только пока
/// палец на зоне клавиш и «хвост» после отрыва (запаздывающий тап). Синтетические события (LLMHF_INJECTED)
/// пропускаются. Обработчик только читает поля — никаких блокировок и выделений памяти.
/// </summary>
internal sealed class WindowsPointerSuppressor : IPointerSuppressor
{
    private readonly InputThread _input;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private volatile bool _contactActive;
    private long _lastActivityMs = long.MinValue / 2;
    private volatile PointerSuppression _mode = PointerSuppression.None;
    private volatile int _tailMs;
    private long _blocked;

    public WindowsPointerSuppressor(InputThread input)
    {
        _input = input;
        _proc = HookProc;
    }

    public string Mechanism => "Низкоуровневый хук мыши (WH_MOUSE_LL)";
    public bool IsActive => _hook != IntPtr.Zero;
    public long BlockedEvents => Interlocked.Read(ref _blocked);

    public bool Activate(SuppressionConfig config, out string? error)
    {
        error = null;
        _mode = config.Mode;
        _tailMs = config.TailMs;
        if (config.Mode == PointerSuppression.None) { Deactivate(); return true; }
        string? err = null;
        bool ok = _input.Invoke(() =>
        {
            if (_hook != IntPtr.Zero) return true;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) { err = $"SetWindowsHookEx(WH_MOUSE_LL): ошибка {Marshal.GetLastWin32Error()}"; return false; }
            return true;
        });
        error = err;
        return ok;
    }

    public void Deactivate()
    {
        if (_hook == IntPtr.Zero) return;
        try
        {
            _input.Invoke(() =>
            {
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }, 2000);
        }
        catch { /* поток ввода остановлен — хук снят вместе с ним */ }
    }

    public void ReportTouchState(bool suppressingContactActive, long lastActivityMs)
    {
        _contactActive = suppressingContactActive;
        Interlocked.Exchange(ref _lastActivityMs, lastActivityMs);
    }

    private bool ShouldBlock() => _mode switch
    {
        PointerSuppression.AllWhileNumPad => true,
        PointerSuppression.WhileTouching => _contactActive || MonotonicClock.NowMs - Interlocked.Read(ref _lastActivityMs) <= _tailMs,
        _ => false,
    };

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool injected = (info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;
                if (!injected && ShouldBlock())
                {
                    Interlocked.Increment(ref _blocked);
                    return new IntPtr(1);
                }
            }
            catch
            {
                // при ошибке — пропускаем событие: лучше лишнее движение курсора, чем «мёртвая» мышь
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Deactivate();
}
