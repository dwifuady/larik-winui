using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Larik.Models;

public enum CompletionKind
{
    Snippet,
    Keyword,
    Table,
    Column,
    Function,
    Join,
    Database,
    Variable,
    Cte,
}

/// <summary>A SQL snippet: a short trigger that expands to a statement.</summary>
public sealed class SnippetDef
{
    public required string Trigger { get; init; }
    public required string Name { get; init; }
    public required string Content { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }

    /// <summary>Null means every dialect.</summary>
    public string[]? Dialects { get; init; }
}

/// <summary>Database dialects a snippet can be scoped to.</summary>
public static class SnippetDialects
{
    /// <summary>The dialects the desktop app ships snippets for, plus room for new providers.</summary>
    public static IReadOnlyList<(string Id, string Label)> Known { get; } =
    [
        ("mssql", "SQL Server"),
        ("postgres", "PostgreSQL"),
    ];

    public static string Label(string id)
    {
        foreach (var dialect in Known)
        {
            if (dialect.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return dialect.Label;
            }
        }

        return id;
    }

    /// <summary>Normalizes a scope: known names, sorted, de-duplicated; empty becomes null (all).</summary>
    public static string[]? Normalize(IEnumerable<string>? dialects)
    {
        if (dialects is null)
        {
            return null;
        }

        var names = dialects
            .Where(dialect => !string.IsNullOrWhiteSpace(dialect))
            .Select(dialect => dialect.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(dialect => dialect, StringComparer.Ordinal)
            .ToArray();

        return names.Length == 0 ? null : names;
    }
}

/// <summary>
/// One stored snippet: a built-in from <see cref="Services.SnippetCatalog"/> or a user-defined row,
/// with the enabled state and dialect scope that decide whether completion offers it.
/// </summary>
public sealed record SnippetRecord
{
    public required string Id { get; init; }

    public required string Trigger { get; init; }

    public required string Name { get; init; }

    public required string Content { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    public bool IsBuiltin { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Null means every SQL dialect.</summary>
    public string[]? Dialects { get; init; }

    public string OriginLabel => IsBuiltin ? "BUILT-IN" : "CUSTOM";

    public Visibility DisabledVisibility => IsEnabled ? Visibility.Collapsed : Visibility.Visible;

    public double RowOpacity => IsEnabled ? 1 : 0.55;

    public string CategoryLabel => string.IsNullOrWhiteSpace(Category) ? "Other" : Category;

    public string ScopeLabel => Dialects is { Length: > 0 } dialects
        ? string.Join(", ", dialects.Select(SnippetDialects.Label))
        : "All SQL databases";

    /// <summary>First non-empty line, so the list can show what the trigger expands to.</summary>
    public string Preview
    {
        get
        {
            var line = Content.Split('\n', 2)[0].Trim();
            return line.Length > 0 ? line : Content;
        }
    }
}

/// <summary>One row in the completion popup.</summary>
public sealed partial class CompletionItem : ObservableObject
{
    private static readonly SolidColorBrush SelectedBrush = new(Color.FromArgb(0x40, 0x63, 0x66, 0xF1));

    public required string Label { get; init; }
    public required string Detail { get; init; }
    public required CompletionKind Kind { get; init; }
    public required string InsertText { get; init; }

    /// <summary>Caret offset inside <see cref="InsertText"/>, or -1 to land at the end.</summary>
    public int CaretOffset { get; init; } = -1;

    /// <summary>Placeholder span to leave selected after insertion.</summary>
    public (int Start, int Length)? Selection { get; init; }

    /// <summary>Secondary sort inside a kind (0 = exact trigger prefix).</summary>
    public int RankSort { get; init; }

    /// <summary>
    /// Sort key before <see cref="Rank"/>: 0 means the item matches what is being typed directly
    /// (a keyword/table prefix, a snippet trigger), higher values are weaker matches such as a
    /// snippet whose *name* merely contains the word. Keeps "wher" showing WHERE above a SNIP.
    /// </summary>
    public int MatchRank { get; init; }

    /// <summary>
    /// Tick state for multi-select in the completion popup: Space toggles a column, Tab/Enter
    /// inserts every ticked column. Only columns can be ticked; tables, keywords and joins keep
    /// the single-accept behaviour.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckGlyph))]
    [NotifyPropertyChangedFor(nameof(CheckOpacity))]
    public partial bool IsChecked { get; set; }

    public bool CanCheck => Kind == CompletionKind.Column;

    public string CheckGlyph => IsChecked ? "\uE73E" : "\uE739";

    public double CheckOpacity => CanCheck ? (IsChecked ? 1 : 0.35) : 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HighlightBrush))]
    public partial bool IsSelected { get; set; }

    public SolidColorBrush? HighlightBrush => IsSelected ? SelectedBrush : null;

    public string KindLabel => Kind switch
    {
        CompletionKind.Snippet => "SNIP",
        CompletionKind.Keyword => "KW",
        CompletionKind.Table => "TBL",
        CompletionKind.Column => "COL",
        CompletionKind.Function => "FN",
        CompletionKind.Database => "DB",
        CompletionKind.Variable => "VAR",
        CompletionKind.Cte => "CTE",
        _ => "JN",
    };

    /// <summary>Ordering bucket so joins and snippets lead, schema objects follow, keywords last.</summary>
    public int Rank => Kind switch
    {
        CompletionKind.Snippet => 0,
        CompletionKind.Join => 0,
        CompletionKind.Table => 1,
        CompletionKind.Database => 1,
        CompletionKind.Variable => 1,
        CompletionKind.Cte => 1,
        CompletionKind.Column => 2,
        CompletionKind.Function => 3,
        _ => 4,
    };
}
