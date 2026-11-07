using Larik.Editor;
using Larik.Models;
using Larik.Services;

namespace Larik.Services;

/// <summary>
/// Bridges the shell's current snippets, schema and databases to any editor surface. The surface
/// asks with a document + caret; this resolves the word, the clause, the tables (and their
/// aliases), declared variables and the UPDATE/INSERT target, then runs the shared engine.
/// </summary>
public sealed class StudioCompletionSource(
    Func<IReadOnlyList<SnippetDef>> snippets,
    Func<IReadOnlyList<Data.TableSchema>> tables,
    Func<IReadOnlyList<string>> databases) : IEditorCompletionSource
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
            CompletionEngine.DeclaredVariables(text, caret),
            databases(),
            CompletionEngine.FindTarget(text, caret, schema),
            Clipboard: string.Empty));
    }
}
