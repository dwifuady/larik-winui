using System.Globalization;

namespace Larik.Services;

/// <summary>A workspace icon: a stable name to persist, the Fluent glyph, and the words the picker searches.</summary>
public sealed record SpaceIcon(string Name, string Glyph, string Label, string Group);

/// <summary>
/// The curated set a workspace can wear. Names are persisted rather than code points, so the artwork
/// can be re-mapped later without touching stored data. The set is deliberately small and spread
/// across meanings: a glyph at 15 px only reads as different from its neighbours when the shapes are
/// genuinely different, which is the whole reason the footer stopped using coloured dots.
/// </summary>
public static class SpaceIcons
{
    public static IReadOnlyList<SpaceIcon> All { get; } =
    [
        // Data — what a warehouse or reporting workspace actually means.
        new("data", "\uE791", "Data", "Data"),
        new("report", "\uE9F9", "Report", "Data"),
        new("trend", "\uEB11", "Trend", "Data"),
        new("pie", "\uEB05", "Pie chart", "Data"),
        new("equalizer", "\uE9E9", "Equalizer", "Data"),
        new("bank", "\uE825", "Bank", "Data"),

        // Work — documents, sources and the things that hold them.
        new("folder", "\uE8B7", "Folder", "Work"),
        new("document", "\uE8A5", "Document", "Work"),
        new("library", "\uE8F1", "Library", "Work"),
        new("book", "\uE82D", "Book", "Work"),
        new("package", "\uE7B8", "Package", "Work"),
        new("calculator", "\uE8EF", "Calculator", "Work"),

        // Dev — the shapes a query workspace usually means.
        new("code", "\uE943", "Code", "Dev"),
        new("terminal", "\uE756", "Terminal", "Dev"),
        new("network", "\uE968", "Network", "Dev"),
        new("flow", "\uEF90", "Flow", "Dev"),
        new("beaker", "\uF196", "Beaker", "Dev"),
        new("diagnostic", "\uE9D9", "Diagnostic", "Dev"),

        // People — ownership and coordination.
        new("person", "\uE77B", "Person", "People"),
        new("people", "\uE716", "People", "People"),
        new("mail", "\uE715", "Mail", "People"),
        new("calendar", "\uE787", "Calendar", "People"),
        new("megaphone", "\uE789", "Announcement", "People"),

        // Places — where the data lives.
        new("globe", "\uE774", "Globe", "Places"),
        new("world", "\uE909", "World", "Places"),
        new("cloud", "\uE753", "Cloud", "Places"),
        new("home", "\uE80F", "Home", "Places"),
        new("location", "\uE707", "Location", "Places"),

        // Marks — the small vocabularies people already use for naming things.
        new("star", "\uE734", "Star", "Marks"),
        new("flag", "\uE7C1", "Flag", "Marks"),
        new("lock", "\uE72E", "Lock", "Marks"),
        new("tag", "\uE8EC", "Tag", "Marks"),
        new("bolt", "\uE945", "Bolt", "Marks"),
        new("bookmarks", "\uE8A4", "Bookmarks", "Marks"),
        new("history", "\uE81C", "History", "Marks"),
        new("filter", "\uE71C", "Filter", "Marks"),
        new("pin", "\uE718", "Pin", "Marks"),
    ];

    private static readonly Dictionary<string, SpaceIcon> ByName =
        All.ToDictionary(icon => icon.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The glyph to draw, or null when the workspace should fall back to its monogram.</summary>
    public static string? Glyph(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ByName.TryGetValue(name, out var icon) ? icon.Glyph : null;

    /// <summary>The label the picker announces, or the raw name when it is no longer in the catalog.</summary>
    public static string Label(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ByName.TryGetValue(name, out var icon) ? icon.Label : "No icon";

    /// <summary>
    /// The first letter of a workspace name, used when no icon is chosen. Text elements rather than
    /// chars, because a name may start with a surrogate pair.
    /// </summary>
    public static string Monogram(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? "?"
            : StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
    }

    /// <summary>Filters by label, group or name; an empty query keeps the whole set.</summary>
    public static IReadOnlyList<SpaceIcon> Search(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? All
            : All.Where(icon =>
                    icon.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || icon.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || icon.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
}
