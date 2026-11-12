using System.Diagnostics;

namespace Larik.Data.Demo;

/// <summary>
/// In-memory provider for offline use and the UI tests. It is a normal provider:
/// nothing in the app special-cases it.
/// </summary>
public sealed class DemoProvider : IDatabaseProvider
{
    public const string ProviderId = "demo";

    public string Id => ProviderId;

    public string DisplayName => "Demo (in-memory)";

    public string Dialect => "mssql";

    public IReadOnlyStatementPolicy? ReadOnlyPolicy => DemoReadOnlyPolicy.Instance;

    public string? DescribeIdentity(ConnectionProfile profile) => "Demo session";

    public IReadOnlyList<ProviderSetting> Settings { get; } =
    [
        new()
        {
            Key = "databases",
            Label = "Databases",
            DefaultValue = "larik_prod,larik_reports",
            Placeholder = "comma,separated",
            Hint = "Pretend databases exposed by this connection. Prefix a name with ! to show it as visible but not openable.",
        },
    ];

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new DemoConnection(profile);
}

internal sealed class DemoConnection(ConnectionProfile profile) : IDatabaseConnection
{
    private static readonly TableSchema[] Tables =
    [
        new()
        {
            Name = "customers",
            Schema = "dbo",
            Columns = ["id", "full_name", "email", "country", "created_at"],
            PrimaryKey = ["id"],
        },
        new()
        {
            Name = "orders",
            Schema = "dbo",
            Columns = ["id", "customer_id", "total_amount", "status", "ordered_at"],
            PrimaryKey = ["id"],
            ForeignKeys = [new ForeignKeySchema("FK_orders_customers", "customer_id", "customers", "id")],
        },
        new()
        {
            Name = "order_items",
            Schema = "dbo",
            Columns = ["id", "order_id", "product_id", "quantity", "unit_price"],
            PrimaryKey = ["id"],
            ForeignKeys =
            [
                new ForeignKeySchema("FK_order_items_orders", "order_id", "orders", "id"),
                new ForeignKeySchema("FK_order_items_products", "product_id", "products", "id"),
            ],
        },
        new()
        {
            Name = "products",
            Schema = "dbo",
            Columns = ["id", "sku", "name", "price", "stock"],
            PrimaryKey = ["id"],
        },
    ];

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? ServerVersion => State == ConnectionState.Connected ? "Demo 1.0" : null;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(80, cancellationToken);
        State = ConnectionState.Connected;
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        (await GetDatabaseDetailsAsync(cancellationToken))
            .Select(database => database.Name)
            .ToList();

