using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ogneliss.NumTouch.App.Views;

/// <summary>Кисти из ресурсов приложения для текущей темы (светлой/тёмной) — доступны и до присоединения элемента к окну.</summary>
public static class Ui
{
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app is null) return Brushes.Gray;
        return app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
    }
}
