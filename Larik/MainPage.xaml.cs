using System.ComponentModel;
using Larik.Data;
using Larik.Editor;
using Larik.Services;
using Larik.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
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

    private bool _newTabDialogOpen;

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

    // ---------------------------------------------------------------- workspace switching

    private int ActiveSpaceIndex() =>
        ViewModel.ActiveSpace is null ? -1 : ViewModel.Spaces.IndexOf(ViewModel.ActiveSpace);

    /// <summary>
    /// The query content slides in from the side the new workspace lives on, so the dots at the
    /// bottom (and the switcher order) read as a left-to-right sequence.
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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

        string sql;
        if (!selection.IsEmpty)
        {
            sql = text.Substring(selection.Start, selection.Length);
        }
        else if (all)
        {
            sql = text;
        }
        else
        {
            var statement = SqlStatementParser.FindStatement(text, SqlEditor.CaretOffset);
            if (!statement.IsEmpty)
            {
                SqlEditor.Select(statement);
            }

            sql = text.Substring(statement.Start, statement.Length);
        }

        await ViewModel.RunSqlAsync(sql, append);
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

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            ViewModel.CloseTab(tab);
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

    /// <summary>The dots at the bottom of the rail switch workspaces, like Arc's space indicator.</summary>
    private void WorkspaceIndicator_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpaceViewModel space })
        {
            ViewModel.SelectSpace(space);
        }
    }

    private void SpaceSwitcher_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        // The connection belongs to the active workspace, so its actions live with the workspace menu.
        var toggle = new MenuFlyoutItem
        {
            Text = ViewModel.ActiveSpace?.Connection.ConnectionActionLabel ?? "Connect",
            Command = ViewModel.ToggleConnectionCommand,
        };
        flyout.Items.Add(toggle);

        var settings = new MenuFlyoutItem
        {
            Text = "Connection settings…",
            Icon = new FontIcon { Glyph = "\uE713" },
        };
        settings.Click += async (_, _) => await ShowConnectionDialogAsync();
        flyout.Items.Add(settings);

        flyout.Items.Add(new MenuFlyoutSeparator());

        foreach (var space in ViewModel.Spaces)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{space.Name} ({space.Connection.ProviderName})",
                GroupName = "workspace",
                IsChecked = space.IsActive,
            };

            item.Click += (_, _) => ViewModel.SelectSpace(space);
            flyout.Items.Add(item);
        }

        // The active workspace's identity colour, one level down in the menu that owns the workspace.
        if (ViewModel.ActiveSpace is { } activeSpace)
        {
            var colours = new MenuFlyoutSubItem { Text = "Workspace colour" };
            for (var index = 0; index < StudioColors.Palette.Length; index++)
            {
                var hex = StudioColors.Palette[index];
                var swatch = new RadioMenuFlyoutItem
                {
                    Text = StudioColors.PaletteNames[index],
                    GroupName = "workspace-colour",
                    IsChecked = string.Equals(hex, activeSpace.ColorHex, StringComparison.OrdinalIgnoreCase),
                    Icon = new PathIcon
                    {
                        Data = new EllipseGeometry { Center = new Point(6, 6), RadiusX = 5, RadiusY = 5 },
                        Foreground = StudioColors.Brush(hex),
                    },
                };

                swatch.Click += (_, _) => ViewModel.SetWorkspaceColor(hex);
                colours.Items.Add(swatch);
            }

            flyout.Items.Add(colours);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var newSpace = new MenuFlyoutItem
        {
            Text = "New workspace",
            Icon = new FontIcon { Glyph = "\uE710" },
        };
        newSpace.Click += async (_, _) => await ShowConnectionDialogAsync(creating: true);
        flyout.Items.Add(newSpace);

        flyout.ShowAt(SpaceSwitcherButton, new Point(0, 40));
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

    private async void ConnectionSettings_Click(object sender, RoutedEventArgs e)
    {
        DatabaseSelectorButton.Flyout?.Hide();
        await ShowConnectionDialogAsync();
    }

    // ---------------------------------------------------------------- connection settings

    /// <summary>
    /// The provider-driven connection form. Create mode also collects the workspace name and
    /// colour, and nothing exists until the user chooses Create &amp; connect: Cancel or closing
    /// the dialog leaves the workspace list untouched.
    /// </summary>
    private async Task ShowConnectionDialogAsync(bool creating = false)
    {
        var space = ViewModel.ActiveSpace;
        if (!creating && space is null)
        {
            return;
        }

        var providers = DatabaseProviderCatalog.Providers;
        if (providers.Count == 0)
        {
            return;
        }

        var connection = creating ? null : space!.Connection;
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
        TextBox? nameBox = null;
        var selectedColour = StudioColors.Palette[ViewModel.Spaces.Count % StudioColors.Palette.Length];

        if (creating)
        {
            // The default name and colour are ready to accept; changing them is optional.
            nameBox = new TextBox
            {
                Text = $"Space {ViewModel.Spaces.Count + 1}",
                PlaceholderText = "Workspace name",
            };
            AutomationProperties.SetName(nameBox, "Workspace name");

            var nameGroup = new StackPanel { Spacing = 4 };
            nameGroup.Children.Add(new TextBlock { Text = "Name", FontSize = 11 });
            nameGroup.Children.Add(nameBox);

            var rings = new Dictionary<string, Ellipse>(StringComparer.OrdinalIgnoreCase);
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var swatchStyle = (Style)Application.Current.Resources["StudioIconButtonStyle"];
            var ringBrush = (Brush)Application.Current.Resources["StudioTextPrimaryBrush"];

            void SelectColour(string hex)
            {
                selectedColour = hex;
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
                    Width = 24,
                    Height = 24,
                    Stroke = ringBrush,
                    StrokeThickness = 1.5,
                    Visibility = Visibility.Collapsed,
                };

                var dot = new Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = StudioColors.Brush(hex),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var swatchGrid = new Grid { Width = 24, Height = 24 };
                swatchGrid.Children.Add(ring);
                swatchGrid.Children.Add(dot);

                var swatch = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Content = swatchGrid,
                    Style = swatchStyle,
                };
                AutomationProperties.SetName(swatch, $"Colour {StudioColors.PaletteNames[index]}");
                ToolTipService.SetToolTip(swatch, StudioColors.PaletteNames[index]);
                swatch.Click += (_, _) => SelectColour(hex);

                rings[hex] = ring;
                swatches.Children.Add(swatch);
            }

            SelectColour(selectedColour);

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
        }

        var dialog = new ContentDialog
        {
            Title = creating ? "New workspace" : "Connection",
            XamlRoot = XamlRoot,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonText = creating ? "Create & connect" : "Save & connect",
            SecondaryButtonText = "Test",
            CloseButtonText = "Cancel",
        };

        if (creating)
        {
            dialog.Opened += (_, _) =>
            {
                nameBox!.Focus(FocusState.Programmatic);
                nameBox.SelectAll();
            };
        }

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
                    nameBox?.Text ?? string.Empty,
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
                await ViewModel.ApplyConnectionSettingsAsync(provider.Id, BuildProfile(), connect: true);
            }
            finally
            {
                deferral.Complete();
            }
        };

        RebuildFields();
        await dialog.ShowAsync();
    }

    // ---------------------------------------------------------------- new query

    private async void ViewModel_NewTabRequested(object? sender, EventArgs e)
    {
        try
        {
            await ShowNewTabDialogAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not open the new query dialog: {ex.Message}";
        }
    }

    /// <summary>
    /// Every new tab starts with a database choice: completion, the database picker and the
    /// result grid are per tab, so the tab must know which database it belongs to from day one.
    /// </summary>
    private async Task ShowNewTabDialogAsync()
    {
        if (_newTabDialogOpen || ViewModel.ActiveSpace is not { } space)
        {
            return;
        }

        _newTabDialogOpen = true;
        try
        {
            var databases = space.Databases.Select(database => database.Name).ToList();
            ComboBox? databaseBox = null;

            var body = new StackPanel { MinWidth = 320, Spacing = 6 };
            if (databases.Count > 0)
            {
                databaseBox = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Header = "Database",
                    ItemsSource = databases,
                    PlaceholderText = "Choose a database",
                };

                var selected = ViewModel.ActiveTab?.Database;
                databaseBox.SelectedItem = databases.FirstOrDefault(name =>
                        string.Equals(name, selected, StringComparison.OrdinalIgnoreCase))
                    ?? databases.FirstOrDefault(name =>
                        string.Equals(name, space.PreferredDatabase, StringComparison.OrdinalIgnoreCase))
                    ?? space.ActiveDatabase?.Name
                    ?? databases[0];
                AutomationProperties.SetName(databaseBox, "New query database");

                body.Children.Add(databaseBox);
                body.Children.Add(new TextBlock
                {
                    Text = "Autocomplete, queries and results stay with this tab's database.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["StudioTextMutedBrush"],
                });
            }
            else
            {
                body.Children.Add(new TextBlock
                {
                    Text = space.Connection.IsConnected
                        ? "This connection exposes no databases. The tab opens without one."
                        : "Connect this workspace to choose a database. The tab opens without one.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["StudioTextMutedBrush"],
                });
            }

            var dialog = new ContentDialog
            {
                Title = "New query",
                XamlRoot = XamlRoot,
                Content = body,
                DefaultButton = ContentDialogButton.Primary,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
            };

            dialog.Opened += (_, _) => databaseBox?.Focus(FocusState.Programmatic);
            dialog.PrimaryButtonClick += (_, _) =>
            {
                var chosen = databaseBox?.SelectedItem as string
                    ?? ViewModel.ActiveTab?.Database;
                ViewModel.CreateTab(chosen);
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _newTabDialogOpen = false;
        }
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

