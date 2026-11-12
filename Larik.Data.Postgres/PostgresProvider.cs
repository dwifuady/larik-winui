using System.Diagnostics;
using System.Globalization;
using System.Text;
using Larik.Data;
using Npgsql;

namespace Larik.Data.Postgres;

/// <summary>
/// PostgreSQL provider. A connection is one server; the databases it exposes are listed from
/// <c>pg_database</c>. PostgreSQL cannot switch database on a live connection, so this provider
/// keeps one physical backend per database and selects the one a command names.
/// </summary>
public sealed class PostgresProvider : IDatabaseProvider
{
    public const string ProviderId = "postgres";

    public string Id => ProviderId;

    public string DisplayName => "PostgreSQL";

    public string Dialect => ProviderId;

    public IEditorLanguage? EditorLanguage => PostgresDialect.Instance;

    public IReadOnlyStatementPolicy? ReadOnlyPolicy => PostgresReadOnlyPolicy.Instance;

    public string? DescribeEndpoint(ConnectionProfile profile)
    {
        var host = profile.Get("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var port = profile.GetInt("port", 5432);
        return $"PostgreSQL · {host}:{port}";
    }

    public string? DescribeIdentity(ConnectionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Get("user")) ? null : profile.Get("user")!.Trim();

    public IReadOnlyList<ProviderSetting> Settings { get; } =
    [
        new()
        {
            Key = "host",
            Label = "Host",
            Required = true,
            DefaultValue = "localhost",
            Placeholder = "localhost or a host name",
        },
        new()
        {
            Key = "port",
            Label = "Port",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "5432",
            IsAdvanced = true,
        },
        new()
        {
            Key = "database",
            Label = "Database",
            DefaultValue = "postgres",
            Placeholder = "postgres",
        },
        new()
        {
            Key = "user",
            Label = "User",
            DefaultValue = "postgres",
            Placeholder = "postgres",
        },
        new() { Key = "password", Label = "Password", Kind = ProviderSettingKind.Password },
        new()
        {
            Key = "sslMode",
            Label = "SSL mode",
            DefaultValue = "Prefer",
            Hint = "Disable, Allow, Prefer, Require, VerifyCA or VerifyFull.",
            IsAdvanced = true,
        },
        new()
        {
            Key = "connectTimeout",
            Label = "Connect timeout (s)",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "8",
            IsAdvanced = true,
        },
        new()
        {
            Key = "queryTimeout",
            Label = "Query timeout (s)",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "60",
            Hint = "How long a statement may run before the client cancels it. 0 waits indefinitely.",
            IsAdvanced = true,
        },
    ];

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new PostgresConnection(profile);
}

internal sealed class PostgresConnection : IDatabaseConnection
{
    private const string DefaultDatabase = "postgres";

    private readonly ConnectionProfile _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PhysicalSession> _sessions = new(StringComparer.Ordinal);
    private string _controlDatabase = DefaultDatabase;
    private PhysicalSession? _control;
    private PhysicalSession? _active;
    private string? _activeDatabase;

