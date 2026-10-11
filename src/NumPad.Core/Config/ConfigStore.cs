using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NumPad.Core.Layouts;

namespace NumPad.Core.Config;

public sealed record LoadResult<T>(T Value, IReadOnlyList<string> Warnings);

/// <summary>
/// Хранилище настроек: <c>settings.json</c> и <c>layouts\*.json</c> в каталоге пользователя.
/// Запись атомарная (временный файл + замена), повреждённые файлы переименовываются в *.corrupt-*.json
/// и заменяются значениями по умолчанию — приложение всегда запускается.
/// </summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConfigStore(string rootDir)
    {
        RootDir = rootDir;
        LayoutsDir = Path.Combine(rootDir, "layouts");
        SettingsPath = Path.Combine(rootDir, "settings.json");
    }

    public string RootDir { get; }
    public string LayoutsDir { get; }
    public string SettingsPath { get; }
    public string LogsDir => Path.Combine(RootDir, "logs");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NumPadFromTouchPad");

    // ------------------------------------------------------------ settings

    public LoadResult<AppSettings> LoadSettings()
    {
        var warnings = new List<string>();
        if (!File.Exists(SettingsPath)) return new(new AppSettings(), warnings);
        try
        {
            var text = File.ReadAllText(SettingsPath, Encoding.UTF8);
            var s = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions)
                    ?? throw new JsonException("Пустой файл настроек.");
            if (s.SchemaVersion > AppSettings.CurrentSchemaVersion)
                warnings.Add("Файл настроек создан более новой версией программы; неизвестные параметры будут потеряны при сохранении.");
            return new(s.Sanitize(), warnings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            string? moved = Quarantine(SettingsPath);
            warnings.Add($"Файл настроек повреждён ({ex.Message}). Загружены настройки по умолчанию"
                         + (moved is null ? "." : $"; копия сохранена: {Path.GetFileName(moved)}."));
            return new(new AppSettings(), warnings);
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        AtomicWrite(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    // ------------------------------------------------------------ layouts

    /// <summary>Встроенные раскладки + пользовательские файлы. Файл с Id встроенной раскладки переопределяет её.</summary>
    public LoadResult<List<Layout>> LoadLayouts()
    {
        var warnings = new List<string>();
        var result = BuiltInLayouts.All.ToDictionary(l => l.Id, l => l, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(LayoutsDir))
        {
            foreach (var file in Directory.EnumerateFiles(LayoutsDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var layout = ParseLayout(File.ReadAllText(file, Encoding.UTF8));
                    var issues = LayoutValidator.Validate(layout);
                    if (LayoutValidator.HasErrors(issues))
                    {
                        warnings.Add($"Раскладка {Path.GetFileName(file)} пропущена: " +
                                     string.Join("; ", issues.Where(i => i.Severity == IssueSeverity.Error).Take(3)));
                        continue;
                    }
                    layout.IsBuiltIn = false;
                    result[layout.Id] = layout;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    string? moved = Quarantine(file);
                    warnings.Add($"Раскладка {Path.GetFileName(file)} повреждена и отложена"
                                 + (moved is null ? "" : $" ({Path.GetFileName(moved)})") + $": {ex.Message}");
                }
            }
        }
        var ordered = result.Values
            .OrderBy(l => BuiltInLayouts.Get(l.Id) is null ? 1 : 0)
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new(ordered, warnings);
    }

    public static Layout ParseLayout(string json)
    {
        var layout = JsonSerializer.Deserialize<Layout>(json, JsonOptions) ?? throw new JsonException("Пустой файл раскладки.");
        layout.Keys ??= new();
        foreach (var k in layout.Keys)
        {
            k.Action ??= new KeyAction();
            k.Action.Modifiers ??= new();
        }
        return layout;
    }

    public static string SerializeLayout(Layout layout)
    {
        var copy = layout.Clone();
        copy.SchemaVersion = Layout.CurrentSchemaVersion;
        copy.IsBuiltIn = false;
        return JsonSerializer.Serialize(copy, JsonOptions);
    }

    /// <summary>Сохраняет раскладку. Бросает <see cref="InvalidOperationException"/>, если есть ошибки валидации.</summary>
    public void SaveLayout(Layout layout)
    {
        var issues = LayoutValidator.Validate(layout);
        if (LayoutValidator.HasErrors(issues))
            throw new InvalidOperationException(string.Join(Environment.NewLine, issues.Where(i => i.Severity == IssueSeverity.Error)));
        AtomicWrite(LayoutPath(layout.Id), SerializeLayout(layout));
    }

    /// <summary>Удаляет пользовательский файл раскладки. Для встроенной это означает возврат к стандартной.</summary>
    public void DeleteLayout(string id)
    {
        var path = LayoutPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    public Layout? ResetToBuiltIn(string id)
    {
        DeleteLayout(id);
        return BuiltInLayouts.Get(id);
    }

    public void ExportLayout(Layout layout, string path) => AtomicWrite(path, SerializeLayout(layout));

    /// <summary>Импорт: при совпадении Id с существующей раскладкой выдаётся новый Id.</summary>
    public Layout ImportLayout(string path, IEnumerable<string> existingIds)
    {
        var layout = ParseLayout(File.ReadAllText(path, Encoding.UTF8));
        var ids = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(layout.Id) || ids.Contains(layout.Id))
            layout.Id = UniqueId(string.IsNullOrWhiteSpace(layout.Id) ? "layout" : layout.Id, ids);
        layout.IsBuiltIn = false;
        return layout;
    }

    public static string UniqueId(string baseId, ICollection<string> existing)
    {
        string clean = SafeFileName(baseId);
        if (!existing.Contains(clean)) return clean;
        for (int i = 2; ; i++)
        {
            string candidate = $"{clean}-{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    public string LayoutPath(string id) => Path.Combine(LayoutsDir, SafeFileName(id) + ".json");

    private static string SafeFileName(string id)
    {
        var sb = new StringBuilder();
        foreach (char ch in id.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-');
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "layout" : s;
    }

    // ------------------------------------------------------------ io

    public static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* ignore */ } }
        }
    }

    private static string? Quarantine(string path)
    {
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}.bak");
            File.Move(path, target, overwrite: true);
            return target;
        }
        catch { return null; }
    }
}
