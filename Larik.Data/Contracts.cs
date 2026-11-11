namespace Larik.Data;

/// <summary>How a provider setting should be rendered when configuring a connection.</summary>
public enum ProviderSettingKind
{
    Text,
    Password,
    Number,
    Boolean,

    /// <summary>
    /// A local file path (a SQLite database, for example). The dialog keeps the path editable and
    /// adds a Browse… action, so values a picker cannot express — an in-memory token or a path
    /// pasted from a script — still work. Appended last so older hosts fall back to a text box.
    /// </summary>
    File,
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
    /// Extensions the browse dialog offers for a <see cref="ProviderSettingKind.File"/> field,
    /// without the leading dot (for example <c>["db", "sqlite"]</c>). Empty means any file. These
    /// are a picker hint, not validation: the path field stays authoritative, and the picker always
    /// allows any file because a file-based engine may accept any name.
    /// </summary>
    public IReadOnlyList<string> FileExtensions { get; init; } = [];

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
/// One equality predicate for a filtered row lookup (for example a foreign-key reference). The
/// value is the already-formatted cell text; the dialect turns it into a SQL literal.
/// </summary>
public sealed record RowFilter(string Column, string? Value, string? DataType = null);

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

    /// <summary>
    /// Informational output the engine sent around the result: <c>PRINT</c>, <c>RAISERROR</c> up to
    /// severity 10, <c>SET STATISTICS IO/TIME</c> and optimizer warnings. Empty for providers (and
    /// engines) without messages; additive, so older providers keep working.
    /// </summary>
    public IReadOnlyList<string> Messages { get; init; } = [];

    public int? RowLimit { get; init; }

    /// <summary>
    /// The native query actually executed, when a provider compiles the user's text into something
    /// else (for example a MongoDB aggregation pipeline compiled from a SQL SELECT). Null when the
    /// executed text is what the user wrote.
    /// </summary>
    public string? GeneratedQuery { get; init; }

    /// <summary>
    /// The statement/batch that produced this result, when the provider can report it. The shell
    /// uses it to map result columns back to their source table (foreign-key previews) without
    /// guessing from whatever is in the editor now. Null keeps older providers working.
    /// </summary>
    public string? Statement { get; init; }

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
/// One database a connection exposes. <see cref="IsAccessible"/> separates the names the login can
/// actually open from the ones it can only see, so the selectors can put the usable databases first
/// without hiding the rest.
/// </summary>
public sealed record DatabaseInfo(string Name, bool IsAccessible = true)
{
    /// <summary>
    /// The order both database selectors use: databases the login can open first, then the rest,
    /// alphabetical inside each group. A <paramref name="preferred"/> name moves to the top of its
    /// own group so the current tab's database stays the first Enter target.
    /// </summary>
    public static IEnumerable<DatabaseInfo> InDisplayOrder(
        IEnumerable<DatabaseInfo> databases,
        string? preferred = null) =>
        databases
            .OrderByDescending(database => database.IsAccessible)
            .ThenByDescending(database => string.Equals(database.Name, preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(database => database.Name, StringComparer.OrdinalIgnoreCase);
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

/// <summary>What a <see cref="TableSchema"/> describes. Views browse and complete like tables.</summary>
public enum TableKind
{
    Table,
    View,
}

/// <summary>
/// A column with the engine-reported data type. Types are optional: a provider that cannot report
/// them leaves them null and the browser falls back to the name-only list.
/// </summary>
public sealed class TableColumn
{
    public required string Name { get; init; }

    /// <summary>Type as written in DDL, e.g. <c>nvarchar(120)</c>, <c>numeric(18,2)</c>, <c>int</c>.</summary>
    public string? DataType { get; init; }

    /// <summary>Null when the provider cannot tell.</summary>
    public bool? IsNullable { get; init; }
}

/// <summary>Tables, views and columns, used to feed completion and the schema browser.</summary>
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

    /// <summary>
    /// Base table or view. Defaults to a table, so a provider written before this member existed
    /// keeps browsing and completing exactly as before.
    /// </summary>
    public TableKind Kind { get; init; } = TableKind.Table;

    /// <summary>
    /// Columns with their types, when the provider can report them. <see cref="Columns"/> stays the
    /// name-only projection completion and older code use.
    /// </summary>
    public IReadOnlyList<TableColumn> ColumnDetails { get; init; } = [];
}

/// <summary>The kind of stored routine a <see cref="RoutineSchema"/> describes.</summary>
public enum RoutineKind
{
    Procedure,
    Function,
}

/// <summary>One routine parameter as the engine stores it.</summary>
public sealed class RoutineParameter
{
    /// <summary>Name including the engine's prefix, e.g. <c>@id</c> for T-SQL or <c>p_id</c> for PostgreSQL.</summary>
    public required string Name { get; init; }

    public string? DataType { get; init; }

    public bool IsOutput { get; init; }

    public bool HasDefault { get; init; }
}

/// <summary>
/// A stored procedure or function: what completion offers after EXEC/CALL and what the schema
/// browser lists. Parameters are ordered as the engine defines them.
/// </summary>
public sealed class RoutineSchema
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public RoutineKind Kind { get; init; } = RoutineKind.Procedure;

    public IReadOnlyList<RoutineParameter> Parameters { get; init; } = [];
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

    /// <summary>
    /// A short description of where this connection points (for example <c>"PostgreSQL · db:5432"</c>),
    /// shown in the rail's connection status line. Null means the host shows
    /// <see cref="DisplayName"/> alone, so an older provider keeps its current label.
    /// </summary>
    string? DescribeEndpoint(ConnectionProfile profile) => null;

    /// <summary>
    /// The identity the saved profile connects as, shown in the workspace list (for example a login
    /// name, or <c>"Windows authentication"</c> when the provider uses integrated security). Null
    /// means the provider has nothing to add, so older providers keep working unchanged. This is
    /// the configured identity, not necessarily the server's live session user.
    /// </summary>
    string? DescribeIdentity(ConnectionProfile profile) => null;

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
    /// Databases visible on this connection, marking the ones the login cannot open. Optional: the
    /// default reports every name from <see cref="GetDatabasesAsync"/> as accessible, so providers
    /// written before this member existed keep working and keep their database list unchanged.
    /// </summary>
    async Task<IReadOnlyList<DatabaseInfo>> GetDatabaseDetailsAsync(CancellationToken cancellationToken = default) =>
        (await GetDatabasesAsync(cancellationToken))
            .Select(name => new DatabaseInfo(name))
            .ToList();

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
    /// Stored procedures and functions for completion and the schema browser. Optional: the default
    /// returns nothing, so a provider that cannot enumerate routines keeps working. Kept separate
    /// from <see cref="GetSchemaAsync"/> so browsing tables never waits on routine metadata.
    /// </summary>
    Task<IReadOnlyList<RoutineSchema>> GetRoutinesAsync(string? database, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RoutineSchema>>([]);

    /// <summary>
    /// A cheap row-count estimate for one table, used to choose between listing a small reference
    /// table and looking up only the matching rows in a large one. Optional: null means "unknown",
    /// which keeps older providers working (the shell then does a filtered lookup).
    /// </summary>
    Task<long?> EstimateRowCountAsync(
        string? database,
        string? schema,
        string table,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<long?>(null);

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
