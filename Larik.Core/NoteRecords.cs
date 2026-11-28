namespace Larik.Services;

/// <summary>
/// What a note is for. A kind is a label the user reads, never a behaviour switch: the same note,
/// anchor and editor serve all four.
/// </summary>
public enum NoteKind
{
    Note,
    Todo,
    Decision,
    Observation,
}

/// <summary>How a stored anchor landed in the current document.</summary>
public enum NoteAnchorState
{
    /// <summary>The fingerprint is exactly where it was stored.</summary>
    Anchored,

    /// <summary>The fingerprint was found elsewhere; the note followed its text.</summary>
    Moved,

    /// <summary>The anchored text is gone. The note is kept and offered a re-anchor.</summary>
    Lost,

    /// <summary>The note intentionally belongs to the tab, not to a range (an empty document).</summary>
    Tab,
}

/// <summary>
/// A note's position, stored as a selector rather than a line number.
/// <para>
/// <see cref="Start"/> and <see cref="Length"/> are UTF-16 offsets and the fast path.
/// <see cref="Quote"/> plus the surrounding <see cref="Prefix"/>/<see cref="Suffix"/> are the
/// fingerprint that survives edits and reloads. <see cref="LineHint"/> is the last known line and
/// only breaks ties. Offsets are only meaningful for one generation of the text: every edit rebases
/// them, and a reload resolves them against the saved content.
/// </para>
/// <para>
/// A tab note has no range: it is a scratchpad line about the tab itself, which is the honest
/// anchor for a note written before the first statement exists.
/// </para>
/// </summary>
public readonly record struct NoteAnchor(
    int Start,
    int Length,
    string Quote,
    string Prefix,
    string Suffix,
    int LineHint,
    bool IsTab = false);

/// <summary>The anchor after resolution: where it is now and how sure that is.</summary>
public readonly record struct ResolvedNoteAnchor(int Start, int Length, NoteAnchorState State);

/// <summary>One note as the store keeps it.</summary>
public sealed class NoteRecord
{
    public string Id { get; set; } = string.Empty;

    public string SpaceId { get; set; } = string.Empty;

    public string TabId { get; set; } = string.Empty;

    public NoteKind Kind { get; set; }

    public string Body { get; set; } = string.Empty;

    public NoteAnchor Anchor { get; set; }

    public bool IsUnanchored { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Storage text for <see cref="NoteKind"/>; unknown values read back as <see cref="NoteKind.Note"/>.</summary>
public static class NoteKinds
{
    public static string ToStorage(NoteKind kind) => kind switch
    {
        NoteKind.Todo => "todo",
        NoteKind.Decision => "decision",
        NoteKind.Observation => "observation",
        _ => "note",
    };

    public static NoteKind Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "todo" => NoteKind.Todo,
        "decision" => NoteKind.Decision,
        "observation" => NoteKind.Observation,
        _ => NoteKind.Note,
    };
}

/// <summary>
/// The anchoring algorithm. Deliberately pure and text-only: the store, the editor and the tests all
/// use the same rules, and nothing here needs WinUI.
/// <para>
/// A note is anchored to a range (the statement under the caret, or the selection). While the
/// document is open every edit rebases the range from the old/new pair; on load the fingerprint is
/// resolved against the saved content. When neither finds the text the note is not deleted and not
/// silently moved: it is marked lost so the list can offer a re-anchor.
/// </para>
/// </summary>
public static class NoteAnchoring
{
    /// <summary>Context kept on each side of the quote for disambiguating repeated text.</summary>
    public const int ContextLength = 32;

    /// <summary>
    /// Longest quote kept per note. A note on a 600-line statement still anchors to the whole range;
    /// only the resolution fingerprint is capped, and the start of the range is enough for that.
    /// </summary>
    public const int MaxQuoteLength = 4000;

    /// <summary>A note about the tab itself, for a document with no statement to anchor to.</summary>
    public static NoteAnchor TabNote() => new(0, 0, string.Empty, string.Empty, string.Empty, 0, IsTab: true);

