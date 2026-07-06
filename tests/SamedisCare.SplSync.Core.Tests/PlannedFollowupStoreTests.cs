using FluentAssertions;
using SamedisCare.SplSync.Core.Sync;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

public class PlannedFollowupStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly StateDb _state;

    public PlannedFollowupStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"spl-sync-followup-{Guid.NewGuid():N}.sqlite");
        _state = new StateDb(_dbPath);
    }

    [Fact]
    public void Exists_is_false_until_recorded_then_true()
    {
        var store = new PlannedFollowupStore(_state);

        store.Exists("tenant1", "TEST-1").Should().BeFalse();

        store.Record("tenant1", "TEST-1", "issue-abc");
        store.Exists("tenant1", "TEST-1").Should().BeTrue();
    }

    [Fact]
    public void Record_is_idempotent_per_tenant_and_test()
    {
        var store = new PlannedFollowupStore(_state);

        store.Record("tenant1", "TEST-1", "issue-abc");
        store.Record("tenant1", "TEST-1", "issue-xyz"); // darf nicht werfen (ON CONFLICT)

        store.Exists("tenant1", "TEST-1").Should().BeTrue();
        // Anderer Mandant / anderer Test bleibt unberührt.
        store.Exists("tenant2", "TEST-1").Should().BeFalse();
        store.Exists("tenant1", "TEST-2").Should().BeFalse();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
