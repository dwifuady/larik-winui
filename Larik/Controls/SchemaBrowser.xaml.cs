using System.Collections.ObjectModel;
using Larik.Data;
using Larik.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Larik.Controls;

/// <summary>
/// The schema surface used twice: a search-first peek over the editor, and a pinned inspector that
/// sits beside it. Both own the same list, filters and selection; only the detail pane and the
/// close affordance differ. It only knows tables — the provider contract has no views or routines yet.
/// </summary>
public sealed partial class SchemaBrowser : UserControl
{
    private readonly ObservableCollection<SchemaRow> _rows = [];
    private IReadOnlyList<TableSchema> _tables = [];
    private IReadOnlyList<string> _recents = [];
    private string? _database;
    private string? _expandedKey;
    private int _selectedIndex = -1;
    private TableSchema? _detailTable;
    private bool _compact;

    public SchemaBrowser()
    {
        InitializeComponent();
        RowList.ItemsSource = _rows;
    }

    /// <summary>A table (or one of its columns) was chosen for insertion.</summary>
    public event EventHandler<SchemaSelection>? Activated;

    /// <summary>Preview the chosen table's first 100 rows.</summary>
    public event EventHandler<SchemaSelection>? PreviewRequested;

    public event EventHandler? CloseRequested;

    public event EventHandler? PinRequested;

    public event EventHandler<string>? StatusRequested;

