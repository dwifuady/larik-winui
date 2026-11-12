using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Larik.Data;
using Larik.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Larik.Mcp;

/// <summary>
/// The tool surface. Deliberately four tools: discovery, databases, schema, query. Everything an
/// agent needs to answer a question, and nothing that lets it change the studio.
/// </summary>
[McpServerToolType]
internal sealed class StudioTools(StudioContext context)
{
    /// <summary>Serialized results are capped so one wide table cannot eat an agent's context.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [McpServerTool(Name = "list_connections", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists the database connections the user has exposed to AI agents in Larik SQL Studio. " +
        "Returns each connection's id, name, engine, endpoint, login and access level. Never returns " +
        "passwords. The id is what every other tool takes as 'connection'.")]
    public string ListConnections()
    {
        var stopwatch = Stopwatch.StartNew();
        var spaces = context.ExposedSpaces();

        var payload = spaces
            .GroupBy(space => new { space.Id, space.Name, space.ProviderId, space.Access })
            .Select(group => group.First())
            .Select(space =>
            {
                var provider = DatabaseProviderCatalog.Find(space.ProviderId);
                var profile = VisibleProfile(space);
                return new
                {
                    id = space.Id,
                    name = space.Name,
                    provider = provider?.DisplayName ?? space.ProviderId,
                    endpoint = provider?.DescribeEndpoint(profile),
                    identity = provider?.DescribeIdentity(profile),
                    access = McpAccessText.ToStorage(space.Access),
                };
            })
            .ToList();

        context.AuditGlobal("list_connections", "ok", reason: null, stopwatch.Elapsed);
        return Serialize(payload);
    }

    [McpServerTool(Name = "list_databases", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists the databases on one connection, marking the ones the saved login cannot open. " +
        "Pass 'database' to get_schema and run_query once you know which one matters.")]
    public async Task<string> ListDatabasesAsync(
        [Description("Connection id from list_connections.")] string connection,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var space = context.RequireSpace(connection);

        var databases = await context.WithConnectionAsync(
            space,
            (db, token) => db.GetDatabaseDetailsAsync(token),
            cancellationToken);

        var payload = DatabaseInfo.InDisplayOrder(databases)
            .Select(database => new { name = database.Name, accessible = database.IsAccessible })
            .ToList();

        context.Audit("list_databases", space, space.Access, statement: null, "ok", reason: null,
            stopwatch.Elapsed, rows: payload.Count);
        return Serialize(payload);
    }

    [McpServerTool(Name = "get_schema", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Returns tables and views with column names and types, primary keys and foreign keys, so " +
        "joins are written against real relationships instead of guessed ones. Pass 'table' to " +
        "narrow to one table (name or schema.name), or 'include_routines' for stored procedures " +
        "and functions with their parameters.")]
    public async Task<string> GetSchemaAsync(
        [Description("Connection id from list_connections.")] string connection,
        [Description("Database name from list_databases.")] string database,
        [Description("Optional table filter: 'orders' or 'dbo.orders'.")] string? table = null,
        [Description("Include stored procedures and functions. Default false.")] bool? include_routines = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var space = context.RequireSpace(connection);

        var (tables, routines) = await context.WithConnectionAsync(
            space,
            async (db, token) =>
            {
                var schema = await db.GetSchemaAsync(database, token);
                var procedures = include_routines == true
                    ? await db.GetRoutinesAsync(database, token)
                    : [];
                return (schema, procedures);
            },
            cancellationToken);

        var filtered = string.IsNullOrWhiteSpace(table)
            ? tables
            : tables.Where(candidate => Matches(candidate, table)).ToList();

        var payload = new
        {
            database,
            tables = filtered.Select(candidate => new
            {
                schema = candidate.Schema,
                name = candidate.Name,
                kind = candidate.Kind == TableKind.View ? "view" : "table",
                columns = candidate.ColumnDetails.Count > 0
                    ? candidate.ColumnDetails.Select(column => new
                    {
                        name = column.Name,
                        type = column.DataType,
                        nullable = column.IsNullable,
                    })
                    : candidate.Columns.Select(name => new { name, type = (string?)null, nullable = (bool?)null }),
                primaryKey = candidate.PrimaryKey,
                foreignKeys = candidate.ForeignKeys.Select(key => new
                {
                    column = key.Column,
                    constraint = key.Constraint,
                    references = new
                    {
                        schema = key.ReferencedSchema,
                        table = key.ReferencedTable,
                        column = key.ReferencedColumn,
                    },
                }),
            }),
            routines = include_routines == true
                ? routines.Select(routine => new
                {
                    schema = routine.Schema,
                    name = routine.Name,
                    kind = routine.Kind == RoutineKind.Function ? "function" : "procedure",
                    parameters = routine.Parameters.Select(parameter => new
                    {
                        name = parameter.Name,
                        type = parameter.DataType,
                    }),
                })
                : null,
        };

        context.Audit("get_schema", space, space.Access, statement: table, "ok", reason: null,
            stopwatch.Elapsed, rows: filtered.Count);
        return Serialize(payload);
    }

    [McpServerTool(Name = "run_query", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Runs SQL on an exposed connection and returns result sets (columns, rows, a per-set " +
        "'truncated' flag) plus engine messages. Read-only workspaces are enforced by the server: " +
        "writes and session control are refused there. Write workspaces require the user's " +
        "approval: in Larik when it is running, otherwise in this client when it can ask.")]
    public async Task<string> RunQueryAsync(
        [Description("Connection id from list_connections.")] string connection,
        [Description("The SQL to run.")] string query,
        [Description("Database to run against; defaults to the connection's saved database.")] string? database = null,
        [Description("Rows per result set: 200 by default, 1000 maximum.")] int? row_limit = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var space = context.RequireSpace(connection);

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("The query is empty.");
        }

        if (query.Length > StudioContext.MaxStatementChars)
        {
            throw new McpException(
                $"The statement is {query.Length:N0} characters; the limit is " +
                $"{StudioContext.MaxStatementChars:N0}. Split it into smaller statements.");
        }

        var provider = context.RequireProvider(space);
        var readOnly = space.Access == McpAccess.Read;
        string? surface = null;

        if (readOnly)
        {
            RequireProvenRead(space, provider, query);
        }
        else
        {
            // A write needs a human. The studio is the only place that can ask one, so when it is
            // closed the statement is refused rather than run on the saved login's permissions.
            var decision = await RequestWriteApprovalAsync(space, query, cancellationToken);
            surface = decision.Surface;

            if (!decision.Approved)
            {
                context.Audit("run_query", space, space.Access, query, "denied",
                    decision.Reason, stopwatch.Elapsed, rows: 0, surface);
                throw new McpException(decision.Reason ?? "The user did not approve this statement.");
            }
        }

        var target = string.IsNullOrWhiteSpace(database) ? DefaultDatabase(space) : database;
        var limit = Math.Clamp(row_limit ?? StudioContext.DefaultRowLimit, 1, StudioContext.MaxRowLimit);

        // An agent must not be able to hold a server statement open indefinitely: the workspace's own
        // timeout is the user's setting (and may be 0 = wait forever), so the server caps it here.
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        execution.CancelAfter(StudioContext.MaxStatementDuration);

        IReadOnlyList<QueryResult> results;
        try
        {
            results = await context.WithConnectionAsync(
                space,
                (db, token) => db.ExecuteBatchAsync(
                    query,
                    target,
                    new QueryExecutionOptions { MaxRows = limit, ReadOnly = true },
                    token),
                execution.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            context.Audit("run_query", space, space.Access, query, "error",
                "statement exceeded the server limit", stopwatch.Elapsed, rows: 0, surface);

            throw new McpException(
                $"The statement was stopped after {StudioContext.MaxStatementDuration.TotalSeconds:N0} " +
                "seconds. Narrow the query (a key or date filter), or run it in the studio.");
        }

        var failed = results.FirstOrDefault(result => result.IsError);
        var rows = results.Sum(result => result.Rows.Count);

        // A provider may translate cancellation into an error result instead of throwing. Either way,
        // when our own cap fired the agent deserves the reason, not the engine's raw text.
        if (execution.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            context.Audit("run_query", space, space.Access, query, "error",
                "statement exceeded the server limit", stopwatch.Elapsed, rows: 0, surface);

            throw new McpException(
                $"The statement was stopped after {StudioContext.MaxStatementDuration.TotalSeconds:N0} " +
                "seconds. Narrow the query (a key or date filter), or run it in the studio.");
        }

        context.Audit(
            "run_query",
            space,
            space.Access,
            query,
            failed is null ? "ok" : "error",
            failed?.Message,
            stopwatch.Elapsed,
            rows,
            surface);

        if (failed is not null)
        {
            // An agent can act on this: the engine's own message, not a generic failure.
            throw new McpException(failed.Message);
        }

        var payload = new RunQueryPayload
        {
            Connection = connection,
            Database = target,
            RowLimit = limit,
            ResultSets = results.Select(result => new ToolResultSet
            {
                Columns = result.Columns.Select(column => new ToolColumn(column.Name, column.DataType)).ToList(),
                Rows = result.Rows.Select(row => row.ToArray()).ToList(),
                RecordsAffected = result.RecordsAffected,
                Truncated = result.IsTruncated,
                Message = result.Message,
                ElapsedMs = (long)result.Elapsed.TotalMilliseconds,
            }).ToList(),
            Messages = results.SelectMany(result => result.Messages).ToList(),
            GeneratedQuery = results.Select(result => result.GeneratedQuery).FirstOrDefault(value => value is not null),
        };

        var counts = payload.ResultSets.Select(set => set.Rows.Count).ToArray();
        payload.TruncatedForSize = McpResponseBudget.Trim(
            payload.ResultSets.Select(set => set.Rows),
            () => System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload, Json))) > 0;

        for (var index = 0; index < payload.ResultSets.Count; index++)
        {
            if (payload.ResultSets[index].Rows.Count < counts[index])
            {
                payload.ResultSets[index].Truncated = true;
            }
        }

        return Serialize(payload);
    }

    /// <summary>One result set as the agent sees it: mutable rows so the size budget can shorten it.</summary>
    private sealed class ToolResultSet
    {
        public required IReadOnlyList<ToolColumn> Columns { get; init; }

        public required List<string?[]> Rows { get; init; }

        public int RecordsAffected { get; init; }

        public bool Truncated { get; set; }

        public string Message { get; init; } = string.Empty;

        public long ElapsedMs { get; init; }
    }

    private sealed record ToolColumn(string Name, string? Type);

    private sealed class RunQueryPayload
    {
        public required string Connection { get; init; }

        public string? Database { get; init; }

        public int RowLimit { get; init; }

        public required List<ToolResultSet> ResultSets { get; init; }

        public required List<string> Messages { get; init; }

        public string? GeneratedQuery { get; init; }

        /// <summary>True when the byte budget, not the row limit, shortened the result.</summary>
        public bool TruncatedForSize { get; set; }
    }

    /// <summary>
    /// Refuses a statement the provider cannot prove is a read, with the reason and the two ways
    /// forward. Fail-closed: an old provider with no policy refuses read-mode queries entirely.
    /// </summary>
    /// <summary>
    /// Puts one write in front of a human. The studio owns the dialog, the notification and the
    /// timeout; this only parks the request and waits for the answer. No studio means no approval:
    /// the statement is refused instead of running with the saved login's permissions.
    /// </summary>
    /// <remarks>
    /// Client-side elicitation (MCP "input_required") would be the tier for a closed studio, but the
    /// attribute-based tool pipeline wraps <c>InputRequiredException</c> into a generic JSON-RPC
    /// error instead of returning an input-required result (verified against a 2026-07-28 session).
    /// Shipping that would turn every write into "An error occurred", so refusal stands until
    /// run_query is registered as a low-level handler, where the documented pattern applies.
    /// </remarks>
    private async Task<McpApprovalDecision> RequestWriteApprovalAsync(
        McpSpaceRecord space,
        string query,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("n");

        context.Store.CreateMcpApproval(new McpApprovalRecord
        {
            Id = id,
            SpaceId = space.Id,
            SpaceName = space.Name,
            Statement = query.Length > StudioContext.MaxStatementChars
                ? query[..StudioContext.MaxStatementChars]
                : query,
        });

        var decision = await ApprovalChannel.TryRequestAsync(
            new McpApprovalRequest
            {
                Id = id,
                SpaceId = space.Id,
                SpaceName = space.Name,
                ProviderId = space.ProviderId,
                Statement = query,
            },
            cancellationToken);

        if (decision is not null)
        {
            context.Store.ResolveMcpApproval(id, decision.Status, decision.Surface);
            return decision;
        }

        context.Store.ResolveMcpApproval(id, McpApprovalStatus.Orphaned, surface: null);
        return McpApprovalDecision.Denied(
            id,
            McpApprovalStatus.Orphaned,
            $"'{space.Name}' is exposed for reading and writing, and a write needs the user's " +
            "approval inside Larik SQL Studio. The studio is not running, so the statement was " +
            "refused. Start Larik, or run the statement there.");
    }

    private void RequireProvenRead(McpSpaceRecord space, IDatabaseProvider provider, string query)
    {
        var policy = provider.ReadOnlyPolicy;
        if (policy is null)
        {
            context.Audit("run_query", space, space.Access, query, "denied",
                "provider cannot prove statements read-only", TimeSpan.Zero, rows: 0);

            throw new McpException(
                $"'{space.Name}' is exposed as read-only, but the {provider.DisplayName} provider " +
                "cannot prove statements read-only in this build, so nothing is executed. Update " +
                "the provider, or use a workspace exposed for reading and writing.");
        }

        if (policy.IsProvenRead(query))
        {
            return;
        }

        var preview = query.Length <= 300 ? query : query[..300] + "…";
        context.Audit("run_query", space, space.Access, query, "denied",
            "statement is not provably read-only", TimeSpan.Zero, rows: 0);

        throw new McpException(
            $"'{space.Name}' is read-only for agents, and this statement is not provably a read: " +
            $"{preview} Writes, DDL, session control and multi-statement batches that mix reads " +
            "with anything else are refused. To write, ask the user to expose the workspace for " +
            "reading and writing, or to run the statement in Larik SQL Studio.");
    }

    private static bool Matches(TableSchema candidate, string filter)
    {
        var qualified = string.IsNullOrEmpty(candidate.Schema)
            ? candidate.Name
            : $"{candidate.Schema}.{candidate.Name}";
        return string.Equals(candidate.Name, filter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(qualified, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DefaultDatabase(McpSpaceRecord space) =>
        space.Profile.TryGetValue("database", out var database) && !string.IsNullOrWhiteSpace(database)
            ? database
            : null;

    /// <summary>The profile without secrets, for descriptions that must never leak credentials.</summary>
    private static ConnectionProfile VisibleProfile(McpSpaceRecord space)
    {
        var profile = new ConnectionProfile { ProviderId = space.ProviderId };
        foreach (var pair in space.Profile)
        {
            profile.Values[pair.Key] = pair.Value;
        }

        return profile;
    }

    private static string Serialize<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        if (json.Length <= McpResponseBudget.MaxBytes)
        {
            return json;
        }

        // The row budget could not get under the limit (one enormous cell, or a wall of messages):
        // tell the agent what happened instead of sending a payload it cannot use.
        return JsonSerializer.Serialize(
            new
            {
                truncated = true,
                message =
                    $"The result exceeded {McpResponseBudget.MaxBytes:N0} bytes even after dropping rows. " +
                    "Select fewer columns, cast large values, or aggregate on the server.",
            },
            Json);
    }
}
