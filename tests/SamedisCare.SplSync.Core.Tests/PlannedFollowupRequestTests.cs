using FluentAssertions;
using Newtonsoft.Json.Linq;
using SamedisCare.Api.Routing;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

/// <summary>
/// The follow-up goes through the same endpoint the frontend uses when a task is closed, so
/// Samedis links it to the finished task and copies device, title, services and intervals.
/// </summary>
public class PlannedFollowupRequestTests
{
    [Fact]
    public void The_follow_up_is_posted_under_the_finished_task()
    {
        UploadEngine.NextEventResource(TenantScope.Standard("t1"), "i1")
            .Should().Be("/api/v4/tenants/t1/issues/i1/next_events");
    }

    [Fact]
    public void The_enterprise_scope_uses_the_same_nesting()
    {
        UploadEngine.NextEventResource(TenantScope.Enterprise("t1", "c1"), "i1")
            .Should().Be("/api/v4/enterprise/tenants/t1/clients/c1/issues/i1/next_events");
    }

    [Fact]
    public void The_envelope_carries_only_the_date()
    {
        var body = JObject.Parse(UploadEngine.BuildNextEventEnvelope(new DateTime(2026, 9, 1)));

        body.Should().BeEquivalentTo(JObject.Parse("""{ "data": { "date": "2026-09-01" } }"""));
    }

    // AddMonths clamps to the last day of the month; the API must see that day, not an overflow.
    [Fact]
    public void A_month_end_test_date_stays_in_the_target_month()
    {
        var body = JObject.Parse(UploadEngine.BuildNextEventEnvelope(new DateTime(2026, 1, 31).AddMonths(1)));

        body["data"]!["date"]!.ToString().Should().Be("2026-02-28");
    }

    [Fact]
    public void An_existing_follow_up_is_read_from_next_issue_id()
    {
        const string json = """
        { "data": { "id": "i1", "type": "issues", "attributes": { "next_issue_id": "i2" } } }
        """;

        UploadEngine.ExistingFollowupId(json).Should().Be("i2");
    }

    [Theory]
    [InlineData("""{ "data": { "id": "i1", "type": "issues", "attributes": { "next_issue_id": null } } }""")]
    [InlineData("""{ "data": { "id": "i1", "type": "issues", "attributes": { } } }""")]
    [InlineData("")]
    public void Without_next_issue_id_there_is_no_follow_up(string json)
    {
        UploadEngine.ExistingFollowupId(json).Should().BeNull();
    }
}
