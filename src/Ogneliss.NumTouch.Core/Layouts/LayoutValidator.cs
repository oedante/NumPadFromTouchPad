using Ogneliss.NumTouch.Core.Keys;

namespace Ogneliss.NumTouch.Core.Layouts;

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
        if (layout is null) { issues.Add(new(IssueSeverity.Error, null, L.T("Layout is missing."))); return issues; }

        if (string.IsNullOrWhiteSpace(layout.Id)) issues.Add(new(IssueSeverity.Error, null, L.T("Empty layout identifier.")));
        if (string.IsNullOrWhiteSpace(layout.Name)) issues.Add(new(IssueSeverity.Error, null, L.T("Empty layout name.")));
        if (layout.SchemaVersion > Layout.CurrentSchemaVersion)
            issues.Add(new(IssueSeverity.Error, null, L.F("The layout was created by a newer version of the program (schema {0}).", layout.SchemaVersion)));
        if (!(layout.AspectRatio > 0.1 && layout.AspectRatio < 10))
            issues.Add(new(IssueSeverity.Error, null, L.T("Invalid aspect ratio (expected 0.1…10).")));
        if (layout.Keys is null || layout.Keys.Count == 0)
        {
            issues.Add(new(IssueSeverity.Error, null, L.T("The layout has no keys.")));
            return issues;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in layout.Keys)
        {
            string? id = string.IsNullOrWhiteSpace(k.Id) ? null : k.Id;
            if (id is null) issues.Add(new(IssueSeverity.Error, null, L.F("Key “{0}” has no identifier.", k.Label)));
            else if (!seen.Add(id)) issues.Add(new(IssueSeverity.Error, id, L.T("Duplicate key identifier.")));

            if (string.IsNullOrWhiteSpace(k.Label)) issues.Add(new(IssueSeverity.Error, id, L.T("Empty key label.")));

            if (double.IsNaN(k.X) || double.IsNaN(k.Y) || double.IsNaN(k.Width) || double.IsNaN(k.Height)
                || double.IsInfinity(k.X) || double.IsInfinity(k.Y) || double.IsInfinity(k.Width) || double.IsInfinity(k.Height))
                issues.Add(new(IssueSeverity.Error, id, L.T("Invalid coordinates.")));
            else
            {
                if (k.Width < MinKeySize || k.Height < MinKeySize)
                    issues.Add(new(IssueSeverity.Error, id, L.T("The zone is too small.")));
                if (k.X < -Tolerance || k.Y < -Tolerance || k.X + k.Width > 1 + Tolerance || k.Y + k.Height > 1 + Tolerance)
                    issues.Add(new(IssueSeverity.Error, id, L.T("The zone extends beyond the area.")));
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
                        issues.Add(new(IssueSeverity.Warning, enabled[i].Id, L.F("Overlaps “{0}”.", enabled[j].Id)));
                }
            }

        if (!layout.Keys.Any(k => k.Enabled))
            issues.Add(new(IssueSeverity.Warning, null, L.T("All keys are disabled.")));

        return issues;
    }

    private static void ValidateAction(KeyZone k, string? id, List<ValidationIssue> issues)
    {
        var a = k.Action;
        if (a is null) { issues.Add(new(IssueSeverity.Error, id, L.T("No action assigned."))); return; }
        switch (a.Kind)
        {
            case KeyActionKind.Key:
                if (!KeyCatalog.IsKnown(a.Key)) issues.Add(new(IssueSeverity.Error, id, L.F("Invalid action: “{0}”.", a.Key)));
                break;
            case KeyActionKind.Combo:
                if (!KeyCatalog.IsKnown(a.Key)) issues.Add(new(IssueSeverity.Error, id, L.F("Invalid shortcut key: “{0}”.", a.Key)));
                if (a.Modifiers is null || a.Modifiers.Count == 0)
                    issues.Add(new(IssueSeverity.Error, id, L.T("Shortcut without modifiers.")));
                else
                    foreach (var m in a.Modifiers.Where(m => !AllowedModifiers.Contains(m)))
                        issues.Add(new(IssueSeverity.Error, id, L.F("Invalid modifier “{0}”.", m)));
                if (a.Modifiers is not null && a.Key is not null &&
                    a.Modifiers.Any(m => m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) &&
                    a.Modifiers.Any(m => m.Equals("Alt", StringComparison.OrdinalIgnoreCase)) &&
                    a.Key.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(IssueSeverity.Error, id, L.T("Ctrl+Alt+Del cannot be generated by software (Windows restriction).")));
                break;
            case KeyActionKind.Text:
                if (string.IsNullOrEmpty(a.Text)) issues.Add(new(IssueSeverity.Error, id, L.T("Empty text.")));
                else if (a.Text.Length > 64) issues.Add(new(IssueSeverity.Error, id, L.T("Text is longer than 64 characters.")));
                break;
            case KeyActionKind.None:
                break;
            default:
                issues.Add(new(IssueSeverity.Error, id, L.T("Unknown action type.")));
                break;
        }
    }

    public static bool HasErrors(IEnumerable<ValidationIssue> issues) => issues.Any(i => i.Severity == IssueSeverity.Error);
}
