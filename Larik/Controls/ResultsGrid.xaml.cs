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
    private bool _previewOpen;
    private int _previewRow = -1;
    private int _previewColumn = -1;
    private CellFormat _previewFormat = CellFormat.Auto;
    private string _previewCopyText = string.Empty;
    private bool _resizingPreview;
    private double _previewResizeStartX;
    private double _previewResizeStartWidth;

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
    }

    public ResultsGrid()
    {
        InitializeComponent();

        _dividerBrush = BrushOr("DividerStrokeColorDefaultBrush", StudioColors.Muted);
        _headerBackground = BrushOr("HairlineSubtleBrush", StudioColors.Muted);
        UpdateAccentBrushes();

        // ListViewItem marks pointer presses handled for its own selection bookkeeping. Listen
        // for handled events too, otherwise clicking a cell never reaches ResultsGrid.
        RowsList.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(RowsList_PointerPressed),
            handledEventsToo: true);
    }

    /// <summary>Raised after a copy so the shell can report it in the status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Shows a result, or clears the grid when <paramref name="result"/> is null.</summary>
    public void ShowResult(QueryResult? result, string accentHex)
    {
        SaveViewState();
        HideCellPreview();

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
        AccentRule.Background = StudioColors.Brush(accentHex);
        AccentRule.Visibility = Visibility.Visible;

        UpdateSelectionVisuals();
        RestoreViewState(state, result);
    }

    private void Clear()
    {
        HideCellPreview();
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
                Padding = new Thickness(12, 0, 12, 0),
            };

            surface.Child = new TextBlock
            {
                Text = column.Name,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 80,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = column.Numeric ? TextAlignment.Right : TextAlignment.Left,
                Foreground = (Brush)Application.Current.Resources["StudioTextMutedBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };

            var headerIndex = index;
            surface.PointerPressed += (_, args) =>
            {
                SelectColumn(headerIndex, IsShiftDown());
                args.Handled = true;
            };

            container.Children.Add(surface);

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
            case VirtualKey.Space:
                ToggleCellPreview();
                e.Handled = true;
                break;
            case VirtualKey.Escape when _previewOpen:
                HideCellPreview();
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
    /// Space (or the context menu) opens the focused cell's full value beside the grid. Cells are
    /// ellipsized, so this is the only way to read a long value without copying it.
    /// </summary>
    private void ToggleCellPreview()
    {
        if (_layout is null || !_hasSelection || _rows.Count == 0)
        {
            return;
        }

        if (_previewOpen && _previewRow == _focusRow && _previewColumn == _focusColumn)
        {
            HideCellPreview();
            return;
        }

        ShowCellPreview(_focusRow, _focusColumn);
    }

    private void ShowCellPreview(int row, int column)
    {
        if (_layout is null || row < 0 || row >= _rows.Count || column < 0 || column >= _layout.Columns.Count)
        {
            return;
        }

        _previewOpen = true;
        _previewRow = row;
        _previewColumn = column;
        PreviewHost.Visibility = Visibility.Visible;

        var layout = _layout.Columns[column];
        var value = column < _rows[row].Cells.Count ? _rows[row].Cells[column] : null;
        var muted = (Brush)Application.Current.Resources["StudioTextMutedBrush"];
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
        CellPreviewValue.Foreground = (Brush)Application.Current.Resources["StudioTextPrimaryBrush"];

        var footer = formatted.Text.Length > PreviewCharacterLimit
            ? $"Showing first {PreviewCharacterLimit:N0} of {formatted.Text.Length:N0} characters — Copy copies all"
            : $"{formatted.Text.Length:N0} characters";

        if (formatted.Error is { } error)
        {
            footer = $"{footer} · {error}";
        }

        CellPreviewFooter.Text = footer;
    }

    private void HideCellPreview()
    {
        if (!_previewOpen)
        {
            return;
        }

        _previewOpen = false;
        _previewRow = -1;
        _previewColumn = -1;
        _previewCopyText = string.Empty;
        PreviewHost.Visibility = Visibility.Collapsed;
        CellPreviewValue.Text = string.Empty;
        CellPreviewFooter.Text = string.Empty;
    }

    /// <summary>Keeps the panel in step with the focused cell while the user arrows around.</summary>
    private void RefreshPreviewIfOpen()
    {
        if (_previewOpen)
        {
            ShowCellPreview(_focusRow, _focusColumn);
        }
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
        HideCellPreview();
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
        if (_listScroller is not null)
        {
            return;
        }

        _listScroller = FindDescendant<ScrollViewer>(RowsList);
        if (_listScroller is not null)
        {
            _listScroller.ViewChanged += RowsList_ViewChanged;
        }
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
    }

    private static Brush BrushOr(string key, Brush fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : fallback;

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
