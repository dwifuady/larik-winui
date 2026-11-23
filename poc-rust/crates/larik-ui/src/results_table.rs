use std::sync::Arc;

use gpui_kit::component::table::{Column, ColumnFixed, TableDelegate, TableState};
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    App, Context, IntoElement, ParentElement, SharedString, Styled, TextAlign, Window, div, px,
};
use larik_data::QueryResult;

/// Maximum characters shaped per cell in the virtualized result grid.
/// Keeps DirectWrite shaping instant even on wide `NVARCHAR(MAX)` / JSON / XML columns,
/// while full original cell values remain intact in `source` for clipboard export and `cell_text`.
const MAX_CELL_PREVIEW_CHARS: usize = 160;

/// Virtualized `DataTable` delegate rendering a `QueryResult` with zero per-frame string
/// allocations: display strings and row numbers are pre-converted to `SharedString` once
/// when a result is attached, and column reordering is O(columns).
pub struct ResultTableDelegate {
    source: Option<Arc<QueryResult>>,
    columns: Vec<Column>,
    column_types: Vec<Option<SharedString>>,
    /// Maps visible data column index (`col_ix - 1`) -> original column index in `source`.
    col_order: Vec<usize>,
    /// Pre-sanitized, preview-capped `SharedString` grid so `render_td` is O(1) refcount clone.
    display_rows: Vec<Vec<Option<SharedString>>>,
    /// Pre-formatted row numbers (`"1"`, `"2"`, ...) so gutter `render_td` never allocates.
    row_numbers: Vec<SharedString>,
}

fn format_cell_preview(raw: &str) -> SharedString {
    let needs_clean = raw.contains(['\r', '\n', '\t']);
    let mut chars = raw.chars();
    let preview: String = chars.by_ref().take(MAX_CELL_PREVIEW_CHARS).collect();
    let truncated = chars.next().is_some();

    if !needs_clean && !truncated {
        return SharedString::from(raw.to_string());
    }

    let mut out = String::with_capacity(preview.len() + 3);
    for ch in preview.chars() {
        match ch {
            '\r' | '\n' | '\t' => out.push(' '),
            other => out.push(other),
        }
    }
    if truncated {
        out.push('…');
    }
    SharedString::from(out)
}

impl ResultTableDelegate {
    pub fn empty() -> Self {
        Self {
            source: None,
            columns: Vec::new(),
            column_types: Vec::new(),
            col_order: Vec::new(),
            display_rows: Vec::new(),
            row_numbers: Vec::new(),
        }
    }

    pub fn set_result(&mut self, result: Option<Arc<QueryResult>>) {
        self.source = None;
        self.columns.clear();
        self.column_types.clear();
        self.col_order.clear();
        self.display_rows.clear();
        self.row_numbers.clear();

        let Some(res) = result else {
            return;
        };
        if res.columns.is_empty() {
            return;
        }

        // Fixed sticky left gutter column (`#`) matching Larik's row-number gutter.
        let mut gutter = Column::new("__row_num__", "#")
            .width(px(52.0))
            .min_width(px(44.0))
            .max_width(px(88.0))
            .fixed(ColumnFixed::Left)
            .resizable(false)
            .movable(false)
            .selectable(false);
        gutter.align = TextAlign::Right;
        self.columns.push(gutter);
        self.column_types.push(None);

        self.col_order.reserve(res.columns.len());
        for (idx, col) in res.columns.iter().enumerate() {
            // Estimate comfortable initial column width from header + first 25 rows.
            let sample_max = res
                .rows
                .iter()
                .take(25)
                .filter_map(|r| r.get(idx).and_then(|v| v.as_deref()))
                .map(|s| s.chars().take(40).count())
                .max()
                .unwrap_or(0);
            let header_len = col.name.chars().count()
                + col
                    .data_type
                    .as_ref()
                    .map(|t| t.chars().count().min(10) + 2)
                    .unwrap_or(0);
            let char_len = header_len.max(sample_max).clamp(9, 38);
            let width = px((char_len as f32 * 8.2 + 30.0).clamp(104.0, 340.0));

            self.columns.push(
                Column::new(format!("col_{idx}"), col.name.clone())
                    .width(width)
                    .min_width(px(72.0))
                    .max_width(px(640.0))
                    .resizable(true)
                    .movable(true),
            );
            self.column_types
                .push(col.data_type.as_ref().map(|t| SharedString::from(t.clone())));
            self.col_order.push(idx);
        }

        self.row_numbers = (1..=res.rows.len())
            .map(|n| SharedString::from(n.to_string()))
            .collect();

        self.display_rows = res
            .rows
            .iter()
            .map(|row| {
                row.iter()
                    .map(|cell| cell.as_deref().map(format_cell_preview))
                    .collect()
            })
            .collect();

        self.source = Some(res);
    }

    /// Format the active grid as tab-separated values for clipboard export (full un-truncated data).
    pub fn to_tsv(&self) -> String {
        let Some(src) = self.source.as_ref() else {
            return String::new();
        };
        if self.columns.len() <= 1 {
            return String::new();
        }
        let mut out = String::new();
        let headers: Vec<&str> = self
            .columns
            .iter()
            .skip(1)
            .map(|c| c.name.as_ref())
            .collect();
        out.push_str(&headers.join("\t"));
        out.push('\n');

        for row in &src.rows {
            for (vis_idx, &src_col_idx) in self.col_order.iter().enumerate() {
                if vis_idx > 0 {
                    out.push('\t');
                }
                match row.get(src_col_idx).and_then(|v| v.as_deref()) {
                    Some(v) => out.push_str(&v.replace(['\t', '\r', '\n'], " ")),
                    None => out.push_str("NULL"),
                }
            }
            out.push('\n');
        }
        out
    }
}

