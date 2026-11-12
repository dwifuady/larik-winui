using Larik.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Larik.Controls;

/// <summary>
/// Picks the target table and key column for a user-defined reference. The shell hosts this in a
/// ContentDialog; the control only decides the pair, so saving stays in one place.
/// </summary>
public sealed partial class ReferenceEditorDialog : UserControl
{
    private readonly IReadOnlyList<TableSchema> _tables;
    private readonly string? _initialSchema;
    private readonly string? _initialTable;
    private readonly string? _initialColumn;
    private IReadOnlyList<TableSchema> _filtered = [];
    private bool _suppressSelection;

    public ReferenceEditorDialog(
        string sourceLabel,
        IReadOnlyList<TableSchema> tables,
        string? targetSchema = null,
        string? targetTable = null,
        string? targetColumn = null)
    {
        _tables = tables;
        _initialSchema = targetSchema;
        _initialTable = targetTable;
        _initialColumn = targetColumn;

        InitializeComponent();

        SourceLabel.Text = $"{sourceLabel} → pick the table it looks up. Saved in Larik only, never on the server.";
        ApplyFilter(string.Empty);
        SelectInitialTarget();
        UpdateSummary();
    }

    internal TableSchema? SelectedTable { get; private set; }

    public string? SelectedColumn { get; private set; }

    public event EventHandler? SelectionChanged;

    public bool CanSave => SelectedTable is not null && !string.IsNullOrWhiteSpace(SelectedColumn);

    public void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void ApplyFilter(string text)
    {
        if (TableList is null || SearchBox is null)
        {
            return;
        }

        var term = text.Trim();
        _filtered = term.Length == 0
            ? _tables
            : _tables
                .Where(table =>
                    $"{table.Schema}.{table.Name}".Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _suppressSelection = true;
        TableList.ItemsSource = _filtered;
        _suppressSelection = false;

        if (SelectedTable is { } selected && _filtered.Contains(selected))
        {
            TableList.SelectedItem = selected;
        }
        else
        {
            TableList.SelectedItem = null;
            ColumnList.ItemsSource = null;
            SelectedTable = null;
            SelectedColumn = null;
            UpdateSummary();
        }
    }

    private void SelectInitialTarget()
    {
        if (_initialTable is not { Length: > 0 } tableName)
        {
            return;
        }

        var table = _tables.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, tableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Schema ?? string.Empty, _initialSchema ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            return;
        }

        _suppressSelection = true;
        TableList.SelectedItem = table;
        _suppressSelection = false;
        LoadColumns(table, _initialColumn);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter(SearchBox.Text ?? string.Empty);

    private void TableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (TableList.SelectedItem is not TableSchema table)
        {
            SelectedTable = null;
            SelectedColumn = null;
            ColumnList.ItemsSource = null;
            UpdateSummary();
            return;
        }

        var preferred = string.Equals(table.Name, _initialTable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(table.Schema ?? string.Empty, _initialSchema ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                ? _initialColumn
                : null;

        LoadColumns(table, preferred);
    }

    private void LoadColumns(TableSchema table, string? preferred)
    {
        SelectedTable = table;
        var columns = table.Columns.ToList();
        ColumnList.ItemsSource = columns;

        var column = preferred is not null && columns.Contains(preferred, StringComparer.OrdinalIgnoreCase)
            ? columns.First(candidate => string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase))
            : table.PrimaryKey.FirstOrDefault() ?? columns.FirstOrDefault();

        ColumnList.SelectedItem = column;
        SelectedColumn = column;
        UpdateSummary();
    }

    private void ColumnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        SelectedColumn = ColumnList.SelectedItem as string;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        SelectionSummary.Text = SelectedTable is { } table && SelectedColumn is { Length: > 0 } column
            ? $"{table.Schema}.{table.Name}.{column}"
            : "Select the table and key column this value points to.";
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
