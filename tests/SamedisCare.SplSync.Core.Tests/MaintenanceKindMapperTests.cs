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

    [Fact]
    public void Resolve_carries_test_spec_and_interval_for_auto_create()
    {
        var mapper = new MaintenanceKindMapper(new[]
        {
            new MaintenanceKindMappingEntry
            {
                Match = "(?i)dguv",
                ActimedKind = "MPBe_§11_STK/DGUV V3",
                ActimedTestSpecName = "EN50699_0702_SKI_ErsatzMessung_allg_Grenzwerte",
                ActimedActivityIntervalMonths = 24
            }
        });

        var match = mapper.Resolve(null, "STK nach DGUV V3", null);

        match.ActimedKind.Should().Be("MPBe_§11_STK/DGUV V3");
        match.ActimedTestSpecName.Should().Be("EN50699_0702_SKI_ErsatzMessung_allg_Grenzwerte");
        match.IntervalMonths.Should().Be(24);
        match.ActimedActivityName.Should().BeNull();
    }
}
