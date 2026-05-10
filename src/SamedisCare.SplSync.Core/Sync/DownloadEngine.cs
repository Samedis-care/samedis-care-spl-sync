using Newtonsoft.Json;
using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Api;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Samedis → Actimed: pulls inventory core data and open maintenance issues for one tenant,
/// writes them into Actimed via IActimedRepository. Implements 5.2 + 5.3 of CLAUDE.md.
///
/// Each cycle is idempotent — re-running from the same cursor must not produce duplicates.
/// </summary>
public class DownloadEngine
{
    private readonly SyncContext _ctx;

    public DownloadEngine(SyncContext ctx) => _ctx = ctx;

    public DownloadResult Run()
    {
        var result = new DownloadResult();
        if (_ctx.Config.Sync.DownloadInventories)
            result.Inventories = DownloadInventories();
        if (_ctx.Config.Sync.DownloadOpenIssues)
            result.Issues = DownloadIssues();

        // Advance cursor only if both phases succeeded (or were skipped).
        if (result.IsSuccess)
            _ctx.Cursor.Set(_ctx.Tenant.SamedisTenantId, Cursor.KindDownload, DateTimeOffset.UtcNow);

        return result;
    }

    // ---------- 5.2 ----------

    private PhaseResult DownloadInventories()
    {
        var phase = new PhaseResult("inventories");
        var page = 1;
        const int pageLimit = 200;

        while (true)
        {
            // Samedis-Gridfilter date ist tagesgenau (yyyy-MM-dd). 'greaterThan' schliesst den
            // Cursor-Tag selbst aus — ein heute angelegtes Issue hätte updated_at vom heutigen
            // Tag und würde nie gefunden, weil der letzte Sync den Cursor bereits auf heute
            // gesetzt hat. 24h-Overlap heilt das, der Sync ist dank issue_link/external_id
            // idempotent. Heisst: wir ziehen die letzten 24h+ doppelt, schadet nicht.
            var since = _ctx.Cursor.Get(_ctx.Tenant.SamedisTenantId, Cursor.KindDownload).AddDays(-1);
            var fb = new FilterBuilder();
            fb.Add("updated_at", FilterBuilder.FilterType.GreaterThan, FilterBuilder.Type.Date, since);
            // URL-Schema laut samedis-care Webview:
            //   /tenants/{id}/inventories?page[..]&quickfilter=&gridfilter={...}
            //                            &filter[variant]=regular&filter[status]=active
            var resource = $"{_ctx.TenantScope}/inventories" +
                           $"?page[number]={page}&page[limit]={pageLimit}" +
                           $"&quickfilter=&gridfilter={fb.Get()}" +
                           $"&filter[variant]=regular" +
                           $"&filter[status]=active";

            var response = _ctx.Samedis.Get(resource);
            if (_ctx.Samedis.StatusCode is < 200 or >= 300)
            {
                phase.Errors.Add(FormatHttpError("GET", resource, _ctx.Samedis.StatusCode, _ctx.Samedis.LastError, response));
                return phase;
            }

            var root = JsonConvert.DeserializeObject<Inventories.Root>(response);
            var batch = root?.Data ?? new List<Inventories.Data>();
            if (batch.Count == 0) break;

            foreach (var item in batch)
            {
                try
                {
                    var outcome = ApplyInventory(item);
                    phase.Applied++;
                    if (outcome == InventoryOutcome.Created)  phase.Created++;
                    if (outcome == InventoryOutcome.Repaired) phase.Repaired++;
                }
                catch (ActimedLockedException)
                {
                    phase.Errors.Add("Actimed locked while writing inventory — sync paused.");
                    throw; // bubble up to SyncWorker so the tray can prompt
                }
                catch (Exception ex)
                {
                    phase.Errors.Add($"inventory {item.Id}: {ex.Message}");
                    phase.Skipped++;
                }
            }

            if (batch.Count < pageLimit) break;
            page++;
        }

        return phase;
    }

