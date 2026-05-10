using FluentAssertions;
using SamedisCare.SplSync.Core.Actimed;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class OaDateTests
{
    [Fact]
    public void Parses_OA_double_to_DateTime()
    {
        // 45278 = 2023-12-18 in OA-Date.
        OaDate.TryParseOaDate(45278d).Should().Be(new DateTime(2023, 12, 18));
    }

    [Fact]
    public void Parses_german_comma_decimal()
    {
        OaDate.TryParseDecimalLoose("132,3").Should().BeApproximately(132.3, 1e-9);
        OaDate.TryParseDecimalLoose("0,49").Should().BeApproximately(0.49, 1e-9);
    }

    [Fact]
    public void Returns_null_for_garbage()
    {
        OaDate.TryParseDecimalLoose("--").Should().BeNull();
        OaDate.TryParseDecimalLoose(null).Should().BeNull();
    }
}
