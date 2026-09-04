using SamedisCare.SplSync.Core.Api;
using SamedisCare.Api.Auth;
using SamedisCare.Api.Http;
using SamedisCare.Api.Query;
using SamedisCare.Helper.Logging;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// File-system watcher around the configured Actimed PDF dropoff folder. Whenever a new PDF
/// shows up, it triggers an upload via UploadEngine.RunPdfPickup. Implements the trigger side
/// of "Modus 1" — the technician finishes the test in Actimed, prints the protocol as PDF
/// (e.g. via PDFCreator with a fixed output folder), and the file lands here.
///
/// Robustness notes:
///   - We debounce. Some PDF printers write the file in stages, so Created can fire while the
///     file is still partially written. We wait until the file size stays stable for ~750ms.
///   - We swallow IOException on partial reads and retry on the next poll.
///   - We dedupe. A single physical file should not be processed twice within a short window.
///
/// Lifecycle einer PDF-Datei:
///   1) PDF erscheint in &lt;protocol_pdf_dir&gt; (Druck oder File-Move durch PDF-Drucker).
///   2) Created/Renamed-Event triggert Handle().
///   3) WaitForStableFile() blockt bis Datei fertig geschrieben.
///   4) UploadEngine.RunPdfPickup() macht den Issue-Update + PDF-Upload.
///   5) Bei Erfolg → Datei wird nach &lt;protocol_pdf_dir&gt;/processed/&lt;yyyy-MM-dd&gt;/ verschoben.
///      So liegt im Pickup-Ordner immer nur, was noch offen ist (z. B. weil der DB-Eintrag
///      noch nicht da ist), und bei einem Restart geht der Initial Sweep nicht das ganze Archiv durch.
///   6) Bei Misserfolg (kein DB-Match) bleibt die Datei liegen und wird beim naechsten Tick erneut versucht.
/// </summary>
public class PdfPickupWatcher : IDisposable
{
    private readonly UploadEngine _upload;
    private readonly ISyncLog _log;
    private readonly string _folder;
    private readonly FileSystemWatcher _fsw;
    private readonly HashSet<string> _recentlyProcessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public PdfPickupWatcher(UploadEngine upload, ISyncLog log, string folder)
    {
        _upload = upload;
        _log = log;
        _folder = folder;

        if (!Directory.Exists(_folder))
        {
            _log.Warn($"PdfPickupWatcher: folder '{_folder}' does not exist (yet). Watcher disabled.");
            _fsw = new FileSystemWatcher(); // not started
            return;
        }

        // Nur Created und Renamed reichen: PDF-Drucker schreiben typischerweise in einen
        // Temp-Namen und benennen am Ende um (.tmp -> .pdf), das ist ein Renamed-Event.
        // PDFCreator und "Microsoft Print to PDF" beide. Changed-Events nehmen wir bewusst NICHT —
        // die feuern auch wenn wir die Datei selbst nach 'processed/' verschieben und wuerden so
        // einen Re-Process-Loop erzeugen. IncludeSubdirectories=false sorgt zusaetzlich dafuer,
        // dass die Move-Operation in 'processed/' keinen Event mehr im Watcher erzeugt.
        _fsw = new FileSystemWatcher(_folder, "*.pdf")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };
        _fsw.Created += (_, e) => HandleAsync(e.FullPath);
        _fsw.Renamed += (_, e) => HandleAsync(e.FullPath);
    }

    public void Start()
    {
        if (_fsw == null || string.IsNullOrEmpty(_fsw.Path)) return;
        _fsw.EnableRaisingEvents = true;
        _log.Info($"PdfPickupWatcher: watching '{_folder}'");

        // Initial sweep — pick up files that were already there when we started.
        // Subdir 'processed/' ist dank SearchOption.TopDirectoryOnly ausgeschlossen, dort liegt
        // das Archiv und soll nicht erneut verarbeitet werden.
        try
        {
            var pending = Directory.EnumerateFiles(_folder, "*.pdf", SearchOption.TopDirectoryOnly).ToList();
            if (pending.Count > 0)
                _log.Info($"PdfPickupWatcher: Initial Sweep — {pending.Count} PDF(s) im Pickup-Ordner.");
            foreach (var existing in pending)
                HandleAsync(existing);
        }
        catch (Exception ex)
        {
            _log.Warn($"PdfPickupWatcher initial sweep failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_fsw != null) _fsw.EnableRaisingEvents = false;
    }

    private void HandleAsync(string path)
    {
        // Fire-and-forget on a thread-pool thread; Watcher events come on its own thread,
        // and we do not want to block subsequent events while waiting for the file to settle.
        Task.Run(() =>
        {
            try { Handle(path); }
            catch (Exception ex) { _log.Error($"PdfPickupWatcher: {ex.Message}", ex); }
        });
    }

    private void Handle(string path)
    {
        if (!IsCandidate(path)) return;

        if (!WaitForStableFile(path, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(20)))
        {
            _log.Warn($"PdfPickupWatcher: file '{Path.GetFileName(path)}' never settled.");
            return;
        }

        var ok = _upload.RunPdfPickup(path);
        if (ok)
        {
            _log.Info($"PdfPickupWatcher: processed '{Path.GetFileName(path)}'");
            ArchivePdf(path);
        }
        // Bei !ok bleibt die Datei liegen; sie wird beim naechsten Tick neu versucht
        // (z. B. wenn der DB-Eintrag in A3_FINISHED_TEST mit Verzoegerung kommt).
    }

    /// <summary>
    /// Verschiebt die erfolgreich verarbeitete PDF in einen Tages-Subfolder unter dem
    /// Pickup-Ordner. Sinn: der Pickup-Ordner enthaelt nur "noch zu tuen", das Archiv
    /// bleibt auditierbar erhalten, und der Initial Sweep beim Tray-Restart geht nicht
    /// das ganze Archiv erneut durch.
    /// Bei Datei-Konflikt (gleicher Name am gleichen Tag) wird ein Suffix angehaengt.
    /// </summary>
    private void ArchivePdf(string path)
    {
        try
        {
            var archiveDir = Path.Combine(_folder, "processed", DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(archiveDir);

            var name = Path.GetFileName(path);
            var target = Path.Combine(archiveDir, name);
            // Konflikt-Behandlung: <name>_<n>.pdf
            var counter = 1;
            while (File.Exists(target))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                target = Path.Combine(archiveDir, $"{stem}_{counter}{ext}");
                counter++;
            }

            File.Move(path, target);
            _log.Info($"PdfPickupWatcher: archiviert nach '{Path.GetRelativePath(_folder, target)}'.");
        }
        catch (Exception ex)
        {
            // Archivieren ist Best-Effort: wenn das Move fehlschlaegt (Permission, locked file),
            // bleibt die Datei liegen — beim naechsten Tick versuchen wir sie erneut zu uploaden.
            // Damit entsteht kein Daten-Verlust, hoechstens ein Doppel-Upload, was auf Samedis-Seite
            // idempotent durch external_id abgefangen wird.
            _log.Warn($"PdfPickupWatcher: konnte '{Path.GetFileName(path)}' nicht archivieren: {ex.Message}");
        }
    }

    private bool IsCandidate(string path)
    {
        lock (_lock)
        {
            if (_recentlyProcessed.Contains(path)) return false;
            _recentlyProcessed.Add(path);

            // Cap the dedupe set so it cannot grow unbounded.
            if (_recentlyProcessed.Count > 256)
            {
                var oldest = _recentlyProcessed.Take(64).ToList();
                foreach (var o in oldest) _recentlyProcessed.Remove(o);
            }
        }
        return true;
    }

    /// <summary>
    /// Polls the file size at small intervals; returns true when the size has not changed
    /// for `quietWindow` and the file is openable for read. Bails after `timeout`.
    /// </summary>
    private static bool WaitForStableFile(string path, TimeSpan quietWindow, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long lastSize = -1;
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path)) return false;
            long size;
            try { size = new FileInfo(path).Length; }
            catch { Thread.Sleep(100); continue; }

            if (size != lastSize)
            {
                lastSize = size;
                stableSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - stableSince >= quietWindow)
            {
                // Final check: openable for read?
                try
                {
                    using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return true;
                }
                catch (IOException) { /* still being written, wait more */ }
            }

            Thread.Sleep(150);
        }
        return false;
    }

    public void Dispose() => _fsw?.Dispose();
}
