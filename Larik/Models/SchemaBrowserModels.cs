using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Data;
using Larik.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace Larik.Models;

/// <summary>The kind of line in the schema browser: a group heading, a table/view, a column or a routine.</summary>
public enum SchemaRowKind
{
    Group,
    Table,
    Column,
    Routine,
}

/// <summary>Stable identity for a table, shared by the browser and its recent-object list.</summary>
public static class SchemaKeys
{
    public static string For(TableSchema table) =>
        $"{table.Schema ?? string.Empty}\u001F{table.Name}";

    public static string For(TableSchema table, string column) =>
        $"{For(table)}\u001F{column}";

    public static string For(RoutineSchema routine) =>
        $"{routine.Schema ?? string.Empty}\u001F{routine.Name}\u001Froutine";
}

/// <summary>
/// What the user picked in the browser: a table/view, one of its columns, or a routine.
/// Recents only track tables and views, so <see cref="Routine"/> is never stored.
/// </summary>
public readonly record struct SchemaSelection(TableSchema? Table, string? Column, RoutineSchema? Routine = null)
{
    public static SchemaSelection ForRoutine(RoutineSchema routine) => new(null, null, routine);
}

/// <summary>One line in the schema browser's flat list (group, table or column).</summary>
public sealed partial class SchemaRow : ObservableObject
{
    private static readonly SolidColorBrush SelectedBrush = new(Color.FromArgb(0x38, 0x63, 0x66, 0xF1));

    public required SchemaRowKind Kind { get; init; }

    public required string Label { get; init; }

    public required string Key { get; init; }

    /// <summary>Right-hand text: a count for groups, the column's parent for search hits.</summary>
    public string? Meta { get; init; }

    public TableSchema? Table { get; init; }

    public string? Column { get; init; }

    public RoutineSchema? Routine { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HighlightBrush))]
    public partial bool IsSelected { get; set; }

    public bool IsSelectable => Kind != SchemaRowKind.Group;

    public string DisplayLabel => Kind == SchemaRowKind.Group ? Label.ToUpperInvariant() : Label;

    public double RowHeight => Kind == SchemaRowKind.Group ? 24 : 30;

    public double LabelSize => Kind == SchemaRowKind.Group ? 9.5 : 12;

    public FontWeight LabelWeight => Kind == SchemaRowKind.Group ? FontWeights.SemiBold : FontWeights.Normal;

    public Brush LabelBrush => Kind == SchemaRowKind.Group
        ? ThemeService.GetBrush("StudioTextMutedBrush", StudioColors.Muted)
        : ThemeService.GetBrush("StudioTextPrimaryBrush", StudioColors.Muted);

    public Visibility TableGlyphVisibility =>
        Kind == SchemaRowKind.Table && Table?.Kind != TableKind.View
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ViewGlyphVisibility =>
        Kind == SchemaRowKind.Table && Table?.Kind == TableKind.View
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility RoutineGlyphVisibility =>
        Kind == SchemaRowKind.Routine ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ColumnGlyphVisibility => Kind == SchemaRowKind.Column ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MetaVisibility =>
        string.IsNullOrWhiteSpace(Meta) ? Visibility.Collapsed : Visibility.Visible;

    public Brush? HighlightBrush => IsSelected ? SelectedBrush : null;
}

/// <summary>One column (or routine parameter) line in the detail: name, type and key role.</summary>
public sealed class SchemaColumnRow
{
    public required string Name { get; init; }

    public string? DataType { get; init; }

    public string? Key { get; init; }

    public string? KeyTooltip { get; init; }

    public Visibility TypeVisibility => string.IsNullOrWhiteSpace(DataType) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility KeyVisibility => Key is null ? Visibility.Collapsed : Visibility.Visible;

    public Brush KeyBrush => Key switch
    {
        "PK" => ThemeService.GetBrush("StudioWarningBrush", StudioColors.Warning),
        "FK" => ThemeService.GetBrush("StudioSyntaxFunctionBrush", StudioColors.Muted),
        _ => ThemeService.GetBrush("StudioTextMutedBrush", StudioColors.Muted),
    };
}
