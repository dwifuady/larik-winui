namespace Larik.Data;

/// <summary>How a provider setting should be rendered when configuring a connection.</summary>
public enum ProviderSettingKind
{
    Text,
    Password,
    Number,
    Boolean,
}

/// <summary>
/// A connection field declared by a provider, so the UI can render any provider's
/// configuration without knowing about it. This is the seam that makes providers pluggable.
/// </summary>
public sealed class ProviderSetting
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public ProviderSettingKind Kind { get; init; } = ProviderSettingKind.Text;
    public bool Required { get; init; }
    public string? DefaultValue { get; init; }
    public string? Placeholder { get; init; }
    public string? Hint { get; init; }

    /// <summary>
    /// Rare tuning fields (port, encryption, timeouts) are tucked into the dialog's Advanced
    /// section instead of competing with the server and credentials.
    /// </summary>
    public bool IsAdvanced { get; init; }
}

/// <summary>
/// The user's saved values for one connection. Provider-declared passwords live in Windows
/// Credential Manager, not in the SQLite state file.
/// </summary>
public sealed class ConnectionProfile
{
    public required string ProviderId { get; init; }

    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback) =>
        bool.TryParse(Get(key), out var value) ? value : fallback;

    public ConnectionProfile Clone()
    {
        var clone = new ConnectionProfile { ProviderId = ProviderId };
        foreach (var pair in Values)
        {
            clone.Values[pair.Key] = pair.Value;
        }

        return clone;
    }
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

public sealed class QueryColumn
{
    public required string Name { get; init; }
    public string? DataType { get; init; }
}

/// <summary>Row-limit defaults shared by the shell and every provider (same shape as the Tauri app).</summary>
public static class QueryLimits
{
    public const int DefaultMaxRows = 5_000;
    public const int MaxAllowedRows = 100_000;

    /// <summary>0 and negatives mean "use the default"; the ceiling is always enforced.</summary>
    public static int Normalize(int maxRows) =>
        Math.Min(maxRows <= 0 ? DefaultMaxRows : maxRows, MaxAllowedRows);
}

/// <summary>Per-statement options. Providers stop reading after <see cref="MaxRows"/> + 1 rows.</summary>
public sealed class QueryExecutionOptions
{
    public static QueryExecutionOptions Default { get; } = new();

    public int MaxRows { get; init; } = QueryLimits.DefaultMaxRows;

    public int EffectiveMaxRows => QueryLimits.Normalize(MaxRows);
}

/// <summary>
/// The session's transaction state as reported by the engine. SQL Server fills
/// <see cref="TransactionCount"/> from <c>@@TRANCOUNT</c> and <see cref="XactState"/> from
/// <c>XACT_STATE()</c> (1 = committable, 0 = no transaction, -1 = doomed).
/// </summary>
public readonly record struct SessionTransactionState(int TransactionCount, int XactState)
{
    public static SessionTransactionState None { get; } = new(0, 0);

    public bool HasTransaction => TransactionCount > 0;

    /// <summary>True when the transaction can only be rolled back (SQL Server XACT_STATE() = -1).</summary>
    public bool IsDoomed => XactState < 0;

    public bool CanCommit => TransactionCount > 0 && XactState == 1;

    public bool CanRollback => TransactionCount > 0;
}

/// <summary>One executed statement: its grid, its message, and how long it took.</summary>
public sealed class QueryResult
{
    public required IReadOnlyList<QueryColumn> Columns { get; init; }
    public required IReadOnlyList<string?[]> Rows { get; init; }
    public int RecordsAffected { get; init; }
    public required string Message { get; init; }
    public bool IsError { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Database { get; init; }

    /// <summary>True when the provider stopped at its row cap; <see cref="RowLimit"/> says where.</summary>
    public bool IsTruncated { get; init; }

    public int? RowLimit { get; init; }

    /// <summary>
    /// The native query actually executed, when a provider compiles the user's text into something
    /// else (for example a MongoDB aggregation pipeline compiled from a SQL SELECT). Null when the
    /// executed text is what the user wrote.
    /// </summary>
    public string? GeneratedQuery { get; init; }

    public static QueryResult Error(string message, TimeSpan elapsed, string? database = null) => new()
    {
        Columns = [],
        Rows = [],
        Message = message,
        IsError = true,
        Elapsed = elapsed,
        Database = database,
    };
}

/// <summary>
/// A foreign key on a table: <see cref="Column"/> points at <see cref="ReferencedTable"/>.<see cref="ReferencedColumn"/>.
/// <see cref="Constraint"/> groups composite keys.
/// </summary>
public sealed record ForeignKeySchema(
    string Constraint,
    string Column,
    string ReferencedTable,
    string ReferencedColumn)
{
    /// <summary>
    /// Schema of the referenced table, when the engine has schemas. An init property rather than a
    /// constructor parameter on purpose: external providers built against the four-argument
    /// constructor keep working.
    /// </summary>
    public string? ReferencedSchema { get; init; }
}

/// <summary>Tables and columns, used to feed completion after a connection opens.</summary>
public sealed class TableSchema
{
    public required string Name { get; init; }

