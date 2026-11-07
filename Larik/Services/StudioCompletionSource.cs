using Larik.Editor;
using Larik.Models;
using Larik.Services;

namespace Larik.Services;

/// <summary>
/// Bridges the shell's current snippets and schema to any editor surface. The surface asks with a
/// document + caret; this resolves the word, the clause, the tables (and their aliases) in play,
/// then runs the shared completion engine.
/// </summary>
public sealed class StudioCompletionSource(
    Func<IReadOnlyList<SnippetDef>> snippets,
    Func<IReadOnlyList<Data.TableSchema>> tables) : IEditorCompletionSource
{
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionQuery query)
    {
        var text = query.DocumentText;
        var caret = query.CaretOffset;
        var (_, word, qualifier) = CompletionEngine.ExtractContext(text, caret);
        var (scope, keyword) = CompletionEngine.DetectScope(text, caret);
        var schema = tables();

        return CompletionEngine.Suggest(new CompletionRequest(
            word,
            qualifier,
            scope,
            keyword,
            snippets(),
            schema,
            CompletionEngine.FindTableReferences(text, caret, schema),
            Clipboard: string.Empty));
    }
}
