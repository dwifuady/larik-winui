using System.ComponentModel;
using Larik.Data;
using Larik.Data.Addons;
using Larik.Editor;
using Larik.Models;
using Larik.Services;
using Larik.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace Larik;

/// <summary>
/// The studio shell. View state lives in <see cref="StudioViewModel"/>; the editor is reached only
/// through <see cref="IEditorSurface"/>; the results grid is a view concern handled here.
/// </summary>
public sealed partial class MainPage : Page
{
    private bool _hooked;
    private readonly StudioCompletionSource _completionSource;
    private Storyboard? _spaceSlide;
    private int _lastSpaceIndex = -1;

    // Two-finger horizontal trackpad swipes over the rail move between workspaces.
    private const double SwipeThreshold = 150;
    private static readonly TimeSpan SwipeCooldown = TimeSpan.FromMilliseconds(350);
    private double _swipeDelta;
    private DateTime _lastWheelAt = DateTime.MinValue;
    private DateTime _lastSwipeAt = DateTime.MinValue;

    private bool _draggingSplitter;
    private double _splitStartY;
    private double _splitStartHeight;
    private double _splitTotalHeight;
    private double _splitResultsHeight;
    private bool _resultsVisible;
    private Storyboard? _resultsSlide;
    private const double DefaultResultsPaneHeight = 320;

    private bool _newTabPaletteOpen;
    private List<DatabaseViewModel> _paletteDatabases = [];

    private bool _schemaPinned;
    private readonly Dictionary<string, List<string>> _recentObjects = new(StringComparer.Ordinal);

    public StudioViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();

        ViewModel.DriverInstallRequested += ViewModel_DriverInstallRequested;

        // Settings is pushed onto the same frame; keep the shell (and its live session) alive.
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += OnLoaded;