    /// <summary>Captures a fresh fingerprint for a range in <paramref name="text"/>.</summary>
    public static NoteAnchor Capture(string? text, int start, int length)
    {
        text ??= string.Empty;
        var safeStart = Math.Clamp(start, 0, text.Length);
        var safeLength = Math.Clamp(length, 0, text.Length - safeStart);

        var quoteLength = Math.Min(safeLength, MaxQuoteLength);
        var quote = quoteLength > 0 ? text.Substring(safeStart, quoteLength) : string.Empty;

        var before = Math.Max(0, safeStart - ContextLength);
        var prefix = text[before..safeStart];

        var afterStart = safeStart + safeLength;
        var afterLength = Math.Clamp(text.Length - afterStart, 0, ContextLength);
        var suffix = afterLength > 0 ? text.Substring(afterStart, afterLength) : string.Empty;

        return new NoteAnchor(
            safeStart,
            safeLength,
            quote,
            prefix,
            suffix,
            LineOf(text, safeStart));
    }

    /// <summary>
    /// Rebases an anchor across one edit. The shell calls this for every text change while the
    /// document is open, using the document before and after the change.
    /// <para>
    /// A RichEdit edit is a single splice in the overwhelming majority of cases (typing, paste,
    /// delete, undo), so the diff is the common prefix/suffix rather than a general LCS. Format
    /// rewrites the whole document as one large splice; when that collapses an anchor, the
    /// fingerprint gets one chance to find its text again before the note is marked lost.
    /// </para>
    /// </summary>
    public static NoteAnchor Rebase(NoteAnchor anchor, string? oldText, string? newText)
    {
        if (anchor.IsTab)
        {
            return anchor;
        }

        oldText ??= string.Empty;
        newText ??= string.Empty;

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return anchor;
        }

        var prefix = CommonPrefix(oldText, newText);
        var suffix = CommonSuffix(oldText, newText, prefix);
        var oldMiddle = oldText.Length - prefix - suffix;
        var newMiddle = newText.Length - prefix - suffix;
        var delta = newMiddle - oldMiddle;
        var spliceEnd = prefix + oldMiddle;

        var start = anchor.Start;
        var end = anchor.Start + anchor.Length;

        int newStart;
        int newEnd;

        if (oldMiddle == 0)
        {
            // Insertion. Typing at the range's first character is typing before it; typing at its
            // end is typing after it; anything strictly inside grows the range.
            if (prefix <= start)
            {
                newStart = start + newMiddle;
                newEnd = end + newMiddle;
            }
            else if (prefix >= end)
            {
                newStart = start;
                newEnd = end;
            }
            else
            {
                newStart = start;
                newEnd = end + newMiddle;
            }
        }
        else
        {
            // The range's own text survives on the left and/or right of the splice; inserted text
            // is only absorbed when the splice happens inside the range.
            newStart = start <= prefix
                ? start
                : start < spliceEnd
                    ? prefix
                    : start + delta;

            newEnd = end <= prefix
                ? end
                : end < spliceEnd
                    ? prefix + newMiddle
                    : end + delta;
        }

        newStart = Math.Clamp(newStart, 0, newText.Length);
        newEnd = Math.Clamp(newEnd, newStart, newText.Length);

        if (newEnd > newStart)
        {
            return Capture(newText, newStart, newEnd - newStart);
        }

        // The range collapsed (its text was deleted or wholly replaced by nothing). Give the old
        // fingerprint one chance: a formatter can move text without deleting it.
        var rescued = Resolve(anchor, newText);
        return rescued.State == NoteAnchorState.Lost
            ? Capture(newText, newStart, 0)
            : Capture(newText, rescued.Start, rescued.Length);
    }

    /// <summary>
    /// Finds the anchor in <paramref name="text"/>. The stored offset is trusted only when the
    /// quote still matches there; otherwise a unique quote, or the occurrence whose surrounding
    /// context matches best, is the note's home.
    /// </summary>
    public static ResolvedNoteAnchor Resolve(NoteAnchor anchor, string? text)
    {
        if (anchor.IsTab)
        {
            return new ResolvedNoteAnchor(0, 0, NoteAnchorState.Tab);
        }

        text ??= string.Empty;

        if (anchor.Quote.Length == 0 || anchor.Length <= 0)
        {
            return new ResolvedNoteAnchor(Math.Clamp(anchor.Start, 0, text.Length), 0, NoteAnchorState.Lost);
        }

        var quoteLength = Math.Min(anchor.Length, anchor.Quote.Length);
        if (IsAt(text, anchor.Start, anchor.Quote, quoteLength))
        {
            return new ResolvedNoteAnchor(
                anchor.Start,
                Math.Min(anchor.Length, text.Length - anchor.Start),
                NoteAnchorState.Anchored);
        }

        var hits = FindAll(text, anchor.Quote, limit: 64);
        if (hits.Count == 0)
        {
            return new ResolvedNoteAnchor(Math.Clamp(anchor.Start, 0, text.Length), 0, NoteAnchorState.Lost);
        }

        var best = hits[0];
        if (hits.Count > 1)
        {
            var bestScore = -1;
            foreach (var hit in hits)
            {
                var score = ContextScore(text, hit, anchor);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hit;
                }
            }
        }

