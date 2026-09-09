using FluentAssertions;
using SamedisCare.SplSync.Core.Config;
using SamedisCare.SplSync.Core.Sync;
using SamedisCare.Helper.Logging;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

/// <summary>
/// Der Zugriffsweg entscheidet über den URL-Präfix — ein Fehler darin äussert sich als 404,
/// was sich wie „keine Daten" liest und nicht wie „falsch konfiguriert". Beide Pfadformen sind
/// hier festgenagelt. Baut wie fluke-sync/external-sync auf der geteilten <c>ITenantScope</c> auf.
/// </summary>
public class AccessModeTests
{
    private const string Client = "507f1f77bcf86cd799439011";
    private const string Enterprise = "6835b1f77bcf86cd79943902";

    private static SyncContext Context(string mode, string enterpriseId = "")
    {
        var cfg = new AppConfig();
        cfg.Samedis.ApiVersion = "v4";
        cfg.Samedis.AccessMode = mode;
        cfg.Samedis.EnterpriseTenantId = enterpriseId;

        var state = new StateDb(Path.Combine(Path.GetTempPath(), $"scope-{Guid.NewGuid():N}.sqlite"));
        return new SyncContext
        {
            Config = cfg,
            Tenant = new TenantConfig { Name = "Kunde", SamedisTenantId = Client },
            Log = new ConsoleSyncLog(0),
            Samedis = null!,
            Actimed = null!,
            State = state,
            Cursor = new Cursor(state),
            IssueLinks = new IssueLinkStore(state),
            KindMapper = new MaintenanceKindMapper(Array.Empty<MaintenanceKindMappingEntry>())
        };
    }

    [Fact]
    public void Direct_access_addresses_the_tenant_itself()
        => Context(AccessModes.Tenant).Scope.Root.Should().Be($"/api/v4/tenants/{Client}");

    [Fact]
    public void Enterprise_access_addresses_the_client_below_the_service_world()
        => Context(AccessModes.Enterprise, Enterprise).Scope.Root
            .Should().Be($"/api/v4/enterprise/tenants/{Enterprise}/clients/{Client}");

    [Fact]
    public void Access_mode_is_case_insensitive()
    {
        var cfg = new AppConfig();
        cfg.Samedis.AccessMode = "Enterprise";
        cfg.Samedis.IsEnterprise.Should().BeTrue();
    }

    [Fact]
    public void Tenant_is_the_default()
        => new AppConfig().Samedis.IsEnterprise.Should().BeFalse();

    // --- Validierung ---------------------------------------------------------------

    private static AppConfig Config(string mode, string enterpriseId = "")
    {
        var cfg = new AppConfig();
        cfg.Samedis.AccessMode = mode;
        cfg.Samedis.EnterpriseTenantId = enterpriseId;
        return cfg;
    }

    [Fact]
    public void Enterprise_without_a_service_world_is_rejected()
        => ConfigValidation.ValidateAccessMode(Config(AccessModes.Enterprise))
            .Should().ContainSingle().Which.Should().Contain("enterprise_tenant_id");

    [Fact]
    public void A_malformed_service_world_id_is_rejected()
        => ConfigValidation.ValidateAccessMode(Config(AccessModes.Enterprise, "<...>"))
            .Should().ContainSingle().Which.Should().Contain("enterprise_tenant_id");

    [Fact]
    public void A_stray_service_world_id_in_direct_mode_is_reported_as_ineffective()
        => ConfigValidation.ValidateAccessMode(Config(AccessModes.Tenant, Enterprise))
            .Should().ContainSingle().Which.Should().Contain("wirkungslos");

    [Fact]
    public void An_unknown_mode_names_the_allowed_values()
        => ConfigValidation.ValidateAccessMode(Config("serviceworld"))
            .Should().ContainSingle().Which.Should().Contain("tenant");

    [Fact]
    public void A_correct_setup_passes()
    {
        ConfigValidation.ValidateAccessMode(Config(AccessModes.Tenant)).Should().BeEmpty();
        ConfigValidation.ValidateAccessMode(Config(AccessModes.Enterprise, Enterprise)).Should().BeEmpty();
    }
}
