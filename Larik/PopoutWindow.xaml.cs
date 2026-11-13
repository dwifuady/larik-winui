using System.ComponentModel;
using System.Runtime.InteropServices;
using Larik.Editor;
using Larik.Services;
using Larik.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Larik;

/// <summary>
/// A query tab in its own window. It renders the same <see cref="TabViewModel"/> the shell keeps,
/// so the tab's session, results and persisted SQL are shared rather than copied; the main window
/// shows a placeholder for that tab while this window owns the visible editor and grid.
/// </summary>
public sealed partial class PopoutWindow : Window
{
    private readonly StudioCompletionSource _completionSource;
    private bool _pinned = true;
    private bool _editorCollapsed;
    private bool _updatingContent;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public PopoutWindow(StudioViewModel viewModel, TabViewModel tab)
    {
        ViewModel = viewModel;
        Tab = tab;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ThemeService.Attach(this);
        ThemeService.Changed += ThemeService_Changed;

        if (!MicaController.IsSupported())
        {
            Root.Background = ThemeService.GetBrush("StudioBackgroundBrush", Root.ActualTheme, StudioColors.Muted);
        }

        // Match the shell in the taskbar and Alt+Tab; a relative path only works when the process
        // starts in its own folder, which the portable build does not guarantee.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        _completionSource = new StudioCompletionSource(
            () => ViewModel.SnippetsFor(Tab),
            () => ViewModel.TablesFor(Tab),
            () => ViewModel.RoutinesFor(Tab),
            () => Tab.Space.Databases.Select(database => database.Name).ToList());

        // The surface owns its visuals and keys; this window only supplies the language, the
        // completion source and the verbs the tab can execute.
        Editor.CompletionSource = _completionSource;
        Editor.EditorLanguage = Tab.Space.Connection.EditorLanguage;
        Editor.Text = Tab.Content;
        Editor.TextChanged += Editor_TextChanged;
        Editor.CommandRequested += Editor_CommandRequested;

        ResultsGrid.OwnerWindow = this;
        ResultsGrid.PreviewWidthChanged += (_, width) => ViewModel.SetCellPreviewWidth(width);
        ResultsGrid.PreviewWidth = ViewModel.CellPreviewWidth ?? 340;

        Tab.PropertyChanged += Tab_PropertyChanged;
        Tab.Results.CollectionChanged += Results_CollectionChanged;
        Tab.Space.Connection.PropertyChanged += Connection_PropertyChanged;

        Closed += OnClosed;

        ApplyDefaultGeometry();

        UpdateTitle();
        UpdateRunState();
        UpdateStatus();
        UpdateTransaction();
        UpdateResults();

