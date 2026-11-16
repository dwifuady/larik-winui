using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Larik.Services;

/// <summary>
/// The run log: one row per execution, whoever started it. It is deliberately not
/// <c>mcp_log</c> - that table records <em>decisions</em> (approve, deny, refuse) for agent
/// calls, this one records <em>executions</em> from both the studio and the MCP server. The
/// app and <c>Larik.Mcp</c> share the store, so both write here.
/// <para>
/// Statement text is stored: without it the log cannot answer "what did I run". It is capped, and
/// rows are pruned on the retention window, which is the honest counterpart to keeping the text at
/// all. An agent may also attach the reason it ran a statement and the ticket the work belongs to,
/// which is what the log needs to answer "why". Nothing here is written when a workspace has run
/// recording turned off.
/// </para>
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>Longest statement kept per row. Longer text is cut, not rejected.</summary>
    public const int MaxRecordedStatementChars = 32_768;

    /// <summary>Longest agent-supplied reason kept per row: one sentence, not an essay.</summary>
    public const int MaxRecordedDescriptionChars = 280;

    /// <summary>
    /// Longest reference kept per row. Deliberately free-form (a ticket, an incident, a branch):
    /// the log has no business deciding which tracker an agent's user works in.
    /// </summary>
    public const int MaxRecordedReferenceChars = 64;

    private const string RunLogKeepDaysStateKey = "run_log_days";
    private const string RunRecordingSettingKey = "record_runs";

    /// <summary>The retention choices offered in the UI; anything else reads back as the default.</summary>
    private static readonly int[] RunLogRetentionChoices = [30, 90, 120];

    private const int DefaultRunLogRetentionDays = 30;

    /// <summary>Records one execution. Returns the new row id, or 0 when the write failed.</summary>
    public long LogRun(RunLogRecord record)
    {
        var statement = record.Statement.Length > MaxRecordedStatementChars
            ? record.Statement[..MaxRecordedStatementChars]
            : record.Statement;

        // Both notes come from an agent, so they are capped like the statement instead of trusted.
        var description = NormalizeNote(record.Description, MaxRecordedDescriptionChars);
        var reference = NormalizeNote(record.Reference, MaxRecordedReferenceChars);

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO run_log
                    (actor, space_id, space_name, database_name, provider_id, tab_id, statement,
                     status, reason, duration_ms, rows_returned, row_limit, is_truncated,
                     description, reference)
                VALUES ($actor, $spaceId, $spaceName, $database, $providerId, $tabId, $statement,
                        $status, $reason, $duration, $rows, $rowLimit, $truncated,
                        $description, $reference);
                SELECT last_insert_rowid();
                """;

            command.Parameters.AddWithValue("$actor", record.Actor);
            command.Parameters.AddWithValue("$spaceId", (object?)record.SpaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$spaceName", (object?)record.SpaceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$database", (object?)record.DatabaseName ?? DBNull.Value);
            command.Parameters.AddWithValue("$providerId", (object?)record.ProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$tabId", (object?)record.TabId ?? DBNull.Value);
            command.Parameters.AddWithValue("$statement", statement);
            command.Parameters.AddWithValue("$status", record.Status);
            command.Parameters.AddWithValue("$reason", (object?)record.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration", record.DurationMs);
            command.Parameters.AddWithValue("$rows", record.RowsReturned);
            command.Parameters.AddWithValue(
                "$rowLimit",
                (object?)record.RowLimit ?? DBNull.Value);
            command.Parameters.AddWithValue("$truncated", record.IsTruncated ? 1 : 0);
            command.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
            command.Parameters.AddWithValue("$reference", (object?)reference ?? DBNull.Value);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch
        {
            // A run must never fail because its history row could not be written.
            return 0;
        }
    }

    /// <summary>Newest first. <paramref name="spaceId"/> null reads every workspace.</summary>
    public IReadOnlyList<RunLogRecord> GetRunLog(string? spaceId = null, int limit = 500)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, recorded_at, actor, space_id, space_name, database_name, provider_id,
                       tab_id, statement, status, reason, duration_ms, rows_returned, row_limit,
                       is_truncated, description, reference
                FROM run_log
                WHERE $space IS NULL OR space_id = $space
                ORDER BY id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$space", (object?)spaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            List<RunLogRecord> entries = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(ReadRecord(reader));
            }

            return entries;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// How many runs the scope holds, independent of the read cap. The page's badge must not claim
    /// "500 runs" when the list simply stopped reading at 500.
    /// </summary>
    public int CountRunLog(string? spaceId = null)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = spaceId is null
                ? "SELECT COUNT(*) FROM run_log;"
                : "SELECT COUNT(*) FROM run_log WHERE space_id = $space;";
            if (spaceId is not null)
            {
                command.Parameters.AddWithValue("$space", spaceId);
            }

            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Search runs in SQL, not over the loaded page: a statement older than the read cap must still
    /// be findable, or the box silently lies about what the log contains.
    /// </summary>
    public IReadOnlyList<RunLogRecord> SearchRunLog(
        string? spaceId,
        string query,
        int limit = 500)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, recorded_at, actor, space_id, space_name, database_name, provider_id,
                       tab_id, statement, status, reason, duration_ms, rows_returned, row_limit,
                       is_truncated, description, reference
                FROM run_log
                WHERE ($space IS NULL OR space_id = $space)
                  AND (statement LIKE $match ESCAPE '\'
                       OR space_name LIKE $match ESCAPE '\'
                       OR database_name LIKE $match ESCAPE '\'
                       OR description LIKE $match ESCAPE '\'
                       OR reference LIKE $match ESCAPE '\')
                ORDER BY id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$space", (object?)spaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$match", $"%{EscapeLike(query)}%");
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            List<RunLogRecord> entries = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(ReadRecord(reader));
            }

            return entries;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>A user's <c>%</c> or <c>_</c> is a character, not a wildcard.</summary>
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Blank is absent; anything longer than the cap is cut, not rejected.</summary>
    private static string? NormalizeNote(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static RunLogRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        RecordedAt = reader.GetString(1),
        Actor = reader.GetString(2),
        SpaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
        SpaceName = reader.IsDBNull(4) ? null : reader.GetString(4),
        DatabaseName = reader.IsDBNull(5) ? null : reader.GetString(5),
        ProviderId = reader.IsDBNull(6) ? null : reader.GetString(6),
        TabId = reader.IsDBNull(7) ? null : reader.GetString(7),
        Statement = reader.GetString(8),
        Status = reader.GetString(9),
        Reason = reader.IsDBNull(10) ? null : reader.GetString(10),
        DurationMs = reader.GetInt64(11),
        RowsReturned = (int)reader.GetInt64(12),
        RowLimit = reader.IsDBNull(13) ? null : reader.GetInt64(13),
        IsTruncated = reader.GetInt64(14) != 0,
        Description = reader.IsDBNull(15) ? null : reader.GetString(15),
        Reference = reader.IsDBNull(16) ? null : reader.GetString(16),
    };

    /// <summary>How long executions are kept. Always one of <see cref="RunLogRetentionChoices"/>.</summary>
    public int GetRunLogRetentionDays() => NormalizeRetention(ReadRunLogRetentionDays());

    public bool SetRunLogRetentionDays(int days)
    {
        var normalized = NormalizeRetention(days);
        try
        {
            using var connection = Open();
            SetState(
                connection,
                null,
                RunLogKeepDaysStateKey,
                normalized.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes rows past the retention window. Called from the existing hourly sweep and once at
    /// launch, so the promise that the log is bounded survives a long-running session.
    /// </summary>
    public int PruneRunLog(int? keepDays = null)
    {
        var days = keepDays is { } explicitDays ? NormalizeRetention(explicitDays) : GetRunLogRetentionDays();
        try
        {
            using var connection = Open();
            return Execute(
                connection,
                null,
                "DELETE FROM run_log WHERE recorded_at < datetime('now', $window);",
                ("$window", $"-{days} days"));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Per-workspace switch. Missing means recording is on; the default never asks a new workspace
    /// to opt in.
    /// </summary>
    public bool IsRunRecordingEnabled(string spaceId)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT value FROM space_settings WHERE space_id = $space AND key = $key;";
            command.Parameters.AddWithValue("$space", spaceId);
            command.Parameters.AddWithValue("$key", RunRecordingSettingKey);

            return command.ExecuteScalar() is not string value
                || !string.Equals(value, "0", StringComparison.Ordinal);
        }
        catch
        {
            // An unreadable store must not silently stop recording; the caller decides.
            return true;
        }
    }

    public bool SetRunRecording(string spaceId, bool enabled)
    {
        try
        {
            using var connection = Open();
            Execute(
                connection,
                null,
                """
                INSERT INTO space_settings (space_id, key, value) VALUES ($space, $key, $value)
                ON CONFLICT(space_id, key) DO UPDATE SET value = excluded.value;
                """,
                ("$space", spaceId),
                ("$key", RunRecordingSettingKey),
                ("$value", enabled ? "1" : "0"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int ReadRunLogRetentionDays()
    {
        try
        {
            using var connection = Open();
            return int.TryParse(
                ReadState(connection, RunLogKeepDaysStateKey),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var days)
                ? NormalizeRetention(days)
                : DefaultRunLogRetentionDays;
        }
        catch
        {
            return DefaultRunLogRetentionDays;
        }
    }

    private static int NormalizeRetention(int days) =>
        RunLogRetentionChoices.Contains(days) ? days : DefaultRunLogRetentionDays;
}

/// <summary>One recorded execution, from the studio (<c>user</c>) or the MCP server (<c>agent</c>).</summary>
public sealed class RunLogRecord
{
    public long Id { get; init; }

    public string RecordedAt { get; init; } = string.Empty;

    /// <summary><see cref="RunActors.User"/> or <see cref="RunActors.Agent"/>.</summary>
    public string Actor { get; init; } = RunActors.User;

    public string? SpaceId { get; init; }

    public string? SpaceName { get; init; }

    public string? DatabaseName { get; init; }

    public string? ProviderId { get; init; }

    /// <summary>The tab the run came from; null for agent runs.</summary>
    public string? TabId { get; init; }

    public string Statement { get; init; } = string.Empty;

    /// <summary><see cref="RunStatuses.Ok"/>, <see cref="RunStatuses.Failed"/>,
    /// <see cref="RunStatuses.Refused"/> or <see cref="RunStatuses.Cancelled"/>.</summary>
    public string Status { get; init; } = RunStatuses.Ok;

    public string? Reason { get; init; }

    public long DurationMs { get; init; }

    public int RowsReturned { get; init; }

    /// <summary>The provider's row cap, set when the result was cut short.</summary>
    public long? RowLimit { get; init; }

    public bool IsTruncated { get; init; }

    /// <summary>
    /// Why the run happened, in the actor's own words. Agent-supplied and untrusted: stored as
    /// text, capped, and never interpreted. Null for a run nobody explained.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The outside work this run belongs to - a ticket key, an incident, a branch. Free-form on
    /// purpose, because the tracker is the user's choice, not the log's.
    /// </summary>
    public string? Reference { get; init; }
}

public static class RunActors
{
    public const string User = "user";

    public const string Agent = "agent";
}

public static class RunStatuses
{
    public const string Ok = "ok";

    public const string Failed = "failed";

    /// <summary>Blocked before execution: a refused write approval, an unguarded mutation.</summary>
    public const string Refused = "refused";

    public const string Cancelled = "cancelled";
}
