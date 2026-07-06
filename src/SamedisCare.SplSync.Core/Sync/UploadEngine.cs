using System.Text.RegularExpressions;
using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Api;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Actimed -> Samedis upload pipeline. Two independent trigger paths:
///
///   Mode 1 (PDF pickup): caller passes a path to a freshly-printed Actimed PDF.
///     - parse Prüfberichtsnummer from filename
///     - look it up in A3_FINISHED_TEST
///     - update Samedis issue + attach the PDF as data[document]
///
///   Mode 2 (PNG on completion): caller passes an ActimedFinishedTest (typically discovered
///     via DB polling).
///     - update Samedis issue
///     - render Werteprotokoll PNG locally and attach as data[image]
///
/// Both paths share UpdateOrCreateIssue. Either or both can be active at the same time;
/// duplicate uploads are harmless because Samedis stores each upload as a separate file.
/// </summary>
public class UploadEngine
{
    private readonly SyncContext _ctx;
    private readonly WerteprotokollRenderer _renderer;
    private readonly string _scratchDir;

    public UploadEngine(SyncContext ctx, string scratchDir)
    {
        _ctx = ctx;
        _renderer = new WerteprotokollRenderer(_ctx.Config.Branding);
        _scratchDir = scratchDir;
        Directory.CreateDirectory(_scratchDir);
    }

    // -----------------------------------------------------------------------------
    // Mode 2 — DB polling driver. Called periodically; finds new tests since the
    // last cursor and uploads them with PNG attachment.
    // -----------------------------------------------------------------------------

    public PhaseResult RunPngOnCompletion()
    {
        var phase = new PhaseResult("upload-png");
        if (!_ctx.Config.Sync.UploadFinishedIssues || !_ctx.Config.Sync.UploadModePngOnCompletion)
            return phase;

        var since = _ctx.Cursor.Get(_ctx.Tenant.SamedisTenantId, Cursor.KindUpload).UtcDateTime;
        var tests = _ctx.Actimed.GetFinishedTestsSince(since, _ctx.Tenant.ActimedCustIds);

        // Wir tracken zwei Zeitstempel:
        //   maxSuccess  = bis hierher haben wir alles erfolgreich verarbeitet (auch "schon done" gilt
        //                 als erfolgreich — der Test wurde betrachtet und Anhang ggf. ergaenzt).
        //   firstFail   = ab hier hatten wir einen ECHTEN Fehler (Network-Issue, Auth, 5xx).
        // Cursor rueckt am Ende auf maxSuccess vor — solange dieser strikt < firstFail ist, sind
        // wir safe. Damit kein Endlos-Retry auf einem Test mehr, dessen Issue bereits "done" ist
        // (siehe Race Mode 1 vs Mode 2 — PDF-Pickup hat schon done gesetzt, PNG-Polling stand
        // sonst ewig auf demselben Test, weil phase.Errors > 0 den Cursor blockiert hat).
        DateTime? maxSuccess = null;
        DateTime? firstFail = null;
        foreach (var test in tests)
        {
            try
            {
                ProcessTest(test, attachPng: true, attachPdfPath: null);
                phase.Applied++;
                if (maxSuccess == null || test.ModifyTime > maxSuccess) maxSuccess = test.ModifyTime;
            }
            catch (Exception ex)
            {
                phase.Errors.Add($"test {test.TestId}: {ex.Message}");
                phase.Skipped++;
                if (firstFail == null) firstFail = test.ModifyTime;
            }
        }

        // Cursor: beim ersten Fehler stehen bleiben, davor advancen. Wenn alles ok, voll vorruecken.
        DateTime? advanceTo = firstFail.HasValue
            ? (maxSuccess.HasValue && maxSuccess < firstFail ? maxSuccess : (DateTime?)null)
            : maxSuccess;
        if (advanceTo.HasValue)
            _ctx.Cursor.Set(_ctx.Tenant.SamedisTenantId, Cursor.KindUpload,
                new DateTimeOffset(advanceTo.Value, TimeSpan.Zero));

        return phase;
    }

    // -----------------------------------------------------------------------------
    // Mode 1 — PDF pickup driver. Called by PdfPickupWatcher when a new file lands
    // in protocol_pdf_dir. The filename must contain the Prüfberichtsnummer.
    // -----------------------------------------------------------------------------

