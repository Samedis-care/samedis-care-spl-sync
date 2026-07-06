using FluentAssertions;
using Newtonsoft.Json;
using SamedisCare.SplSync.Core.Api;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class IssuesModelTests
{
    [Fact]
    public void Deserializes_with_service_intervals_value_and_unit()
    {
        // Vertrag für den Download: das Wartungsintervall kommt als with_service_intervals[]
        // (value + unit day/week/month/year), siehe samedis-public.yaml.
        const string json = """
        { "data": [ { "id": "abc", "type": "issues",
          "attributes": { "issue_type": "maintenance", "title": "STK nach DGUV V3",
            "with_service_intervals": [
              { "category": "maintenance", "label": "STK", "value": 2, "unit": "year" }
            ] } } ] }
        """;

        var root = JsonConvert.DeserializeObject<Issues.Root>(json);

        var si = root!.Data![0].Attributes!.WithServiceIntervals;
        si.Should().HaveCount(1);
        si![0].Value.Should().Be(2);
        si[0].Unit.Should().Be("year");
        si[0].Category.Should().Be("maintenance");
    }

    [Fact]
    public void With_service_intervals_is_null_when_absent()
    {
        const string json = """
        { "data": [ { "id": "abc", "type": "issues",
          "attributes": { "issue_type": "maintenance", "title": "Wartung" } } ] }
        """;

        var root = JsonConvert.DeserializeObject<Issues.Root>(json);
        root!.Data![0].Attributes!.WithServiceIntervals.Should().BeNull();
    }

    [Fact]
    public void PickServiceInterval_prefers_maintenance_category()
    {
        var all = new List<Issues.ServiceInterval>
        {
            new() { Category = "inspection", Label = "SIP", Value = 6, Unit = "month" },
            new() { Category = "maintenance", Label = "STK", Value = 2, Unit = "year" },
        };

        var picked = DownloadEngine.PickServiceInterval(all, services: null, title: null);
        picked!.Category.Should().Be("maintenance");
        picked.Value.Should().Be(2);
    }

    [Fact]
    public void PickServiceInterval_matches_by_label_against_services()
    {
        var all = new List<Issues.ServiceInterval>
        {
            new() { Category = "maintenance", Label = "MTK", Value = 24, Unit = "month" },
            new() { Category = "maintenance", Label = "STK", Value = 12, Unit = "month" },
        };

        var picked = DownloadEngine.PickServiceInterval(all, services: new[] { "STK" }, title: "STK nach DGUV V3");
        picked!.Label.Should().Be("STK");
        picked.Value.Should().Be(12);
    }

    [Fact]
    public void PickServiceInterval_returns_null_when_empty()
    {
        DownloadEngine.PickServiceInterval(null, null, null).Should().BeNull();
        DownloadEngine.PickServiceInterval(new List<Issues.ServiceInterval>(), null, null).Should().BeNull();
    }
}
