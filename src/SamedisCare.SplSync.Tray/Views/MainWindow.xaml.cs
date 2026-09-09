using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Config;
using SamedisCare.SplSync.Core.Sync;
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;

namespace SamedisCare.SplSync.Tray.Views;

public partial class MainWindow : Window
{
    private readonly string _configPath;
    private readonly TrayStatus _status;
    private DispatcherTimer? _refreshTimer;

    /// <summary>
    /// Mapping-Editor-Backing-Store. Wird einmalig beim Loaded-Event aus cfg.MaintenanceKindMapping
    /// befuellt und vom DataGrid live editiert. Auf Save schreiben wir die Collection zurueck in
    /// die config.yml — das 2s-Refresh laesst diese Collection in Ruhe (ueberschreibt nicht).
    /// </summary>
    private readonly ObservableCollection<MaintenanceKindMappingEntry> _mappingEntries = new();

    public MainWindow(string configPath, TrayStatus status)
    {
        InitializeComponent();
        _configPath = configPath;
        _status = status;

        Loaded += (_, _) =>
        {
            Refresh();
            LoadSettingsIntoUi();
            LoadMappingIntoUi();
            // 2s Polling: damit Status, Letzte Meldungen und Mandanten-Liste live mitlaufen,
            // ohne dass die Window neu geöffnet werden muss. Klein gehalten, weil Refresh
            // eh nur einen In-Memory-Status und die config.yml liest.
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (_, _) => Refresh();
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
        };
    }

    /// <summary>
    /// Wird vom App.xaml.cs nach First-Run aufgerufen, damit der User direkt im
    /// Settings-Tab landet statt erst durch die Reiter klicken zu müssen.
    /// </summary>
    public void SwitchToSettingsTab()
    {
        if (SettingsTab != null) MainTabs.SelectedItem = SettingsTab;
    }

