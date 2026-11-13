using Larik.Data;

namespace Larik.Data.Demo;

/// <summary>
/// The demo engine's read-only gate. It exists because the demo provider is what the MCP smoke test
/// and the UI suite connect to: a workspace exposed as read-only has to behave like one here too,
/// not quietly allow everything because no real server would refuse it.
/// </summary>
public sealed class DemoReadOnlyPolicy : IReadOnlyStatementPolicy
{
    public static DemoReadOnlyPolicy Instance { get; } = new();

    private static readonly ReadOnlyKeywordRules Rules = new()
    {
        LeadingKeywords = ReadOnlyKeywordRules.Set("select", "with"),
        DeniedKeywords = ReadOnlyKeywordRules.Set(
            "insert",
            "update",
            "delete",
            "merge",
            "into",
            "exec",
            "execute",
            "create",
            "alter",
            "drop",
            "truncate",
            "grant",
            "revoke",
            "set",
            "use",
            "begin",
            "commit",
            "rollback",
            "declare",
            "print"),
    };

    public bool IsProvenRead(string sql) => ReadOnlySql.IsProvenRead(sql, Rules);
}
