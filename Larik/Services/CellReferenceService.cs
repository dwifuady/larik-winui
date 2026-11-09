using Larik.Data;

namespace Larik.Services;

/// <summary>One column pair of a (possibly composite) foreign key.</summary>
public sealed record ColumnReferencePair(string SourceColumn, string TargetColumn);

/// <summary>
/// A resolved foreign key for one result-set column: where the value points and every column of
/// the constraint, so a composite key can filter on the parts present in the result.
/// </summary>
public sealed class ColumnReference
{
    public required string Constraint { get; init; }

    public required string SourceSchema { get; init; }

    public required string SourceTable { get; init; }

    public required string SourceColumn { get; init; }

    public required string TargetSchema { get; init; }

    public required string TargetTable { get; init; }

    public required string TargetColumn { get; init; }

    public required IReadOnlyList<ColumnReferencePair> Pairs { get; init; }
}

/// <summary>The data one reference preview needs, already resolved into a query result.</summary>
public sealed record CellReferenceSnapshot(
    ColumnReference? Reference,
    QueryResult? Result,
    IReadOnlyList<int> MatchedRows,
    bool IsList,
    long? EstimatedRows,
    bool Truncated,
    bool IsNullValue,
    string? Warning,
    string? Error)
{
    public static CellReferenceSnapshot Empty(string? error = null) =>
        new(null, null, [], false, null, false, false, null, error);

    public string TargetLabel => Reference is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(Reference.TargetSchema)
            ? $"{Reference.TargetTable}.{Reference.TargetColumn}"
            : $"{Reference.TargetSchema}.{Reference.TargetTable}.{Reference.TargetColumn}";
}

/// <summary>
/// The UI's port to reference lookups. ResultsGrid only asks whether a column can be previewed and
/// for the rows; resolving the foreign key and building SQL stays in the service.
/// </summary>
public interface ICellReferenceController
{
    bool CanPreview(int columnIndex);

    /// <summary>Human-readable target for a column header icon, or null when there is no reference.</summary>
    string? Describe(int columnIndex);

    Task<CellReferenceSnapshot> PreviewAsync(
        int rowIndex,
        int columnIndex,
        CancellationToken cancellationToken);
}