    /// <summary>Outcome of one ApplyInventory pass.</summary>
    private enum InventoryOutcome
    {
        /// <summary>Device existed, FKs were healthy → nothing changed.</summary>
        Existed,
        /// <summary>Device did not exist → a new A3_DEV row was inserted.</summary>
        Created,
        /// <summary>Device existed but had FK=0/NULL → RepairDeviceForeignKeys patched it.</summary>
        Repaired
    }

    private InventoryOutcome ApplyInventory(Inventories.Data item)
    {
        var attr = item.Attributes ?? throw new InvalidOperationException("inventory has no attributes");
        var inventoryNo = attr.DeviceNumber ?? attr.InventoryNumber;
        if (string.IsNullOrWhiteSpace(inventoryNo))
        {
            _ctx.Log.Debug($"Inventory {item.Id} has no device_number — skipping");
            return InventoryOutcome.Existed;
        }

        var custIds = _ctx.Tenant.ActimedCustIds;
        if (custIds.Count == 0)
            throw new InvalidOperationException($"Tenant {_ctx.Tenant.Name} has no actimed_cust_ids in config");

        // Defensiv: leere Strings auf "Unbekannt" mappen, damit ACE keine "Data type mismatch"-
        // Fehlermeldungen bei WHERE col = '' wirft.
        static string Sanitize(string? raw) =>
            string.IsNullOrWhiteSpace(raw) ? "Unbekannt" : raw.Trim();

        // 1) Manufacturer
        // Reihenfolge: was auf dem Typenschild steht hat Vorrang vor "current responsible
        // manufacturer" (das kann durch Mergers/Marken-Wechsel abweichen). Beides leer -> "Unbekannt".
        var manuName = Sanitize(attr.DeviceModelManufacturerAccordingToTypePlate
                              ?? attr.DeviceModelCurrentResponsibleManufacturer);
        ActimedManufacturer manu;
        try
        {
            manu = _ctx.Actimed.FindManufacturerByName(manuName)
                   ?? new ActimedManufacturer(0, manuName, null);
            if (manu.ManuId == 0)
                manu = manu with { ManuId = _ctx.Actimed.InsertManufacturer(manu) };
        }
        catch (Exception ex) when (ex is not ActimedLockedException)
        {
            throw new InvalidOperationException(
                $"Manufacturer-Lookup/Insert fehlgeschlagen fuer name='{manuName}': {ex.Message}", ex);
        }

        // 2) Device kind ("Geräteart")
        var kindName = Sanitize(attr.DeviceTypeTitle);
        ActimedDeviceKind kind;
        try
        {
            kind = _ctx.Actimed.FindDeviceKindByName(kindName)
                   ?? new ActimedDeviceKind(0, kindName, null);
            if (kind.DevKindId == 0)
                kind = kind with { DevKindId = _ctx.Actimed.InsertDeviceKind(kind) };
        }
        catch (Exception ex) when (ex is not ActimedLockedException)
        {
            throw new InvalidOperationException(
                $"DeviceKind-Lookup/Insert fehlgeschlagen fuer name='{kindName}': {ex.Message}", ex);
        }

        // 3) Device type (model)
        var modelName = Sanitize(attr.DeviceModelTitle);
        var typeName = modelName;
        ActimedDeviceType type;
        try
        {
            type = _ctx.Actimed.FindDeviceTypeByName(typeName)
                   ?? new ActimedDeviceType(0, manu.ManuId, kind.DevKindId, typeName, modelName);
            if (type.DevTypeId == 0)
                type = type with { DevTypeId = _ctx.Actimed.InsertDeviceType(type) };
        }
        catch (Exception ex) when (ex is not ActimedLockedException)
        {
            throw new InvalidOperationException(
                $"DeviceType-Lookup/Insert fehlgeschlagen fuer name='{typeName}': {ex.Message}", ex);
        }

        // 4) Device itself
        var existing = _ctx.Actimed.FindDeviceByInventoryNo(inventoryNo, custIds);
        if (existing == null)
        {
            var primaryCust = custIds[0];
            var device = new ActimedDevice(
                DevId: 0,
                CustId: primaryCust,
                InventoryNo: inventoryNo,
                SerialNo: attr.SerialNumber ?? "",
                DevTypeId: type.DevTypeId,
                LocationId: null,
                StatusId: null,
                NextActivityId: null,
                Memo: $"created_by=spl-sync; samedis_inventory_id={item.Id}");
            _ctx.Actimed.InsertDevice(device);
            return InventoryOutcome.Created;
        }

        // Auto-Repair: aelterere Builds des Sync-Tools haben A3_DEV ohne FK-Defaults geschrieben.
        // Solche Datensaetze sind in der Actimed-UI unsichtbar (Standardfilter wirft sie raus, weil
        // LOCATION_ID=0/STATUS_ID=0 auf nicht existente Stammdaten zeigen). Wir patchen das hier
        // beim nächsten Sync-Lauf still nach. RepairDeviceForeignKeys ändert NUR die kaputten
        // Spalten; manuell vom Techniker gesetzte Werte bleiben unangetastet.
        var repaired = _ctx.Actimed.RepairDeviceForeignKeys(existing.DevId);

        // TODO: optional UPDATE-Pfad für geänderte SamedisModel/Manufacturer-Werte
        return repaired ? InventoryOutcome.Repaired : InventoryOutcome.Existed;
    }

