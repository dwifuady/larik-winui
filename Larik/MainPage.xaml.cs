using System.ComponentModel;
using Larik.Data;
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

    private bool _newTabPaletteOpen;
    private List<string> _paletteDatabases = [];

    private bool _schemaPinned;
    private readonly Dictionary<string, List<string>> _recentObjects = new(StringComparer.Ordinal);

    public StudioViewModel ViewModel { get; } = new();

    /// <summary>Inactive tabs recede; the active tab carries primary text (Arc sidebar behaviour).</summary>
    public static Brush TabTitleBrush(bool isActive) =>
        (Brush)Application.Current.Resources[isActive ? "StudioTextPrimaryBrush" : "StudioTextSecondaryBrush"];

    public MainPage()
    {
        InitializeComponent();

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
            Root.Background = (Brush)Application.Current.Resources["StudioBackgroundBrush"];
        }

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
    }

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
            ViewModel.ResultChanged += (_, _) => RenderResults();
            ViewModel.NewTabRequested += ViewModel_NewTabRequested;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _lastSpaceIndex = ActiveSpaceIndex();
            ViewModel.StartBackgroundMaintenance();
        }

        // The caption lives inside the content column (the rail runs full height), so the page owns the drag region.
        App.Window.SetTitleBar(AppTitleBar);

        SyncEditorFromActiveTab();
        RenderResults();
        SqlEditor.FocusEditor();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        ViewModel.ToggleSidebarCommand.Execute(null);

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SettingsPage));

    // ---------------------------------------------------------------- schema browser

    private void SchemaAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenSchemaPeek();
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
        SchemaPeek.Show(ViewModel.Tables, database, recents);

        if (_schemaPinned)
        {
            SchemaPinned.Show(ViewModel.Tables, database, recents);
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
        var text = selection.Column ?? QualifiedTableName(selection.Table);
        SqlEditor.InsertText(text);
        RememberSchemaObject(selection.Table);
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
        var name = QualifiedTableName(selection.Table);
        RememberSchemaObject(selection.Table);

        if (closePeek)
        {
            CloseSchemaPeek();
        }
        else if (_schemaPinned)
        {
            RefreshSchemaBrowsers();
        }

        ViewModel.StatusText = $"Previewing {name} · top 100";
        await ViewModel.RunSqlAsync($"SELECT TOP 100 * FROM {name};", append: true);
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
        if (e.PropertyName == nameof(StudioViewModel.Tables))
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
            case EditorCommandKind.ToggleSidebar:
                ViewModel.ToggleSidebarCommand.Execute(null);
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

        var pin = new MenuFlyoutItem
        {
            Text = tab.PinLabel,
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
        ViewModel.SyncTabOrder();

    /// <summary>Pinned and normal sections share the same persisted order (pinned first).</summary>
    private void PinnedTabsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.SyncTabOrder();

    /// <summary>The dots at the bottom of the rail switch workspaces, like Arc's space indicator.</summary>
    private void WorkspaceIndicator_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpaceViewModel space })
        {
            ViewModel.SelectSpace(space);
        }
    }

    /// <summary>
    /// The workspace panel. Its one job is switching, so the current workspace's identity controls
    /// stay at the top (colour), then its connection, then the list, then creation. It also opens
    /// from the toolbar badge while the rail is collapsed, which is why the connection action is
    /// repeated here: the rail is not always on screen to carry it.
    /// </summary>
    private void SpaceSwitcher_Click(object sender, RoutedEventArgs e) => ShowWorkspaceFlyout(SpaceSwitcherButton);

    private void ContextBadge_Click(object sender, RoutedEventArgs e) => ShowWorkspaceFlyout(ContextBadgeButton);

    private void ShowWorkspaceFlyout(FrameworkElement anchor)
    {
        var mutedBrush = (Brush)Application.Current.Resources["StudioTextMutedBrush"];
        var panel = new StackPanel { MinWidth = 288, Spacing = 2 };

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = true,
        };

        if (ViewModel.ActiveSpace is { } active)
        {
            // The host detail the rail trims away; it says what Connect would connect to.
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(12, 8, 12, 6),
                FontSize = 10,
                Foreground = mutedBrush,
                Text = active.Connection.StatusLine,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // The one action for the line above it. Identity and connection settings live on the
            // workspace's own row (the pencil), so this panel never grows a second editor.
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

        foreach (var space in ViewModel.Spaces)
        {
            panel.Children.Add(CreateWorkspaceRow(space, flyout));
        }

        panel.Children.Add(CreateFlyoutSeparator());
        panel.Children.Add(CreateFlyoutAction("\uE710", "New workspace", async () =>
        {
            flyout.Hide();
            await ShowWorkspaceDialogAsync();
        }));

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    /// <summary>A workspace row: click to switch, the pencil renames it in place.</summary>
    private FrameworkElement CreateWorkspaceRow(SpaceViewModel space, Flyout flyout)
    {
        var primaryBrush = (Brush)Application.Current.Resources["StudioTextPrimaryBrush"];
        var secondaryBrush = (Brush)Application.Current.Resources["StudioTextSecondaryBrush"];
        var accentBrush = (Brush)Application.Current.Resources["StudioAccentBrush"];
        var iconStyle = (Style)Application.Current.Resources["StudioIconButtonStyle"];

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = space.AccentBrush,
        };

        var name = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = space.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = space.IsActive ? primaryBrush : secondaryBrush,
            Text = space.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var check = new FontIcon
        {
            FontSize = 10,
            Foreground = accentBrush,
            Glyph = "\uE73E",
            Visibility = space.IsActive ? Visibility.Visible : Visibility.Collapsed,
        };

        Grid.SetColumn(name, 1);
        Grid.SetColumn(check, 2);
        content.Children.Add(dot);
        content.Children.Add(name);
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

        AutomationProperties.SetName(switchButton, space.Name);
        ToolTipService.SetToolTip(switchButton, space.TooltipText);
        switchButton.Click += (_, _) =>
        {
            flyout.Hide();
            ViewModel.SelectSpace(space);
        };

        var edit = new Button
        {
            Width = 28,
            Height = 36,
            Padding = new Thickness(0),
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { FontSize = 10, Glyph = "\uE70F" },
            Style = iconStyle,
        };

        AutomationProperties.SetName(edit, $"Edit {space.Name}");
        ToolTipService.SetToolTip(edit, "Workspace settings");
        edit.Click += async (_, _) =>
        {
            flyout.Hide();
            await ShowWorkspaceDialogAsync(space);
        };

        var inner = new Grid { Height = 36, ColumnSpacing = 2 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.Children.Add(switchButton);
        Grid.SetColumn(edit, 1);
        inner.Children.Add(edit);

        // Arc marks the active workspace with its tint, not only a check mark. The tint covers the
        // edit affordance too so the row reads as one object, and never sits in the switch hit area.
        var row = new Border
        {
            Margin = new Thickness(4, 0, 4, 0),
            Background = space.IsActive ? space.ActiveTintBrush : null,
            Child = inner,
            CornerRadius = new CornerRadius(6),
        };
        return row;
    }

    private static Border CreateFlyoutSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 4, 8, 4),
        Background = (Brush)Application.Current.Resources["HairlineBrush"],
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
    /// One palette strip, used by the workspace editor. The ring marks the current colour so the
    /// state is visible without opening anything.
    /// </summary>
    private static StackPanel CreateColourSwatches(string selectedHex, Action<string> changed)
    {
        const double dotSize = 16;
        const double ringSize = dotSize + 8;
        var rings = new Dictionary<string, Ellipse>(StringComparer.OrdinalIgnoreCase);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var iconStyle = (Style)Application.Current.Resources["StudioIconButtonStyle"];
        var ringBrush = (Brush)Application.Current.Resources["StudioTextPrimaryBrush"];

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
    }

    // ---------------------------------------------------------------- workspace settings

    /// <summary>
    /// The workspace editor. A workspace is its name, colour and single connection, so one dialog
    /// covers all three. Create mode (no <paramref name="editing"/>) starts from a ready name and
    /// colour and creates nothing until confirmed; edit mode is the same form, prefilled, and can
    /// be opened from any row in the switcher without leaving the current workspace.
    /// </summary>
    private async Task ShowWorkspaceDialogAsync(SpaceViewModel? editing = null)
    {
        var creating = editing is null;
        var providers = DatabaseProviderCatalog.Providers;
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
        var mutedBrush = (Brush)Application.Current.Resources["StudioTextMutedBrush"];
        var dangerBrush = StudioColors.Danger;
        var successBrush = StudioColors.Success;

        var providerBox = new ComboBox
        {
            ItemsSource = providers.Select(p => p.DisplayName).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(providerBox, "Provider");

        var providerId = connection?.ProviderId ?? DatabaseProviderCatalog.Default?.Id;
        var current = providers.FirstOrDefault(provider => provider.Id == providerId) ?? providers[0];
        providerBox.SelectedIndex = providers.ToList().IndexOf(current);

        var primaryFields = new StackPanel { Spacing = 10 };
        var advancedFields = new StackPanel { Spacing = 10 };
        var inputs = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = mutedBrush,
        };

        // Rare provider fields (port, encryption, timeouts) live one level down instead of
        // competing with the server and credentials.
        var advanced = new Expander
        {
            Header = "Advanced",
            IsExpanded = false,
            Content = advancedFields,
            Visibility = Visibility.Collapsed,
        };

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
                group.Children.Add(input);

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

            advanced.Visibility = advancedFields.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        providerGroup.Children.Add(new TextBlock { Text = "Provider", FontSize = 11 });
        providerGroup.Children.Add(providerBox);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(primaryFields);
        body.Children.Add(advanced);

        // The dialog's own scroller does not always engage when the form outgrows the window,
        // so the fields scroll in their own viewport and the status line stays in view.
        var fieldsScroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
        };

        var form = new Grid { MinWidth = 360, RowSpacing = 12 };
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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

        // Name and colour are always part of the form: a workspace is identified by them, and
        // editing one is editing the other. The default is ready to accept.
        var nameBox = new TextBox
        {
            Text = creating ? $"Space {ViewModel.Spaces.Count + 1}" : editing!.Name,
            PlaceholderText = "Workspace name",
        };
        AutomationProperties.SetName(nameBox, "Workspace name");

        var nameGroup = new StackPanel { Spacing = 4 };
        nameGroup.Children.Add(new TextBlock { Text = "Name", FontSize = 11 });
        nameGroup.Children.Add(nameBox);

        var swatches = CreateColourSwatches(selectedColour, hex => selectedColour = hex);

        var colourGroup = new StackPanel { Spacing = 4 };
        colourGroup.Children.Add(new TextBlock { Text = "Colour", FontSize = 11 });
        colourGroup.Children.Add(swatches);

        var header = new StackPanel { Spacing = 12 };
        header.Children.Add(nameGroup);
        header.Children.Add(colourGroup);

        var root = new Grid { MinWidth = 360, RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(form, 1);
        root.Children.Add(header);
        root.Children.Add(form);
        content = root;

        var dialog = new ContentDialog
        {
            Title = creating ? "New workspace" : "Workspace settings",
            XamlRoot = XamlRoot,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonText = creating ? "Create & connect" : "Save",
            SecondaryButtonText = "Test",
            CloseButtonText = "Cancel",
        };

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
        _paletteDatabases = space.Databases
            .Select(database => database.Name)
            .OrderByDescending(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
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
            .Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        NewTabDatabaseList.ItemsSource = matches;
        if (matches.Count > 0)
        {
            NewTabDatabaseList.SelectedIndex = 0;
            NewTabPaletteMessage.Visibility = Visibility.Collapsed;
            return;
        }

        NewTabPaletteMessage.Visibility = Visibility.Visible;
        NewTabPaletteMessage.Text = _paletteDatabases.Count == 0
            ? ViewModel.ActiveSpace?.Connection.IsConnected == true
                ? "This connection exposes no databases. Press Enter to open a tab without one."
                : "Connect this workspace to choose a database. Press Enter to open a tab without one."
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
            var chosen = NewTabDatabaseList.SelectedItem as string
                ?? (NewTabDatabaseList.ItemsSource as IEnumerable<string>)?.FirstOrDefault();

            // Keep the palette open when the user typed a query with no matching database.
            if (chosen is null && _paletteDatabases.Count > 0)
            {
                e.Handled = true;
                return;
            }

            OpenNewTabPaletteSelection(chosen);
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
        var count = NewTabDatabaseList.Items.Count;
        if (count == 0)
        {
            return;
        }

        var index = NewTabDatabaseList.SelectedIndex;
        NewTabDatabaseList.SelectedIndex = index < 0
            ? offset > 0 ? 0 : count - 1
            : (index + offset + count) % count;
    }

    private void NewTabDatabaseList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string database)
        {
            OpenNewTabPaletteSelection(database);
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
    }

    // ---------------------------------------------------------------- splitter

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = true;
        _splitStartY = e.GetCurrentPoint(Root).Position.Y;
        _splitStartHeight = EditorRow.ActualHeight;
        _splitTotalHeight = EditorRow.ActualHeight + ResultsRow.ActualHeight;
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
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = false;
        Splitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- results grid

    private void RenderResults()
    {
        var result = ViewModel.LastResult;
        ResultMessageText.Foreground = result?.IsError == true
            ? StudioColors.Danger
            : (Brush)Application.Current.Resources["StudioTextMutedBrush"];

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

    private void ResultTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultSetViewModel result })
        {
            ViewModel.CloseResult(result);
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

    private void ToggleSidebarAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ToggleSidebarCommand.Execute(null);
        args.Handled = true;
    }
}

