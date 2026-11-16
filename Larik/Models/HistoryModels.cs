using System.Globalization;
using Larik.Services;
using Microsoft.UI.Xaml;

namespace Larik.Models;

/// <summary>Which half of the History page is showing. The halves own their own toolbars.</summary>
public enum HistorySegment
{
    Runs,
    Archived,
}

/// <summary>The one filter the Runs bar asks about, as a single choice.</summary>
public enum RunActorFilter
{
    All,
    You,
    Agent,
    Failed,
}

/// <summary>
/// One execution in the Runs list. The studio and the MCP server both produce rows; the actor is
/// the first difference the page shows, and an agent row may also carry why it ran and the work it
/// belongs to.
/// </summary>
public sealed class RunRowViewModel
{
    public RunRowViewModel(RunLogRecord record, bool tabExists, bool showWorkspace, string? spaceName = null)
    {
        Id = record.Id;
        Statement = record.Statement;
        SpaceId = record.SpaceId;
        SpaceName = spaceName ?? record.SpaceName ?? "Deleted workspace";
        DatabaseName = record.DatabaseName;
        TabId = record.TabId;
        Actor = record.Actor;
        Status = record.Status;
        RecordedAt = ParseTimestamp(record.RecordedAt);
        TabExists = tabExists;
        Description = record.Description;
        Reference = record.Reference;

        MetaLine = string.Join(
            " · ",
            new[]
            {
                showWorkspace ? SpaceName : null,
                string.IsNullOrWhiteSpace(record.DatabaseName) ? null : record.DatabaseName,
                DurationLabel(record.DurationMs),
                RowsLabel(record.RowsReturned, record.RowLimit, record.IsTruncated),
                string.IsNullOrWhiteSpace(record.Reason) ? null : record.Reason,
            }.Where(part => !string.IsNullOrEmpty(part)));
    }

    public long Id { get; }

    public string Statement { get; }

    public string? SpaceId { get; }

    public string SpaceName { get; }

    public string? DatabaseName { get; }

    public string? TabId { get; }

    public string Actor { get; }

    public string Status { get; }

    public DateTimeOffset RecordedAt { get; }

    /// <summary>True when the originating tab is still open, so the row offers "Go to tab".</summary>
    public bool TabExists { get; }

    /// <summary>Why the run happened, when the actor explained it. Agent prose, not instructions.</summary>
    public string? Description { get; }

    /// <summary>The ticket or incident the run belongs to, when one was named.</summary>
    public string? Reference { get; }

    /// <summary>The note line is present only when there is something to say.</summary>
    public Visibility NotesVisibility =>
        string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(Reference)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility DescriptionVisibility =>
        string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ReferenceVisibility =>
        string.IsNullOrWhiteSpace(Reference) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Workspace, database, duration, size and the reason a run failed - in that order.</summary>
    public string MetaLine { get; }

    public string TimeLabel => RecordedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    public string ActionLabel => TabExists ? "Go to tab" : "Open in new tab";

    public Visibility YouVisibility => Actor == RunActors.Agent ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AgentVisibility => Actor == RunActors.Agent ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OkVisibility => StatusVisibility(RunStatuses.Ok);

    public Visibility FailedVisibility => StatusVisibility(RunStatuses.Failed);

    public Visibility RefusedVisibility => StatusVisibility(RunStatuses.Refused);

    public Visibility CancelledVisibility => StatusVisibility(RunStatuses.Cancelled);

    /// <summary>One line for screen readers, which cannot see the chips and the colour.</summary>
    public string Announcement =>
        string.Join(
            ", ",
            new[]
            {
                $"{TimeLabel}, {SpaceName}, {(Actor == RunActors.Agent ? "agent" : "you")}, {Status}, {Statement}",
                string.IsNullOrWhiteSpace(Description) ? null : $"why: {Description}",
                string.IsNullOrWhiteSpace(Reference) ? null : $"ref: {Reference}",
            }.Where(part => !string.IsNullOrEmpty(part)));

    private Visibility StatusVisibility(string status) =>
        string.Equals(Status, status, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    private static string DurationLabel(long milliseconds) => milliseconds switch
    {
        < 1000 => $"{milliseconds} ms",
        < 60_000 => $"{milliseconds / 1000d:0.0} s",
        _ => $"{milliseconds / 60_000} m {milliseconds / 1000 % 60} s",
    };

    private static string RowsLabel(int rows, long? rowLimit, bool truncated)
    {
        if (truncated && rowLimit is { } limit)
        {
            return $"first {rows:N0} of {limit:N0} rows";
        }

        return rows switch
        {
            0 => "no rows",
            1 => "1 row",
            _ => $"{rows:N0} rows",
        };
    }

    /// <summary><c>recorded_at</c> is SQLite's UTC <c>datetime('now')</c> text, not an ISO stamp.</summary>
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTime.TryParseExact(
            value,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
}

/// <summary>One day's runs, newest day first. The list is day-grouped, not a flat feed.</summary>
public sealed class RunDayGroup
{
    public RunDayGroup(string label, IReadOnlyList<RunRowViewModel> runs)
    {
        Label = label;
        Runs = runs;
    }

    public string Label { get; }

    public IReadOnlyList<RunRowViewModel> Runs { get; }
}