    // ---------- 5.3 ----------

    private PhaseResult DownloadIssues()
    {
        var phase = new PhaseResult("issues");
        var page = 1;
        const int pageLimit = 200;

        while (true)
        {
            // 24h-Overlap am Cursor — siehe Begruendung in DownloadInventories.
            var since = _ctx.Cursor.Get(_ctx.Tenant.SamedisTenantId, Cursor.KindDownload).AddDays(-1);
            var fb = new FilterBuilder();
            fb.Add("updated_at", FilterBuilder.FilterType.GreaterThan, FilterBuilder.Type.Date, since);

            // URL-Schema laut public.yaml (v4_tenant_issues, action=index):
            //   /tenants/{id}/issues?page[..]&quickfilter=&gridfilter={...}
            //                       &filter[status]=not_done&filter[issue_type]=maintenance
            //
            // 'not_done' ist ein Sammelstatus, der alles ausser 'done' liefert.
            // Hinweis: filter[archive] ist KEIN Boolean — gültige Werte laut Spec sind nur ''
            // (default: nur die letzten 24 Monate) und 'true' (auch aeltere). Wir lassen den
            // Parameter weg und nehmen den 24-Monats-Default; das passt zu unserem Use-Case.
            var resource = $"{_ctx.TenantScope}/issues" +
                           $"?page[number]={page}&page[limit]={pageLimit}" +
                           $"&quickfilter=&gridfilter={fb.Get()}" +
                           $"&filter[status]=not_done" +
                           $"&filter[issue_type]=maintenance";

            var response = _ctx.Samedis.Get(resource);
            if (_ctx.Samedis.StatusCode is < 200 or >= 300)
            {
                phase.Errors.Add(FormatHttpError("GET", resource, _ctx.Samedis.StatusCode, _ctx.Samedis.LastError, response));
                return phase;
            }

            var root = JsonConvert.DeserializeObject<Issues.Root>(response);
            var batch = root?.Data ?? new List<Issues.Data>();
            if (batch.Count == 0) break;

            foreach (var issue in batch)
            {
                try
                {
                    var created = ApplyIssue(issue);
                    phase.Applied++;
                    if (created) phase.Created++;
                }
                catch (ActimedLockedException) { throw; }
                catch (UnknownInventoryException uiex)
                {
                    _ctx.Log.Warn(uiex.Message);
                    phase.Skipped++;
                }
                catch (UnmappedKindException umkex)
                {
                    // Wichtig fuer UX: User soll im UI sofort sehen warum sein Issue nicht angekommen
                    // ist. Push als Error, damit es in "Letzte Meldungen" auftaucht (Warn pusht nicht).
                    phase.Errors.Add(umkex.Message);
                    phase.Skipped++;
                }
                catch (Exception ex)
                {
                    phase.Errors.Add($"issue {issue.Id}: {ex.Message}");
                    phase.Skipped++;
                }
            }

            if (batch.Count < pageLimit) break;
            page++;
        }

        return phase;
    }

