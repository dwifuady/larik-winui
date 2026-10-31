using Larik.Models;
using Larik.Services;
using Larik.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
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

        SyncEditorFromActiveTab();
        RenderResults();
        Editor.Focus(FocusState.Programmatic);
    }

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

    private void SpaceDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpaceViewModel space })
        {
            ViewModel.SelectSpace(space);
        }
    }

    private void SpaceHeader_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        foreach (var space in ViewModel.Spaces)
        {
            var item = new MenuFlyoutItem
            {
                Text = space.Name,
                Icon = new FontIcon { Glyph = "\uE8B7", Foreground = space.AccentBrush },
            };

            item.Click += (_, _) => ViewModel.SelectSpace(space);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var newSpace = new MenuFlyoutItem
        {
            Text = "New space",
            Icon = new FontIcon { Glyph = "\uE710" },
        };
        newSpace.Click += (_, _) => ViewModel.NewSpaceCommand.Execute(null);
        flyout.Items.Add(newSpace);

        flyout.ShowAt(SpaceHeaderButton, new Point(12, 48));
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

        if (result.Columns.Length == 0 || result.Rows.Count == 0)
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
        for (var c = 0; c < columns.Length; c++)
        {
            var numeric = result.Rows.All(r => double.TryParse(r[c], out _));
            ResultsHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(c == 0 ? 72 : 176),
                MinWidth = 72,
            });

            ResultsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = columns[c],
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

            for (var c = 0; c < columns.Length; c++)
            {
                var numeric = columns.Length > 0 && result.Rows.All(row => double.TryParse(row[c], out _));
                var cell = new TextBlock
                {
                    Text = result.Rows[r][c],
                    FontFamily = (FontFamily)Application.Current.Resources["StudioMonoFont"],
                    FontSize = 12,
                    Padding = new Thickness(12, 5, 12, 5),
                    Foreground = c == 0 ? secondaryBrush : primaryBrush,
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
        Grid.SetColumnSpan(rule, columns.Length);
        Grid.SetRow(rule, 0);
        ResultsHost.Children.Add(rule);
    }

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
