using Larik.Data;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Larik.Controls;

/// <summary>
/// The vertical reference view for a lookup: one record card per returned row. Large tables return
/// only the matching row(s), so cards read better than a wide table in the narrow preview pane.
/// </summary>
public sealed partial class ReferenceRecordsView : UserControl
{
    private QueryResult? _result;
    private IReadOnlyList<int>? _matchedRows;
    private Brush? _matchBackground;
    private Brush? _matchBorder;

    public ReferenceRecordsView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Render();
    }

    public void Show(QueryResult result, IReadOnlyList<int> matchedRows, Brush matchBackground, Brush matchBorder)
    {
        _result = result;
        _matchedRows = matchedRows;
        _matchBackground = matchBackground;
        _matchBorder = matchBorder;
        Render();
    }

    private void Render()
    {
        if (_result is not { } result || _matchedRows is not { } matchedRows)
        {
            return;
        }

        var matches = new HashSet<int>(matchedRows);
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var divider = ThemeService.GetBrush("DividerStrokeColorDefaultBrush", ActualTheme, StudioColors.Muted);
        var records = new List<ReferenceRowItem>(result.Rows.Count);

        for (var rowIndex = 0; rowIndex < result.Rows.Count; rowIndex++)
        {
            var row = result.Rows[rowIndex];
            var isMatch = matches.Contains(rowIndex);
            records.Add(new ReferenceRowItem
            {
                Cells = result.Columns
                    .Select((column, columnIndex) =>
                    {
                        var value = columnIndex < row.Length ? row[columnIndex] : null;
                        return new ReferenceCellItem
                        {
                            Name = column.Name,
                            Value = value ?? "NULL",
                            IsNull = value is null,
                        };
                    })
                    .ToList(),
                IsMatch = isMatch,
                Background = isMatch ? _matchBackground ?? transparent : transparent,
                BorderBrush = isMatch ? _matchBorder ?? divider : divider,
            });
        }

        RowsList.ItemsSource = records;

        if (matchedRows.Count > 0)
        {
            var first = matchedRows[0];
            if (first >= 0 && first < records.Count)
            {
                DispatcherQueue.TryEnqueue(() => RowsList.ScrollIntoView(records[first]));
            }
        }
    }

    public void Clear()
    {
        _result = null;
        _matchedRows = null;
        RowsList.ItemsSource = null;
    }
}
