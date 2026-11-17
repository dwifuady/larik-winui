using Larik.Services;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The anchoring rules are the part of notes a user cannot see and must still trust: a note follows
/// its text through edits, a reload re-finds it, and a deleted range is reported instead of silently
/// pointing somewhere else.
/// </summary>
public sealed class NoteAnchoringTests
{
    [Fact]
    public void Capture_KeepsRangeAndContext()
    {
        const string text = "SELECT a FROM t WHERE x > 5;";
        var anchor = NoteAnchoring.Capture(text, 7, 8);

        Assert.Equal(7, anchor.Start);
        Assert.Equal(8, anchor.Length);
        Assert.Equal("a FROM t", anchor.Quote);
        Assert.Equal("SELECT ", anchor.Prefix);
        Assert.Equal(" WHERE x > 5;", anchor.Suffix);
        Assert.Equal(1, anchor.LineHint);
    }

    [Fact]
    public void Resolve_ExactPositionIsAnchored()
    {
        const string text = "one\nSELECT a FROM t;\ntwo";
        var anchor = NoteAnchoring.Capture(text, 4, 16);
        var resolved = NoteAnchoring.Resolve(anchor, text);

        Assert.Equal(NoteAnchorState.Anchored, resolved.State);
        Assert.Equal(4, resolved.Start);
        Assert.Equal(16, resolved.Length);
    }

    [Fact]
    public void Resolve_MovedTextIsFoundByQuote()
    {
        const string original = "SELECT a FROM t;";
        var anchor = NoteAnchoring.Capture(original, 0, original.Length);

        const string shifted = "header\n\nSELECT a FROM t;";
        var resolved = NoteAnchoring.Resolve(anchor, shifted);

        Assert.Equal(NoteAnchorState.Moved, resolved.State);
        Assert.Equal(shifted.IndexOf("SELECT", StringComparison.Ordinal), resolved.Start);
        Assert.Equal(original.Length, resolved.Length);
    }

    [Fact]
    public void Resolve_RepeatedQuoteUsesContext()
    {
        const string original = "x\nSELECT 1;\ny";
        var anchor = NoteAnchoring.Capture(original, 2, 9);

        // Two identical statements: only the second one is followed by the anchored "\ny".
        const string moved = "SELECT 1;\nzzz\nSELECT 1;\ny";
        var resolved = NoteAnchoring.Resolve(anchor, moved);

        Assert.Equal(NoteAnchorState.Moved, resolved.State);
        Assert.Equal(moved.LastIndexOf("SELECT 1;", StringComparison.Ordinal), resolved.Start);
    }

    [Fact]
    public void Resolve_DeletedTextIsLostButKeepsPosition()
    {
        const string original = "aaBEGINbb";
        var anchor = NoteAnchoring.Capture(original, 2, 5);
        var resolved = NoteAnchoring.Resolve(anchor, "aabb");

        Assert.Equal(NoteAnchorState.Lost, resolved.State);
        Assert.InRange(resolved.Start, 0, 4);
    }

    [Fact]
    public void Rebase_InsertBeforeShiftsTheRange()
    {
        const string oldText = "SELECT a;\nSELECT b;";
        var anchor = NoteAnchoring.Capture(oldText, 10, 9);

        var newText = "x" + oldText;
        var rebased = NoteAnchoring.Rebase(anchor, oldText, newText);

        Assert.Equal(11, rebased.Start);
        Assert.Equal(9, rebased.Length);
        Assert.Equal("SELECT b;", rebased.Quote);
    }

    [Fact]
    public void Rebase_DeleteBeforeShiftsTheRange()
    {
        const string oldText = "abc\nSELECT b;";
        var anchor = NoteAnchoring.Capture(oldText, 4, 9);

        var newText = oldText.Remove(0, 2);
        var rebased = NoteAnchoring.Rebase(anchor, oldText, newText);

        Assert.Equal(2, rebased.Start);
        Assert.Equal(9, rebased.Length);
        Assert.Equal("SELECT b;", rebased.Quote);
    }

    [Fact]
    public void Rebase_InsertAtTheStartShiftsTheRange()
    {
        const string oldText = "SELECT a FROM t;";
        var anchor = NoteAnchoring.Capture(oldText, 0, oldText.Length);

        var newText = "\r\n" + oldText;
        var rebased = NoteAnchoring.Rebase(anchor, oldText, newText);

        Assert.Equal(2, rebased.Start);
        Assert.Equal(oldText.Length, rebased.Length);
        Assert.Equal("SELECT a FROM t;", rebased.Quote);
    }

