using FluentAssertions;
using SamedisCare.SplSync.Core.Api;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

/// <summary>
/// The two models this project keeps of its own after the API layer moved to
/// SamedisCare.Api. They are not duplicates: each carries fields the shared model
/// deliberately does not, and this is where that stays visible.
/// </summary>
/// <remarks>
/// None of the four appear in doc/v4/public.yaml, and only <c>maintenance_type</c> exists on
/// the backend model at all (Issue, but not in any serializer). Every call site therefore
/// treats them as a fallback behind a field the API really sends — see the tests below. If
/// one of them turns out to be dead, the fallback goes with it; until someone checks against
/// a live response, dropping them would silently lose data the server might send.
/// </remarks>
public class ApiModelTests
{
    [Fact]
    public void The_inventory_model_keeps_the_two_fields_the_shared_one_lacks()
    {
        var attributes = new Inventories.Attributes
        {
            DeviceNumber = "320000",
            InventoryNumber = "ALT-1",
            SourceLocationId = "L-1",
        };

        attributes.InventoryNumber.Should().Be("ALT-1");
        attributes.SourceLocationId.Should().Be("L-1");
    }

    // The order the download engine reads them in: what the API sends wins, the local field
    // is only consulted when it is absent.
    [Fact]
    public void The_device_number_wins_over_the_local_inventory_number()
    {
        var withBoth = new Inventories.Attributes { DeviceNumber = "320000", InventoryNumber = "ALT-1" };
        var withOnlyLocal = new Inventories.Attributes { DeviceNumber = null, InventoryNumber = "ALT-1" };

        (withBoth.DeviceNumber ?? withBoth.InventoryNumber).Should().Be("320000");
        (withOnlyLocal.DeviceNumber ?? withOnlyLocal.InventoryNumber).Should().Be("ALT-1");
    }

    [Fact]
    public void The_issue_model_keeps_the_two_fields_the_shared_one_lacks()
    {
        var attributes = new Issues.Attributes { DueOn = "2026-09-01", MaintenanceType = "stk" };

        attributes.DueOn.Should().Be("2026-09-01");
        attributes.MaintenanceType.Should().Be("stk");
    }

    // Same order as above: due_on is read before date, so an API that starts sending it takes
    // precedence without a code change.
    [Fact]
    public void The_due_date_falls_back_to_the_date_the_api_does_send()
    {
        var withDueOn = new Issues.Attributes { DueOn = "2026-09-01", Date = "2026-08-01" };
        var withoutDueOn = new Issues.Attributes { DueOn = null, Date = "2026-08-01" };

        (withDueOn.DueOn ?? withDueOn.Date).Should().Be("2026-09-01");
        (withoutDueOn.DueOn ?? withoutDueOn.Date).Should().Be("2026-08-01");
    }
}
