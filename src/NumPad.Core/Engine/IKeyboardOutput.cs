using NumPad.Core.Keys;

namespace NumPad.Core.Engine;

/// <summary>
/// Генерация системных клавиатурных событий. Реализации: SendInput (Windows), CGEventPost (macOS),
/// тестовый регистратор. Каждый метод возвращает false, если событие не удалось отправить.
/// </summary>
public interface IKeyboardOutput
{
    bool KeyDown(KeyDefinition key);
    bool KeyUp(KeyDefinition key);
    /// <summary>Ввод текста (нажатие+отпускание каждого символа как Unicode).</summary>
    bool TypeText(string text);
    /// <summary>Текущее состояние Num Lock (для режима «как настоящий цифровой блок»).</summary>
    bool IsNumLockOn { get; }
}
