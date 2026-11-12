using System.Runtime.CompilerServices;
using System.Text;
using Larik.Data;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Larik.Controls;

/// <summary>
/// The results surface: a virtualized row list plus a sticky column header. It owns selection,
/// keyboard navigation, clipboard copy and live column resizing, so the shell only feeds it a
/// <see cref="QueryResult"/>.
/// </summary>
public sealed partial class ResultsGrid : UserControl
{
    private const double RowHeight = 28;
    private const double HeaderHeight = 30;
    private const double MinColumnWidth = 48;
    private const double MaxColumnWidth = 900;

    private readonly ConditionalWeakTable<QueryResult, ResultLayout> _layouts = new();
    private readonly ConditionalWeakTable<QueryResult, ResultViewState> _viewStates = new();
    private readonly List<Grid> _headerContainers = [];
    private readonly List<Border> _headerSurfaces = [];

    private QueryResult? _result;
    private ResultLayout? _layout;
    private IReadOnlyList<ResultRow> _rows = [];
    private ScrollViewer? _listScroller;
    private bool _syncingScroll;
    private bool _hasSelection;
    private int _anchorRow;
    private int _anchorColumn;
    private int _focusRow;
    private int _focusColumn;

    private ColumnResizeHandle? _resizeHandle;
    private int _resizeColumnIndex;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private string _accentHex = StudioColors.Palette[0];
    private Brush _selectionBrush = null!;
    private Brush _activeCellBrush = null!;
    private Brush _headerSelectionBrush = null!;
    private Brush _headerBackground = null!;
    private Brush _dividerBrush = null!;

    // Cell preview: the full value of the focused cell, which the grid itself ellipsizes.
    private const int PreviewCharacterLimit = 100_000;
    private const double MinPreviewWidth = 240;
    private const double MaxPreviewWidth = 720;
    private const double GridReserveWidth = 320;

    // One Run per syntax token is the whole cost of colour, so both the painted span and the run
    // count are capped. Values past the cap stay plain; find still searches the whole visible value.
    private const int PreviewSyntaxCharacterLimit = 15_000;
    private const int PreviewSyntaxTokenLimit = 1_000;
    private const int PreviewSearchMatchLimit = 1_000;

    private bool _previewOpen;
    private bool _previewRequested;
    private int _previewRow = -1;
    private int _previewColumn = -1;
    private CellFormat _previewFormat = CellFormat.Auto;
    private string _previewCopyText = string.Empty;
    private bool _resizingPreview;
    private double _previewResizeStartX;
    private double _previewResizeStartWidth;
    private double _desiredPreviewWidth = 340;
    private PreviewMode _previewMode = PreviewMode.Value;
    private CancellationTokenSource? _referenceCancellation;
    private Brush _referenceMatchBrush = null!;
    private Brush _referenceBorderBrush = null!;

    // Rendered value plus its find state. The document is rebuilt from these on a theme switch.
    private string _previewText = string.Empty;
    private IReadOnlyList<CellSyntaxToken> _previewTokens = [];
    private bool _previewTextMuted;
    private string _previewQuery = string.Empty;
    private readonly List<TextRange> _previewMatches = [];
    private int _previewMatchIndex = -1;
    private bool _previewFindOpen;

    private Brush _previewPrimaryBrush = null!;
    private Brush _previewMutedBrush = null!;
    private Brush _previewFindMatchBrush = null!;
    private Dictionary<CellSyntaxTokenKind, Brush> _previewTokenBrushes = [];

    private enum PreviewMode
    {
        Value,
        Reference,
    }

    /// <summary>Per-result scroll/selection state, so each result tab remembers where it was.</summary>
    private sealed class ResultViewState
    {
        public double VerticalOffset { get; set; }

        public double HorizontalOffset { get; set; }

        public bool HasSelection { get; set; }

        public int AnchorRow { get; set; }

        public int AnchorColumn { get; set; }

        public int FocusRow { get; set; }

        public int FocusColumn { get; set; }

        /// <summary>Whether the cell preview was open for this result; state is per result/tab.</summary>
        public bool PreviewRequested { get; set; }
    }

    public ResultsGrid()
    {
        InitializeComponent();

        _dividerBrush = ThemeService.GetBrush("DividerStrokeColorDefaultBrush", ActualTheme, StudioColors.Muted);
        _headerBackground = ThemeService.GetBrush("HairlineSubtleBrush", ActualTheme, StudioColors.Muted);
        UpdateAccentBrushes();
        RefreshPreviewBrushes();

        // Rows and headers cache brushes in code; a runtime Light/Dark switch must rebuild them.
        ActualThemeChanged += (_, _) => ApplyTheme();

        // ListViewItem marks pointer presses handled for its own selection bookkeeping. Listen
        // for handled events too, otherwise clicking a cell never reaches ResultsGrid.
        RowsList.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(RowsList_PointerPressed),
            handledEventsToo: true);

