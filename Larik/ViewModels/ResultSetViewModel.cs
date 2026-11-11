using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Data;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Larik.ViewModels;

/// <summary>One result tab inside a query tab. A batch can produce several of these.</summary>
public partial class ResultSetViewModel : ObservableObject
{
    /// <summary>
    /// <paramref name="title"/> is the tab's label at creation. It is deliberately stable rather
    /// than derived from the position, so reordering the strip never renumbers a result.
    /// </summary>
    public ResultSetViewModel(TabViewModel tab, QueryResult result, string title)
    {
        Tab = tab;
        Result = result;
        _title = title;
    }

    public TabViewModel Tab { get; }

    public QueryResult Result { get; }

    private string _title;

    /// <summary>The visible name, kept across reorders and used by the tab's automation name.</summary>
    public string Title => _title;

    public string CloseName => $"Close {Title}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleVisibility))]
    [NotifyPropertyChangedFor(nameof(RenameEditorVisibility))]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string DraftTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabBackground))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    [NotifyPropertyChangedFor(nameof(CloseOpacity))]
    [NotifyPropertyChangedFor(nameof(ActiveStripOpacity))]
    public partial bool IsActive { get; set; }

    public Visibility TitleVisibility => IsRenaming ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RenameEditorVisibility => IsRenaming ? Visibility.Visible : Visibility.Collapsed;

    public string Detail => Result.IsError
        ? "Error"
        : Result.Messages.Count > 0
            ? $"{Result.Messages.Count:N0} message{(Result.Messages.Count == 1 ? string.Empty : "s")}"
            : Result.Rows.Count switch
            {
                0 => Result.Message,
                1 => $"1 row · {Result.Elapsed.TotalMilliseconds:0} ms",
                var count => $"{count:N0} rows · {Result.Elapsed.TotalMilliseconds:0} ms",
            };

    public Brush DetailBrush => Result.IsError
        ? ThemeService.GetBrush("StudioDangerBrush", StudioColors.Danger)
        : ThemeService.GetBrush("StudioTextMutedBrush", StudioColors.Muted);

    /// <summary>Header text for the active result, including the row-cap notice.</summary>
    public string Message => Result.IsTruncated && Result.RowLimit is { } limit
        ? $"Showing first {limit:N0} rows"
        : Result.Message;

    public string Summary => Result is { IsError: false, Rows.Count: > 0 }
        ? $"{Result.Rows.Count} rows · {Result.Elapsed.TotalMilliseconds:0} ms"
        : string.Empty;

    public string Tooltip => Result.Messages.Count > 0
        ? string.Join(Environment.NewLine, Result.Messages)
        : Result.Message;

    public Brush? TabBackground => IsActive ? Tab.Space.ActiveTintBrush : null;

    public FontWeight TitleWeight => IsActive
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;

    public Brush TitleBrush => IsActive
        ? ThemeService.GetBrush("StudioTextPrimaryBrush", StudioColors.Muted)
        : ThemeService.GetBrush("StudioTextSecondaryBrush", StudioColors.Muted);

    /// <summary>Inactive tabs keep their close affordance, but quiet until the tab is chosen.</summary>
    public double CloseOpacity => IsActive ? 1 : 0.45;

    public double ActiveStripOpacity => IsActive ? 1 : 0;

    public SolidColorBrush AccentBrush => Tab.Space.AccentBrush;

    /// <summary>Code-resolved brushes do not follow a runtime theme switch on their own.</summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(DetailBrush));
        OnPropertyChanged(nameof(TitleBrush));
    }

    public void BeginRename()
    {
        DraftTitle = Title;
        IsRenaming = true;
    }

    public void EndRename() => IsRenaming = false;

    /// <summary>Applies a typed name. An empty name is ignored so a tab never loses its label.</summary>
    public bool Rename(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, _title, StringComparison.Ordinal))
        {
            return false;
        }

        _title = trimmed;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CloseName));
        return true;
    }
}
