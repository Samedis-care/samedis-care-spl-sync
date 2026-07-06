using FluentAssertions;
using Microsoft.Data.Sqlite;
using SamedisCare.SplSync.Core.Actimed;
using Xunit;

namespace SamedisCare.SplSync.Core.Tests;

/// <summary>
/// Round-trip-Tests für die TEST_SPEC-Lookups und das Anlegen einer A3_ACTIVITY über den
/// SqliteActimedRepository — die Bausteine, die der DownloadEngine für das Auto-Anlegen von
/// Tätigkeiten (create_activities_from_mapping) nutzt.
/// </summary>
public class SqliteActivityCreationTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteActivityCreationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"spl-sync-test-{Guid.NewGuid():N}.sqlite");
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        Exec(conn, "CREATE TABLE test_spec (test_spec_id INTEGER PRIMARY KEY, name TEXT);");
        Exec(conn, "CREATE TABLE a3_activity_kind (kind_id INTEGER PRIMARY KEY, kind_name TEXT, kind_dscr TEXT);");
        Exec(conn, "CREATE TABLE a3_activity (activity_id INTEGER PRIMARY KEY, test_spec_id INTEGER, kind_id INTEGER, activity_interval INTEGER, activity_name TEXT);");
        Exec(conn, "INSERT INTO test_spec (test_spec_id, name) VALUES (1, 'Unbekannt'), (54, 'EN50699_0702_SKI_ErsatzMessung_allg_Grenzwerte');");
        Exec(conn, "INSERT INTO a3_activity_kind (kind_id, kind_name, kind_dscr) VALUES (2, 'MPBe_§11_STK/DGUV V3', 'x');");
    }

    [Fact]
    public void FindTestSpecByName_returns_row()
    {
        var repo = new SqliteActimedRepository(_dbPath);
        var spec = repo.FindTestSpecByName("EN50699_0702_SKI_ErsatzMessung_allg_Grenzwerte");
        spec.Should().NotBeNull();
        spec!.TestSpecId.Should().Be(54);
        spec.IsUnknown.Should().BeFalse();
    }

    [Fact]
    public void GetTestSpecById_flags_unknown()
    {
        var repo = new SqliteActimedRepository(_dbPath);
        repo.GetTestSpecById(1)!.IsUnknown.Should().BeTrue();
        repo.GetTestSpecById(999).Should().BeNull();
    }

    [Fact]
    public void InsertActivity_then_find_by_kind_roundtrips()
    {
        var repo = new SqliteActimedRepository(_dbPath);

        repo.FindActivityByKindId(2).Should().BeNull("noch keine Tätigkeit für DGUV V3");

        var newId = repo.InsertActivity(new ActimedActivity(
            ActivityId: 0, TestSpecId: 54, KindId: 2, IntervalMonths: 24, Name: "MPBe_§11_STK/DGUV V3"));
        newId.Should().BeGreaterThan(0);

        var found = repo.FindActivityByKindId(2);
        found.Should().NotBeNull();
        found!.TestSpecId.Should().Be(54);
        found.IntervalMonths.Should().Be(24);
        found.KindId.Should().Be(2);
    }

    [Fact]
    public void GetActivityById_returns_interval_for_upload()
    {
        var repo = new SqliteActimedRepository(_dbPath);
        var id = repo.InsertActivity(new ActimedActivity(0, 54, 2, 12, "MPBe_STK_HF_emed-100-014"));

        var byId = repo.GetActivityById(id);
        byId.Should().NotBeNull();
        byId!.IntervalMonths.Should().Be(12);

        repo.GetActivityById(999999).Should().BeNull();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = new SqliteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
