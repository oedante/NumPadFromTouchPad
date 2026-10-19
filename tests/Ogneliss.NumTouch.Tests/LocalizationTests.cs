using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Ogneliss.NumTouch.Tests;

/// <summary>
/// Полнота перевода. Ключи берутся прямо из исходников: вызовы L.T/L.F/L.N с литералом в C# и {l:T '…'} в XAML.
/// Так непереведённая строка или забытый русский текст в коде ловятся тестом, а не пользователем.
/// </summary>
public class LocalizationTests
{
    private static readonly Regex CsCall = new(@"\bL\.(?:T|F|N)\(\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex CsAnyCall = new(@"\bL\.(?:T|F|N)\(", RegexOptions.Compiled);
    private static readonly Regex XamlCall = new(@"\{l:T\s+'([^']*)'\s*\}", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"\{\d+(?:[,:][^}]*)?\}", RegexOptions.Compiled);
    private static readonly Regex Cyrillic = new(@"\p{IsCyrillic}", RegexOptions.Compiled);

    /// <summary>Файлы, где L.T вызывается с переменной (сам механизм перевода и справочник клавиш).</summary>
    private static readonly string[] DynamicCallFiles = { "L.cs", "TExtension.cs", "KeyCatalog.cs" };

    private static string SrcRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OgnelissNumTouch.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src");
        }
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(SrcRoot, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static readonly Lazy<HashSet<string>> Keys = new(() =>
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in SourceFiles("*.cs"))
            foreach (Match m in CsCall.Matches(File.ReadAllText(f)))
                keys.Add(Regex.Unescape(m.Groups[1].Value));
        foreach (var f in SourceFiles("*.axaml"))
            foreach (Match m in XamlCall.Matches(File.ReadAllText(f)))
                keys.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
        return keys;
    });

    [Fact]
    public void SourceScan_FindsKeys() => Assert.True(Keys.Value.Count > 300, $"Найдено ключей: {Keys.Value.Count}");

    [Fact]
    public void EveryKey_HasRussianTranslation()
    {
        var missing = Keys.Value.Where(k => !RussianStrings.Table.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Нет перевода:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void NoUnusedTranslations()
    {
        var unused = RussianStrings.Table.Keys.Where(k => !Keys.Value.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(unused.Count == 0, "Перевод без использования в коде:\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Placeholders_Match_AndFormat()
    {
        var bad = new List<string>();
        foreach (var (en, ru) in RussianStrings.Table)
        {
            var a = Placeholder.Matches(en).Select(m => m.Value).OrderBy(x => x).ToList();
            var b = Placeholder.Matches(ru).Select(m => m.Value).OrderBy(x => x).ToList();
            if (!a.SequenceEqual(b)) { bad.Add($"{en} ⇄ {ru}"); continue; }
            if (string.IsNullOrWhiteSpace(ru)) bad.Add($"{en}: пустой перевод");
            try
            {
                var args = Enumerable.Range(0, 6).Select(i => (object)(i + 0.5)).ToArray();
                _ = string.Format(CultureInfo.InvariantCulture, en, args);
                _ = string.Format(CultureInfo.InvariantCulture, ru, args);
            }
            catch (FormatException) { bad.Add($"{en}: неверный формат"); }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void TranslationCalls_UseLiterals()
    {
        var bad = new List<string>();
        foreach (var f in SourceFiles("*.cs").Where(f => !DynamicCallFiles.Contains(Path.GetFileName(f))))
        {
            var text = File.ReadAllText(f);
            int all = CsAnyCall.Matches(text).Count, literal = CsCall.Matches(text).Count;
            if (all != literal) bad.Add($"{Path.GetFileName(f)}: {all - literal} вызов(а) без литерала");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>Русский текст в строках кода и в XAML допустим только в таблице перевода (комментарии не в счёт).</summary>
    [Fact]
    public void NoHardcodedCyrillicStrings()
    {
        var bad = new List<string>();
        foreach (var f in SourceFiles("*.cs").Where(f => Path.GetFileName(f) != "RussianStrings.cs"))
        {
            int n = 0;
            foreach (var raw in File.ReadLines(f))
            {
                n++;
                var line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || raw.Contains("loc-ok", StringComparison.Ordinal)) continue;
                var code = StripTrailingComment(raw);
                if (Regex.IsMatch(code, @"""[^""]*\p{IsCyrillic}")) bad.Add($"{Path.GetFileName(f)}:{n}: {raw.Trim()}");
            }
        }
        foreach (var f in SourceFiles("*.axaml"))
        {
            var text = Regex.Replace(File.ReadAllText(f), "<!--.*?-->", "", RegexOptions.Singleline);
            if (Cyrillic.IsMatch(text)) bad.Add($"{Path.GetFileName(f)}: русский текст в разметке");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    private static string StripTrailingComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && inString) { i++; continue; }
            if (c == '"') inString = !inString;
            else if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("ru", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("", "en")]
    public void AutoLanguage_FromCulture(string culture, string expected) =>
        Assert.Equal(expected, L.Resolve(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void English_IsIdentity_AndUnknownFallsBack()
    {
        // Сборка тестов работает на английском (TestLanguage): ключ возвращается как есть.
        Assert.Equal("Calibration", L.T("Calibration"));
        Assert.Equal("no such key", L.T("no such key"));
        Assert.Equal("Point 2 of 5", L.F("Point {0} of {1}", 2, 5));
        Assert.Equal("Калибровка", RussianStrings.Table["Calibration"]);
    }

    [Fact]
    public void Retranslate_BringsAnyLanguageToCurrent()
    {
        // Текущий язык тестов — английский: русское сообщение, сформированное до смены языка, становится английским.
        Assert.Equal("Calibration", L.Retranslate(RussianStrings.Table["Calibration"]));
        Assert.Equal("Calibration", L.Retranslate("Calibration"));
        Assert.Equal("/dev/uinput: errno 5", L.Retranslate("/dev/uinput: errno 5"));
    }
}