    [Fact]
    public void Rebase_InsertAtTheEndDoesNotExtendTheRange()
    {
        const string oldText = "SELECT a FROM t;";
        var anchor = NoteAnchoring.Capture(oldText, 0, oldText.Length);

        var newText = oldText + "\r\n-- trailing";
        var rebased = NoteAnchoring.Rebase(anchor, oldText, newText);

        Assert.Equal(0, rebased.Start);
        Assert.Equal(oldText.Length, rebased.Length);
        Assert.Equal(oldText, rebased.Quote);
    }

    [Fact]
    public void Rebase_InsertInsideExpandsTheRange()
    {
        const string oldText = "SELECT a FROM t;";
        var anchor = NoteAnchoring.Capture(oldText, 0, oldText.Length);

        var newText = oldText.Insert(10, "\n    AND x = 1");
        var rebased = NoteAnchoring.Rebase(anchor, oldText, newText);

        Assert.Equal(0, rebased.Start);
        Assert.Equal(newText.Length, rebased.Length);
    }

    [Fact]
    public void Rebase_DeleteInsideShrinksTheRange()
    {
        const string oldText = "SELECT a, b FROM t;";
        var anchor = NoteAnchoring.Capture(oldText, 0, oldText.Length);

        var newText = oldText.Remove(8, 3); // ", b"
        var rebased = NoteAnchoring.Rebase(anchor, oldText, newText);

        Assert.Equal(0, rebased.Start);
        Assert.Equal(newText.Length, rebased.Length);
        Assert.Equal(newText, rebased.Quote);
    }

    [Fact]
    public void Rebase_DeletingTheAnchoredTextMarksItLost()
    {
        const string oldText = "aaBEGINbb";
        var anchor = NoteAnchoring.Capture(oldText, 2, 5);

        var rebased = NoteAnchoring.Rebase(anchor, oldText, "aabb");

        Assert.Equal(0, rebased.Length);
        Assert.Equal(string.Empty, rebased.Quote);
    }

    [Fact]
    public void Rebase_UnrelatedEditKeepsTheAnchorAndRefreshesContext()
    {
        const string oldText = "SELECT a FROM t;";
        var anchor = NoteAnchoring.Capture(oldText, 7, 1); // "a"

        var newText = "SELECT a FROM t WHERE x;";
        var rebased = NoteAnchoring.Rebase(anchor, oldText, newText);

        Assert.Equal(7, rebased.Start);
        Assert.Equal(1, rebased.Length);
        Assert.Equal("a", rebased.Quote);
        Assert.Equal(" FROM t WHERE x;", rebased.Suffix);
    }

    [Theory]
    [InlineData("a\nb", 2, 2)]
    [InlineData("a\r\nb", 3, 2)]
    [InlineData("a\rb", 2, 2)]
    public void LineOf_CountsEveryNewlineStyle(string text, int offset, int expected)
    {
        Assert.Equal(expected, NoteAnchoring.LineOf(text, offset));
    }

    [Fact]
    public void LineSpanAt_ReturnsTheLineWithoutTerminators()
    {
        (int Start, int Length) span = NoteAnchoring.LineSpanAt("ab\r\ncd", 4);

        Assert.Equal(4, span.Start);
        Assert.Equal(2, span.Length);
    }

    [Fact]
    public void A_tab_note_stays_with_the_tab_through_edits()
    {
        var anchor = NoteAnchoring.TabNote();

        Assert.Equal(NoteAnchorState.Tab, NoteAnchoring.Resolve(anchor, "SELECT 1;").State);
        Assert.Equal(anchor, NoteAnchoring.Rebase(anchor, "SELECT 1;", "SELECT 2;"));
    }

    [Fact]
    public void A_collapsed_range_is_lost_not_a_tab_note()
    {
        var anchor = NoteAnchoring.Capture("SELECT 1;", 0, 9);
        var collapsed = NoteAnchoring.Rebase(anchor, "SELECT 1;", "");

        Assert.Equal(NoteAnchorState.Lost, NoteAnchoring.Resolve(collapsed, "").State);
    }

    [Theory]
    [InlineData(NoteKind.Note, "note")]
    [InlineData(NoteKind.Todo, "todo")]
    [InlineData(NoteKind.Decision, "decision")]
    [InlineData(NoteKind.Observation, "observation")]
    public void NoteKind_RoundTripsThroughStorage(NoteKind kind, string stored)
    {
        Assert.Equal(stored, NoteKinds.ToStorage(kind));
        Assert.Equal(kind, NoteKinds.Parse(stored));
    }

    [Fact]
    public void NoteKind_UnknownStorageValueFallsBackToNote()
    {
        Assert.Equal(NoteKind.Note, NoteKinds.Parse("something-new"));
    }
}