    public PostgresConnection(ConnectionProfile profile) => _profile = profile;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? ServerVersion { get; private set; }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens the control database while the caller already holds <see cref="_gate"/>. Every session
    /// mutation goes through this one lock, so a connect cannot race a database refresh and create
    /// two backends (or corrupt the session map). Metadata probes pass
    /// <paramref name="activate"/> false so reopening a dead control backend cannot steal the
    /// session that a transaction commit/rollback targets.
    /// </summary>
    private async Task EnsureOpenCoreAsync(CancellationToken cancellationToken, bool activate = true)
    {
        if (State == ConnectionState.Connected && _control is { } open && IsOpen(open))
        {
            return;
        }

        State = ConnectionState.Connecting;

        try
        {
            _controlDatabase = ResolveDatabase(_profile.Get("database"));
            _control = await GetPhysicalCoreAsync(_controlDatabase, cancellationToken, activate);
            ServerVersion = _control.Connection.ServerVersion;
            State = ConnectionState.Connected;
        }
        catch
        {
            State = ConnectionState.Faulted;
            throw;
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var session in _sessions.Values)
            {
                try
                {
                    await session.Connection.CloseAsync();
                    await session.Connection.DisposeAsync();
                }
                catch
                {
                    // Disconnecting is best effort: one broken backend must not block the rest.
                }
            }

            _sessions.Clear();
            _control = null;
            _active = null;
            _activeDatabase = null;
            ServerVersion = null;
            State = ConnectionState.Disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        (await GetDatabaseDetailsAsync(cancellationToken))
            .Select(database => database.Name)
            .ToList();

    /// <summary>
    /// <c>has_database_privilege</c> separates the names the login can actually open from the ones
    /// it can only see, so the pickers put the usable databases first without hiding the rest.
    /// </summary>
    public async Task<IReadOnlyList<DatabaseInfo>> GetDatabaseDetailsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenCoreAsync(cancellationToken, activate: false);
            var session = await EnsureControlCoreAsync(cancellationToken);
            await using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT datname,
                       datallowconn AND has_database_privilege(current_user, datname, 'CONNECT')
                FROM pg_catalog.pg_database
                WHERE NOT datistemplate
                ORDER BY datname;
                """;
            command.CommandTimeout = 15;

            var databases = new List<DatabaseInfo>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                databases.Add(new DatabaseInfo(reader.GetString(0), !reader.IsDBNull(1) && reader.GetBoolean(1)));
            }

            return databases;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// PostgreSQL cannot <c>USE</c> another database, so the session's database only changes when
    /// the shell opens a command against a different one. Reporting the active backend keeps the
    /// tab, rail and picker honest.
    /// </summary>
    public Task<string?> GetCurrentDatabaseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(State == ConnectionState.Connected ? _activeDatabase ?? _controlDatabase : null);

    public async Task<IReadOnlyList<TableSchema>> GetSchemaAsync(string? database, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenCoreAsync(cancellationToken, activate: false);

            // A missing/unreachable database is a real error; only the catalog reads below are
            // allowed to degrade to an empty schema.
            var session = await GetPhysicalCoreAsync(database, cancellationToken, activate: false);

            try
            {
                var columns = await ReadColumnsAsync(session, cancellationToken);
                var primaryKeys = await ReadPrimaryKeysAsync(session, cancellationToken);
                var foreignKeys = await ReadForeignKeysAsync(session, cancellationToken);

                return columns.Values
                    .Select(info =>
                    {
                        var key = QualifiedKey(info.Schema, info.Name);
                        return new TableSchema
                        {
                            Name = info.Name,
                            Schema = info.Schema,
                            Kind = info.IsView ? TableKind.View : TableKind.Table,
                            Columns = info.Columns.Select(column => column.Name).ToList(),
                            ColumnDetails = info.Columns,
                            PrimaryKey = primaryKeys.TryGetValue(key, out var keys) ? keys : [],
                            ForeignKeys = foreignKeys.TryGetValue(key, out var references) ? references : [],
                        };
                    })
                    .ToList();
            }
            catch (PostgresException)
            {
                // Schema is a completion nicety; a permission failure must not break the session.
                return [];
            }
            catch (NpgsqlException)
            {
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One table's identity while the three catalog passes are stitched together.</summary>
    private sealed record TableInfo(string Schema, string Name, bool IsView, List<TableColumn> Columns);

    private static string QualifiedKey(string schema, string table) => $"{schema}\u001F{table}";

    private static async Task<Dictionary<string, TableInfo>> ReadColumnsAsync(
        PhysicalSession session,
        CancellationToken cancellationToken)
    {
        // pg_class covers ordinary, partitioned and foreign tables plus views and materialized
        // views in one pass; relkind is the only thing that separates them.
        await using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT n.nspname,
                   c.relname,
                   c.relkind,
                   a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull
            FROM pg_catalog.pg_class AS c
            INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            INNER JOIN pg_catalog.pg_attribute AS a ON a.attrelid = c.oid
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg\_%'
            ORDER BY n.nspname, c.relname, a.attnum;
            """;
        command.CommandTimeout = 30;

        var tables = new Dictionary<string, TableInfo>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var key = QualifiedKey(schema, name);
            if (!tables.TryGetValue(key, out var info))
            {
                var relkind = reader.GetChar(2);
                var isView = relkind is 'v' or 'm';
                info = new TableInfo(schema, name, isView, []);
                tables[key] = info;
            }

            info.Columns.Add(new TableColumn
            {
                Name = reader.GetString(3),
                DataType = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsNullable = !reader.IsDBNull(5) && reader.GetBoolean(5),
            });
        }

