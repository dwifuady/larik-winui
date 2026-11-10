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
    public ResultSetViewModel(TabViewModel tab, QueryResult result, int index)
    {
        Tab = tab;
        Result = result;
        Index = index;
    }

    public TabViewModel Tab { get; }

    public QueryResult Result { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(CloseName))]
    public partial int Index { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabBackground))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    [NotifyPropertyChangedFor(nameof(CloseOpacity))]
    [NotifyPropertyChangedFor(nameof(ActiveStripOpacity))]
    public partial bool IsActive { get; set; }

    public string Title => Result.Messages.Count > 0 ? "Messages" : $"Result {Index + 1}";

    public string CloseName => $"Close {Title}";

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
        ? StudioColors.Danger
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

    /// <summary>Code-resolved brushes do not follow a runtime theme switch on their own.</summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(DetailBrush));
        OnPropertyChanged(nameof(TitleBrush));
    }

    /// <summary>Inactive tabs keep their close affordance, but quiet until the tab is chosen.</summary>
    public double CloseOpacity => IsActive ? 1 : 0.45;

    public double ActiveStripOpacity => IsActive ? 1 : 0;

    public SolidColorBrush AccentBrush => Tab.Space.AccentBrush;
}