    private static readonly Regex PruefberichtsnummerPattern =
        new Regex(@"\d{12,}", RegexOptions.Compiled); // Actimed format e.g. 202312181612401313

    public bool RunPdfPickup(string pdfPath)
    {
        if (!_ctx.Config.Sync.UploadFinishedIssues || !_ctx.Config.Sync.UploadModePdfPickup)
            return false;
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath)) return false;

        var fileName = Path.GetFileNameWithoutExtension(pdfPath);
        var match = PruefberichtsnummerPattern.Match(fileName);
        if (!match.Success)
        {
            _ctx.Log.Warn($"PDF '{Path.GetFileName(pdfPath)}': could not extract Prüfberichtsnummer from filename.");
            return false;
        }

        var pruefberichtsnr = match.Value;

        // Find the corresponding test in Actimed. We do not have a direct "by report-number"
        // lookup, so we scan recent tests. Our scratchDir holds nothing here — pull from DB.
        var sinceFallback = DateTime.UtcNow.AddDays(-30); // safety window
        var candidates = _ctx.Actimed.GetFinishedTestsSince(sinceFallback, _ctx.Tenant.ActimedCustIds);
        var test = candidates.FirstOrDefault(t =>
            string.Equals(t.Pruefberichtsnummer?.Trim(), pruefberichtsnr, StringComparison.Ordinal));
        if (test == null)
        {
            _ctx.Log.Warn($"PDF '{Path.GetFileName(pdfPath)}' has Prüfberichtsnr={pruefberichtsnr} but no matching A3_FINISHED_TEST yet.");
            return false;
        }

        try
        {
            ProcessTest(test, attachPng: false, attachPdfPath: pdfPath);
            _ctx.Log.Info($"PDF pickup: uploaded for Prüfberichtsnr={pruefberichtsnr}");
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Log.Error($"PDF pickup failed for {pruefberichtsnr}: {ex.Message}", ex);
            return false;
        }
    }

    // -----------------------------------------------------------------------------
    // Shared core: ensure issue is updated, optionally attach PNG, optionally attach PDF.
    // -----------------------------------------------------------------------------

    private void ProcessTest(ActimedFinishedTest test, bool attachPng, string? attachPdfPath)
    {
        // PDF-Pickup ist ein expliziter User-Akt: der Techniker hat das Pruefprotokoll gedruckt
        // und damit klar dokumentiert "diese Pruefung gehoert in Samedis". Wenn kein vorhandenes
        // Issue gefunden wird, legen wir es deshalb auch ohne create_issues_from_actimed an —
        // sonst wuerde die Pruefung verloren gehen, was schlechter ist als ein "ungeplantes"
        // Issue in Samedis.
        var pdfPickup = !string.IsNullOrEmpty(attachPdfPath);
        string? issueId = ResolveIssueId(test);
        var newlyCreated = false;

        if (string.IsNullOrEmpty(issueId))
        {
            issueId = CreateIfAllowed(test, force: pdfPickup);
            newlyCreated = !string.IsNullOrEmpty(issueId);
        }

        if (string.IsNullOrEmpty(issueId))
            throw new InvalidOperationException(
                $"No Samedis issue for Prüfberichtsnr={test.Pruefberichtsnummer} " +
                $"(create_issues_from_actimed=false, kein PDF-Pickup).");

        // KEIN Update wenn wir das Issue gerade erst angelegt haben — der POST traegt schon
        // alle Felder + status=done. Server lehnt PUT auf "done" ab mit
        // "Tasks which are set as done cannot be edited anymore."
        // Genauso skippen wir den Update, wenn das Issue bereits done ist (z. B. durch einen
        // vorhergehenden PDF-Pickup) — dann nur Anhang nachreichen.
        if (!newlyCreated)
            UpdateIssueIfNotDone(issueId, test);

        if (attachPng)
            UploadWerteprotokollPng(issueId, test);

        if (!string.IsNullOrEmpty(attachPdfPath))
            UploadActimedPdf(issueId, test, attachPdfPath!);

        // Optional: geplante Folgemaßnahme in Samedis anlegen (Prüfdatum + Intervall).
        if (_ctx.Config.Sync.CreatePlannedIssueAfterCompletion)
            CreatePlannedFollowup(test);
    }

    /// <summary>
    /// Legt nach Abschluss einer Prüfung die geplante Folgemaßnahme in Samedis an:
    /// neues maintenance-Issue mit status=_new und due_on = Prüfdatum + Intervall (Monate).
    /// Idempotent über <see cref="PlannedFollowupStore"/> (verhindert Duplikate beim 30-s-Polling
    /// und im Dual-Mode). Ein Fehler hier wird nur geloggt, nicht geworfen — der eigentliche
    /// Abschluss + Anhang soll dadurch nicht zurückgerollt werden.
    /// </summary>
    private void CreatePlannedFollowup(ActimedFinishedTest test)
    {
        var followups = new PlannedFollowupStore(_ctx.State);
        if (followups.Exists(_ctx.Tenant.SamedisTenantId, test.TestId))
            return; // schon angelegt

        var months = ResolveIntervalMonths(test);
        if (months is not int m || m <= 0)
        {
            _ctx.Log.Warn(
                $"Folgemaßnahme für Test {test.TestId} übersprungen: kein Prüfintervall ermittelbar " +
                $"(weder A3_ACTIVITY noch NEXT_TEST_DATE).");
            return;
        }

        var dueOn = test.TestDate.AddMonths(m);

        try
        {
            var attrs = BuildFollowupAttributes(test, dueOn, m);
            var resource = $"{_ctx.TenantScope}/issues";
            var response = _ctx.Samedis.Post(resource, Issues.BuildEnvelope(attrs));
            if (_ctx.Samedis.StatusCode is < 200 or >= 300)
            {
                _ctx.Log.Warn(DownloadEngine.FormatHttpError("POST", resource, _ctx.Samedis.StatusCode, _ctx.Samedis.LastError, response));
                return;
            }

            var newId = Helper.ExtractDataId(response);
            followups.Record(_ctx.Tenant.SamedisTenantId, test.TestId, newId);
            _ctx.Log.Info(
                $"Geplante Folgemaßnahme angelegt: Issue {newId}, inventar={test.DevId}, " +
                $"due_on={dueOn:yyyy-MM-dd} (Prüfdatum {test.TestDate:yyyy-MM-dd} + {m} Monate).");
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"Folgemaßnahme für Test {test.TestId} fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>
    /// Payload für die geplante Folgemaßnahme (neues, offenes maintenance-Issue). Bewusst OHNE
    /// external_id/done_at/test_result — die trägt erst die tatsächlich durchgeführte Prüfung.
    /// </summary>
    private Dictionary<string, object> BuildFollowupAttributes(ActimedFinishedTest test, DateTime dueOn, int months)
    {
        var device = _ctx.Actimed.GetDeviceById(test.DevId);
        var inventoryDeviceNumber = device?.InventoryNo ?? "";
        var title = string.IsNullOrWhiteSpace(test.PvsName) ? "Wartung/Pruefung" : test.PvsName!;

        var dict = new Dictionary<string, object>
        {
            ["issue_type"]       = "maintenance",
            ["task_type"]        = "maintenance",
            ["maintenance_type"] = "maintenance",
            ["status"]           = "_new",
            ["title"]            = title,
            ["services"]         = new[] { title },
            ["due_on"]           = dueOn.ToString("yyyy-MM-dd"),
            ["auto_create_test_protocol"] = false
        };

        if (!string.IsNullOrEmpty(inventoryDeviceNumber))
        {
            dict["inventory_device_number"] = inventoryDeviceNumber;
            var invId = ResolveInventoryId(inventoryDeviceNumber);
            if (!string.IsNullOrEmpty(invId)) dict["inventory_id"] = invId;
        }

        var intervals = BuildServiceIntervals(months, title);
        if (intervals != null) dict["with_service_intervals"] = intervals;

        return dict;
    }

    /// <summary>
    /// Legt ein neues Issue in Samedis an. <paramref name="force"/> umgeht das
    /// <c>CreateIssuesFromActimed</c>-Flag — wird vom PDF-Pickup-Pfad verwendet, weil der Druck
    /// des Prueprotokolls eine eindeutige User-Aktion ist.
    /// </summary>
    private string? CreateIfAllowed(ActimedFinishedTest test, bool force = false)
    {
        if (!force && !_ctx.Config.Sync.CreateIssuesFromActimed) return null;
        var attrs = BuildAttributes(test);
        attrs["status"] = "done";
        var resource = $"{_ctx.TenantScope}/issues";
        var response = _ctx.Samedis.Post(resource, Issues.BuildEnvelope(attrs));
        if (_ctx.Samedis.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException(
                DownloadEngine.FormatHttpError("POST", resource, _ctx.Samedis.StatusCode, _ctx.Samedis.LastError, response));
        var newId = Helper.ExtractDataId(response);
        if (!string.IsNullOrEmpty(newId))
            _ctx.Log.Info($"PDF-Pickup: neues Samedis-Issue {newId} fuer Pruefberichtsnr={test.Pruefberichtsnummer} angelegt.");
        return newId;
    }

    /// <summary>
    /// PUT auf das Issue. Wenn der Server mit dem 400-"Tasks which are set as done cannot be
    /// edited anymore" antwortet, schlucken wir das stillschweigend — typischerweise hat der
    /// PDF-Pickup-Pfad das Issue schon vorher angelegt+done gesetzt, und ein anschliessender
    /// Mode-2-Polling-Lauf will denselben Test nochmal updaten. Anhang darf trotzdem rein,
    /// also kein Throw — der Caller macht mit UploadWerteprotokollPng/UploadActimedPdf weiter.
    /// </summary>
    private void UpdateIssueIfNotDone(string issueId, ActimedFinishedTest test)
    {
        var attrs = BuildAttributes(test);
        attrs["status"] = "done";
        var body = Issues.BuildEnvelope(attrs);
        var resource = $"{_ctx.TenantScope}/issues";
        var response = _ctx.Samedis.Put(resource, issueId, body);
        var status = _ctx.Samedis.StatusCode;
        if (status is >= 200 and < 300) return;

        // 400 + "set as done" ist kein echter Fehler — Issue ist bereits abgeschlossen,
        // wir machen einfach mit dem Anhang weiter. Andere 4xx/5xx werden geworfen.
        if (status == 400 && !string.IsNullOrEmpty(response) &&
            response.Contains("set as done", StringComparison.OrdinalIgnoreCase))
        {
            _ctx.Log.Info($"Issue {issueId} ist bereits 'done' — kein Update, Anhang wird trotzdem hochgeladen.");
            return;
        }

        throw new InvalidOperationException(
            DownloadEngine.FormatHttpError("PUT", $"{resource}/{issueId}", status, _ctx.Samedis.LastError, response));
    }

    private string? ResolveIssueId(ActimedFinishedTest test)
    {
        // Primary: the issue_link table populated by DownloadEngine. Wir suchen nur ueber
        // DevId — die ActivityId aus A3_FINISHED_TEST kennen wir nicht zuverlaessig (Actimed
        // verbraucht die A3_IS_ACT_DEV-Planung nicht zwingend beim Speichern), und im
        // Normalfall hat ein Geraet ohnehin nur ein offenes Maintenance-Issue.
        var link = _ctx.IssueLinks.FindByDev(_ctx.Tenant.SamedisTenantId, test.DevId);
        if (link != null && !string.IsNullOrEmpty(link.SamedisIssueId))
            return link.SamedisIssueId;

        if (string.IsNullOrWhiteSpace(test.Pruefberichtsnummer)) return null;

        // Fallback: search by external_id == Prüfberichtsnummer.
        // Gleiche URL-Konvention wie der Download-Pfad. Kein archive-Filter — wenn ein Issue
        // schon archiviert wurde, wollen wir es trotzdem updaten koennen.
        var fb = new FilterBuilder();
        fb.Add("external_id", FilterBuilder.FilterType.Equals, FilterBuilder.Type.Text, test.Pruefberichtsnummer);
        var resource = $"{_ctx.TenantScope}/issues" +
                       $"?page[number]=1&page[limit]=1" +
                       $"&quickfilter=&gridfilter={fb.Get()}";
        var response = _ctx.Samedis.Get(resource);
        if (_ctx.Samedis.StatusCode is < 200 or >= 300) return null;
        return Helper.ExtractDataId(response);
    }

    /// <summary>
    /// Cache der inventory_id-Lookups pro UploadEngine-Instanz. Ohne Cache wuerden wir bei einer
    /// Folge von Pruefungen am selben Geraet jedes Mal einen API-Call zum Resolver machen.
    /// </summary>
    private readonly Dictionary<string, string?> _inventoryIdCache = new();

    /// <summary>
    /// Baut das with_service_intervals-Array für den Upload (siehe samedis-public.yaml): ein Eintrag
    /// mit category=maintenance, dem Service-Label und dem Prüfintervall in Monaten. Actimed führt
    /// das Intervall nur in Monaten, daher unit="month". Liefert null, wenn kein Intervall bekannt
    /// ist — dann überträgt der Upload kein Intervall (statt eine falsche 0 zu setzen).
    /// </summary>
    public static IReadOnlyList<Dictionary<string, object>>? BuildServiceIntervals(
        int? months, string serviceLabel)
    {
        if (months is not int m || m <= 0) return null;

        return new[]
        {
            new Dictionary<string, object>
            {
                ["category"] = "maintenance",
                ["label"]    = serviceLabel,
                ["value"]    = m,
                ["unit"]     = "month",
            }
        };
    }

    /// <summary>
    /// Ermittelt das Prüfintervall (Monate) für einen abgeschlossenen Test. Primär aus
    /// A3_ACTIVITY.ACTIVITY_INTERVAL der zugehörigen Tätigkeit (über die issue_link-Brücke,
    /// DEV_ID → ACTIVITY_ID) — dieser Wert stammt ursprünglich aus Samedis und ist zuverlässig.
    /// Fallback: aus TEST_DATE → NEXT_TEST_DATE ableiten (nur wenn NEXT_TEST_DATE gesetzt ist).
    /// Liefert null, wenn keine Quelle greift.
    /// </summary>
    private int? ResolveIntervalMonths(ActimedFinishedTest test)
    {
        var link = _ctx.IssueLinks.FindByDev(_ctx.Tenant.SamedisTenantId, test.DevId);
        if (link is { ActimedActivityId: > 0 })
        {
            var activity = _ctx.Actimed.GetActivityById(link.ActimedActivityId);
            if (activity is { IntervalMonths: > 0 })
                return activity.IntervalMonths;
        }

        if (test.NextTestDate is DateTime next)
        {
            var months = IntervalConversion.MonthsBetween(test.TestDate, next);
            if (months > 0) return months;
        }

        return null;
    }

    private Dictionary<string, object> BuildAttributes(ActimedFinishedTest test)
    {
        var device = _ctx.Actimed.GetDeviceById(test.DevId);
        var inventoryDeviceNumber = device?.InventoryNo ?? "";

        // POST /issues braucht laut Server ZWINGEND: inventory_id, task_type, due_on, title.
        // PUT auf existierendes Issue ist toleranter — fehlt eines, bleibt der bisherige Wert.
        // Wir setzen bei beiden alles, dann passt's in jedem Fall.
        var title = string.IsNullOrWhiteSpace(test.PvsName) ? "Wartung/Pruefung" : test.PvsName!;
        var performer = string.IsNullOrWhiteSpace(test.TesterName)
            ? _ctx.Config.Actimed.DefaultTesterName
            : test.TesterName;
        var dateIso = test.TestDate.ToString("yyyy-MM-dd");

        var dict = new Dictionary<string, object>
        {
            // Issue-Klassifizierung — beide Aliase setzen, weil je nach Backend-Version mal das
            // eine, mal das andere als pflicht-required-Feld validiert wird.
            ["issue_type"]       = "maintenance",
            ["task_type"]        = "maintenance",
            ["maintenance_type"] = "maintenance",

            ["external_id"]   = test.Pruefberichtsnummer ?? "",
            ["title"]         = title,
            ["services"]      = new[] { title },
            ["date"]          = dateIso,
            ["done_at"]       = dateIso,
            ["due_on"]        = dateIso,                  // Server-required bei POST
            ["responsible_name"]    = performer,
            ["maintenance_performer"] = performer,

            // Verhindert, dass Samedis sein eigenes Pruefprotokoll generiert — wir liefern ja
            // unser eigenes (PNG-Wertenachweis oder Actimed-PDF) direkt als Anhang.
            ["auto_create_test_protocol"] = false
        };

        // Inventory-Resolver: Server akzeptiert beim POST nur inventory_id (ObjectId). Wir
        // resolven device_number -> inventory_id ueber einen API-Call, gecacht pro UploadEngine.
        if (!string.IsNullOrEmpty(inventoryDeviceNumber))
        {
            dict["inventory_device_number"] = inventoryDeviceNumber;
            var invId = ResolveInventoryId(inventoryDeviceNumber);
            if (!string.IsNullOrEmpty(invId)) dict["inventory_id"] = invId;
        }

        var testResult = ResultMapping.FromActimed(test.Pruefergebnis);
        if (!string.IsNullOrEmpty(testResult))
        {
            dict["test_result"] = testResult;
            // Boolean-Variante fuer Backends, die maintenance_passed validieren.
            dict["maintenance_passed"] = testResult == "passed" || testResult == "passed_conditionally";
        }
        if (!string.IsNullOrWhiteSpace(test.Memo)) dict["test_comment"] = test.Memo!;

        // Prüfintervall aus Actimed mitliefern, sonst legt Samedis die Wartungsart ohne Intervall
        // ("Unbekannt") an. Primär aus A3_ACTIVITY.ACTIVITY_INTERVAL (Samedis-Ursprung), sonst
        // aus den Test-Daten abgeleitet.
        var intervals = BuildServiceIntervals(ResolveIntervalMonths(test), title);
        if (intervals != null) dict["with_service_intervals"] = intervals;

        // inventory_operation_status ist beim CREATE pflicht-required (Server-Validator).
        // Erlaubt: active | limited_use | out_of_order | retired | decommissioned.
        // Default = active (Geraet ist nach erfolgreicher Pruefung weiter einsatzfaehig).
        // Bei not_passed + Config-Flag: limited_use (Geraet darf nur eingeschraenkt verwendet werden).
        var operationStatus = "active";
        if (_ctx.Config.Sync.SetInventoryOperationStatusOnFailedMaintenance && testResult == "not_passed")
            operationStatus = "limited_use";
        dict["inventory_operation_status"] = operationStatus;

        return dict;
    }

    /// <summary>
    /// Loest device_number → inventory_id (ObjectId) per gridfilter-Suche auf
    /// /tenants/{id}/inventories?gridfilter[device_number][equals]=&lt;nr&gt;.
    /// Ergebnis wird gecacht.
    /// </summary>
    private string? ResolveInventoryId(string deviceNumber)
    {
        if (_inventoryIdCache.TryGetValue(deviceNumber, out var cached))
            return cached;

        var fb = new FilterBuilder();
        fb.Add("device_number", FilterBuilder.FilterType.Equals, FilterBuilder.Type.Text, deviceNumber);
        var resource = $"{_ctx.TenantScope}/inventories" +
                       $"?page[number]=1&page[limit]=1" +
                       $"&quickfilter=&gridfilter={fb.Get()}";
        var response = _ctx.Samedis.Get(resource);
        string? id = null;
        if (_ctx.Samedis.StatusCode is >= 200 and < 300)
            id = Helper.ExtractDataId(response);
        _inventoryIdCache[deviceNumber] = id;
        if (string.IsNullOrEmpty(id))
            _ctx.Log.Warn($"Konnte inventory_id fuer device_number='{deviceNumber}' nicht aufloesen.");
        return id;
    }

    // -----------------------------------------------------------------------------
    // Attachment helpers
    // -----------------------------------------------------------------------------

    private void UploadWerteprotokollPng(string issueId, ActimedFinishedTest test)
    {
        var items = _ctx.Actimed.GetTestItemsForTest(test.TestId);
        var results = _ctx.Actimed.GetTestResultsForTest(test.TestId);
        var device = _ctx.Actimed.GetDeviceById(test.DevId);

        var data = new WerteprotokollRenderer.TestData
        {
            Header = test,
            Items = items,
            Results = results,
            DeviceLabel = device != null ? $"{device.InventoryNo} ({device.SerialNo})" : null
        };

        var fileName = SafeFile($"{test.Pruefberichtsnummer}_werteprotokoll.png");

        // Bevorzugt im Reports-Ordner ablegen (parallel zu den PDFs aus dem Pickup-Pfad), damit
        // der Techniker beide Anhaenge beieinander hat. Fallback: scratchDir, wenn protocol_pdf_dir
        // nicht konfiguriert oder noch nicht angelegt ist.
        var preferredDir = _ctx.Config.Actimed.ProtocolPdfDir;
        var targetDir = !string.IsNullOrWhiteSpace(preferredDir)
            ? preferredDir
            : _scratchDir;
        try { Directory.CreateDirectory(targetDir); }
        catch { targetDir = _scratchDir; Directory.CreateDirectory(targetDir); }

        var path = Path.Combine(targetDir, fileName);
        _renderer.RenderToFile(data, path);

        AssertReadable(path, "PNG-Werteprotokoll");
        _ctx.Log.Info($"PNG-Upload: '{path}' ({new FileInfo(path).Length} bytes) -> Issue {issueId}");

        var uploadUrl = $"{_ctx.TenantScope}/issues/{issueId}/uploads";
        var response = _ctx.Samedis.PostIssueImage(uploadUrl, path, fileName);
        if (_ctx.Samedis.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException(
                DownloadEngine.FormatHttpError("POST", uploadUrl, _ctx.Samedis.StatusCode, _ctx.Samedis.LastError, response));

        // Erfolgreich hochgeladen → ins gleiche processed/<datum>/-Archiv verschieben wie der
        // PdfPickupWatcher das fuer PDFs macht. So bleibt der Reports-Ordner sauber.
        ArchiveUploadedFile(targetDir, path);
    }

    private void UploadActimedPdf(string issueId, ActimedFinishedTest test, string pdfPath)
    {
        AssertReadable(pdfPath, "Actimed-PDF");
        _ctx.Log.Info($"PDF-Upload: '{pdfPath}' ({new FileInfo(pdfPath).Length} bytes) -> Issue {issueId}");

        var fileName = SafeFile($"{test.Pruefberichtsnummer}.pdf");
        var uploadUrl = $"{_ctx.TenantScope}/issues/{issueId}/uploads";
        var response = _ctx.Samedis.PostIssueDocument(uploadUrl, pdfPath, fileName);
        if (_ctx.Samedis.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException(
                DownloadEngine.FormatHttpError("POST", uploadUrl, _ctx.Samedis.StatusCode, _ctx.Samedis.LastError, response));
    }

    /// <summary>
    /// Prueft vor dem Multipart-Upload, dass die Datei tatsaechlich existiert und nicht leer ist.
    /// Sonst kommt eine kryptische "File cannot be blank"-400 vom Server, ohne dass der User
    /// versteht warum — der eigentliche Bug liegt z. B. an einem fehlgeschlagenen Render
    /// (PNG mit 0 Bytes), Permission-Problem oder File-Lock.
    /// </summary>
    private static void AssertReadable(string path, string label)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{label}: Datei nicht gefunden: '{path}'.");
        var len = new FileInfo(path).Length;
        if (len <= 0)
            throw new InvalidOperationException($"{label}: Datei ist leer (0 Bytes): '{path}'.");
        // Lese-Test um File-Lock auszuschliessen (Drucker schreibt noch / Antivirus blockt).
        try
        {
            using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[Math.Min(1024, len)];
            _ = s.Read(buf, 0, buf.Length);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"{label}: Datei '{path}' kann nicht geoeffnet werden ({ex.Message}). " +
                $"Moeglicherweise noch geschrieben oder durch Antivirus blockiert.");
        }
    }

    private static string SafeFile(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    /// <summary>
    /// Verschiebt die erfolgreich hochgeladene Datei in einen Tages-Subfolder unter
    /// <paramref name="baseDir"/>/processed/&lt;yyyy-MM-dd&gt;/. Sinn: Reports-Ordner bleibt
    /// sauber (zeigt nur "noch zu tun"), Archiv bleibt auditierbar.
    /// Identische Logik wie PdfPickupWatcher.ArchivePdf — bewusst dupliziert, damit beide
    /// Pfade unabhaengig voneinander funktionieren und sich nicht gegenseitig zerschiessen.
    /// </summary>
    private void ArchiveUploadedFile(string baseDir, string filePath)
    {
        try
        {
            var archiveDir = Path.Combine(baseDir, "processed", DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(archiveDir);

            var name = Path.GetFileName(filePath);
            var target = Path.Combine(archiveDir, name);
            // Konflikt-Behandlung: <name>_<n>.<ext>
            var counter = 1;
            while (File.Exists(target))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                target = Path.Combine(archiveDir, $"{stem}_{counter}{ext}");
                counter++;
            }
            File.Move(filePath, target);
            _ctx.Log.Info($"Archiviert nach '{Path.GetRelativePath(baseDir, target)}'.");
        }
        catch (Exception ex)
        {
            // Best-Effort: wenn das Move fehlschlaegt (Permission, Lock), bleibt die Datei liegen.
            // Beim naechsten Tick wuerde sie noch einmal hochgeladen — Samedis ignoriert das
            // dank external_id idempotent, kein Datenverlust.
            _ctx.Log.Warn($"Konnte '{Path.GetFileName(filePath)}' nicht archivieren: {ex.Message}");
        }
    }
}