    /// <summary>Schema the table lives in ("dbo", "public"), when the engine has schemas.</summary>
    public string? Schema { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Primary-key columns in key order; empty when the provider cannot report them.</summary>
    public IReadOnlyList<string> PrimaryKey { get; init; } = [];

    /// <summary>Foreign keys declared on this table, used to suggest join conditions.</summary>
    public IReadOnlyList<ForeignKeySchema> ForeignKeys { get; init; } = [];
}

/// <summary>
/// A database provider (SQL Server today, PostgreSQL or MongoDB later). Implement this in a
/// separate assembly and drop it into the Plugins folder — no app changes required.
/// <para>
/// Contract compatibility rule: this interface is loaded by plugins built against older versions,
/// so changes are ADD-ONLY and every added member needs a default implementation (see
/// <see cref="EditorLanguage"/> and <see cref="IDatabaseConnection.ExecuteBatchAsync"/>). Never
/// rename or remove a member, and never add positional parameters to contract records; add nullable
/// init properties instead. Breaking these rules makes previously working plugins fail at runtime.
/// </para>
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>Stable id stored with the connection, e.g. "mssql".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Dialect id used for snippet/feature scoping ("mssql", "postgres", "mongodb").</summary>
    string Dialect { get; }

    /// <summary>Fields the configuration UI renders for this provider.</summary>
    IReadOnlyList<ProviderSetting> Settings { get; }

    /// <summary>
    /// The editor language this provider owns: tokenizing, statement splitting and (for SQL)
    /// completion metadata. Null means the host's generic SQL fallback, which is the historical
    /// behaviour, so providers written before this member existed keep working unchanged.
    /// </summary>
    IEditorLanguage? EditorLanguage => null;

    IDatabaseConnection CreateConnection(ConnectionProfile profile);
}

/// <summary>One open connection. A provider owns its own client library and pooling.</summary>
public interface IDatabaseConnection : IAsyncDisposable
{
    ConnectionState State { get; }

    string? ServerVersion { get; }

    /// <summary>Opens (or re-opens) the underlying connection. Throws when the server refuses.</summary>
    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Databases visible on this connection.</summary>
    Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The database the open session is using, when the engine can report it. A script's
    /// <c>USE</c> changes this; the shell adopts the name so the tab, rail, database picker and
    /// completion follow it. Null means "unknown", which keeps older providers working.
    /// </summary>
    Task<string?> GetCurrentDatabaseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    Task<QueryResult> ExecuteAsync(string sql, string? database, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes with an explicit row cap. The default implementation keeps older providers working;
    /// new providers should stop reading at <see cref="QueryExecutionOptions.EffectiveMaxRows"/> + 1.
    /// </summary>
    Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(sql, database, cancellationToken);

    /// <summary>
    /// Executes a batch and returns one result per result set (one result tab each). Providers
    /// that support native multiple result sets should override this; the default keeps older
    /// providers working with a single result.
    /// </summary>
    async Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default) =>
        [await ExecuteAsync(sql, database, options, cancellationToken)];

    Task<IReadOnlyList<TableSchema>> GetSchemaAsync(string? database, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the provider can report and finish transactions the user started in SQL.
    /// Providers without transaction support keep the default and the shell hides the guard.
    /// </summary>
    bool SupportsTransactions => false;

    /// <summary>
    /// Reads the session's transaction depth. SQL Server uses <c>@@TRANCOUNT</c> and
    /// <c>XACT_STATE()</c>; other providers can map their own introspection.
    /// </summary>
    Task<SessionTransactionState> GetTransactionStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SessionTransactionState.None);

    /// <summary>Commits the user's open transaction. Check <see cref="SupportsTransactions"/> first.</summary>
    Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support transactions.");

    /// <summary>Rolls back the user's open transaction. Check <see cref="SupportsTransactions"/> first.</summary>
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support transactions.");
}
