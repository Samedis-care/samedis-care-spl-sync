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
            // KIND_ID suchen und wiederverwenden.
            activity = _ctx.Actimed.FindActivityByKindId(kind.KindId);
            if (activity == null)
            {
                // Fehlt die Tätigkeit ganz: nur dann selbst anlegen, wenn der User es erlaubt UND
                // die Prüfvorschrift explizit im Mapping steht. Sonst weiter mit klarer Meldung
                // abbrechen (eine Stub-Tätigkeit mit TEST_SPEC=1 wäre wertlos).
                activity = TryCreateActivity(issue, inventoryDeviceNumber, kind, match);
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

    /// <summary>
    /// Wählt aus with_service_intervals[] den für diesen Vorgang passenden Eintrag: bevorzugt
    /// category=maintenance; bei mehreren Kandidaten Feinabgleich über das Label gegen
    /// services/title des Issues; sonst der erste maintenance-Eintrag (bzw. der erste überhaupt).
    /// Liefert null, wenn keine Intervalle vorhanden sind.
    /// </summary>
    public static Issues.ServiceInterval? PickServiceInterval(
        List<Issues.ServiceInterval>? all, IEnumerable<string>? services, string? title)
    {
        if (all == null || all.Count == 0) return null;

        var pool = all.Where(x => string.Equals(x.Category, "maintenance", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0) pool = all;

        if (pool.Count > 1)
        {
            var haystack = string.Join(" | ",
                new[] { title }.Concat(services ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))!).ToLowerInvariant();
            if (haystack.Length > 0)
            {
                var byLabel = pool.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.Label) && haystack.Contains(x.Label!.Trim().ToLowerInvariant()));
                if (byLabel != null) return byLabel;
            }
        }

        return pool[0];
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, out var dt) ? dt : null;
    }

    /// <summary>
    /// Legt eine fehlende A3_ACTIVITY (Tätigkeit) für die gemappte Tätigkeitsart an — aber nur,
    /// wenn sync.create_activities_from_mapping=true ist UND der Mapping-Eintrag eine gültige
    /// Prüfvorschrift (TEST_SPEC) angibt (per Name oder ID, niemals "Unbekannt"/ID 1). In allen
    /// anderen Fällen wird eine UnmappedKindException mit konkreter Handlungsanweisung geworfen,
    /// damit das Issue offen bleibt und der User sieht, was zu konfigurieren ist.
    /// </summary>
    private ActimedActivity TryCreateActivity(
        Issues.Data issue, string? inventoryDeviceNumber, ActimedActivityKind kind, MaintenanceKindMatch match)
    {
        var kindName = kind.Name.Trim();

        if (!_ctx.Config.Sync.CreateActivitiesFromMapping)
        {
            throw new UnmappedKindException(
                $"Issue {issue.Id} (inventar={inventoryDeviceNumber}): " +
                $"Keine A3_ACTIVITY (Tätigkeit) für KIND_NAME='{kindName}' in Actimed vorhanden " +
                $"und im Mapping ist kein 'actimed_activity_name' gesetzt. " +
                $"ENTWEDER in Actimed eine Tätigkeit zu dieser Tätigkeitsart anlegen (mit Prüfvorschrift), " +
                $"ODER im Wartungsart-Mapping (Tab 'Wartungsart-Mapping') das Feld 'Activity Name' " +
                $"auf eine existierende Tätigkeit setzen, " +
                $"ODER sync.create_activities_from_mapping aktivieren und im Mapping eine Prüfvorschrift " +
                $"('actimed_test_spec_name') hinterlegen — sonst hätte die Tätigkeit in Actimed " +
                $"keine Prüfvorschrift hinterlegt (TEST_SPEC=Unbekannt, Pruefdialog leer).");
        }

        // Prüfvorschrift auflösen: ID hat Vorrang vor Name.
        ActimedTestSpec? spec = null;
        if (match.ActimedTestSpecId is int specId)
            spec = _ctx.Actimed.GetTestSpecById(specId);
        else if (!string.IsNullOrWhiteSpace(match.ActimedTestSpecName))
            spec = _ctx.Actimed.FindTestSpecByName(match.ActimedTestSpecName);

        if (spec == null || spec.IsUnknown)
        {
            var wanted = match.ActimedTestSpecId is int id
                ? $"actimed_test_spec_id={id}"
                : $"actimed_test_spec_name='{match.ActimedTestSpecName}'";
            throw new UnmappedKindException(
                $"Issue {issue.Id} (inventar={inventoryDeviceNumber}): " +
                $"Für KIND_NAME='{kindName}' existiert keine Tätigkeit und das automatische Anlegen ist " +
                $"aktiv, aber die im Mapping angegebene Prüfvorschrift ({wanted}) wurde in TEST_SPEC " +
                $"nicht gefunden oder ist 'Unbekannt' (ID 1). Bitte im Wartungsart-Mapping eine " +
                $"existierende, gültige Prüfvorschrift eintragen.");
        }

        // Intervall-Quelle (nur beim Anlegen): Samedis-Wartungsmaßnahme > Mapping-Config > Default 12.
        // Existierende Tätigkeiten fassen wir bewusst nicht an (Modellierung: "nur beim Anlegen setzen").
        // Samedis liefert with_service_intervals[] mit Betrag + Einheit (day/week/month/year) —
        // passenden Eintrag wählen und in Monate umrechnen (Actimed-Einheit).
        var si = PickServiceInterval(issue.Attributes?.WithServiceIntervals, issue.Attributes?.Services, issue.Attributes?.Title);
        var samedisMonths = IntervalConversion.ToMonths(si?.Value, si?.Unit);
        int interval;
        string intervalSource;
        if (samedisMonths is int sm)                     { interval = sm; intervalSource = "Samedis"; }
        else if (match.IntervalMonths is int m && m > 0) { interval = m;  intervalSource = "Mapping-Config"; }
        else                                             { interval = 12; intervalSource = "Default"; }

        var newId = _ctx.Actimed.InsertActivity(new ActimedActivity(
            ActivityId: 0,          // wird vom Repository (MAX+1) vergeben
            TestSpecId: spec.TestSpecId,
            KindId: kind.KindId,
            IntervalMonths: interval,
            Name: kindName));

        var rawInterval = si != null ? $"{si.Value?.ToString() ?? "null"} {si.Unit ?? ""} (category={si.Category ?? "-"})" : "kein with_service_intervals";
        _ctx.Log.Info(
            $"A3_ACTIVITY angelegt: ACTIVITY_ID={newId}, KIND_ID={kind.KindId} ('{kindName}'), " +
            $"TEST_SPEC_ID={spec.TestSpecId} ('{spec.Name.Trim()}'), " +
            $"Intervall={interval} Monate (Quelle: {intervalSource}, Samedis-Rohwert: {rawInterval}).");

        return new ActimedActivity(newId, spec.TestSpecId, kind.KindId, interval, kindName);
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
