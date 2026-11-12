using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Larik.Services;

/// <summary>
/// MCP state: the global switch, the per-workspace exposure level and the audit log. It lives in the
/// same store as everything else on purpose — the app and the MCP server process both read it, and
/// there is exactly one audit list to show the user.
/// </summary>
public sealed partial class StudioStore
{
    private const string McpEnabledStateKey = "mcp_enabled";

    /// <summary>The global switch. Missing or unreadable means off: nothing is listed.</summary>
    public bool IsMcpEnabled()
    {
        try
        {
            using var connection = Open();
            return bool.TryParse(ReadState(connection, McpEnabledStateKey), out var enabled) && enabled;
        }
        catch
        {
            // A store that cannot be read must never enable an agent surface.
            return false;
        }
    }

    public bool SetMcpEnabled(bool enabled)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            SetState(connection, transaction, McpEnabledStateKey, enabled ? "true" : "false");
            transaction.Commit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every workspace with the connection profile an MCP client may use.</summary>
    public IReadOnlyList<McpSpaceRecord> GetMcpSpaces()
    {
        try
        {
            using var connection = Open();

            List<(string Id, string Name, string ProviderId, bool Hidden, McpAccess Access)> rows = [];
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT id, name, provider_id, COALESCE(hidden, 0), COALESCE(mcp_access, 'off')
                    FROM spaces
                    ORDER BY sort_order, name;
                    """;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3) != 0,
                        McpAccessText.Parse(reader.GetString(4))));
                }
            }

            // Profiles are read after the workspace reader closes: one connection, one statement at
            // a time keeps this boring on a database two processes share.
            List<McpSpaceRecord> spaces = [];
            foreach (var row in rows)
            {
                spaces.Add(new McpSpaceRecord
                {
                    Id = row.Id,
                    Name = row.Name,
                    ProviderId = row.ProviderId,
                    Hidden = row.Hidden,
                    Access = row.Access,
                    Profile = ReadProfile(connection, row.Id),
                });
            }

            return spaces;
        }
        catch
        {
            return [];
        }
    }

    public bool SetSpaceMcpAccess(string spaceId, McpAccess access)
    {
        try
        {
            using var connection = Open();
            return Execute(
                connection,
                null,
                "UPDATE spaces SET mcp_access = $access, updated_at = datetime('now') WHERE id = $id;",
                ("$access", McpAccessText.ToStorage(access)),
                ("$id", spaceId)) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Records one tool call. Audit rows are written by both processes, so a busy database is
    /// expected: the caller retries rather than losing the entry that explains what an agent did.
    /// </summary>
    public long LogMcpCall(McpLogRecord record)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO mcp_log
                    (tool, space_id, space_name, provider_id, access_level, statement, status, reason,
                     duration_ms, rows_returned, decision_surface)
                VALUES ($tool, $spaceId, $spaceName, $providerId, $access, $statement, $status, $reason,
                        $duration, $rows, $surface);
                SELECT last_insert_rowid();
                """;

            command.Parameters.AddWithValue("$tool", record.Tool);
            command.Parameters.AddWithValue("$spaceId", (object?)record.SpaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$spaceName", (object?)record.SpaceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$providerId", (object?)record.ProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$access", McpAccessText.ToStorage(record.Access));
            command.Parameters.AddWithValue("$statement", (object?)record.Statement ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", record.Status);
            command.Parameters.AddWithValue("$reason", (object?)record.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration", record.DurationMs);
            command.Parameters.AddWithValue("$rows", record.RowsReturned);
            command.Parameters.AddWithValue("$surface", (object?)record.DecisionSurface ?? DBNull.Value);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch
        {
            // An audit write must never be the reason a query result is lost.
            return 0;
        }
    }

    /// <summary>Newest first, read-only list for Settings.</summary>
    public IReadOnlyList<McpLogRecord> GetMcpLog(int limit = 100)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, recorded_at, tool, space_id, space_name, provider_id, access_level,
                       statement, status, reason, duration_ms, rows_returned, decision_surface
                FROM mcp_log
                ORDER BY id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            List<McpLogRecord> entries = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new McpLogRecord
                {
                    Id = reader.GetInt64(0),
                    RecordedAt = reader.GetString(1),
                    Tool = reader.GetString(2),
                    SpaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SpaceName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ProviderId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Access = McpAccessText.Parse(reader.GetString(6)),
                    Statement = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Status = reader.GetString(8),
                    Reason = reader.IsDBNull(9) ? null : reader.GetString(9),
                    DurationMs = reader.GetInt64(10),
                    RowsReturned = (int)reader.GetInt64(11),
                    DecisionSurface = reader.IsDBNull(12) ? null : reader.GetString(12),
                });
            }

            return entries;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Clears the audit list; the user's only destructive action on this surface.</summary>
    public bool ClearMcpLog()
    {
        try
        {
            using var connection = Open();
            Execute(connection, null, "DELETE FROM mcp_log;");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records a write approval the MCP server is about to ask a human for.</summary>
    public bool CreateMcpApproval(McpApprovalRecord approval)
    {
        try
        {
            using var connection = Open();
            return Execute(
                connection,
                null,
                """
                INSERT INTO mcp_approvals (id, space_id, space_name, statement, status)
                VALUES ($id, $spaceId, $spaceName, $statement, 'pending');
                """,
                ("$id", approval.Id),
                ("$spaceId", approval.SpaceId),
                ("$spaceName", approval.SpaceName),
                ("$statement", approval.Statement)) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Closes a pending approval once a human (or the timeout) decided.</summary>
    public bool ResolveMcpApproval(string id, string status, string? surface)
    {
        try
        {
            using var connection = Open();
            return Execute(
                connection,
                null,
                """
                UPDATE mcp_approvals
                SET status = $status, decided_at = datetime('now'), surface = $surface
                WHERE id = $id AND status = 'pending';
                """,
                ("$status", status),
                ("$surface", surface),
                ("$id", id)) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Closes approvals whose requester died. A pending row older than the timeout can never be
    /// answered: the process that was waiting on it is gone or gave up.
    /// </summary>
    public int SweepMcpApprovals()
    {
        try
        {
            using var connection = Open();
            return Execute(
                connection,
                null,
                """
                UPDATE mcp_approvals
                SET status = 'orphaned', decided_at = datetime('now')
                WHERE status = 'pending' AND created_at < datetime('now', '-10 minutes');
                """);
        }
        catch
        {
            return 0;
        }
    }
}
