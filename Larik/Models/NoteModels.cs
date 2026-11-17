namespace Larik.Models;

/// <summary>What the notes pane is listing. A scope is a filter, not a different store.</summary>
public enum NoteScope
{
    /// <summary>Notes on the active tab, in document order.</summary>
    ThisTab,

    /// <summary>Notes across every open tab in the active workspace.</summary>
    Space,

    /// <summary>Notes on the active tab whose anchored text is gone.</summary>
    Unanchored,
}
