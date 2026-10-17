using System.Runtime.InteropServices;
using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Keys;
using static Ogneliss.NumTouch.Platform.Windows.NativeMethods;

namespace Ogneliss.NumTouch.Platform.Windows;

/// <summary>
/// Keyboard Output Backend для Windows. Генерирует события через SendInput с явным VK, скан-кодом
/// и флагом extended (без KEYEVENTF_SCANCODE — так VK_NUMPAD7 не превращается в Home при выключенном Num Lock).
/// Помечает свои события <see cref="Signature"/> в dwExtraInfo, чтобы их можно было отличить.
/// </summary>
public sealed class SendInputKeyboard : IKeyboardOutput
{
    /// <summary>Подпись в dwExtraInfo («NPTP»).</summary>
    public static readonly IntPtr Signature = new(0x4E505450);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public int LastError { get; private set; }

    public bool KeyDown(KeyDefinition key) => Send(Make(key, up: false));
    public bool KeyUp(KeyDefinition key) => Send(Make(key, up: true));

    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text) // суррогатные пары передаются двумя UTF-16 единицами — так требует KEYEVENTF_UNICODE
        {
            inputs.Add(Unicode(ch, up: false));
            inputs.Add(Unicode(ch, up: true));
        }
        return Send(inputs.ToArray());
    }

    public bool IsNumLockOn => (GetKeyState(0x90) & 0x0001) != 0;

    private static INPUT Make(KeyDefinition key, bool up)
    {
        uint flags = 0;
        if (key.WinExtended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (up) flags |= KEYEVENTF_KEYUP;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = key.WinVk, wScan = key.WinScan, dwFlags = flags, dwExtraInfo = Signature } },
        };
    }

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = Signature } },
    };

    private bool Send(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent == inputs.Length) return true;
        // 0 — ввод заблокирован (UIPI: окно с более высокими правами; защищённый рабочий стол).
        LastError = Marshal.GetLastWin32Error();
        return false;
    }
}
