using Larik.Editor;
using Larik.Models;
using Larik.Services;

namespace Larik.Services;

/// <summary>
/// Bridges the shell's current snippets and schema to any editor surface. The surface asks with a
/// document + caret; this resolves the word, the clause and the tables in play, then runs the
/// shared completion engine.
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
        var schema = tables();

        return CompletionEngine.Suggest(new CompletionRequest(
            word,
            qualifier,
            CompletionEngine.DetectScope(text, caret),
            snippets(),
            schema,
            CompletionEngine.TablesInStatement(text, caret, schema),
            qualifier is null ? null : CompletionEngine.ResolveQualifier(text, caret, qualifier, schema),
            Clipboard: string.Empty));
    }
}
