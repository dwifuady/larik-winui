use std::sync::{Arc, RwLock};

use anyhow::Result;
use gpui_kit::component::input::{CompletionProvider, Rope, RopeExt};
use gpui_kit::{App, Task, Window};
use larik_data::sql_language::{CompletionKind, SqlLanguage, suggest_completions};
use larik_data::{EditorLanguage, TableSchema};
use lsp_types::{
    CompletionContext, CompletionItem, CompletionItemKind, CompletionResponse,
    CompletionTextEdit, Range, TextEdit,
};

#[derive(Clone)]
pub struct CompletionSnapshot {
    pub tables: Vec<TableSchema>,
    pub language: Arc<dyn EditorLanguage>,
}

impl Default for CompletionSnapshot {
    fn default() -> Self {
        Self {
            tables: Vec::new(),
            language: Arc::new(SqlLanguage::tsql()),
        }
    }
}

/// Bridges `larik-data`'s schema + dialect completion engine into GPUI Kit's LSP completion menu.
pub struct SqlCompletionProvider {
    snapshot: Arc<RwLock<CompletionSnapshot>>,
}

impl SqlCompletionProvider {
    pub fn new(snapshot: Arc<RwLock<CompletionSnapshot>>) -> Self {
        Self { snapshot }
    }
}

impl CompletionProvider for SqlCompletionProvider {
    fn completions(
        &self,
        text: &Rope,
        offset: usize,
        _trigger: CompletionContext,
        _window: &mut Window,
        _cx: &mut App,
    ) -> Task<Result<CompletionResponse>> {
        let sql = text.to_string();
        let snap = self
            .snapshot
            .read()
            .map(|g| g.clone())
            .unwrap_or_default();

        let (word_start, candidates) =
            suggest_completions(&sql, offset, &snap.tables, snap.language.as_ref());

        let start_pos = text.offset_to_position(word_start);
        let end_pos = text.offset_to_position(offset);
        let replace_range = Range::new(start_pos, end_pos);

        let items: Vec<CompletionItem> = candidates
            .into_iter()
            .enumerate()
            .map(|(idx, c)| CompletionItem {
                label: c.label,
                kind: Some(match c.kind {
                    CompletionKind::Table => CompletionItemKind::STRUCT,
                    CompletionKind::Column => CompletionItemKind::FIELD,
                    CompletionKind::Keyword => CompletionItemKind::KEYWORD,
                    CompletionKind::Function => CompletionItemKind::FUNCTION,
                }),
                detail: c.detail,
                sort_text: Some(format!("{idx:04}")),
                text_edit: Some(CompletionTextEdit::Edit(TextEdit {
                    range: replace_range,
                    new_text: c.insert_text,
                })),
                ..Default::default()
            })
            .collect();

        Task::ready(Ok(CompletionResponse::Array(items)))
    }

    fn is_completion_trigger(&self, _offset: usize, new_text: &str, _cx: &mut App) -> bool {
        new_text == "."
            || new_text
                .chars()
                .last()
                .is_some_and(|c| c.is_ascii_alphabetic() || c == '_')
    }
}
