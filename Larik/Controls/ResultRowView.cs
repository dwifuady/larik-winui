using Larik.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Larik.Controls;

/// <summary>
/// One row of the results grid. The ListView realizes only visible rows, so this control is
/// created a few dozen times regardless of result size; recycling updates its cells in place.
/// </summary>
public sealed class ResultRowView : Grid
{
    private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);

    private readonly StackPanel _cellsPanel;
    private readonly Border _bottomLine;
    private readonly Border _matchBar;
    private readonly List<Border> _cellBorders = [];
    private readonly List<TextBlock> _cells = [];
    private readonly FontFamily _monoFont;
    private readonly Brush _mutedBrush;
    private readonly Brush _secondaryBrush;
    private readonly Brush _primaryBrush;
    private readonly Brush _dividerBrush;
    private readonly Brush _hoverBrush;

    private int _selectionAnchorRow = -1;
    private int _selectionAnchorColumn = -1;
    private int _selectionFocusRow = -1;
    private int _selectionFocusColumn = -1;
    private Brush? _selectionBrush;
    private Brush? _activeCellBrush;

    public ResultRowView()
    {
        Height = 28;
        HorizontalAlignment = HorizontalAlignment.Left;

        _monoFont = (FontFamily)Application.Current.Resources["StudioMonoFont"];
        _mutedBrush = (Brush)Application.Current.Resources["StudioTextMutedBrush"];
        _secondaryBrush = (Brush)Application.Current.Resources["StudioTextSecondaryBrush"];
        _primaryBrush = (Brush)Application.Current.Resources["StudioTextPrimaryBrush"];

        _dividerBrush = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var value)
            && value is Brush dividerBrush
                ? dividerBrush
                : _mutedBrush;
        _hoverBrush = Application.Current.Resources.TryGetValue("HairlineSubtleBrush", out value)
            && value is Brush hoverBrush
                ? hoverBrush
                : _mutedBrush;

        _cellsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        Children.Add(_cellsPanel);

        _bottomLine = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = _dividerBrush,
            Opacity = 0.7,
        };
        Children.Add(_bottomLine);

        _matchBar = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_matchBar);

        DataContextChanged += (_, _) => UpdateCells();
        Loaded += (_, _) => FindAncestor(this)?.AttachRow(this);
        PointerEntered += (_, _) => Background = _hoverBrush;
        PointerExited += (_, _) => RestoreBackground();
    }

    /// <summary>Re-applies the visible cell backgrounds for the grid's current selection.</summary>
    public void ApplySelection(
        int anchorRow,
        int anchorColumn,
        int focusRow,
        int focusColumn,
        Brush selectionBrush,
        Brush activeCellBrush)
    {
        _selectionAnchorRow = anchorRow;
        _selectionAnchorColumn = anchorColumn;
        _selectionFocusRow = focusRow;
        _selectionFocusColumn = focusColumn;
        _selectionBrush = selectionBrush;
        _activeCellBrush = activeCellBrush;
        UpdateSelection();
    }

    /// <summary>Applies live column width changes without rebuilding the row.</summary>
    public void ApplyLayout()
    {
        if (DataContext is not ResultRow row)
        {
            return;
        }

        Width = row.Layout.TotalWidth;
        for (var index = 0; index < _cellBorders.Count && index < row.Layout.Columns.Count; index++)
        {
            var column = row.Layout.Columns[index];
            _cellBorders[index].Width = column.Width;
            _cells[index].TextAlignment = column.Numeric ? TextAlignment.Right : TextAlignment.Left;
        }
    }

    private void UpdateCells()
    {
        if (DataContext is not ResultRow row)
        {
            return;
        }

        Width = row.Layout.TotalWidth;
        Background = row.HighlightBrush ?? Transparent;
        _matchBar.Background = row.HighlightBorderBrush;
        _matchBar.Visibility = row.HighlightBorderBrush is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        while (_cellBorders.Count < row.Layout.Columns.Count)
        {
            var text = new TextBlock
            {
                FontFamily = _monoFont,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var cell = new Border
            {
                BorderThickness = new Thickness(0, 0, 1, 0),
                BorderBrush = _dividerBrush,
                Padding = new Thickness(12, 5, 12, 5),
                Child = text,
            };

            _cells.Add(text);
            _cellBorders.Add(cell);
            _cellsPanel.Children.Add(cell);
        }

        for (var index = 0; index < _cellBorders.Count; index++)
        {
            var cell = _cellBorders[index];
            if (index >= row.Layout.Columns.Count)
            {
                cell.Visibility = Visibility.Collapsed;
                continue;
            }

            var column = row.Layout.Columns[index];
            var value = index < row.Cells.Count ? row.Cells[index] : null;

            cell.Visibility = Visibility.Visible;
            cell.Width = column.Width;
            // A transparent background makes the whole cell (not just its text glyphs) hit-testable.
            cell.Background = Transparent;

            var text = _cells[index];
            text.Text = value ?? "NULL";
            text.TextAlignment = column.Numeric ? TextAlignment.Right : TextAlignment.Left;
            text.Foreground = value is null
                ? _mutedBrush
                : index == 0 ? _secondaryBrush : _primaryBrush;
            ToolTipService.SetToolTip(text, value);
        }

        UpdateSelection();
    }

    private void RestoreBackground() =>
        Background = DataContext is ResultRow row ? row.HighlightBrush ?? Transparent : Transparent;

    private void UpdateSelection()
    {
        if (DataContext is not ResultRow row || _selectionBrush is null || _activeCellBrush is null)
        {
            return;
        }

        var minRow = Math.Min(_selectionAnchorRow, _selectionFocusRow);
        var maxRow = Math.Max(_selectionAnchorRow, _selectionFocusRow);
        var minColumn = Math.Min(_selectionAnchorColumn, _selectionFocusColumn);
        var maxColumn = Math.Max(_selectionAnchorColumn, _selectionFocusColumn);
        var rowSelected = row.Index >= minRow && row.Index <= maxRow;

        for (var index = 0; index < _cellBorders.Count && index < row.Layout.Columns.Count; index++)
        {
            var selected = rowSelected && index >= minColumn && index <= maxColumn;
            _cellBorders[index].Background = !selected
                ? Transparent
                : row.Index == _selectionFocusRow && index == _selectionFocusColumn
                    ? _activeCellBrush
                    : _selectionBrush;
        }
    }

    private static ResultsGrid? FindAncestor(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ResultsGrid grid)
            {
                return grid;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}
