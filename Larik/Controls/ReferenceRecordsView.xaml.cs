using Larik.Data;
using Larik.Models;
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
    public ReferenceRecordsView() => InitializeComponent();

    public void Show(QueryResult result, IReadOnlyList<int> matchedRows, Brush matchBackground, Brush matchBorder)
    {
        var matches = new HashSet<int>(matchedRows);
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
                Background = isMatch ? matchBackground : transparent,
                BorderBrush = isMatch ? matchBorder : (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
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

    public void Clear() => RowsList.ItemsSource = null;
}
