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
        },
        new()
        {
            Key = "trustServerCertificate",
            Label = "Trust server certificate",
            Kind = ProviderSettingKind.Boolean,
            DefaultValue = "true",
            Hint = "Needed for local/dev servers with self-signed certificates.",
        },
        new()
        {
            Key = "connectTimeout",
            Label = "Connect timeout (s)",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "8",
        },
    ];

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new SqlServerConnection(profile);
}

internal sealed class SqlServerConnection : IDatabaseConnection
{
    private readonly ConnectionProfile _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqlConnection? _connection;

    public SqlServerConnection(ConnectionProfile profile) => _profile = profile;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? ServerVersion { get; private set; }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        State = ConnectionState.Connecting;

        try
        {
            _connection ??= new SqlConnection(BuildConnectionString());
            if (_connection.State != System.Data.ConnectionState.Open)
            {
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

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name;";

            var databases = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                databases.Add(reader.GetString(0));
            }

            return databases;
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

            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT t.name AS table_name, c.name AS column_name
                FROM sys.tables AS t
                INNER JOIN sys.columns AS c ON c.object_id = t.object_id
                ORDER BY t.name, c.column_id;
                """;

            var tables = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                var column = reader.GetString(1);
                if (!tables.TryGetValue(table, out var columns))
                {
                    columns = [];
                    tables[table] = columns;
                }

                columns.Add(column);
            }

            return tables
                .Select(pair => new TableSchema { Name = pair.Key, Columns = pair.Value })
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

    public async Task<QueryResult> ExecuteAsync(string sql, string? database, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SwitchDatabase(database);

            await using var command = _connection!.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var columns = new List<QueryColumn>();
            var rows = new List<string?[]>();

            if (reader.FieldCount > 0)
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(new QueryColumn
                    {
                        Name = reader.GetName(i),
                        DataType = reader.GetDataTypeName(i),
                    });
                }

                while (await reader.ReadAsync(cancellationToken))
                {
                    var values = new string?[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        values[i] = Format(reader.GetValue(i));
                    }

                    if (rows.Count < 5_000)
                    {
                        rows.Add(values);
                    }
                }
            }

            var affected = reader.RecordsAffected;
            stopwatch.Stop();

            var message = columns.Count > 0
                ? $"{rows.Count} rows"
                : $"{Math.Max(affected, 0)} rows affected";

            return new QueryResult
            {
                Columns = columns,
                Rows = rows,
                RecordsAffected = affected,
                Message = message,
                Elapsed = stopwatch.Elapsed,
                Database = database,
            };
        }
        catch (SqlException ex)
        {
            stopwatch.Stop();
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, database);
        }
        catch (InvalidOperationException ex)
        {
            stopwatch.Stop();
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, database);
        }
        finally
        {
            _gate.Release();
        }
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
