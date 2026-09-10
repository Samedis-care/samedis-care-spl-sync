using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Api;
using SamedisCare.Api.Auth;
using SamedisCare.Api.Http;
using SamedisCare.Api.V4.Common;
using SamedisCare.Api.Query;
using SamedisCare.Helper.Logging;
using SamedisCare.SplSync.Core.Config;
using MessageBox = System.Windows.MessageBox;

namespace SamedisCare.SplSync.Tray.Dialogs;

/// <summary>
/// Modaler Dialog: laedt Samedis-Tenants über die API + Actimed-Kunden aus der lokalen DB,
/// zeigt eine Mapping-Liste, schreibt das Ergebnis zurück in die config.yml.
///
/// Diagnose: jeder API/DB-Schritt loggt in einen RecordingSyncLog. Das Ergebnis wird
/// per "Diagnose anzeigen" als Textbox angezeigt — entscheidend, um den richtigen
/// Endpoint-Pfad zu finden, falls die Default-Variante nichts liefert.
/// </summary>
public partial class TenantMappingDialog : Window
{
    private readonly string _configPath;
    private AppConfig _cfg;
    private readonly RecordingSyncLog _diagLog = new(level: 2);

    public ObservableCollection<TenantMappingRow> Rows { get; } = new();
    public ObservableCollection<ActimedCustomer> Customers { get; } = new();

