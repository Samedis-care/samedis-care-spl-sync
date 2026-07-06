using FluentAssertions;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class IntervalConversionTests
{
    [Theory]
    [InlineData(12, "month", 12)]
    [InlineData(24, "months", 24)]
    [InlineData(1, "year", 12)]
    [InlineData(2, "years", 24)]
    [InlineData(1, "jahr", 12)]
    [InlineData(6, "monat", 6)]
    public void Converts_month_and_year_units(int value, string unit, int expected)
    {
        IntervalConversion.ToMonths(value, unit).Should().Be(expected);
    }

    [Theory]
    [InlineData(30, "day", 1)]     // 30/30 = 1
    [InlineData(90, "days", 3)]    // 90/30 = 3
    [InlineData(1, "day", 1)]      // rundet auf, Minimum 1 Monat
    [InlineData(4, "week", 1)]     // 28/30 -> ~0.93 -> gerundet 1
    [InlineData(9, "weeks", 2)]    // 63/30 = 2.1 -> 2
    public void Converts_day_and_week_units_with_min_one_month(int value, string unit, int expected)
    {
        IntervalConversion.ToMonths(value, unit).Should().Be(expected);
    }

    [Fact]
    public void Missing_or_unknown_unit_is_treated_as_months()
    {
        IntervalConversion.ToMonths(18, null).Should().Be(18);
        IntervalConversion.ToMonths(18, "").Should().Be(18);
        IntervalConversion.ToMonths(18, "quarter").Should().Be(18);
    }

    [Theory]
    [InlineData(null, "month")]
    [InlineData(0, "month")]
    [InlineData(-3, "year")]
    public void No_usable_amount_returns_null(int? value, string unit)
    {
        IntervalConversion.ToMonths(value, unit).Should().BeNull();
    }

    [Theory]
    [InlineData("d", IntervalConversion.Unit.Day)]
    [InlineData("Woche", IntervalConversion.Unit.Week)]
    [InlineData("MONTHS", IntervalConversion.Unit.Month)]
    [InlineData("Jährlich", IntervalConversion.Unit.Year)]
    [InlineData("bogus", IntervalConversion.Unit.Unknown)]
    public void ParseUnit_is_case_and_language_tolerant(string raw, IntervalConversion.Unit expected)
    {
        IntervalConversion.ParseUnit(raw).Should().Be(expected);
    }
}
