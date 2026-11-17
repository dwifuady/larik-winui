using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Larik.Services;

/// <summary>
/// Notes are stored beside the workspace session but outside it: a note is an object with an
/// anchor, not a column on a tab. The table is loaded per tab, so switching tabs costs one indexed
/// read and a reload never has to parse the tab list to find annotations.
/// <para>
/// Notes are intentionally not foreign-keyed to <c>tabs</c>. Archiving a tab removes its row from
/// <c>tabs</c> while keeping the text in <c>archived_tabs</c>, and a cascading delete would take
/// the annotations with it. <see cref="Save"/> prunes notes whose tab is gone from both tables, so
/// archiving keeps them and deleting an archived tab removes them.
/// </para>
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>Every note for one tab, in document order. Lost anchors sort by their last position.</summary>
    public IReadOnlyList<NoteRecord> GetNotes(string tabId) =>
        string.IsNullOrEmpty(tabId) ? [] : ReadNotes("tab_id = $value", tabId);

    /// <summary>Every note for a workspace, for the pane's "all notes" scope and the startup load.</summary>
    public IReadOnlyList<NoteRecord> GetNotesForSpace(string spaceId) =>
        string.IsNullOrEmpty(spaceId) ? [] : ReadNotes("space_id = $value", spaceId);

    private IReadOnlyList<NoteRecord> ReadNotes(string predicate, string value)
    {
        var notes = new List<NoteRecord>();

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT id, space_id, tab_id, kind, body,
                       anchor_start, anchor_length, quote, prefix, suffix,
                       line_hint, is_unanchored, is_tab, created_at, updated_at
                FROM notes
                WHERE {predicate}
                ORDER BY tab_id, anchor_start, created_at;
                """;
            command.Parameters.AddWithValue("$value", value);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                notes.Add(new NoteRecord
                {
                    Id = reader.GetString(0),
                    SpaceId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    TabId = reader.GetString(2),
                    Kind = NoteKinds.Parse(reader.IsDBNull(3) ? null : reader.GetString(3)),
                    Body = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Anchor = new NoteAnchor(
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                        reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                        reader.GetInt32(10),
                        reader.GetInt32(12) != 0),
                    IsUnanchored = reader.GetInt32(11) != 0,
                    CreatedAt = ParseTimestamp(reader, 13),
                    UpdatedAt = ParseTimestamp(reader, 14),
                });
            }
        }
        catch
        {
            // Notes must never take the studio down; an unreadable table is an empty list.
        }

        return notes;
    }

    /// <summary>Insert or update one note. Returns false when the write failed.</summary>
    public bool SaveNote(NoteRecord note)
    {
        if (string.IsNullOrWhiteSpace(note.Id) || string.IsNullOrWhiteSpace(note.TabId))
        {
            return false;
        }

        try
        {
            using var connection = Open();
            Execute(
                connection,
                null,
                """
                INSERT INTO notes
                    (id, space_id, tab_id, kind, body,
                     anchor_start, anchor_length, quote, prefix, suffix,
                     line_hint, is_unanchored, is_tab, created_at, updated_at)
                VALUES
                    ($id, $space, $tab, $kind, $body,
                     $start, $length, $quote, $prefix, $suffix,
                     $line, $lost, $tabnote, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    space_id = excluded.space_id,
                    tab_id = excluded.tab_id,
                    kind = excluded.kind,
                    body = excluded.body,
                    anchor_start = excluded.anchor_start,
                    anchor_length = excluded.anchor_length,
                    quote = excluded.quote,
                    prefix = excluded.prefix,
                    suffix = excluded.suffix,
                    line_hint = excluded.line_hint,
                    is_unanchored = excluded.is_unanchored,
                    is_tab = excluded.is_tab,
                    updated_at = excluded.updated_at;
                """,
                ("$id", note.Id),
                ("$space", note.SpaceId),
                ("$tab", note.TabId),
                ("$kind", NoteKinds.ToStorage(note.Kind)),
                ("$body", note.Body),
                ("$start", note.Anchor.Start),
                ("$length", note.Anchor.Length),
                ("$quote", note.Anchor.Quote),
                ("$prefix", note.Anchor.Prefix),
                ("$suffix", note.Anchor.Suffix),
                ("$line", note.Anchor.LineHint),
                ("$lost", note.IsUnanchored ? 1 : 0),
                ("$tabnote", note.Anchor.IsTab ? 1 : 0),
                ("$created", note.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
                ("$updated", note.UpdatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes one note. Idempotent: a missing row is not an error.</summary>
    public bool DeleteNote(string noteId)
    {
        if (string.IsNullOrWhiteSpace(noteId))
        {
            return false;
        }

        try
        {
            using var connection = Open();
            Execute(connection, null, "DELETE FROM notes WHERE id = $id;", ("$id", noteId));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes every note belonging to a tab (permanent deletion, not archiving).</summary>
    public bool DeleteNotesForTab(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return false;
        }

        try
        {
            using var connection = Open();
            Execute(connection, null, "DELETE FROM notes WHERE tab_id = $tab;", ("$tab", tabId));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes every note for a set of tabs in one transaction (clearing the archive).</summary>
    public bool DeleteNotesForTabs(IEnumerable<string> tabIds)
    {
        var ids = tabIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return true;
        }

        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
            {
                Execute(connection, transaction, "DELETE FROM notes WHERE tab_id = $tab;", ("$tab", id));
            }

            transaction.Commit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DateTimeOffset.UtcNow;
        }

        return DateTimeOffset.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : DateTimeOffset.UtcNow;
    }
}
