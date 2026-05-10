using FluentAssertions;
using SamedisCare.SplSync.Core.Config;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class MaintenanceKindMapperTests
{
    private static MaintenanceKindMapper Default() => new(new[]
    {
        new MaintenanceKindMappingEntry { Match = "(?i)dguv\\s*v?3|stk.*§11", ActimedKind = "MPBe_§11_STK/DGUV V3" },
        new MaintenanceKindMappingEntry { Match = "(?i)mtk.*bdm|blutdruck",   ActimedKind = "MPBe_MTK_BDM" },
        new MaintenanceKindMappingEntry { Match = "(?i)defi.*aed",            ActimedKind = "MPBe_STK Defi (AED)" },
        new MaintenanceKindMappingEntry { Match = null,                       ActimedKind = "MPBe_§7_Wartung/Inspektion" }
    });

    [Theory]
    [InlineData("STK nach DGUV V3",         null,            "MPBe_§11_STK/DGUV V3")]
    [InlineData("MTK BDM 24 Monate",        null,            "MPBe_MTK_BDM")]
    [InlineData("Blutdruckmessgerät MTK",   null,            "MPBe_MTK_BDM")]
    [InlineData("Defi AED Funktionsprüfung", "maintenance",  "MPBe_STK Defi (AED)")]
    [InlineData("Allgemeine Wartung",       null,            "MPBe_§7_Wartung/Inspektion")]
    [InlineData("",                          null,           "MPBe_§7_Wartung/Inspektion")]
    public void Maps_titles_to_actimed_kind(string title, string? mtype, string expected)
    {
        var mapper = Default();
        mapper.Map(mtype, title, null).Should().Be(expected);
    }
}