    /// <summary>
    /// The demo has no server-side access rules, so a leading <c>!</c> fakes one: the database is
    /// listed but the login cannot open it. The UI tests use it to cover the disabled selector state.
    /// </summary>
    public async Task<IReadOnlyList<DatabaseInfo>> GetDatabaseDetailsAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(60, cancellationToken);
        var configured = profile.Get("databases") ?? "larik_prod";
        return configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.StartsWith('!')
                ? new DatabaseInfo(name[1..].Trim(), IsAccessible: false)
                : new DatabaseInfo(name))
            .ToList();
    }

    public async Task<IReadOnlyList<TableSchema>> GetSchemaAsync(string? database, CancellationToken cancellationToken = default)
    {
        await Task.Delay(40, cancellationToken);
        return Tables;
    }

    public Task<QueryResult> ExecuteAsync(string sql, string? database, CancellationToken cancellationToken = default) =>
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
        // The demo provider keeps its statement splitter deliberately simple; the SQL Server
        // provider reads native result sets instead of parsing.
        var statements = sql
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => !string.IsNullOrWhiteSpace(statement))
            .ToList();

        if (statements.Count == 0)
        {
            return [QueryResult.Error("Nothing to run — the editor is empty.", TimeSpan.Zero, database)];
        }

        var results = new List<QueryResult>(statements.Count);
        foreach (var statement in statements)
        {
            var result = ExecuteStatement(statement, database, options);
            results.Add(result);

            if (result.IsError)
            {
                break;
            }
        }

        return results;
    }

    private static QueryResult ExecuteStatement(
        string sql,
        string? database,
        QueryExecutionOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var trimmed = sql.Trim();

        var tableName = FindTargetTable(trimmed);
        var table = tableName is null
            ? null
            : Tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            stopwatch.Stop();
            return QueryResult.Error(
                tableName is null
                    ? "No table in this statement — SELECT ... FROM <table> to see rows."
                    : $"Invalid object name '{tableName}'.",
                stopwatch.Elapsed,
                database);
        }

        var rowLimit = options.EffectiveMaxRows;
        var generated = GenerateRows(table, 24).Cast<string?[]>().ToList();
        var truncated = generated.Count > rowLimit;
        var rows = truncated ? generated[..rowLimit] : generated;
        stopwatch.Stop();
        return new QueryResult
        {
            Columns = table.Columns.Select(c => new QueryColumn { Name = Headerize(c) }).ToList(),
            Rows = rows,
            Message = $"{rows.Count} rows",
            Elapsed = stopwatch.Elapsed,
            Database = database,
            Statement = trimmed,
            IsTruncated = truncated,
            RowLimit = rowLimit,
        };
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private static string? FindTargetTable(string sql)
    {
        var tokens = sql.Split([' ', '\t', '\r', '\n', '(', ')', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            var keyword = tokens[i].ToUpperInvariant();
            if (keyword is "FROM" or "JOIN" or "INTO" or "UPDATE" or "TABLE")
            {
                var candidate = tokens[i + 1].Trim('[', ']', '"');

                // "dbo.orders" and "[dbo].[orders]" both name the same demo table.
                var dot = candidate.LastIndexOf('.');
                if (dot >= 0)
                {
                    candidate = candidate[(dot + 1)..].Trim('[', ']', '"');
                }

                if (candidate.Length > 0 && !candidate.StartsWith('@'))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Headerize(string column) => column.Replace('_', ' ').ToUpperInvariant();

    private static List<string[]> GenerateRows(TableSchema table, int count)
    {
        string[] people = ["Ada Lovelace", "Grace Hopper", "Alan Turing", "Katherine Johnson", "Linus Torvalds", "Radia Perlman", "Barbara Liskov", "Ken Thompson"];
        string[] countries = ["ID", "SG", "US", "DE", "JP", "BR", "NL", "GB"];
        string[] statuses = ["paid", "pending", "fulfilled", "refunded"];
        string[] products = ["Mechanical Keyboard", "27\" Monitor", "USB-C Dock", "Studio Mic", "Ergo Mouse", "Laptop Stand", "NVMe Drive", "Webcam"];
        string[] skus = ["KB-01", "MN-27", "DK-04", "MC-11", "MS-02", "LS-08", "NV-1T", "WC-4K"];

        var rows = new List<string[]>(count);
        for (var i = 0; i < count; i++)
        {
            var n = i + 1;
            rows.Add(table.Name switch
            {
                "customers" => [n.ToString(), people[i % people.Length], $"{people[i % people.Length].Split(' ')[0].ToLowerInvariant()}@example.com", countries[i % countries.Length], $"2026-0{(i % 9) + 1}-1{i % 9}"],
                "orders" => [n.ToString(), ((i % 8) + 1).ToString(), (49.9 + i * 23.4).ToString("0.00"), statuses[i % statuses.Length], $"2026-1{i % 2}-0{(i % 9) + 1}"],
                "order_items" => [n.ToString(), ((i % 10) + 1).ToString(), ((i % 8) + 1).ToString(), ((i % 4) + 1).ToString(), (19.9 + i * 7.5).ToString("0.00")],
                "products" => [n.ToString(), skus[i % skus.Length], products[i % products.Length], (89.0 + i * 31.75).ToString("0.00"), ((i * 17) % 120).ToString()],
                _ => table.Columns.Select((_, c) => $"r{n}c{c}").ToArray(),
            });
        }

        return rows;
    }
}