        return tables;
    }

    private static async Task<Dictionary<string, List<string>>> ReadPrimaryKeysAsync(
        PhysicalSession session,
        CancellationToken cancellationToken)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT n.nspname, c.relname, a.attname
            FROM pg_catalog.pg_constraint AS con
            INNER JOIN pg_catalog.pg_class AS c ON c.oid = con.conrelid
            INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            INNER JOIN pg_catalog.pg_attribute AS a
                ON a.attrelid = c.oid AND a.attnum = ANY(con.conkey)
            WHERE con.contype = 'p'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg\_%'
            ORDER BY n.nspname, c.relname, array_position(con.conkey, a.attnum);
            """;
        command.CommandTimeout = 30;

        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = QualifiedKey(reader.GetString(0), reader.GetString(1));
            if (!keys.TryGetValue(key, out var columns))
            {
                columns = [];
                keys[key] = columns;
            }

            columns.Add(reader.GetString(2));
        }

        return keys;
    }

    private static async Task<Dictionary<string, List<ForeignKeySchema>>> ReadForeignKeysAsync(
        PhysicalSession session,
        CancellationToken cancellationToken)
    {
        // unnest keeps composite keys aligned by ordinality, so the pairing is exact for multicolumn FKs.
        await using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT sn.nspname, sc.relname, sa.attname,
                   tn.nspname, tc.relname, ta.attname,
                   con.conname
            FROM pg_catalog.pg_constraint AS con
            INNER JOIN pg_catalog.pg_class AS sc ON sc.oid = con.conrelid
            INNER JOIN pg_catalog.pg_namespace AS sn ON sn.oid = sc.relnamespace
            INNER JOIN pg_catalog.pg_class AS tc ON tc.oid = con.confrelid
            INNER JOIN pg_catalog.pg_namespace AS tn ON tn.oid = tc.relnamespace
            INNER JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS ck(attnum, ord) ON TRUE
            INNER JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS fk(attnum, ord) ON fk.ord = ck.ord
            INNER JOIN pg_catalog.pg_attribute AS sa
                ON sa.attrelid = con.conrelid AND sa.attnum = ck.attnum
            INNER JOIN pg_catalog.pg_attribute AS ta
                ON ta.attrelid = con.confrelid AND ta.attnum = fk.attnum
            WHERE con.contype = 'f'
              AND sn.nspname NOT IN ('pg_catalog', 'information_schema')
              AND sn.nspname NOT LIKE 'pg\_%'
            ORDER BY sn.nspname, sc.relname, con.conname, ck.ord;
            """;
        command.CommandTimeout = 30;

        var references = new Dictionary<string, List<ForeignKeySchema>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = QualifiedKey(reader.GetString(0), reader.GetString(1));
            if (!references.TryGetValue(key, out var foreignKeys))
            {
                foreignKeys = [];
                references[key] = foreignKeys;
            }

