using System.Runtime.CompilerServices;

namespace Ogneliss.NumTouch.Tests;

internal static class TestLanguage
{
    /// <summary>
    /// Тесты сравнивают английские сообщения. Язык — глобальное состояние, поэтому он фиксируется один раз
    /// при загрузке сборки и тестами не переключается (тесты идут параллельно).
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => L.Apply(UiLanguage.English);
}