        // Two-finger horizontal trackpad swipes over the rail move between workspaces. The
        // handler is registered as handledEventsToo so the tab list's scroller cannot swallow it.
        Sidebar.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(Sidebar_PointerWheelChanged),
            handledEventsToo: true);

        _completionSource = new StudioCompletionSource(
            () => ViewModel.Snippets,
            () => ViewModel.Tables,
            () => ViewModel.Routines,
            () =>
            {
                var space = ViewModel.ActiveSpace;
                return space is null
                    ? []
                    : space.Databases.Select(database => database.Name).ToList();
            });

        // The shell is drawn on Mica: only real surfaces (cards, popup) paint over it.
        // Without Mica support the backdrop is blank, so fall back to the design's solid background.
        if (!MicaController.IsSupported())
        {
            Root.Background = ThemeBrush("StudioBackgroundBrush", StudioColors.Muted);
        }

        ActualThemeChanged += (_, _) => ApplyThemeResources();

        // One schema control serves both placements; the shell owns what insert and preview mean.
        SchemaPeek.CloseRequested += (_, _) => CloseSchemaPeek();
        SchemaPeek.PinRequested += (_, _) => PinSchemaBrowser(open: true);
        SchemaPeek.Activated += (_, selection) => InsertSchemaObject(selection, closePeek: true);
        SchemaPeek.PreviewRequested += (_, selection) => _ = PreviewSchemaObjectAsync(selection, closePeek: true);
        SchemaPeek.StatusRequested += (_, message) => ViewModel.StatusText = message;

        SchemaPinned.CloseRequested += (_, _) => PinSchemaBrowser(open: false);
        SchemaPinned.PinRequested += (_, _) => PinSchemaBrowser(open: false);
        SchemaPinned.Activated += (_, selection) => InsertSchemaObject(selection, closePeek: false);
        SchemaPinned.PreviewRequested += (_, selection) => _ = PreviewSchemaObjectAsync(selection, closePeek: false);
        SchemaPinned.StatusRequested += (_, message) => ViewModel.StatusText = message;

        ResultsGrid.PreviewWidthChanged += (_, width) => ViewModel.SetCellPreviewWidth(width);
    }

    private void ApplyThemeResources()
    {
        if (!MicaController.IsSupported())
        {
            Root.Background = ThemeBrush("StudioBackgroundBrush", StudioColors.Muted);
        }

        ResultMessageText.Foreground = ViewModel.LastResult?.IsError == true
            ? ThemeBrush("StudioDangerBrush", StudioColors.Danger)
            : ThemeBrush("StudioTextMutedBrush", StudioColors.Muted);

        // The OS can change the system theme while the preference is still "System", which does
        // not raise ThemeService.Changed; CLR brush properties need this nudge either way.
        ViewModel.NotifyThemeChanged();
    }

    /// <summary>
    /// Resolves a design token against the page's live theme. Code-built surfaces (flyouts,
    /// dialogs, generated rows) must not read <c>Application.Current.Resources</c>: that lookup
    /// follows the startup theme, so after an in-app Light/Dark switch their text keeps the old
    /// theme's colour (near-white on a light flyout, for example).
    /// </summary>
    private Brush ThemeBrush(string key, Brush fallback) =>
        ThemeService.GetBrush(key, ActualTheme, fallback);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;

            // The shell only ever talks to the surface through the interface.
            SqlEditor.CompletionSource = _completionSource;
            SqlEditor.TextChanged += SqlEditor_TextChanged;
            SqlEditor.CaretChanged += SqlEditor_CaretChanged;
            SqlEditor.CommandRequested += SqlEditor_CommandRequested;

            ViewModel.ActiveTabChanged += (_, _) => SyncEditorFromActiveTab();
            ViewModel.FormatRequested += (_, _) => FormatActiveTab();
            ViewModel.ResultChanged += (_, _) => RenderResults();
            ViewModel.NewTabRequested += ViewModel_NewTabRequested;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;

            // An agent can hand a statement over as a tab. The pipe service marshals this to the UI
            // thread, and returning null means it opened.
            McpApprovalService.SetOpenHandler(request =>
                ViewModel.OpenExternalQuery(request.SpaceId, request.Database, request.Statement)
                    ? null
                    : "That workspace is no longer open in Larik.");
            _lastSpaceIndex = ActiveSpaceIndex();
            ViewModel.StartBackgroundMaintenance();
        }

        // The caption lives inside the content column (the rail runs full height), so the page owns the drag region.
        App.Window.SetTitleBar(AppTitleBar);

        ResultsGrid.PreviewWidth = ViewModel.CellPreviewWidth ?? 340;
        SyncEditorFromActiveTab();
        RenderResults();
        SqlEditor.FocusEditor();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        ViewModel.ToggleSidebarCommand.Execute(null);

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SettingsPage), ViewModel);

    // ---------------------------------------------------------------- schema browser

    private void SchemaAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenSchemaPeek();
    }

    private void ToggleResultsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleResultsPane();
    }

    private void BrowseSchema_Click(object sender, RoutedEventArgs e) => OpenSchemaPeek();

    private void OpenSchemaPeek()
    {
        if (SchemaPeekHost.Visibility == Visibility.Visible)
        {
            CloseSchemaPeek();
            return;
        }

        if (ViewModel.ActiveSpace is not { IsConnected: true })
        {
            ViewModel.StatusText = "Connect the workspace to browse its schema";
            return;
        }

        RefreshSchemaBrowsers();
        SchemaPeek.ResetSearch();
        SchemaPeekHost.Visibility = Visibility.Visible;
        SchemaPeek.FocusSearch();
    }

    private void CloseSchemaPeek()
    {
        if (SchemaPeekHost.Visibility != Visibility.Visible)
        {
            return;
        }

        SchemaPeekHost.Visibility = Visibility.Collapsed;
        SqlEditor.FocusEditor();
    }

    private void SchemaPeekBackdrop_Tapped(object sender, TappedRoutedEventArgs e) => CloseSchemaPeek();

    /// <summary>Re-feeds both placements from the active tab's schema.</summary>
    private void RefreshSchemaBrowsers()
    {
        var database = ViewModel.ActiveSpace?.ActiveDatabase?.Name ?? ViewModel.ActiveTab?.Database;
        var recents = RecentsForActiveSpace();
        SchemaPeek.Show(ViewModel.Tables, ViewModel.Routines, database, recents);

        if (_schemaPinned)
        {
            SchemaPinned.Show(ViewModel.Tables, ViewModel.Routines, database, recents);
        }
    }

    private void PinSchemaBrowser(bool open)
    {
        _schemaPinned = open;
        Root.ColumnDefinitions[1].Width = open ? new GridLength(300) : new GridLength(0);
        SchemaPinnedHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (open)
        {
            CloseSchemaPeek();
            RefreshSchemaBrowsers();
            SchemaPinned.FocusSearch();
            ViewModel.StatusText = "Schema pinned beside the editor";
        }
        else
        {
            ViewModel.StatusText = "Schema unpinned";
        }
    }

    private void InsertSchemaObject(SchemaSelection selection, bool closePeek)
    {
        string text;
        if (selection.Routine is { } routine)
        {
            text = RoutineInvocation(routine);
        }
        else if (selection.Table is { } table)
        {
            text = selection.Column ?? QualifiedTableName(table);
            RememberSchemaObject(table);
        }
        else
        {
            return;
        }

        SqlEditor.InsertText(text);
        ViewModel.StatusText = $"Inserted {text}";

        if (closePeek)
        {
            CloseSchemaPeek();
        }
        else if (_schemaPinned)
        {
            RefreshSchemaBrowsers();
        }
    }

    private async Task PreviewSchemaObjectAsync(SchemaSelection selection, bool closePeek)
    {
        if (selection.Table is not { } table)
        {
            return;
        }

        var name = QualifiedTableName(table);
        RememberSchemaObject(table);

        // Preview is a run too: like Ctrl+Enter, it must reveal the results pane if the user
        // hid it, otherwise the query succeeds with nowhere to see the rows.
        ShowResultsPane();

        if (closePeek)
        {
            CloseSchemaPeek();
        }
        else if (_schemaPinned)
        {
            RefreshSchemaBrowsers();
        }

        ViewModel.StatusText = $"Previewing {name} · top 100";
        var statement = ViewModel.ActiveLanguage is ISqlDialect dialect
            ? dialect.PreviewStatement(table.Schema, table.Name, 100)
            : $"SELECT * FROM {name}";
        await ViewModel.RunSqlAsync($"{statement};", append: true);
    }

    /// <summary>A complete procedure call in the active dialect, parameters filled with NULL.</summary>
    private string RoutineInvocation(RoutineSchema routine)
    {
        if (ViewModel.ActiveLanguage is ISqlDialect dialect)
        {
            return dialect.RoutineInvocation(routine.Schema, routine.Name, routine.Parameters, routine.Kind);
        }

        return string.IsNullOrWhiteSpace(routine.Schema)
            ? routine.Name
            : $"{routine.Schema}.{routine.Name}";
    }

    /// <summary>The name inserted into the editor, quoted the way the active dialect requires.</summary>
    private string QualifiedTableName(TableSchema table)
    {
        var dialect = ViewModel.ActiveLanguage as ISqlDialect;
        string Quote(string value) => dialect?.QuoteIdentifier(value) ?? value;

        return string.IsNullOrWhiteSpace(table.Schema)
            ? Quote(table.Name)
            : $"{Quote(table.Schema)}.{Quote(table.Name)}";
    }

    private List<string> RecentsForActiveSpace()
    {
        var id = ViewModel.ActiveSpace?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return [];
        }

        if (!_recentObjects.TryGetValue(id, out var list))
        {
            list = [];
            _recentObjects[id] = list;
        }

        return list;
    }

    private void RememberSchemaObject(TableSchema table)
    {
        var list = RecentsForActiveSpace();
        var key = SchemaKeys.For(table);
        list.Remove(key);
        list.Insert(0, key);

        while (list.Count > 5)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    // ---------------------------------------------------------------- workspace switching

    private int ActiveSpaceIndex() =>
        ViewModel.ActiveSpace is null ? -1 : ViewModel.Spaces.IndexOf(ViewModel.ActiveSpace);

    /// <summary>
    /// The query content slides in from the side the new workspace lives on, so the dots at the
    /// bottom (and the switcher order) read as a left-to-right sequence.
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StudioViewModel.Tables) or nameof(StudioViewModel.Routines))
        {
            if (SchemaPeekHost.Visibility == Visibility.Visible || _schemaPinned)
            {
                RefreshSchemaBrowsers();
            }

            return;
        }

        if (e.PropertyName == nameof(StudioViewModel.ActiveLanguage))
        {
            SqlEditor.EditorLanguage = ViewModel.ActiveLanguage;
            return;
        }

        if (e.PropertyName == nameof(StudioViewModel.HasSpaces) && !ViewModel.HasSpaces)
        {
            // The welcome surface replaces the editor; a result pane left open would sit behind it.
            HideResultsPane();
        }

        if (e.PropertyName != nameof(StudioViewModel.ActiveSpace))
        {
            return;
        }

        var index = ActiveSpaceIndex();
        if (_hooked && _lastSpaceIndex >= 0 && index >= 0 && index != _lastSpaceIndex)
        {
            SlideQueryContent(index > _lastSpaceIndex ? 1 : -1);
        }

        _lastSpaceIndex = index;

        // The rail changed workspace: the peek belongs to the tab you left, the pin follows along.
        if (SchemaPeekHost.Visibility == Visibility.Visible)
        {
            CloseSchemaPeek();
        }

        if (_schemaPinned)
        {
            RefreshSchemaBrowsers();
        }
    }

    private void SlideQueryContent(int direction)
    {
        if (!new UISettings().AnimationsEnabled)
        {
            return;
        }

        _spaceSlide?.Stop();

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var offset = direction > 0 ? 32 : -32;
        var board = new Storyboard();

        foreach (FrameworkElement pane in new FrameworkElement[] { QueryToolbar, SqlEditor, Splitter, ResultsPane })
        {
            if (pane.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                pane.RenderTransform = transform;
            }

            transform.X = offset;
            pane.Opacity = 0.7;

            var slide = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "X");

            var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(fade, pane);
            Storyboard.SetTargetProperty(fade, "Opacity");

            board.Children.Add(slide);
            board.Children.Add(fade);
        }

        _spaceSlide = board;
        board.Begin();
    }

    /// <summary>
    /// A two-finger horizontal swipe over the rail moves one workspace in the swipe direction.
    /// Deltas are accumulated because precision touchpads send a stream of small values; one
    /// gesture switches once (cooldown), and the ends do not wrap.
    /// </summary>
    private void Sidebar_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var wheel = e.GetCurrentPoint(Sidebar).Properties;
        if (!wheel.IsHorizontalMouseWheel)
        {
            return;
        }

        e.Handled = true;

        var now = DateTime.UtcNow;
        if (now - _lastSwipeAt < SwipeCooldown)
        {
            return;
        }

        if (now - _lastWheelAt > TimeSpan.FromMilliseconds(300))
        {
            _swipeDelta = 0;
        }

        _lastWheelAt = now;

        var delta = wheel.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        if (_swipeDelta != 0 && Math.Sign(delta) != Math.Sign(_swipeDelta))
        {
            _swipeDelta = 0;
        }

        _swipeDelta += delta;
        if (Math.Abs(_swipeDelta) < SwipeThreshold)
        {
            return;
        }

        // Positive horizontal wheel = scroll right = swipe left: reveal the workspace on the right.
        ViewModel.SelectRelativeSpace(_swipeDelta > 0 ? 1 : -1);
        _swipeDelta = 0;
        _lastSwipeAt = now;
    }

    // ---------------------------------------------------------------- editor surface

    private void SyncEditorFromActiveTab()
    {
        // Language first: the first highlight after a tab/space switch must use the right one.
        SqlEditor.EditorLanguage = ViewModel.ActiveLanguage;
        SqlEditor.Text = ViewModel.ActiveTab?.Content ?? string.Empty;
    }

    /// <summary>
    /// Formats the active tab's SQL through the editor surface, as one undoable edit. The surface
    /// raises TextChanged, which writes the new content back to the tab.
    /// </summary>
    private void FormatActiveTab()
    {
        if (ViewModel.ActiveTab is not { } tab)
        {
            return;
        }

        SqlEditor.ReplaceAll(SqlFormatter.Format(tab.Content));
        ViewModel.StatusText = "Formatted";
    }

    private void SqlEditor_TextChanged(object? sender, EventArgs e)
    {
        if (ViewModel.ActiveTab is not null)
        {
            ViewModel.ActiveTab.Content = SqlEditor.Text;
        }
    }

    private void SqlEditor_CaretChanged(object? sender, EventArgs e)
    {
        var caret = SqlEditor.Caret;
        ViewModel.SetCaret(caret.Line, caret.Column);
    }

    private void SqlEditor_CommandRequested(object? sender, EditorCommandKind command)
    {
        switch (command)
        {
            case EditorCommandKind.RunStatement:
                _ = RunStatementAsync();
                break;
            case EditorCommandKind.RunAll:
                _ = RunAllAsync();
                break;
            case EditorCommandKind.RunNewResult:
                _ = RunStatementAsync(append: true);
                break;
            case EditorCommandKind.NewTab:
                ViewModel.NewTabCommand.Execute(null);
                break;
            case EditorCommandKind.NextTab:
                ViewModel.SelectRelativeTab(1);
                break;
            case EditorCommandKind.PreviousTab:
                ViewModel.SelectRelativeTab(-1);
                break;
            case EditorCommandKind.ToggleSidebar:
                ViewModel.ToggleSidebarCommand.Execute(null);
                break;
            case EditorCommandKind.ToggleResults:
                ToggleResultsPane();
                break;
            case EditorCommandKind.BrowseSchema:
                OpenSchemaPeek();
                break;
        }
    }

    // ---------------------------------------------------------------- running SQL

    /// <summary>
    /// The shell owns the editor, so it is the one place that knows what "the query" means:
    /// an explicit selection always wins; otherwise Ctrl+Enter runs the statement under the
    /// caret (and selects it, so the run is visible), while F5 runs the whole document.
    /// </summary>
    private async Task RunFromEditorAsync(bool all, bool append)
    {
        var text = SqlEditor.Text;
        var selection = SqlEditor.Selection;

        ShowResultsPane();

        IReadOnlyList<string> batches;
        if (!selection.IsEmpty)
        {
            batches = SplitBatches(text.Substring(selection.Start, selection.Length));
        }
        else if (all)
        {
            // F5 may contain GO: SQL Server does not understand it (it is an SSMS/sqlcmd
            // separator), so ask the active language to split real server batches before sending.
            batches = SplitBatches(text);
        }
        else
        {
            var statement = SqlEditor.EditorLanguage.StatementAt(text, SqlEditor.CaretOffset);
            if (!statement.IsEmpty)
            {
                SqlEditor.Select(new EditorSpan(statement.Start, statement.Length));
            }

            batches = [text.Substring(statement.Start, statement.Length)];
        }

        await ViewModel.RunBatchesAsync(batches, append);
    }

    /// <summary>
    /// The provider's language owns batch boundaries (T-SQL splits on <c>GO</c>); the shell only
    /// turns the returned document spans into the strings the session sends.
    /// </summary>
    private IReadOnlyList<string> SplitBatches(string text)
    {
        var spans = SqlEditor.EditorLanguage.SplitBatches(text);
        if (spans.Count == 0)
        {
            return [];
        }

        var batches = new List<string>(spans.Count);
        foreach (var span in spans)
        {
            batches.Add(text.Substring(span.Start, span.Length));
        }

        return batches;
    }

    private Task RunStatementAsync(bool append = false) => RunFromEditorAsync(all: false, append);

    private Task RunAllAsync() => RunFromEditorAsync(all: true, append: false);

    private async void RunButton_Click(object sender, RoutedEventArgs e) => await RunStatementAsync();

    // ---------------------------------------------------------------- sidebar

    private void TabsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TabViewModel tab)
        {
            ViewModel.SelectTab(tab);
        }
    }

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabViewModel tab })
        {
            return;
        }

        await CloseTabWithConfirmationAsync(tab);
    }

    private async Task CloseTabWithConfirmationAsync(TabViewModel tab)
    {
        if (tab.HasOpenTransaction)
        {
            var consequence = tab.IsRunning
                ? "The running query will be stopped and the transaction rolled back. Nothing it changed will be saved."
                : "The transaction will be rolled back. Nothing it changed will be saved.";

            var dialog = new ContentDialog
            {
                Title = tab.TransactionSummary,
                XamlRoot = XamlRoot,
                Content = new TextBlock
                {
                    Text = $"Close “{tab.Title}”?\n\n{consequence}",
                    TextWrapping = TextWrapping.Wrap,
                },
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonText = "Roll back and close",
                CloseButtonText = "Cancel",
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ViewModel.CloseTabAsync(tab);
    }

    /// <summary>Right-click a tab for inline rename, pin and close; the menu is built per tab.</summary>
    private void TabItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement target || target.DataContext is not TabViewModel tab)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var rename = new MenuFlyoutItem
        {
            Text = "Rename",
            Icon = new FontIcon { Glyph = "\uE8AC" },
        };
        rename.Click += (_, _) => BeginInlineRename(tab, target);
        flyout.Items.Add(rename);

        // The existing menu gains the two group entries; grouping never needs a new toolbar.
        if (tab.Space.Groups.Count > 0 || tab.Group is not null)
        {
            var move = new MenuFlyoutSubItem
            {
                Text = "Move to group",
                Icon = new FontIcon { Glyph = "\uE8B7" },
            };

            foreach (var group in tab.Space.Groups.Where(candidate => !ReferenceEquals(candidate, tab.Group)))
            {
                var destination = new MenuFlyoutItem
                {
                    Text = group.Name,
                    Icon = new FontIcon { Glyph = group.IsPinned ? "\uE840" : "\uE8B7" },
                };
                destination.Click += (_, _) => ViewModel.MoveTabToGroup(tab, group);
                move.Items.Add(destination);
            }

            if (tab.Group is not null)
            {
                var loose = new MenuFlyoutItem
                {
                    Text = "Loose tabs",
                    Icon = new FontIcon { Glyph = "\uE8A9" },
                };
                loose.Click += (_, _) => ViewModel.MoveTabToGroup(tab, null);
                move.Items.Add(loose);
            }

            if (move.Items.Count > 0)
            {
                flyout.Items.Add(move);
            }
        }

        var newGroup = new MenuFlyoutItem
        {
            Text = "New group from tab",
            Icon = new FontIcon { Glyph = "\uE8B7" },
        };
        newGroup.Click += (_, _) => ViewModel.NewGroupFromTab(tab);

        if (tab.Group is null)
        {
            flyout.Items.Add(newGroup);
        }

        var pin = new MenuFlyoutItem
        {
            Text = tab.Group is { } tabGroup
                ? (tabGroup.IsPinned ? "Unpin group" : "Pin group")
                : tab.PinLabel,
            Icon = new FontIcon { Glyph = "\uE840" },
        };
        pin.Click += (_, _) => ViewModel.TogglePin(tab);
        flyout.Items.Add(pin);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var close = new MenuFlyoutItem
        {
            Text = "Close tab",
            Icon = new FontIcon { Glyph = "\uE711" },
        };
        close.Click += async (_, _) => await CloseTabWithConfirmationAsync(tab);
        flyout.Items.Add(close);

        if (args.TryGetPosition(target, out var position))
        {
            flyout.ShowAt(target, position);
        }
        else
        {
            flyout.ShowAt(target);
        }

        args.Handled = true;
    }

    /// <summary>Right-click a group: the same verbs as a tab, scoped to the set.</summary>
    private void GroupHeader_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement target || target.DataContext is not TabGroupViewModel group)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var rename = new MenuFlyoutItem
        {
            Text = "Rename group",
            Icon = new FontIcon { Glyph = "\uE8AC" },
        };
        rename.Click += async (_, _) => await RenameGroupAsync(group);
        flyout.Items.Add(rename);

        var collapse = new MenuFlyoutItem
        {
            Text = group.IsExpanded ? "Collapse group" : "Expand group",
            Icon = new FontIcon { Glyph = group.IsExpanded ? "\uE70D" : "\uE76C" },
        };
        collapse.Click += (_, _) => ViewModel.ToggleGroupExpanded(group);
        flyout.Items.Add(collapse);

        var pin = new MenuFlyoutItem
        {
            Text = group.PinLabel,
            Icon = new FontIcon { Glyph = "\uE840" },
        };
        pin.Click += (_, _) => ViewModel.ToggleGroupPin(group);
        flyout.Items.Add(pin);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var ungroup = new MenuFlyoutItem
        {
            Text = "Ungroup",
            Icon = new FontIcon { Glyph = "\uE8A9" },
        };
        ungroup.Click += (_, _) => ViewModel.Ungroup(group);
        flyout.Items.Add(ungroup);

        if (args.TryGetPosition(target, out var position))
        {
            flyout.ShowAt(target, position);
        }
        else
        {
            flyout.ShowAt(target);
        }

        args.Handled = true;
    }

    /// <summary>Group names are short; a dialog is less machinery than an inline editor in a header row.</summary>
    private async Task RenameGroupAsync(TabGroupViewModel group)
    {
        var box = new TextBox
        {
            Text = group.Name,
            SelectionStart = group.Name.Length,
            MinWidth = 280,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename group",
            Content = box,
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
        };

        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.RenameGroup(group, box.Text);
        }
    }

    /// <summary>Double-clicking a tab title is the quick path to renaming it in place.</summary>
    private void TabItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement container && container.DataContext is TabViewModel tab)
        {
            BeginInlineRename(tab, container);
            e.Handled = true;
        }
    }

    private void BeginInlineRename(TabViewModel tab, FrameworkElement container)
    {
        if (!tab.IsRenaming)
        {
            tab.BeginRename();
        }

        // The editor is only visible after the tab has re-rendered, so focus it next tick.
        DispatcherQueue.TryEnqueue(() =>
        {
            var editor = FindDescendant<TextBox>(container, "TabTitleEditor");
            if (editor is null)
            {
                return;
            }

            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        });
    }

    private void TabTitleEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not TabViewModel tab)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitInlineRename(tab, box.Text);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelInlineRename(tab);
            e.Handled = true;
        }
    }

    private void TabTitleEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box
            && box.DataContext is TabViewModel tab
            && tab.IsRenaming)
        {
            CommitInlineRename(tab, box.Text);
        }
    }

    private void CommitInlineRename(TabViewModel tab, string text)
    {
        if (!tab.IsRenaming)
        {
            return;
        }

        tab.EndRename();
        ViewModel.RenameTab(tab, text);
    }

    private void CancelInlineRename(TabViewModel tab)
    {
        if (!tab.IsRenaming)
        {
            return;
        }

        tab.EndRename();
        ViewModel.StatusText = "Rename cancelled";
    }

    private static T? FindDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (FindDescendant<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- archive / history

    private void ArchiveSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            ViewModel.ArchiveSearch = box.Text;
        }
    }

    private void ArchiveRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArchivedTabViewModel archived })
        {
            ViewModel.RestoreArchived(archived);
        }
    }

    private void ArchiveDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArchivedTabViewModel archived })
        {
            ViewModel.DeleteArchived(archived);
        }
    }

    private async void ArchiveClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasArchivedTabs)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Clear archive?",
            XamlRoot = XamlRoot,
            Content = new TextBlock
            {
                Text = $"Delete all {ViewModel.ArchivedTabCount} archived tab{(ViewModel.ArchivedTabCount == 1 ? string.Empty : "s")}? This cannot be undone.",
                TextWrapping = TextWrapping.Wrap,
            },
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonText = "Delete all",
            CloseButtonText = "Cancel",
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ClearArchive();
        }
    }

    private void TabItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            tab.IsPointerOver = true;
        }
    }

    private void TabItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            tab.IsPointerOver = false;
        }
    }

    /// <summary>The ListView already reordered the rail mirror; persist the same order on the space.</summary>
    private void TabsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.SyncRailOrder(pinnedBand: false);

    /// <summary>Pinned and normal sections share the same model; only the band differs.</summary>
    private void PinnedTabsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.SyncRailOrder(pinnedBand: true);

    /// <summary>
    /// Headers are not draggable. A tab crossing one during a reorder is how it joins that group,
    /// but dragging the header itself would detach the rows from their names.
    /// </summary>
    private void RailList_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (args.Items.Any(item => item is TabGroupViewModel))
        {
            args.Cancel = true;
        }
    }

    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabGroupViewModel group })
        {
            ViewModel.ToggleGroupExpanded(group);
        }
    }

    private void GroupByDatabase_Click(object sender, RoutedEventArgs e) =>
        ViewModel.GroupByDatabaseCommand.Execute(null);

    /// <summary>The icons at the bottom of the rail switch workspaces, like Arc's space indicator.</summary>
    private void WorkspaceIndicator_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpaceViewModel space })
        {
            ViewModel.SelectSpace(space);
        }
    }

    /// <summary>Above six workspaces the footer is a pill; its icon opens the same list as the switcher.</summary>
    private void WorkspacePill_Click(object sender, RoutedEventArgs e) =>
        ShowWorkspaceFlyout((FrameworkElement)sender);

    /// <summary>One step along the workspace order. The pill's chevrons never wrap.</summary>
    private void WorkspacePrevious_Click(object sender, RoutedEventArgs e) => ViewModel.SelectRelativeSpace(-1);

    private void WorkspaceNext_Click(object sender, RoutedEventArgs e) => ViewModel.SelectRelativeSpace(1);

    /// <summary>
    /// The workspace panel. Its one job is switching: the active connection, then the workspace
    /// list, then creation and the manager. It opens from the toolbar badge while the rail is
    /// collapsed, which is why the connection action is repeated here.
    /// </summary>
    private void SpaceSwitcher_Click(object sender, RoutedEventArgs e) => ShowWorkspaceFlyout(SpaceSwitcherButton);

    private void ContextBadge_Click(object sender, RoutedEventArgs e) => ShowWorkspaceFlyout(ContextBadgeButton);

    private void ShowWorkspaceFlyout(FrameworkElement anchor)
    {
        var mutedBrush = ThemeBrush("StudioTextMutedBrush", StudioColors.Muted);

        // A fixed width, not a minimum: the switcher must not grow just because one row (the
        // endpoint) is long. Every row measures inside this width instead.
        var panel = new StackPanel { Width = 288, Spacing = 2 };

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = true,
        };

        if (ViewModel.ActiveSpace is { } active)
        {
            // The host detail the rail trims away; it says what Connect would connect to. The
            // fixed panel width trims it; the full value is in the tooltip.
            var statusLine = new TextBlock
            {
                Margin = new Thickness(12, 8, 12, 6),
                FontSize = 10,
                Foreground = mutedBrush,
                Text = active.Connection.StatusLine,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(statusLine, active.Connection.StatusLine);
            panel.Children.Add(statusLine);

            // The one action for the line above it. Connection settings live on the Workspaces
            // page, so this panel never grows a second editor.
            panel.Children.Add(CreateFlyoutAction(
                "\uE7E8",
                active.Connection.ConnectionActionLabel,
                async () =>
                {
                    flyout.Hide();
                    await ViewModel.ToggleConnectionCommand.ExecuteAsync(null);
                }));
        }

        panel.Children.Add(CreateFlyoutSeparator());
        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(12, 6, 12, 2),
            CharacterSpacing = 120,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = mutedBrush,
            Text = "WORKSPACES",
        });

        foreach (var space in ViewModel.RailSpaces)
        {
            panel.Children.Add(CreateWorkspaceRow(space, flyout));
        }

        if (ViewModel.HiddenSpaceCount > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(12, 4, 12, 6),
                FontSize = 10,
                Foreground = mutedBrush,
                Text = $"{ViewModel.HiddenSpaceCountLabel} · manage on the Workspaces page",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(CreateFlyoutSeparator());
        panel.Children.Add(CreateFlyoutAction("\uE710", "New workspace", async () =>
        {
            flyout.Hide();
            await ShowWorkspaceDialogAsync();
        }));

        panel.Children.Add(CreateFlyoutAction("\uE8B7", "Manage workspaces…", () =>
        {
            flyout.Hide();
            NavigateToWorkspaces();
            return Task.CompletedTask;
        }));

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// The Workspaces page is a shell destination. It shares this page's view model and the one
    /// workspace editor, so editing from the list opens the same dialog with the list's XamlRoot.
    /// </summary>
    private void NavigateToWorkspaces() =>
        Frame.Navigate(
            typeof(WorkspacesPage),
            new WorkspacesPageContext(ViewModel, (space, root) => ShowWorkspaceDialogAsync(space, root)));

    /// <summary>
    /// A workspace row: click to switch. The provider chip carries the fact the name cannot, and
    /// editing moved to the Workspaces page so this surface keeps one job.
    /// </summary>
    private FrameworkElement CreateWorkspaceRow(SpaceViewModel space, Flyout flyout)
    {
        var primaryBrush = ThemeBrush("StudioTextPrimaryBrush", StudioColors.Muted);
        var secondaryBrush = ThemeBrush("StudioTextSecondaryBrush", StudioColors.Muted);
        var accentBrush = ThemeBrush("StudioAccentBrush", StudioColors.Muted);
        var hairlineBrush = ThemeBrush("HairlineBrush", StudioColors.Muted);
        var iconStyle = (Style)Application.Current.Resources["StudioIconButtonStyle"];

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = CreateSpaceIcon(space.IconGlyph, space.Monogram, 14, space.IsActive ? primaryBrush : secondaryBrush);

        var name = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = space.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = space.IsActive ? primaryBrush : secondaryBrush,
            Text = space.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var provider = new Border
        {
            MaxWidth = 88,
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = hairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new TextBlock
            {
                FontSize = 9,
                Foreground = secondaryBrush,
                Text = space.ProviderName,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };

        var check = new FontIcon
        {
            FontSize = 10,
            Foreground = accentBrush,
            Glyph = "\uE73E",
            Visibility = space.IsActive ? Visibility.Visible : Visibility.Collapsed,
        };

        Grid.SetColumn(name, 1);
        Grid.SetColumn(provider, 2);
        Grid.SetColumn(check, 3);
        content.Children.Add(icon);
        content.Children.Add(name);
        content.Children.Add(provider);
        content.Children.Add(check);

        var switchButton = new Button
        {
            Height = 36,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
            Style = iconStyle,
        };

        AutomationProperties.SetName(switchButton, $"{space.Name}, {space.ProviderName}");
        ToolTipService.SetToolTip(switchButton, space.TooltipText);
        switchButton.Click += (_, _) =>
        {
            flyout.Hide();
            ViewModel.SelectSpace(space);
        };

        return new Border
        {
            Margin = new Thickness(4, 0, 4, 0),
            Background = space.IsActive ? space.ActiveTintBrush : null,
            Child = switchButton,
            CornerRadius = new CornerRadius(6),
        };
    }

    private Border CreateFlyoutSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 4, 8, 4),
        Background = ThemeBrush("HairlineBrush", StudioColors.Muted),
    };

    private Button CreateFlyoutAction(string glyph, string label, Func<Task> action)
    {
        var button = new Button
        {
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["StudioIconButtonStyle"],
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { FontSize = 11, Glyph = glyph },
                    new TextBlock { FontSize = 12, Text = label },
                },
            },
        };

        AutomationProperties.SetName(button, label.TrimEnd('…'));
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>
    /// A workspace's icon, or its first letter until one is picked, in the colour the surface needs.
    /// One helper so the rail footer, the workspace list and the dialog preview cannot drift apart.
    /// </summary>
    private static FrameworkElement CreateSpaceIcon(string glyph, string monogram, double size, Brush brush)
    {
        var hasIcon = glyph.Length > 0;
        var visual = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

        visual.Children.Add(new FontIcon
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = size - 1,
            Foreground = brush,
            Glyph = glyph,
            Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed,
        });

        visual.Children.Add(new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = Math.Round(size * 0.62),
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
            Text = monogram,
            Visibility = hasIcon ? Visibility.Collapsed : Visibility.Visible,
        });

        return visual;
    }

    /// <summary>
    /// The colour palette popup. The ring marks the current choice; the identity row's swatch shows
    /// the same colour without opening it.
    /// </summary>
    private StackPanel CreateColourSwatches(string selectedHex, Action<string> changed)
    {
        const double dotSize = 16;
        const double ringSize = dotSize + 8;
        var rings = new Dictionary<string, Ellipse>(StringComparer.OrdinalIgnoreCase);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var iconStyle = (Style)Application.Current.Resources["StudioIconButtonStyle"];
        var ringBrush = ThemeBrush("StudioTextPrimaryBrush", StudioColors.Muted);

        void Select(string hex)
        {
            foreach (var ring in rings)
            {
                ring.Value.Visibility = string.Equals(ring.Key, hex, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        for (var index = 0; index < StudioColors.Palette.Length; index++)
        {
            var hex = StudioColors.Palette[index];
            var ring = new Ellipse
            {
                Width = ringSize,
                Height = ringSize,
                Stroke = ringBrush,
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
            };

            var dot = new Ellipse
            {
                Width = dotSize,
                Height = dotSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = StudioColors.Brush(hex),
            };

            var swatchGrid = new Grid { Width = ringSize, Height = ringSize };
            swatchGrid.Children.Add(ring);
            swatchGrid.Children.Add(dot);

            var swatch = new Button
            {
                Width = ringSize + 4,
                Height = ringSize + 4,
                Padding = new Thickness(0),
                Content = swatchGrid,
                Style = iconStyle,
            };

            var paletteName = StudioColors.PaletteNames[index];
            AutomationProperties.SetName(swatch, $"Colour {paletteName}");
            ToolTipService.SetToolTip(swatch, paletteName);
            swatch.Click += (_, _) =>
            {
                Select(hex);
                changed(hex);
            };

            rings[hex] = ring;
            swatches.Children.Add(swatch);
        }

        Select(selectedHex);
        return swatches;
    }

    private async void DatabaseList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DatabaseViewModel database)
        {
            return;
        }

        await ViewModel.SelectDatabaseAsync(database);
        DatabaseSelectorButton.Flyout?.Hide();

        // Choosing a database is a step towards writing SQL, not the destination: put the caret
        // back in the editor so the next keystroke goes where the user expects.
        DispatcherQueue.TryEnqueue(() => SqlEditor.FocusEditor());
    }

    // ---------------------------------------------------------------- workspace settings

    /// <summary>Onboarding's one action: the same workspace dialog the switcher opens.</summary>
    private async void NewWorkspace_Click(object sender, RoutedEventArgs e) =>
        await ShowWorkspaceDialogAsync();

    /// <summary>
    /// Installs a driver for a workspace whose provider is missing. No other dialog is open here,
    /// so a small modal is enough; the workspace refreshes in place after it closes.
    /// </summary>
    private async Task<string?> ShowInstallDriverDialogAsync(string? providerId)
    {
        AddonIndex index;
        try
        {
            index = await AddonCatalog.CreateIndexClient().FetchAsync(AddonCatalog.IndexLocation);
        }
        catch (Exception ex)
        {
            await ShowDriverMessageAsync("Couldn't reach the driver catalogue", ex.Message);
            return null;
        }

        var entries = index.Addons
            .Where(entry => entry.Matches(AddonCatalog.AppVersion, AddonCatalog.CurrentPlatform))
            .Where(entry => providerId is null ||
                entry.ProviderIds.Contains(providerId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count == 0)
        {
            await ShowDriverMessageAsync(
                providerId is null ? "No drivers available" : "Driver not found",
                providerId is null
                    ? "The catalogue has no drivers for this version of Larik."
                    : $"The catalogue has no installable driver for '{providerId}'.");
            return null;
        }

        var muted = ThemeBrush("StudioTextMutedBrush", StudioColors.Muted);
        var danger = ThemeBrush("StudioDangerBrush", StudioColors.Danger);

        var picker = new ComboBox
        {
            ItemsSource = entries.Select(entry => $"{entry.Name} {entry.Version}").ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(picker, "Driver to install");

        var summary = new TextBlock
        {
            Text = entries[0].Summary,
            FontSize = 11,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
        };

        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
        };

        var progress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };

        picker.SelectionChanged += (_, _) =>
        {
            var position = Math.Clamp(picker.SelectedIndex, 0, entries.Count - 1);
            summary.Text = entries[position].Summary;
        };

        var body = new StackPanel { Spacing = 10, MinWidth = 340 };
        body.Children.Add(picker);
        body.Children.Add(summary);
        body.Children.Add(progress);
        body.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "Install database driver",
            XamlRoot = XamlRoot,
            Content = body,
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        string? installedId = null;

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var entry = entries[Math.Clamp(picker.SelectedIndex, 0, entries.Count - 1)];

                progress.IsActive = true;
                progress.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.CloseButtonText = string.Empty;
                status.Foreground = muted;
                status.Text = $"Installing {entry.Name} {entry.Version}…";

                await AddonCatalog.CreateInstaller().InstallAsync(entry, LarikPaths.PluginsDirectory);
                DriverRuntime.LoadInstalled(entry.Id);
                installedId = entry.Id;
                status.Text = $"{entry.Name} {entry.Version} installed.";
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                status.Foreground = danger;
                status.Text = $"Install failed: {ex.Message}";
                dialog.IsPrimaryButtonEnabled = true;
                dialog.CloseButtonText = "Cancel";
            }
            finally
            {
                progress.IsActive = false;
                progress.Visibility = Visibility.Collapsed;
                deferral.Complete();
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? installedId : null;
    }

    private async Task ShowDriverMessageAsync(string title, string message) =>
        await new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        }.ShowAsync();

    private async void ViewModel_DriverInstallRequested(
        object? sender,
        StudioViewModel.DriverInstallRequest request)
    {
        var installed = await ShowInstallDriverDialogAsync(request.ProviderId);
        if (installed is null)
        {
            return;
        }

        ViewModel.RefreshProviderAvailability();
    }

    /// <summary>
    /// The workspace editor. A workspace is its name, colour and single connection, so one dialog
    /// covers all three. Create mode (no <paramref name="editing"/>) starts from a ready name and
    /// colour and creates nothing until confirmed; edit mode is the same form, prefilled. It is
    /// opened from onboarding and from the Workspaces page, which lends its own XamlRoot so the
    /// dialog is shown over whichever surface is current.
    /// </summary>
    private async Task ShowWorkspaceDialogAsync(SpaceViewModel? editing = null, XamlRoot? ownerRoot = null)
    {
        var ownerXamlRoot = ownerRoot ?? XamlRoot;
        var creating = editing is null;
        var providers = DatabaseProviderCatalog.Providers.ToList();
        if (providers.Count == 0)
        {
            return;
        }

        if (editing is not null)
        {
            // Show the passwords the connection will actually use; empty boxes would save blank.
            ViewModel.PrepareConnectionEdit(editing);
        }

        var connection = editing?.Connection;
        var mutedBrush = ThemeBrush("StudioTextMutedBrush", StudioColors.Muted);
        var dangerBrush = ThemeBrush("StudioDangerBrush", StudioColors.Danger);
        var successBrush = ThemeBrush("StudioSuccessBrush", StudioColors.Success);

        var providerBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(providerBox, "Database");

        var providerId = connection?.ProviderId ?? DatabaseProviderCatalog.Default?.Id;

        // The picker is rebuilt after a driver is installed while this dialog is open, so the user
        // can choose the new engine without losing the workspace draft.
        void RefreshProviderChoices(string? selectId = null)
        {
            providers = DatabaseProviderCatalog.Providers.ToList();
            providerBox.ItemsSource = providers.Select(provider => provider.DisplayName).ToList();
            var selected = providers.FirstOrDefault(provider => provider.Id == selectId)
                ?? providers.FirstOrDefault();
            providerBox.SelectedIndex = selected is null ? -1 : providers.IndexOf(selected);
        }

        RefreshProviderChoices(providerId);

        var primaryFields = new StackPanel { Spacing = 10 };
        var advancedFields = new StackPanel { Spacing = 10 };
        var inputs = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = mutedBrush,
        };

        // A file field keeps the text box (the profile stores exactly what the provider reads) and
        // adds Browse… beside it. Open-only by design: creating a new database file is the path
        // field's job, so the dialog never has to guess which intent the picker should have.
        Grid CreateFileField(ProviderSetting setting, TextBox pathBox)
        {
            var browse = new Button
            {
                Content = "Browse…",
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(browse, $"Browse for {setting.Label}");
            ToolTipService.SetToolTip(browse, "Choose an existing file");

            browse.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker();
                foreach (var extension in setting.FileExtensions)
                {
                    var normalized = extension.StartsWith('.') ? extension : "." + extension;
                    if (!picker.FileTypeFilter.Contains(normalized))
                    {
                        picker.FileTypeFilter.Add(normalized);
                    }
                }

                // Engines that open a file do not always mandate an extension, so the declared
                // list must never be the only way to reach a valid file.
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    return;
                }

                if (string.IsNullOrEmpty(file.Path))
                {
                    status.Foreground = dangerBrush;
                    status.Text = "That file has no local path. Copy it to disk and try again.";
                    return;
                }

                pathBox.Text = file.Path;
                status.Text = string.Empty;
            };

            var host = new Grid { ColumnSpacing = 8 };
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            host.Children.Add(pathBox);
            Grid.SetColumn(browse, 1);
            host.Children.Add(browse);
            return host;
        }

        // Rare provider fields (port, encryption, timeouts) open in a focused popup instead of
        // expanding the form: the connection form keeps its shape and the fields are visible
        // immediately rather than below the fold.
        var advancedFieldsScroll = new ScrollViewer
        {
            Content = advancedFields,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
        };

        var advancedPanel = new StackPanel { Width = 360, Spacing = 10 };
        advancedPanel.Children.Add(new TextBlock
        {
            Text = "Advanced connection settings",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        });
        advancedPanel.Children.Add(advancedFieldsScroll);

        var advancedDone = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(advancedDone, "Done");
        advancedPanel.Children.Add(advancedDone);

        var advancedFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = true,
            Content = advancedPanel,
        };
        advancedDone.Click += (_, _) => advancedFlyout.Hide();

        var advancedButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 9, 12, 9),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", StudioColors.Muted),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", StudioColors.Muted),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Style = (Style)Application.Current.Resources["StudioIconButtonStyle"],
            Flyout = advancedFlyout,
            Visibility = Visibility.Collapsed,
        };

        var advancedContent = new Grid { ColumnSpacing = 8 };
        advancedContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advancedContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        advancedContent.Children.Add(new TextBlock
        {
            Text = "Advanced connection settings",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var advancedChevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 9,
            Foreground = mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(advancedChevron, 1);
        advancedContent.Children.Add(advancedChevron);
        advancedButton.Content = advancedContent;
        AutomationProperties.SetName(advancedButton, "Advanced connection settings");
        AutomationProperties.SetHelpText(advancedButton, "Opens the rare provider fields");
        ToolTipService.SetToolTip(advancedButton, "Advanced connection settings");

        IDatabaseProvider SelectedProvider() => providers[Math.Max(0, providerBox.SelectedIndex)];

        void RebuildFields()
        {
            primaryFields.Children.Clear();
            advancedFields.Children.Clear();
            inputs.Clear();

            foreach (var setting in SelectedProvider().Settings)
            {
                var value = connection?.Profile.Get(setting.Key)
                    ?? setting.DefaultValue
                    ?? (setting.Kind == ProviderSettingKind.Boolean ? "false" : string.Empty);

                FrameworkElement input = setting.Kind switch
                {
                    ProviderSettingKind.Password => new PasswordBox { Password = value },
                    ProviderSettingKind.Boolean => new ToggleSwitch
                    {
                        IsOn = bool.TryParse(value, out var on) && on,
                        OnContent = "On",
                        OffContent = "Off",
                    },
                    ProviderSettingKind.Number => new NumberBox
                    {
                        Value = double.TryParse(value, out var number) ? number : double.NaN,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                    },
                    _ => new TextBox { Text = value, PlaceholderText = setting.Placeholder ?? string.Empty },
                };

                AutomationProperties.SetName(input, setting.Label);
                inputs[setting.Key] = input;

                var label = new TextBlock { Text = setting.Label, FontSize = 11 };
                var group = new StackPanel { Spacing = 4 };
                group.Children.Add(label);
                group.Children.Add(setting.Kind == ProviderSettingKind.File && input is TextBox pathBox
                    ? CreateFileField(setting, pathBox)
                    : input);

                if (setting.Hint is { Length: > 0 } hint)
                {
                    group.Children.Add(new TextBlock
                    {
                        Text = hint,
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = mutedBrush,
                    });
                }

                (setting.IsAdvanced ? advancedFields : primaryFields).Children.Add(group);
            }

            advancedButton.Visibility = advancedFields.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        string ReadValue(ProviderSetting setting) => inputs[setting.Key] switch
        {
            PasswordBox password => password.Password,
            ToggleSwitch toggle => toggle.IsOn ? "true" : "false",
            NumberBox number => double.IsNaN(number.Value) ? string.Empty : number.Value.ToString("0"),
            TextBox text => text.Text,
            _ => string.Empty,
        };

        ConnectionProfile BuildProfile()
        {
            var provider = SelectedProvider();
            var profile = new ConnectionProfile { ProviderId = provider.Id };
            foreach (var setting in provider.Settings)
            {
                profile.Values[setting.Key] = ReadValue(setting);
            }

            return profile;
        }

        var providerGroup = new StackPanel { Spacing = 4 };
        providerGroup.Children.Add(new TextBlock { Text = "Database", FontSize = 11 });
        providerGroup.Children.Add(providerBox);

        // The picker only lists installed drivers; the rare "my engine is missing" path expands in
        // place, where the user notices it, instead of sending them to Settings and losing the draft.
        var installDriverLink = new HyperlinkButton
        {
            Content = "Install another database driver…",
            FontSize = 11,
            Padding = new Thickness(0),
        };
        AutomationProperties.SetName(installDriverLink, "Install another database driver");
        if (!AddonPolicy.AddonsEnabled)
        {
            // The Store edition ships its drivers in the package; there is no add-on store.
            installDriverLink.Visibility = Visibility.Collapsed;
        }

        providerGroup.Children.Add(installDriverLink);

        var installEntries = new List<AddonIndexEntry>();
        var installPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(installPicker, "Driver to install");

        var installSummary = new TextBlock
        {
            FontSize = 11,
            Foreground = mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var installButton = new Button
        {
            Content = "Install",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Visibility = Visibility.Collapsed,
        };

        var installProgress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };

        var installStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var installPanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        installPanel.Children.Add(installPicker);
        installPanel.Children.Add(installSummary);
        installPanel.Children.Add(installButton);
        installPanel.Children.Add(installProgress);
        installPanel.Children.Add(installStatus);
        providerGroup.Children.Add(installPanel);

        void ShowInstallStatus(string text, Brush brush)
        {
            installStatus.Text = text;
            installStatus.Foreground = brush;
            installStatus.Visibility = Visibility.Visible;
        }

        async Task LoadInstallChoicesAsync()
        {
            try
            {
                var index = await AddonCatalog.CreateIndexClient().FetchAsync(AddonCatalog.IndexLocation);
                installEntries.Clear();
                installEntries.AddRange(index.Addons.Where(entry =>
                    entry.Matches(AddonCatalog.AppVersion, AddonCatalog.CurrentPlatform)));
            }
            catch (Exception ex)
            {
                ShowInstallStatus($"Could not reach the driver catalogue: {ex.Message}", dangerBrush);
                return;
            }

            if (installEntries.Count == 0)
            {
                ShowInstallStatus("No drivers are available for this version of Larik.", mutedBrush);
                return;
            }

            installPicker.ItemsSource = installEntries
                .Select(entry => $"{entry.Name} {entry.Version}")
                .ToList();
            installPicker.SelectedIndex = 0;
            installSummary.Text = installEntries[0].Summary;
            installSummary.Visibility = Visibility.Visible;
            installStatus.Visibility = Visibility.Collapsed;
            installPicker.Visibility = Visibility.Visible;
            installButton.Visibility = Visibility.Visible;
        }

        installPicker.SelectionChanged += (_, _) =>
        {
            if (installEntries.Count == 0)
            {
                return;
            }

            var position = Math.Clamp(installPicker.SelectedIndex, 0, installEntries.Count - 1);
            installSummary.Text = installEntries[position].Summary;
        };

        installDriverLink.Click += async (_, _) =>
        {
            if (installPanel.Visibility == Visibility.Visible)
            {
                installPanel.Visibility = Visibility.Collapsed;
                return;
            }

            installPanel.Visibility = Visibility.Visible;
            if (installEntries.Count == 0)
            {
                await LoadInstallChoicesAsync();
            }
        };

        installButton.Click += async (_, _) =>
        {
            if (installEntries.Count == 0)
            {
                return;
            }

            var entry = installEntries[Math.Clamp(installPicker.SelectedIndex, 0, installEntries.Count - 1)];

            installButton.IsEnabled = false;
            installPicker.IsEnabled = false;
            installProgress.Visibility = Visibility.Visible;
            installProgress.IsActive = true;
            ShowInstallStatus($"Installing {entry.Name} {entry.Version}…", mutedBrush);

            try
            {
                await AddonCatalog.CreateInstaller().InstallAsync(entry, LarikPaths.PluginsDirectory);
                var added = DriverRuntime.LoadInstalled(entry.Id);
                var installedProviderId = added.FirstOrDefault()?.Id ?? entry.ProviderIds.FirstOrDefault();
                RefreshProviderChoices(installedProviderId);

                installPanel.Visibility = Visibility.Collapsed;
                status.Foreground = successBrush;
                status.Text = added.Count > 0
                    ? $"{entry.Name} is ready."
                    : $"{entry.Name} updated. Restart Larik to use it.";
            }
            catch (Exception ex)
            {
                ShowInstallStatus($"Install failed: {ex.Message}", dangerBrush);
            }
            finally
            {
                installProgress.IsActive = false;
                installProgress.Visibility = Visibility.Collapsed;
                installButton.IsEnabled = true;
                installPicker.IsEnabled = true;
            }
        };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(primaryFields);
        body.Children.Add(advancedButton);

        // The dialog's own scroller does not always engage when the form outgrows the window,
        // so the fields scroll in their own viewport and the status line stays in view.
        var fieldsScroll = new ScrollViewer
        {
            Content = body,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
        };

        var form = new Grid { MinWidth = 360, RowSpacing = 12 };
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(providerGroup, 0);
        Grid.SetRow(fieldsScroll, 1);
        Grid.SetRow(status, 2);
        form.Children.Add(providerGroup);
        form.Children.Add(fieldsScroll);
        form.Children.Add(status);

        if (DatabaseProviderCatalog.LoadErrors.Count > 0)
        {
            var loadErrors = new TextBlock
            {
                Text = "Provider load problems: " + string.Join("; ", DatabaseProviderCatalog.LoadErrors),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = dangerBrush,
            };

            Grid.SetRow(loadErrors, 3);
            form.Children.Add(loadErrors);
        }

        FrameworkElement content = form;
        var selectedColour = editing?.ColorHex
            ?? StudioColors.Palette[ViewModel.Spaces.Count % StudioColors.Palette.Length];
        var selectedIcon = editing?.IconName;
        var iconStyle = (Style)Application.Current.Resources["StudioIconButtonStyle"];
        var secondaryBrush = ThemeBrush("StudioTextSecondaryBrush", StudioColors.Muted);

        // Identity is one row: icon, name, colour. The icon is the only new decision, and leaving it
        // unanswered is valid — the first letter of the name stands in, so no workspace is a blank.
        var nameBox = new TextBox
        {
            Text = creating ? $"Space {ViewModel.Spaces.Count + 1}" : editing!.Name,
            PlaceholderText = "Workspace name",
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(nameBox, "Workspace name");

        var iconButton = new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(9),
            Style = iconStyle,
        };
        AutomationProperties.SetName(iconButton, "Workspace icon");

        var iconPicker = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = true,
        };

        Button? colourButton = null;

        void RefreshIcon()
        {
            // A caret in the corner, so the icon reads as a control that opens something.
            var content = new Grid();
            content.Children.Add(CreateSpaceIcon(
                SpaceIcons.Glyph(selectedIcon) ?? string.Empty,
                SpaceIcons.Monogram(nameBox.Text),
                20,
                secondaryBrush));
            content.Children.Add(new FontIcon
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                FontSize = 7,
                Glyph = "\uE70D",
                Foreground = secondaryBrush,
                Opacity = 0.7,
            });

            iconButton.Content = content;
            iconButton.Background = StudioColors.Tint(selectedColour);
            iconButton.BorderBrush = StudioColors.Brush(selectedColour);
            iconButton.BorderThickness = new Thickness(1);
            AutomationProperties.SetName(iconButton, $"Workspace icon, {SpaceIcons.Label(selectedIcon)}");
            AutomationProperties.SetHelpText(iconButton, "Opens the icon picker");
            ToolTipService.SetToolTip(iconButton, $"Change icon · {SpaceIcons.Label(selectedIcon)}");

            if (colourButton?.Content is Ellipse dot)
            {
                dot.Fill = StudioColors.Brush(selectedColour);
                var index = Array.IndexOf(StudioColors.Palette, selectedColour);
                var colourName = index >= 0 ? StudioColors.PaletteNames[index] : "Custom";
                AutomationProperties.SetName(colourButton, $"Workspace colour, {colourName}");
                AutomationProperties.SetHelpText(colourButton, "Opens the colour picker");
                ToolTipService.SetToolTip(colourButton, $"Workspace colour · {colourName}");
            }
        }

        // A shipped, named set rather than the whole Fluent font: a 15 px glyph is only readable as
        // itself if the shapes differ, and a search box over unnamed code points helps nobody.
        void ShowIconPicker()
        {
            var panel = new StackPanel { Width = 276, Spacing = 8 };
            var search = new TextBox { PlaceholderText = "Search icons", FontSize = 12 };
            AutomationProperties.SetName(search, "Search icons");

            var gridHost = new Grid();
            var scroller = new ScrollViewer
            {
                Content = gridHost,
                MaxHeight = 208,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            void Fill(string? query)
            {
                gridHost.Children.Clear();
                gridHost.ColumnDefinitions.Clear();
                gridHost.RowDefinitions.Clear();

                const int columns = 6;
                for (var column = 0; column < columns; column++)
                {
                    gridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                var icons = SpaceIcons.Search(query);
                for (var index = 0; index < icons.Count; index++)
                {
                    var icon = icons[index];
                    var row = index / columns;
                    if (row >= gridHost.RowDefinitions.Count)
                    {
                        gridHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    }

                    var selected = string.Equals(icon.Name, selectedIcon, StringComparison.OrdinalIgnoreCase);
                    var tile = new Button
                    {
                        Height = 34,
                        Margin = new Thickness(1),
                        Padding = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        CornerRadius = new CornerRadius(6),
                        Background = selected ? StudioColors.Tint(selectedColour) : null,
                        BorderBrush = selected ? StudioColors.Brush(selectedColour) : null,
                        BorderThickness = selected ? new Thickness(1) : new Thickness(0),
                        Content = new FontIcon { FontSize = 15, Glyph = icon.Glyph, Foreground = secondaryBrush },
                        Style = iconStyle,
                    };

                    AutomationProperties.SetName(tile, icon.Label);
                    ToolTipService.SetToolTip(tile, icon.Label);
                    tile.Click += (_, _) =>
                    {
                        selectedIcon = icon.Name;
                        RefreshIcon();
                        iconPicker.Hide();
                    };

                    Grid.SetRow(tile, row);
                    Grid.SetColumn(tile, index % columns);
                    gridHost.Children.Add(tile);
                }

                if (icons.Count == 0)
                {
                    gridHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var empty = new TextBlock
                    {
                        Margin = new Thickness(8, 12, 8, 12),
                        FontSize = 12,
                        Foreground = mutedBrush,
                        Text = "No icons match.",
                    };
                    Grid.SetColumnSpan(empty, columns);
                    gridHost.Children.Add(empty);
                }
            }

            var letter = new Button
            {
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = selectedIcon is null ? StudioColors.Tint(selectedColour) : null,
                Style = iconStyle,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = StudioColors.Brush(selectedColour),
                            Text = SpaceIcons.Monogram(nameBox.Text),
                        },
                        new TextBlock
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            FontSize = 12,
                            Text = "Use the first letter",
                        },
                    },
                },
            };
            AutomationProperties.SetName(letter, "Use the first letter");
            letter.Click += (_, _) =>
            {
                selectedIcon = null;
                RefreshIcon();
                iconPicker.Hide();
            };

            search.TextChanged += (_, _) => Fill(search.Text);
            Fill(null);

            panel.Children.Add(search);
            panel.Children.Add(scroller);
            panel.Children.Add(letter);

            iconPicker.Content = panel;

            // Attach before showing: Loaded fires while ShowAt builds the popup.
            search.Loaded += (_, _) => search.Focus(FocusState.Programmatic);
            iconPicker.ShowAt(iconButton);
        }

        iconButton.Click += (_, _) => ShowIconPicker();

        var colourPicker = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
        };

        var swatches = CreateColourSwatches(selectedColour, hex =>
        {
            selectedColour = hex;
            RefreshIcon();
            colourPicker.Hide();
        });

        var colourPanel = new StackPanel { Spacing = 10 };
        colourPanel.Children.Add(new TextBlock
        {
            Text = "Workspace colour",
            FontSize = 11,
            Foreground = secondaryBrush,
        });
        colourPanel.Children.Add(swatches);
        colourPicker.Content = colourPanel;

        var colourButtonControl = new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(9),
            Style = iconStyle,
            Flyout = colourPicker,
            Content = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = StudioColors.Brush(selectedColour),
                Stroke = secondaryBrush,
                StrokeThickness = 1,
            },
        };
        colourButton = colourButtonControl;

        var identity = new Grid { ColumnSpacing = 10 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.Children.Add(iconButton);
        Grid.SetColumn(nameBox, 1);
        identity.Children.Add(nameBox);
        Grid.SetColumn(colourButtonControl, 2);
        identity.Children.Add(colourButtonControl);

        nameBox.TextChanged += (_, _) => RefreshIcon();
        RefreshIcon();

        var root = new Grid { MinWidth = 360, RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(identity, 0);
        Grid.SetRow(form, 1);
        root.Children.Add(identity);
        root.Children.Add(form);

        // Destructive actions are last, quiet, and only exist once there is something to delete.
        Button? deleteButton = null;
        if (!creating)
        {
            var deleteContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            deleteContent.Children.Add(new FontIcon
            {
                FontSize = 11,
                Glyph = "\uE74D",
                Foreground = dangerBrush,
            });
            deleteContent.Children.Add(new TextBlock
            {
                Text = "Delete workspace",
                FontSize = 12,
                Foreground = dangerBrush,
            });

            deleteButton = new Button
            {
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = deleteContent,
                Style = iconStyle,
            };
            AutomationProperties.SetName(deleteButton, "Delete workspace");
            ToolTipService.SetToolTip(deleteButton, "Move its tabs to Archive and remove its connection");

            var deleteSection = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            deleteSection.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 0, 0, 8),
                Background = ThemeBrush("DividerStrokeColorDefaultBrush", StudioColors.Muted),
            });
            deleteSection.Children.Add(deleteButton);
            deleteSection.Children.Add(new TextBlock
            {
                Text = "Tabs with SQL move to Archive. This cannot be undone.",
                FontSize = 10,
                Foreground = mutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(deleteSection, 2);
            root.Children.Add(deleteSection);
        }

        // ContentDialog does not reliably scroll a tall body; keep the destructive action
        // reachable by scrolling it inside our own viewport.
        content = new ScrollViewer
        {
            Content = root,
            MaxHeight = 540,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
        };

        var deleteRequested = false;
        var dialog = new ContentDialog
        {
            Title = creating ? "New workspace" : "Workspace settings",
            XamlRoot = ownerXamlRoot,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonText = creating ? "Create & connect" : "Save",
            SecondaryButtonText = "Test",
            CloseButtonText = "Cancel",
        };

        if (deleteButton is not null)
        {
            deleteButton.Click += (_, _) =>
            {
                if (editing!.Tabs.Any(tab => tab.IsRunning) || editing.Tabs.Any(tab => tab.HasOpenTransaction))
                {
                    // The reason belongs next to the form, not hidden behind a closed dialog.
                    status.Foreground = dangerBrush;
                    status.Text = "Stop the query and roll back the transaction before deleting this workspace.";
                    return;
                }

                deleteRequested = true;
                dialog.Hide();
            };
        }

        dialog.Opened += (_, _) =>
        {
            nameBox.Focus(FocusState.Programmatic);
            nameBox.SelectAll();
        };

        providerBox.SelectionChanged += (_, _) =>
        {
            status.Text = string.Empty;
            RebuildFields();
        };

        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            status.Foreground = mutedBrush;
            status.Text = "Testing…";
            dialog.IsSecondaryButtonEnabled = false;

            IDatabaseConnection? probe = null;
            try
            {
                probe = SelectedProvider().CreateConnection(BuildProfile());
                await probe.OpenAsync();
                var databases = await probe.GetDatabasesAsync();
                status.Foreground = successBrush;
                status.Text = $"Connected · {databases.Count} databases";
            }
            catch (Exception ex)
            {
                status.Foreground = dangerBrush;
                status.Text = ex.Message;
            }
            finally
            {
                if (probe is not null)
                {
                    await probe.DisposeAsync();
                }

                dialog.IsSecondaryButtonEnabled = true;
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var provider = SelectedProvider();
            var missing = provider.Settings
                .Where(s => s.Required)
                .FirstOrDefault(s => string.IsNullOrWhiteSpace(ReadValue(s)));

            if (missing is not null)
            {
                args.Cancel = true;
                status.Foreground = dangerBrush;
                status.Text = $"{missing.Label} is required.";
                return;
            }

            if (creating)
            {
                // Nothing exists before this point: cancelling or closing the dialog created nothing.
                var created = ViewModel.CreateSpace(
                    nameBox.Text,
                    selectedColour,
                    selectedIcon,
                    provider.Id,
                    BuildProfile());

                // The dialog closes on this await; the workspace connects in the background.
                await ViewModel.ConnectSpaceAsync(created);
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                var refusal = await ViewModel.ApplyWorkspaceSettingsAsync(
                    editing!,
                    nameBox.Text,
                    selectedColour,
                    selectedIcon,
                    provider.Id,
                    BuildProfile());

                if (refusal is not null)
                {
                    // Keep the form open so the reason is visible next to the fields, not behind them.
                    args.Cancel = true;
                    status.Foreground = dangerBrush;
                    status.Text = refusal;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        RebuildFields();
        await dialog.ShowAsync();

        if (deleteRequested && editing is not null)
        {
            await ConfirmDeleteWorkspaceAsync(editing, ownerXamlRoot);
        }
    }

    /// <summary>
    /// Confirms a workspace deletion after its settings dialog has closed. The consequence is
    /// stated up front because the workspace itself cannot be restored; its tabs can.
    /// </summary>
    private async Task ConfirmDeleteWorkspaceAsync(SpaceViewModel space, XamlRoot root)
    {
        var withSql = space.Tabs.Count(tab => !string.IsNullOrWhiteSpace(tab.Content));
        var consequence = withSql switch
        {
            0 => $"\"{space.Name}\" and its connection settings will be removed. This cannot be undone.",
            1 => $"\"{space.Name}\" and its connection settings will be removed. Its tab moves to Archive so its SQL is not lost.",
            _ => $"\"{space.Name}\" and its connection settings will be removed. Its {withSql} tabs move to Archive so their SQL is not lost.",
        };

        var dialog = new ContentDialog
        {
            Title = "Delete workspace?",
            XamlRoot = root,
            Content = new TextBlock
            {
                MaxWidth = 360,
                Text = consequence,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Application.Current.Resources["StudioDangerButtonStyle"],
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSpaceAsync(space);
        }
    }

    // ---------------------------------------------------------------- new query palette

    private void ViewModel_NewTabRequested(object? sender, EventArgs e) => ShowNewTabPalette();

    /// <summary>
    /// New tabs start with a database, but the choice is a command palette instead of a dialog:
    /// type to filter, arrow keys to move, Enter opens. Esc or the backdrop cancels.
    /// </summary>
    private void ShowNewTabPalette()
    {
        if (ViewModel.ActiveSpace is not { } space)
        {
            return;
        }

        var preferred = ViewModel.ActiveTab?.Database ?? space.PreferredDatabase;
        _paletteDatabases = DatabaseInfo
            .InDisplayOrder(space.Databases.Select(database => database.Info), preferred)
            .Select(info => new DatabaseViewModel(info))
            .ToList();

        _newTabPaletteOpen = true;
        NewTabSearchBox.Text = string.Empty;
        ApplyNewTabFilter();
        NewTabPalette.Visibility = Visibility.Visible;

        DispatcherQueue.TryEnqueue(() =>
        {
            NewTabSearchBox.Focus(FocusState.Programmatic);
            NewTabSearchBox.SelectAll();
        });
    }

    private void HideNewTabPalette()
    {
        _newTabPaletteOpen = false;
        NewTabPalette.Visibility = Visibility.Collapsed;
    }

    private void ApplyNewTabFilter()
    {
        var query = NewTabSearchBox.Text.Trim();
        var matches = _paletteDatabases
            .Where(database => database.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        NewTabDatabaseList.ItemsSource = matches;

        // Inaccessible names stay visible but are never the Enter target: they are listed only so
        // the user can tell a database exists rather than wonder why it is missing.
        var firstAccessible = matches.FindIndex(database => database.IsAccessible);
        if (firstAccessible >= 0)
        {
            NewTabDatabaseList.SelectedIndex = firstAccessible;
            NewTabPaletteMessage.Visibility = Visibility.Collapsed;
            return;
        }

        NewTabDatabaseList.SelectedIndex = -1;
        NewTabPaletteMessage.Visibility = Visibility.Visible;
        NewTabPaletteMessage.Text = _paletteDatabases.Count == 0
            ? ViewModel.ActiveSpace?.Connection.IsConnected == true
                ? "This connection exposes no databases. Press Enter to open a tab without one."
                : "Connect this workspace to choose a database. Press Enter to open a tab without one."
            : matches.Count > 0
                ? matches.Count == 1
                    ? $"You don't have access to {matches[0].Name}."
                    : "You don't have access to those databases."
                : $"No database matches \"{query}\".";
    }

    private void NewTabSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_newTabPaletteOpen)
        {
            ApplyNewTabFilter();
        }
    }

    private void NewTabSearchBox_KeyDown(object sender, KeyRoutedEventArgs e) =>
        NewTabPalette_KeyDown(sender, e);

    private void NewTabPalette_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_newTabPaletteOpen)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HideNewTabPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var chosen = NewTabDatabaseList.SelectedItem as DatabaseViewModel
                ?? (NewTabDatabaseList.ItemsSource as IEnumerable<DatabaseViewModel>)?
                    .FirstOrDefault(database => database.IsAccessible);

            // Keep the palette open when the user typed a query with no usable database.
            if (chosen is null && _paletteDatabases.Count > 0)
            {
                e.Handled = true;
                return;
            }

            OpenNewTabPaletteSelection(chosen?.Name);
            e.Handled = true;
            return;
        }

        // The search box owns Up/Down; when the list has focus, let ListView move natively.
        if (NewTabSearchBox.FocusState != FocusState.Unfocused
            && e.Key is Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up)
        {
            MoveNewTabSelection(e.Key == Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
        }
    }

    private void MoveNewTabSelection(int offset)
    {
        // Arrow keys step over inaccessible rows: they are readable, not reachable.
        var accessible = new List<int>();
        for (var i = 0; i < NewTabDatabaseList.Items.Count; i++)
        {
            if (NewTabDatabaseList.Items[i] is DatabaseViewModel { IsAccessible: true })
            {
                accessible.Add(i);
            }
        }

        if (accessible.Count == 0)
        {
            return;
        }

        var position = accessible.IndexOf(NewTabDatabaseList.SelectedIndex);
        var next = position < 0
            ? offset > 0 ? 0 : accessible.Count - 1
            : (position + offset + accessible.Count) % accessible.Count;

        NewTabDatabaseList.SelectedIndex = accessible[next];
    }

    private void NewTabDatabaseList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item)
        {
            return;
        }

        var inaccessible = args.Item is DatabaseViewModel { IsAccessible: false };
        item.IsEnabled = !inaccessible;
        AutomationProperties.SetHelpText(
            item,
            inaccessible ? "This login cannot open this database." : string.Empty);
    }

    private void NewTabDatabaseList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DatabaseViewModel { IsAccessible: true } database)
        {
            OpenNewTabPaletteSelection(database.Name);
        }
    }

    private void NewTabPaletteBackdrop_Tapped(object sender, TappedRoutedEventArgs e) =>
        HideNewTabPalette();

    private void OpenNewTabPaletteSelection(string? database)
    {
        if (!_newTabPaletteOpen)
        {
            return;
        }

        HideNewTabPalette();
        ViewModel.CreateTab(database);

        // A new query tab is ready to type in; the palette's search box has already closed.
        DispatcherQueue.TryEnqueue(() => SqlEditor.FocusEditor());
    }

    // ---------------------------------------------------------------- splitter

    /// <summary>
    /// Reveals the results pane on the first run of a session. The saved height is reused; the
    /// pane slides up from below so the editor's loss of space is visible rather than a jump.
    /// </summary>
    private void ShowResultsPane()
    {
        if (_resultsVisible)
        {
            return;
        }

        _resultsVisible = true;
        ResultsPane.Visibility = Visibility.Visible;
        Splitter.Visibility = Visibility.Visible;

        var total = EditorRow.ActualHeight + ResultsRow.ActualHeight;
        var height = ViewModel.ResultsPaneHeight ?? DefaultResultsPaneHeight;
        if (total > 0)
        {
            height = Math.Clamp(height, 140, Math.Max(160, total - 160));
        }

        ResultsRow.Height = new GridLength(height);
        EditorRow.Height = new GridLength(1, GridUnitType.Star);

        var transform = ResultsPane.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            ResultsPane.RenderTransform = transform;
        }

        transform.Y = 0;
        ResultsPane.Opacity = 1;

        if (!new UISettings().AnimationsEnabled)
        {
            return;
        }

        _resultsSlide?.Stop();
        transform.Y = 28;
        ResultsPane.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var board = new Storyboard();

        var slide = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(fade, ResultsPane);
        Storyboard.SetTargetProperty(fade, "Opacity");

        board.Children.Add(slide);
        board.Children.Add(fade);
        _resultsSlide = board;
        board.Begin();
    }

    private void ToggleResultsPane()
    {
        if (_resultsVisible)
        {
            HideResultsPane();
        }
        else
        {
            ShowResultsPane();
        }
    }

    private void HideResultsPane()
    {
        if (!_resultsVisible)
        {
            return;
        }

        _resultsVisible = false;
        _resultsSlide?.Stop();

        var height = ResultsRow.ActualHeight;
        if (height > 0)
        {
            ViewModel.SetResultsPaneHeight(height);
        }

        ResultsPane.Visibility = Visibility.Collapsed;
        Splitter.Visibility = Visibility.Collapsed;
        ResultsRow.Height = new GridLength(0);
        EditorRow.Height = new GridLength(1, GridUnitType.Star);
        ResultsPane.Opacity = 1;

        if (ResultsPane.RenderTransform is TranslateTransform transform)
        {
            transform.Y = 0;
        }
    }

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = true;
        _splitStartY = e.GetCurrentPoint(Root).Position.Y;
        _splitStartHeight = EditorRow.ActualHeight;
        _splitTotalHeight = EditorRow.ActualHeight + ResultsRow.ActualHeight;
        _splitResultsHeight = Math.Max(0, _splitTotalHeight - _splitStartHeight);
        Splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter)
        {
            return;
        }

        var delta = e.GetCurrentPoint(Root).Position.Y - _splitStartY;
        var height = Math.Clamp(_splitStartHeight + delta, 120, Math.Max(140, _splitTotalHeight - 120));
        EditorRow.Height = new GridLength(height);
        ResultsRow.Height = new GridLength(1, GridUnitType.Star);
        _splitResultsHeight = Math.Max(0, _splitTotalHeight - height);
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = false;
        Splitter.ReleasePointerCapture(e.Pointer);
        ViewModel.SetResultsPaneHeight(_splitResultsHeight);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- results grid

    private void RenderResults()
    {
        var result = ViewModel.LastResult;
        ResultMessageText.Foreground = result?.IsError == true
            ? ThemeBrush("StudioDangerBrush", StudioColors.Danger)
            : ThemeBrush("StudioTextMutedBrush", StudioColors.Muted);

        ResultsGrid.ReferenceController = result is null
            ? null
            : ViewModel.CreateCellReferenceController(result);
        ResultsGrid.ShowResult(result, ViewModel.ActiveSpace?.ColorHex ?? StudioColors.Palette[0]);
        BringActiveResultTabIntoView();
    }

    /// <summary>Keeps the active result tab visible when Ctrl+\ appends beyond the viewport.</summary>
    private void BringActiveResultTabIntoView()
    {
        if (!ViewModel.HasMultipleResults || ViewModel.ActiveTab?.ActiveResult is not { } active)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
            ResultTabsList.ScrollIntoView(active, ScrollIntoViewAlignment.Leading));
    }

    private void ResultsGrid_StatusChanged(object? sender, string message) =>
        ViewModel.StatusText = message;

    private void ResultTab_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultSetViewModel result)
        {
            ViewModel.SelectResult(result);
        }
    }

    /// <summary>Double-clicking a result tab is the quick path to renaming it in place.</summary>
    private void ResultTab_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultSetViewModel result } container)
        {
            BeginResultRename(result, container);
            e.Handled = true;
        }
    }

    private void BeginResultRename(ResultSetViewModel result, FrameworkElement container)
    {
        if (!result.IsRenaming)
        {
            result.BeginRename();
        }

        // The editor is only visible after the tab has re-rendered, so focus it next tick.
        DispatcherQueue.TryEnqueue(() =>
        {
            var editor = FindDescendant<TextBox>(container, "ResultTitleEditor");
            if (editor is null)
            {
                return;
            }

            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        });
    }

    private void ResultTitleEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ResultSetViewModel result)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitResultRename(result, box.Text);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelResultRename(result);
            e.Handled = true;
        }
    }

    private void ResultTitleEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box
            && box.DataContext is ResultSetViewModel result
            && result.IsRenaming)
        {
            CommitResultRename(result, box.Text);
        }
    }

    private void CommitResultRename(ResultSetViewModel result, string text)
    {
        if (!result.IsRenaming)
        {
            return;
        }

        result.EndRename();
        ViewModel.RenameResult(result, text);
    }

    private void CancelResultRename(ResultSetViewModel result)
    {
        if (!result.IsRenaming)
        {
            return;
        }

        result.EndRename();
        ViewModel.StatusText = "Rename cancelled";
    }

    private void ResultTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultSetViewModel result })
        {
            ViewModel.CloseResult(result);
            HideResultsPaneIfEmpty();
        }
    }

    /// <summary>
    /// The single-result header has no tab strip close button, so it carries one. Closing the
    /// last result takes the whole pane with it; an empty pane is just chrome.
    /// </summary>
    private void ResultHeaderClose_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab?.ActiveResult is { } result)
        {
            ViewModel.CloseResult(result);
        }

        HideResultsPaneIfEmpty();
    }

    private void HideResultsPaneIfEmpty()
    {
        if (ViewModel.ActiveTab?.Results.Count is 0)
        {
            HideResultsPane();
        }
    }

    /// <summary>The results ListView owns the gesture; write the new order back to the query tab.</summary>
    private void ResultTabsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.SyncResultOrder();

    // ---------------------------------------------------------------- accelerators

    private async void RunStatementAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunStatementAsync();
    }

    private async void RunAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunAllAsync();
    }

    private void NewTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.NewTabCommand.Execute(null);
        args.Handled = true;
    }

    private void NextTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ViewModel.SelectRelativeTab(1);
    }

    private void PreviousTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ViewModel.SelectRelativeTab(-1);
    }

    private void ToggleSidebarAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ToggleSidebarCommand.Execute(null);
        args.Handled = true;
    }
}

