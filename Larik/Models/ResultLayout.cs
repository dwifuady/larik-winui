using Microsoft.UI.Xaml.Media;

namespace Larik.Models;

/// <summary>Column geometry for the results grid; shared by every row of one result.</summary>
public sealed class ResultColumnLayout
{
    public required string Name { get; init; }

    /// <summary>Provider-reported type (e.g. "nvarchar"), shown in the cell preview.</summary>
    public string? DataType { get; init; }

    /// <summary>Changed live by the header resize handles; cached with the result.</summary>
    public double Width { get; set; }

    public bool Numeric { get; set; }
}

/// <summary>The column layout of one result. Rows reference it instead of copying cell metadata.</summary>
public sealed class ResultLayout
{
    public required IReadOnlyList<ResultColumnLayout> Columns { get; init; }

    public double TotalWidth => Columns.Sum(column => column.Width);
}

/// <summary>
/// One visible results row. Only rows the list actually realizes need a view, so these stay cheap
/// and are recreated when the active tab's result changes.
/// </summary>
public sealed class ResultRow
{
    public required int Index { get; init; }

    public required IReadOnlyList<string?> Cells { get; init; }

    public required ResultLayout Layout { get; init; }

    /// <summary>Optional row tint for reference-preview matches; null in the main results grid.</summary>
    public Brush? HighlightBrush { get; init; }

    /// <summary>Optional left-edge accent for reference-preview matches.</summary>
    public Brush? HighlightBorderBrush { get; init; }
}
