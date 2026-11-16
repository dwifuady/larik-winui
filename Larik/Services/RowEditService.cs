using Larik.Data;

namespace Larik.Services;

/// <summary>One cell the user changed in a grid row: the provider column index and its new text.</summary>
public sealed record CellEdit(int ResultIndex, string? Value);

/// <summary>
/// One row's staged change. A delete carries no edits; an update carries only the cells that
/// actually differ, so an untouched value is never written back with less precision.
/// </summary>
public sealed record RowEditRequest(
    int RowIndex,
    bool Delete,
    IReadOnlyList<CellEdit>? Edits = null,
    int? ResultColumn = null);

public enum RowEditKind
{
    Update,
    Delete,
}

/// <summary>One generated statement, kept separate so the dialog can show them one at a time.</summary>
public sealed record RowEditStatement(RowEditKind Kind, string Sql);

/// <summary>A whole save, ready to review and execute as one batch.</summary>
public sealed class RowEditPlan
{
    public required IReadOnlyList<RowEditStatement> Statements { get; init; }

    /// <summary>The single command actually sent: the statements wrapped by the dialect.</summary>
    public required string Batch { get; init; }

    /// <summary>Rows the save touches, so the grid can patch exactly those cells afterwards.</summary>
    public required IReadOnlyList<RowEditRequest> Requests { get; init; }

    public int UpdateCount => Statements.Count(statement => statement.Kind == RowEditKind.Update);

    public int DeleteCount => Statements.Count(statement => statement.Kind == RowEditKind.Delete);

    public int ChangeCount => Statements.Count;
}

/// <summary>What the database said about a save.</summary>
public sealed class RowEditOutcome
{
    public required bool Success { get; init; }

    public required int AffectedRows { get; init; }

    /// <summary>The engine's message on failure; empty on success.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// The grid's port to inline editing: whether a cell can be written, the SQL a save would run, and
/// running it on the tab's own session. ResultsGrid never builds SQL or touches a connection.
/// </summary>
public interface IRowEditController
{
    /// <summary>True when the cell's table column is writable and its row is provably identified.</summary>
    bool CanEdit(int resultColumnIndex);

    /// <summary>"orders.status" for a tooltip, or null when the cell isn't writable.</summary>
    string? Describe(int resultColumnIndex);

    /// <summary>Why the cell can't be edited, in one sentence the status bar can show.</summary>
    string RefuseReason(int resultColumnIndex);

    /// <summary>Builds the statements for a set of changes; null when they can't be built safely.</summary>
    RowEditPlan? Plan(IReadOnlyList<RowEditRequest> requests, out string? error);

    /// <summary>Runs the batch and reports whether it landed in one piece.</summary>
    Task<RowEditOutcome> ApplyAsync(RowEditPlan plan, CancellationToken cancellationToken = default);

    /// <summary>The dialect's literal formatting, so copied SQL and saved SQL agree.</summary>
    string FormatLiteral(string? value, string? dataType);
}

/// <summary>
/// Resolves which table each result column writes to and turns staged cells into UPDATE/DELETE
/// statements. The statements are the dialect's (identifier quoting, literals, transaction wrapper);
/// this type only maps rows, cells and primary keys onto them.
/// </summary>
public sealed class RowEditController : IRowEditController
{
    private readonly QueryResult _result;
    private readonly ISqlDialect _dialect;
    private readonly RowEditResolution _resolution;
    private readonly Dictionary<int, RowEditTarget> _columnTargets = [];
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<QueryResult>>> _execute;

    /// <param name="references">
    /// The result's reference resolver, which already maps columns back to their source table (and
    /// refuses ambiguous ones). Editing depends on that same mapping rather than re-parsing the SQL.
    /// </param>
    /// <param name="execute">Runs one batch on the tab's session, so temp tables and transactions apply.</param>
    public RowEditController(
        QueryResult result,
        string? statement,
        IReadOnlyList<TableSchema> tables,
        ISqlDialect dialect,
        ICellReferenceController references,
        Func<string, CancellationToken, Task<IReadOnlyList<QueryResult>>> execute)
    {
        _result = result;
        _dialect = dialect;
        _execute = execute;

        var origins = new Dictionary<int, ColumnOrigin>();
        for (var index = 0; index < result.Columns.Count; index++)
        {
            if (references.GetSource(index) is { } source)
            {
                origins[index] = new ColumnOrigin(source.Schema, source.Table, source.Column);
            }
        }

        _resolution = RowEditResolver.Resolve(result, statement, tables, origins);
        foreach (var target in _resolution.Targets)
        {
            foreach (var index in target.Columns.Keys)
            {
                _columnTargets[index] = target;
            }
        }
    }

    public bool CanEdit(int resultColumnIndex) => _columnTargets.ContainsKey(resultColumnIndex);

    public string? Describe(int resultColumnIndex) =>
        _columnTargets.TryGetValue(resultColumnIndex, out var target)
        && target.Columns.TryGetValue(resultColumnIndex, out var column)
            ? $"{target.Label}.{column.Column}"
            : null;

