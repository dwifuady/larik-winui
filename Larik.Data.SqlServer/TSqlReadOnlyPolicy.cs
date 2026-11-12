using Larik.Data;

namespace Larik.Data.SqlServer;

/// <summary>
/// The T-SQL gate for read-only exposure. SQL Server has no session-level read-only mode, so this
/// classifier is paired with an always-rolled-back transaction in
/// <see cref="SqlServerConnection.ExecuteBatchAsync"/>: the classifier refuses the statements it can
/// recognise, and the transaction undoes the transactional writes it cannot.
/// </summary>
public sealed class TSqlReadOnlyPolicy : IReadOnlyStatementPolicy
{
    public static TSqlReadOnlyPolicy Instance { get; } = new();

    private static readonly ReadOnlyKeywordRules Rules = new()
    {
        LeadingKeywords = ReadOnlyKeywordRules.Set("select", "with"),

        // Deliberately blunt: refusing a column that shares a keyword name is the fail-closed
        // trade. Every entry either writes, changes session state, or reaches outside the server.
        DeniedKeywords = ReadOnlyKeywordRules.Set(
            "insert",
            "update",
            "delete",
            "merge",
            "into",
            "exec",
            "execute",
            "alter",
            "create",
            "drop",
            "truncate",
            "grant",
            "revoke",
            "deny",
            "use",
            "set",
            "declare",
            "begin",
            "commit",
            "rollback",
            "save",
            "backup",
            "restore",
            "dbcc",
            "kill",
            "shutdown",
            "waitfor",
            "reconfigure",
            "checkpoint",
            "bulk",
            "openrowset",
            "openquery",
            "opendatasource",
            "openxml",
            "enable",
            "disable",
            "receive",
            "send",
            "print",
            "raiserror"),

        // Extended procedures are the classic escape hatch (xp_cmdshell, xp_regread, …).
        DeniedPrefixes = ["xp_"],
    };

    public bool IsProvenRead(string sql) => ReadOnlySql.IsProvenRead(sql, Rules);
}
