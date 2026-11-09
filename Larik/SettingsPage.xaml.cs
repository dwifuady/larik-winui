using System.Collections.ObjectModel;
using System.Reflection;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace Larik;

/// <summary>
/// Application settings. The only section today is the snippet manager: every built-in and custom
/// snippet, with the trigger, scope and expansion that completion uses. Edits go through
/// <see cref="SnippetLibrary"/>, so the next keystroke in the editor sees them.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SnippetLibrary _snippets = SnippetLibrary.Current;
    private readonly ObservableCollection<SnippetRecord> _visible = [];
    private readonly HashSet<string> _editorDialects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToggleButton> _dialectButtons = new(StringComparer.OrdinalIgnoreCase);

    private ToggleButton? _allDialectsButton;
    private string? _editingId;
    private bool _creating;
    private bool _loadingEditor;
    private bool _suppressSelection;
    private bool _suppressCategory;
    private string _search = string.Empty;
    private string? _category;

    public SettingsPage()
    {
        InitializeComponent();

        NavigationCacheMode = NavigationCacheMode.Required;
        SnippetList.ItemsSource = _visible;
        Loaded += SettingsPage_Loaded;
    }

    // ---------------------------------------------------------------- page lifecycle

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Window.SetTitleBar(SettingsTitleBar);
        DatabasePathText.Text = LarikPaths.DatabasePath;

        var version = typeof(SettingsPage).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = $"Version {(string.IsNullOrEmpty(version) ? "0.0.0" : version.Split('+')[0])} · MIT licensed";

        if (DialectChips.Children.Count == 0)
        {
            BuildDialectChips();
        }

        if (SettingsNav.SelectedItem is null)
        {
            SettingsNav.SelectedItem = SnippetsNavItem;
        }

        RefreshCategories();
        RefreshList();
    }

    private void SettingsTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(MainPage));
        }
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (SnippetsSection is null || AboutSection is null)
        {
            return;
        }

        var about = string.Equals(
            (args.SelectedItemContainer as NavigationViewItem)?.Tag as string,
            "about",
            StringComparison.Ordinal);

        SnippetsSection.Visibility = about ? Visibility.Collapsed : Visibility.Visible;
        AboutSection.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- filtering

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? string.Empty;
        RefreshList();
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCategory)
        {
            return;
        }

        _category = (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        RefreshList();
    }

    private void RefreshCategories()
    {
        _suppressCategory = true;
        try
        {
            var categories = _snippets.All
                .Select(snippet => snippet.CategoryLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CategoryFilter.Items.Clear();
            CategoryFilter.Items.Add(new ComboBoxItem { Content = "All categories", Tag = null });
            foreach (var category in categories)
            {
                CategoryFilter.Items.Add(new ComboBoxItem { Content = category, Tag = category });
            }

            if (_category is not null)
            {
                var match = CategoryFilter.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, _category, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    CategoryFilter.SelectedItem = match;
                    return;
                }

                // The filter's category disappeared (the last snippet in it was renamed or deleted):
                // the combo now says "All categories", so the filter must agree.
                _category = null;
            }

            CategoryFilter.SelectedIndex = 0;
        }
        finally
        {
            _suppressCategory = false;
        }
    }

    private void RefreshList()
    {
        IEnumerable<SnippetRecord> matches = _snippets.All;

        if (_search.Length > 0)
        {
            matches = matches.Where(snippet =>
                snippet.Trigger.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                snippet.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                snippet.Content.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                (snippet.Description?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (_category is not null)
        {
            matches = matches.Where(snippet =>
                string.Equals(snippet.CategoryLabel, _category, StringComparison.OrdinalIgnoreCase));
        }

        var items = matches.ToList();
        var selectedId = _editingId;

        _suppressSelection = true;
        try
        {
            _visible.Clear();
            foreach (var snippet in items)
            {
                _visible.Add(snippet);
            }

            SnippetList.SelectedItem = selectedId is null
                ? null
                : _visible.FirstOrDefault(snippet => string.Equals(snippet.Id, selectedId, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelection = false;
        }

        EmptyListText.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = _snippets.All.Count;
        var builtins = _snippets.All.Count(snippet => snippet.IsBuiltin);
        SnippetCountText.Text = _visible.Count == total
            ? $"{total} snippets · {builtins} built-in · {total - builtins} custom"
            : $"{_visible.Count} of {total} snippets · {builtins} built-in · {total - builtins} custom";
    }

    // ---------------------------------------------------------------- selection

    private void SnippetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (SnippetList.SelectedItem is SnippetRecord snippet)
        {
            LoadEditor(snippet);
        }
        else
        {
            _editingId = null;
            _creating = false;
            HideEditor();
        }
    }

    private void LoadEditor(SnippetRecord snippet)
    {
        _creating = false;
        _editingId = snippet.Id;

        _loadingEditor = true;
        try
        {
            TriggerBox.Text = snippet.Trigger;
            NameBox.Text = snippet.Name;
            CategoryBox.Text = snippet.Category ?? string.Empty;
            DescriptionBox.Text = snippet.Description ?? string.Empty;
            ContentBox.Text = snippet.Content;
            EnabledToggle.IsOn = snippet.IsEnabled;

            _editorDialects.Clear();
            foreach (var dialect in snippet.Dialects ?? [])
            {
                _editorDialects.Add(dialect);
            }

            ApplyDialectChips();
        }
        finally
        {
            _loadingEditor = false;
        }

        EditorHeading.Text = "Edit snippet";
        EditorSubheading.Text = snippet.IsBuiltin
            ? "Built-in snippet — you can edit it, but not delete it."
            : "Custom snippet";
        SaveButton.Content = "Save changes";
        ResetButton.Visibility = snippet.IsBuiltin ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = snippet.IsBuiltin ? Visibility.Collapsed : Visibility.Visible;

        ShowEditor();
    }

    private void NewSnippet_Click(object sender, RoutedEventArgs e)
    {
        _creating = true;
        _editingId = null;

        _suppressSelection = true;
        try
        {
            SnippetList.SelectedItem = null;
        }
        finally
        {
            _suppressSelection = false;
        }

        _loadingEditor = true;
        try
        {
            TriggerBox.Text = string.Empty;
            NameBox.Text = string.Empty;
            CategoryBox.Text = string.Empty;
            DescriptionBox.Text = string.Empty;
            ContentBox.Text = string.Empty;
            EnabledToggle.IsOn = true;

            _editorDialects.Clear();
            ApplyDialectChips();
        }
        finally
        {
            _loadingEditor = false;
        }

        EditorHeading.Text = "New snippet";
        EditorSubheading.Text = "Type the trigger in the editor and press Tab to expand.";
        SaveButton.Content = "Create snippet";
        ResetButton.Visibility = Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Collapsed;

        ShowEditor();
        TriggerBox.Focus(FocusState.Programmatic);
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _creating = false;
        _editingId = null;

        _suppressSelection = true;
        try
        {
            SnippetList.SelectedItem = null;
        }
        finally
        {
            _suppressSelection = false;
        }

        HideEditor();
    }

    private void HideEditor()
    {
        EditorPanel.Visibility = Visibility.Collapsed;
        EditorEmptyText.Visibility = Visibility.Visible;
        EditorError.Visibility = Visibility.Collapsed;
    }

    private void ShowEditor()
    {
        EditorPanel.Visibility = Visibility.Visible;
        EditorEmptyText.Visibility = Visibility.Collapsed;
        EditorError.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- save + delete

    private void SaveSnippet_Click(object sender, RoutedEventArgs e)
    {
        var trigger = TriggerBox.Text.Trim();
        var name = NameBox.Text.Trim();
        var content = ContentBox.Text ?? string.Empty;

        if (trigger.Length == 0)
        {
            ShowError("A trigger is required.");
            TriggerBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!trigger.All(character => char.IsLetterOrDigit(character) || character == '_'))
        {
            ShowError("Triggers can only contain letters, digits and underscores.");
            TriggerBox.Focus(FocusState.Programmatic);
            return;
        }

        if (name.Length == 0)
        {
            ShowError("A name is required.");
            NameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            ShowError("Snippet content is required.");
            ContentBox.Focus(FocusState.Programmatic);
            return;
        }

        var dialects = _editorDialects.ToArray();
        if (_snippets.HasTriggerConflict(trigger, dialects, _editingId))
        {
            ShowError($"The trigger \"{trigger}\" already exists for this database scope.");
            TriggerBox.Focus(FocusState.Programmatic);
            return;
        }

        try
        {
            var creating = _creating || _editingId is null;
            var saved = creating
                ? _snippets.Create(trigger, name, content, DescriptionBox.Text, CategoryBox.Text, dialects)
                : _snippets.Update(_editingId!, trigger, name, content, DescriptionBox.Text, CategoryBox.Text, dialects);

            _creating = false;
            _editingId = saved.Id;

            // A new snippet must be visible after saving, even if the current filters would hide it.
            if (creating)
            {
                _search = string.Empty;
                _category = null;
                SearchBox.Text = string.Empty;
            }

            RefreshCategories();
            RefreshList();
            LoadEditor(saved);
        }
        catch (Exception ex)
        {
            ShowError($"Could not save the snippet: {ex.Message}");
        }
    }

    private async void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId is not { } id || _snippets.Find(id) is not { } snippet || snippet.IsBuiltin)
        {
            return;
        }

        if (!await ConfirmAsync("Delete snippet", $"Delete \"{snippet.Name}\"?", "Delete"))
        {
            return;
        }

        try
        {
            if (_snippets.Delete(id))
            {
                _editingId = null;
                _creating = false;
                RefreshCategories();
                RefreshList();
                HideEditor();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Could not delete the snippet: {ex.Message}");
        }
    }

    private async void ResetSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId is not { } id || _snippets.Find(id) is not { } snippet || !snippet.IsBuiltin)
        {
            return;
        }

        if (!await ConfirmAsync("Reset snippet", $"Reset \"{snippet.Name}\" to its built-in default?", "Reset"))
        {
            return;
        }

        try
        {
            var reset = _snippets.ResetBuiltin(id);
            if (reset is null)
            {
                ShowError("This built-in snippet has no catalog default any more.");
                return;
            }

            RefreshList();
            LoadEditor(reset);
        }
        catch (Exception ex)
        {
            ShowError($"Could not reset the snippet: {ex.Message}");
        }
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || _creating || _editingId is not { } id)
        {
            return;
        }

        try
        {
            _snippets.SetEnabled(id, EnabledToggle.IsOn);
            RefreshList();
        }
        catch (Exception ex)
        {
            ShowError($"Could not update the snippet: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        EditorError.Text = message;
        EditorError.Visibility = Visibility.Visible;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            XamlRoot = XamlRoot,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---------------------------------------------------------------- dialect chips

    private void BuildDialectChips()
    {
        DialectChips.Children.Clear();
        _dialectButtons.Clear();

        _allDialectsButton = CreateDialectChip("All SQL databases", null);
        DialectChips.Children.Add(_allDialectsButton);

        foreach (var (id, label) in SnippetDialects.Known)
        {
            var button = CreateDialectChip(label, id);
            _dialectButtons[id] = button;
            DialectChips.Children.Add(button);
        }

        ApplyDialectChips();
    }

    private ToggleButton CreateDialectChip(string label, string? dialect)
    {
        var button = new ToggleButton
        {
            Content = label,
            FontSize = 11,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
        };

        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => ToggleDialect(dialect, button);
        return button;
    }

    private void ToggleDialect(string? dialect, ToggleButton button)
    {
        if (_loadingEditor)
        {
            return;
        }

        if (dialect is null)
        {
            _editorDialects.Clear();
        }
        else if (button.IsChecked == true)
        {
            _editorDialects.Add(dialect);
        }
        else
        {
            _editorDialects.Remove(dialect);
        }

        ApplyDialectChips();
    }

    private void ApplyDialectChips()
    {
        if (_allDialectsButton is not null)
        {
            _allDialectsButton.IsChecked = _editorDialects.Count == 0;
        }

        foreach (var (dialect, button) in _dialectButtons)
        {
            button.IsChecked = _editorDialects.Contains(dialect);
        }
    }

    // ---------------------------------------------------------------- placeholder helpers

    private void InsertCursor_Click(object sender, RoutedEventArgs e) => InsertAtCursor("${cursor}");

    private void InsertPlaceholder_Click(object sender, RoutedEventArgs e) => InsertAtCursor("${1:placeholder}");

    private void InsertPaste_Click(object sender, RoutedEventArgs e) => InsertAtCursor("${paste}");

    private void InsertAtCursor(string text)
    {
        var content = ContentBox.Text ?? string.Empty;
        var start = Math.Clamp(ContentBox.SelectionStart, 0, content.Length);
        var length = Math.Clamp(ContentBox.SelectionLength, 0, content.Length - start);

        ContentBox.Text = content[..start] + text + content[(start + length)..];
        ContentBox.SelectionStart = start + text.Length;
        ContentBox.SelectionLength = 0;
        ContentBox.Focus(FocusState.Programmatic);
    }
}
