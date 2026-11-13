using Larik.Data;
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
    Func<IReadOnlyList<Data.RoutineSchema>> routines,
    Func<IReadOnlyList<string>> databases) : IEditorCompletionSource
{
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionQuery query)
    {
        var text = query.DocumentText;
        var caret = query.CaretOffset;
        var dialect = query.Language as ISqlDialect;
        var schema = tables();

        // A provider can complete a document the SQL model does not understand (mongosh's db.…).
        // SQL providers return null here and fall through to the shared engine unchanged.
        var hints = query.Language?.GetCompletionHints(
            new CompletionHintQuery(text, caret, schema.Select(table => table.Name).ToList()));
        if (hints is { Count: > 0 })
        {
            return hints
                .Select(hint => new CompletionItem
                {
                    Label = hint.Label,
                    Detail = hint.Detail,
                    Kind = hint.Kind == CompletionHintKind.Method ? CompletionKind.Function : CompletionKind.Table,
                    InsertText = hint.InsertText,
                    CaretOffset = hint.Kind == CompletionHintKind.Method ? hint.InsertText.Length - 1 : -1,
                })
                .ToList();
        }

        var (_, word, qualifier) = CompletionEngine.ExtractContext(text, caret);
        var (scope, keyword) = CompletionEngine.DetectScope(text, caret, dialect);
        var ctes = CompletionEngine.FindCtes(text, caret, schema);

        return CompletionEngine.Suggest(new CompletionRequest(
            word,
            qualifier,
            scope,
            keyword,
            snippets(),
            schema,
            CompletionEngine.FindTableReferences(text, caret, schema, ctes),
            ctes,
            CompletionEngine.DeclaredVariables(text, caret),
            databases(),
            CompletionEngine.FindTarget(text, caret, schema),
            Clipboard: string.Empty)
        {
            Dialect = dialect,
            Routines = routines(),
        });
    }
}
