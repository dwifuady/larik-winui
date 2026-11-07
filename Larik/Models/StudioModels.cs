using CommunityToolkit.Mvvm.ComponentModel;
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
