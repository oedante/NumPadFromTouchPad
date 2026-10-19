using System.Globalization;

namespace Ogneliss.NumTouch.Core.Localization;

public enum UiLanguage { Auto, English, Russian }

/// <summary>
/// Локализация интерфейса в стиле gettext: ключ — английский текст, перевод — в <see cref="RussianStrings"/>.
/// Нет перевода — показывается английский текст (никогда пустая строка или ключ-идентификатор).
/// Полноту перевода проверяет тест LocalizationTests: он находит в исходниках все вызовы L.T, L.F, L.N с литералом
/// и расширение разметки l:T в XAML.
/// </summary>
public static class L
{
    private static volatile IReadOnlyDictionary<string, string>? _table;

    static L() => Set(UiLanguage.Auto, raise: false);

    /// <summary>Действующий язык: "ru" или "en".</summary>
    public static string Code { get; private set; } = "en";

    public static UiLanguage Selected { get; private set; }

    /// <summary>Язык изменился (UI перестраивает окна).</summary>
    public static event Action? Changed;

    public static void Apply(UiLanguage language) => Set(language, raise: true);

    private static void Set(UiLanguage language, bool raise)
    {
        string code = language switch
        {
            UiLanguage.Russian => "ru",
            UiLanguage.English => "en",
            _ => Resolve(CultureInfo.CurrentUICulture),
        };
        bool changed = code != Code;
        Selected = language;
        Code = code;
        _table = code == "ru" ? RussianStrings.Table : null;
        if (raise && changed) Changed?.Invoke();
    }

    /// <summary>Автовыбор: русский для русской локали системы, иначе английский.</summary>
    public static string Resolve(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    /// <summary>Перевод строки.</summary>
    public static string T(string english) =>
        _table is { } t && t.TryGetValue(english, out var s) ? s : english;

    /// <summary>Перевод строки с подстановкой {0}, {1}…</summary>
    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    private static readonly Lazy<Dictionary<string, string>> Reverse = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (en, ru) in RussianStrings.Table) map.TryAdd(ru, en);
        return map;
    });

    /// <summary>
    /// Перевести заново на текущий язык уже готовый текст сообщения — на любом из языков программы. Нужно для
    /// сообщений, которые платформа сформировала при запуске (нет доступа к устройству и т. п.): после смены языка
    /// они показываются на новом языке без перезапуска. Текст с подстановками и незнакомый текст — как есть.
    /// </summary>
    public static string Retranslate(string text) =>
        T(Reverse.Value.TryGetValue(text, out var english) ? english : text);

    /// <summary>
    /// Пометка «это ключ перевода» без перевода (для данных, переводимых позже через <see cref="T"/>,
    /// например названий клавиш в справочнике).
    /// </summary>
    public static string N(string english) => english;
}
