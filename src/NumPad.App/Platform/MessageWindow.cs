using System.Windows.Interop;

namespace NumPad.App.Platform;

/// <summary>
/// Скрытое окно только для сообщений (HWND_MESSAGE): получает WM_INPUT, WM_INPUT_DEVICE_CHANGE и WM_HOTKEY
/// независимо от того, открыто ли главное окно (приложение может жить только в трее).
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly HwndSource _source;

    /// <summary>(msg, wParam, lParam) → true, если сообщение обработано и DefWindowProc вызывать не нужно.</summary>
    public Func<int, IntPtr, IntPtr, bool>? Handler { get; set; }

    public MessageWindow()
    {
        var p = new HwndSourceParameters("NumPadFromTouchPad.MessageWindow")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_INPUT обязательно передаётся в DefWindowProc (очистка буфера Raw Input), поэтому handled=false.
        bool consumed = Handler?.Invoke(msg, wParam, lParam) ?? false;
        if (consumed && msg != NativeMethods.WM_INPUT) handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
