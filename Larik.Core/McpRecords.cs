namespace Larik.Services;

/// <summary>
/// How far one workspace is exposed to MCP clients. Off is the default everywhere: a workspace has
/// to be opted in twice (the global switch and this level) before an agent can read it.
/// </summary>
public enum McpAccess
{
    Off,
    Read,
    Write,
}

/// <summary>Storage form of <see cref="McpAccess"/>; unknown values read as <see cref="McpAccess.Off"/>.</summary>
public static class McpAccessText
{
    public static McpAccess Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "read" => McpAccess.Read,
            "write" => McpAccess.Write,
            _ => McpAccess.Off,
        };

    public static string ToStorage(McpAccess access) =>
        access switch
        {
            McpAccess.Read => "read",
            McpAccess.Write => "write",
            _ => "off",
        };

    /// <summary>Outcome wording for the UI: what the agent may do, not what the level is called.</summary>
    public static string Describe(McpAccess access) =>
        access switch
        {
            McpAccess.Read => "Read only",
            McpAccess.Write => "Read and write",
            _ => "Off",
        };
}

/// <summary>One workspace as an MCP server sees it: identity, access level and saved profile.</summary>
public sealed class McpSpaceRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string ProviderId { get; init; }

    public bool Hidden { get; init; }

    public McpAccess Access { get; init; }

    /// <summary>Stored profile values; password-kind settings are added from the secret store at connect time.</summary>
    public Dictionary<string, string> Profile { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the workspace has enough settings to attempt a connection.</summary>
    public bool HasConnection => Profile.Count > 0;
}

/// <summary>One audited MCP tool call, written by whichever process served it.</summary>
public sealed class McpLogRecord
{
    public long Id { get; init; }

    public string RecordedAt { get; init; } = string.Empty;

    public string Tool { get; init; } = string.Empty;

    public string? SpaceId { get; init; }

    public string? SpaceName { get; init; }

    public string? ProviderId { get; init; }

    public McpAccess Access { get; init; }

    public string? Statement { get; init; }

    /// <summary><c>ok</c>, <c>error</c> or <c>denied</c>.</summary>
    public string Status { get; init; } = "ok";

    public string? Reason { get; init; }

    public long DurationMs { get; init; }

    public int RowsReturned { get; init; }

    /// <summary>Who approved a write: <c>app</c> or <c>client</c>; null when no approval was needed.</summary>
    public string? DecisionSurface { get; init; }
}

/// <summary>
/// One pending write approval. The row is written by the MCP server when the request appears and
/// closed when a human decides, so the audit list explains a refused write even if the app was
/// closed and nobody was asked.
/// </summary>
public sealed class McpApprovalRecord
{
    public required string Id { get; init; }

    public required string SpaceId { get; init; }

    public required string SpaceName { get; init; }

    public required string Statement { get; init; }
}

/// <summary>Approval lifecycle values shared by the app, the server and the store.</summary>
public static class McpApprovalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string TimedOut = "timeout";
    public const string Orphaned = "orphaned";
}