    public TenantMappingDialog(string configPath, AppConfig cfg)
    {
        InitializeComponent();
        _configPath = configPath;
        _cfg = cfg;
        MappingGrid.ItemsSource = Rows;
        CustomersGrid.ItemsSource = Customers;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void ShowDiag_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title  = "Diagnose-Log",
            Width  = 900,
            Height = 500,
            Owner  = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon   = this.Icon,
            Content = new System.Windows.Controls.TextBox
            {
                Text = _diagLog.ToText(),
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Menlo, monospace"),
                FontSize = 12,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            }
        };
        dlg.ShowDialog();
    }

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        SamedisStatusText.Text = $"Samedis: authentifiziere gegen {_cfg.Auth.Uri} (Timeout {_cfg.Http.TimeoutSeconds}s) ...";
        ActimedStatusText.Text = $"Actimed: warte auf Samedis ...";
        ToggleButtons(false);
        Rows.Clear();
        Customers.Clear();
        _diagLog.Entries.Clear();

        // 1) Samedis ----------------------------------------------------------------
        IReadOnlyList<UserTenantSummary> samedisTenants = Array.Empty<UserTenantSummary>();
        string samedisStatus;
        try
        {
            samedisTenants = await System.Threading.Tasks.Task.Run(() => FetchSamedisTenants(_cfg, _diagLog));
            samedisStatus = samedisTenants.Count == 0
                ? "Samedis: 0 Tenants geliefert. Diagnose öffnen, um den HTTP-Status zu sehen."
                : $"Samedis: {samedisTenants.Count} Tenant(s) geladen.";
        }
        catch (Exception ex)
        {
            _diagLog.Error("Samedis-Auth/-API fehlgeschlagen", ex);
            samedisStatus = $"Samedis: FEHLER - {ex.Message}";
        }
        SamedisStatusText.Text = samedisStatus;

        // 2) Actimed ----------------------------------------------------------------
        ActimedStatusText.Text = $"Actimed: lade Kunden aus '{_cfg.Actimed.DatabasePath}' ...";
        IReadOnlyList<ActimedCustomer> customers = Array.Empty<ActimedCustomer>();
        string actimedStatus;
        try
        {
            customers = await System.Threading.Tasks.Task.Run(() => FetchActimedCustomers(_cfg, _diagLog));
            actimedStatus = customers.Count == 0
                ? $"Actimed: 0 Kunden in '{_cfg.Actimed.DatabasePath}'. Diagnose öffnen für Details."
                : $"Actimed: {customers.Count} Kunde(n) aus '{_cfg.Actimed.DatabasePath}' gelesen.";
        }
        catch (Exception ex)
        {
            _diagLog.Error("Actimed-Repository fehlgeschlagen", ex);
            actimedStatus = $"Actimed: FEHLER - {ex.Message}";
        }
        ActimedStatusText.Text = actimedStatus;

        // 3) Tabellen befüllen -----------------------------------------------------
        foreach (var c in customers) Customers.Add(c);

        var existing = _cfg.Tenants
            .Where(t => !string.IsNullOrEmpty(t.SamedisTenantId))
            .ToDictionary(t => t.SamedisTenantId);

        foreach (var t in samedisTenants)
        {
            existing.TryGetValue(t.TenantId, out var local);
            Rows.Add(new TenantMappingRow
            {
                SamedisTenantId = t.TenantId,
                TenantDisplay   = string.IsNullOrWhiteSpace(t.Name) ? t.TenantId : t.Name,
                LocalName       = local?.Name ?? t.Name ?? t.TenantId,
                Enabled         = local?.Enabled ?? false,
                CustIdsText     = local == null ? string.Empty : string.Join(",", local.ActimedCustIds)
            });
        }

        // verwaiste lokale Tenants — die API liefert sie nicht, behalten wir aber
        foreach (var local in _cfg.Tenants)
        {
            if (samedisTenants.Any(t => t.TenantId == local.SamedisTenantId)) continue;
            Rows.Add(new TenantMappingRow
            {
                SamedisTenantId = local.SamedisTenantId,
                TenantDisplay   = $"(nur lokal) {local.Name}",
                LocalName       = local.Name,
                Enabled         = local.Enabled,
                CustIdsText     = string.Join(",", local.ActimedCustIds)
            });
        }

        ToggleButtons(true);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var newTenants = new List<TenantConfig>();
            foreach (var row in Rows)
            {
                if (string.IsNullOrWhiteSpace(row.SamedisTenantId)) continue;
                var ids = ParseCustIds(row.CustIdsText);
                newTenants.Add(new TenantConfig
                {
                    Name = string.IsNullOrWhiteSpace(row.LocalName) ? row.TenantDisplay : row.LocalName,
                    SamedisTenantId = row.SamedisTenantId,
                    ActimedCustIds = ids,
                    Enabled = row.Enabled
                });
            }

            _cfg.Tenants = newTenants;
            ConfigStore.Save(_configPath, _cfg);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Konnte config.yml nicht speichern:\n{ex.Message}",
                "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<int> ParseCustIds(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<int>();
        var parts = raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<int>();
        foreach (var p in parts)
            if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                result.Add(n);
        return result;
    }

    // --- Data fetchers ----------------------------------------------------

    private static IReadOnlyList<UserTenantSummary> FetchSamedisTenants(AppConfig cfg, ISyncLog log)
    {
        var http = cfg.Http.ToSettings();
        log.Info($"Authenticating against {cfg.Auth.Uri} as {cfg.Auth.ClientId}");
        var auth = new Authenticate(cfg.Auth.Uri, cfg.Auth.ClientId, cfg.Auth.ClientSecret, http, log);
        if (auth.StatusCode is < 200 or >= 300 || string.IsNullOrEmpty(auth.BearerToken))
            throw new InvalidOperationException(
                $"Authentifizierung fehlgeschlagen (HTTP {auth.StatusCode}). " +
                $"client_id/client_secret in config.yml prüfen.");
        log.Info($"Auth OK, user={auth.User}");

        log.Info($"Querying tenants list against {cfg.Samedis.Uri} (api {cfg.Samedis.ApiVersion})");
        var samedis = new RequestData(cfg.Samedis.Uri, auth.BearerToken, http, log);

        // Zwei Welten, zwei Quellen. Ein externer Dienstleister ist kein Mitglied der
        // Kundenmandanten — /user/tenants liefert ihm deshalb seine eigene Service-Welt,
        // nicht seine Kunden. Die stehen unter enterprise/tenants/{id}/clients.
        if (cfg.Samedis.IsEnterprise)
        {
            log.Info($"Service-Welt {cfg.Samedis.EnterpriseTenantId}: lade Kundenliste.");
            return EnterpriseClients
                .List(samedis, cfg.Samedis.ApiVersion, cfg.Samedis.EnterpriseTenantId, log)
                .Select(c => new UserTenantSummary(c.TenantId, c.Name))
                .ToList();
        }

        log.Info("Direkter Zugriff: lade die Mandanten des Accounts.");
        return Tenant.ListUserTenants(samedis, cfg.Samedis.ApiVersion, log);
    }

    private static IReadOnlyList<ActimedCustomer> FetchActimedCustomers(AppConfig cfg, ISyncLog log)
    {
        var path = cfg.Actimed.DatabasePath;
        log.Info($"Actimed DB path: '{path}'");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("actimed.database_path ist nicht gesetzt.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Actimed-DB nicht gefunden: {path}");

        IActimedRepository repo;
        if (path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
        {
            log.Info("Using SqliteActimedRepository (.sqlite mirror)");
            repo = new SqliteActimedRepository(path);
        }
        else if (OperatingSystem.IsWindows())
        {
            log.Info("Using OleDbActimedRepository (Microsoft.ACE.OLEDB.16.0)");
            repo = new OleDbActimedRepository(path);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "MDB-Lesezugriff nur unter Windows; auf macOS/Linux einen .sqlite-Spiegel angeben.");
        }

        var customers = repo.ListCustomers();
        log.Info($"A3_CUST: {customers.Count} Zeile(n)");
        return customers;
    }

    // --- UI helpers -------------------------------------------------------

    private void ToggleButtons(bool enabled)
    {
        SaveButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
        ReloadButton.IsEnabled = enabled;
        DiagButton.IsEnabled = enabled;
    }
}

/// <summary>One row in the mapping grid.</summary>
public class TenantMappingRow : INotifyPropertyChanged
{
    private string _samedisTenantId = string.Empty;
    public string SamedisTenantId
    {
        get => _samedisTenantId;
        set { _samedisTenantId = value; OnChanged(nameof(SamedisTenantId)); }
    }

    private string _tenantDisplay = string.Empty;
    public string TenantDisplay
    {
        get => _tenantDisplay;
        set { _tenantDisplay = value; OnChanged(nameof(TenantDisplay)); }
    }

    private string _localName = string.Empty;
    public string LocalName
    {
        get => _localName;
        set { _localName = value; OnChanged(nameof(LocalName)); }
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnChanged(nameof(Enabled)); }
    }

    private string _custIdsText = string.Empty;
    public string CustIdsText
    {
        get => _custIdsText;
        set { _custIdsText = value; OnChanged(nameof(CustIdsText)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
