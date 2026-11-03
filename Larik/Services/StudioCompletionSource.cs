using Larik.Editor;
using Larik.Models;
using Larik.Services;

namespace Larik.Services;

/// <summary>
/// Bridges the shell's current snippets and schema to any editor surface. The surface asks with a
/// document + caret; this resolves the word and runs the shared completion engine.
/// </summary>
public sealed class StudioCompletionSource(
    Func<IReadOnlyList<SnippetDef>> snippets,
    Func<IReadOnlyList<Data.TableSchema>> tables) : IEditorCompletionSource
{
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionQuery query)
    {
        var (_, word, qualifier) = CompletionEngine.ExtractContext(query.DocumentText, query.CaretOffset);
        return CompletionEngine.Suggest(word, qualifier, snippets(), tables(), clipboard: string.Empty);
    }
}