    private void Refresh()
    {
        StatusText.Text = $"{_status.State} — {_status.Message}" +
                          (_status.LastRun.HasValue ? $" (zuletzt: {_status.LastRun:G})" : "");

        // Snapshot ziehen — RecentErrors wird vom Worker-Thread parallel mutiert,
        // direkt enumerieren würde sporadisch eine InvalidOperationException werfen.
        List<string> messagesSnapshot;
        lock (_status.RecentErrors)
        {
            messagesSnapshot = _status.RecentErrors.ToList();
        }
        messagesSnapshot.Reverse(); // neueste oben
        // Leerzeile zwischen den Entries — viele Meldungen sind mehrzeilig (URL + Body).
        var newText = string.Join(Environment.NewLine + Environment.NewLine, messagesSnapshot);
        if (ErrorsList.Text != newText)
        {
            ErrorsList.Text = newText;
            // Auf Anfang scrollen, weil neueste Meldung oben steht.
            ErrorsList.ScrollToHome();
        }

        try
        {
            var cfg = ConfigStore.Load(_configPath);
            TenantsGrid.ItemsSource = cfg.Tenants.Select(t => new
            {
                t.Name,
                t.SamedisTenantId,
                CustIds = string.Join(",", t.ActimedCustIds),
                t.Enabled
            }).ToList();
            // MappingGrid bewusst NICHT aktualisieren — das ist ein Editor mit ObservableCollection,
            // der vom User aktiv bearbeitet werden kann. Reload geht ueber den Button.
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Konfiguration konnte nicht geladen werden: {ex.Message}";
        }
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        // Direkter Durchgriff auf den InProcessSyncHost via App.TriggerSyncNow.
        // Frueher zeigte dieser Button nur einen Hinweis, was vom User zurecht als kaputt
        // wahrgenommen wurde — jetzt loest er denselben Sync aus wie das Tray-Menue-Item.
        if (Application.Current is App app && app.TriggerSyncNow())
        {
            StatusText.Text = "Sync gestartet — siehe 'Letzte Meldungen' fuer den Fortschritt.";
        }
        else
        {
            MessageBox.Show(this,
                "Sync-Worker ist nicht aktiv. Bitte Konfiguration pruefen (config.yml gueltig?) und Tray neu starten.",
                "Sync nicht moeglich",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MapTenants_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = ConfigStore.Load(_configPath);
            var dlg = new Dialogs.TenantMappingDialog(_configPath, cfg) { Owner = this };
            if (dlg.ShowDialog() == true) Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Mandanten-Dialog konnte nicht geöffnet werden:\n{ex.Message}");
        }
    }

    // -----------------------------------------------------------------------------
    // Settings-Tab — Load + Save + Browse-Buttons
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Liest die aktuelle config.yml und schiebt jeden Wert in die passende Eingabe.
    /// Wird beim Loaded-Event aufgerufen UND nach "Verwerfen / Neu laden".
    /// </summary>
    private void LoadSettingsIntoUi()
    {
        AppConfig cfg;
        try
        {
            cfg = ConfigStore.Load(_configPath);
        }
        catch (Exception ex)
        {
            SetSettingsStatus($"Konfiguration konnte nicht geladen werden: {ex.Message}", isError: true);
            return;
        }

        AuthUri.Text          = cfg.Auth.Uri;
        AuthClientId.Text     = cfg.Auth.ClientId;
        AuthClientSecret.Password = cfg.Auth.ClientSecret;

        SamedisUri.Text        = cfg.Samedis.Uri;
        SamedisApiVersion.Text = cfg.Samedis.ApiVersion;
        SelectComboValue(SamedisAccessMode, cfg.Samedis.AccessMode);
        SamedisEnterpriseTenantId.Text = cfg.Samedis.EnterpriseTenantId;
        UpdateEnterpriseFieldVisibility();

        ActimedDatabasePath.Text   = cfg.Actimed.DatabasePath;
        ActimedProtocolPdfDir.Text = cfg.Actimed.ProtocolPdfDir;
        ActimedUseLocalSnapshot.IsChecked = cfg.Actimed.UseLocalSnapshot;
        ActimedDefaultTester.Text  = cfg.Actimed.DefaultTesterName;

        BrandingName.Text     = cfg.Branding.ServiceProviderName;
        BrandingLogoPath.Text = cfg.Branding.LogoPath;

        SyncDownloadIntervalMinutes.Text = cfg.Sync.DownloadIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        SyncUploadPollSeconds.Text       = cfg.Sync.UploadPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        SyncRunOnReconnect.IsChecked     = cfg.Sync.RunOnNetworkReconnect;
        SyncDownloadInventories.IsChecked = cfg.Sync.DownloadInventories;
        SyncDownloadOpenIssues.IsChecked  = cfg.Sync.DownloadOpenIssues;
        SyncUploadFinishedIssues.IsChecked = cfg.Sync.UploadFinishedIssues;
        SyncUploadModePdfPickup.IsChecked = cfg.Sync.UploadModePdfPickup;
        SyncUploadModePngOnCompletion.IsChecked = cfg.Sync.UploadModePngOnCompletion;
        SyncCreateIssuesFromActimed.IsChecked = cfg.Sync.CreateIssuesFromActimed;
        SyncCreateInventoriesFromActimed.IsChecked = cfg.Sync.CreateInventoriesFromActimed;
        SyncSetInventoryOpStatusOnFailed.IsChecked = cfg.Sync.SetInventoryOperationStatusOnFailedMaintenance;
        SyncCreateActivitiesFromMapping.IsChecked = cfg.Sync.CreateActivitiesFromMapping;
        SyncCreatePlannedIssueAfterCompletion.IsChecked = cfg.Sync.CreatePlannedIssueAfterCompletion;

        LoggingLevel.Text     = cfg.Logging.Level.ToString(CultureInfo.InvariantCulture);
        LoggingMode.Text      = cfg.Logging.Mode.ToString(CultureInfo.InvariantCulture);
        LoggingDirectory.Text = cfg.Logging.Directory;

        HttpValidCertificate.IsChecked = cfg.Http.ValidCertificate;
        HttpProxy.Text                 = cfg.Http.Proxy;
        HttpProxyUsername.Text         = cfg.Http.ProxyUsername;
        HttpProxyPassword.Password     = cfg.Http.ProxyPassword;
        HttpTimeoutSeconds.Text        = cfg.Http.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        SetSettingsStatus("");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        AppConfig cfg;
        try
        {
            // Wir laden vor dem Schreiben nochmal, damit Felder, die wir hier nicht
            // editieren (Tenants, MaintenanceKindMapping, App.AutostartPrompted) erhalten bleiben.
            cfg = ConfigStore.Load(_configPath);
        }
        catch (Exception ex)
        {
            SetSettingsStatus($"Konfiguration konnte nicht geladen werden: {ex.Message}", isError: true);
            return;
        }

        try
        {
            cfg.Auth.Uri          = AuthUri.Text.Trim();
            cfg.Auth.ClientId     = AuthClientId.Text.Trim();
            cfg.Auth.ClientSecret = AuthClientSecret.Password;

            cfg.Samedis.Uri        = SamedisUri.Text.Trim();
            cfg.Samedis.ApiVersion = SamedisApiVersion.Text.Trim();
            cfg.Samedis.AccessMode = (SamedisAccessMode.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                     ?? AccessModes.Tenant;
            cfg.Samedis.EnterpriseTenantId = SamedisEnterpriseTenantId.Text.Trim();

            cfg.Actimed.DatabasePath      = ActimedDatabasePath.Text.Trim();
            cfg.Actimed.ProtocolPdfDir    = ActimedProtocolPdfDir.Text.Trim();
            cfg.Actimed.UseLocalSnapshot  = ActimedUseLocalSnapshot.IsChecked == true;
            cfg.Actimed.DefaultTesterName = ActimedDefaultTester.Text.Trim();

            cfg.Branding.ServiceProviderName = BrandingName.Text.Trim();
            cfg.Branding.LogoPath            = BrandingLogoPath.Text.Trim();

            cfg.Sync.DownloadIntervalMinutes = ParseIntOrDefault(SyncDownloadIntervalMinutes.Text, 15, min: 1);
            cfg.Sync.UploadPollIntervalSeconds = ParseIntOrDefault(SyncUploadPollSeconds.Text, 30, min: 5);
            cfg.Sync.RunOnNetworkReconnect = SyncRunOnReconnect.IsChecked == true;
            cfg.Sync.DownloadInventories   = SyncDownloadInventories.IsChecked == true;
            cfg.Sync.DownloadOpenIssues    = SyncDownloadOpenIssues.IsChecked == true;
            cfg.Sync.UploadFinishedIssues  = SyncUploadFinishedIssues.IsChecked == true;
            cfg.Sync.UploadModePdfPickup   = SyncUploadModePdfPickup.IsChecked == true;
            cfg.Sync.UploadModePngOnCompletion = SyncUploadModePngOnCompletion.IsChecked == true;
            cfg.Sync.CreateIssuesFromActimed = SyncCreateIssuesFromActimed.IsChecked == true;
            cfg.Sync.CreateInventoriesFromActimed = SyncCreateInventoriesFromActimed.IsChecked == true;
            cfg.Sync.SetInventoryOperationStatusOnFailedMaintenance = SyncSetInventoryOpStatusOnFailed.IsChecked == true;
            cfg.Sync.CreateActivitiesFromMapping = SyncCreateActivitiesFromMapping.IsChecked == true;
            cfg.Sync.CreatePlannedIssueAfterCompletion = SyncCreatePlannedIssueAfterCompletion.IsChecked == true;

            cfg.Logging.Level     = ParseIntOrDefault(LoggingLevel.Text, 1, min: 0, max: 2);
            cfg.Logging.Mode      = ParseIntOrDefault(LoggingMode.Text, 3, min: 0, max: 3);
            cfg.Logging.Directory = LoggingDirectory.Text.Trim();

            cfg.Http.ValidCertificate = HttpValidCertificate.IsChecked == true;
            cfg.Http.Proxy            = HttpProxy.Text.Trim();
            cfg.Http.ProxyUsername    = HttpProxyUsername.Text.Trim();
            cfg.Http.ProxyPassword    = HttpProxyPassword.Password;
            cfg.Http.TimeoutSeconds   = ParseIntOrDefault(HttpTimeoutSeconds.Text, 30, min: 1);

            ConfigStore.Save(_configPath, cfg);
            // Die meisten Loops lesen config.yml bei jeder Iteration neu — Intervalle, Tenants,
            // Kind-Mapping etc. werden so beim nächsten Tick automatisch wirksam.
            // ABER: Pdf-Pickup-Watcher wird nur einmal initialisiert. Damit Modus-1-Schalter
            // und protocol_pdf_dir-Änderungen sofort greifen, triggern wir hier ein Reload.
            if (Application.Current is App app)
                app.NotifyConfigChanged();

            SetSettingsStatus("Gespeichert. Änderungen sind sofort aktiv (PDF-Watcher neu gestartet, Loops lesen Config beim nächsten Tick).");
        }
        catch (Exception ex)
        {
            SetSettingsStatus($"Speichern fehlgeschlagen: {ex.Message}", isError: true);
        }
    }

    private void ReloadSettings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        SetSettingsStatus("Aktuelle config.yml neu geladen.");
    }

    private void BrowseDatabasePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Actimed-Datenbank (*.mdb;*.sqlite)|*.mdb;*.sqlite|Alle Dateien (*.*)|*.*",
            Title = "actimed3db.mdb auswählen"
        };
        if (!string.IsNullOrWhiteSpace(ActimedDatabasePath.Text)) dlg.FileName = ActimedDatabasePath.Text;
        if (dlg.ShowDialog(this) == true) ActimedDatabasePath.Text = dlg.FileName;
    }

    private void BrowseProtocolPdfDir_Click(object sender, RoutedEventArgs e)
    {
        // WPF bringt keinen FolderBrowser mit — wir nutzen den WinForms-Dialog.
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "PDF-Pickup-Verzeichnis auswählen",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(ActimedProtocolPdfDir.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : ActimedProtocolPdfDir.Text
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ActimedProtocolPdfDir.Text = dlg.SelectedPath;
    }

    private void BrowseLogoPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Bilder (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Alle Dateien (*.*)|*.*",
            Title = "Logo für Werteprotokolle auswählen"
        };
        if (!string.IsNullOrWhiteSpace(BrandingLogoPath.Text)) dlg.FileName = BrandingLogoPath.Text;
        if (dlg.ShowDialog(this) == true) BrandingLogoPath.Text = dlg.FileName;
    }

    private void SetSettingsStatus(string text, bool isError = false)
    {
        SettingsStatus.Text = text;
        SettingsStatus.Foreground = isError
            ? System.Windows.Media.Brushes.DarkRed
            : System.Windows.Media.Brushes.DarkGreen;
    }

    private static int ParseIntOrDefault(string raw, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return fallback;
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    // -----------------------------------------------------------------------------
    // Zugriffsweg (tenant / enterprise)
    // -----------------------------------------------------------------------------

    private void AccessMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateEnterpriseFieldVisibility();

    /// <summary>
    /// Das Service-Welt-Feld nur zeigen, wenn der Zugriffsweg auf enterprise steht — im
    /// tenant-Modus wäre es leer und verwirrend.
    /// </summary>
    private void UpdateEnterpriseFieldVisibility()
    {
        if (SamedisEnterpriseTenantId is null) return;   // während InitializeComponent

        var enterprise = string.Equals((SamedisAccessMode.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                                       AccessModes.Enterprise, StringComparison.OrdinalIgnoreCase);
        var visibility = enterprise ? Visibility.Visible : Visibility.Collapsed;

        EnterpriseTenantLabel.Visibility = visibility;
        SamedisEnterpriseTenantId.Visibility = visibility;
        EnterpriseTenantHint.Visibility = visibility;
    }

    private static void SelectComboValue(System.Windows.Controls.ComboBox box, string? value)
    {
        var match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    // -----------------------------------------------------------------------------
    // Wartungsart-Mapping — Editor
    // -----------------------------------------------------------------------------

    private void LoadMappingIntoUi()
    {
        try
        {
            var cfg = ConfigStore.Load(_configPath);
            _mappingEntries.Clear();
            foreach (var entry in cfg.MaintenanceKindMapping) _mappingEntries.Add(entry);
            MappingGrid.ItemsSource = _mappingEntries;
            SetMappingStatus("");
        }
        catch (Exception ex)
        {
            SetMappingStatus($"Konfiguration konnte nicht geladen werden: {ex.Message}", isError: true);
        }
    }

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        var entry = new MaintenanceKindMappingEntry { Match = "(?i)", ActimedKind = "" };
        _mappingEntries.Add(entry);
        // Direkt in den neuen Eintrag fokussieren, damit der User sofort tippen kann.
        MappingGrid.SelectedItem = entry;
        MappingGrid.ScrollIntoView(entry);
        SetMappingStatus("Neuer Eintrag — Felder ausfüllen und 'Speichern' klicken.");
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (MappingGrid.SelectedItem is MaintenanceKindMappingEntry sel)
        {
            _mappingEntries.Remove(sel);
            SetMappingStatus("Eintrag entfernt — 'Speichern' klicken um die Änderung zu persistieren.");
        }
        else
        {
            SetMappingStatus("Bitte erst eine Zeile auswählen.", isError: true);
        }
    }

    private void SaveMapping_Click(object sender, RoutedEventArgs e)
    {
        AppConfig cfg;
        try { cfg = ConfigStore.Load(_configPath); }
        catch (Exception ex)
        {
            SetMappingStatus($"Konfiguration konnte nicht geladen werden: {ex.Message}", isError: true);
            return;
        }

        // Pending DataGrid-Edit committen — sonst geht der Cell-Edit verloren wenn der User
        // direkt aus dem Eingabefeld auf 'Speichern' klickt ohne vorher Tab/Enter zu drücken.
        MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);

        // Validierung: actimed_kind ist Pflichtfeld. Match darf leer sein (= Fallback).
        var problems = _mappingEntries
            .Select((entry, idx) => (idx, entry))
            .Where(x => string.IsNullOrWhiteSpace(x.entry.ActimedKind))
            .Select(x => $"Zeile {x.idx + 1}: 'Actimed Kind' fehlt.")
            .ToList();
        if (problems.Count > 0)
        {
            SetMappingStatus(string.Join(" | ", problems), isError: true);
            return;
        }

        try
        {
            cfg.MaintenanceKindMapping = _mappingEntries.ToList();
            ConfigStore.Save(_configPath, cfg);

            // Loops im InProcessSyncHost lesen die config.yml bei jedem Tick neu — aber wir
            // wollen, dass der Mapper sofort mit den neuen Regeln arbeitet, falls gerade ein
            // Sync läuft. NotifyConfigChanged stösst zusätzlich den PDF-Watcher-Reload an,
            // was hier zwar nicht relevant ist, aber konsistent mit dem Settings-Save-Pfad.
            if (Application.Current is App app) app.NotifyConfigChanged();

            SetMappingStatus($"Gespeichert — {cfg.MaintenanceKindMapping.Count} Regel(n) aktiv. Wirkt beim nächsten Sync-Tick.");
        }
        catch (Exception ex)
        {
            SetMappingStatus($"Speichern fehlgeschlagen: {ex.Message}", isError: true);
        }
    }

    private void ReloadMapping_Click(object sender, RoutedEventArgs e)
    {
        LoadMappingIntoUi();
        SetMappingStatus("Aktuelle config.yml neu geladen.");
    }

    private void SetMappingStatus(string text, bool isError = false)
    {
        MappingStatus.Text = text;
        MappingStatus.Foreground = isError
            ? System.Windows.Media.Brushes.DarkRed
            : System.Windows.Media.Brushes.DarkGreen;
    }

    // -----------------------------------------------------------------------------
    // Wartungs-Operationen — direkt aus dem Settings-Tab anstoßbar
    // -----------------------------------------------------------------------------

    private async void RepairAll_Click(object sender, RoutedEventArgs e)
    {
        await RunMaintenanceAsync("Reparatur-Lauf", (cfg, ops) =>
        {
            var totalFound = 0;
            var totalRepaired = 0;
            var touchedTenants = 0;
            foreach (var tenant in cfg.Tenants.Where(t => t.Enabled && t.ActimedCustIds.Count > 0))
            {
                touchedTenants++;
                var (found, repaired) = ops.RepairAllSyncCreatedDevices(tenant.ActimedCustIds);
                totalFound += found;
                totalRepaired += repaired;
            }

            // Diagnose-freundlich: Wenn Found=0, weiss der User dass die LIKE-Query nichts gefunden hat
            // (CUST_ID-Mapping falsch, oder das DEV_Memo-Pattern matched nicht).
            // Wenn Found>0 aber Repaired=0, sind die FKs schon ok — also alles gut.
            string summary;
            if (touchedTenants == 0)
                summary = "Keine aktiven Mandanten mit CUST_IDs in der Konfiguration.";
            else if (totalFound == 0)
                summary = $"Keine vom Sync angelegten Inventare gefunden über {touchedTenants} Mandanten. " +
                          "Pruefe ob CUST_IDs in der Tenant-Konfiguration stimmen.";
            else if (totalRepaired == 0)
                summary = $"{totalFound} Sync-Inventar(e) gefunden, alle FKs bereits OK — keine Reparatur noetig.";
            else
                summary = $"{totalRepaired} von {totalFound} Sync-Inventar(en) repariert über {touchedTenants} Mandanten.";

            return (summary, totalRepaired);
        });
    }

    private async void ResetCursor_Click(object sender, RoutedEventArgs e)
    {
        var (ok, summary, count) = await RunMaintenanceAsync("Cursor-Reset", (cfg, ops) =>
        {
            var n = 0;
            foreach (var tenant in cfg.Tenants.Where(t => t.Enabled))
            {
                ops.ResetDownloadCursor(tenant.SamedisTenantId);
                ops.ResetUploadCursor(tenant.SamedisTenantId);
                n++;
            }
            return ($"Cursor für {n} Mandant(en) zurückgesetzt. Der nächste Sync zieht alle Daten neu.", n);
        });

        if (!ok || count <= 0) return;

        // UX: nach dem Reset will der User in 99% der Fälle direkt syncen — bieten wir an.
        var doSync = MessageBox.Show(this,
            $"{summary}\n\nJetzt direkt einen Sync auslösen?",
            "Cursor zurückgesetzt",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (doSync == MessageBoxResult.Yes && Application.Current is App app && app.TriggerSyncNow())
            SetSettingsStatus("Sync gestartet — siehe 'Letzte Meldungen' für den Fortschritt.");
    }

    private async void DeleteSyncDevices_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Es werden ALLE A3_DEV-Einträge gelöscht, deren DEV_Memo mit 'created_by=spl-sync' beginnt — " +
            "für alle aktiven Mandanten in der Konfiguration.\n\n" +
            "User-eigene Inventare bleiben erhalten.\n\n" +
            "Empfehlung: nach dem Löschen 'Cursor zurücksetzen' + 'Jetzt synchronisieren'.\n\n" +
            "Fortfahren?",
            "Sync-Inventare löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await RunMaintenanceAsync("Sync-Inventare löschen", (cfg, ops) =>
        {
            var deleted = 0;
            var touchedTenants = 0;
            foreach (var tenant in cfg.Tenants.Where(t => t.Enabled && t.ActimedCustIds.Count > 0))
            {
                touchedTenants++;
                deleted += ops.DeleteSyncCreatedDevices(tenant.ActimedCustIds);
            }
            return ($"{deleted} Inventar(e) gelöscht über {touchedTenants} aktive Mandanten.", deleted);
        });
    }

    /// <summary>
    /// Boilerplate für Wartungs-Operationen. Läuft die Operation in Task.Run, damit der
    /// UI-Thread nicht blockiert (Repair über 220 Devices kann mehrere Sekunden brauchen).
    /// Status-Updates landen wieder im UI-Thread, weil die await-Continuation auf den
    /// SynchronizationContext zurückspringt. Liefert (ok, statusText, count).
    /// </summary>
    private async Task<(bool Ok, string Summary, int Count)> RunMaintenanceAsync(
        string label,
        Func<AppConfig, MaintenanceOperations, (string Summary, int Count)> action)
    {
        SetSettingsStatus($"{label} läuft...");

        try
        {
            var configPath = _configPath;
            var (summary, count) = await Task.Run(() =>
            {
                var cfg = ConfigStore.Load(configPath);
                var stateDb = OpenStateDbForUi();
                var actimed = BuildActimedRepository(cfg);
                var ops = new MaintenanceOperations(actimed, stateDb);
                return action(cfg, ops);
            });
            SetSettingsStatus(summary);
            return (true, summary, count);
        }
        catch (ActimedLockedException)
        {
            SetSettingsStatus("Actimed ist gerade geöffnet — bitte schließen und erneut versuchen.", isError: true);
            MessageBox.Show(this,
                "Actimed hält die Datenbank exklusiv geöffnet. Bitte Actimed kurz schließen und " +
                "die Wartungs-Operation erneut anstoßen.",
                "Actimed gesperrt",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return (false, "", 0);
        }
        catch (Exception ex)
        {
            SetSettingsStatus($"{label} fehlgeschlagen: {ex.Message}", isError: true);
            return (false, "", 0);
        }
    }

    private static StateDb OpenStateDbForUi() => AppPaths.OpenStateDb();

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
            "Direkter Zugriff auf actimed3db.mdb erfordert Windows + Microsoft.ACE.OLEDB.16.0.");
    }

}
