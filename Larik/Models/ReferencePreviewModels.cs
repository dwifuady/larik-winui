using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Larik.Models;

/// <summary>One record in the vertical reference view: all columns, with a match highlight.</summary>
public sealed class ReferenceRowItem
{
    public required IReadOnlyList<ReferenceCellItem> Cells { get; init; }

    public required bool IsMatch { get; init; }

    public required Brush Background { get; init; }

    public required Brush BorderBrush { get; init; }

    public Visibility MatchVisibility => IsMatch ? Visibility.Visible : Visibility.Collapsed;

    public Thickness BorderThickness => IsMatch
        ? new Thickness(3, 0, 0, 1)
        : new Thickness(0, 0, 0, 1);
}

/// <summary>One column of a reference record: its name and formatted value.</summary>
public sealed class ReferenceCellItem
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public required bool IsNull { get; init; }

    public Brush ValueBrush => IsNull
        ? ThemeService.GetBrush("StudioTextMutedBrush", StudioColors.Muted)
        : ThemeService.GetBrush("StudioTextPrimaryBrush", StudioColors.Muted);
}
