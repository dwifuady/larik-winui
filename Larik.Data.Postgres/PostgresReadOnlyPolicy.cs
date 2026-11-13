using Larik.Data;

namespace Larik.Data.Postgres;

/// <summary>
/// The PostgreSQL gate for read-only exposure. The engine can enforce this itself
/// (<c>BEGIN TRANSACTION READ ONLY</c> in <see cref="PostgresConnection.ExecuteBatchAsync"/>), so
/// this classifier is the second line: it refuses session control and the functions that write even
/// inside a read-only transaction (sequences, large objects, session settings).
/// </summary>
public sealed class PostgresReadOnlyPolicy : IReadOnlyStatementPolicy
{
    public static PostgresReadOnlyPolicy Instance { get; } = new();

    private static readonly ReadOnlyKeywordRules Rules = new()
    {
        LeadingKeywords = ReadOnlyKeywordRules.Set("select", "with", "values", "table"),

        DeniedKeywords = ReadOnlyKeywordRules.Set(
            "insert",
            "update",
            "delete",
            "merge",
            "into",
            "copy",
            "create",
            "alter",
            "drop",
            "truncate",
            "grant",
            "revoke",
            "comment",
            "security",
            "set",
            "reset",
            "begin",
            "commit",
            "rollback",
            "savepoint",
            "release",
            "prepare",
            "execute",
            "deallocate",
            "declare",
            "fetch",
            "move",
            "close",
            "listen",
            "notify",
            "unlisten",
            "lock",
            "vacuum",
            "analyze",
            "cluster",
            "reindex",
            "refresh",
            "discard",
            "do",
            "call",
            "load",
            // Reads on the surface, writes in effect: sequences, large objects, sessions.
            "nextval",
            "setval",
            "set_config",
            "lo_import",
            "lo_export",
            "lo_unlink",
            "dblink",
            "pg_terminate_backend",
            "pg_cancel_backend",
            "pg_reload_conf",
            "pg_switch_wal",
            "pg_create_restore_point",
            "pg_promote",
            "pg_start_backup",
            "pg_stop_backup"),
    };

    public bool IsProvenRead(string sql) => ReadOnlySql.IsProvenRead(sql, Rules);
}