impl TableDelegate for ResultTableDelegate {
    fn columns_count(&self, _cx: &App) -> usize {
        self.columns.len()
    }

    fn rows_count(&self, _cx: &App) -> usize {
        self.display_rows.len()
    }

    fn column(&self, col_ix: usize, _cx: &App) -> Column {
        self.columns.get(col_ix).cloned().unwrap_or_default()
    }

    fn move_column(
        &mut self,
        col_ix: usize,
        to_ix: usize,
        _window: &mut Window,
        _cx: &mut Context<TableState<Self>>,
    ) {
        // Keep column 0 (`#` row gutter) pinned on the far left; O(columns) indirection.
        if col_ix == 0 || to_ix == 0 || col_ix >= self.columns.len() || to_ix >= self.columns.len()
        {
            return;
        }
        let col = self.columns.remove(col_ix);
        self.columns.insert(to_ix, col);
        let ty = self.column_types.remove(col_ix);
        self.column_types.insert(to_ix, ty);

        let mapped = self.col_order.remove(col_ix - 1);
        self.col_order.insert(to_ix - 1, mapped);
    }

    fn render_th(
        &mut self,
        col_ix: usize,
        _window: &mut Window,
        cx: &mut Context<TableState<Self>>,
    ) -> impl IntoElement {
        let name = self
            .columns
            .get(col_ix)
            .map(|c| c.name.clone())
            .unwrap_or_default();
        let data_type = self.column_types.get(col_ix).and_then(|t| t.clone());
        let theme = cx.theme();
        let mono = theme.mono_font_family.clone();

        if col_ix == 0 {
            return div()
                .w_full()
                .pr_1()
                .text_right()
                .text_size(px(11.0))
                .font_family(mono)
                .text_color(theme.muted_foreground.opacity(0.75))
                .child("#");
        }

        div()
            .w_full()
            .flex()
            .items_center()
            .justify_between()
            .gap_2()
            .overflow_hidden()
            .child(
                div()
                    .text_size(px(12.0))
                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                    .text_color(theme.foreground)
                    .overflow_hidden()
                    .whitespace_nowrap()
                    .child(name),
            )
            .when_some(data_type, |el, dt| {
                el.child(
                    div()
                        .flex_shrink_0()
                        .text_size(px(10.5))
                        .font_family(mono)
                        .text_color(theme.muted_foreground.opacity(0.7))
                        .child(dt),
                )
            })
    }

    fn render_td(
        &mut self,
        row_ix: usize,
        col_ix: usize,
        _window: &mut Window,
        cx: &mut Context<TableState<Self>>,
    ) -> impl IntoElement {
        let theme = cx.theme();
        let mono = theme.mono_font_family.clone();

        if col_ix == 0 {
            let label = self
                .row_numbers
                .get(row_ix)
                .cloned()
                .unwrap_or_default();
            return div()
                .w_full()
                .pr_1()
                .text_right()
                .text_size(px(11.5))
                .font_family(mono)
                .text_color(theme.muted_foreground.opacity(0.75))
                .child(label);
        }

        let src_col = self.col_order.get(col_ix - 1).copied().unwrap_or(col_ix - 1);
        let cell = self
            .display_rows
            .get(row_ix)
            .and_then(|r| r.get(src_col))
            .and_then(|v| v.as_ref());

        // `DataTable::render_cell` already applies `overflow_hidden().whitespace_nowrap()`,
        // so returning a single flat `div` with a pre-built `SharedString` avoids both
        // per-frame heap allocations and redundant DirectWrite ellipsis re-shaping.
        match cell {
            Some(text) => div()
                .w_full()
                .text_size(px(12.0))
                .font_family(mono)
                .text_color(theme.foreground)
                .child(text.clone()),
            None => div()
                .w_full()
                .text_size(px(11.5))
                .font_family(mono)
                .italic()
                .text_color(theme.muted_foreground.opacity(0.65))
                .child(SharedString::new_static("NULL")),
        }
    }

    fn cell_text(&self, row_ix: usize, col_ix: usize, _cx: &App) -> String {
        if col_ix == 0 {
            return (row_ix + 1).to_string();
        }
        let src_col = self.col_order.get(col_ix - 1).copied().unwrap_or(col_ix - 1);
        self.source
            .as_ref()
            .and_then(|s| s.rows.get(row_ix))
            .and_then(|r| r.get(src_col))
            .and_then(|v| v.clone())
            .unwrap_or_else(|| "NULL".to_string())
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use larik_data::QueryColumn;

    use super::*;

    #[test]
    fn preview_truncates_long_cells_while_preserving_full_tsv() {
        let long_val = "x".repeat(300);
        let result = Arc::new(QueryResult {
            statement: None,
            columns: vec![QueryColumn {
                name: "payload".into(),
                data_type: Some("nvarchar".into()),
            }],
            rows: vec![vec![Some(long_val.clone())], vec![None]],
            records_affected: None,
            message: String::new(),
            messages: Vec::new(),
            is_error: false,
            is_truncated: false,
            row_limit: None,
            elapsed: Duration::from_millis(1),
            database: None,
        });

        let mut delegate = ResultTableDelegate::empty();
        delegate.set_result(Some(result));

        let preview = delegate.display_rows[0][0].as_ref().unwrap();
        assert_eq!(preview.chars().count(), MAX_CELL_PREVIEW_CHARS + 1);
        assert!(preview.ends_with('…'));

        let tsv = delegate.to_tsv();
        assert!(tsv.contains(&long_val));
        assert!(tsv.contains("NULL"));
    }
}