    /// <summary>Returns true if a new A3_IS_ACT_DEV row was inserted, false if it already existed (and was updated).</summary>
    private bool ApplyIssue(Issues.Data issue)
    {
        var attr = issue.Attributes ?? throw new InvalidOperationException("issue has no attributes");
        var inventoryDeviceNumber = attr.InventoryDeviceNumber;

        ActimedDevice? device = null;
        if (!string.IsNullOrWhiteSpace(inventoryDeviceNumber))
            device = _ctx.Actimed.FindDeviceByInventoryNo(inventoryDeviceNumber, _ctx.Tenant.ActimedCustIds);

        if (device == null)
            throw new UnknownInventoryException(
                $"Issue {issue.Id} references inventory {inventoryDeviceNumber} which is not in Actimed for this tenant.");

        // Map Samedis maintenance flavor → Actimed activity kind + (optional) konkrete Activity.
        var match = _ctx.KindMapper.Resolve(attr.MaintenanceType, attr.Title, attr.Services);
        var kindName = match.ActimedKind;

        var kind = _ctx.Actimed.FindActivityKindByName(kindName);
        if (kind == null)
        {
            // Wir werfen UnmappedKindException — der DownloadIssues-foreach faengt das ab und
            // schiebt die Begruendung ins UI, damit der User klar sieht warum sein Issue nicht in Actimed landet.
            throw new UnmappedKindException(
                $"Issue {issue.Id} (inventar={inventoryDeviceNumber}): " +
                $"kind '{kindName}' nicht in A3_ACTIVITY_KIND gefunden. " +
                $"Bitte 'maintenance_kind_mapping' in der config.yml korrigieren — " +
                $"actimed_kind muss exakt mit einem KIND_NAME aus der A3_ACTIVITY_KIND-Tabelle " +
                $"uebereinstimmen (inkl. Sonderzeichen wie §).");
        }

        ActimedActivity? activity;
        if (!string.IsNullOrWhiteSpace(match.ActimedActivityName))
        {
            // Mapping verweist explizit auf eine konkrete Tätigkeit (mit hinterlegter Prüfvorschrift) —
            // diese muss existieren, sonst ist die Konfiguration falsch.
            activity = _ctx.Actimed.FindActivityByName(match.ActimedActivityName);
            if (activity == null)
            {
                throw new UnmappedKindException(
                    $"Issue {issue.Id} (inventar={inventoryDeviceNumber}): " +
                    $"actimed_activity_name '{match.ActimedActivityName}' nicht in A3_ACTIVITY gefunden. " +
                    $"Bitte 'maintenance_kind_mapping' in der config.yml korrigieren — " +
                    $"actimed_activity_name muss exakt mit einem ACTIVITY_NAME aus A3_ACTIVITY uebereinstimmen.");
            }
        }
        else
        {
            // Kein actimed_activity_name im Mapping — fallback: existierende Tätigkeit per
            // KIND_ID suchen. Wenn keine existiert UND TEST_SPEC_ID=1 (=Unbekannt) waere, legen
            // wir KEINE Stub-Tätigkeit an, sondern werfen eine UnmappedKindException. Das war
            // frueher das stille "Prüfvorschrift: Unbekannt"-Verhalten — schwer zu erkennen, weil
            // der Eintrag in Actimed sichtbar war, aber beim Start der Pruefung leer blieb.
            // Jetzt skipt der Sync das Issue offen und der User sieht, was er konfigurieren muss.
            activity = _ctx.Actimed.FindActivityByKindId(kind.KindId);
            if (activity == null)
            {
                throw new UnmappedKindException(
                    $"Issue {issue.Id} (inventar={inventoryDeviceNumber}): " +
                    $"Keine A3_ACTIVITY (Tätigkeit) für KIND_NAME='{kindName}' in Actimed vorhanden " +
                    $"und im Mapping ist kein 'actimed_activity_name' gesetzt. " +
                    $"ENTWEDER in Actimed eine Tätigkeit zu dieser Tätigkeitsart anlegen (mit Prüfvorschrift), " +
                    $"ODER im Wartungsart-Mapping (Tab 'Wartungsart-Mapping') das Feld 'Activity Name' " +
                    $"auf eine existierende Tätigkeit setzen — sonst hätte die Tätigkeit in Actimed " +
                    $"keine Prüfvorschrift hinterlegt (TEST_SPEC=Unbekannt, Pruefdialog leer).");
            }
        }

        // Schedule it.
        var dueOn = ParseDate(attr.DueOn) ?? ParseDate(attr.Date) ?? DateTime.Today;
        var inserted = _ctx.Actimed.UpsertIsActDev(new ActimedIsActDev(
            DevId: device.DevId,
            ActivityId: activity.ActivityId,
            Next: dueOn,
            Last: null,
            TesterId: 1 /* TODO: aus Config / responsible_name lookup */));

        // Bridge table (5.3 step 4): issue_link
        _ctx.IssueLinks.Upsert(new IssueLinkRow(
            TenantId: _ctx.Tenant.SamedisTenantId,
            SamedisIssueId: issue.Id ?? "",
            SamedisExternalId: attr.ExternalId,
            ActimedDevId: device.DevId,
            ActimedActivityId: activity.ActivityId,
            PlannedDue: dueOn != default ? new DateTimeOffset(dueOn, TimeSpan.Zero) : null,
            DownloadedAt: DateTimeOffset.UtcNow));

        return inserted;
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, out var dt) ? dt : null;
    }

    /// <summary>
    /// Builds a human-readable error string that contains everything we need to debug:
    /// HTTP method + full URL + status + RestSharp last error + truncated response body.
    /// The response body is what Samedis actually says when it returns 4xx — the most useful
    /// info we have, and previously was getting swallowed.
    /// </summary>
    internal static string FormatHttpError(string method, string url, int status, string? lastError, string? body)
    {
        const int maxBody = 800;
        var bodyPreview = string.IsNullOrEmpty(body)
            ? "<empty>"
            : (body.Length > maxBody ? body.Substring(0, maxBody) + "..." : body);
        var le = string.IsNullOrEmpty(lastError) ? "" : $" {lastError}";
        return $"{method} {url} -> HTTP {status}{le}\n  body: {bodyPreview}";
    }

    public class DownloadResult
    {
        public PhaseResult Inventories { get; set; } = new("inventories");
        public PhaseResult Issues { get; set; } = new("issues");
        public bool IsSuccess => Inventories.Errors.Count == 0 && Issues.Errors.Count == 0;
    }
}

public class PhaseResult
{
    public string Name { get; }
    /// <summary>Total processed without error (Created + Updated + already-current).</summary>
    public int Applied { get; set; }
    /// <summary>Newly inserted (sub-count of Applied).</summary>
    public int Created { get; set; }
    /// <summary>Existing rows that had broken FKs and got auto-repaired (sub-count of Applied).</summary>
    public int Repaired { get; set; }
    /// <summary>Skipped due to non-fatal reasons (unknown_inventory, mapping unclear, ...).</summary>
    public int Skipped { get; set; }
    public List<string> Errors { get; } = new();

    public PhaseResult(string name) => Name = name;
}

public class UnknownInventoryException : Exception
{
    public UnknownInventoryException(string message) : base(message) { }
}

/// <summary>
/// Wird geworfen, wenn der MaintenanceKindMapper einen KIND_NAME liefert, den die echte
/// A3_ACTIVITY_KIND-Tabelle nicht enthaelt. Typischer Grund: das Mapping in der config.yml
/// ist falsch geschrieben (z. B. 'paragraph 7' statt '§7'). Der User muss die config korrigieren.
/// </summary>
public class UnmappedKindException : Exception
{
    public UnmappedKindException(string message) : base(message) { }
}