        // The results pane can start collapsed, when ActualWidth is still 0. Apply the stored
        // preview width once the control has real space, and re-clamp it when the window changes.
        Loaded += (_, _) =>
        {
            EnsureListScroller();
            ApplyPreviewWidth();
        };
        SizeChanged += (_, _) =>
        {
            EnsureListScroller();
            ApplyPreviewWidth();
        };
    }

    /// <summary>
    /// Finds the ListView's inner scroller. A collapsed results pane may have fired Loaded before
    /// its template existed, so this is retried when the control gets real size and when a result
    /// is shown; otherwise the sticky header would never follow horizontal scrolling.
    /// </summary>
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

    private void ApplyPreviewWidth()
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        CellPreview.Width = Math.Clamp(_desiredPreviewWidth, MinPreviewWidth, MaxPreviewWidthForControl());
    }

    /// <summary>Raised after a copy so the shell can report it in the status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Raised when the user finishes dragging the preview's width.</summary>
    public event EventHandler<double>? PreviewWidthChanged;

    /// <summary>The preview pane's width; persisted by the shell across launches.</summary>
    public double PreviewWidth
    {
        get => _desiredPreviewWidth;
        set
        {
            _desiredPreviewWidth = value;
            ApplyPreviewWidth();
        }
    }

    /// <summary>
    /// Resolves foreign-key references for the current result. Null disables Shift+Space reference
    /// previews; the shell supplies a new controller whenever the active result changes.
    /// </summary>
    public ICellReferenceController? ReferenceController { get; set; }

    /// <summary>Shows a result, or clears the grid when <paramref name="result"/> is null.</summary>
    public void ShowResult(QueryResult? result, string accentHex)
    {
        SaveViewState();
        HideCellPreview();
        UpdateGeneratedQuery(result);

        // The messages panel shares the row area with the grid. A previous Messages tab can leave
        // it visible, and its selectable text would then sit over the next tab's rows.
        MessagesScroll.Visibility = Visibility.Collapsed;

        _accentHex = accentHex;
        UpdateAccentBrushes();

        if (result is null)
        {
            Clear();
            return;
        }

        if (result.Columns.Count == 0 || result.Rows.Count == 0)
        {
            AppLog.Info(
                "results",
                $"show: no grid ({result.Columns.Count:N0} column(s), {result.Rows.Count:N0} row(s), {result.Messages.Count:N0} message(s))");
            Clear();
            _result = result;

            if (result.Messages.Count > 0)
            {
                MessagesText.Text = string.Join(Environment.NewLine, result.Messages);
                MessagesScroll.Visibility = Visibility.Visible;
                return;
            }

            EmptyText.Text = result.Message;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        // The last line before rows are realized is the one that explains a crash inside the row
        // template: a provider that ignores the row cap and returns megabyte cells shows up here.
        AppLog.Info(
            "results",
            $"show: {result.Rows.Count:N0} row(s) x {result.Columns.Count:N0} column(s), largest cell {LargestCellLength(result):N0} characters");

        _result = result;
        _layout = GetOrBuildLayout(result);
        _rows = BuildRows(result, _layout);
        BuildHeader(_layout);

        var state = _viewStates.GetOrCreateValue(result);
        IsTabStop = true;
        _hasSelection = _rows.Count > 0 && _layout.Columns.Count > 0;
        _anchorRow = _hasSelection ? Math.Clamp(state.AnchorRow, 0, _rows.Count - 1) : 0;
        _focusRow = _hasSelection ? Math.Clamp(state.FocusRow, 0, _rows.Count - 1) : 0;
        _anchorColumn = _hasSelection ? Math.Clamp(state.AnchorColumn, 0, _layout.Columns.Count - 1) : 0;
        _focusColumn = _hasSelection ? Math.Clamp(state.FocusColumn, 0, _layout.Columns.Count - 1) : 0;

        EmptyText.Visibility = Visibility.Collapsed;
        RowsList.ItemsSource = _rows;
        RowsList.UpdateLayout();
        EnsureListScroller();
        AccentRule.Background = StudioColors.Brush(accentHex);
        AccentRule.Visibility = Visibility.Visible;

        UpdateSelectionVisuals();
        RestoreViewState(state, result);

        // The preview remembers its open state per result, not globally: switching to a tab or
        // result that never opened it must not inherit another tab's inspector.
        _previewRequested = state.PreviewRequested;
        if (_previewRequested && _hasSelection)
        {
            ShowCellPreview(_focusRow, _focusColumn, _previewMode);
        }
    }

    private void Clear()
    {
        HideCellPreview();
        _previewRequested = false;
        _result = null;
        _layout = null;
        _rows = [];
        _hasSelection = false;
        IsTabStop = false;
        RowsList.ItemsSource = null;
        HeaderHost.Children.Clear();
        _headerContainers.Clear();
        _headerSurfaces.Clear();
        HeaderHost.Width = double.NaN;
        AccentRule.Visibility = Visibility.Collapsed;
        EmptyText.Text = string.Empty;
        EmptyText.Visibility = Visibility.Collapsed;
        MessagesText.Text = string.Empty;
        MessagesScroll.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the command a provider actually sent, when it reports one (for example MongoDB's
    /// pipeline for a translated SELECT). Collapsed by default so the grid stays quiet.
    /// </summary>
    private void UpdateGeneratedQuery(QueryResult? result)
    {
        var query = result?.GeneratedQuery;
        GeneratedQueryText.Text = query ?? string.Empty;
        GeneratedQueryBar.Visibility = string.IsNullOrWhiteSpace(query) ? Visibility.Collapsed : Visibility.Visible;
        GeneratedQueryScroll.Visibility = Visibility.Collapsed;
        GeneratedQueryChevron.Glyph = "\uE70D";
    }

    private void GeneratedQueryToggle_Click(object sender, RoutedEventArgs e)
    {
        var expand = GeneratedQueryScroll.Visibility != Visibility.Visible;
        GeneratedQueryScroll.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        GeneratedQueryChevron.Glyph = expand ? "\uE70E" : "\uE70D";
    }

    private void CopyGeneratedQuery_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(GeneratedQueryText.Text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(GeneratedQueryText.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        StatusChanged?.Invoke(this, "Generated query copied");
    }
    // ---------------------------------------------------------------- layout + rows

    private ResultLayout GetOrBuildLayout(QueryResult result)
    {
        if (_layouts.TryGetValue(result, out var cached))
        {
            return cached;
        }

        var layout = BuildLayout(result);
        _layouts.Add(result, layout);
        return layout;
    }

    private static ResultLayout BuildLayout(QueryResult result)
    {
        var sampleCount = Math.Min(result.Rows.Count, 128);
        var widthSampleCount = Math.Min(result.Rows.Count, 64);
        var columns = new List<ResultColumnLayout>(result.Columns.Count);

        for (var c = 0; c < result.Columns.Count; c++)
        {
            var numeric = sampleCount > 0;
            for (var r = 0; r < sampleCount; r++)
            {
                if (!IsNumeric(result.Rows[r][c]))
                {
                    numeric = false;
                    break;
                }
            }

            var longest = result.Columns[c].Name.Length;
            for (var r = 0; r < widthSampleCount; r++)
            {
                var length = result.Rows[r][c]?.Length ?? 0;
                if (length > longest)
                {
                    longest = length;
                }
            }

            columns.Add(new ResultColumnLayout
            {
                Name = result.Columns[c].Name,
                DataType = result.Columns[c].DataType,
                Width = Math.Clamp(24 + (longest * 7.2), 72, 360),
                Numeric = numeric,
            });
        }

        return new ResultLayout { Columns = columns };
    }

    /// <summary>Largest cell length in the result; a length check only, so this allocates nothing.</summary>
    private static int LargestCellLength(QueryResult result)
    {
        var largest = 0;
        foreach (var row in result.Rows)
        {
            foreach (var cell in row)
            {
                if (cell is { Length: > 0 } && cell.Length > largest)
                {
                    largest = cell.Length;
                }
            }
        }

        return largest;
    }

    private static List<ResultRow> BuildRows(QueryResult result, ResultLayout layout)
    {
        var rows = new List<ResultRow>(result.Rows.Count);
        for (var index = 0; index < result.Rows.Count; index++)
        {
            rows.Add(new ResultRow
            {
                Index = index,
                Cells = result.Rows[index],
                Layout = layout,
            });
        }

        return rows;
    }

    // ---------------------------------------------------------------- header

    private void BuildHeader(ResultLayout layout)
    {
        HeaderHost.Children.Clear();
        _headerContainers.Clear();
        _headerSurfaces.Clear();
        HeaderHost.Width = layout.TotalWidth;

        for (var index = 0; index < layout.Columns.Count; index++)
        {
            var column = layout.Columns[index];
            var referenceLabel = ReferenceController?.Describe(index);
            var container = new Grid
            {
                Width = column.Width,
                Height = HeaderHeight,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };

            var surface = new Border
            {
                Background = _headerBackground,
                BorderBrush = _dividerBrush,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(8, 0, referenceLabel is null ? 8 : 20, 0),
            };

            surface.Child = new TextBlock
            {
                Text = column.Name,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 80,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = column.Numeric ? TextAlignment.Right : TextAlignment.Left,
                Foreground = ThemeService.GetBrush("StudioTextMutedBrush", ActualTheme, StudioColors.Muted),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var headerIndex = index;
            surface.PointerPressed += (_, args) =>
            {
                SelectColumn(headerIndex, IsShiftDown());
                args.Handled = true;
            };

            container.Children.Add(surface);

            if (referenceLabel is not null)
            {
                var referenceButton = new Button
                {
                    Width = 14,
                    Height = 18,
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = "\uE71B", FontSize = 9 },
                };
                AutomationProperties.SetName(referenceButton, referenceLabel);
                ToolTipService.SetToolTip(referenceButton, $"{referenceLabel} (Shift+Space)");
                referenceButton.Click += (_, _) =>
                {
                    MoveTo(_focusRow, headerIndex, extend: false);
                    ShowCellPreview(_focusRow, headerIndex, PreviewMode.Reference);
                };
                container.Children.Add(referenceButton);
            }

            var handle = new ColumnResizeHandle
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Tag = index,
            };

            handle.PointerPressed += ResizeHandle_PointerPressed;
            handle.PointerMoved += ResizeHandle_PointerMoved;
            handle.PointerReleased += ResizeHandle_PointerReleased;
            handle.PointerCaptureLost += (_, _) => _resizeHandle = null;
            AutomationProperties.SetName(handle, $"Resize {column.Name} column");
            ToolTipService.SetToolTip(handle, "Drag to resize");

            container.Children.Add(handle);
            _headerContainers.Add(container);
            _headerSurfaces.Add(surface);
            HeaderHost.Children.Add(container);
        }

        UpdateHeaderSelection();
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ColumnResizeHandle handle
            || handle.Tag is not int index
            || _layout is null
            || index < 0
            || index >= _layout.Columns.Count)
        {
            return;
        }

        _resizeHandle = handle;
        _resizeColumnIndex = index;
        _resizeStartX = e.GetCurrentPoint(HeaderHost).Position.X;
        _resizeStartWidth = _layout.Columns[index].Width;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHandle is null || !ReferenceEquals(sender, _resizeHandle))
        {
            return;
        }

        var x = e.GetCurrentPoint(HeaderHost).Position.X;
        ResizeColumn(
            _resizeColumnIndex,
            Math.Clamp(_resizeStartWidth + (x - _resizeStartX), MinColumnWidth, MaxColumnWidth));
        e.Handled = true;
    }

    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHandle is null)
        {
            return;
        }

        _resizeHandle.ReleasePointerCapture(e.Pointer);
        _resizeHandle = null;
        UpdateSelectionVisuals();
        e.Handled = true;
    }

    private void ResizeColumn(int index, double width)
    {
        if (_layout is null || index < 0 || index >= _layout.Columns.Count)
        {
            return;
        }

        var column = _layout.Columns[index];
        if (Math.Abs(column.Width - width) < 0.5)
        {
            return;
        }

        column.Width = width;
        _headerContainers[index].Width = width;
        HeaderHost.Width = _layout.TotalWidth;

        foreach (var row in FindDescendants<ResultRowView>(RowsList))
        {
            row.ApplyLayout();
        }
    }

    private void UpdateHeaderSelection()
    {
        if (_layout is null || _headerSurfaces.Count == 0)
        {
            return;
        }

        var min = Math.Min(_anchorColumn, _focusColumn);
        var max = Math.Max(_anchorColumn, _focusColumn);

        for (var index = 0; index < _headerSurfaces.Count; index++)
        {
            _headerSurfaces[index].Background = !_hasSelection || index < min || index > max
                ? _headerBackground
                : index == _focusColumn ? _activeCellBrush : _headerSelectionBrush;
        }
    }

    // ---------------------------------------------------------------- selection

    private void SelectColumn(int column, bool extend)
    {
        if (!_hasSelection || _rows.Count == 0)
        {
            return;
        }

        if (!extend)
        {
            _anchorColumn = column;
        }

        _anchorRow = 0;
        _focusRow = _rows.Count - 1;
        _focusColumn = column;

        Focus(FocusState.Programmatic);
        UpdateSelectionVisuals();
    }

    private void SelectAll()
    {
        if (!_hasSelection || _rows.Count == 0 || _layout is null)
        {
            return;
        }

        _anchorRow = 0;
        _anchorColumn = 0;
        _focusRow = _rows.Count - 1;
        _focusColumn = _layout.Columns.Count - 1;
        UpdateSelectionVisuals();
    }

    private void RowsList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RowsList);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source
            || FindAncestor<ResultRowView>(source) is not { } rowView
            || rowView.DataContext is not ResultRow row
            || _layout is null)
        {
            return;
        }

        var x = e.GetCurrentPoint(rowView).Position.X;
        var column = ColumnAt(x);
        var extend = IsShiftDown();

        if (!extend)
        {
            _anchorRow = row.Index;
            _anchorColumn = column;
        }

        _focusRow = row.Index;
        _focusColumn = column;
        _hasSelection = true;

        Focus(FocusState.Programmatic);
        UpdateSelectionVisuals();

        if (point.Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
        }
    }

    private void ResultsGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = IsKeyDown(VirtualKey.Control);

        // Ctrl+F finds in the open preview; with no preview it opens the focused cell first so the
        // shortcut never appears to do nothing.
        if (e.Key == VirtualKey.F && control)
        {
            if (!_previewOpen && _hasSelection && _layout is not null && _rows.Count > 0)
            {
                ShowCellPreview(_focusRow, _focusColumn);
            }

            if (_previewOpen && _previewMode == PreviewMode.Value)
            {
                OpenPreviewFind();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == VirtualKey.F3 && _previewFindOpen)
        {
            MovePreviewMatch(IsShiftDown() ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && _previewFindOpen)
        {
            ClosePreviewFind();
            e.Handled = true;
            return;
        }

        // The find box and its buttons own typing, Space, Home/End and Ctrl+A.
        if (IsFocusInPreviewFind())
        {
            return;
        }

        if (!_hasSelection || _layout is null || _rows.Count == 0)
        {
            return;
        }

        var shift = IsKeyDown(VirtualKey.Shift);

        switch (e.Key)
        {
            case VirtualKey.Left:
                MoveCell(0, -1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                MoveCell(0, 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveCell(-1, 0, shift);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                MoveCell(1, 0, shift);
                e.Handled = true;
                break;
            case VirtualKey.Home when control:
                MoveTo(0, 0, shift);
                e.Handled = true;
                break;
            case VirtualKey.End when control:
                MoveTo(_rows.Count - 1, _layout.Columns.Count - 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                MoveTo(_focusRow, 0, shift);
                e.Handled = true;
                break;
            case VirtualKey.End:
                MoveTo(_focusRow, _layout.Columns.Count - 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                MoveCell(-VisibleRowCount(), 0, shift);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                MoveCell(VisibleRowCount(), 0, shift);
                e.Handled = true;
                break;
            case VirtualKey.C when control:
                CopySelection(withHeaders: shift);
                e.Handled = true;
                break;
            case VirtualKey.A when control:
                SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.Space when control:
                SelectColumn(_focusColumn, extend: false);
                e.Handled = true;
                break;
            case VirtualKey.Space when shift:
                ToggleCellPreview(reference: true);
                e.Handled = true;
                break;
            case VirtualKey.Space:
                ToggleCellPreview(reference: false);
                e.Handled = true;
                break;
            case VirtualKey.Escape when _previewOpen:
                CloseCellPreview();
                e.Handled = true;
                break;
        }
    }

    private void MoveCell(int rowDelta, int columnDelta, bool extend) =>
        MoveTo(_focusRow + rowDelta, _focusColumn + columnDelta, extend);

    private void MoveTo(int row, int column, bool extend)
    {
        if (_layout is null || _rows.Count == 0)
        {
            return;
        }

        var nextRow = Math.Clamp(row, 0, _rows.Count - 1);
        var nextColumn = Math.Clamp(column, 0, _layout.Columns.Count - 1);

        if (!extend)
        {
            _anchorRow = nextRow;
            _anchorColumn = nextColumn;
        }

        _focusRow = nextRow;
        _focusColumn = nextColumn;
        _hasSelection = true;

        UpdateSelectionVisuals();
        EnsureCellVisible();
    }

    private int VisibleRowCount() =>
        Math.Max(1, (int)(RowsList.ActualHeight / RowHeight) - 1);

    private int ColumnAt(double x)
    {
        if (_layout is null)
        {
            return 0;
        }

        var left = 0d;
        for (var index = 0; index < _layout.Columns.Count; index++)
        {
            left += _layout.Columns[index].Width;
            if (x < left)
            {
                return index;
            }
        }

        return _layout.Columns.Count - 1;
    }

    private void UpdateSelectionVisuals()
    {
        if (!_hasSelection)
        {
            return;
        }

        foreach (var row in FindDescendants<ResultRowView>(RowsList))
        {
            ApplyRowSelection(row);
        }

        UpdateHeaderSelection();
        RefreshPreviewIfOpen();
    }

    internal void AttachRow(ResultRowView row) => ApplyRowSelection(row);

    private void ApplyRowSelection(ResultRowView row) =>
        row.ApplySelection(_anchorRow, _anchorColumn, _focusRow, _focusColumn, _selectionBrush, _activeCellBrush);

    private void EnsureCellVisible()
    {
        if (_layout is null || _focusRow < 0 || _focusRow >= _rows.Count)
        {
            return;
        }

        RowsList.ScrollIntoView(_rows[_focusRow], ScrollIntoViewAlignment.Default);

        if (_listScroller is null)
        {
            return;
        }

        var left = 0d;
        for (var index = 0; index < _focusColumn; index++)
        {
            left += _layout.Columns[index].Width;
        }

        var width = _layout.Columns[_focusColumn].Width;
        var offset = _listScroller.HorizontalOffset;
        var viewport = _listScroller.ViewportWidth;
        if (viewport <= 0)
        {
            return;
        }

        if (left < offset)
        {
            _listScroller.ChangeView(left, null, null, disableAnimation: true);
        }
        else if (left + width > offset + viewport)
        {
            _listScroller.ChangeView(left + width - viewport, null, null, disableAnimation: true);
        }
    }

    // ---------------------------------------------------------------- export

    private bool _exporting;

    /// <summary>
    /// Writes the whole result, not the selection: the save picker's file type is the format
    /// choice, so there is no second dialog asking a question the extension already answers.
    /// </summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || _result is not { } result || result.Columns.Count == 0)
        {
            return;
        }

        _exporting = true;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"results_{DateTime.Now:yyyy-MM-dd_HHmm}",
            };
            picker.FileTypeChoices.Add("CSV file", [ResultExport.CsvExtension]);
            picker.FileTypeChoices.Add("JSON file", [ResultExport.JsonExtension]);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var format = string.Equals(file.FileType, ResultExport.JsonExtension, StringComparison.OrdinalIgnoreCase)
                ? ResultExportFormat.Json
                : ResultExportFormat.Csv;

            var text = ResultExport.Format(result, format);

            // The picker already granted access to the file; writing through its stream keeps a
            // packaged (MSIX) app out of file-system virtualization trouble. Excel needs the BOM
            // to read UTF-8 CSV correctly; strict JSON readers prefer no BOM.
            await using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ResultExportFormat.Csv)))
            {
                await writer.WriteAsync(text);
            }

            StatusChanged?.Invoke(this, $"Exported {result.Rows.Count:N0} rows to {file.Name}");
        }
        catch (Exception ex)
        {
            // A save dialog or disk failure must be visible, not silent, and must not crash the shell.
            StatusChanged?.Invoke(this, $"Export failed: {ex.Message}");
        }
        finally
        {
            _exporting = false;
        }
    }

    // ---------------------------------------------------------------- cell preview

    /// <summary>
    /// Space opens the focused cell's value; Shift+Space opens the referenced rows when the column
    /// has a foreign key (and falls back to the value otherwise). Pressing the same combination on
    /// the same cell closes the panel.
    /// </summary>
    private void ToggleCellPreview(bool reference)
    {
        if (_layout is null || !_hasSelection || _rows.Count == 0)
        {
            return;
        }

        var mode = reference && ReferenceController?.CanPreview(_focusColumn) == true
            ? PreviewMode.Reference
            : PreviewMode.Value;

        if (_previewOpen && _previewRow == _focusRow && _previewColumn == _focusColumn && _previewMode == mode)
        {
            CloseCellPreview();
            return;
        }

        ShowCellPreview(_focusRow, _focusColumn, mode);
    }

    private void ShowCellPreview(int row, int column, PreviewMode mode = PreviewMode.Value)
    {
        if (_layout is null || row < 0 || row >= _rows.Count || column < 0 || column >= _layout.Columns.Count)
        {
            return;
        }

        if (mode == PreviewMode.Reference && ReferenceController?.CanPreview(column) != true)
        {
            mode = PreviewMode.Value;
        }

        if (mode == PreviewMode.Reference && _previewFindOpen)
        {
            // Find belongs to the value view; the reference view has its own navigation.
            ResetPreviewFind();
        }

        _referenceCancellation?.Cancel();
        _referenceCancellation?.Dispose();
        _referenceCancellation = null;

        _previewRequested = true;
        _previewOpen = true;
        _previewRow = row;
        _previewColumn = column;
        _previewMode = mode;
        PreviewHost.Visibility = Visibility.Visible;

        var canPreviewReference = ReferenceController?.CanPreview(column) == true;
        ReferenceToggleButton.Visibility = canPreviewReference ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(
            ReferenceToggleButton,
            mode == PreviewMode.Reference
                ? "Show the cell value (Space)"
                : "Preview referenced rows (Shift+Space)");
        PreviewFormatButton.Visibility = mode == PreviewMode.Value ? Visibility.Visible : Visibility.Collapsed;
        ValueView.Visibility = mode == PreviewMode.Value ? Visibility.Visible : Visibility.Collapsed;
        ReferenceView.Visibility = mode == PreviewMode.Reference ? Visibility.Visible : Visibility.Collapsed;
        CellPreviewFooter.Visibility = mode == PreviewMode.Value ? Visibility.Visible : Visibility.Collapsed;

        if (mode == PreviewMode.Reference)
        {
            CellPreviewTitle.Text = _layout.Columns[column].Name;
            CellPreviewSubtitle.Text = "Reference";
            ShowReferenceLoading();
            _ = LoadReferenceAsync(row, column);
            return;
        }

        ShowValuePreview(row, column);
    }

    private void ShowValuePreview(int row, int column)
    {
        if (_layout is null || row < 0 || row >= _rows.Count || column < 0 || column >= _layout.Columns.Count)
        {
            return;
        }

        var layout = _layout.Columns[column];
        var value = column < _rows[row].Cells.Count ? _rows[row].Cells[column] : null;
        var subtitle = string.IsNullOrWhiteSpace(layout.DataType)
            ? $"row {_rows[row].Index + 1}"
            : $"{layout.DataType} · row {_rows[row].Index + 1}";

        CellPreviewTitle.Text = layout.Name;

        if (value is null)
        {
            _previewCopyText = string.Empty;
            CellPreviewSubtitle.Text = subtitle;
            SetPreviewText("NULL", CellFormat.Plain, muted: true);
            CellPreviewFooter.Text = string.Empty;
            return;
        }

        if (value.Length == 0)
        {
            _previewCopyText = string.Empty;
            CellPreviewSubtitle.Text = subtitle;
            SetPreviewText("\"\"", CellFormat.Plain, muted: true);
            CellPreviewFooter.Text = "Empty string";
            return;
        }

        AppLog.Info(
            "preview",
            $"open: '{layout.Name}' ({layout.DataType ?? "unknown type"}), {value.Length:N0} characters, mode {_previewFormat}");

        var formatted = CellContentFormatter.Format(value, _previewFormat);
        _previewCopyText = formatted.Text;

        AppLog.Info(
            "preview",
            $"formatted: {formatted.Kind}, {formatted.Text.Length:N0} characters"
                + (formatted.Error is null ? string.Empty : $", note '{formatted.Error}'"));

        CellPreviewSubtitle.Text = formatted.Kind is CellFormat.Json or CellFormat.Xml
            ? $"{subtitle} · {formatted.Kind.ToString().ToUpperInvariant()}"
            : subtitle;

        // A single multi-megabyte cell would make the RichTextBlock the slowest thing in the window;
        // the copy button still hands over the whole formatted value.
        var display = formatted.Text.Length > PreviewCharacterLimit
            ? formatted.Text[..PreviewCharacterLimit]
            : formatted.Text;

        SetPreviewText(display, formatted.Kind, muted: false);

        // The line that separates "the formatter worked" from "WinUI died laying out the text".
        AppLog.Info("preview", $"rendered: {display.Length:N0} characters in the panel");

        var footer = formatted.Text.Length > PreviewCharacterLimit
            ? $"Showing first {PreviewCharacterLimit:N0} of {formatted.Text.Length:N0} characters — Copy copies all"
            : $"{formatted.Text.Length:N0} characters";

        if (formatted.Error is { } error)
        {
            footer = $"{footer} · {error}";
        }

        CellPreviewFooter.Text = footer;
    }

    /// <summary>
    /// Replaces the preview document with <paramref name="text"/>, colouring JSON/XML tokens.
    /// Muted text is the NULL/empty marker; everything else uses the primary text colour.
    /// </summary>
    private void SetPreviewText(string text, CellFormat format, bool muted)
    {
        _previewText = text;
        _previewTextMuted = muted;
        _previewTokens = format is CellFormat.Json or CellFormat.Xml
            ? CellSyntaxTokenizer.Tokenize(text, format, PreviewSyntaxCharacterLimit)
            : [];

        RenderPreviewText();

        // Arrowing between cells keeps the find bar useful: the query follows the new value.
        if (_previewFindOpen)
        {
            UpdatePreviewSearch(resetIndex: true);
        }
    }

    private void RenderPreviewText()
    {
        var plain = _previewTextMuted ? _previewMutedBrush : _previewPrimaryBrush;
        var paragraph = new Paragraph();
        var limit = SyntaxLimit();
        var cursor = 0;

        foreach (var token in _previewTokens)
        {
            if (token.Start >= limit)
            {
                break;
            }

            if (token.Start > cursor)
            {
                AddPreviewRun(paragraph, cursor, token.Start - cursor, plain);
            }

            var end = Math.Min(token.End, limit);
            if (end > token.Start)
            {
                AddPreviewRun(paragraph, token.Start, end - token.Start, BrushFor(token.Kind));
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor < _previewText.Length)
        {
            AddPreviewRun(paragraph, cursor, _previewText.Length - cursor, plain);
        }

        CellPreviewValue.TextHighlighters.Clear();
        CellPreviewValue.Blocks.Clear();
        if (_previewText.Length > 0)
        {
            CellPreviewValue.Blocks.Add(paragraph);
        }
    }

    /// <summary>
    /// Where colour stops. The character cap keeps a pathological cell cheap; the token cap bounds
    /// the number of <c>Run</c> objects, and colour always ends at a line boundary so the cut looks
    /// deliberate instead of half-highlighted.
    /// </summary>
    private int SyntaxLimit()
    {
        if (_previewTokens.Count == 0)
        {
            return _previewText.Length;
        }

        var limit = Math.Min(_previewText.Length, PreviewSyntaxCharacterLimit);

        if (_previewTokens.Count > PreviewSyntaxTokenLimit)
        {
            limit = Math.Min(limit, _previewTokens[PreviewSyntaxTokenLimit - 1].End);
        }

        var newline = _previewText.IndexOf('\n', Math.Min(limit, _previewText.Length));
        return newline < 0 ? _previewText.Length : newline + 1;
    }

    private void AddPreviewRun(Paragraph paragraph, int start, int length, Brush foreground)
    {
        if (length <= 0)
        {
            return;
        }

        paragraph.Inlines.Add(new Run
        {
            Text = _previewText.Substring(start, length),
            Foreground = foreground,
        });
    }

    private Brush BrushFor(CellSyntaxTokenKind kind) =>
        _previewTokenBrushes.TryGetValue(kind, out var brush) ? brush : _previewPrimaryBrush;

    // ---------------------------------------------------------------- preview find

    /// <summary>
    /// Ctrl+F in the preview: a find-as-you-type bar over the displayed value. The current match is
    /// selected (so it reads as the active hit) and scrolled into view; every other match carries a
    /// quiet accent highlighter so the shape of the document stays visible.
    /// </summary>
    private void OpenPreviewFind()
    {
        if (!_previewOpen || _previewMode != PreviewMode.Value)
        {
            return;
        }

        // Seed from a short manual selection, the way desktop find bars do. A match selected by an
        // earlier find is cleared when the bar closes, so this only picks up a deliberate selection.
        if (PreviewFindBox.Text.Length == 0)
        {
            var selected = CellPreviewValue.SelectedText;
            if (selected.Length is > 0 and <= 80
                && !selected.Contains('\n')
                && !selected.Contains('\r'))
            {
                PreviewFindBox.Text = selected;
            }
        }

        _previewFindOpen = true;
        PreviewFindBar.Visibility = Visibility.Visible;
        UpdatePreviewSearch(resetIndex: true);
        PreviewFindBox.Focus(FocusState.Programmatic);
        PreviewFindBox.SelectAll();
    }

    private void ClosePreviewFind()
    {
        ResetPreviewFind();
        Focus(FocusState.Programmatic);
    }

    private void ResetPreviewFind()
    {
        _previewFindOpen = false;
        _previewQuery = string.Empty;
        _previewMatches.Clear();
        _previewMatchIndex = -1;
        PreviewFindBar.Visibility = Visibility.Collapsed;
        PreviewFindBox.Text = string.Empty;
        UpdatePreviewFindStatus();
        CellPreviewValue.TextHighlighters.Clear();

        if (CellPreviewValue.Blocks.Count > 0)
        {
            CellPreviewValue.Select(CellPreviewValue.ContentStart, CellPreviewValue.ContentStart);
        }
    }

    private void UpdatePreviewSearch(bool resetIndex)
    {
        _previewQuery = PreviewFindBox.Text;
        _previewMatches.Clear();

        if (_previewQuery.Length > 0 && _previewText.Length >= _previewQuery.Length)
        {
            var from = 0;
            while (from <= _previewText.Length - _previewQuery.Length
                && _previewMatches.Count < PreviewSearchMatchLimit)
            {
                var found = _previewText.IndexOf(_previewQuery, from, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }

                _previewMatches.Add(new TextRange { StartIndex = found, Length = _previewQuery.Length });
                from = found + _previewQuery.Length;
            }
        }

        if (resetIndex || _previewMatchIndex >= _previewMatches.Count)
        {
            _previewMatchIndex = _previewMatches.Count > 0 ? 0 : -1;
        }

        ApplyPreviewFindVisuals(scrollToMatch: true);
        UpdatePreviewFindStatus();
    }

    private void ApplyPreviewFindVisuals(bool scrollToMatch)
    {
        CellPreviewValue.TextHighlighters.Clear();

        if (_previewMatches.Count > 0)
        {
            var highlighter = new TextHighlighter { Background = _previewFindMatchBrush };
            foreach (var match in _previewMatches)
            {
                highlighter.Ranges.Add(match);
            }

            CellPreviewValue.TextHighlighters.Add(highlighter);
        }

        if (_previewMatchIndex >= 0)
        {
            SelectPreviewMatch(_previewMatchIndex, scrollToMatch);
        }
    }

    private void SelectPreviewMatch(int index, bool scroll)
    {
        if (index < 0 || index >= _previewMatches.Count || CellPreviewValue.Blocks.Count == 0)
        {
            return;
        }

        var match = _previewMatches[index];
        var start = CellPreviewValue.ContentStart.GetPositionAtOffset(match.StartIndex, LogicalDirection.Forward);
        var end = start?.GetPositionAtOffset(match.Length, LogicalDirection.Forward);
        if (start is null || end is null)
        {
            return;
        }

        CellPreviewValue.Select(start, end);

        if (!scroll)
        {
            return;
        }

        // The value view is the only scrollable ancestor, so bringing the character rect into view
        // moves the pane without a second scroll viewer or a line-height guess. Jumping instead of
        // animating keeps fast typing from queuing scroll animations and respects reduced motion.
        CellPreviewValue.UpdateLayout();
        var rect = start.GetCharacterRect(LogicalDirection.Forward);
        CellPreviewValue.StartBringIntoView(new BringIntoViewOptions
        {
            TargetRect = rect,
            VerticalAlignmentRatio = 0.35,
            AnimationDesired = false,
        });
    }

    private void UpdatePreviewFindStatus()
    {
        string text;
        if (_previewQuery.Length == 0)
        {
            text = string.Empty;
        }
        else if (_previewMatches.Count == 0)
        {
            text = "No results";
        }
        else
        {
            var capped = _previewMatches.Count >= PreviewSearchMatchLimit ? "+" : string.Empty;
            text = $"{_previewMatchIndex + 1} of {_previewMatches.Count:N0}{capped}";
        }

        PreviewFindStatus.Text = text;
        // The count is the accessible feedback; keep the announced name equal to what is shown.
        AutomationProperties.SetName(PreviewFindStatus, text.Length > 0 ? text : "Find matches");
    }

    private void MovePreviewMatch(int delta)
    {
        if (_previewMatches.Count == 0)
        {
            return;
        }

        _previewMatchIndex = (_previewMatchIndex + delta + _previewMatches.Count) % _previewMatches.Count;
        SelectPreviewMatch(_previewMatchIndex, scroll: true);
        UpdatePreviewFindStatus();
    }

    private bool IsFocusInPreviewFind()
    {
        if (!_previewFindOpen || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
        {
            return false;
        }

        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, PreviewFindBar))
            {
                return true;
            }
        }

        return false;
    }

    private void PreviewFindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_previewFindOpen)
        {
            return;
        }

        UpdatePreviewSearch(resetIndex: true);
    }

    private void PreviewFindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            MovePreviewMatch(IsShiftDown() ? -1 : 1);
            e.Handled = true;
        }
    }

    private void PreviewFindPrevious_Click(object sender, RoutedEventArgs e) => MovePreviewMatch(-1);

    private void PreviewFindNext_Click(object sender, RoutedEventArgs e) => MovePreviewMatch(1);

    private void PreviewFindClose_Click(object sender, RoutedEventArgs e) => ClosePreviewFind();

    /// <summary>
    /// Syntax colours ride the editor's existing token brushes rather than a second palette, so the
    /// preview and the SQL editor stay visually consistent across Light, Dark and High Contrast.
    /// </summary>
    private void RefreshPreviewBrushes()
    {
        var fallback = StudioColors.Muted;
        _previewPrimaryBrush = ThemeService.GetBrush("StudioTextPrimaryBrush", ActualTheme, fallback);
        _previewMutedBrush = ThemeService.GetBrush("StudioTextMutedBrush", ActualTheme, fallback);
        _previewFindMatchBrush = ThemeService.GetBrush("StudioAccentSoftBrush", ActualTheme, fallback);
        _previewTokenBrushes = new Dictionary<CellSyntaxTokenKind, Brush>
        {
            [CellSyntaxTokenKind.Property] = ThemeService.GetBrush("StudioSyntaxFunctionBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.String] = ThemeService.GetBrush("StudioSyntaxStringBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.Number] = ThemeService.GetBrush("StudioSyntaxNumberBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.Keyword] = ThemeService.GetBrush("StudioSyntaxKeywordBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.Punctuation] = ThemeService.GetBrush("StudioTextSecondaryBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.Tag] = ThemeService.GetBrush("StudioSyntaxFunctionBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.Attribute] = ThemeService.GetBrush("StudioSyntaxVariableBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.Comment] = ThemeService.GetBrush("StudioSyntaxCommentBrush", ActualTheme, fallback),
            [CellSyntaxTokenKind.Declaration] = ThemeService.GetBrush("StudioSyntaxKeywordBrush", ActualTheme, fallback),
        };
    }

    private void ShowReferenceLoading()
    {
        ReferenceSummary.Text = string.Empty;
        ReferenceFooter.Text = string.Empty;
        HideReferenceViews();
        ReferenceStatus.Text = "Loading referenced rows…";
        ReferenceStatus.Visibility = Visibility.Visible;
    }

    private void HideReferenceViews()
    {
        ReferenceTable.Clear();
        ReferenceTable.Visibility = Visibility.Collapsed;
        ReferenceRecords.Clear();
        ReferenceRecords.Visibility = Visibility.Collapsed;
    }

    private async Task LoadReferenceAsync(int row, int column)
    {
        var controller = ReferenceController;
        if (controller is null)
        {
            ShowValuePreview(row, column);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _referenceCancellation = cancellation;

        try
        {
            var snapshot = await controller.PreviewAsync(row, column, cancellation.Token);
            if (cancellation.IsCancellationRequested
                || !_previewOpen
                || _previewMode != PreviewMode.Reference
                || _previewRow != row
                || _previewColumn != column)
            {
                return;
            }

            RenderReference(snapshot);
        }
        catch (OperationCanceledException)
        {
            // Closing the pane or moving to another cell cancels the lookup silently.
        }
        catch (Exception exception)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            HideReferenceViews();
            ReferenceStatus.Text = $"Reference preview failed: {exception.Message}";
            ReferenceStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_referenceCancellation, cancellation))
            {
                _referenceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void RenderReference(CellReferenceSnapshot snapshot)
    {
        if (snapshot.Reference is not { } reference)
        {
            HideReferenceViews();
            ReferenceStatus.Text = snapshot.Error ?? "This cell has no reference.";
            ReferenceStatus.Visibility = Visibility.Visible;
            ReferenceFooter.Text = string.Empty;
            return;
        }

        CellPreviewTitle.Text = reference.SourceColumn;
        CellPreviewSubtitle.Text = $"References {snapshot.TargetLabel}";

        var mode = snapshot.IsList ? "full list" : "matching rows";
        var estimate = snapshot.EstimatedRows is { } rowEstimate ? $" · ~{rowEstimate:N0} rows" : string.Empty;
        ReferenceSummary.Text = $"{reference.Constraint} · {mode}{estimate}";

        if (snapshot.IsNullValue)
        {
            HideReferenceViews();
            ReferenceStatus.Text = "This cell is NULL — no referenced row.";
            ReferenceStatus.Visibility = Visibility.Visible;
            ReferenceFooter.Text = string.Empty;
            return;
        }

        if (snapshot.Error is { } error)
        {
            HideReferenceViews();
            ReferenceStatus.Text = error;
            ReferenceStatus.Visibility = Visibility.Visible;
            ReferenceFooter.Text = string.Empty;
            return;
        }

        var result = snapshot.Result;
        if (result is null || result.Rows.Count == 0)
        {
            HideReferenceViews();
            ReferenceStatus.Text = snapshot.Warning ?? "No referenced rows found.";
            ReferenceStatus.Visibility = Visibility.Visible;
            ReferenceFooter.Text = string.Empty;
            return;
        }

        if (snapshot.IsList)
        {
            ReferenceRecords.Clear();
            ReferenceRecords.Visibility = Visibility.Collapsed;
            ReferenceTable.Show(
                result,
                snapshot.MatchedRows,
                _referenceMatchBrush,
                _referenceBorderBrush,
                _dividerBrush);
            ReferenceTable.Visibility = Visibility.Visible;
        }
        else
        {
            ReferenceTable.Clear();
            ReferenceTable.Visibility = Visibility.Collapsed;
            ReferenceRecords.Show(
                result,
                snapshot.MatchedRows,
                _referenceMatchBrush,
                _referenceBorderBrush);
            ReferenceRecords.Visibility = Visibility.Visible;
        }

        ReferenceStatus.Visibility = Visibility.Collapsed;
        var footer = snapshot.IsList
            ? $"Full list · {result.Rows.Count:N0} row{(result.Rows.Count == 1 ? string.Empty : "s")}"
            : $"Matching rows · {result.Rows.Count:N0} row{(result.Rows.Count == 1 ? string.Empty : "s")}";

        if (snapshot.MatchedRows.Count > 0)
        {
            footer += $" · match on row {snapshot.MatchedRows[0] + 1:N0}";
        }
        else if (snapshot.Warning is null)
        {
            footer += " · no match in loaded rows";
        }

        if (snapshot.Truncated)
        {
            footer += " · truncated";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Warning))
        {
            footer += $" · {snapshot.Warning}";
        }

        ReferenceFooter.Text = footer;
    }

    private void HideCellPreview()
    {
        if (!_previewOpen)
        {
            return;
        }

        _referenceCancellation?.Cancel();
        _referenceCancellation?.Dispose();
        _referenceCancellation = null;

        _previewOpen = false;
        _previewRow = -1;
        _previewColumn = -1;
        _previewCopyText = string.Empty;
        PreviewHost.Visibility = Visibility.Collapsed;
        ResetPreviewFind();
        _previewText = string.Empty;
        _previewTokens = [];
        CellPreviewValue.Blocks.Clear();
        CellPreviewFooter.Text = string.Empty;
        HideReferenceViews();
        ReferenceView.Visibility = Visibility.Collapsed;
    }

    /// <summary>User closed the pane: it stays closed across results until opened again.</summary>
    private void CloseCellPreview()
    {
        _previewRequested = false;
        HideCellPreview();
    }

    /// <summary>Keeps the panel in step with the focused cell while the user arrows around.</summary>
    private void RefreshPreviewIfOpen()
    {
        if (_previewOpen)
        {
            ShowCellPreview(_focusRow, _focusColumn, _previewMode);
        }
    }

    private void ReferenceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewOpen || _previewRow < 0 || _previewColumn < 0)
        {
            return;
        }

        ShowCellPreview(
            _previewRow,
            _previewColumn,
            _previewMode == PreviewMode.Reference ? PreviewMode.Value : PreviewMode.Reference);
    }


    private void PreviewCell_Click(object sender, RoutedEventArgs e) =>
        ShowCellPreview(_focusRow, _focusColumn);

    private void CopyPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewOpen)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_previewCopyText);

        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Copy failed: {ex.Message}");
            return;
        }

        try
        {
            // Another process can briefly hold the clipboard open; the content is already set.
            Clipboard.Flush();
        }
        catch
        {
            // Best effort.
        }

        StatusChanged?.Invoke(this, "Cell value copied");
        Focus(FocusState.Programmatic);
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        CloseCellPreview();
        Focus(FocusState.Programmatic);
    }

    private void SetPreviewFormat(CellFormat format)
    {
        _previewFormat = format;
        AppLog.Info("preview", $"format: {format}");
        PreviewFormatLabel.Text = format switch
        {
            CellFormat.Json => "JSON",
            CellFormat.Xml => "XML",
            CellFormat.Plain => "Plain",
            _ => "Auto",
        };

        if (_previewOpen)
        {
            ShowCellPreview(_previewRow, _previewColumn);
        }
    }

    private void FormatAuto_Click(object sender, RoutedEventArgs e) => SetPreviewFormat(CellFormat.Auto);

    private void FormatJson_Click(object sender, RoutedEventArgs e) => SetPreviewFormat(CellFormat.Json);

    private void FormatXml_Click(object sender, RoutedEventArgs e) => SetPreviewFormat(CellFormat.Xml);

    private void FormatPlain_Click(object sender, RoutedEventArgs e) => SetPreviewFormat(CellFormat.Plain);

    // ---------------------------------------------------------------- preview resize

    private void PreviewResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _resizingPreview = true;
        _previewResizeStartWidth = CellPreview.ActualWidth;
        _previewResizeStartX = e.GetCurrentPoint(this).Position.X;
        PreviewResizeHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PreviewResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingPreview)
        {
            return;
        }

        var x = e.GetCurrentPoint(this).Position.X;
        CellPreview.Width = Math.Clamp(
            _previewResizeStartWidth - (x - _previewResizeStartX),
            MinPreviewWidth,
            MaxPreviewWidthForControl());
        _desiredPreviewWidth = CellPreview.Width;
        e.Handled = true;
    }

    private void PreviewResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingPreview)
        {
            return;
        }

        _resizingPreview = false;
        PreviewResizeHandle.ReleasePointerCapture(e.Pointer);
        PreviewWidthChanged?.Invoke(this, CellPreview.Width);
        e.Handled = true;
    }

    private void PreviewResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        _resizingPreview = false;

    /// <summary>The preview never squeezes the grid below a usable width.</summary>
    private double MaxPreviewWidthForControl() =>
        Math.Max(MinPreviewWidth, Math.Min(MaxPreviewWidth, ActualWidth - GridReserveWidth));

    // ---------------------------------------------------------------- clipboard

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection(withHeaders: false);

    private void CopyWithHeaders_Click(object sender, RoutedEventArgs e) => CopySelection(withHeaders: true);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();

    private void CopySelection(bool withHeaders)
    {
        if (!TryGetSelection(out var top, out var left, out var bottom, out var right) || _layout is null)
        {
            return;
        }

        var columnCount = right - left + 1;
        var rowCount = bottom - top + 1;
        var lines = new List<string>(rowCount + 1);

        if (withHeaders)
        {
            lines.Add(string.Join('\t', _layout.Columns
                .Skip(left)
                .Take(columnCount)
                .Select(column => Escape(column.Name))));
        }

        for (var row = top; row <= bottom; row++)
        {
            lines.Add(string.Join('\t', _rows[row].Cells
                .Skip(left)
                .Take(columnCount)
                .Select(value => Escape(value ?? string.Empty))));
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, lines));

        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Copy failed: {ex.Message}");
            return;
        }

        try
        {
            // Another process can briefly hold the clipboard open; the content is already set,
            // so a failed flush must not take the studio down.
            Clipboard.Flush();
        }
        catch
        {
            // Best effort.
        }

        StatusChanged?.Invoke(
            this,
            withHeaders
                ? $"Copied {rowCount}×{columnCount} cells with headers"
                : $"Copied {rowCount}×{columnCount} cells");
    }

    private bool TryGetSelection(out int top, out int left, out int bottom, out int right)
    {
        top = left = bottom = right = 0;
        if (!_hasSelection || _layout is null || _rows.Count == 0)
        {
            return false;
        }

        top = Math.Min(_anchorRow, _focusRow);
        bottom = Math.Max(_anchorRow, _focusRow);
        left = Math.Min(_anchorColumn, _focusColumn);
        right = Math.Max(_anchorColumn, _focusColumn);
        return true;
    }

    private static string Escape(string value) =>
        value.Contains('\t') || value.Contains('\n') || value.Contains('\r') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ---------------------------------------------------------------- scrolling

    private void RowsList_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureListScroller();
    }

    private void RowsList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_syncingScroll || _listScroller is null)
        {
            return;
        }

        _syncingScroll = true;
        HeaderScroller.ChangeView(_listScroller.HorizontalOffset, null, null, disableAnimation: true);
        _syncingScroll = false;
    }

    private void HeaderScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_syncingScroll || _listScroller is null)
        {
            return;
        }

        _syncingScroll = true;
        _listScroller.ChangeView(HeaderScroller.HorizontalOffset, null, null, disableAnimation: true);
        _syncingScroll = false;
    }

    private void SaveViewState()
    {
        if (_result is null)
        {
            return;
        }

        var state = _viewStates.GetOrCreateValue(_result);
        if (_listScroller is not null)
        {
            state.VerticalOffset = _listScroller.VerticalOffset;
            state.HorizontalOffset = _listScroller.HorizontalOffset;
        }

        state.HasSelection = _hasSelection;
        state.AnchorRow = _anchorRow;
        state.AnchorColumn = _anchorColumn;
        state.FocusRow = _focusRow;
        state.FocusColumn = _focusColumn;
        state.PreviewRequested = _previewRequested;
    }

    private void RestoreViewState(ResultViewState state, QueryResult result)
    {
        void Restore()
        {
            if (!ReferenceEquals(_result, result) || _listScroller is null)
            {
                return;
            }

            _listScroller.ChangeView(
                state.HorizontalOffset,
                state.VerticalOffset,
                null,
                disableAnimation: true);
        }

        // Give the new item source a layout pass so the scroll extent exists before restoring.
        RowsList.UpdateLayout();
        Restore();
        DispatcherQueue.TryEnqueue(Restore);
    }

    // ---------------------------------------------------------------- helpers

    private void UpdateAccentBrushes()
    {
        var color = StudioColors.Parse(_accentHex);
        _selectionBrush = new SolidColorBrush(StudioColors.WithAlpha(color, 0x2E));
        _activeCellBrush = new SolidColorBrush(StudioColors.WithAlpha(color, 0x70));
        _headerSelectionBrush = new SolidColorBrush(StudioColors.WithAlpha(color, 0x42));
        _referenceMatchBrush = new SolidColorBrush(StudioColors.WithAlpha(color, 0x24));
        _referenceBorderBrush = new SolidColorBrush(StudioColors.WithAlpha(color, 0xC0));
    }

    /// <summary>
    /// Rebuilds the header and repaints realized rows after the user switches Light/Dark. Token
    /// brushes are cached in fields and in row views, so the framework cannot refresh them alone.
    /// </summary>
    private void ApplyTheme()
    {
        _dividerBrush = ThemeService.GetBrush("DividerStrokeColorDefaultBrush", ActualTheme, StudioColors.Muted);
        _headerBackground = ThemeService.GetBrush("HairlineSubtleBrush", ActualTheme, StudioColors.Muted);

        if (_layout is not null)
        {
            BuildHeader(_layout);
        }

        foreach (var row in FindDescendants<ResultRowView>(RowsList))
        {
            row.ApplyTheme();
        }

        RefreshPreviewBrushes();

        if (_previewOpen && _previewMode == PreviewMode.Value)
        {
            // Runs carry their own brush instances, so a Light/Dark switch has to rebuild the text.
            RenderPreviewText();
            ApplyPreviewFindVisuals(scrollToMatch: false);
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsShiftDown() => IsKeyDown(VirtualKey.Shift);

    private static bool IsNumeric(string? value) =>
        value is not null
        && double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T target)
            {
                return target;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
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

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T target)
            {
                yield return target;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
