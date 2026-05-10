using System.IO;
using System.Windows;
using SamedisCare.SplSync.Core.Config;
using SamedisCare.SplSync.Tray.Views;
// Disambiguate names that exist in both WPF and WinForms namespaces (we use both
// because the tray icon needs WinForms NotifyIcon while the dialogs are WPF).
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;
using NotifyIcon  = System.Windows.Forms.NotifyIcon;
using Forms       = System.Windows.Forms;
using Drawing     = System.Drawing;

namespace SamedisCare.SplSync.Tray;

/// <summary>
/// Entry point of the Tray app.
/// Hosts a NotifyIcon (System.Windows.Forms) so we can sit in the system tray cleanly,
/// and only shows a window when the user opens it.
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private InProcessSyncHost? _syncHost;
    private TrayStatus _status = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Sicherheitsnetz: jede unhandled exception sichtbar machen statt App still zu killen.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show(
                $"Unbehandelter Fehler in der UI:\n\n{args.Exception.GetType().Name}: {args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "SamedisCare SplSync — Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show(
                    $"Unbehandelter Hintergrundfehler:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "SamedisCare SplSync — Hintergrundfehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            MessageBox.Show(
                $"Unobservierte Task-Exception:\n\n{args.Exception.GetType().Name}: {args.Exception.Message}",
                "SamedisCare SplSync — Task-Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        var configPath = ResolveConfigPath(e.Args);

        // First-Run-Bootstrap: wenn config.yml fehlt, aus eingebetteter Resource erzeugen.
        // Das macht die EXE selbst-genügsam — Operations muss keine Begleitdatei beilegen.
        var firstRun = false;
        try
        {
            firstRun = ConfigBootstrap.EnsureConfigExists(configPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Konfiguration konnte nicht initialisiert werden:\n{ex.Message}\n\nPfad: {configPath}",
                "SamedisCare SplSync",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        AppConfig? cfg = null;
        try { cfg = ConfigStore.Load(configPath); }
        catch (Exception ex) { MessageBox.Show($"config.yml konnte nicht geladen werden:\n{ex.Message}", "SamedisCare SplSync"); }

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "SamedisCare SplSync",
            Icon = LoadIcon("samedis-logo.ico")
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMain(configPath);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Jetzt synchronisieren", null, (_, _) => _syncHost?.RunOnceAsync());
        menu.Items.Add("Mandanten zuordnen...", null, (_, _) => ShowTenantMapping(configPath));
        menu.Items.Add("Übersicht öffnen...", null, (_, _) => ShowMain(configPath));
        menu.Items.Add("Einstellungen...", null, (_, _) => ShowMain(configPath, openSettingsTab: true));
        menu.Items.Add("config.yml im Editor öffnen...", null, (_, _) => OpenConfig(configPath));
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Autostart toggle (checkmark reflects current state)
        var autostartItem = new Forms.ToolStripMenuItem("Mit Windows starten")
        {
            CheckOnClick = false, // we handle the toggle ourselves so we can persist + show errors
            Checked = SafeIsAutostartEnabled()
        };
        autostartItem.Click += (_, _) => ToggleAutostart(configPath, autostartItem);
        menu.Items.Add(autostartItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ShutdownApp());
        _notifyIcon.ContextMenuStrip = menu;

        if (cfg != null)
        {
            _syncHost = new InProcessSyncHost(configPath, _status);
            _syncHost.OnStatusChanged += UpdateTrayIcon;
            _syncHost.OnLockBlocked += ShowLockDialog;
            _syncHost.Start();

            // First-launch: prompt the user about Autostart, but only once.
            MaybePromptAutostart(configPath, cfg, autostartItem);
        }

        UpdateTrayIcon(_status);

        // First-Run: Settings-Dialog automatisch öffnen, damit der User die Defaults
        // (auth, samedis, actimed.database_path, mandanten...) gleich angepasst bekommt.
        // Erst NACH UpdateTrayIcon, damit das Tray-Icon schon sichtbar ist falls der
        // User abbricht — sonst wäre die App "weg" und die EXE laeuft nur im Hintergrund.
        if (firstRun)
        {
            MessageBox.Show(
                "Willkommen bei SamedisCare SplSync.\n\n" +
                "Es wurde eine neue config.yml mit Standardwerten angelegt. Bitte\n" +
                "fülle jetzt die Konfiguration aus (Authentifizierung, Actimed-Pfad,\n" +
                "Mandanten-Zuordnung). Du kannst diesen Dialog später jederzeit über\n" +
                "das Tray-Symbol wieder öffnen.",
                "SamedisCare SplSync - Erster Start",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ShowMain(configPath, openSettingsTab: true);
        }
    }

    // -----------------------------------------------------------------------------
    // Autostart wiring
    // -----------------------------------------------------------------------------

    private static bool SafeIsAutostartEnabled()
    {
        try { return AutostartHelper.IsEnabled(); }
        catch { return false; }
    }

    /// <summary>
    /// Asks the user once whether SamedisCare SplSync should start with Windows.
    /// Either decision marks `app.autostart_prompted = true` so we never ask again;
    /// the user can still toggle later via the tray menu.
    /// </summary>
    private void MaybePromptAutostart(string configPath, AppConfig cfg, Forms.ToolStripMenuItem autostartItem)
    {
        if (cfg.App.AutostartPrompted) return;
        if (SafeIsAutostartEnabled()) { PersistPromptDone(configPath, cfg); return; }

        var answer = MessageBox.Show(
            "Soll SamedisCare SplSync bei jedem Windows-Start automatisch geladen werden?\n\n" +
            "Empfohlen: Ja. Damit ist der Sync immer aktiv, ohne dass du daran denken musst.\n\n" +
            "Du kannst das jederzeit über das Tray-Menue (Rechtsklick auf das Icon) wieder ändern.",
            "SamedisCare SplSync - Autostart",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                var exe = AutostartHelper.CurrentExePath();
                if (string.IsNullOrEmpty(exe))
                    throw new InvalidOperationException("EXE-Pfad konnte nicht ermittelt werden.");
                AutostartHelper.Enable(exe);
                autostartItem.Checked = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Konnte Autostart nicht aktivieren:\n{ex.Message}",
                    "Autostart - Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        PersistPromptDone(configPath, cfg);
    }

    private static void PersistPromptDone(string configPath, AppConfig cfg)
    {
        try
        {
            cfg.App.AutostartPrompted = true;
            ConfigStore.Save(configPath, cfg);
        }
        catch
        {
            // best-effort: nicht kritisch wenn der Save fehlschlaegt — wir fragen halt nochmal
        }
    }

    /// <summary>Tray-Menu-Toggle: aktiv -> deaktivieren, inaktiv -> aktivieren.</summary>
    private static void ToggleAutostart(string configPath, Forms.ToolStripMenuItem item)
    {
        try
        {
            if (AutostartHelper.IsEnabled())
            {
                AutostartHelper.Disable();
                item.Checked = false;
            }
            else
            {
                var exe = AutostartHelper.CurrentExePath()
                          ?? throw new InvalidOperationException("EXE-Pfad konnte nicht ermittelt werden.");
                AutostartHelper.Enable(exe);
                item.Checked = true;
            }

            // Markiere als 'einmal gefragt', damit der initial-Prompt nicht mehr aufpoppt.
            try
            {
                var cfg = ConfigStore.Load(configPath);
                if (!cfg.App.AutostartPrompted)
                {
                    cfg.App.AutostartPrompted = true;
                    ConfigStore.Save(configPath, cfg);
                }
            }
            catch { /* best-effort */ }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Autostart konnte nicht umgeschaltet werden:\n{ex.Message}",
                "Autostart - Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Wird vom MainWindow aufgerufen, wenn der User den "Jetzt synchronisieren"-Button
    /// in der Übersicht klickt. Triggert den InProcessSyncHost — derselbe Pfad wie das
    /// gleichnamige Tray-Menü-Item. Liefert true, wenn der Sync angestoßen wurde, false
    /// wenn der Host nicht initialisiert ist (z. B. config.yml fehlerhaft).
    /// </summary>
    public bool TriggerSyncNow()
    {
        if (_syncHost == null) return false;
        _syncHost.RunOnceAsync();
        return true;
    }

    /// <summary>
    /// Nach einem Save in den Einstellungen aufzurufen. Re-initialisiert dynamische Komponenten
    /// (insbesondere PDF-Pickup-Watcher), damit Config-Aenderungen sofort aktiv werden. Settings,
    /// die in den periodischen Loops eh pro Iteration neu gelesen werden (Intervalle,
    /// Tenant-Mapping, Kind-Mapping, Auth, …), brauchen diesen Aufruf nicht.
    /// </summary>
    public void NotifyConfigChanged()
    {
        _syncHost?.ReloadDynamicComponents();
    }

    private void ShowTenantMapping(string configPath)
    {
        try
        {
            var cfg = ConfigStore.Load(configPath);
            var dlg = new Dialogs.TenantMappingDialog(configPath, cfg);
            dlg.ShowDialog();
            // Kein Stop/Start - die Loops in InProcessSyncHost rufen ConfigStore.Load()
            // bei jeder Iteration neu auf, neue Mappings sind beim nächsten Tick automatisch wirksam.
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Mandanten-Dialog konnte nicht geöffnet werden:\n{ex.Message}");
        }
    }

    private void ShowMain(string configPath, bool openSettingsTab = false)
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(configPath, _status);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Activate();
        }
        if (openSettingsTab) _mainWindow.SwitchToSettingsTab();
    }

    private static void OpenConfig(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Konnte config.yml nicht öffnen:\n{ex.Message}"); }
    }

    private readonly Dictionary<string, Drawing.Icon> _iconCache = new();

    /// <summary>
    /// Loads an embedded .ico resource (under /assets/) as a WinForms Drawing.Icon
    /// for the tray NotifyIcon. Results are cached per asset name so we are not
    /// re-decoding on every status update.
    /// </summary>
    private Drawing.Icon LoadIcon(string assetName)
    {
        if (_iconCache.TryGetValue(assetName, out var cached)) return cached;
        try
        {
            var uri = new Uri($"pack://application:,,,/assets/{assetName}", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
            {
                var icon = new Drawing.Icon(stream);
                _iconCache[assetName] = icon;
                return icon;
            }
        }
        catch
        {
            // fall through to system icon below
        }
        var fallback = Drawing.SystemIcons.Information;
        _iconCache[assetName] = fallback;
        return fallback;
    }

    private void UpdateTrayIcon(TrayStatus status)
    {
        if (_notifyIcon == null) return;
        // Pick the tray icon based on status:
        //   Error -> red logo (samedis-logo-error.ico)
        //   Warn  -> amber logo (samedis-logo-warn.ico)
        //   else  -> default logo
        var asset = status.State switch
        {
            SyncState.Error => "samedis-logo-error.ico",
            SyncState.Warn  => "samedis-logo-warn.ico",
            _               => "samedis-logo.ico"
        };
        _notifyIcon.Icon = LoadIcon(asset);
        var text = $"SamedisCare SplSync - {status.State}: {status.Message}";
        _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 60) + "..." : text;
    }

    private void ShowLockDialog()
    {
        Dispatcher.Invoke(() =>
        {
            var dlg = new Dialogs.LockDialog();
            dlg.ShowDialog();
            // After the user confirms, the worker retries on its next tick.
        });
    }

    private void ShutdownApp()
    {
        _notifyIcon?.Dispose();
        _syncHost?.Stop();
        Shutdown();
    }

    private static string ResolveConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--config") return args[i + 1];
        var env = Environment.GetEnvironmentVariable("SPLSYNC_CONFIG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(AppContext.BaseDirectory, "config.yml");
    }
}
