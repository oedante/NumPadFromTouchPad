using System.Runtime.InteropServices;
using static NumPad.App.Platform.NativeMethods;

namespace NumPad.App.Platform;

/// <summary>
/// Подавление указателя в режиме NumPad через низкоуровневый хук мыши (WH_MOUSE_LL).
/// Хук не различает тачпад и внешнюю мышь, поэтому блокирует события, только пока решение
/// <see cref="ShouldBlock"/> истинно (палец на зоне клавиш + «хвост» после отрыва — запаздывающий тап).
/// Синтетические события других программ (LLMHF_INJECTED) не блокируются.
/// Обработчик выполняет только чтение полей: Windows снимает хук, если он отвечает дольше LowLevelHooksTimeout.
/// </summary>
public sealed class PointerSuppressor : IDisposable
{
    private readonly LowLevelMouseProc _proc; // держим ссылку, чтобы делегат не собрал GC
    private IntPtr _hook = IntPtr.Zero;

    public PointerSuppressor()
    {
        _proc = HookProc;
    }

    /// <summary>Решение «блокировать сейчас»; вызывается из потока хука (UI-поток).</summary>
    public Func<bool> ShouldBlock { get; set; } = () => false;
    public bool IsInstalled => _hook != IntPtr.Zero;
    public long BlockedEvents { get; private set; }
    public long PassedEvents { get; private set; }

    public bool Install(out string error)
    {
        error = "";
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            error = $"SetWindowsHookEx(WH_MOUSE_LL): ошибка {Marshal.GetLastWin32Error()}";
            return false;
        }
        return true;
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

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
                    BlockedEvents++;
                    return new IntPtr(1); // не передавать событие дальше
                }
                PassedEvents++;
            }
            catch
            {
                // при любой ошибке — пропускаем событие: лучше лишнее движение курсора, чем «мёртвая» мышь
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
