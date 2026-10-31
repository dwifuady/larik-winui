using Larik.Data;
using Larik.Models;
using Larik.Services;
using Larik.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Larik;

/// <summary>
/// The studio shell. View state lives in <see cref="StudioViewModel"/>; caret geometry,
/// the snippet popup, and the results grid are view concerns handled here.
/// </summary>
public sealed partial class MainPage : Page
{
    private const double PopupWidth = 392;
    private const double PopupMaxHeight = 268;
    private const int MaxRenderedRows = 500;

    private bool _suppressEditorEvents;
    private bool _hooked;
    private IReadOnlyList<CompletionItem>? _completions;
    private int _selectedIndex = -1;

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
            ViewModel.ActiveTabChanged += (_, _) => SyncEditorFromActiveTab();
            ViewModel.ResultChanged += (_, _) => RenderResults();
        }

        // The caption lives inside the content column (the rail runs full height), so the page owns the drag region.
        App.Window.SetTitleBar(AppTitleBar);

        SyncEditorFromActiveTab();
        RenderResults();
        Editor.Focus(FocusState.Programmatic);
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        ViewModel.ToggleSidebarCommand.Execute(null);

    // ---------------------------------------------------------------- editor <-> model

    private string GetEditorText()
    {
        Editor.Document.GetText(TextGetOptions.None, out var text);
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private void SyncEditorFromActiveTab()
    {
        _suppressEditorEvents = true;
        try
        {
            Editor.Document.SetText(TextSetOptions.None, ViewModel.ActiveTab?.Content ?? string.Empty);
            Editor.Document.Selection.SetRange(0, 0);
        }
        finally
        {
            _suppressEditorEvents = false;
        }

        HideCompletions();
        ViewModel.UpdateCaret(GetEditorText(), Editor.Document.Selection.StartPosition);
    }

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        var text = GetEditorText();
        if (ViewModel.ActiveTab is not null)
        {
            ViewModel.ActiveTab.Content = text;
        }

        ViewModel.UpdateCaret(text, Editor.Document.Selection.StartPosition);
        UpdateCompletions();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        var selection = Editor.Document.Selection;
        if (selection.StartPosition != selection.EndPosition)
        {
            HideCompletions();
        }

        ViewModel.UpdateCaret(GetEditorText(), selection.StartPosition);
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs e) => HideCompletions();

    // ---------------------------------------------------------------- completion popup

    private void UpdateCompletions()
    {
        if (_suppressEditorEvents || ViewModel.ActiveTab is null)
        {
            return;
        }

        var text = GetEditorText();
        var caret = Editor.Document.Selection.StartPosition;
        var (_, word, qualifier) = CompletionEngine.ExtractContext(text, caret);

        if (word.Length == 0)
        {
            HideCompletions();
            return;
        }

        var items = CompletionEngine.Suggest(
            word,
            qualifier,
            ViewModel.Snippets,
            ViewModel.Tables,
            clipboard: string.Empty);

        if (items.Count == 0)
        {
            HideCompletions();
            return;
        }

        _completions = items;
        CompletionList.ItemsSource = items;
        SelectCompletion(0);
        CompletionPopup.IsOpen = true;
        PositionCompletions();
    }

    private void PositionCompletions()
    {
        if (XamlRoot?.Content is not FrameworkElement root)
        {
            return;
        }

        Editor.Document.Selection.GetRect(PointOptions.ClientCoordinates, out var caretRect, out _);
        var origin = Editor
            .TransformToVisual(root)
            .TransformPoint(new Point(caretRect.X, caretRect.Y + caretRect.Height + 4));

        CompletionPopup.Child.Measure(new Size(PopupWidth, double.PositiveInfinity));
        var height = Math.Min(CompletionPopup.Child.DesiredSize.Height, PopupMaxHeight);

        var x = Math.Clamp(origin.X, 8, Math.Max(8, root.ActualWidth - PopupWidth - 8));
        var y = origin.Y;
        if (y + height > root.ActualHeight - 8)
        {
            y = Math.Max(8, origin.Y - height - caretRect.Height - 8);
        }

        CompletionPopup.HorizontalOffset = x;
        CompletionPopup.VerticalOffset = y;
    }

    private void SelectCompletion(int index)
    {
        if (_completions is null || _completions.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _completions.Count - 1);

        for (var i = 0; i < _completions.Count; i++)
        {
            _completions[i].IsSelected = i == index;
        }

        _selectedIndex = index;
        CompletionList.ScrollIntoView(_completions[index]);
    }

    private void HideCompletions()
    {
        CompletionPopup.IsOpen = false;
        CompletionList.ItemsSource = null;
        _completions = null;
        _selectedIndex = -1;
    }

    private void AcceptCompletion()
    {
        if (_completions is null || _selectedIndex < 0)
        {
            return;
        }

        var item = _completions[_selectedIndex];
        var text = GetEditorText();
        var caret = Editor.Document.Selection.StartPosition;
        var (start, _, _) = CompletionEngine.ExtractContext(text, caret);

        _suppressEditorEvents = true;
        try
        {
            var selection = Editor.Document.Selection;
            selection.SetRange(start, caret);
            selection.Text = item.InsertText;

            var placeholder = item.Selection;
            if (placeholder is { } span)
            {
                selection.SetRange(start + span.Start, start + span.Start + span.Length);
            }
            else
            {
                var caretAt = item.CaretOffset >= 0
                    ? start + item.CaretOffset
                    : start + item.InsertText.Length;
                selection.SetRange(caretAt, caretAt);
            }
        }
        finally
        {
            _suppressEditorEvents = false;
        }

        var updated = GetEditorText();
        if (ViewModel.ActiveTab is not null)
        {
            ViewModel.ActiveTab.Content = updated;
        }

        ViewModel.UpdateCaret(updated, Editor.Document.Selection.StartPosition);
        HideCompletions();
        Editor.Focus(FocusState.Programmatic);
    }

    private void Editor_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // RichEditBox swallows Ctrl+Enter, so run from here rather than the page accelerator.
        if (e.Key is VirtualKey.Enter or VirtualKey.F5 && (e.Key == VirtualKey.F5 || IsKeyDown(VirtualKey.Control)))
        {
            HideCompletions();
            ViewModel.RunCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (CompletionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case VirtualKey.Tab:
                case VirtualKey.Enter:
                    AcceptCompletion();
                    e.Handled = true;
                    return;
                case VirtualKey.Down:
                    SelectCompletion(_selectedIndex + 1);
                    e.Handled = true;
                    return;
                case VirtualKey.Up:
                    SelectCompletion(_selectedIndex - 1);
                    e.Handled = true;
                    return;
                case VirtualKey.Escape:
                    HideCompletions();
                    e.Handled = true;
                    return;
            }

            return;
        }

        // Shell shortcuts must win over RichEditBox's own handling (Ctrl+B is bold in rich edit).
        if (IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.Shift) && !IsKeyDown(VirtualKey.Menu))
        {
            switch (e.Key)
            {
                case VirtualKey.B:
                    ViewModel.ToggleSidebarCommand.Execute(null);
                    e.Handled = true;
                    return;
                case VirtualKey.T:
                    ViewModel.NewTabCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        // Tab keeps focus in the editor and indents; completion accept owns Tab while open.
        if (e.Key == VirtualKey.Tab)
        {
            _suppressEditorEvents = true;
            try
            {
                Editor.Document.Selection.Text = "    ";
            }
            finally
            {
                _suppressEditorEvents = false;
            }

            var text = GetEditorText();
            if (ViewModel.ActiveTab is not null)
            {
                ViewModel.ActiveTab.Content = text;
            }

            ViewModel.UpdateCaret(text, Editor.Document.Selection.StartPosition);
            e.Handled = true;
        }
    }

    private void CompletionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CompletionItem item || _completions is null)
        {
            return;
        }

        var index = _completions.ToList().IndexOf(item);
        if (index >= 0)
        {
            SelectCompletion(index);
            AcceptCompletion();
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

        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(new TextBlock { Text = "Provider", FontSize = 11 });
        content.Children.Add(providerBox);
        content.Children.Add(fields);
        content.Children.Add(status);

        if (DatabaseProviderCatalog.LoadErrors.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Provider load problems: " + string.Join("; ", DatabaseProviderCatalog.LoadErrors),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = dangerBrush,
            });
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

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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
