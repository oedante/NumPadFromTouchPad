using NumPad.Core.Keys;

namespace NumPad.Core.Layouts;

public enum IssueSeverity { Warning, Error }

public sealed record ValidationIssue(IssueSeverity Severity, string? KeyId, string Message)
{
    public override string ToString() => KeyId is null ? $"{Severity}: {Message}" : $"{Severity} [{KeyId}]: {Message}";
}

/// <summary>Проверка раскладки перед применением/сохранением. Ошибки блокируют применение, предупреждения — нет.</summary>
public static class LayoutValidator
{
    public const double MinKeySize = 0.02;     // 2 % стороны области
    private const double Tolerance = 1e-6;
    private static readonly HashSet<string> AllowedModifiers = new(StringComparer.OrdinalIgnoreCase)
        { "Ctrl", "Shift", "Alt", "Win", "LeftCtrl", "RightCtrl", "LeftShift", "RightShift", "LeftAlt", "RightAlt", "LeftWin", "RightWin" };

    public static IReadOnlyList<ValidationIssue> Validate(Layout? layout)
    {
        var issues = new List<ValidationIssue>();
        if (layout is null) { issues.Add(new(IssueSeverity.Error, null, "Раскладка отсутствует.")); return issues; }

        if (string.IsNullOrWhiteSpace(layout.Id)) issues.Add(new(IssueSeverity.Error, null, "Пустой идентификатор раскладки."));
        if (string.IsNullOrWhiteSpace(layout.Name)) issues.Add(new(IssueSeverity.Error, null, "Пустое название раскладки."));
        if (layout.SchemaVersion > Layout.CurrentSchemaVersion)
            issues.Add(new(IssueSeverity.Error, null, $"Раскладка создана более новой версией программы (схема {layout.SchemaVersion})."));
        if (!(layout.AspectRatio > 0.1 && layout.AspectRatio < 10))
            issues.Add(new(IssueSeverity.Error, null, "Недопустимое соотношение сторон (ожидается 0.1…10)."));
        if (layout.Keys is null || layout.Keys.Count == 0)
        {
            issues.Add(new(IssueSeverity.Error, null, "В раскладке нет клавиш."));
            return issues;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in layout.Keys)
        {
            string? id = string.IsNullOrWhiteSpace(k.Id) ? null : k.Id;
            if (id is null) issues.Add(new(IssueSeverity.Error, null, $"Клавиша «{k.Label}» без идентификатора."));
            else if (!seen.Add(id)) issues.Add(new(IssueSeverity.Error, id, "Повторяющийся идентификатор клавиши."));

            if (string.IsNullOrWhiteSpace(k.Label)) issues.Add(new(IssueSeverity.Error, id, "Пустая подпись клавиши."));

            if (double.IsNaN(k.X) || double.IsNaN(k.Y) || double.IsNaN(k.Width) || double.IsNaN(k.Height)
                || double.IsInfinity(k.X) || double.IsInfinity(k.Y) || double.IsInfinity(k.Width) || double.IsInfinity(k.Height))
                issues.Add(new(IssueSeverity.Error, id, "Некорректные координаты."));
            else
            {
                if (k.Width < MinKeySize || k.Height < MinKeySize)
                    issues.Add(new(IssueSeverity.Error, id, "Слишком маленький размер зоны."));
                if (k.X < -Tolerance || k.Y < -Tolerance || k.X + k.Width > 1 + Tolerance || k.Y + k.Height > 1 + Tolerance)
                    issues.Add(new(IssueSeverity.Error, id, "Зона выходит за границы области."));
            }

            ValidateAction(k, id, issues);
        }

        // Перекрытия — предупреждение: hit-test разрешает их по ближайшему центру, но обычно это ошибка разметки.
        var enabled = layout.Keys.Where(k => k.Enabled).ToList();
        for (int i = 0; i < enabled.Count; i++)
            for (int j = i + 1; j < enabled.Count; j++)
            {
                var a = enabled[i].Bounds;
                var b = enabled[j].Bounds;
                if (a.IntersectsWith(b))
                {
                    double ow = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    double oh = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                    if (ow > 1e-4 && oh > 1e-4)
                        issues.Add(new(IssueSeverity.Warning, enabled[i].Id, $"Перекрывается с «{enabled[j].Id}»."));
                }
            }

        if (!layout.Keys.Any(k => k.Enabled))
            issues.Add(new(IssueSeverity.Warning, null, "Все клавиши отключены."));

        return issues;
    }

    private static void ValidateAction(KeyZone k, string? id, List<ValidationIssue> issues)
    {
        var a = k.Action;
        if (a is null) { issues.Add(new(IssueSeverity.Error, id, "Не задано назначение.")); return; }
        switch (a.Kind)
        {
            case KeyActionKind.Key:
                if (!KeyCatalog.IsKnown(a.Key)) issues.Add(new(IssueSeverity.Error, id, $"Недопустимое назначение: «{a.Key}»."));
                break;
            case KeyActionKind.Combo:
                if (!KeyCatalog.IsKnown(a.Key)) issues.Add(new(IssueSeverity.Error, id, $"Недопустимая клавиша сочетания: «{a.Key}»."));
                if (a.Modifiers is null || a.Modifiers.Count == 0)
                    issues.Add(new(IssueSeverity.Error, id, "Сочетание без модификаторов."));
                else
                    foreach (var m in a.Modifiers.Where(m => !AllowedModifiers.Contains(m)))
                        issues.Add(new(IssueSeverity.Error, id, $"Недопустимый модификатор «{m}»."));
                if (a.Modifiers is not null && a.Key is not null &&
                    a.Modifiers.Any(m => m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) &&
                    a.Modifiers.Any(m => m.Equals("Alt", StringComparison.OrdinalIgnoreCase)) &&
                    a.Key.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(IssueSeverity.Error, id, "Ctrl+Alt+Del не может быть сгенерирован программно (ограничение Windows)."));
                break;
            case KeyActionKind.Text:
                if (string.IsNullOrEmpty(a.Text)) issues.Add(new(IssueSeverity.Error, id, "Пустой текст."));
                else if (a.Text.Length > 64) issues.Add(new(IssueSeverity.Error, id, "Текст длиннее 64 символов."));
                break;
            case KeyActionKind.None:
                break;
            default:
                issues.Add(new(IssueSeverity.Error, id, "Неизвестный тип назначения."));
                break;
        }
    }

    public static bool HasErrors(IEnumerable<ValidationIssue> issues) => issues.Any(i => i.Severity == IssueSeverity.Error);
}
