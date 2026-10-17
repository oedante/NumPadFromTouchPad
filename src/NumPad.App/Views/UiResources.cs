using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NumPad.App.Views;

/// <summary>Кисти из ресурсов приложения (App.axaml) — доступны и до присоединения элемента к окну.</summary>
public static class Ui
{
    public static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var v) == true && v is IBrush b ? b : Brushes.Black;
}
