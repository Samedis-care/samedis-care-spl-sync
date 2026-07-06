using FluentAssertions;
using SamedisCare.SplSync.Core.Config;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class ConfigValidationTests
{
    private static TenantConfig Tenant(string id) => new() { Name = "Mandant", SamedisTenantId = id };

    [Fact]
    public void Accepts_valid_24_hex_objectid()
    {
        ConfigValidation.ValidateTenantId(Tenant("507f1f77bcf86cd799439011")).Should().BeNull();
    }

    [Fact]
    public void Rejects_empty_id()
    {
        ConfigValidation.ValidateTenantId(Tenant("")).Should().Contain("fehlt");
    }

    [Theory]
    [InlineData("<...>")]
    [InlineData("<24-stelliger ObjectId aus Samedis>")]
    public void Rejects_placeholder_with_angle_brackets(string id)
    {
        // Das ist der konkrete Bug: '<' und '>' sind unter Windows verbotene Pfadzeichen und
        // hätten sonst einen kryptischen "Syntax für den Dateinamen ist falsch"-Fehler ausgelöst.
        ConfigValidation.ValidateTenantId(Tenant(id)).Should().Contain("ungültige Zeichen");
    }

    [Theory]
    [InlineData("507f1f77bcf86cd79943901")]   // 23 Zeichen — zu kurz
    [InlineData("507f1f77bcf86cd799439011x")] // 25 Zeichen — zu lang
    [InlineData("507f1f77bcf86cd79943901g")]  // Nicht-Hex-Zeichen
    public void Rejects_ids_that_are_not_24_hex(string id)
    {
        ConfigValidation.ValidateTenantId(Tenant(id)).Should().Contain("gültige 24-stellige");
    }
}
