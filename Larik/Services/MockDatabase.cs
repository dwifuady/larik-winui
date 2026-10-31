using Larik.Models;

namespace Larik.Services;

/// <summary>
/// Stand-in for the real database drivers (tiberius / tokio-postgres / rusqlite).
/// Gives the shell believable schema + results until the ADO.NET/Npgsql layer lands.
/// </summary>
public static class MockDatabase
{
    public static IReadOnlyList<TableDef> Tables { get; } =
    [
        new() { Name = "customers", Columns = ["id", "full_name", "email", "country", "created_at"] },
        new() { Name = "orders", Columns = ["id", "customer_id", "total_amount", "status", "ordered_at"] },
        new() { Name = "order_items", Columns = ["id", "order_id", "product_id", "quantity", "unit_price"] },
        new() { Name = "products", Columns = ["id", "sku", "name", "price", "stock"] },
    ];

    public static IReadOnlyList<string> Words { get; } =
        Tables.SelectMany(t => t.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static ResultSet Execute(string sql)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var trimmed = sql.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new ResultSet { Columns = [], Rows = [], Message = "Nothing to run — the editor is empty." };
        }

        var tableName = FindTargetTable(trimmed);
        var table = tableName is null
            ? null
            : Tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            return new ResultSet
            {
                Columns = [],
                Rows = [],
                Message = tableName is null
                    ? "No table in this statement — SELECT ... FROM <table> to see rows."
                    : $"Invalid object name '{tableName}'.",
                IsError = true,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }

        var rows = GenerateRows(table, 24);
        stopwatch.Stop();
        return new ResultSet
        {
            Columns = table.Columns.Select(Headerize).ToArray(),
            Rows = rows,
            Message = $"{rows.Count} rows",
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
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
                if (candidate.Length > 0 && !candidate.StartsWith('@'))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Headerize(string column) => column.Replace('_', ' ').ToUpperInvariant();

    private static List<string[]> GenerateRows(TableDef table, int count)
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
