using Larik.Services;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Notes are stored outside the tab list but pruned by it: archiving keeps them, deleting the last
/// copy of the tab removes them. These pin the round trip and that lifecycle.
/// </summary>
public sealed class NoteStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"larik-notes-{Guid.NewGuid():N}.db");

    [Fact]
    public void Note_round_trips_with_its_anchor_and_kind()
    {
        var store = new StudioStore(_path);
        var anchor = NoteAnchoring.Capture("SELECT a FROM t;", 0, 16);

        Assert.True(store.SaveNote(new NoteRecord
        {
            Id = "note-1",
            SpaceId = "space-1",
            TabId = "tab-1",
            Kind = NoteKind.Decision,
            Body = "5,000 is the Finance threshold.",
            Anchor = anchor,
        }));

        var note = Assert.Single(store.GetNotes("tab-1"));
        Assert.Equal("note-1", note.Id);
        Assert.Equal("space-1", note.SpaceId);
        Assert.Equal(NoteKind.Decision, note.Kind);
        Assert.Equal("5,000 is the Finance threshold.", note.Body);
        Assert.Equal(anchor.Start, note.Anchor.Start);
        Assert.Equal(anchor.Length, note.Anchor.Length);
        Assert.Equal(anchor.Quote, note.Anchor.Quote);
        Assert.Equal(anchor.Prefix, note.Anchor.Prefix);
        Assert.Equal(anchor.Suffix, note.Anchor.Suffix);
        Assert.Equal(1, note.Anchor.LineHint);
        Assert.False(note.IsUnanchored);
    }

    [Fact]
    public void Tab_note_round_trips_without_an_anchor()
    {
        var store = new StudioStore(_path);

        store.SaveNote(new NoteRecord
        {
            Id = "note-tab",
            SpaceId = "space-1",
            TabId = "tab-1",
            Body = "Scratchpad for this query",
            Anchor = NoteAnchoring.TabNote(),
        });

        var note = Assert.Single(store.GetNotes("tab-1"));
        Assert.True(note.Anchor.IsTab);
        Assert.Equal(0, note.Anchor.Length);
        Assert.Equal(string.Empty, note.Anchor.Quote);
        Assert.Equal(0, note.Anchor.LineHint);
    }

    [Fact]
    public void Saving_again_updates_the_row_without_losing_creation_time()
    {
        var store = new StudioStore(_path);
        var created = new NoteRecord
        {
            Id = "note-1",
            TabId = "tab-1",
            Body = "first",
            Anchor = NoteAnchoring.Capture("SELECT 1;", 0, 9),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        };

        store.SaveNote(created);
        var before = Assert.Single(store.GetNotes("tab-1"));

        store.SaveNote(new NoteRecord
        {
            Id = "note-1",
            TabId = "tab-1",
            Kind = NoteKind.Todo,
            Body = "second",
            Anchor = NoteAnchoring.Capture("SELECT 1;", 0, 9),
            IsUnanchored = true,
            CreatedAt = created.CreatedAt,
        });

        var after = Assert.Single(store.GetNotes("tab-1"));
        Assert.Equal("second", after.Body);
        Assert.Equal(NoteKind.Todo, after.Kind);
        Assert.True(after.IsUnanchored);
        Assert.Equal(before.CreatedAt.ToUnixTimeSeconds(), after.CreatedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void Notes_are_scoped_per_tab_and_deleted_by_id()
    {
        var store = new StudioStore(_path);
        store.SaveNote(new NoteRecord
        {
            Id = "a",
            TabId = "tab-1",
            Anchor = NoteAnchoring.Capture("SELECT 1;", 0, 9),
        });
        store.SaveNote(new NoteRecord
        {
            Id = "b",
            TabId = "tab-2",
            Anchor = NoteAnchoring.Capture("SELECT 2;", 0, 9),
        });

        Assert.Single(store.GetNotes("tab-1"));
        Assert.Single(store.GetNotes("tab-2"));

        Assert.True(store.DeleteNote("a"));
        Assert.Empty(store.GetNotes("tab-1"));
        Assert.Single(store.GetNotes("tab-2"));
    }

    [Fact]
    public void Saving_the_session_keeps_notes_for_live_tabs_and_prunes_orphans()
    {
        var store = new StudioStore(_path);

        var state = new StudioState
        {
            ActiveSpaceId = "space-1",
            Spaces =
            [
                new SpaceState
                {
                    Id = "space-1",
                    Name = "Sales",
                    Tabs =
                    [
                        new TabState { Id = "tab-1", Title = "Query 1", Content = "SELECT 1;" },
                    ],
                },
            ],
        };

        store.Save(state);
        store.SaveNote(new NoteRecord
        {
            Id = "note-1",
            SpaceId = "space-1",
            TabId = "tab-1",
            Anchor = NoteAnchoring.Capture("SELECT 1;", 0, 9),
        });

        store.Save(state);
        Assert.Single(store.GetNotes("tab-1"));

        // The tab is gone from both the live list and the archive: the note goes with it.
        state.Spaces[0].Tabs.Clear();
        store.Save(state);
        Assert.Empty(store.GetNotes("tab-1"));
    }

    [Fact]
    public void Archiving_a_tab_keeps_its_notes_and_deleting_the_archive_removes_them()
    {
        var store = new StudioStore(_path);
        var state = new StudioState
        {
            ActiveSpaceId = "space-1",
            Spaces =
            [
                new SpaceState
                {
                    Id = "space-1",
                    Name = "Sales",
                    Tabs =
                    [
                        new TabState { Id = "tab-1", Title = "Query 1", Content = "SELECT 1;" },
                    ],
                },
            ],
        };

        store.Save(state);
        store.SaveNote(new NoteRecord
        {
            Id = "note-1",
            SpaceId = "space-1",
            TabId = "tab-1",
            Anchor = NoteAnchoring.Capture("SELECT 1;", 0, 9),
        });

        // Archiving moves the tab out of the workspace but keeps its text and its notes.
        state.Spaces[0].Tabs.Clear();
        state.ArchivedTabs.Add(new ArchivedTabState
        {
            Id = "tab-1",
            SpaceId = "space-1",
            SpaceName = "Sales",
            Title = "Query 1",
            Content = "SELECT 1;",
            ArchivedAt = DateTimeOffset.UtcNow,
        });

        store.Save(state);
        Assert.Single(store.GetNotes("tab-1"));

        // Deleting the archived tab is permanent: the note goes with it.
        state.ArchivedTabs.Clear();
        store.Save(state);
        Assert.Empty(store.GetNotes("tab-1"));
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // The test process can still hold the WAL handles; the temp file is disposable.
        }
    }
}
