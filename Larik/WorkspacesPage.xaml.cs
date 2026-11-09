using System.Collections.Specialized;
using System.ComponentModel;
using Larik.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Larik;

/// <summary>
/// What the Workspaces page needs from the shell: the one live view model and the one workspace
/// editor. The editor lives on the shell page because it is the same dialog the rail and onboarding
/// open; the page only lends its XamlRoot so the dialog can be shown over the manager.
/// </summary>
public sealed record WorkspacesPageContext(
    StudioViewModel Studio,
    Func<SpaceViewModel?, XamlRoot, Task> EditWorkspace);

/// <summary>
/// The workspace collection: one ordered list with provider, endpoint and identity per row.
/// Switching stays in the rail and switcher; this page only maintains the collection.
/// </summary>
public sealed partial class WorkspacesPage : Page
{
    private StudioViewModel? _studio;
    private WorkspacesPageContext? _context;

    public WorkspacesPage()
    {
        InitializeComponent();

        // Returning to the studio keeps this page's view model and scroll position; the shell page
        // itself is cached for the same reason.
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += (_, _) => App.Window.SetTitleBar(WorkspacesTitleBar);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not WorkspacesPageContext context)
        {
            return;
        }

        if (_studio is not null)
        {
            _studio.Spaces.CollectionChanged -= Spaces_CollectionChanged;
            _studio.PropertyChanged -= Studio_PropertyChanged;
        }

        _context = context;
        _studio = context.Studio;
        WorkspaceList.ItemsSource = _studio.Spaces;
        _studio.Spaces.CollectionChanged += Spaces_CollectionChanged;
        _studio.PropertyChanged += Studio_PropertyChanged;

        RefreshSummary();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_studio is not null)
        {
            _studio.Spaces.CollectionChanged -= Spaces_CollectionChanged;
            _studio.PropertyChanged -= Studio_PropertyChanged;
        }
    }

    private void WorkspacesTitleBar_BackRequested(TitleBar sender, object args)
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

    private async void NewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null)
        {
            return;
        }

        await _context.EditWorkspace(null, XamlRoot);
        RefreshSummary();
    }

    private async void EditWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || (sender as FrameworkElement)?.DataContext is not SpaceViewModel space)
        {
            return;
        }

        await _context.EditWorkspace(space, XamlRoot);
        RefreshSummary();
    }

    private void ToggleVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (_studio is null || (sender as FrameworkElement)?.DataContext is not SpaceViewModel space)
        {
            return;
        }

        _studio.ToggleWorkspaceHidden(space);
        RefreshSummary();
    }

    private void Spaces_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSummary();

    private void Studio_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StudioViewModel.HiddenSpaceCount) or nameof(StudioViewModel.ActiveSpace))
        {
            RefreshSummary();
        }
    }

    private void RefreshSummary()
    {
        if (_studio is null)
        {
            return;
        }

        var total = _studio.Spaces.Count;
        var hidden = _studio.HiddenSpaceCount;
        var providers = _studio.Spaces
            .Select(space => space.ProviderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        SummaryText.Text = total == 0
            ? "No workspaces yet"
            : $"{total} workspace{(total == 1 ? string.Empty : "s")} · {providers} provider{(providers == 1 ? string.Empty : "s")}"
                + (hidden > 0 ? $" · {hidden} hidden from the rail" : string.Empty);

        WorkspaceList.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
