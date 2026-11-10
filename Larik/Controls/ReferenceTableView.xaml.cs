using Larik.Data;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Larik.Controls;

/// <summary>
/// A small, virtualized table for the reference preview: column headers stay aligned with the rows
/// while both scroll horizontally. It reuses <see cref="ResultRowView"/>, so a reference row looks
/// like a result row and matching rows get the same tint/border treatment.
/// </summary>
public sealed partial class ReferenceTableView : UserControl
{
    private const double MinColumnWidth = 64;
    private const double MaxColumnWidth = 280;

    private ScrollViewer? _listScroller;
    private QueryResult? _result;
    private IReadOnlyList<int>? _matchedRows;
    private Brush? _matchBackground;
    private Brush? _matchBorder;
    private Brush? _divider;

    public ReferenceTableView()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsureListScroller();
        SizeChanged += (_, _) => EnsureListScroller();
        ActualThemeChanged += (_, _) => Render();
    }

    private void EnsureListScroller()
    {
        var scroller = FindDescendant<ScrollViewer>(RowsList);
        if (scroller is null || ReferenceEquals(scroller, _listScroller))
        {
            return;
        }

        if (_listScroller is not null)
        {
            _listScroller.ViewChanged -= RowsList_ViewChanged;
        }

        _listScroller = scroller;
        _listScroller.ViewChanged += RowsList_ViewChanged;
    }

    public void Show(
        QueryResult result,
        IReadOnlyList<int> matchedRows,
        Brush matchBackground,
        Brush matchBorder,
        Brush divider)
    {
        _result = result;
        _matchedRows = matchedRows;
        _matchBackground = matchBackground;
        _matchBorder = matchBorder;
        _divider = divider;
        Render();
    }

    private void Render()
    {
        if (_result is not { } result || _matchedRows is not { } matchedRows)
        {
            return;
        }

        if (result.Columns.Count == 0)
        {
            Clear();
            return;
        }

        var divider = ThemeService.GetBrush("DividerStrokeColorDefaultBrush", ActualTheme, _divider ?? StudioColors.Muted);
        var layout = BuildLayout(result);
        BuildHeader(layout, divider);

        var matches = new HashSet<int>(matchedRows);
        var rows = new List<ResultRow>(result.Rows.Count);
        for (var index = 0; index < result.Rows.Count; index++)
        {
            var isMatch = matches.Contains(index);
            rows.Add(new ResultRow
            {
                Index = index,
                Cells = result.Rows[index],
                Layout = layout,
                HighlightBrush = isMatch ? _matchBackground : null,
                HighlightBorderBrush = isMatch ? _matchBorder : null,
            });
        }

        RowsList.ItemsSource = rows;
        RowsList.UpdateLayout();
        EnsureListScroller();

        DispatcherQueue.TryEnqueue(() =>
        {
            _listScroller ??= FindDescendant<ScrollViewer>(RowsList);
            _listScroller?.ChangeView(0, 0, null, disableAnimation: true);

            if (rows.Count > 0 && matchedRows.Count > 0)
            {
                var first = matchedRows[0];
                if (first >= 0 && first < rows.Count)
                {
                    RowsList.ScrollIntoView(rows[first]);
                }
            }
        });
    }

    public void Clear()
    {
        _result = null;
        _matchedRows = null;
        RowsList.ItemsSource = null;
        HeaderHost.Children.Clear();
        HeaderHost.Width = double.NaN;
    }

    private static ResultLayout BuildLayout(QueryResult result)
    {
        var sample = Math.Min(result.Rows.Count, 48);
        var columns = new List<ResultColumnLayout>(result.Columns.Count);

        for (var columnIndex = 0; columnIndex < result.Columns.Count; columnIndex++)
        {
            var name = result.Columns[columnIndex].Name;
            var maxLength = name.Length;
            var numeric = sample > 0;

            for (var rowIndex = 0; rowIndex < sample; rowIndex++)
            {
                var value = columnIndex < result.Rows[rowIndex].Length
                    ? result.Rows[rowIndex][columnIndex]
                    : null;
                if (value is null)
                {
                    numeric = false;
                    continue;
                }

                maxLength = Math.Max(maxLength, value.Length);
                if (!double.TryParse(
                        value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _))
                {
                    numeric = false;
                }
            }

            columns.Add(new ResultColumnLayout
            {
                Name = name,
                DataType = result.Columns[columnIndex].DataType,
                Width = Math.Clamp((maxLength * 7.2) + 24, MinColumnWidth, MaxColumnWidth),
                Numeric = numeric,
            });
        }

        return new ResultLayout { Columns = columns };
    }

    private void BuildHeader(ResultLayout layout, Brush divider)
    {
        HeaderHost.Children.Clear();
        HeaderHost.Width = layout.TotalWidth;

        var background = ThemeService.GetBrush("HairlineSubtleBrush", ActualTheme, divider);
        var muted = ThemeService.GetBrush("StudioTextMutedBrush", ActualTheme, divider);

        foreach (var column in layout.Columns)
        {
            var label = new TextBlock
            {
                Text = column.Name,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(label, column.Name);

            var header = new Border
            {
                Width = column.Width,
                Height = 30,
                Padding = new Thickness(12, 0, 10, 0),
                Background = background,
                BorderBrush = divider,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = label,
            };

            HeaderHost.Children.Add(header);
        }
    }

    private void RowsList_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureListScroller();
    }

    private void RowsList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_listScroller is null)
        {
            return;
        }

        // The header is a passive mirror: only the rows can be scrolled by the user, so header
        // and body can never drift apart.
        HeaderScroller.ChangeView(_listScroller.HorizontalOffset, null, null, disableAnimation: true);
    }

    private static T? FindDescendant<T>(DependencyObject? node)
        where T : DependencyObject
    {
        if (node is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is T target)
            {
                return target;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
