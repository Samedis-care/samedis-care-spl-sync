using System.IO;
using SamedisCare.SplSync.Core.Sync;

namespace SamedisCare.SplSync.Tray;

/// <summary>
/// Zentrale Laufzeit-Pfade. Alles liegt <b>relativ zum EXE-Verzeichnis</b>
/// (<see cref="AppContext.BaseDirectory"/>) — konsistent damit, dass auch die config.yml dort
/// liegt (siehe App.ResolveConfigPath). Vorteil: das Tool ist portabel, ein Kopieren des
/// EXE-Ordners nimmt Konfiguration, Logs, State-DB und Scratch-Cache mit; kein Streuen über
/// %PROGRAMDATA%. Voraussetzung: der EXE-Ordner ist beschreibbar (z. B. C:\spl-sync\, nicht
/// C:\Program Files\).
/// </summary>
public static class AppPaths
{
    /// <summary>Verzeichnis, in dem die EXE liegt.</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>&lt;exe&gt;\logs</summary>
    public static string LogsDir => Path.Combine(BaseDir, "logs");

    /// <summary>&lt;exe&gt;\state</summary>
    public static string StateDir => Path.Combine(BaseDir, "state");

    /// <summary>&lt;exe&gt;\scratch</summary>
    public static string ScratchRoot => Path.Combine(BaseDir, "scratch");

    /// <summary>&lt;exe&gt;\state\state.sqlite</summary>
    public static string StateDbPath => Path.Combine(StateDir, "state.sqlite");

    /// <summary>Öffnet die State-DB und legt das Verzeichnis bei Bedarf an.</summary>
    public static StateDb OpenStateDb()
    {
        Directory.CreateDirectory(StateDir);
        return new StateDb(StateDbPath);
    }

    /// <summary>Scratch-Unterordner pro Mandant (&lt;exe&gt;\scratch\&lt;tenant_id&gt;).</summary>
    public static string TenantScratchDir(string samedisTenantId)
        => Path.Combine(ScratchRoot, samedisTenantId);

    /// <summary>
    /// Prüft, ob der EXE-Ordner beschreibbar ist (dort landen config.yml, logs\, state\, scratch\).
    /// Schreibt testweise eine Datei und löscht sie wieder. Fängt ein versehentliches Deployment
    /// in einen schreibgeschützten Ort ab (typisch C:\Program Files\, wo Windows Schreibzugriffe
    /// blockiert oder in den VirtualStore umleitet). Liefert false + Fehlermeldung, wenn nicht.
    /// </summary>
    public static bool IsBaseDirWritable(out string? error)
    {
        error = null;
        try
        {
            var probe = Path.Combine(BaseDir, $".writetest-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
