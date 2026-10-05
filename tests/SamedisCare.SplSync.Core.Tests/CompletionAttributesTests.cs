using FluentAssertions;
using SamedisCare.SplSync.Core.Actimed;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

/// <summary>
/// What completing a planned task writes. The task already carries its maintenance type and
/// interval; the Actimed test spec name must not replace them, or the linked follow-up
/// inherits the spec name (samedis-care-issues#2650).
/// </summary>
public class CompletionAttributesTests
{
    private static ActimedFinishedTest Test(string result = "bestanden", string tester = "Anna Prüfer") => new(
        TestId: "T1", DevId: 1, TestDate: new DateTime(2026, 10, 5), TesterName: tester,
        PvsName: "MPBe_STK_HF_emed-100-014", Pruefberichtsnummer: "PB-1", Pruefergebnis: result,
        LastTestDate: null, NextTestDate: null, Memo: "ok", ModifyTime: DateTime.UtcNow);

    [Fact]
    public void Completion_leaves_maintenance_type_and_intervals_alone()
    {
        var attrs = UploadEngine.BuildCompletionAttributes(Test(), "Servicetechniker", limitedUseOnFail: false);

        attrs.Should().NotContainKey("services");
        attrs.Should().NotContainKey("title");
        attrs.Should().NotContainKey("with_service_intervals");
    }

    [Fact]
    public void Completion_carries_result_tester_and_day()
    {
        var attrs = UploadEngine.BuildCompletionAttributes(Test(), "Servicetechniker", limitedUseOnFail: false);

        attrs["test_result"].Should().Be("passed");
        attrs["maintenance_passed"].Should().Be(true);
        attrs["maintenance_performer"].Should().Be("Anna Prüfer");
        attrs["date"].Should().Be("2026-10-05");
        attrs["done_at"].Should().Be("2026-10-05");
        attrs["external_id"].Should().Be("PB-1");
        attrs["test_comment"].Should().Be("ok");
        attrs["inventory_operation_status"].Should().Be("active");
    }

    [Fact]
    public void Without_a_tester_the_configured_default_is_used()
        => UploadEngine.BuildCompletionAttributes(Test(tester: ""), "Servicetechniker", limitedUseOnFail: false)
            ["maintenance_performer"].Should().Be("Servicetechniker");

    [Fact]
    public void A_failed_test_limits_use_only_when_configured()
    {
        UploadEngine.BuildCompletionAttributes(Test("nicht bestanden"), "x", limitedUseOnFail: true)
            ["inventory_operation_status"].Should().Be("limited_use");
        UploadEngine.BuildCompletionAttributes(Test("nicht bestanden"), "x", limitedUseOnFail: false)
            ["inventory_operation_status"].Should().Be("active");
    }

    // A task created from Actimed has no maintenance type yet; that path names it after the spec.
    [Fact]
    public void A_task_created_from_actimed_is_named_after_the_test_spec()
    {
        UploadEngine.ServiceName(Test()).Should().Be("MPBe_STK_HF_emed-100-014");
        UploadEngine.ServiceName(Test() with { PvsName = " " }).Should().Be("Wartung/Pruefung");
    }
}
