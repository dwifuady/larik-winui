using System.Collections.Concurrent;
using Larik.Data;
using Larik.Services;
using ModelContextProtocol;

namespace Larik.Mcp;

/// <summary>
/// Everything a tool needs from the studio: the shared store, the exposure policy, and a connection
/// built from a workspace's saved profile. One <see cref="IDatabaseConnection"/> per tool call, so an
/// agent can never touch a query tab's session or the workspace's control connection.
/// </summary>
internal sealed class StudioContext(StudioStore store)
{
    /// <summary>Row cap for agent queries: enough to reason about, small enough to leave context.</summary>
    internal const int DefaultRowLimit = 200;

    internal const int MaxRowLimit = 1000;

    /// <summary>
    /// Hard cap on one statement. The workspace's own timeout is the user's setting and may be
    /// "wait forever", which is fine for a person watching a query and wrong for an agent.
    /// </summary>
    internal static readonly TimeSpan MaxStatementDuration = TimeSpan.FromMinutes(2);

    /// <summary>One agent may run this many calls against a workspace at once, no more.</summary>
    private const int MaxConcurrentPerSpace = 2;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SpaceGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Longer statements are refused rather than truncated: half a statement is a surprise.</summary>
    internal const int MaxStatementChars = 10_000;

    public StudioStore Store => store;

    /// <summary>
    /// Every tool starts here. MCP being off is not an error in the studio, so it reads as a
    /// sentence an agent can relay to the user instead of a protocol failure.
    /// </summary>
    public IReadOnlyList<McpSpaceRecord> ExposedSpaces()
    {
        if (!store.IsMcpEnabled())
        {
            throw new McpException(
                "MCP access is off in Larik SQL Studio. Open the studio, go to Settings → AI agents " +
                "and turn MCP on, then choose which workspaces an agent may see.");
        }

        return store.GetMcpSpaces()
            .Where(space => space.Access != McpAccess.Off && space.HasConnection)
            .ToList();
    }

    public McpSpaceRecord RequireSpace(string connection)
    {
        var spaces = ExposedSpaces();
        foreach (var space in spaces)
        {
            if (string.Equals(space.Id, connection, StringComparison.OrdinalIgnoreCase))
            {
                return space;
            }
        }

        throw new McpException(
            $"No connection '{connection}' is exposed to agents. Call list_connections for the ids " +
            "the user made available, or ask them to expose that workspace in Larik.");
    }

    public IDatabaseProvider RequireProvider(McpSpaceRecord space) =>
        DatabaseProviderCatalog.Find(space.ProviderId)
        ?? throw new McpException(
            $"The provider '{space.ProviderId}' is not installed in Larik's MCP server, so " +
            $"'{space.Name}' cannot be queried from here.");

    public ConnectionProfile ProfileFor(McpSpaceRecord space, IDatabaseProvider provider)
    {
        var profile = new ConnectionProfile { ProviderId = provider.Id };
        foreach (var pair in space.Profile)
        {
            profile.Values[pair.Key] = pair.Value;
        }

        // Passwords live in Windows Credential Manager, never in the store. The server runs as the
        // same user, so it reads the same entries the app wrote.
        foreach (var setting in provider.Settings.Where(setting => setting.Kind == ProviderSettingKind.Password))
        {
            var secret = SecretStore.Read(SecretStore.Target(space.Id, setting.Key));
            if (!string.IsNullOrEmpty(secret))
            {
                profile.Values[setting.Key] = secret;
            }
        }

        return profile;
    }

    /// <summary>Creates, opens and disposes one connection around <paramref name="work"/>.</summary>
    public async Task<T> WithConnectionAsync<T>(
        McpSpaceRecord space,
        Func<IDatabaseConnection, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        // Several agents can point at the same workspace at once. The cap keeps a burst of calls
        // from opening a dozen server sessions while one of them is already waiting on a table.
        var gate = SpaceGates.GetOrAdd(
            space.Id,
            _ => new SemaphoreSlim(MaxConcurrentPerSpace, MaxConcurrentPerSpace));

        await gate.WaitAsync(cancellationToken);
        try
        {
            var provider = RequireProvider(space);
            await using var connection = provider.CreateConnection(ProfileFor(space, provider));
            await connection.OpenAsync(cancellationToken);
            return await work(connection, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Records one call. Denials are audited too: that is the point of the list.</summary>
    public void Audit(
        string tool,
        McpSpaceRecord? space,
        McpAccess access,
        string? statement,
        string status,
        string? reason,
        TimeSpan duration,
        int rows,
        string? decisionSurface = null)
    {
        store.LogMcpCall(new McpLogRecord
        {
            Tool = tool,
            SpaceId = space?.Id,
            SpaceName = space?.Name,
            ProviderId = space?.ProviderId,
            Access = access,
            Statement = statement is { Length: > MaxStatementChars }
                ? statement[..MaxStatementChars]
                : statement,
            Status = status,
            Reason = reason,
            DurationMs = (long)duration.TotalMilliseconds,
            RowsReturned = rows,
            DecisionSurface = decisionSurface,
        });
    }

    /// <summary>Audits a call with no workspace context (list_connections, a refused lookup).</summary>
    public void AuditGlobal(string tool, string status, string? reason, TimeSpan duration) =>
        Audit(tool, space: null, McpAccess.Off, statement: null, status, reason, duration, rows: 0);
}