            foreignKeys.Add(new ForeignKeySchema(
                reader.GetString(6),
                reader.GetString(2),
                reader.GetString(4),
                reader.GetString(5))
            {
                ReferencedSchema = reader.GetString(3),
            });
        }

        return references;
    }

    /// <summary>Procedures and functions in the requested database, with call parameters ordered.</summary>
    public async Task<IReadOnlyList<RoutineSchema>> GetRoutinesAsync(
        string? database,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenCoreAsync(cancellationToken, activate: false);

            // Opening the database must surface a real failure; only the catalog read degrades.
            var session = await GetPhysicalCoreAsync(database, cancellationToken, activate: false);

            try
            {
                return await ReadRoutinesAsync(session, cancellationToken);
            }
            catch (PostgresException)
            {
                // Routine metadata is a browsing nicety; a permission failure must not break the session.
                return [];
            }
            catch (NpgsqlException)
            {
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<IReadOnlyList<RoutineSchema>> ReadRoutinesAsync(
        PhysicalSession session,
        CancellationToken cancellationToken)
    {
        // information_schema gives the callable parameters for both procedures and functions. Pure
        // OUT parameters are not arguments, so they are filtered out; INOUT is still passed in.
        await using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT r.routine_schema,
                   r.routine_name,
                   r.specific_name,
                   r.routine_type,
                   p.ordinal_position,
                   p.parameter_name,
                   p.data_type,
                   p.udt_name,
                   p.parameter_mode,
                   p.parameter_default
            FROM information_schema.routines AS r
            LEFT JOIN information_schema.parameters AS p
                ON p.specific_schema = r.specific_schema
               AND p.specific_name = r.specific_name
               AND (p.parameter_mode IS NULL OR p.parameter_mode IN ('IN', 'INOUT'))
            WHERE r.routine_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY r.routine_schema, r.routine_name, r.specific_name, p.ordinal_position;
            """;
        command.CommandTimeout = 30;

        var order = new List<string>();
        var builders = new Dictionary<string, RoutineBuilder>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var specificName = reader.GetString(2);
            var key = QualifiedKey(schema, name);

            if (!builders.TryGetValue(key, out var builder))
            {
                var kind = reader.GetString(3).Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase)
                    ? RoutineKind.Procedure
                    : RoutineKind.Function;
                builder = new RoutineBuilder(schema, name, specificName, kind);
                builders[key] = builder;
                order.Add(key);
            }
            else if (!string.Equals(builder.SpecificName, specificName, StringComparison.Ordinal))
            {
                // Overloads share a name; the contract identifies a routine by schema + name, so
                // keep the first overload's signature rather than mixing two parameter lists.
                continue;
            }

            if (reader.IsDBNull(4))
            {
                continue;
            }

            var ordinal = reader.GetInt32(4);
            var parameterName = reader.IsDBNull(5) ? $"${ordinal}" : reader.GetString(5);
            var dataType = reader.IsDBNull(6) ? null : reader.GetString(6);
            var udtName = reader.IsDBNull(7) ? null : reader.GetString(7);

            // information_schema reports every enum/domain/array as USER-DEFINED or ARRAY; the
            // underlying type name is the useful detail in the schema browser.
            if (dataType is "USER-DEFINED" or "ARRAY" && udtName is { Length: > 0 })
            {
                dataType = dataType == "ARRAY" && udtName[0] == '_'
                    ? $"{udtName[1..]}[]"
                    : udtName;
            }

            builder.Parameters.Add(new RoutineParameter
            {
                Name = parameterName,
                DataType = dataType,
                HasDefault = !reader.IsDBNull(9),
            });
        }

        return order
            .Select(key => builders[key].ToSchema())
            .ToList();
    }

    private sealed record RoutineBuilder(
        string Schema,
        string Name,
        string SpecificName,
        RoutineKind Kind)
    {
        public List<RoutineParameter> Parameters { get; } = [];

        public RoutineSchema ToSchema() => new()
        {
            Schema = Schema,
            Name = Name,
            Kind = Kind,
            Parameters = Parameters,
        };
    }

    /// <summary><c>pg_class.reltuples</c> is a metadata-only estimate; -1 means "not analyzed".</summary>
    public async Task<long?> EstimateRowCountAsync(
        string? database,
        string? schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenCoreAsync(cancellationToken, activate: false);
            var session = await GetPhysicalCoreAsync(database, cancellationToken, activate: false);
            await using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT c.reltuples::bigint
                FROM pg_catalog.pg_class AS c
                INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
                WHERE n.nspname = COALESCE(NULLIF(@schema, ''), current_schema())
                  AND c.relname = @table
                  AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                ORDER BY c.oid
                LIMIT 1;
                """;
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue("schema", schema ?? string.Empty);
            command.Parameters.AddWithValue("table", table);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null or DBNull)
            {
                return null;
            }

            var estimate = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return estimate < 0 ? null : estimate;
        }
        catch (NpgsqlException)
        {
            // The estimate only chooses a preview mode; an unknown value falls back to a filtered lookup.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(sql, database, QueryExecutionOptions.Default, cancellationToken);

    public async Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = await ExecuteBatchAsync(sql, database, options, cancellationToken);
        return results.Count > 0
            ? results[0]
            : QueryResult.Error("No result returned.", TimeSpan.Zero, database);
    }

    /// <summary>
    /// PostgreSQL has no <c>GO</c>; the editor's <see cref="IEditorLanguage.SplitBatches"/> therefore
    /// sends the document as one batch. Execution still splits it on semicolons (outside strings,
    /// quoted identifiers, comments and dollar quotes) so every statement reports its own result and
    /// a failure can be attributed to the statement that caused it.
    /// </summary>
    public async Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenCoreAsync(cancellationToken);
            var session = await GetPhysicalCoreAsync(database, cancellationToken);
            session.ClearMessages();

            var statements = PostgresDialect.Instance.Split(sql);
            if (statements.Count == 0)
            {
                return [QueryResult.Error("Nothing to run — the editor is empty.", stopwatch.Elapsed, database)];
            }

            var results = new List<QueryResult>(statements.Count);

            // Read-only exposure (an agent reading through a saved connection): a server-side
            // read-only transaction makes PostgreSQL itself reject writes and DDL, so the statement
            // gate run by the host is the second line rather than the only one. It is rolled back
            // in the finally and never committed.
            var readOnly = options.ReadOnly == true;
            try
            {
                if (readOnly)
                {
                    await using var begin = session.Connection.CreateCommand();
                    begin.CommandText = "BEGIN TRANSACTION READ ONLY";
                    await begin.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var span in statements)
                {
                    var statement = sql.Substring(span.Start, span.Length);
                    var result = await ExecuteStatementAsync(
                        session,
                        statement,
                        database,
                        options,
                        stopwatch,
                        cancellationToken);

                    results.Add(result);
                    ApplyTransactionCommand(session, statement, succeeded: !result.IsError);

                    if (result.IsError)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (readOnly)
                {
                    try
                    {
                        await using var rollback = session.Connection.CreateCommand();
                        rollback.CommandText = "ROLLBACK";
                        await rollback.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // A connection that already died ended the transaction server-side.
                    }
                }
            }

            // One Messages tab per batch: a notice can arrive between statements, so attributing it
            // to an individual result would be guesswork. Errors keep their own result.
            var messages = session.TakeMessages();
            if (messages.Count > 0)
            {
                results.Add(new QueryResult
                {
                    Columns = [],
                    Rows = [],
                    Message = $"{messages.Count} message{(messages.Count == 1 ? string.Empty : "s")}",
                    Messages = messages,
                    Elapsed = stopwatch.Elapsed,
                    Database = database,
                });
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            // A cancelled statement aborts any explicit transaction it ran inside.
            MarkTransactionAborted();
            throw;
        }
        catch (NpgsqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            MarkTransactionAborted();
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            return [QueryResult.Error(ex.Message, stopwatch.Elapsed, database)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MarkTransactionAborted()
    {
        if (_active is { } session && session.Transaction.HasTransaction)
        {
            session.Transaction = new SessionTransactionState(1, -1);
        }
    }

    private async Task<QueryResult> ExecuteStatementAsync(
        PhysicalSession session,
        string statement,
        string? database,
        QueryExecutionOptions options,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = session.Connection.CreateCommand();
            command.CommandText = statement;
            // The provider's timeout setting is the user's one control: 0 means wait indefinitely.
            command.CommandTimeout = Math.Max(0, _profile.GetInt("queryTimeout", 60));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (reader.FieldCount == 0)
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    // No columns: drain the (empty) reader so RecordsAffected is final.
                }

                var affected = reader.RecordsAffected;
                return new QueryResult
                {
                    Columns = [],
                    Rows = [],
                    RecordsAffected = affected > 0 ? affected : 0,
                    Message = affected > 0 ? $"{affected} rows affected" : "Statement completed",
                    Elapsed = stopwatch.Elapsed,
                    Database = database,
                    Statement = statement,
                };
            }

            var columns = new List<QueryColumn>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(new QueryColumn
                {
                    Name = reader.GetName(i),
                    DataType = reader.GetDataTypeName(i),
                });
            }

            var rowLimit = options.EffectiveMaxRows;
            var rows = new List<string?[]>();
            var truncated = false;

            // Read one row past the cap only to know whether more data existed; the provider never
            // materializes the rest of a large result.
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= rowLimit)
                {
                    truncated = true;
                    break;
                }

                var values = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : Format(reader.GetValue(i));
                }

                rows.Add(values);
            }

            return new QueryResult
            {
                Columns = columns,
                Rows = rows,
                Message = $"{rows.Count} rows",
                Elapsed = stopwatch.Elapsed,
                Database = database,
                Statement = statement,
                IsTruncated = truncated,
                RowLimit = rowLimit,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException ex)
        {
            return QueryResult.Error(FormatError(ex), stopwatch.Elapsed, database);
        }
        catch (NpgsqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, database);
        }
        catch (InvalidOperationException ex)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, database);
        }
    }

    public bool SupportsTransactions => true;

    /// <summary>
    /// PostgreSQL does not expose "am I in a transaction?" to the session running the query (its own
    /// <c>pg_stat_activity</c> row is <c>active</c> while the probe runs). The provider therefore
    /// tracks the transaction commands it sent: BEGIN/START opens, COMMIT/END/ROLLBACK/ABORT closes,
    /// and a failed statement while open means the transaction is aborted and rollback-only.
    /// </summary>
    public Task<SessionTransactionState> GetTransactionStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_active?.Transaction ?? SessionTransactionState.None);

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
        ExecuteTransactionControlAsync("COMMIT;", cancellationToken);

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
        ExecuteTransactionControlAsync("ROLLBACK;", cancellationToken);

    private async Task ExecuteTransactionControlAsync(string sql, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenCoreAsync(cancellationToken, activate: false);

            // _active is only moved by execution and Open, never by a metadata probe, so COMMIT and
            // ROLLBACK land on the backend the tab actually ran against. A backend the driver has
            // already marked broken is replaced; its transaction died with it.
            var session = _active is { } active && IsOpen(active)
                ? active
                : await GetPhysicalCoreAsync(_activeDatabase, cancellationToken);
            await using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken);
            session.Transaction = SessionTransactionState.None;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- transaction tracking

    private static void ApplyTransactionCommand(PhysicalSession session, string sql, bool succeeded) =>
        session.Transaction = PostgresTransactionTracker.Apply(session.Transaction, sql, succeeded);

    // ---------------------------------------------------------------- physical sessions

    private sealed class PhysicalSession(NpgsqlConnection connection)
    {
        private readonly List<string> _messages = [];
        private readonly object _messageGate = new();

        public NpgsqlConnection Connection { get; } = connection;

        public SessionTransactionState Transaction { get; set; } = SessionTransactionState.None;

        public void AddMessage(string message)
        {
            lock (_messageGate)
            {
                _messages.Add(message);
            }
        }

        public void ClearMessages()
        {
            lock (_messageGate)
            {
                _messages.Clear();
            }
        }

        public List<string> TakeMessages()
        {
            lock (_messageGate)
            {
                if (_messages.Count == 0)
                {
                    return [];
                }

                var copy = new List<string>(_messages);
                _messages.Clear();
                return copy;
            }
        }
    }

    private async Task<PhysicalSession> EnsureControlCoreAsync(CancellationToken cancellationToken) =>
        _control is { } control && IsOpen(control)
            ? control
            : await GetPhysicalCoreAsync(_controlDatabase, cancellationToken, activate: false);

    /// <summary>
    /// Returns the backend for <paramref name="database"/>, opening it on demand. Only execution
    /// paths pass <paramref name="activate"/> true: a schema or routine probe must never move the
    /// session that a later COMMIT/ROLLBACK targets.
    /// </summary>
    private async Task<PhysicalSession> GetPhysicalCoreAsync(
        string? database,
        CancellationToken cancellationToken,
        bool activate = true)
    {
        var name = ResolveDatabase(database);

        if (_sessions.TryGetValue(name, out var existing))
        {
            if (IsOpen(existing))
            {
                if (activate)
                {
                    _active = existing;
                    _activeDatabase = name;
                }

                return existing;
            }

            _sessions.Remove(name);
            if (ReferenceEquals(_control, existing))
            {
                _control = null;
            }

            try
            {
                await existing.Connection.DisposeAsync();
            }
            catch
            {
                // A broken backend is already gone as far as the server is concerned.
            }
        }

        var connection = new NpgsqlConnection(BuildConnectionString(name));
        var session = new PhysicalSession(connection);
        connection.Notice += (_, args) => session.AddMessage(FormatNotice(args.Notice));

        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        _sessions[name] = session;

        if (activate)
        {
            _active = session;
            _activeDatabase = name;
        }

        if (string.Equals(name, _controlDatabase, StringComparison.Ordinal))
        {
            _control = session;
        }

        return session;
    }

    private static bool IsOpen(PhysicalSession session) =>
        session.Connection.State == System.Data.ConnectionState.Open;

    private string ResolveDatabase(string? database) =>
        string.IsNullOrWhiteSpace(database) ? _controlDatabase : database.Trim();

    private string BuildConnectionString(string database)
    {
        var host = _profile.Get("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("The PostgreSQL connection needs a host name.");
        }

        var port = _profile.GetInt("port", 5432);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port is > 0 and <= 65535 ? port : 5432,
            Database = database,
            ApplicationName = "Larik SQL Studio",
            Timeout = Math.Max(1, _profile.GetInt("connectTimeout", 8)),
            Pooling = true,
            SslMode = ParseSslMode(_profile.Get("sslMode")),
        };

        var user = _profile.Get("user");
        if (!string.IsNullOrWhiteSpace(user))
        {
            builder.Username = user;
        }

        var password = _profile.Get("password");
        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static SslMode ParseSslMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SslMode.Prefer;
        }

        if (Enum.TryParse<SslMode>(value.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        // Silently downgrading a typo to Prefer would leave the user believing VerifyFull is active.
        throw new InvalidOperationException(
            $"Unknown SSL mode '{value}'. Use Disable, Allow, Prefer, Require, VerifyCA or VerifyFull.");
    }

    private static string FormatError(PostgresException error)
    {
        var builder = new StringBuilder(error.MessageText);

        if (!string.IsNullOrWhiteSpace(error.Detail))
        {
            builder.AppendLine().Append(error.Detail.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(error.Hint))
        {
            builder.AppendLine().Append("Hint: ").Append(error.Hint.TrimEnd());
        }

        builder.Append(" (SQLSTATE ").Append(error.SqlState);
        if (error.Position > 0)
        {
            builder.Append(", position ").Append(error.Position);
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string FormatNotice(PostgresNotice notice)
    {
        var text = notice.MessageText?.TrimEnd() ?? string.Empty;
        var severity = notice.Severity.ToString().ToUpperInvariant();
        var line = severity.Length == 0 ? text : $"{severity}: {text}";

        if (!string.IsNullOrWhiteSpace(notice.Detail))
        {
            line += Environment.NewLine + notice.Detail.TrimEnd();
        }

        return line;
    }

    private static string? Format(object value) => value switch
    {
        null or DBNull => null,
        DateTime { Kind: DateTimeKind.Utc } utc => utc.ToString("yyyy-MM-dd HH:mm:ss.fffffff 'Z'", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString(),
        byte[] bytes => $@"\x{Convert.ToHexString(bytes).ToLowerInvariant()}",
        bool flag => flag ? "true" : "false",
        char character => character.ToString(),
        Array array => FormatArray(array),
        System.Collections.BitArray bits => FormatBits(bits),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>PostgreSQL array text, close enough to the server's own <c>{a,b}</c> output to read.</summary>
    private static string FormatArray(Array array)
    {
        var items = new List<string?>(array.Length);
        foreach (var item in array)
        {
            items.Add(item switch
            {
                null or DBNull => "NULL",
                Array nested => FormatArray(nested),
                _ => Format(item),
            });
        }

        return $"{{{string.Join(",", items)}}}";
    }

    private static string FormatBits(System.Collections.BitArray bits)
    {
        var builder = new StringBuilder(bits.Length);
        for (var i = 0; i < bits.Length; i++)
        {
            builder.Append(bits[i] ? '1' : '0');
        }

        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync();
        }
        catch
        {
            // Disposal never throws: the app is shutting the session down either way.
        }

        _gate.Dispose();
    }
}
