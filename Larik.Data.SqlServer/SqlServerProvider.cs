using System.Data;
using System.Diagnostics;
using System.Globalization;
using Larik.Data;
using Microsoft.Data.SqlClient;

namespace Larik.Data.SqlServer;

/// <summary>
/// SQL Server provider. A connection is one server; the databases it exposes are listed from
/// <c>sys.databases</c> and queried with <c>USE</c>-style database switching on the same connection.
/// </summary>
public sealed class SqlServerProvider : IDatabaseProvider
{
    public const string ProviderId = "mssql";

    public string Id => ProviderId;

    public string DisplayName => "SQL Server";

    public string Dialect => ProviderId;

    public IEditorLanguage? EditorLanguage => TSqlDialect.Instance;

    public string? DescribeEndpoint(ConnectionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Get("server"))
            ? null
            : $"SQL Server · {profile.Get("server")}";

    public IReadOnlyList<ProviderSetting> Settings { get; } =
    [
        new()
        {
            Key = "server",
            Label = "Server",
            Required = true,
            Placeholder = @"localhost\SQLEXPRESS or (localdb)\MSSQLLocalDB",
            DefaultValue = @"(localdb)\MSSQLLocalDB",
        },
        new()
        {
            Key = "port",
            Label = "Port",
            Kind = ProviderSettingKind.Number,
            Placeholder = "optional, e.g. 1433",
            IsAdvanced = true,
        },
        new() { Key = "database", Label = "Initial database", Placeholder = "optional, e.g. master" },
        new() { Key = "user", Label = "User", Placeholder = "leave empty for integrated security" },
        new() { Key = "password", Label = "Password", Kind = ProviderSettingKind.Password },
        new()
        {
            Key = "encrypt",
            Label = "Encrypt",
            Kind = ProviderSettingKind.Boolean,
            DefaultValue = "true",
            IsAdvanced = true,
        },
        new()
        {
            Key = "trustServerCertificate",
            Label = "Trust server certificate",
            Kind = ProviderSettingKind.Boolean,
            DefaultValue = "true",
            Hint = "Needed for local/dev servers with self-signed certificates.",
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

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new SqlServerConnection(profile);
}

internal sealed class SqlServerConnection : IDatabaseConnection
{
    private readonly ConnectionProfile _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _messages = [];
    private readonly object _messageGate = new();
    private SqlConnection? _connection;

    public SqlServerConnection(ConnectionProfile profile) => _profile = profile;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? ServerVersion { get; private set; }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        State = ConnectionState.Connecting;

        try
        {
            _connection ??= new SqlConnection(BuildConnectionString())
            {
                // PRINT always arrives as InfoMessage; this also reports RAISERROR up to severity 10
                // as a message instead of throwing it as a client exception, the way SSMS shows it.
                FireInfoMessageEventOnUserErrors = true,
            };

            if (_connection.State != System.Data.ConnectionState.Open)
            {
                // Attach once per connection instance: re-subscribing would multiply the handler.
                _connection.InfoMessage -= OnInfoMessage;
                _connection.InfoMessage += OnInfoMessage;
                await _connection.OpenAsync(cancellationToken);
            }

            ServerVersion = _connection.ServerVersion;
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
            if (_connection is not null)
            {
                await _connection.CloseAsync();
            }

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
    /// <c>sys.databases</c> can show a name the login cannot open (HAS_DBACCESS returns 0), so the
    /// pickers can keep those after the usable ones instead of letting them clutter the top.
    /// </summary>
    public async Task<IReadOnlyList<DatabaseInfo>> GetDatabaseDetailsAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT name, COALESCE(HAS_DBACCESS(name), 0)
                FROM sys.databases
                WHERE state_desc = 'ONLINE'
                ORDER BY name;
                """;

            var databases = new List<DatabaseInfo>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                databases.Add(new DatabaseInfo(reader.GetString(0), reader.GetInt32(1) == 1));
            }

            return databases;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetCurrentDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not { State: System.Data.ConnectionState.Open })
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME();";
            command.CommandTimeout = 5;
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TableSchema>> GetSchemaAsync(string? database, CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SwitchDatabase(database);

            var columns = await ReadColumnsAsync(cancellationToken);
            var primaryKeys = await ReadPrimaryKeysAsync(cancellationToken);
            var foreignKeys = await ReadForeignKeysAsync(cancellationToken);

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
        catch (SqlException)
        {
            // Schema is a completion nicety; a failure must not break the session.
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool SupportsTransactions => true;

    public async Task<SessionTransactionState> GetTransactionStateAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT @@TRANCOUNT, XACT_STATE();";
            command.CommandTimeout = 5;

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new SessionTransactionState(
                    Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture))
                : SessionTransactionState.None;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
        ExecuteControlAsync("COMMIT TRANSACTION;", cancellationToken);

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
        ExecuteControlAsync("ROLLBACK TRANSACTION;", cancellationToken);

    private async Task ExecuteControlAsync(string sql, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One table's identity while the three introspection passes are stitched together.</summary>
    private sealed record TableInfo(string Schema, string Name, bool IsView, List<TableColumn> Columns);

    private static string QualifiedKey(string schema, string table) => $"{schema}\u001F{table}";

    private async Task<Dictionary<string, TableInfo>> ReadColumnsAsync(CancellationToken cancellationToken)
    {
        // sys.objects covers base tables and views in one pass; type "V" is the only thing that
        // separates them, and sys.columns carries the same column metadata for both.
        await using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.name, o.name, o.type, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = o.object_id
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE o.type IN ('U', 'V')
            ORDER BY s.name, o.name, c.column_id;
            """;

        var tables = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var key = QualifiedKey(schema, name);
            if (!tables.TryGetValue(key, out var info))
            {
                var isView = reader.GetString(2).Trim().Equals("V", StringComparison.OrdinalIgnoreCase);
                info = new TableInfo(schema, name, isView, []);
                tables[key] = info;
            }

            info.Columns.Add(new TableColumn
            {
                Name = reader.GetString(3),
                DataType = FormatDataType(
                    reader.GetString(4),
                    reader.GetInt16(5),
                    reader.GetByte(6),
                    reader.GetByte(7)),
                IsNullable = reader.GetBoolean(8),
            });
        }

        return tables;
    }

    /// <summary>
    /// A type as it would be written in DDL. Lengths are in bytes in sys.columns, so the national
    /// character types divide by two; -1 means <c>(max)</c>.
    /// </summary>
    private static string FormatDataType(string name, short maxLength, byte precision, byte scale) =>
        name.ToLowerInvariant() switch
        {
            "char" or "varchar" or "binary" or "varbinary" =>
                maxLength < 0 ? $"{name}(max)" : $"{name}({maxLength})",
            "nchar" or "nvarchar" =>
                maxLength < 0 ? $"{name}(max)" : $"{name}({maxLength / 2})",
            "decimal" or "numeric" => $"{name}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" =>
                scale == 0 ? name : $"{name}({scale})",
            _ => name,
        };

    /// <summary>Procedures in the current database, with parameters in declaration order.</summary>
    public async Task<IReadOnlyList<RoutineSchema>> GetRoutinesAsync(
        string? database,
        CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SwitchDatabase(database);
            return await ReadRoutinesAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // Routine metadata is a completion/browsing nicety; a permission failure must not
            // break the session.
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<RoutineSchema>> ReadRoutinesAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.name, p.name, pa.name, ty.name, pa.max_length, pa.precision, pa.scale,
                   pa.is_output, pa.has_default_value
            FROM sys.procedures AS p
            INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
            LEFT JOIN sys.parameters AS pa ON pa.object_id = p.object_id
            LEFT JOIN sys.types AS ty ON ty.user_type_id = pa.user_type_id
            ORDER BY s.name, p.name, pa.parameter_id;
            """;
        var order = new List<string>();
        var builders = new Dictionary<string, (string Schema, string Name, List<RoutineParameter> Parameters)>(
            StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var key = QualifiedKey(schema, name);

            if (!builders.TryGetValue(key, out var builder))
            {
                builder = (schema, name, []);
                builders[key] = builder;
                order.Add(key);
            }

            // A procedure without parameters still produces one LEFT JOIN row with NULL metadata.
            if (reader.IsDBNull(2))
            {
                continue;
            }

            builder.Parameters.Add(new RoutineParameter
            {
                Name = reader.GetString(2),
                DataType = reader.IsDBNull(3)
                    ? null
                    : FormatDataType(
                        reader.GetString(3),
                        reader.GetInt16(4),
                        reader.GetByte(5),
                        reader.GetByte(6)),
                IsOutput = reader.GetBoolean(7),
                HasDefault = reader.GetBoolean(8),
            });
        }

        return order
            .Select(key =>
            {
                var builder = builders[key];
                return new RoutineSchema
                {
                    Schema = builder.Schema,
                    Name = builder.Name,
                    Parameters = builder.Parameters,
                };
            })
            .ToList();
    }

    /// <summary>sys.partitions row estimate: metadata only, no table scan.</summary>
    public async Task<long?> EstimateRowCountAsync(
        string? database,
        string? schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SwitchDatabase(database);

            var qualified = string.IsNullOrWhiteSpace(schema)
                ? TSqlDialect.Instance.QuoteIdentifier(table)
                : $"{TSqlDialect.Instance.QuoteIdentifier(schema)}.{TSqlDialect.Instance.QuoteIdentifier(table)}";
            var objectName = qualified.Replace("'", "''");

            await using var command = _connection!.CreateCommand();
            command.CommandText = $"""
                SELECT SUM(p.[rows])
                FROM sys.partitions AS p
                WHERE p.[object_id] = OBJECT_ID(N'{objectName}')
                  AND p.[index_id] IN (0, 1);
                """;
            command.CommandTimeout = 5;

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull
                ? null
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            // The estimate only picks a preview mode; a permission or metadata failure means
            // "unknown", which falls back to the filtered lookup.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, List<string>>> ReadPrimaryKeysAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.name, t.name, c.name
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes AS i ON i.object_id = t.object_id AND i.is_primary_key = 1
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = t.object_id AND c.column_id = ic.column_id
            ORDER BY s.name, t.name, ic.key_ordinal;
            """;

        var keys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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

    private async Task<Dictionary<string, List<ForeignKeySchema>>> ReadForeignKeysAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT sp.name, tp.name, cp.name, sr.name, tr.name, cr.name, fk.name
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.tables AS tp ON tp.object_id = fkc.parent_object_id
            INNER JOIN sys.schemas AS sp ON sp.schema_id = tp.schema_id
            INNER JOIN sys.columns AS cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            INNER JOIN sys.tables AS tr ON tr.object_id = fkc.referenced_object_id
            INNER JOIN sys.schemas AS sr ON sr.schema_id = tr.schema_id
            INNER JOIN sys.columns AS cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            ORDER BY sp.name, tp.name, fk.name, fkc.constraint_column_id;
            """;

        var references = new Dictionary<string, List<ForeignKeySchema>>(StringComparer.OrdinalIgnoreCase);
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

    public async Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var rowLimit = options.EffectiveMaxRows;
        var results = new List<QueryResult>();

        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Anything that arrived between commands (a schema load, a transaction probe) is not
            // this run's output.
            ClearMessages();

            SwitchDatabase(database);

            await using var command = _connection!.CreateCommand();
            command.CommandText = sql;
            // The provider's timeout setting is the user's one control here: a report that needs
            // minutes must not die at a value the app chose. 0 means wait indefinitely.
            command.CommandTimeout = Math.Max(0, _profile.GetInt("queryTimeout", 60));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var affectedBefore = 0;
            do
            {
                var affected = reader.RecordsAffected;
                if (reader.FieldCount > 0)
                {
                    results.Add(await ReadResultSetAsync(
                        reader,
                        database,
                        sql,
                        rowLimit,
                        stopwatch,
                        cancellationToken));
                }
                else
                {
                    var delta = affected > affectedBefore ? affected - affectedBefore : 0;
                    results.Add(new QueryResult
                    {
                        Columns = [],
                        Rows = [],
                        RecordsAffected = delta,
                        Message = delta > 0 ? $"{delta} rows affected" : "Statement completed",
                        Elapsed = stopwatch.Elapsed,
                        Database = database,
                    });
                }

                affectedBefore = Math.Max(affectedBefore, affected);
            }
            while (await reader.NextResultAsync(cancellationToken));
        }
        catch (SqlException ex)
        {
            results.Add(QueryResult.Error(ex.Message, stopwatch.Elapsed, database));
        }
        catch (InvalidOperationException ex)
        {
            results.Add(QueryResult.Error(ex.Message, stopwatch.Elapsed, database));
        }
        finally
        {
            _gate.Release();
        }

        // One Messages tab per batch: a message can arrive between result sets, so attributing it
        // to an individual set would be guesswork. Errors keep their own result and messages follow.
        var messages = TakeMessages();
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

        if (results.Count == 0)
        {
            results.Add(new QueryResult
            {
                Columns = [],
                Rows = [],
                Message = "Statement completed",
                Elapsed = stopwatch.Elapsed,
                Database = database,
            });
        }

        return results;
    }

    // ---------------------------------------------------------------- server messages

    /// <summary>
    /// PRINT, RAISERROR up to severity 10, SET STATISTICS and engine warnings. SqlClient raises this
    /// while the command runs; the gate keeps one command in flight per connection, and the lock
    /// covers an event that arrives on a worker thread.
    /// </summary>
    private void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
    {
        lock (_messageGate)
        {
            foreach (SqlError error in e.Errors)
            {
                _messages.Add(FormatMessage(error));
            }
        }
    }

    private static string FormatMessage(SqlError error)
    {
        var text = error.Message.TrimEnd();

        // PRINT arrives as number 0 / severity 0, so the text is the whole message. RAISERROR and
        // warnings carry a number and level (SqlError.Class); SSMS shows those as a header line.
        return error.Number == 0 && error.Class <= 0
            ? text
            : $"Msg {error.Number}, Level {error.Class}, State {error.State}, Line {error.LineNumber}: {text}";
    }

    private List<string> TakeMessages()
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

    private void ClearMessages()
    {
        lock (_messageGate)
        {
            _messages.Clear();
        }
    }

    private static async Task<QueryResult> ReadResultSetAsync(
        SqlDataReader reader,
        string? database,
        string sql,
        int rowLimit,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var columns = new List<QueryColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new QueryColumn
            {
                Name = reader.GetName(i),
                DataType = reader.GetDataTypeName(i),
            });
        }

        var rows = new List<string?[]>();
        var truncated = false;

        // Read one row past the cap only to know whether more data existed; the provider
        // never materializes the rest of a large result.
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
                values[i] = Format(reader.GetValue(i));
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
            Statement = sql,
            IsTruncated = truncated,
            RowLimit = rowLimit,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();
        State = ConnectionState.Disconnected;
    }

    private void SwitchDatabase(string? database)
    {
        if (string.IsNullOrWhiteSpace(database) || _connection is null)
        {
            return;
        }

        if (!string.Equals(_connection.Database, database, StringComparison.OrdinalIgnoreCase))
        {
            _connection.ChangeDatabase(database);
        }
    }

    private string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = ComposeDataSource(),
            ApplicationName = "Larik SQL Studio",
            ConnectTimeout = Math.Max(1, _profile.GetInt("connectTimeout", 8)),
            Encrypt = _profile.GetBool("encrypt", true),
            TrustServerCertificate = _profile.GetBool("trustServerCertificate", true),
            Pooling = true,
        };

        var database = _profile.Get("database");
        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.InitialCatalog = database;
        }

        var user = _profile.Get("user");
        if (string.IsNullOrWhiteSpace(user))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = user;
            builder.Password = _profile.Get("password") ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    private string ComposeDataSource()
    {
        var server = _profile.Get("server");
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new InvalidOperationException("The SQL Server connection needs a server name.");
        }

        var port = _profile.GetInt("port", 0);
        if (port <= 0)
        {
            return server;
        }

        // LocalDB and named instances already carry their own instance suffix.
        return server.Contains('\\') ? server : $"{server},{port}";
    }

    private static string? Format(object value) => value switch
    {
        DBNull or null => null,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