/// <summary>
/// Maps result columns back to their source tables and foreign keys. Parsing is lexical (the app
/// has no SQL AST yet), so a column is mapped only when its source table is unambiguous: one known
/// FROM/JOIN table, or exactly one table that owns the column name. Aliased output columns fall
/// back to the value preview rather than risking the wrong reference.
/// </summary>
public static class CellReferenceResolver
{
    public static IReadOnlyDictionary<int, ColumnReference> Resolve(
        QueryResult result,
        string? statement,
        IReadOnlyList<TableSchema> tables)
    {
        var references = new Dictionary<int, ColumnReference>();
        if (result.Columns.Count == 0 || string.IsNullOrWhiteSpace(statement) || tables.Count == 0)
        {
            return references;
        }

        // FindTableReferences works from a caret; a trailing terminator would put the caret in a
        // new empty statement, so trim it before asking.
        var text = statement.Trim();
        while (text.EndsWith(';'))
        {
            text = text[..^1].TrimEnd();
        }

        if (text.Length == 0)
        {
            return references;
        }

        // A batch can contain several statements but QueryResult.Statement is batch-scoped. Mapping
        // columns across them could pick the wrong table, so only a single-statement text is safe.
        if (text.Contains(';'))
        {
            return references;
        }

        var knownTables = CompletionEngine
            .FindTableReferences(text, text.Length, tables)
            .Where(reference => reference.Table is not null)
            .Select(reference => reference.Table!)
            .DistinctBy(TableKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (knownTables.Count == 0)
        {
            return references;
        }

        for (var index = 0; index < result.Columns.Count; index++)
        {
            var column = result.Columns[index].Name;
            var source = FindSourceTable(knownTables, column);
            if (source is null)
            {
                continue;
            }

            var reference = FindReference(source, column);
            if (reference is not null)
            {
                references[index] = reference;
            }
        }

        return references;
    }

    private static TableSchema? FindSourceTable(IReadOnlyList<TableSchema> tables, string column)
    {
        if (tables.Count == 1)
        {
            return tables[0].Columns.Any(candidate => Equal(candidate, column)) ? tables[0] : null;
        }

        var candidates = tables
            .Where(table => table.Columns.Any(candidate => Equal(candidate, column)))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static ColumnReference? FindReference(TableSchema table, string column)
    {
        var groups = table.ForeignKeys
            .GroupBy(key => key.Constraint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToList())
            .Where(group => group.Any(key => Equal(key.Column, column)))
            .OrderBy(group => group.Count)
            .ToList();

        if (groups.Count == 0)
        {
            return null;
        }

        var pairs = groups[0];
        var clicked = pairs.First(key => Equal(key.Column, column));
        return new ColumnReference
        {
            Constraint = clicked.Constraint,
            SourceSchema = table.Schema ?? string.Empty,
            SourceTable = table.Name,
            SourceColumn = clicked.Column,
            TargetSchema = clicked.ReferencedSchema ?? table.Schema ?? string.Empty,
            TargetTable = clicked.ReferencedTable,
            TargetColumn = clicked.ReferencedColumn,
            Pairs = pairs
                .Select(key => new ColumnReferencePair(key.Column, key.ReferencedColumn))
                .ToList(),
        };
    }

    private static string TableKey(TableSchema table) => $"{table.Schema}\u001F{table.Name}";

    private static bool Equal(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves the cell's reference and loads the referenced rows. Small tables are listed in full
/// with the matching rows highlighted; large tables (or unknown estimates) use a filtered lookup
/// so opening a reference never pulls an arbitrary slice of a big table.
/// </summary>
public sealed class CellReferenceController : ICellReferenceController
{
    private const int ListLimit = 200;
    private const int LookupLimit = 25;

    private readonly QueryResult _result;
    private readonly IReadOnlyDictionary<int, ColumnReference> _references;
    private readonly ISqlDialect _dialect;
    private readonly Func<string, int, CancellationToken, Task<QueryResult>> _execute;
    private readonly Func<string?, string, CancellationToken, Task<long?>> _estimate;

    public CellReferenceController(
        QueryResult result,
        string? statement,
        IReadOnlyList<TableSchema> tables,
        ISqlDialect dialect,
        Func<string, int, CancellationToken, Task<QueryResult>> execute,
        Func<string?, string, CancellationToken, Task<long?>> estimate)
    {
        _result = result;
        _dialect = dialect;
        _execute = execute;
        _estimate = estimate;
        _references = CellReferenceResolver.Resolve(result, statement, tables);
    }

    public bool CanPreview(int columnIndex) => _references.ContainsKey(columnIndex);

    public string? Describe(int columnIndex) =>
        _references.TryGetValue(columnIndex, out var reference)
            ? string.IsNullOrWhiteSpace(reference.TargetSchema)
                ? $"References {reference.TargetTable}.{reference.TargetColumn}"
                : $"References {reference.TargetSchema}.{reference.TargetTable}.{reference.TargetColumn}"
            : null;

    public async Task<CellReferenceSnapshot> PreviewAsync(
        int rowIndex,
        int columnIndex,
        CancellationToken cancellationToken)
    {
        if (!_references.TryGetValue(columnIndex, out var reference))
        {
            return CellReferenceSnapshot.Empty();
        }

        if (rowIndex < 0 || rowIndex >= _result.Rows.Count)
        {
            return CellReferenceSnapshot.Empty("The selected row is no longer available.");
        }

        var row = _result.Rows[rowIndex];
        var cellValue = columnIndex < row.Length ? row[columnIndex] : null;
        if (cellValue is null)
        {
            return new CellReferenceSnapshot(
                reference, null, [], false, null, false, true, null, null);
        }

        var filters = BuildFilters(reference, row, out var skipped);
        if (filters.Count == 0)
        {
            return new CellReferenceSnapshot(
                reference,
                null,
                [],
                false,
                null,
                false,
                false,
                "The result set does not include the reference key column, so there is nothing to look up.",
                null);
        }

        long? estimate = null;
        try
        {
            estimate = await _estimate(reference.TargetSchema, reference.TargetTable, cancellationToken);
        }
        catch
        {
            // An unreadable estimate only means "unknown"; the lookup path still works.
        }

        var isList = estimate is { } rows && rows <= ListLimit;
        var limit = isList ? ListLimit : LookupLimit;
        var sql = _dialect.RowsStatement(
            reference.TargetSchema,
            reference.TargetTable,
            isList ? [] : filters,
            isList ? reference.TargetColumn : null,
            limit);

        QueryResult result;
        try
        {
            result = await _execute(sql, limit, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CellReferenceSnapshot(
                reference, null, [], isList, estimate, false, false, null, exception.Message);
        }

        if (result.IsError)
        {
            return new CellReferenceSnapshot(
                reference, result, [], isList, estimate, false, false, null, result.Message);
        }

        var matchedRows = FindMatchedRows(result, filters);
        var truncated = result.IsTruncated || result.Rows.Count >= limit;
        var warning = skipped.Count == 0
            ? null
            : $"Composite key: only {filters.Count} of {reference.Pairs.Count} key columns are in this result.";

        return new CellReferenceSnapshot(
            reference,
            result,
            matchedRows,
            isList,
            estimate,
            truncated,
            false,
            warning,
            null);
    }

    private List<RowFilter> BuildFilters(
        ColumnReference reference,
        IReadOnlyList<string?> row,
        out List<string> skipped)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _result.Columns.Count; index++)
        {
            columns[_result.Columns[index].Name] = index;
        }

        var filters = new List<RowFilter>(reference.Pairs.Count);
        skipped = [];
        foreach (var pair in reference.Pairs)
        {
            if (!columns.TryGetValue(pair.SourceColumn, out var sourceIndex))
            {
                skipped.Add(pair.TargetColumn);
                continue;
            }

            filters.Add(new RowFilter(
                pair.TargetColumn,
                sourceIndex < row.Count ? row[sourceIndex] : null,
                _result.Columns[sourceIndex].DataType));
        }

        return filters;
    }

    private static IReadOnlyList<int> FindMatchedRows(QueryResult result, IReadOnlyList<RowFilter> filters)
    {
        if (result.Rows.Count == 0 || filters.Count == 0)
        {
            return [];
        }

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < result.Columns.Count; index++)
        {
            columns[result.Columns[index].Name] = index;
        }

        var matches = new List<int>();
        for (var rowIndex = 0; rowIndex < result.Rows.Count; rowIndex++)
        {
            var row = result.Rows[rowIndex];
            var matchesAll = true;
            foreach (var filter in filters)
            {
                if (!columns.TryGetValue(filter.Column, out var columnIndex)
                    || !ValuesMatch(
                        columnIndex < row.Length ? row[columnIndex] : null,
                        filter.Value))
                {
                    matchesAll = false;
                    break;
                }
            }

            if (matchesAll)
            {
                matches.Add(rowIndex);
            }
        }

        return matches;
    }

    /// <summary>Values arrive as provider-formatted text; compare numerically when both look numeric.</summary>
    private static bool ValuesMatch(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var a = left.Trim();
        var b = right.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return double.TryParse(a, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var numberA)
               && double.TryParse(b, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var numberB)
               && numberA.Equals(numberB);
    }
}
