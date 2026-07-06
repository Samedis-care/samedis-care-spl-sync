using FluentAssertions;
using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class UploadIntervalTests
{
    [Theory]
    [InlineData("2023-12-18", "2025-12-18", 24)]  // Defi AED, Intervall 24
    [InlineData("2024-07-30", "2025-07-30", 12)]  // STK_HF, Intervall 12
    [InlineData("2024-11-22", "2024-12-22", 1)]   // 1 Monat
    public void MonthsBetween_derives_interval(string from, string to, int expected)
    {
        IntervalConversion.MonthsBetween(DateTime.Parse(from), DateTime.Parse(to)).Should().Be(expected);
    }

    [Fact]
    public void MonthsBetween_is_never_negative()
    {
        IntervalConversion.MonthsBetween(DateTime.Parse("2025-01-01"), DateTime.Parse("2024-01-01")).Should().Be(0);
    }

    [Fact]
    public void BuildServiceIntervals_emits_maintenance_entry_in_months()
    {
        var intervals = UploadEngine.BuildServiceIntervals(12, "MPBe_STK_HF_emed-100-014");

        intervals.Should().NotBeNull();
        intervals!.Should().HaveCount(1);
        var e = intervals![0];
        e["category"].Should().Be("maintenance");
        e["label"].Should().Be("MPBe_STK_HF_emed-100-014");
        e["value"].Should().Be(12);
        e["unit"].Should().Be("month");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void BuildServiceIntervals_returns_null_without_usable_months(int? months)
    {
        UploadEngine.BuildServiceIntervals(months, "x").Should().BeNull();
    }
}
