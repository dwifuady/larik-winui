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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
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
    private IReadOnlyList<int> _rowNumbers = [];
    private ScrollViewer? _listScroller;
    private ScrollViewer? _rowNumberScroller;
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

    private int _dragColumn = -1;
    private double _dragStartX;
    private bool _dragMoved;
    private int _dropIndex = -1;

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

        // Rows and headers cache brushes in code; a runtime Light/Dark switch must rebuild them.
        ActualThemeChanged += (_, _) => ApplyTheme();

        // ListViewItem marks pointer presses handled for its own selection bookkeeping. Listen
        // for handled events too, otherwise clicking a cell never reaches ResultsGrid.
        RowsList.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(RowsList_PointerPressed),
            handledEventsToo: true);

        // The row-number list's own scroller consumes the wheel before a normal handler runs.
        RowNumberList.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(RowNumberList_PointerWheelChanged),
            handledEventsToo: true);

        // ListViewItem marks pointer presses handled for its selection bookkeeping; listen for
        // handled events or clicking a row number would never select the row.
        RowNumberList.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(RowNumberList_PointerPressed),
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
        if (scroller is not null && !ReferenceEquals(scroller, _listScroller))
        {
            if (_listScroller is not null)
            {
                _listScroller.ViewChanged -= RowsList_ViewChanged;
            }

            _listScroller = scroller;
            _listScroller.ViewChanged += RowsList_ViewChanged;
        }

        _rowNumberScroller = FindDescendant<ScrollViewer>(RowNumberList) ?? _rowNumberScroller;
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
        _rowNumbers = Enumerable.Range(1, _rows.Count).ToList();
        RowNumberList.ItemsSource = _rowNumbers;
        var rowNumberWidth = RowNumberWidth(_rows.Count);
        RowNumberHeader.Width = rowNumberWidth;
        RowNumberList.Width = rowNumberWidth;
        RowNumberHeader.Visibility = Visibility.Visible;
        RowNumberList.Visibility = Visibility.Visible;
        RowNumberList.UpdateLayout();
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
        _rowNumbers = [];
        _hasSelection = false;
        IsTabStop = false;
        RowsList.ItemsSource = null;
        RowNumberList.ItemsSource = null;
        RowNumberHeader.Visibility = Visibility.Collapsed;
        RowNumberList.Visibility = Visibility.Collapsed;
        HeaderHost.Children.Clear();
        HeaderContent.Width = double.NaN;
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
                SourceIndex = c,
                DataType = result.Columns[c].DataType,
                Width = Math.Clamp(24 + (longest * 7.2), 72, 360),
                Numeric = numeric,
            });
        }

        return new ResultLayout { Columns = columns };
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
        HeaderContent.Width = layout.TotalWidth;
        ColumnDropIndicator.Visibility = Visibility.Collapsed;

        for (var index = 0; index < layout.Columns.Count; index++)
        {
            var column = layout.Columns[index];
            var referenceLabel = ReferenceController?.Describe(column.SourceIndex);
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
                BeginHeaderDrag(headerIndex, args);
                args.Handled = true;
            };
            surface.PointerMoved += HeaderSurface_PointerMoved;
            surface.PointerReleased += HeaderSurface_PointerReleased;
            surface.PointerCaptureLost += (_, _) => EndHeaderDrag();
            var canSetReference = referenceLabel is null
                && ReferenceController?.GetSource(column.SourceIndex) is not null;
            AutomationProperties.SetName(surface, $"Reorder {column.Name} column");
            ToolTipService.SetToolTip(
                surface,
                canSetReference
                    ? "Click to select · drag to reorder · right-click to set a custom reference"
                    : "Click to select · drag to reorder");

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

    // ---------------------------------------------------------------- column reorder

    private void BeginHeaderDrag(int index, PointerRoutedEventArgs e)
    {
        if (index < 0 || index >= _headerSurfaces.Count)
        {
            return;
        }

        _dragColumn = index;
        _dragStartX = e.GetCurrentPoint(HeaderHost).Position.X;
        _dragMoved = false;
        _dropIndex = index;
        _headerSurfaces[index].CapturePointer(e.Pointer);
    }

    private void HeaderSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragColumn < 0
            || _layout is null
            || _dragColumn >= _headerSurfaces.Count
            || !ReferenceEquals(sender, _headerSurfaces[_dragColumn]))
        {
            return;
        }

        var x = e.GetCurrentPoint(HeaderHost).Position.X;
        if (!_dragMoved && Math.Abs(x - _dragStartX) < 6)
        {
            return;
        }

        _dragMoved = true;
        _dropIndex = DropIndexAt(x);
        ShowDropIndicator(_dropIndex);
        e.Handled = true;
    }

    private void HeaderSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragColumn < 0 || !ReferenceEquals(sender, _headerSurfaces[_dragColumn]))
        {
            return;
        }

        var from = _dragColumn;
        var moved = _dragMoved;
        var drop = _dropIndex;
        EndHeaderDrag();

        if (moved && drop >= 0)
        {
            ReorderColumn(from, drop);
        }

        e.Handled = true;
    }

    private void EndHeaderDrag()
    {
        var dragged = _dragColumn;
        _dragColumn = -1;
        _dragMoved = false;
        _dropIndex = -1;
        ColumnDropIndicator.Visibility = Visibility.Collapsed;

        if (dragged >= 0 && dragged < _headerSurfaces.Count)
        {
            _headerSurfaces[dragged].ReleasePointerCaptures();
        }
    }

    /// <summary>The insertion index for a pointer x in header content coordinates.</summary>
    private int DropIndexAt(double x)
    {
        if (_layout is null)
        {
            return -1;
        }

        var left = 0d;
        for (var index = 0; index < _layout.Columns.Count; index++)
        {
            var width = _layout.Columns[index].Width;
            if (x < left + (width / 2))
            {
                return index;
            }

            left += width;
        }

        return _layout.Columns.Count;
    }

    private void ShowDropIndicator(int index)
    {
        if (_layout is null || index < 0)
        {
            ColumnDropIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var left = 0d;
        for (var i = 0; i < Math.Min(index, _layout.Columns.Count); i++)
        {
            left += _layout.Columns[i].Width;
        }

        ColumnDropIndicator.Height = HeaderHeight;
        ColumnDropIndicator.Margin = new Thickness(left, 0, 0, 0);
        ColumnDropIndicator.Visibility = Visibility.Visible;
    }

    private void ReorderColumn(int from, int insertionIndex)
    {
        if (_layout is null || from < 0 || from >= _layout.Columns.Count)
        {
            return;
        }

        // Removing the dragged column first means dropping to its right lands after the neighbour.
        var target = insertionIndex > from ? insertionIndex - 1 : insertionIndex;
        target = Math.Clamp(target, 0, _layout.Columns.Count - 1);
        if (target == from)
        {
            return;
        }

        var column = _layout.Columns[from];
        _layout.Columns.RemoveAt(from);
        _layout.Columns.Insert(target, column);

        _anchorColumn = MapColumnAfterReorder(_anchorColumn, from, target);
        _focusColumn = MapColumnAfterReorder(_focusColumn, from, target);

        BuildHeader(_layout);
        foreach (var row in FindDescendants<ResultRowView>(RowsList))
        {
            row.Reload();
        }

        UpdateSelectionVisuals();
    }

    private static int MapColumnAfterReorder(int index, int from, int to)
    {
        if (index == from)
        {
            return to;
        }

        if (from < index && index <= to)
        {
            return index - 1;
        }

        if (to <= index && index < from)
        {
            return index + 1;
        }

        return index;
    }

    // ---------------------------------------------------------------- row numbers

    private static double RowNumberWidth(int rowCount)
    {
        var digits = Math.Max(1, rowCount.ToString().Length);
        return Math.Clamp(16 + (digits * 8), 28, 56);
    }

    private void RowNumberList_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureListScroller();
        if (_listScroller is not null && _rowNumberScroller is not null)
        {
            _rowNumberScroller.ChangeView(
                null,
                _listScroller.VerticalOffset,
                null,
                disableAnimation: true);
        }
    }

    private void RowNumberList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(RowNumberList);
        if (point.Properties.IsRightButtonPressed)
        {
            return;
        }

        var row = -1;
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<ListViewItem>(source) is { } item)
        {
            row = RowNumberList.IndexFromContainer(item);
        }

        if (row < 0)
        {
            var offset = _rowNumberScroller?.VerticalOffset ?? 0;
            row = (int)Math.Floor((point.Position.Y + offset) / RowHeight);
        }

        if (row < 0 || row >= _rows.Count)
        {
            return;
        }

        SelectRow(row);
        e.Handled = true;
    }

    private void RowNumberList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_listScroller is null)
        {
            return;
        }

        var delta = e.GetCurrentPoint(RowNumberList).Properties.MouseWheelDelta;
        var target = Math.Max(0, _listScroller.VerticalOffset - delta);
        _listScroller.ChangeView(null, target, null, disableAnimation: true);
        e.Handled = true;
    }

    private void RowNumberList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem item)
        {
            item.Background = IsRowSelected(args.ItemIndex)
                ? _selectionBrush
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void SelectRow(int row)
    {
        if (!_hasSelection || _layout is null || row < 0 || row >= _rows.Count)
        {
            return;
        }

        if (!IsShiftDown())
        {
            _anchorRow = row;
        }

        _focusRow = row;
        _anchorColumn = 0;
        _focusColumn = _layout.Columns.Count - 1;

        RowsList.ScrollIntoView(_rows[row], ScrollIntoViewAlignment.Default);
        Focus(FocusState.Programmatic);
        UpdateSelectionVisuals();
    }

    private bool IsRowSelected(int row)
    {
        if (!_hasSelection)
        {
            return false;
        }

        var min = Math.Min(_anchorRow, _focusRow);
        var max = Math.Max(_anchorRow, _focusRow);
        return row >= min && row <= max;
    }

    private void UpdateRowNumberSelection()
    {
        if (_rowNumbers.Count == 0 || RowNumberList.ItemsSource is null)
        {
            return;
        }

        var offset = _rowNumberScroller?.VerticalOffset ?? 0;
        var first = Math.Max(0, (int)(offset / RowHeight));
        var last = Math.Min(
            _rowNumbers.Count - 1,
            first + (int)(RowNumberList.ActualHeight / RowHeight) + 2);

        for (var index = first; index <= last; index++)
        {
            if (RowNumberList.ContainerFromIndex(index) is ListViewItem item)
            {
                item.Background = IsRowSelected(index)
                    ? _selectionBrush
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
    }

    // ---------------------------------------------------------------- resize

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
        var preserveSelection = point.Properties.IsRightButtonPressed
            && !extend
            && IsCellInSelection(row.Index, column);

        if (!extend && !preserveSelection)
        {
            _anchorRow = row.Index;
            _anchorColumn = column;
        }

        // Right-clicking inside an existing selection keeps that selection (and target contract)
        // while making the clicked cell active. Requiring Shift to keep the block was a bug.
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

    private bool IsCellInSelection(int row, int column)
    {
        if (!_hasSelection)
        {
            return false;
        }

        var minRow = Math.Min(_anchorRow, _focusRow);
        var maxRow = Math.Max(_anchorRow, _focusRow);
        var minColumn = Math.Min(_anchorColumn, _focusColumn);
        var maxColumn = Math.Max(_anchorColumn, _focusColumn);
        return row >= minRow && row <= maxRow && column >= minColumn && column <= maxColumn;
    }

    private void ResultsGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_hasSelection || _layout is null || _rows.Count == 0)
        {
            return;
        }

        var shift = IsKeyDown(VirtualKey.Shift);
        var control = IsKeyDown(VirtualKey.Control);

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

    /// <summary>Maps a display column to the provider's original cell index after a drag reorder.</summary>
    private int SourceIndexFor(int displayColumn) =>
        _layout is not null && displayColumn >= 0 && displayColumn < _layout.Columns.Count
            ? _layout.Columns[displayColumn].SourceIndex
            : displayColumn;

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
        UpdateRowNumberSelection();
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

        var mode = reference && ReferenceController?.CanPreview(SourceIndexFor(_focusColumn)) == true
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

        if (mode == PreviewMode.Reference && ReferenceController?.CanPreview(SourceIndexFor(column)) != true)
        {
            mode = PreviewMode.Value;
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

        var canPreviewReference = ReferenceController?.CanPreview(SourceIndexFor(column)) == true;
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
        var sourceIndex = layout.SourceIndex;
        var value = sourceIndex < _rows[row].Cells.Count ? _rows[row].Cells[sourceIndex] : null;
        var muted = ThemeService.GetBrush("StudioTextMutedBrush", ActualTheme, StudioColors.Muted);
        var subtitle = string.IsNullOrWhiteSpace(layout.DataType)
            ? $"row {_rows[row].Index + 1}"
            : $"{layout.DataType} · row {_rows[row].Index + 1}";

        CellPreviewTitle.Text = layout.Name;

        if (value is null)
        {
            _previewCopyText = string.Empty;
            CellPreviewSubtitle.Text = subtitle;
            CellPreviewValue.Text = "NULL";
            CellPreviewValue.Foreground = muted;
            CellPreviewFooter.Text = string.Empty;
            return;
        }

        if (value.Length == 0)
        {
            _previewCopyText = string.Empty;
            CellPreviewSubtitle.Text = subtitle;
            CellPreviewValue.Text = "\"\"";
            CellPreviewValue.Foreground = muted;
            CellPreviewFooter.Text = "Empty string";
            return;
        }

        var formatted = CellContentFormatter.Format(value, _previewFormat);
        _previewCopyText = formatted.Text;

        CellPreviewSubtitle.Text = formatted.Kind is CellFormat.Json or CellFormat.Xml
            ? $"{subtitle} · {formatted.Kind.ToString().ToUpperInvariant()}"
            : subtitle;

        // A single multi-megabyte cell would make the TextBlock the slowest thing in the window;
        // the copy button still hands over the whole formatted value.
        var display = formatted.Text.Length > PreviewCharacterLimit
            ? formatted.Text[..PreviewCharacterLimit]
            : formatted.Text;

        CellPreviewValue.Text = display;
        CellPreviewValue.Foreground = ThemeService.GetBrush("StudioTextPrimaryBrush", ActualTheme, StudioColors.Muted);

        var footer = formatted.Text.Length > PreviewCharacterLimit
            ? $"Showing first {PreviewCharacterLimit:N0} of {formatted.Text.Length:N0} characters — Copy copies all"
            : $"{formatted.Text.Length:N0} characters";

        if (formatted.Error is { } error)
        {
            footer = $"{footer} · {error}";
        }

        CellPreviewFooter.Text = footer;
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
            var snapshot = await controller.PreviewAsync(row, SourceIndexFor(column), cancellation.Token);
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
        CellPreviewValue.Text = string.Empty;
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

    // ---------------------------------------------------------------- custom references

    private void ResultsMenu_Opening(object sender, object e)
    {
        var sourceIndex = SourceIndexFor(_focusColumn);
        var reference = ReferenceController?.GetReference(sourceIndex);
        var hasSource = ReferenceController?.GetSource(sourceIndex) is not null;
        var isCustom = reference?.IsVirtual == true;

        // A real database FK always wins, so offering a custom reference there would be a lie.
        SetReferenceMenuItem.Visibility = hasSource && reference is not { IsVirtual: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetReferenceMenuItem.Text = isCustom ? "Edit custom reference…" : "Set custom reference…";
        AutomationProperties.SetName(
            SetReferenceMenuItem,
            isCustom ? "Edit custom reference" : "Set custom reference");

        RemoveReferenceMenuItem.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SetReference_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceController is not { } controller || _layout is null)
        {
            return;
        }

        var sourceIndex = SourceIndexFor(_focusColumn);
        if (controller.GetSource(sourceIndex) is not { } source)
        {
            return;
        }

        var existing = controller.GetReference(sourceIndex);
        var choice = await ShowReferenceEditorAsync(source.Label, controller.Tables, existing);
        if (choice is null)
        {
            return;
        }

        if (!controller.SaveReference(source, choice.Table, choice.Column))
        {
            StatusChanged?.Invoke(this, "Could not save the custom reference.");
            return;
        }

        ApplyReferenceChange(
            $"Custom reference saved · {source.Label} → {choice.Table.Schema}.{choice.Table.Name}.{choice.Column}");
    }

    private sealed record ReferenceEditorChoice(TableSchema Table, string Column);

    /// <summary>
    /// A ContentDialog cannot be moved, which hides the rows the user is comparing against. This
    /// card is a small draggable overlay instead: the window-darkening scrim blocks the page, and
    /// its title strip moves the card anywhere over the whole window.
    /// </summary>
    private async Task<ReferenceEditorChoice?> ShowReferenceEditorAsync(
        string sourceLabel,
        IReadOnlyList<TableSchema> tables,
        ColumnReference? existing)
    {
        if (XamlRoot is not { } xamlRoot)
        {
            return null;
        }

        var editor = new ReferenceEditorDialog(
            sourceLabel,
            tables,
            existing?.TargetSchema,
            existing?.TargetTable,
            existing?.TargetColumn);

        var completion = new TaskCompletionSource<ReferenceEditorChoice?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var translate = new TranslateTransform();
        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = existing?.IsVirtual == true ? "Edit custom reference" : "Set custom reference",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var closeButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
        };
        AutomationProperties.SetName(closeButton, "Close custom reference");

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(title);
        Grid.SetColumn(closeButton, 1);
        headerGrid.Children.Add(closeButton);

        var header = new MoveHandle
        {
            Padding = new Thickness(16, 10, 8, 10),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        header.Children.Add(headerGrid);
        AutomationProperties.SetName(header, "Drag custom reference");

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(6),
        };
        AutomationProperties.SetName(cancelButton, "Cancel custom reference");

        var saveButton = new Button
        {
            Content = "Save reference",
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(6),
            IsEnabled = editor.CanSave,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        AutomationProperties.SetName(saveButton, "Save custom reference");

        var footer = new Grid
        {
            Padding = new Thickness(16, 4, 16, 14),
            ColumnSpacing = 8,
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(cancelButton, 1);
        Grid.SetColumn(saveButton, 2);
        footer.Children.Add(cancelButton);
        footer.Children.Add(saveButton);

        editor.Margin = new Thickness(16, 0, 16, 0);
        cardGrid.Children.Add(header);
        cardGrid.Children.Add(editor);
        cardGrid.Children.Add(footer);
        Grid.SetRow(editor, 1);
        Grid.SetRow(footer, 2);

        var popupTheme = ActualTheme switch
        {
            ElementTheme.Light => ElementTheme.Light,
            ElementTheme.Dark => ElementTheme.Dark,
            _ => ThemeService.CurrentTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light,
        };

        var card = new Border
        {
            Width = 592,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeService.GetBrush(
                "ElevatedSurfaceBrush",
                popupTheme,
                new SolidColorBrush(popupTheme == ElementTheme.Dark
                    ? Windows.UI.Color.FromArgb(0xFF, 0x23, 0x23, 0x26)
                    : Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0))),
            BorderBrush = ThemeService.GetBrush(
                "HairlineBrush",
                popupTheme,
                new SolidColorBrush(Microsoft.UI.Colors.Gray)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            RenderTransform = translate,
            RequestedTheme = popupTheme,
            Child = cardGrid,
        };
        AutomationProperties.SetName(card, "Custom reference");

        var overlay = new Grid
        {
            Width = xamlRoot.Size.Width,
            Height = xamlRoot.Size.Height,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0, 0, 0)),
            IsTabStop = true,
            RequestedTheme = popupTheme,
        };
        overlay.Children.Add(card);

        var popup = new Popup
        {
            XamlRoot = xamlRoot,
            Child = overlay,
            IsLightDismissEnabled = false,
            ShouldConstrainToRootBounds = false,
            HorizontalOffset = 0,
            VerticalOffset = 0,
        };

        void Complete(ReferenceEditorChoice? choice)
        {
            if (!completion.Task.IsCompleted)
            {
                completion.TrySetResult(choice);
            }
        }

        void ResizeForRoot()
        {
            overlay.Width = xamlRoot.Size.Width;
            overlay.Height = xamlRoot.Size.Height;
        }

        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeForRoot();

        editor.SelectionChanged += (_, _) => saveButton.IsEnabled = editor.CanSave;
        closeButton.Click += (_, _) => Complete(null);
        cancelButton.Click += (_, _) => Complete(null);
        saveButton.Click += (_, _) =>
        {
            if (editor.SelectedTable is { } table && editor.SelectedColumn is { Length: > 0 } column)
            {
                Complete(new ReferenceEditorChoice(table, column));
            }
        };

        overlay.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                Complete(null);
                args.Handled = true;
            }
        };

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            Complete(null);
            args.Handled = true;
        };
        overlay.KeyboardAccelerators.Add(escape);

        var dragging = false;
        Point dragStart = default;
        double dragStartX = 0;
        double dragStartY = 0;

        header.PointerPressed += (_, args) =>
        {
            dragging = true;
            dragStart = args.GetCurrentPoint(overlay).Position;
            dragStartX = translate.X;
            dragStartY = translate.Y;
            header.CapturePointer(args.Pointer);
            args.Handled = true;
        };

        header.PointerMoved += (_, args) =>
        {
            if (!dragging)
            {
                return;
            }

            var position = args.GetCurrentPoint(overlay).Position;
            var maxX = Math.Max(0, ((overlay.Width - card.ActualWidth) / 2) - 8);
            var maxY = Math.Max(0, ((overlay.Height - card.ActualHeight) / 2) - 8);
            translate.X = Math.Clamp(dragStartX + (position.X - dragStart.X), -maxX, maxX);
            translate.Y = Math.Clamp(dragStartY + (position.Y - dragStart.Y), -maxY, maxY);
            args.Handled = true;
        };

        header.PointerReleased += (_, args) =>
        {
            dragging = false;
            header.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        };

        header.PointerCaptureLost += (_, _) => dragging = false;

        xamlRoot.Changed += RootChanged;
        popup.Opened += (_, _) => DispatcherQueue.TryEnqueue(editor.FocusSearch);

        try
        {
            popup.IsOpen = true;
            return await completion.Task;
        }
        finally
        {
            popup.IsOpen = false;
            popup.Child = null;
            xamlRoot.Changed -= RootChanged;
        }
    }


    private void RemoveReference_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceController is not { } controller)
        {
            return;
        }

        var sourceIndex = SourceIndexFor(_focusColumn);
        var source = controller.GetSource(sourceIndex);
        if (!controller.DeleteReference(sourceIndex))
        {
            StatusChanged?.Invoke(this, "Could not remove the custom reference.");
            return;
        }

        ApplyReferenceChange(source is null ? "Custom reference removed" : $"Custom reference removed · {source.Label}");
    }

    private void ApplyReferenceChange(string status)
    {
        if (_layout is not null)
        {
            BuildHeader(_layout);
        }

        UpdateSelectionVisuals();
        StatusChanged?.Invoke(this, status);
    }

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

    private void CopyAsInsert_Click(object sender, RoutedEventArgs e) => CopyAsSql(insert: true);

    private void CopyAsInClause_Click(object sender, RoutedEventArgs e) => CopyAsSql(insert: false);

    /// <summary>
    /// Mirrors the desktop app: Copy as INSERT copies a VALUES list and Copy as IN copies a
    /// parenthesised list, both formatted as SQL literals for the provider's column types.
    /// </summary>
    private void CopyAsSql(bool insert)
    {
        if (!TryGetSelection(out var top, out var left, out var bottom, out var right) || _layout is null)
        {
            return;
        }

        // A right-click on one cell means "this row" for INSERT, like the desktop app; a real
        // multi-cell selection is respected as the chosen column range.
        if (insert && top == bottom && left == right)
        {
            left = 0;
            right = _layout.Columns.Count - 1;
        }

        var columns = _layout.Columns.Skip(left).Take(right - left + 1).ToList();
        var rows = new List<string>();
        var values = new List<string>();

        for (var row = top; row <= bottom; row++)
        {
            var cells = _rows[row].Cells;
            var rowValues = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                var value = column.SourceIndex < cells.Count ? cells[column.SourceIndex] : null;
                var literal = FormatSqlLiteral(value, column.DataType);
                rowValues.Add(literal);
                values.Add(literal);
            }

            rows.Add($"({string.Join(", ", rowValues)})");
        }

        var text = insert
            ? $"VALUES {string.Join(",\n       ", rows)}"
            : $"IN ({string.Join(", ", values)})";

        CopyText(text, insert
            ? $"Copied {rows.Count} row{(rows.Count == 1 ? string.Empty : "s")} as INSERT"
            : $"Copied {values.Count} value{(values.Count == 1 ? string.Empty : "s")} as IN clause");
    }

    private void CopyText(string text, string status)
    {
        var package = new DataPackage();
        package.SetText(text);

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

        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Formats one provider-formatted cell as a SQL literal, using the column's type.</summary>
    internal static string FormatSqlLiteral(string? value, string? dataType)
    {
        if (value is null)
        {
            return "NULL";
        }

        var type = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        var escaped = value.Replace("'", "''");

        if (type.Contains("binary") || type.Contains("image"))
        {
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? value
                : $"'{escaped}'";
        }

        if (type is "bit" or "boolean" or "bool")
        {
            if (value is "1" or "0")
            {
                return value;
            }

            if (bool.TryParse(value, out var flag))
            {
                return flag ? "1" : "0";
            }
        }

        var numeric = type.Contains("int")
            || type.Contains("decimal")
            || type.Contains("numeric")
            || type.Contains("float")
            || type.Contains("real")
            || type.Contains("money")
            || type.Contains("double");
        if (numeric
            && decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        if (type.Contains("nvarchar") || type.Contains("nchar") || type.Contains("ntext"))
        {
            return $"N'{escaped}'";
        }

        return $"'{escaped}'";
    }

    private void CopySelection(bool withHeaders)
    {
        if (!TryGetSelection(out var top, out var left, out var bottom, out var right) || _layout is null)
        {
            return;
        }

        var columnCount = right - left + 1;
        var rowCount = bottom - top + 1;
        var columns = _layout.Columns.Skip(left).Take(columnCount).ToList();
        var lines = new List<string>(rowCount + 1);

        if (withHeaders)
        {
            lines.Add(string.Join('\t', columns.Select(column => Escape(column.Name))));
        }

        for (var row = top; row <= bottom; row++)
        {
            var cells = _rows[row].Cells;
            lines.Add(string.Join('\t', columns.Select(column =>
                Escape(column.SourceIndex < cells.Count ? cells[column.SourceIndex] ?? string.Empty : string.Empty))));
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

        if (_rowNumberScroller is not null)
        {
            _rowNumberScroller.ChangeView(null, _listScroller.VerticalOffset, null, disableAnimation: true);
        }

        UpdateRowNumberSelection();
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
            _rowNumberScroller?.ChangeView(null, state.VerticalOffset, null, disableAnimation: true);
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

        if (_previewOpen)
        {
            CellPreviewValue.Foreground = ThemeService.GetBrush(
                "StudioTextPrimaryBrush",
                ActualTheme,
                StudioColors.Muted);
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