    /// <summary>Pinned mode: no detail column, no close button, columns expand under the selected table.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            ApplyMode();
        }
    }

    /// <summary>Feeds the browser (called on open, on schema refresh and on space/database switch).</summary>
    public void Show(IReadOnlyList<TableSchema> tables, string? database, IReadOnlyList<string> recents)
    {
        _tables = tables;
        _database = database;
        _recents = recents;

        var columns = tables.Sum(table => table.Columns.Count);
        ContextText.Text = tables.Count == 0
            ? "No schema loaded for this database."
            : $"{database ?? "no database"} · {tables.Count} tables · {columns} columns";

        RebuildRows();
    }

    public void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    /// <summary>Clears the filter; used when the peek is reopened so it always starts from the tree.</summary>
    public void ResetSearch()
    {
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty;
        }
    }

    private void ApplyMode()
    {
        DetailColumn.Width = _compact ? new GridLength(0) : new GridLength(248);
        Detail.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(
            PinButton,
            _compact ? "Unpin from the editor" : "Pin beside the editor");
    }

    // ---------------------------------------------------------------- rows

    private void RebuildRows(string? keepKey = null)
    {
        var selected = keepKey ?? SelectedRow?.Key;
        _rows.Clear();

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length > 0)
        {
            BuildMatches(query);
        }
        else
        {
            BuildTree();
        }

        EmptyText.Text = _tables.Count == 0
            ? "No schema loaded for this database. Connect the workspace to browse it."
            : "No matches.";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _selectedIndex = -1;
        var index = selected is null
            ? -1
            : _rows.ToList().FindIndex(row => row.IsSelectable && row.Key == selected);
        if (index >= 0)
        {
            SelectRow(index, expand: false);
            return;
        }

        SelectFirstSelectable();
    }

    private void BuildTree()
    {
        var byKey = _tables.ToDictionary(SchemaKeys.For, StringComparer.Ordinal);
        var recentRows = _recents
            .Select(key => byKey.TryGetValue(key, out var table) ? table : null)
            .Where(table => table is not null)
            .Select(table => table!)
            .DistinctBy(SchemaKeys.For)
            .Take(5)
            .ToList();

        // In the pinned tree recents would duplicate the table rows and expanding one would open
        // the other; the peek has room for the shortcut, the tree does not.
        if (!_compact && recentRows.Count > 0)
        {
            _rows.Add(GroupRow("Recent", recentRows.Count));
            foreach (var table in recentRows)
            {
                AddObjectRow(table);
            }
        }

        foreach (var group in _tables
                     .GroupBy(table => string.IsNullOrWhiteSpace(table.Schema) ? "tables" : table.Schema!)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(GroupRow(group.Key, group.Count()));
            foreach (var table in group.OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase))
            {
                AddObjectRow(table);
                if (_compact && string.Equals(SchemaKeys.For(table), _expandedKey, StringComparison.Ordinal))
                {
                    AddColumnRows(table);
                }
            }
        }
    }

    private void BuildMatches(string query)
    {
        var tables = _tables
            .Where(table => table.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(table => table.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(table => table.Name.Length)
            .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tables.Count > 0)
        {
            _rows.Add(GroupRow("Tables", tables.Count));
            foreach (var table in tables)
            {
                AddObjectRow(table);
            }
        }

        var columns = _tables
            .SelectMany(table => table.Columns
                .Where(column => column.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(column => (Table: table, Column: column)))
            .OrderByDescending(hit => hit.Column.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(hit => hit.Column.Length)
            .ThenBy(hit => hit.Table.Name, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        if (columns.Count > 0)
        {
            _rows.Add(GroupRow("Columns", columns.Count));
            foreach (var (table, column) in columns)
            {
                _rows.Add(new SchemaRow
                {
                    Kind = SchemaRowKind.Column,
                    Label = column,
                    Key = SchemaKeys.For(table, column),
                    Meta = string.IsNullOrWhiteSpace(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}",
                    Table = table,
                    Column = column,
                });
            }
        }
    }

    private void AddObjectRow(TableSchema table)
    {
        var key = SchemaKeys.For(table);
        _rows.Add(new SchemaRow
        {
            Kind = SchemaRowKind.Table,
            Label = table.Name,
            Key = key,
            Meta = $"{table.Columns.Count} cols",
            Table = table,
        });
    }

    private void AddColumnRows(TableSchema table)
    {
        var primaryKeys = new HashSet<string>(table.PrimaryKey, StringComparer.OrdinalIgnoreCase);
        var foreignKeys = table.ForeignKeys
            .GroupBy(key => key.Column, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var column in table.Columns)
        {
            _rows.Add(new SchemaRow
            {
                Kind = SchemaRowKind.Column,
                Label = column,
                Key = SchemaKeys.For(table, column),
                Meta = primaryKeys.Contains(column) ? "PK" : foreignKeys.ContainsKey(column) ? "FK" : null,
                Table = table,
                Column = column,
            });
        }
    }

    private static SchemaRow GroupRow(string label, int count) => new()
    {
        Kind = SchemaRowKind.Group,
        Label = label,
        Key = $"group\u001F{label}",
        Meta = count.ToString(),
    };

    // ---------------------------------------------------------------- selection

    private SchemaRow? SelectedRow =>
        _selectedIndex >= 0 && _selectedIndex < _rows.Count ? _rows[_selectedIndex] : null;

    private void SelectFirstSelectable()
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            if (_rows[index].IsSelectable)
            {
                SelectRow(index, expand: true);
                return;
            }
        }

        ClearDetail();
    }

    private void SelectRow(int index, bool expand)
    {
        if (index < 0 || index >= _rows.Count || !_rows[index].IsSelectable)
        {
            return;
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].IsSelected = i == index;
        }

        _selectedIndex = index;
        RowList.ScrollIntoView(_rows[index]);
        UpdateDetail();

        var row = _rows[index];
        if (expand && _compact && row.Kind == SchemaRowKind.Table
            && string.IsNullOrWhiteSpace(SearchBox.Text)
            && !string.Equals(row.Key, _expandedKey, StringComparison.Ordinal))
        {
            _expandedKey = row.Key;
            RebuildRows(row.Key);
        }
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var index = _selectedIndex;
        for (var step = 0; step < _rows.Count; step++)
        {
            index += delta;
            if (index < 0)
            {
                index = _rows.Count - 1;
            }
            else if (index >= _rows.Count)
            {
                index = 0;
            }

            if (_rows[index].IsSelectable)
            {
                SelectRow(index, expand: true);
                return;
            }
        }
    }

    private void UpdateDetail()
    {
        var table = SelectedRow?.Table;
        if (table is null)
        {
            ClearDetail();
            return;
        }

        _detailTable = table;
        DetailTitle.Text = string.IsNullOrWhiteSpace(table.Schema)
            ? table.Name
            : $"{table.Schema}.{table.Name}";

        var primaryKeys = new HashSet<string>(table.PrimaryKey, StringComparer.OrdinalIgnoreCase);
        var foreignKeys = table.ForeignKeys
            .GroupBy(key => key.Column, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var keys = table.Columns.Count(primaryKeys.Contains) + foreignKeys.Count;
        DetailSubtitle.Text = $"Table · {table.Columns.Count} columns" + (keys > 0 ? $" · {keys} keys" : string.Empty);

        ColumnList.ItemsSource = table.Columns
            .Select(column =>
            {
                var isPrimary = primaryKeys.Contains(column);
                var foreign = foreignKeys.GetValueOrDefault(column);
                return new SchemaColumnRow
                {
                    Name = column,
                    Key = isPrimary ? "PK" : foreign is null ? null : "FK",
                    KeyTooltip = isPrimary
                        ? "Primary key"
                        : foreign is null
                            ? null
                            : string.IsNullOrWhiteSpace(foreign.ReferencedSchema)
                                ? $"References {foreign.ReferencedTable}.{foreign.ReferencedColumn}"
                                : $"References {foreign.ReferencedSchema}.{foreign.ReferencedTable}.{foreign.ReferencedColumn}",
                };
            })
            .ToList();

        PreviewButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
    }

    private void ClearDetail()
    {
        _detailTable = null;
        DetailTitle.Text = _rows.Count == 0 ? "No matches" : "Select an object";
        DetailSubtitle.Text = string.Empty;
        ColumnList.ItemsSource = null;
        PreviewButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
    }

    // ---------------------------------------------------------------- actions

    private void ActivateSelected()
    {
        if (SelectedRow is not { Table: { } table } row)
        {
            return;
        }

        Activated?.Invoke(this, new SchemaSelection(table, row.Column));
    }

    private void PreviewSelected()
    {
        var table = SelectedRow?.Table ?? _detailTable;
        if (table is not null)
        {
            PreviewRequested?.Invoke(this, new SchemaSelection(table, null));
        }
    }

    private void CopySelected()
    {
        if (_detailTable is null)
        {
            return;
        }

        var name = DetailTitle.Text;
        var package = new DataPackage();
        package.SetText(name);

        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            StatusRequested?.Invoke(this, $"Copy failed: {ex.Message}");
            return;
        }

        StatusRequested?.Invoke(this, $"Copied {name}");
    }

    // ---------------------------------------------------------------- events

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _expandedKey = null;
        RebuildRows();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter when IsShiftDown():
                PreviewSelected();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ActivateSelected();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private void RowList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter when IsShiftDown():
                PreviewSelected();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ActivateSelected();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private void RowList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SchemaRow row)
        {
            return;
        }

        var index = _rows.IndexOf(row);
        if (index >= 0 && row.IsSelectable)
        {
            SelectRow(index, expand: true);
        }
    }

    private void RowList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ActivateSelected();

    private void PreviewButton_Click(object sender, RoutedEventArgs e) => PreviewSelected();

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void PinButton_Click(object sender, RoutedEventArgs e) =>
        PinRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
