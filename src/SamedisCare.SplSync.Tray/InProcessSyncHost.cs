using System.IO;
using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Api;
using SamedisCare.SplSync.Core.Config;
using SamedisCare.SplSync.Core.Sync;

namespace SamedisCare.SplSync.Tray;

/// <summary>
/// In-process worker hosted by the Tray app. The Service-EXE has been removed from the
/// solution; the Tray is the single deployable.
///
/// Three independent loops per running instance:
///   1) Download loop    — every Sync.DownloadIntervalMinutes, pulls inventory + open issues
///                          for every enabled tenant.
///   2) Upload poll loop — every Sync.UploadPollIntervalSeconds, scans A3_FINISHED_TEST for
///                          new completed checks and runs Mode 2 (PNG) for any that show up.
///   3) PDF watcher      — FileSystemWatcher on Actimed.ProtocolPdfDir, runs Mode 1 (PDF
///                          attach) whenever a new PDF lands.
///
/// All three feed into the same UploadEngine; status callbacks bubble up to the tray icon.
/// On ActimedLockedException the host pauses and emits OnLockBlocked so the UI can surface
/// the modal "please close Actimed" dialog.
/// </summary>
public class InProcessSyncHost
{
    private readonly string _configPath;
    private readonly TrayStatus _status;

    private CancellationTokenSource? _cts;
    private Task? _downloadLoop;
    private Task? _uploadLoop;
    private readonly List<PdfPickupWatcher> _pdfWatchers = new();

    public event Action<TrayStatus>? OnStatusChanged;
    public event Action? OnLockBlocked;

    public InProcessSyncHost(string configPath, TrayStatus status)
    {
        _configPath = configPath;
        _status = status;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _downloadLoop = Task.Run(() => DownloadLoop(_cts.Token));
        _uploadLoop   = Task.Run(() => UploadPollLoop(_cts.Token));
        StartPdfWatchers();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { Task.WaitAll(new[] { _downloadLoop, _uploadLoop }.Where(t => t != null).ToArray()!, TimeSpan.FromSeconds(2)); } catch { }
        StopPdfWatchers();
        _cts = null;
        _downloadLoop = null;
        _uploadLoop = null;
    }

    private void StopPdfWatchers()
    {
        foreach (var w in _pdfWatchers) { try { w.Stop(); w.Dispose(); } catch { } }
        _pdfWatchers.Clear();
    }

    /// <summary>
    /// Nach einem Config-Save aufzurufen, damit zur Laufzeit gemachte UI-Aenderungen wirklich
    /// aktiv werden — insbesondere fuer Settings, die nicht in der periodischen Loop-Iteration
    /// neu gelesen werden:
    ///   * Pdf-Pickup-Watcher (Pfad-Aenderung, Modus 1 ein/aus)
    /// Download/Upload-Intervalle und alles, was die Loops bei jedem Tick neu laden, ist
    /// hier nicht noetig — die werden ohnehin pro Iteration aus der config.yml gelesen.
    /// </summary>
    public void ReloadDynamicComponents()
    {
        // Pdf-Watcher: stop + neu starten mit aktualisierter Config.
        StopPdfWatchers();
        StartPdfWatchers();
    }

    /// <summary>Manually trigger one full sync cycle for all enabled tenants.</summary>
    public void RunOnceAsync() => Task.Run(() =>
    {
        var cfg = TryLoadConfig();
        if (cfg == null) return;
        var stateDb = OpenStateDb();
        foreach (var tenant in cfg.Tenants.Where(t => t.Enabled))
        {
            RunDownloadFor(cfg, tenant, stateDb);
            RunUploadPngFor(cfg, tenant, stateDb);
        }
    });

    // -----------------------------------------------------------------------------
    // Loops
    // -----------------------------------------------------------------------------

    private async Task DownloadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var cfg = TryLoadConfig();
            if (cfg == null)
            {
                await SafeDelay(TimeSpan.FromSeconds(30), token);
                continue;
            }

            var stateDb = OpenStateDb();
            UpdateStatus(SyncState.Running, "Download laeuft...");
            var anyError = false;
            var anyWarn = false;

