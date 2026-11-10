using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// A tab that is no longer in a workspace but kept in the archive/history page. Restoring it
/// recreates the original tab (content, database and pin state included) in its workspace.
/// </summary>
public sealed class ArchivedTabViewModel
{
    public ArchivedTabViewModel(
        string id,
        string spaceId,
        string spaceName,
        string colorHex,
        string title,
        string database,
        string content,
        bool pinned,
        DateTimeOffset archivedAt,
        string? groupName = null)
    {
        Id = id;
        SpaceId = spaceId;
        SpaceName = spaceName;
        ColorHex = colorHex;
        Title = title;
        Database = database;
        Content = content;
        IsPinned = pinned;
        ArchivedAt = archivedAt;
        GroupName = groupName;
    }

    public string Id { get; }

    public string SpaceId { get; }

    public string SpaceName { get; }

    public string ColorHex { get; }

    public string Title { get; }

    public string Database { get; }

    public string Content { get; }

    public bool IsPinned { get; }

    public DateTimeOffset ArchivedAt { get; }

    /// <summary>The group this tab belonged to, so restoring it returns it to the same place.</summary>
    public string? GroupName { get; }

    /// <summary>Workspace and database context, matching the tab subtitle style.</summary>
    public string ContextLine => string.IsNullOrWhiteSpace(Database)
        ? SpaceName
        : $"{SpaceName} · {Database}";

    /// <summary>First meaningful line of the saved SQL, so the archive reads like a history list.</summary>
    public string Preview
    {
        get
        {
            var line = Content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .FirstOrDefault(item => item.Length > 0);

            if (string.IsNullOrEmpty(line))
            {
                return "Empty query";
            }

            return line.Length > 140 ? line[..140] + "…" : line;
        }
    }

    public string AgeLabel
    {
        get
        {
            var age = DateTimeOffset.UtcNow - ArchivedAt;
            if (age < TimeSpan.FromMinutes(1))
            {
                return "just now";
            }

            if (age < TimeSpan.FromHours(1))
            {
                return $"{(int)age.TotalMinutes} min ago";
            }

            if (age < TimeSpan.FromDays(1))
            {
                return $"{(int)age.TotalHours} h ago";
            }

            if (age < TimeSpan.FromDays(30))
            {
                return $"{(int)age.TotalDays} d ago";
            }

            return ArchivedAt.ToLocalTime().ToString("MMM d, yyyy");
        }
    }

    public SolidColorBrush AccentBrush => StudioColors.Brush(ColorHex);

    public Visibility PinVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public string RestoreName => $"Restore {Title}";

    public string DeleteName => $"Delete {Title}";
}