        var length = Math.Clamp(Math.Max(anchor.Length, quoteLength), 0, text.Length - best);
        return new ResolvedNoteAnchor(best, length, NoteAnchorState.Moved);
    }

    /// <summary>One-based line number for an offset, counting LF, CRLF, and lone CR alike.</summary>
    public static int LineOf(string? text, int offset)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        var end = Math.Clamp(offset, 0, text.Length);
        var span = text.AsSpan(0, end);
        var line = 1;
        var index = 0;
        while (index < end)
        {
            var next = span.Slice(index).IndexOfAny('\n', '\r');
            if (next < 0)
            {
                break;
            }

            index += next;
            if (span[index] == '\n' || index + 1 >= text.Length || text[index + 1] != '\n')
            {
                line++;
            }

            index++;
        }

        return line;
    }

    /// <summary>Line span for the line <paramref name="offset"/> sits on, terminators excluded.</summary>
    public static (int Start, int Length) LineSpanAt(string? text, int offset)
    {
        text ??= string.Empty;
        var span = text.AsSpan();
        var caret = Math.Clamp(offset, 0, span.Length);

        var prevBreak = span.Slice(0, caret).LastIndexOfAny('\n', '\r');
        var start = prevBreak < 0 ? 0 : prevBreak + 1;

        var nextBreak = span.Slice(caret).IndexOfAny('\n', '\r');
        var end = nextBreak < 0 ? span.Length : caret + nextBreak;

        return (start, end - start);
    }

    private static bool IsAt(string text, int start, string quote, int quoteLength)
    {
        if (start < 0 || quoteLength <= 0 || start + quoteLength > text.Length)
        {
            return false;
        }

        return string.CompareOrdinal(text, start, quote, 0, quoteLength) == 0;
    }

    private static List<int> FindAll(string text, string quote, int limit)
    {
        var hits = new List<int>();
        var index = 0;
        while (hits.Count < limit && index <= text.Length - quote.Length)
        {
            var hit = text.IndexOf(quote, index, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            hits.Add(hit);
            index = hit + 1;
        }

        return hits;
    }

    private static int ContextScore(string text, int start, NoteAnchor anchor)
    {
        var score = 0;

        var prefixStart = start - anchor.Prefix.Length;
        if (prefixStart >= 0
            && string.CompareOrdinal(text, prefixStart, anchor.Prefix, 0, anchor.Prefix.Length) == 0)
        {
            score += anchor.Prefix.Length + 1;
        }

        var suffixStart = start + anchor.Quote.Length;
        if (suffixStart >= 0
            && suffixStart + anchor.Suffix.Length <= text.Length
            && string.CompareOrdinal(text, suffixStart, anchor.Suffix, 0, anchor.Suffix.Length) == 0)
        {
            score += anchor.Suffix.Length + 1;
        }

        return score;
    }

    private static int CommonPrefix(string left, string right) =>
        left.AsSpan().CommonPrefixLength(right);

    private static int CommonSuffix(string left, string right, int prefix)
    {
        var limit = Math.Min(left.Length, right.Length) - prefix;
        if (limit <= 0)
        {
            return 0;
        }

        var leftTail = left.AsSpan(left.Length - limit, limit);
        var rightTail = right.AsSpan(right.Length - limit, limit);
        var matched = 0;

        // Compare 64-char blocks with SIMD SequenceEqual from the end before scanning the remainder.
        const int block = 64;
        while (limit - matched >= block
            && leftTail.Slice(limit - matched - block, block).SequenceEqual(rightTail.Slice(limit - matched - block, block)))
        {
            matched += block;
        }

        while (matched < limit && leftTail[limit - 1 - matched] == rightTail[limit - 1 - matched])
        {
            matched++;
        }

        return matched;
    }
}
