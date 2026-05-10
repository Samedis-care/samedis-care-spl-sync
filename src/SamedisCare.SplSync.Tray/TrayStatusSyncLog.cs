using SamedisCare.SplSync.Core.Api;

namespace SamedisCare.SplSync.Tray;

/// <summary>
/// ISyncLog-Implementierung, die Meldungen sowohl an die Konsole/STDERR weitergibt
/// (fuer attached debugger / OutputDebugString-Sichtbarkeit) als auch ins TrayStatus
/// schiebt — sodass z. B. PdfPickupWatcher-Diagnostik im "Letzte Meldungen"-Panel
/// sichtbar wird, statt nur ins Logfile zu fliessen.
///
/// Vorher loggte der Watcher in ein ConsoleSyncLog, das in der GUI-Tray-EXE keine sichtbare
/// Konsole hat — der User sah dadurch nie, dass eine Datei gefunden / verarbeitet / verworfen
/// wurde. Diese Klasse loest das.
/// </summary>
internal sealed class TrayStatusSyncLog : ISyncLog
{
    private readonly TrayStatus _status;
    private readonly string _prefix;
    private readonly ISyncLog _innerConsole;

    public int Level { get; }

    public TrayStatusSyncLog(TrayStatus status, string prefix, int level = 1)
    {
        _status = status;
        _prefix = string.IsNullOrEmpty(prefix) ? "" : $"{prefix}: ";
        Level = level;
        _innerConsole = new ConsoleSyncLog(level);
    }

    public void Info(string message)
    {
        _innerConsole.Info(message);
        if (Level >= 1) Push(message);
    }

    public void Warn(string message)
    {
        _innerConsole.Warn(message);
        if (Level >= 1) Push("⚠ " + message);
    }

    public void Error(string message, Exception? ex = null)
    {
        _innerConsole.Error(message, ex);
        var line = ex == null ? "✖ " + message : $"✖ {message} ({ex.GetType().Name}: {ex.Message})";
        Push(line);
    }

    public void Debug(string message)
    {
        _innerConsole.Debug(message);
        // Debug nur in die Konsole, nicht ins UI — sonst wird die Liste ueberfuellt.
    }

    private void Push(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {_prefix}{text}";
        lock (_status.RecentErrors)
        {
            _status.RecentErrors.Add(line);
            if (_status.RecentErrors.Count > 50) _status.RecentErrors.RemoveAt(0);
        }
    }
}
