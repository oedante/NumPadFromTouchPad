using System.Text;

namespace Ogneliss.NumTouch.Core.Config;

/// <summary>
/// Обнаружение аварийного завершения и данные для восстановления.
/// • <c>session.lock</c> создаётся при запуске и удаляется при штатном выходе. Если при старте файл есть —
///   прошлый сеанс завершился аварийно: нужно отпустить модификаторы и восстановить настройки тачпада.
/// • <c>pending-restore.json</c> — исходные настройки тачпада, сохранённые ДО их изменения.
/// </summary>
public sealed class SessionGuard
{
    private readonly string _lockPath;
    private readonly string _restorePath;

    public SessionGuard(string rootDir)
    {
        Directory.CreateDirectory(rootDir);
        _lockPath = Path.Combine(rootDir, "session.lock");
        _restorePath = Path.Combine(rootDir, "pending-restore.json");
    }

    public bool PreviousSessionCrashed { get; private set; }

    /// <summary>Начало сеанса. Возвращает true, если предыдущий сеанс завершился аварийно.</summary>
    public bool Begin()
    {
        PreviousSessionCrashed = File.Exists(_lockPath);
        File.WriteAllText(_lockPath, $"{Environment.ProcessId} {DateTime.UtcNow:O}", Encoding.UTF8);
        return PreviousSessionCrashed;
    }

    public void EndClean()
    {
        try { if (File.Exists(_lockPath)) File.Delete(_lockPath); } catch { /* ignore */ }
    }

    public void SavePendingRestore(string payload) => ConfigStore.AtomicWrite(_restorePath, payload);

    public string? LoadPendingRestore()
    {
        try { return File.Exists(_restorePath) ? File.ReadAllText(_restorePath, Encoding.UTF8) : null; }
        catch { return null; }
    }

    public void ClearPendingRestore()
    {
        try { if (File.Exists(_restorePath)) File.Delete(_restorePath); } catch { /* ignore */ }
    }

    public bool HasPendingRestore => File.Exists(_restorePath);
}
