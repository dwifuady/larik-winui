using System.Text.Json;

namespace Larik.Services;

/// <summary>
/// The studio ↔ MCP-server approval channel: a named pipe owned by the app, connected to by
/// Larik.Mcp when it needs a human to allow a write. Loopback only, current user only, newline
/// delimited JSON in both directions.
/// </summary>
public static class McpPipe
{
    /// <summary>Fixed name so the two processes agree without configuration; versioned for changes.</summary>
    public const string Name = "larik.studio.mcp.v1";

    /// <summary>How long a write request may sit unanswered before it is denied.</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait for the app before falling back to the client or a refusal.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(900);

    public const string MessageTypeApproval = "approval";
    public const string MessageTypeDecision = "decision";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>The MCP server asking the studio to show one statement to the user.</summary>
public sealed class McpApprovalRequest
{
    public string Type { get; init; } = McpPipe.MessageTypeApproval;

    public required string Id { get; init; }

    public required string SpaceId { get; init; }

    public required string SpaceName { get; init; }

    public required string ProviderId { get; init; }

    public required string Statement { get; init; }
}

/// <summary>The studio's answer. <see cref="Surface"/> records who decided, for the audit row.</summary>
public sealed class McpApprovalDecision
{
    public string Type { get; init; } = McpPipe.MessageTypeDecision;

    public required string Id { get; init; }

    public bool Approved { get; init; }

    public string Surface { get; init; } = "app";

    /// <summary><c>approved</c>, <c>denied</c> or <c>timeout</c>.</summary>
    public string Status { get; init; } = McpApprovalStatus.Denied;

    public string? Reason { get; init; }

    public static McpApprovalDecision Denied(string id, string status, string reason) => new()
    {
        Id = id,
        Approved = false,
        Status = status,
        Reason = reason,
    };
}
