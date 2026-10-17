using Ogneliss.NumTouch.Core.Config;

namespace Ogneliss.NumTouch.App;

/// <summary>
/// Один экземпляр на пользователя. Именованный Mutex работает на Windows, Linux и macOS; просьба «показать окно»
/// передаётся файлом-сигналом (именованные события Windows на Unix недоступны).
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static FileSystemWatcher? _watcher;
    private static string SignalPath => Path.Combine(ConfigStore.DefaultRoot, "show.request");

    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(true, "OgnelissNumTouch.SingleInstance." + Environment.UserName, out bool created);
            if (!created) { _mutex.Dispose(); _mutex = null; }
            return created;
        }
        catch { return true; } // если механизм недоступен — не мешаем запуску
    }

    public static void RequestShow()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.DefaultRoot);
            File.WriteAllText(SignalPath, DateTime.UtcNow.ToString("O"));
        }
        catch { /* ignore */ }
    }

    /// <summary>Подписаться на просьбы второго экземпляра (вызывается из фонового потока).</summary>
    public static void Watch(Action onShowRequested)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.DefaultRoot);
            if (File.Exists(SignalPath)) File.Delete(SignalPath);
            _watcher = new FileSystemWatcher(ConfigStore.DefaultRoot, "show.request")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) =>
            {
                try { if (File.Exists(SignalPath)) File.Delete(SignalPath); } catch { /* ignore */ }
                onShowRequested();
            };
            _watcher.Created += h;
            _watcher.Changed += h;
        }
        catch { /* ignore */ }
    }

    public static void Release()
    {
        _watcher?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        _mutex?.Dispose();
    }
}