    public string RefuseReason(int resultColumnIndex)
    {
        if (_resolution.Reasons.TryGetValue(resultColumnIndex, out var reason))
        {
            return reason;
        }

        return string.IsNullOrWhiteSpace(_resolution.Refusal)
            ? "This column is generated, read-only, or not a plain column of an editable table."
            : _resolution.Refusal;
    }

    public string FormatLiteral(string? value, string? dataType) => _dialect.FormatLiteral(value, dataType);

    public RowEditPlan? Plan(IReadOnlyList<RowEditRequest> requests, out string? error)
    {
        error = null;
        var statements = new List<RowEditStatement>();
        var applied = new List<RowEditRequest>();

        // Updates first: they are the common case and keep the dialog's order predictable. The batch
        // is atomic, so the order is for reading, not for retry safety.
        foreach (var request in requests.Where(request => !request.Delete))
        {
            if (request.Edits is not { Count: > 0 } edits)
            {
                continue;
            }

            if (request.RowIndex < 0 || request.RowIndex >= _result.Rows.Count)
            {
                error = "That row is no longer part of this result.";
                return null;
            }

            var row = _result.Rows[request.RowIndex];

            // A joined row can carry cells from two tables; each one is its own UPDATE so a single
            // JOIN result stays editable without asking the user to run two queries.
            var byTarget = new Dictionary<RowEditTarget, List<ColumnAssignment>>();

            foreach (var edit in edits)
            {
                if (!_columnTargets.TryGetValue(edit.ResultIndex, out var editTarget)
                    || !editTarget.Columns.TryGetValue(edit.ResultIndex, out var column))
                {
                    error = "One of the edited cells can't be written back.";
                    return null;
                }

                var original = edit.ResultIndex < row.Length ? row[edit.ResultIndex] : null;
                if (string.Equals(original, edit.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!byTarget.TryGetValue(editTarget, out var assignments))
                {
                    assignments = [];
                    byTarget[editTarget] = assignments;
                }

                assignments.Add(new ColumnAssignment(column.Column, edit.Value, column.DataType));
            }

            foreach (var pair in byTarget)
            {
                if (!TryKeyFilters(pair.Key, row, out var key, out error))
                {
                    return null;
                }

                statements.Add(new RowEditStatement(
                    RowEditKind.Update,
                    _dialect.UpdateStatement(pair.Key.Schema, pair.Key.Table, pair.Value, key)));
            }

            if (byTarget.Count > 0)
            {
                applied.Add(request);
            }
        }

        foreach (var request in requests.Where(request => request.Delete))
        {
            if (request.RowIndex < 0 || request.RowIndex >= _result.Rows.Count)
            {
                error = "That row is no longer part of this result.";
                return null;
            }

            // A row is deleted through one of the tables it came from: the selected cell names the
            // table when the user right-clicked a joined row, otherwise the first identifiable one.
            var target = request.ResultColumn is { } columnIndex
                         && _columnTargets.TryGetValue(columnIndex, out var hinted)
                ? hinted
                : TargetForRow(request.RowIndex);
            if (target is null)
            {
                error = "This row can't be identified, so it can't be deleted.";
                return null;
            }

            if (!TryKeyFilters(target, _result.Rows[request.RowIndex], out var key, out error))
            {
                return null;
            }

            statements.Add(new RowEditStatement(
                RowEditKind.Delete,
                _dialect.DeleteStatement(target.Schema, target.Table, key)));
            applied.Add(request);
        }

        if (statements.Count == 0)
        {
            error = "Nothing changed.";
            return null;
        }

        return new RowEditPlan
        {
            Statements = statements,
            Batch = _dialect.DmlBatch(statements.Select(statement => statement.Sql).ToList()),
            Requests = applied,
        };
    }

    public async Task<RowEditOutcome> ApplyAsync(RowEditPlan plan, CancellationToken cancellationToken = default)
    {
        var results = await _execute(plan.Batch, cancellationToken);
        var failure = results.FirstOrDefault(result => result.IsError);
        var affected = results.Sum(result => result.RecordsAffected);

        return failure is null
            ? new RowEditOutcome { Success = true, AffectedRows = affected, Message = string.Empty }
            : new RowEditOutcome { Success = false, AffectedRows = affected, Message = failure.Message };
    }

    private RowEditTarget? TargetForRow(int rowIndex)
    {
        if (_resolution.Targets.Count == 1)
        {
            return _resolution.Targets[0];
        }

        // A joined row can belong to several tables. The selected cell decides when the grid passes
        // it; without one, the first target whose key values are all present is the honest choice.
        foreach (var target in _resolution.Targets)
        {
            if (TryKeyFilters(target, _result.Rows[rowIndex], out _, out _))
            {
                return target;
            }
        }

        return null;
    }

    private bool TryKeyFilters(
        RowEditTarget target,
        string?[] row,
        out List<RowFilter> key,
        out string? error)
    {
        key = new List<RowFilter>(target.Key.Count);
        error = null;

        foreach (var column in target.Key)
        {
            if (column.ResultIndex >= row.Length)
            {
                error = $"The result no longer holds {target.Label}.{column.Column}.";
                return false;
            }

            key.Add(new RowFilter(column.Column, row[column.ResultIndex], column.DataType));
        }

        return true;
    }
}
