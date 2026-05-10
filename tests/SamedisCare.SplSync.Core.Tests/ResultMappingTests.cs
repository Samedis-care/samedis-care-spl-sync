using FluentAssertions;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class ResultMappingTests
{
    [Theory]
    [InlineData("bestanden",            "passed")]
    [InlineData("OK",                   "passed")]
    [InlineData("i.O.",                 "passed")]
    [InlineData("bedingt bestanden",    "passed_conditionally")]
    [InlineData("nicht bestanden",      "not_passed")]
    [InlineData("durchgefallen",        "not_passed")]
    [InlineData("nicht i.O.",           "not_passed")]
    public void Maps_known_actimed_results(string input, string expected)
        => ResultMapping.FromActimed(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pizza")]
    public void Returns_null_for_unknown(string? input)
        => ResultMapping.FromActimed(input).Should().BeNull();
}
