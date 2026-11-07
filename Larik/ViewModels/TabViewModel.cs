using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Larik.ViewModels;

/// <summary>
/// One vertical tab in the sidebar. A tab owns its database context and its last result, so
/// switching tabs swaps the completion schema, database picker and results grid together.
/// </summary>
public partial class TabViewModel : ObservableObject
{
    private bool _isPointerOver;

    public TabViewModel(string id, string title, string? database, SpaceViewModel space, string content)
    {
        Id = id;
        Space = space;
        Title = title;
        Database = database ?? string.Empty;
        Content = content;

        // The workspace colour flows through to the tab strip and tint.
        space.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SpaceViewModel.ColorHex)
                or nameof(SpaceViewModel.AccentBrush)
                or nameof(SpaceViewModel.ActiveTintBrush))
            {
                OnPropertyChanged(nameof(AccentBrush));
                OnPropertyChanged(nameof(ActiveTintBrush));
            }
        };
    }

    public string Id { get; }

    public SpaceViewModel Space { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    public partial string Database { get; set; }

    [ObservableProperty]
    public partial string Content { get; set; }

    /// <summary>Result tabs produced by this query tab, in execution order.</summary>
    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

    private ResultSetViewModel? _activeResult;

    public ResultSetViewModel? ActiveResult => _activeResult;

    public QueryResult? Result => _activeResult?.Result;

    /// <summary>
    /// Replaces the active result tab when <paramref name="append"/> is false, or appends the new
    /// result tabs when it is true. A batch can produce several results at once.
    /// </summary>
    public void ApplyResults(IReadOnlyList<QueryResult> results, bool append)
    {
        if (results.Count == 0)
        {
            return;
        }

        var index = append || Results.Count == 0
            ? Results.Count
            : Math.Clamp(Results.IndexOf(_activeResult!), 0, Results.Count - 1);

        if (!append && Results.Count > 0)
        {
            Results.RemoveAt(index);
        }

        for (var i = 0; i < results.Count; i++)
        {
            Results.Insert(index + i, new ResultSetViewModel(this, results[i], index + i));
        }

        Reindex();
        SetActiveResult(Results[Math.Clamp(index, 0, Results.Count - 1)]);
    }

    public void SelectResult(ResultSetViewModel result)
    {
        if (Results.Contains(result))
        {
            SetActiveResult(result);
        }
    }

    public void CloseResult(ResultSetViewModel result)
    {
        var index = Results.IndexOf(result);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(_activeResult, result);
        Results.RemoveAt(index);
        Reindex();

        if (Results.Count == 0)
        {
            SetActiveResult(null);
        }
        else if (wasActive)
        {
            SetActiveResult(Results[Math.Clamp(index, 0, Results.Count - 1)]);
        }
    }

    private void SetActiveResult(ResultSetViewModel? result)
    {
        foreach (var item in Results)
        {
            item.IsActive = item == result;
        }

        if (!ReferenceEquals(_activeResult, result))
        {
            _activeResult = result;
            OnPropertyChanged(nameof(ActiveResult));
            OnPropertyChanged(nameof(Result));
        }
    }

    private void Reindex()
    {
        for (var index = 0; index < Results.Count; index++)
        {
            Results[index].Index = index;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTintBrush))]
    [NotifyPropertyChangedFor(nameof(ActiveStripOpacity))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    [NotifyPropertyChangedFor(nameof(CloseButtonVisibility))]
    public partial bool IsActive { get; set; }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (SetProperty(ref _isPointerOver, value))
            {
                OnPropertyChanged(nameof(SubtitleVisibility));
                OnPropertyChanged(nameof(CloseButtonVisibility));
            }
        }
    }

    public string Subtitle => string.IsNullOrWhiteSpace(Database) ? Space.Name : Database;

    /// <summary>Database label reveals on hover or when the tab is active (Arc behaviour).</summary>
    public Visibility SubtitleVisibility => IsActive || IsPointerOver ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloseButtonVisibility => IsActive || IsPointerOver ? Visibility.Visible : Visibility.Collapsed;

    public Brush? ActiveTintBrush => IsActive ? Space.ActiveTintBrush : null;

    public double ActiveStripOpacity => IsActive ? 1 : 0;

    public FontWeight TitleWeight => IsActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public SolidColorBrush AccentBrush => Space.AccentBrush;
}
