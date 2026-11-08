using Larik.Models;

namespace Larik.Services;

/// <summary>
/// The live snippet set: built-ins from <see cref="SnippetCatalog"/> plus user-defined snippets,
/// loaded once from <see cref="SnippetStore"/> and kept in memory for completion. Every mutation
/// persists first, then updates the list and raises <see cref="Changed"/>, so an open editor picks
/// the new snippets up on its next keystroke.
/// </summary>
public sealed class SnippetLibrary
{
    private static SnippetLibrary? _current;

    private readonly SnippetStore _store;
    private List<SnippetRecord> _snippets = [];

    public SnippetLibrary(SnippetStore? store = null)
    {
        _store = store ?? new SnippetStore();
        Reload();
    }

    public static SnippetLibrary Current => _current ??= new SnippetLibrary();

    /// <summary>Raised after a snippet was created, edited, enabled, reset or deleted.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SnippetRecord> All => _snippets;

    /// <summary>Snippets completion may offer in <paramref name="dialect"/>: enabled and in scope.</summary>
    public IReadOnlyList<SnippetDef> ForDialect(string dialect)
    {
        var target = string.IsNullOrWhiteSpace(dialect) ? "mssql" : dialect;

        return _snippets
            .Where(snippet => snippet.IsEnabled && AppliesTo(snippet, target))
            .Select(ToDefinition)
            .ToList();
    }

    public SnippetRecord Create(
        string trigger,
        string name,
        string content,
        string? description,
        string? category,
        IEnumerable<string>? dialects)
    {
        var record = new SnippetRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            Trigger = trigger.Trim(),
            Name = name.Trim(),
            Content = content,
            Description = Clean(description),
            Category = Clean(category),
            IsBuiltin = false,
            IsEnabled = true,
            Dialects = SnippetDialects.Normalize(dialects),
        };

        _store.Save(record);
        _snippets.Add(record);
        Sort();
        NotifyChanged();
        return record;
    }

    public SnippetRecord Update(
        string id,
        string trigger,
        string name,
        string content,
        string? description,
        string? category,
        IEnumerable<string>? dialects)
    {
        var existing = Find(id) ?? throw new InvalidOperationException("Snippet no longer exists.");

        var updated = existing with
        {
            Trigger = trigger.Trim(),
            Name = name.Trim(),
            Content = content,
            Description = Clean(description),
            Category = Clean(category),
            Dialects = SnippetDialects.Normalize(dialects),
        };

        _store.Save(updated);
        Replace(updated);
        Sort();
        NotifyChanged();
        return updated;
    }

    public bool Delete(string id)
    {
        var existing = Find(id);
        if (existing is null || existing.IsBuiltin)
        {
            return false;
        }

        if (!_store.Delete(id))
        {
            return false;
        }

        _snippets.Remove(existing);
        NotifyChanged();
        return true;
    }

    public SnippetRecord? SetEnabled(string id, bool enabled)
    {
        var existing = Find(id);
        if (existing is null || existing.IsEnabled == enabled)
        {
            return existing;
        }

        var updated = existing with { IsEnabled = enabled };
        _store.Save(updated);
        Replace(updated);
        NotifyChanged();
        return updated;
    }

    /// <summary>Restores a built-in's catalog name, content, category and scope; keeps its enabled state.</summary>
    public SnippetRecord? ResetBuiltin(string id)
    {
        var existing = Find(id);
        if (existing is not { IsBuiltin: true })
        {
            return null;
        }

        var original = SnippetCatalog.All.FirstOrDefault(snippet =>
                           string.Equals(SnippetCatalog.BuiltinId(snippet), existing.Id, StringComparison.Ordinal))
                       ?? SnippetCatalog.All.FirstOrDefault(snippet =>
                           snippet.Trigger.Equals(existing.Trigger, StringComparison.OrdinalIgnoreCase) &&
                           ScopesEqual(SnippetDialects.Normalize(snippet.Dialects), existing.Dialects));

        if (original is null)
        {
            return null;
        }

        var reset = SnippetCatalog.ToRecord(original, existing.IsEnabled) with { Id = existing.Id };
        _store.Save(reset);
        Replace(reset);
        Sort();
        NotifyChanged();
        return reset;
    }

    /// <summary>
    /// True when another snippet already answers to this trigger in an overlapping scope. Two
    /// unscoped snippets would both show up for every keystroke, which is never what the user meant.
    /// </summary>
    public bool HasTriggerConflict(string trigger, IEnumerable<string>? dialects, string? exceptId = null)
    {
        var candidate = SnippetDialects.Normalize(dialects);
        var normalizedTrigger = trigger.Trim();

        return _snippets.Any(snippet =>
            !string.Equals(snippet.Id, exceptId, StringComparison.Ordinal) &&
            snippet.Trigger.Equals(normalizedTrigger, StringComparison.OrdinalIgnoreCase) &&
            ScopesOverlap(candidate, snippet.Dialects));
    }

    public SnippetRecord? Find(string id) =>
        _snippets.FirstOrDefault(snippet => string.Equals(snippet.Id, id, StringComparison.Ordinal));

    // ---------------------------------------------------------------- loading

    private void Reload()
    {
        try
        {
            _snippets = _store.Load().ToList();
        }
        catch
        {
            // A broken database must not take completion down: fall back to the shipped catalog.
            _snippets = SnippetCatalog.All.Select(snippet => SnippetCatalog.ToRecord(snippet)).ToList();
        }

        Sort();
    }

    private void Sort() =>
        _snippets = _snippets
            .OrderBy(snippet => snippet.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snippet => snippet.Trigger, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snippet => snippet.IsBuiltin ? 0 : 1)
            .ToList();

    private void Replace(SnippetRecord record)
    {
        var index = _snippets.FindIndex(snippet => string.Equals(snippet.Id, record.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _snippets[index] = record;
        }
        else
        {
            _snippets.Add(record);
        }
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- helpers

    private static bool AppliesTo(SnippetRecord snippet, string dialect) =>
        snippet.Dialects is not { Length: > 0 } ||
        snippet.Dialects.Contains(dialect, StringComparer.OrdinalIgnoreCase);

    private static bool ScopesEqual(string[]? left, string[]? right) =>
        (left is null && right is null) ||
        (left is not null && right is not null &&
         left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase));

    private static bool ScopesOverlap(string[]? left, string[]? right) =>
        left is null || right is null || left.Intersect(right, StringComparer.OrdinalIgnoreCase).Any();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SnippetDef ToDefinition(SnippetRecord snippet) => new()
    {
        Trigger = snippet.Trigger,
        Name = snippet.Name,
        Content = snippet.Content,
        Description = snippet.Description,
        Category = snippet.Category,
        Dialects = snippet.Dialects,
    };
}
