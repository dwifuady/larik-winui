using Larik.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The run log is written by two processes and read by the UI, and it holds statement text on a
/// retention window. These pin the parts that would silently rot: the row shape, the window, and
/// the upgrade from a store that predates the table.
/// </summary>
public sealed class RunLogStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"larik-runlog-{Guid.NewGuid():N}.db");

    [Fact]
    public void Entry_round_trips_with_its_actor_and_result()
    {
        var store = new StudioStore(_path);

        var id = store.LogRun(new RunLogRecord
        {
            Actor = RunActors.Agent,
            SpaceId = "space-1",
            SpaceName = "Production",
            DatabaseName = "Sales",
            ProviderId = "mssql",
            Statement = "select top (10) * from dbo.Orders",
            Status = RunStatuses.Ok,
            DurationMs = 812,
            RowsReturned = 10,
            RowLimit = 200,
            IsTruncated = true,
        });

        Assert.True(id > 0);

        var entry = Assert.Single(store.GetRunLog());
        Assert.Equal("space-1", entry.SpaceId);
        Assert.Equal("Production", entry.SpaceName);
        Assert.Equal("Sales", entry.DatabaseName);
        Assert.Equal(RunActors.Agent, entry.Actor);
        Assert.Equal(RunStatuses.Ok, entry.Status);
        Assert.Equal(812, entry.DurationMs);
        Assert.Equal(10, entry.RowsReturned);
        Assert.Equal(200, entry.RowLimit);
        Assert.True(entry.IsTruncated);
    }

    [Fact]
    public void Reads_narrow_to_one_workspace_and_stay_newest_first()
    {
        var store = new StudioStore(_path);
        store.LogRun(Run(spaceId: "a", statement: "select 1"));
        store.LogRun(Run(spaceId: "b", statement: "select 2"));
        store.LogRun(Run(spaceId: "a", statement: "select 3"));

        var scoped = store.GetRunLog("a");
        Assert.Equal(2, scoped.Count);
        Assert.Equal("select 3", scoped[0].Statement);
        Assert.Equal(3, store.GetRunLog().Count);
    }

    [Fact]
    public void Pruning_drops_rows_past_the_window_and_keeps_the_rest()
    {
        var store = new StudioStore(_path);
        store.LogRun(Run(spaceId: "a", statement: "select old"));
        store.LogRun(Run(spaceId: "a", statement: "select new"));

        Backdate("select old", days: 200);

        Assert.Equal(1, store.PruneRunLog(keepDays: 120));
        var remaining = Assert.Single(store.GetRunLog());
        Assert.Equal("select new", remaining.Statement);

        // The same row survives every window that is longer than its age.
        Assert.Equal(0, store.PruneRunLog(keepDays: 30));
        Assert.Single(store.GetRunLog());
    }

    [Fact]
    public void Retention_accepts_its_choices_and_falls_back_to_the_default()
    {
        var store = new StudioStore(_path);

        Assert.Equal(30, store.GetRunLogRetentionDays());

        Assert.True(store.SetRunLogRetentionDays(90));
        Assert.Equal(90, store.GetRunLogRetentionDays());

        Assert.True(store.SetRunLogRetentionDays(120));
        Assert.Equal(120, store.GetRunLogRetentionDays());

        // A hand-edited or obsolete value must not create an unbounded log.
        Assert.True(store.SetRunLogRetentionDays(5000));
        Assert.Equal(30, store.GetRunLogRetentionDays());
    }

    [Fact]
    public void Recording_is_on_by_default_and_can_be_turned_off_per_workspace()
    {
        var store = new StudioStore(_path);
        SaveWorkspace(store, "space-1");

        Assert.True(store.IsRunRecordingEnabled("space-1"));

        Assert.True(store.SetRunRecording("space-1", enabled: false));
        Assert.False(store.IsRunRecordingEnabled("space-1"));

        Assert.True(store.SetRunRecording("space-1", enabled: true));
        Assert.True(store.IsRunRecordingEnabled("space-1"));
    }

    [Fact]
    public void A_store_that_predates_the_table_gains_it_on_open()
    {
        // Version 7 shipped without run_log. The migration runs only when the version is behind,
        // so a store created by the previous build is the shape that matters.
        var store = new StudioStore(_path);
        store.LogRun(Run(spaceId: "a", statement: "select before upgrade"));

        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE run_log;
                PRAGMA user_version = 7;
                """;
            command.ExecuteNonQuery();
        }

        var upgraded = new StudioStore(_path);
        Assert.Empty(upgraded.GetRunLog());

        upgraded.LogRun(Run(spaceId: "a", statement: "select after upgrade"));
        var entry = Assert.Single(upgraded.GetRunLog());
        Assert.Equal("select after upgrade", entry.Statement);
    }

    [Fact]
    public void A_long_statement_is_cut_rather_than_rejected()
    {
        var store = new StudioStore(_path);
        var statement = "-- " + new string('a', StudioStore.MaxRecordedStatementChars + 500);

        Assert.True(store.LogRun(Run(spaceId: "a", statement)) > 0);

        var entry = Assert.Single(store.GetRunLog());
        Assert.Equal(StudioStore.MaxRecordedStatementChars, entry.Statement.Length);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // A test store that cannot be deleted is not worth failing the run over.
            }
        }
    }

    private static RunLogRecord Run(string? spaceId, string statement) => new()
    {
        Actor = RunActors.User,
        SpaceId = spaceId,
        SpaceName = "Production",
        DatabaseName = "Sales",
        ProviderId = "mssql",
        TabId = "tab-1",
        Statement = statement,
        Status = RunStatuses.Ok,
        DurationMs = 12,
        RowsReturned = 1,
    };

    private static void SaveWorkspace(StudioStore store, string spaceId) =>
        store.Save(new StudioState
        {
            ActiveSpaceId = spaceId,
            Spaces =
            [
                new SpaceState
                {
                    Id = spaceId,
                    Name = "Production",
                    Profile = new Dictionary<string, string> { ["server"] = "localhost" },
                },
            ],
        });

    /// <summary>Moves one row into the past so the retention window can be exercised.</summary>
    private void Backdate(string statement, int days)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_log SET recorded_at = datetime('now', $offset) WHERE statement = $statement;
            """;
        command.Parameters.AddWithValue("$offset", $"-{days} days");
        command.Parameters.AddWithValue("$statement", statement);
        command.ExecuteNonQuery();
    }
}