        // Completion reads the cache; warming it for this tab keeps a pop-out of another workspace
        // from borrowing the active tab's schema.
        _ = ViewModel.EnsurePopoutSchemaAsync(Tab);
    }

    public StudioViewModel ViewModel { get; }

    public TabViewModel Tab { get; }

    // ---------------------------------------------------------------- window chrome

    private void ApplyDefaultGeometry()
    {
        try
        {
            var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
            if (scale <= 0)
            {
                scale = 1;
            }

            var width = (int)(820 * scale);
            var height = (int)(620 * scale);
            AppWindow.Resize(new SizeInt32(width, height));

            // Open over the main window rather than at the screen origin, nudged so both stay usable.
            if (App.Window is { } main)
            {
                var origin = main.AppWindow.Position;
                var mainSize = main.AppWindow.Size;
                var x = origin.X + Math.Max(40, (mainSize.Width - width) / 2);
                var y = origin.Y + Math.Max(40, (mainSize.Height - height) / 2);
                AppWindow.Move(new PointInt32(x, y));
            }
        }
        catch (Exception ex)
        {
            // Geometry is cosmetic; a bad rectangle must not stop the window from opening.
            AppLog.Warning("popout", "could not size the pop-out window", ex);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _pinned;
        }

        UpdatePinChrome();
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        _pinned = !_pinned;
        presenter.IsAlwaysOnTop = _pinned;
        UpdatePinChrome();
    }

    private void UpdatePinChrome()
    {
        PinIcon.Opacity = _pinned ? 1 : 0.45;
        ToolTipService.SetToolTip(
            PinButton,
            _pinned ? "Always on top. Click to unpin" : "Pin on top of other windows");
    }

    private void DockButton_Click(object sender, RoutedEventArgs e) => Close();

    private void DockAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    // ---------------------------------------------------------------- editor

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        // The tab's own content is the source of truth; writing it back keeps saving and docking
        // exact, and the guard stops an external refresh from fighting the caret.
        _updatingContent = true;
        try
        {
            Tab.Content = Editor.Text;
        }
        finally
        {
            _updatingContent = false;
        }

        ApplyCollapsedQuery();
    }

    private void Editor_CommandRequested(object? sender, EditorCommandKind command)
    {
        switch (command)
        {
            case EditorCommandKind.RunStatement:
                _ = RunAsync(all: false);
                break;
            case EditorCommandKind.RunAll:
                _ = RunAsync(all: true);
                break;
            case EditorCommandKind.RunNewResult:
                _ = RunAsync(all: false, append: true);
                break;
            case EditorCommandKind.ToggleResults:
                SetEditorCollapsed(!_editorCollapsed);
                break;

            // Tabs, the rail and the schema browser belong to the shell; a pop-out owns one tab.
            case EditorCommandKind.NewTab:
            case EditorCommandKind.NextTab:
            case EditorCommandKind.PreviousTab:
            case EditorCommandKind.ToggleSidebar:
            case EditorCommandKind.BrowseSchema:
            default:
                break;
        }
    }

    private async Task RunAsync(bool all, bool append = false)
    {
        var batches = QueryRunner.ResolveBatches(Editor, all);
        await ViewModel.RunBatchesAsync(Tab, batches, append);
    }

    private void RunButton_Click(object sender, RoutedEventArgs e) => _ = RunAsync(all: false);

    private void StopButton_Click(object sender, RoutedEventArgs e) => ViewModel.CancelRun(Tab);

    private void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        var formatted = SqlFormatter.Format(Tab.Content);
        Editor.ReplaceAll(formatted);
        Tab.Content = formatted;
        ViewModel.StatusText = "Formatted";
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => SetEditorCollapsed(!_editorCollapsed);

    private void ToggleEditorAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetEditorCollapsed(!_editorCollapsed);
    }

    private void SetEditorCollapsed(bool collapsed)
    {
        _editorCollapsed = collapsed;
        ApplyEditorLayout();

        if (!collapsed)
        {
            Editor.FocusEditor();
        }
    }

    private void RunStatementAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = RunAsync(all: false);
    }

    private void RunAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = RunAsync(all: true);
    }

    // ---------------------------------------------------------------- state

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.IsRunning):
            case nameof(TabViewModel.CanRun):
                UpdateRunState();
                break;
            case nameof(TabViewModel.StatusText):
                UpdateStatus();
                break;
            case nameof(TabViewModel.HasOpenTransaction):
            case nameof(TabViewModel.CanCommit):
            case nameof(TabViewModel.CanRollback):
            case nameof(TabViewModel.TransactionSummary):
            case nameof(TabViewModel.TransactionBrush):
                UpdateTransaction();
                break;
            case nameof(TabViewModel.ActiveResult):
                UpdateResults();
                break;
            case nameof(TabViewModel.Content) when !_updatingContent:
                if (!string.Equals(Editor.Text, Tab.Content, StringComparison.Ordinal))
                {
                    Editor.Text = Tab.Content;
                    ApplyCollapsedQuery();
                }

                break;
            case nameof(TabViewModel.Title):
            case nameof(TabViewModel.Database):
                UpdateTitle();
                if (e.PropertyName == nameof(TabViewModel.Database))
                {
                    // A USE changed the tab's database; completion for the new one is loaded lazily.
                    _ = ViewModel.EnsurePopoutSchemaAsync(Tab);
                }

                break;
        }
    }

    private void Connection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.EditorLanguage))
        {
            Editor.EditorLanguage = Tab.Space.Connection.EditorLanguage;
        }
    }

    private void Results_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateResults();

    private void UpdateTitle()
    {
        var database = Tab.Database.Trim();
        AppTitleBar.Title = Tab.Title;
        AppTitleBar.Subtitle = database.Length == 0 ? Tab.Space.Name : $"{Tab.Space.Name} · {database}";
        Title = database.Length == 0
            ? $"{Tab.Title} - Larik"
            : $"{Tab.Title} - {database} - Larik";
        AppWindow.Title = Title;
    }

    private void UpdateRunState()
    {
        var running = Tab.IsRunning;
        RunButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        RunButton.IsEnabled = Tab.CanRun;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunProgressRing.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunProgressRing.IsActive = running;
    }

    private void UpdateStatus() => StatusTextBlock.Text = Tab.StatusText;

    private void UpdateTransaction()
    {
        TransactionPanel.Visibility = Tab.HasOpenTransaction ? Visibility.Visible : Visibility.Collapsed;
        if (!Tab.HasOpenTransaction)
        {
            return;
        }

        TransactionText.Text = Tab.TransactionSummary;
        TransactionText.Foreground = Tab.TransactionBrush;
        TransactionDot.Fill = Tab.TransactionBrush;
        CommitButton.IsEnabled = Tab.CanCommit;
        RollbackButton.IsEnabled = Tab.CanRollback;
    }

    private void UpdateResults()
    {
        var active = Tab.ActiveResult;

        ResultsGrid.ReferenceController = active is null
            ? null
            : ViewModel.CreateCellReferenceController(Tab, active.Result);
        ResultsGrid.ShowResult(active?.Result, Tab.Space.ColorHex);

        var count = Tab.Results.Count;
        var hasResult = count > 0;
        ResultsPane.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;
        ResultTabsList.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;

        // A single result has no tab strip, so the header carries its close action.
        ResultCloseButton.Visibility = count > 1 ? Visibility.Collapsed : Visibility.Visible;

        ResultMessageText.Text = active?.Message ?? string.Empty;
        ResultMessageText.Foreground = active?.Result.IsError == true
            ? ThemeService.GetBrush("StudioDangerBrush", Root.ActualTheme, StudioColors.Danger)
            : ThemeService.GetBrush("StudioTextMutedBrush", Root.ActualTheme, StudioColors.Muted);
        ResultSummaryText.Text = active?.Summary ?? string.Empty;

        ApplyEditorLayout();
    }

    private void ApplyEditorLayout()
    {
        var hasResult = Tab.Results.Count > 0;

        Editor.Visibility = _editorCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedQueryButton.Visibility = _editorCollapsed ? Visibility.Visible : Visibility.Collapsed;

        EditorRow.Height = _editorCollapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ResultsRow.Height = hasResult
            ? new GridLength(_editorCollapsed ? 1 : 0.9, GridUnitType.Star)
            : new GridLength(0);

        CollapseIcon.Glyph = _editorCollapsed ? "\uE70D" : "\uE70E";
        AutomationProperties.SetName(CollapseButton, _editorCollapsed ? "Show the editor" : "Hide the editor");
        ToolTipService.SetToolTip(
            CollapseButton,
            _editorCollapsed
                ? "Show the editor (Ctrl+R)"
                : "Hide the editor so the results fill the window (Ctrl+R)");

        ApplyCollapsedQuery();
    }

    private void ApplyCollapsedQuery() => CollapsedQueryText.Text = FirstLine(Tab.Content);

    /// <summary>First non-blank line, so the collapsed bar shows the query rather than whitespace.</summary>
    private static string FirstLine(string content) =>
        content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?
            .Trim() ?? "Empty query";

    // ---------------------------------------------------------------- results strip

    private void ResultTab_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultSetViewModel result)
        {
            ViewModel.SelectResult(Tab, result);
        }
    }

    private void ResultTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultSetViewModel result })
        {
            ViewModel.CloseResult(Tab, result);
        }
    }

    private void ResultHeaderClose_Click(object sender, RoutedEventArgs e)
    {
        if (Tab.ActiveResult is { } active)
        {
            ViewModel.CloseResult(Tab, active);
        }
    }

    private void ResultsGrid_StatusChanged(object? sender, string message) => ViewModel.StatusText = message;

    private void CommitButton_Click(object sender, RoutedEventArgs e) => _ = ViewModel.CommitTransactionAsync(Tab);

    private void RollbackButton_Click(object sender, RoutedEventArgs e) => _ = ViewModel.RollbackTransactionAsync(Tab);

    private void ThemeService_Changed(object? sender, EventArgs e)
    {
        ThemeService.Attach(this);
        Tab.NotifyThemeChanged();
        foreach (var result in Tab.Results)
        {
            result.NotifyThemeChanged();
        }

        Root.Background = MicaController.IsSupported()
            ? Root.Background
            : ThemeService.GetBrush("StudioBackgroundBrush", Root.ActualTheme, StudioColors.Muted);
        UpdateTransaction();
        UpdateResults();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ThemeService.Changed -= ThemeService_Changed;
        Tab.PropertyChanged -= Tab_PropertyChanged;
        Tab.Results.CollectionChanged -= Results_CollectionChanged;
        Tab.Space.Connection.PropertyChanged -= Connection_PropertyChanged;
        Editor.TextChanged -= Editor_TextChanged;
        Editor.CommandRequested -= Editor_CommandRequested;
        Editor.CompletionSource = null;
        ResultsGrid.OwnerWindow = null;
    }
}
