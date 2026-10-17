using System.Collections.Concurrent;
using System.Text;

namespace Ogneliss.NumTouch.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

public interface ILog
{
    void Write(LogLevel level, string message, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Info(this ILog log, string m) => log.Write(LogLevel.Info, m);
    public static void Warn(this ILog log, string m, Exception? ex = null) => log.Write(LogLevel.Warning, m, ex);
    public static void Error(this ILog log, string m, Exception? ex = null) => log.Write(LogLevel.Error, m, ex);
    public static void Debug(this ILog log, string m) => log.Write(LogLevel.Debug, m);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message, Exception? ex = null) { }
}

/// <summary>
/// Технический журнал с ротацией (1 МБ × 3 файла). Политика конфиденциальности: в журнал НЕ пишутся
/// нажатые клавиши, введённый текст и координаты касаний — только запуск, режимы, устройства, ошибки.
/// Последние записи доступны в памяти для экрана диагностики.
/// </summary>
public sealed class FileLog : ILog
{
    private const long MaxBytes = 1024 * 1024;
    private const int Keep = 3;
    private readonly string _dir;
    private readonly string _path;
    private readonly object _sync = new();
    private readonly ConcurrentQueue<string> _recent = new();

    public FileLog(string dir)
    {
        _dir = dir;
        _path = Path.Combine(dir, "numtouch.log");
    }

    public bool Enabled { get; set; } = true;
    public LogLevel MinLevel { get; set; } = LogLevel.Info;
    public string FilePath => _path;
    public string Directory => _dir;
    public IReadOnlyCollection<string> Recent => _recent.ToArray();
    public event Action<string>? Written;

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinLevel) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex is not null) line += $" | {ex.GetType().Name}: {ex.Message}";
        _recent.Enqueue(line);
        while (_recent.Count > 200 && _recent.TryDequeue(out _)) { }
        try { Written?.Invoke(line); } catch { /* ignore */ }
        if (!Enabled) return;
        try
        {
            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(_dir);
                Rotate();
                File.AppendAllText(_path, line + Environment.NewLine + (ex is not null && level >= LogLevel.Error ? ex + Environment.NewLine : ""), Encoding.UTF8);
            }
        }
        catch { /* журнал никогда не роняет приложение */ }
    }

    private void Rotate()
    {
        var fi = new FileInfo(_path);
        if (!fi.Exists || fi.Length < MaxBytes) return;
        for (int i = Keep - 1; i >= 1; i--)
        {
            var src = i == 1 ? _path : $"{_path}.{i - 1}";
            var dst = $"{_path}.{i}";
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
    }
}
