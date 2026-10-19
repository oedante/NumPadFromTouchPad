using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Ogneliss.NumTouch.App.Views;

/// <summary>
/// Простые модальные диалоги (в Avalonia нет MessageBox). Возвращают текст нажатой кнопки или null.
/// Первая кнопка — основная (Enter, акцентный цвет), «Отмена»/«Нет» — по Esc. Порядок на экране как в системе:
/// в Windows основная кнопка слева, в macOS и GNOME — справа.
/// </summary>
public static class Dialogs
{
    public static async Task<string?> AskAsync(Visual? owner, string title, string message, params string[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { "OK" };
        string? result = null;
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        window.Classes.Add(Services.PlatformTheme.OsClass);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var created = new List<Button>();
        for (int i = 0; i < buttons.Length; i++)
        {
            string text = buttons[i];
            var b = new Button { Content = text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) { b.Classes.Add("accent"); b.IsDefault = true; }
            // Esc — «Отмена»/«Нет», а если таких нет — основная кнопка (например, «OK» в окне «О программе»).
            bool isCancel = text == L.T("Cancel") || text == L.T("No");
            if (isCancel || (i == 0 && !buttons.Any(x => x == L.T("Cancel") || x == L.T("No")))) b.IsCancel = true;
            b.Click += (_, _) => { result = text; window.Close(); };
            created.Add(b);
        }
        if (Services.PlatformTheme.AffirmativeLast) created.Reverse();
        foreach (var b in created) panel.Children.Add(b);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                panel,
            },
        };

        if (owner?.GetVisualRoot() is Window ow && ow.IsVisible)
            await window.ShowDialog(ow);
        else
        {
            var tcs = new TaskCompletionSource();
            window.Closed += (_, _) => tcs.TrySetResult();
            window.Show();
            await tcs.Task;
        }
        return result;
    }

    public static Task InfoAsync(Visual? owner, string title, string message) => AskAsync(owner, title, message, "OK");

    public static async Task<bool> ConfirmAsync(Visual? owner, string title, string message, string? yes = null, string? no = null)
    {
        yes ??= L.T("Yes");
        no ??= L.T("No");
        return await AskAsync(owner, title, message, yes, no) == yes;
    }
}
