use std::collections::HashMap;
use std::sync::{Arc, RwLock};

use anyhow::Result;
use gpui_kit::component::input::{CompletionProvider, Rope, RopeExt};
use gpui_kit::{App, Task, Window};
use larik_data::sql_language::SqlLanguage;
use larik_data::{
    CompletionKind, CompletionRequest, DatabaseInfo, EditorLanguage, SnippetDef, TableSchema,
    snippet_uses_clipboard,
};
use lsp_types::{
    CompletionContext, CompletionItem, CompletionItemKind, CompletionResponse,
    CompletionTextEdit, CompletionTriggerKind, Documentation, MarkupContent, MarkupKind, Range,
    TextEdit,
};

#[derive(Clone, Debug)]
pub struct PostInsertAction {
    pub caret_offset: usize,
    pub selection: Option<(usize, usize)>,
    pub reopen_completion: bool,
}

#[derive(Clone)]
pub struct CompletionSnapshot {
    pub tables: Arc<Vec<TableSchema>>,
    pub databases: Arc<Vec<DatabaseInfo>>,
    pub language: Arc<dyn EditorLanguage>,
    pub snippets: Arc<Vec<SnippetDef>>,
}

impl Default for CompletionSnapshot {
    fn default() -> Self {
        Self {
            tables: Arc::new(Vec::new()),
            databases: Arc::new(Vec::new()),
            language: Arc::new(SqlLanguage::tsql()),
            snippets: Arc::new(Vec::new()),
        }
    }
}

/// Bridges `larik-data`'s schema + dialect + snippet completion engine into GPUI Kit's LSP completion menu.
pub struct SqlCompletionProvider {
    snapshot: Arc<RwLock<CompletionSnapshot>>,
    pending_post_insert: Arc<RwLock<HashMap<String, PostInsertAction>>>,
}

impl SqlCompletionProvider {
    pub fn new(
        snapshot: Arc<RwLock<CompletionSnapshot>>,
        pending_post_insert: Arc<RwLock<HashMap<String, PostInsertAction>>>,
    ) -> Self {
        Self {
            snapshot,
            pending_post_insert,
        }
    }
}

impl CompletionProvider for SqlCompletionProvider {
    fn completions(
        &self,
        text: &Rope,
        offset: usize,
        trigger: CompletionContext,
        _window: &mut Window,
        cx: &mut App,
    ) -> Task<Result<CompletionResponse>> {
        let sql = text.to_string();
        let snap = self
            .snapshot
            .read()
            .map(|g| g.clone())
            .unwrap_or_default();

        let is_explicit = trigger.trigger_kind == CompletionTriggerKind::INVOKED;
        let typed_prefix = current_word_before(&sql, offset).to_ascii_lowercase();
        let needs_clipboard = snap.snippets.iter().any(|s| {
            snippet_uses_clipboard(&s.content)
                && if typed_prefix.is_empty() {
                    is_explicit
                } else {
                    s.trigger.to_ascii_lowercase().starts_with(&typed_prefix)
                        || s.name.to_ascii_lowercase().contains(&typed_prefix)
                }
        });
        let clipboard = if needs_clipboard {
            cx.read_from_clipboard()
                .and_then(|item| item.text())
                .unwrap_or_default()
        } else {
            String::new()
        };

        let result = snap.language.suggest_completions(&CompletionRequest {
            text: &sql,
            byte_offset: offset,
            tables: snap.tables.as_slice(),
            databases: snap.databases.as_slice(),
            is_explicit,
            snippets: snap.snippets.as_slice(),
            clipboard: &clipboard,
        });

        let start_pos = text.offset_to_position(result.replace_start);
        let end_pos = text.offset_to_position(offset);
        let replace_range = Range::new(start_pos, end_pos);

        let mut next_post_inserts = HashMap::new();

        let items: Vec<CompletionItem> = result
            .items
            .into_iter()
            .enumerate()
            .map(|(idx, c)| {
                let caret_offset = c.caret_offset.unwrap_or(c.insert_text.len());
                let clamped_caret = caret_offset.min(c.insert_text.len());
                let before_caret = if c.insert_text.is_char_boundary(clamped_caret) {
                    &c.insert_text[..clamped_caret]
                } else {
                    c.insert_text.as_str()
                };
                let upper_before_caret = before_caret.to_ascii_uppercase();
                let has_non_empty_selection = c.selection.is_some_and(|(_, len)| len > 0);
                let reopen_completion = !has_non_empty_selection
                    && (upper_before_caret.ends_with("FROM ")
                        || upper_before_caret.ends_with("JOIN "));

                if c.caret_offset.is_some() || has_non_empty_selection || reopen_completion {
                    next_post_inserts.insert(
                        c.insert_text.clone(),
                        PostInsertAction {
                            caret_offset,
                            selection: c.selection.filter(|(_, len)| *len > 0),
                            reopen_completion,
                        },
                    );
                }

                let documentation = c.documentation.map(|md| {
                    Documentation::MarkupContent(MarkupContent {
                        kind: MarkupKind::Markdown,
                        value: md,
                    })
                });

                CompletionItem {
                    label: c.label,
                    kind: Some(match c.kind {
                        CompletionKind::Table => CompletionItemKind::STRUCT,
                        CompletionKind::Column => CompletionItemKind::FIELD,
                        CompletionKind::Keyword => CompletionItemKind::KEYWORD,
                        CompletionKind::Function => CompletionItemKind::FUNCTION,
                        CompletionKind::Join => CompletionItemKind::SNIPPET,
                        CompletionKind::Database => CompletionItemKind::MODULE,
                        CompletionKind::Variable => CompletionItemKind::VARIABLE,
                        CompletionKind::Cte => CompletionItemKind::CLASS,
                        CompletionKind::Snippet => CompletionItemKind::SNIPPET,
                    }),
                    detail: c.detail,
                    documentation,
                    filter_text: c.filter_text,
                    sort_text: Some(format!("{idx:04}")),
                    text_edit: Some(CompletionTextEdit::Edit(TextEdit {
                        range: replace_range,
                        new_text: c.insert_text,
                    })),
                    ..Default::default()
                }
            })
            .collect();

        if let Ok(mut guard) = self.pending_post_insert.write() {
            *guard = next_post_inserts;
        }

        Task::ready(Ok(CompletionResponse::Array(items)))
    }

    fn is_completion_trigger(&self, offset: usize, new_text: &str, _cx: &mut App) -> bool {
        self.snapshot
            .read()
            .map(|snap| snap.language.is_completion_trigger(offset, new_text))
            .unwrap_or(false)
    }
}

fn current_word_before(sql: &str, byte_offset: usize) -> &str {
    let mut end = byte_offset.min(sql.len());
    while end > 0 && !sql.is_char_boundary(end) {
        end -= 1;
    }
    let bytes = &sql.as_bytes()[..end];
    let mut start = end;
    while start > 0 {
        let b = bytes[start - 1];
        if b.is_ascii_alphanumeric() || b == b'_' || b == b'@' || b == b'$' {
            start -= 1;
        } else {
            break;
        }
    }
    &sql[start..end]
}
