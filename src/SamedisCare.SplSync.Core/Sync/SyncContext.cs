using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Api;
using SamedisCare.Api.Auth;
using SamedisCare.Api.Http;
using SamedisCare.Api.Query;
using SamedisCare.Api.Routing;
using SamedisCare.Helper.Logging;
using SamedisCare.SplSync.Core.Config;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Per-tenant runtime context — composed once per sync cycle.
/// Holds the authenticated Samedis client, the tenant-specific URL prefix,
/// the Actimed repo handle, and convenience accessors for state/cursor.
/// </summary>
public class SyncContext
{
    public required AppConfig Config { get; init; }
    public required TenantConfig Tenant { get; init; }
    public required ISyncLog Log { get; init; }
    public required RequestData Samedis { get; init; }
    public required IActimedRepository Actimed { get; init; }
    public required StateDb State { get; init; }
    public required Cursor Cursor { get; init; }
    public required IssueLinkStore IssueLinks { get; init; }
    public required MaintenanceKindMapper KindMapper { get; init; }

    /// <summary>
    /// URL-Präfix, unter dem die Ressourcen dieses Mandanten liegen — die einzige Stelle,
    /// an der sich Direktzugriff und Service-Welt unterscheiden:
    ///
    /// <code>
    /// tenant     /api/v4/tenants/{mandant}
    /// enterprise /api/v4/enterprise/tenants/{dienstleister}/clients/{mandant}
    /// </code>
    ///
    /// Baut auf der geteilten <see cref="ITenantScope"/> aus <c>SamedisCare.Api</c> auf —
    /// dieselbe Präfix-Quelle wie external-sync und fluke-sync. Die Ressourcen darunter
    /// heissen gleich und akzeptieren dieselben Felder; Interpolation
    /// (<c>$"{Scope}/issues"</c>) ruft <see cref="object.ToString"/> und liefert den Präfix.
    /// </summary>
    public ITenantScope Scope => Config.Samedis.IsEnterprise
        ? TenantScope.Enterprise(Config.Samedis.EnterpriseTenantId, Tenant.SamedisTenantId, Config.Samedis.ApiVersion)
        : TenantScope.Standard(Tenant.SamedisTenantId, Config.Samedis.ApiVersion);
}

/// <summary>
/// Builds a SyncContext for a tenant: authenticate, set up the Actimed repo, etc.
/// </summary>
public static class SyncContextBuilder
{
    public static SyncContext Build(
        AppConfig config,
        TenantConfig tenant,
        IActimedRepository actimed,
        StateDb state,
        ISyncLog log)
    {
        var http = config.Http.ToSettings();
        var auth = new Authenticate(config.Auth.Uri, config.Auth.ClientId, config.Auth.ClientSecret, http, log);
        if (auth.StatusCode is < 200 or >= 300 || string.IsNullOrEmpty(auth.BearerToken))
            throw new InvalidOperationException($"Authentication failed (status={auth.StatusCode}). Check auth.* in config.yml.");

        var samedis = new RequestData(config.Samedis.Uri, auth.BearerToken, http, log);
        var cursor = new Cursor(state);
        var issueLinks = new IssueLinkStore(state);
        var mapper = new MaintenanceKindMapper(config.MaintenanceKindMapping);

        return new SyncContext
        {
            Config = config,
            Tenant = tenant,
            Log = log,
            Samedis = samedis,
            Actimed = actimed,
            State = state,
            Cursor = cursor,
            IssueLinks = issueLinks,
            KindMapper = mapper
        };
    }
}
