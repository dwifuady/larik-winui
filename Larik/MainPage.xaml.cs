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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Larik;

/// <summary>
/// The studio shell. View state lives in <see cref="StudioViewModel"/>; the editor is reached only
/// through <see cref="IEditorSurface"/>; the results grid is a view concern handled here.
/// </summary>
public sealed partial class MainPage : Page
{
    private const int MaxRenderedRows = 500;

    private bool _hooked;
    private readonly StudioCompletionSource _completionSource;

    private bool _draggingSplitter;
    private double _splitStartY;
    private double _splitStartHeight;
    private double _splitTotalHeight;

    public StudioViewModel ViewModel { get; } = new();

    /// <summary>Inactive tabs recede; the active tab carries primary text (Arc sidebar behaviour).</summary>
    public static Brush TabTitleBrush(bool isActive) =>
        (Brush)Application.Current.Resources[isActive ? "StudioTextPrimaryBrush" : "StudioTextSecondaryBrush"];

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        _completionSource = new StudioCompletionSource(() => ViewModel.Snippets, () => ViewModel.Tables);

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
        }

        // The caption lives inside the content column (the rail runs full height), so the page owns the drag region.
        App.Window.SetTitleBar(AppTitleBar);

        SyncEditorFromActiveTab();
        RenderResults();
        SqlEditor.FocusEditor();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        ViewModel.ToggleSidebarCommand.Execute(null);

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
            case EditorCommandKind.RunQuery:
                ViewModel.RunCommand.Execute(null);
                break;
            case EditorCommandKind.NewTab:
                ViewModel.NewTabCommand.Execute(null);
                break;
            case EditorCommandKind.ToggleSidebar:
                ViewModel.ToggleSidebarCommand.Execute(null);
                break;
        }
    }

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

        flyout.Items.Add(new MenuFlyoutSeparator());

        var newSpace = new MenuFlyoutItem
        {
            Text = "New workspace",
            Icon = new FontIcon { Glyph = "\uE710" },
        };
        newSpace.Click += (_, _) => ViewModel.NewSpaceCommand.Execute(null);
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
    /// Renders whatever fields the selected provider declares, so adding a provider needs no
    /// dialog changes: the schema comes from <see cref="IDatabaseProvider.Settings"/>.
    /// </summary>
    private async Task ShowConnectionDialogAsync()
    {
        if (ViewModel.ActiveSpace is null)
        {
            return;
        }

        var space = ViewModel.ActiveSpace;
        var connection = space.Connection;
        var providers = DatabaseProviderCatalog.Providers;
        if (providers.Count == 0)
        {
            return;
        }

        var mutedBrush = (Brush)Application.Current.Resources["StudioTextMutedBrush"];
        var dangerBrush = StudioColors.Danger;
        var successBrush = StudioColors.Success;

        var providerBox = new ComboBox
        {
            ItemsSource = providers.Select(p => p.DisplayName).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(providerBox, "Provider");

        var current = providers.FirstOrDefault(p => p.Id == connection.ProviderId) ?? providers[0];
        providerBox.SelectedIndex = providers.ToList().IndexOf(current);

        var fields = new StackPanel { Spacing = 10 };
        var inputs = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = mutedBrush,
        };

        IDatabaseProvider SelectedProvider() => providers[Math.Max(0, providerBox.SelectedIndex)];

        void RebuildFields()
        {
            fields.Children.Clear();
            inputs.Clear();

            foreach (var setting in SelectedProvider().Settings)
            {
                var value = connection.Profile.Get(setting.Key)
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

                fields.Children.Add(group);
            }
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

        // The dialog's own scroller does not always engage when the form outgrows the window,
        // so the fields scroll in their own viewport and the status line stays in view.
        var fieldsScroll = new ScrollViewer
        {
            Content = fields,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
        };

        var content = new Grid { MinWidth = 360, RowSpacing = 12 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(providerGroup, 0);
        Grid.SetRow(fieldsScroll, 1);
        Grid.SetRow(status, 2);
        content.Children.Add(providerGroup);
        content.Children.Add(fieldsScroll);
        content.Children.Add(status);

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
            content.Children.Add(loadErrors);
        }

        var dialog = new ContentDialog
        {
            Title = "Connection",
            XamlRoot = XamlRoot,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonText = "Save & connect",
            SecondaryButtonText = "Test",
            CloseButtonText = "Cancel",
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
        ResultsHost.Children.Clear();
        ResultsHost.ColumnDefinitions.Clear();
        ResultsHost.RowDefinitions.Clear();

        var result = ViewModel.LastResult;
        ResultMessageText.Foreground = result?.IsError == true
            ? StudioColors.Danger
            : (Brush)Application.Current.Resources["StudioTextMutedBrush"];

        if (result is null)
        {
            return;
        }

        if (result.Columns.Count == 0 || result.Rows.Count == 0)
        {
            ResultsHost.Children.Add(new TextBlock
            {
                Text = result.Message,
                Margin = new Thickness(14, 12, 14, 12),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["StudioTextMutedBrush"],
            });
            return;
        }

        var accent = StudioColors.Brush(ViewModel.ActiveSpace?.ColorHex ?? StudioColors.Palette[0]);
        var mutedBrush = (Brush)Application.Current.Resources["StudioTextMutedBrush"];
        var secondaryBrush = (Brush)Application.Current.Resources["StudioTextSecondaryBrush"];
        var primaryBrush = (Brush)Application.Current.Resources["StudioTextPrimaryBrush"];

        var columns = result.Columns;
        for (var c = 0; c < columns.Count; c++)
        {
            var numeric = result.Rows.All(r => IsNumeric(r[c]));
            ResultsHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(c == 0 ? 72 : 176),
                MinWidth = 72,
            });

            ResultsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = columns[c].Name,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 80,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = mutedBrush,
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(header, c);
            Grid.SetRow(header, 0);
            ResultsHost.Children.Add(header);
        }

        var rowCount = Math.Min(result.Rows.Count, MaxRenderedRows);
        for (var r = 0; r < rowCount; r++)
        {
            ResultsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var c = 0; c < columns.Count; c++)
            {
                var numeric = columns.Count > 0 && result.Rows.All(row => IsNumeric(row[c]));
                var cell = new TextBlock
                {
                    Text = result.Rows[r][c] ?? "NULL",
                    FontFamily = (FontFamily)Application.Current.Resources["StudioMonoFont"],
                    FontSize = 12,
                    Padding = new Thickness(12, 5, 12, 5),
                    Foreground = result.Rows[r][c] is null
                        ? mutedBrush
                        : c == 0 ? secondaryBrush : primaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                };

                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r + 1);
                ResultsHost.Children.Add(cell);
            }
        }

        // Keep the space colour visible in the result header: a 2px accent rule under row 0.
        var rule = new Border
        {
            Height = 2,
            Background = accent,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(rule, 0);
        Grid.SetColumnSpan(rule, columns.Count);
        Grid.SetRow(rule, 0);
        ResultsHost.Children.Add(rule);
    }

    private static bool IsNumeric(string? value) =>
        value is not null && double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);

    // ---------------------------------------------------------------- accelerators

    private void RunAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.RunCommand.Execute(null);
        args.Handled = true;
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
