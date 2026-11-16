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
            Description = "Checking duplicate orders after the Q3 import",
            Reference = "PROJ-123",
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
        Assert.Equal("Checking duplicate orders after the Q3 import", entry.Description);
        Assert.Equal("PROJ-123", entry.Reference);
    }

    [Fact]
    public void The_reason_and_reference_are_capped_and_blank_becomes_absent()
    {
        var store = new StudioStore(_path);

        store.LogRun(new RunLogRecord
        {
            Actor = RunActors.Agent,
            SpaceId = "a",
            Statement = "select 1",
            Status = RunStatuses.Ok,
            // Agent prose, so the cap is the only bound: trim first, then cut.
            Description = $"  {new string('d', StudioStore.MaxRecordedDescriptionChars + 20)}  ",
            Reference = "   ",
        });

        var entry = Assert.Single(store.GetRunLog());
        Assert.Equal(StudioStore.MaxRecordedDescriptionChars, entry.Description!.Length);
        Assert.Null(entry.Reference);
    }

    [Fact]
    public void Search_finds_a_run_by_its_reason_or_reference()
    {
        var store = new StudioStore(_path);
        store.LogRun(new RunLogRecord
        {
            Actor = RunActors.Agent,
            SpaceId = "a",
            SpaceName = "Production",
            Statement = "select count(*) from dbo.Orders",
            Status = RunStatuses.Ok,
            Description = "Investigating duplicate orders",
            Reference = "PROJ-123",
        });
        store.LogRun(Run(spaceId: "a", statement: "select 2"));

        Assert.Single(store.SearchRunLog(null, "duplicate"));
        Assert.Single(store.SearchRunLog(null, "PROJ-123"));
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
    public void A_store_with_the_v8_run_log_gains_the_reason_columns_on_open()
    {
        // Version 8 shipped run_log without the agent's why. Dropping the columns is the honest
        // shape of that store, so the upgrade path (ALTER TABLE, not CREATE TABLE) is exercised.
        var store = new StudioStore(_path);
        store.LogRun(Run(spaceId: "a", statement: "select before the notes"));

        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                ALTER TABLE run_log DROP COLUMN description;
                ALTER TABLE run_log DROP COLUMN reference;
                PRAGMA user_version = 8;
                """;
            command.ExecuteNonQuery();
        }

        var upgraded = new StudioStore(_path);

        // The old row survives the upgrade with the new columns empty, not missing.
        var kept = Assert.Single(upgraded.GetRunLog());
        Assert.Null(kept.Description);
        Assert.Null(kept.Reference);

        upgraded.LogRun(new RunLogRecord
        {
            Actor = RunActors.Agent,
            SpaceId = "a",
            Statement = "select after the notes",
            Status = RunStatuses.Ok,
            Description = "why it ran",
            Reference = "REF-1",
        });

        var entry = upgraded.GetRunLog()[0];
        Assert.Equal("why it ran", entry.Description);
        Assert.Equal("REF-1", entry.Reference);
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

    [Fact]
    public void The_count_covers_the_scope_not_the_page_that_was_read()
    {
        var store = new StudioStore(_path);
        for (var i = 0; i < 12; i++)
        {
            store.LogRun(Run(spaceId: i % 2 == 0 ? "a" : "b", statement: $"select {i}"));
        }

        // Reading is capped; counting is not, so a badge cannot claim the cap is the total.
        Assert.Equal(4, store.GetRunLog("a", limit: 4).Count);
        Assert.Equal(6, store.CountRunLog("a"));
        Assert.Equal(12, store.CountRunLog());
    }

    [Fact]
    public void Search_reaches_past_the_read_cap()
    {
        var store = new StudioStore(_path);
        store.LogRun(Run(spaceId: "a", statement: "select the needle"));
        for (var i = 0; i < 30; i++)
        {
            store.LogRun(Run(spaceId: "a", statement: $"select haystack {i}"));
        }

        // The match is the oldest row, so it sits outside a page of 5.
        Assert.DoesNotContain(
            store.GetRunLog("a", limit: 5),
            entry => entry.Statement.Contains("needle", StringComparison.Ordinal));

        var found = Assert.Single(store.SearchRunLog("a", "needle"));
        Assert.Equal("select the needle", found.Statement);
    }

    [Fact]
    public void Search_matches_workspace_and_database_and_uses_sql_injection_safe_parameters()
    {
        var store = new StudioStore(_path);
        store.LogRun(new RunLogRecord
        {
            Actor = RunActors.User,
            SpaceId = "a",
            SpaceName = "Production",
            DatabaseName = "Sales",
            Statement = "select 1",
            Status = RunStatuses.Ok,
        });
        store.LogRun(new RunLogRecord
        {
            Actor = RunActors.User,
            SpaceId = "a",
            SpaceName = "Scratch",
            DatabaseName = "Warehouse",
            Statement = "select 2",
            Status = RunStatuses.Ok,
        });

        Assert.Single(store.SearchRunLog(null, "production"));
        Assert.Single(store.SearchRunLog(null, "warehouse"));
        Assert.Equal(2, store.SearchRunLog(null, "select").Count);

        // A quote is text, not SQL: the whole table must survive being searched for one.
        Assert.Empty(store.SearchRunLog(null, "'; DROP TABLE run_log; --"));
        Assert.Equal(2, store.CountRunLog());
    }

    [Fact]
    public void Search_treats_wildcards_as_characters()
    {
        var store = new StudioStore(_path);
        store.LogRun(Run(spaceId: "a", statement: "select 100"));
        store.LogRun(Run(spaceId: "a", statement: "select a_b"));
        store.LogRun(Run(spaceId: "a", statement: "select axb"));

        // "%" and "_" are the user's characters, not LIKE wildcards.
        Assert.Empty(store.SearchRunLog(null, "%"));
        Assert.Single(store.SearchRunLog(null, "a_b"));
        Assert.Single(store.SearchRunLog(null, "axb"));
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
