using Avalonia.Markup.Xaml;

namespace Ogneliss.NumTouch.App.Localization;

/// <summary>
/// Перевод в XAML: <c>Text="{l:T 'Save layout'}"</c>. Значение берётся при загрузке окна; при смене языка
/// окно пересоздаётся (см. App.OnLanguageChanged), поэтому подписка на изменения не нужна.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