            foreach (var tenant in cfg.Tenants.Where(t => t.Enabled))
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var ok = RunDownloadFor(cfg, tenant, stateDb);
                    if (!ok) anyWarn = true;
                }
                catch (ActimedLockedException) { anyError = true; OnLockBlocked?.Invoke(); break; }
                catch (Exception ex)
                {
                    anyError = true;
                    PushError($"{tenant.Name} (Download): {ex.Message}");
                }
            }

            _status.LastRun = DateTimeOffset.Now;
            if (anyError)     UpdateStatus(SyncState.Error, "Fehler beim Download.");
            else if (anyWarn) UpdateStatus(SyncState.Warn,  "Download mit Warnungen.");
            else              UpdateStatus(SyncState.Ok,    $"Letzter Download: {DateTime.Now:HH:mm:ss}");

            var interval = TimeSpan.FromMinutes(Math.Max(1, cfg.Sync.DownloadIntervalMinutes));
            await SafeDelay(interval, token);
        }
    }

    private async Task UploadPollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var cfg = TryLoadConfig();
            if (cfg == null || !cfg.Sync.UploadFinishedIssues || !cfg.Sync.UploadModePngOnCompletion)
            {
                // Mode 2 disabled — sleep a bit, the PDF watcher (Mode 1) keeps running.
                await SafeDelay(TimeSpan.FromSeconds(30), token);
                continue;
            }

            var stateDb = OpenStateDb();
            foreach (var tenant in cfg.Tenants.Where(t => t.Enabled))
            {
                if (token.IsCancellationRequested) break;
                try { RunUploadPngFor(cfg, tenant, stateDb); }
                catch (ActimedLockedException) { OnLockBlocked?.Invoke(); break; }
                catch (Exception ex) { PushError($"{tenant.Name} (Upload-PNG): {ex.Message}"); }
            }

            var interval = TimeSpan.FromSeconds(Math.Max(5, cfg.Sync.UploadPollIntervalSeconds));
            await SafeDelay(interval, token);
        }
    }

    // -----------------------------------------------------------------------------
    // Per-tenant helpers
    // -----------------------------------------------------------------------------

    private bool RunDownloadFor(AppConfig cfg, TenantConfig tenant, StateDb stateDb)
    {
        var ctx = BuildContext(cfg, tenant, stateDb);
        var dl = new DownloadEngine(ctx).Run();
        // Engine-Warnungen in die "Letzte Meldungen"-Liste schieben, sonst sieht der User nichts.
        foreach (var e in dl.Inventories.Errors) PushError($"{tenant.Name} (Inventories): {e}");
        foreach (var e in dl.Issues.Errors)      PushError($"{tenant.Name} (Issues): {e}");

        // Zusammenfassung — auch bei Erfolg, damit der User sieht "es ist was passiert".
        // Format: "<Mandant> (Download): 220 Inventare (5 neu, 12 repariert, 0 skip), 12 Aufträge (3 neu, 1 skip)"
        var inv = dl.Inventories;
        var iss = dl.Issues;
        var invRepairFragment = inv.Repaired > 0 ? $", {inv.Repaired} repariert" : "";
        var summary = $"{tenant.Name} (Download): " +
                      $"{inv.Applied} Inventare ({inv.Created} neu{invRepairFragment}, {inv.Skipped} skip), " +
                      $"{iss.Applied} Aufträge ({iss.Created} neu, {iss.Skipped} skip)";
        PushError(summary);

        return dl.IsSuccess;
    }

    private void RunUploadPngFor(AppConfig cfg, TenantConfig tenant, StateDb stateDb)
    {
        var ctx = BuildContext(cfg, tenant, stateDb);
        var scratch = TenantScratchDir(tenant);
        var ul = new UploadEngine(ctx, scratch).RunPngOnCompletion();
        foreach (var e in ul.Errors) PushError($"{tenant.Name} (Upload-PNG): {e}");

        // Nur Zusammenfassung pushen, wenn etwas passiert ist — der Polling-Loop laeuft alle 30s
        // und wir wollen die "Letzte Meldungen"-Liste nicht mit "0 Prüfungen" zumüllen.
        if (ul.Applied > 0 || ul.Skipped > 0)
        {
            PushError($"{tenant.Name} (Upload-PNG): " +
                      $"{ul.Applied} Prüfungen hochgeladen, {ul.Skipped} skip");
        }
    }

    private SyncContext BuildContext(AppConfig cfg, TenantConfig tenant, StateDb stateDb)
    {
        // TrayStatusSyncLog schreibt zusaetzlich ins "Letzte Meldungen"-Panel —
        // sonst sieht der User keine Watcher-Diagnose (gefunden, verworfen, kein DB-Match etc.).
        var log = new TrayStatusSyncLog(_status, tenant.Name, cfg.Logging.Level);
        var repo = BuildActimedRepository(cfg);
        return SyncContextBuilder.Build(cfg, tenant, repo, stateDb, log);
    }

    // -----------------------------------------------------------------------------
    // PDF watcher wiring
    // -----------------------------------------------------------------------------

    private void StartPdfWatchers()
    {
        var cfg = TryLoadConfig();
        if (cfg == null)
        {
            PushError("PdfWatcher: Konfiguration konnte nicht geladen werden — Watcher nicht gestartet.");
            return;
        }

        // Diagnose: warum genau wird der Watcher nicht gestartet? Frueher hat StartPdfWatchers
        // bei jeder Bedingung still aufgegeben, was den User ratlos zurueckliess.
        if (!cfg.Sync.UploadFinishedIssues)
        {
            PushError("PdfWatcher: nicht aktiv (sync.upload_finished_issues = false). " +
                      "Master-Schalter aktivieren, falls Uploads gewuenscht.");
            return;
        }
        if (!cfg.Sync.UploadModePdfPickup)
        {
            PushError("PdfWatcher: nicht aktiv (sync.upload_mode_pdf_pickup = false). " +
                      "In den Einstellungen 'Modus 1 — PDF-Pickup-Watcher' aktivieren.");
            return;
        }
        if (string.IsNullOrWhiteSpace(cfg.Actimed.ProtocolPdfDir))
        {
            PushError("PdfWatcher: nicht aktiv (actimed.protocol_pdf_dir leer). " +
                      "Pfad in den Einstellungen setzen.");
            return;
        }

        // Falls der Ordner noch nicht existiert: anlegen, sonst wirft FileSystemWatcher.
        // Das ist defensiv — der Techniker konfiguriert hier oft einen Pfad bevor sein PDF-Drucker
        // den ersten Druck dort abgelegt hat.
        try
        {
            if (!Directory.Exists(cfg.Actimed.ProtocolPdfDir))
            {
                Directory.CreateDirectory(cfg.Actimed.ProtocolPdfDir);
                PushError($"PdfWatcher: Verzeichnis '{cfg.Actimed.ProtocolPdfDir}' existierte nicht und wurde angelegt.");
            }
        }
        catch (Exception ex)
        {
            PushError($"PdfWatcher: Verzeichnis '{cfg.Actimed.ProtocolPdfDir}' konnte nicht angelegt werden: {ex.Message}");
            return;
        }

        var stateDb = OpenStateDb();
        var enabledTenants = cfg.Tenants.Where(t => t.Enabled).ToList();
        if (enabledTenants.Count == 0)
        {
            PushError("PdfWatcher: kein aktiver Mandant in der Konfiguration — Watcher nicht gestartet.");
            return;
        }

        var startedCount = 0;
        foreach (var tenant in enabledTenants)
        {
            try
            {
                var ctx = BuildContext(cfg, tenant, stateDb);
                var scratch = TenantScratchDir(tenant);
                var upload = new UploadEngine(ctx, scratch);
                var watcher = new PdfPickupWatcher(upload, ctx.Log, cfg.Actimed.ProtocolPdfDir);
                watcher.Start();
                _pdfWatchers.Add(watcher);
                startedCount++;
            }
            catch (Exception ex)
            {
                PushError($"{tenant.Name} (PdfWatcher): {ex.Message}");
            }
        }

        if (startedCount > 0)
            PushError($"PdfWatcher aktiv: {startedCount} Mandant(en), Pfad '{cfg.Actimed.ProtocolPdfDir}'.");
    }

    // -----------------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------------

    private AppConfig? TryLoadConfig()
    {
        try { return ConfigStore.Load(_configPath); }
        catch (Exception ex)
        {
            UpdateStatus(SyncState.Error, $"Config: {ex.Message}");
            return null;
        }
    }

    private static StateDb OpenStateDb()
    {
        var stateDir = Environment.ExpandEnvironmentVariables(@"%PROGRAMDATA%\SamedisCare\SplSync\state");
        Directory.CreateDirectory(stateDir);
        return new StateDb(Path.Combine(stateDir, "state.sqlite"));
    }

    private static string TenantScratchDir(TenantConfig tenant)
    {
        var root = Environment.ExpandEnvironmentVariables(@"%PROGRAMDATA%\SamedisCare\SplSync\scratch");
        return Path.Combine(root, tenant.SamedisTenantId);
    }

    private static IActimedRepository BuildActimedRepository(AppConfig cfg)
    {
        var path = cfg.Actimed.DatabasePath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("actimed.database_path ist nicht gesetzt.");

        if (path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            return new SqliteActimedRepository(path);

        if (OperatingSystem.IsWindows())
            return new OleDbActimedRepository(path);

        throw new PlatformNotSupportedException(
            "Direkter Zugriff auf actimed3db.mdb erfordert Windows + Microsoft.ACE.OLEDB.16.0. " +
            "Auf Nicht-Windows-Hosts bitte einen .sqlite-Spiegel als actimed.database_path nutzen.");
    }

    private void UpdateStatus(SyncState state, string message)
    {
        _status.State = state;
        _status.Message = message;
        OnStatusChanged?.Invoke(_status);
    }

    private void PushError(string text)
    {
        // MainWindow.Refresh() liest die selbe Liste aus dem UI-Thread; lock auf der Liste
        // selbst, damit Add/RemoveAt nicht mit der Enumeration kollidiert.
        lock (_status.RecentErrors)
        {
            _status.RecentErrors.Add($"{DateTime.Now:HH:mm:ss} {text}");
            if (_status.RecentErrors.Count > 50) _status.RecentErrors.RemoveAt(0);
        }
    }

    private static async Task SafeDelay(TimeSpan span, CancellationToken token)
    {
        try { await Task.Delay(span, token); }
        catch (TaskCanceledException) { /* shutting down */ }
    }
}
