use std::collections::HashMap;
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

/// Upper bound on cached formatted cell previews per table instance.
/// Only visible and recently scrolled cells are formatted on demand so attaching a large
/// result set stays O(columns) instead of O(rows * columns).
const MAX_CELL_CACHE_ENTRIES: usize = 8_192;

/// Upper bound on cached 1-based row number labels per table instance.
const MAX_ROW_NUMBER_CACHE_ENTRIES: usize = 2_048;

/// Virtualized `DataTable` delegate rendering a `QueryResult` with O(columns) attachment
/// and bounded lazy `SharedString` caching: only visible cells are formatted on demand,
/// repeat frames are O(1) refcount clones, and column reordering is O(columns).
pub struct ResultTableDelegate {
    source: Option<Arc<QueryResult>>,
    columns: Vec<Column>,
    column_types: Vec<Option<SharedString>>,
    /// Maps visible data column index (`col_ix - 1`) -> original column index in `source`.
    col_order: Vec<usize>,
    /// Lazy bounded cache of preview-capped `SharedString` values keyed by `(row_ix, src_col)`.
    cell_cache: HashMap<(usize, usize), SharedString>,
    /// Lazy bounded cache of formatted 1-based row numbers keyed by `row_ix`.
    row_number_cache: HashMap<usize, SharedString>,
}

fn format_cell_preview(raw: &str) -> SharedString {
    match raw {
        "" => return SharedString::new_static(""),
        "0" => return SharedString::new_static("0"),
        "1" => return SharedString::new_static("1"),
        _ => {}
    }

    let mut needs_clean = false;
    let mut char_count = 0usize;
    let mut cut_byte_idx = raw.len();
    let mut truncated = false;

    for (byte_idx, ch) in raw.char_indices() {
        if char_count == MAX_CELL_PREVIEW_CHARS {
            cut_byte_idx = byte_idx;
            truncated = true;
            break;
        }
        if matches!(ch, '\r' | '\n' | '\t') {
            needs_clean = true;
        }
        char_count += 1;
    }

    if !needs_clean && !truncated {
        return SharedString::from(Arc::<str>::from(raw));
    }

    let slice = &raw[..cut_byte_idx];
    let mut out = String::with_capacity(slice.len() + if truncated { 3 } else { 0 });
    if needs_clean {
        for ch in slice.chars() {
            match ch {
                '\r' | '\n' | '\t' => out.push(' '),
                other => out.push(other),
            }
        }
    } else {
        out.push_str(slice);
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
            cell_cache: HashMap::new(),
            row_number_cache: HashMap::new(),
        }
    }

    pub fn from_result(result: Arc<QueryResult>) -> Self {
        let mut delegate = Self::empty();
        delegate.set_result(Some(result));
        delegate
    }

    pub fn is_same_result(&self, result: &Arc<QueryResult>) -> bool {
        self.source
            .as_ref()
            .is_some_and(|existing| Arc::ptr_eq(existing, result))
    }

    /// Detach owned result data and lazy caches so they can be dropped on a background thread,
    /// avoiding UI-thread deallocation pauses even if GPUI defers entity cleanup.
    pub fn detach_for_drop(
        &mut self,
    ) -> (
        Option<Arc<QueryResult>>,
        HashMap<(usize, usize), SharedString>,
        HashMap<usize, SharedString>,
    ) {
        self.columns.clear();
        self.column_types.clear();
        self.col_order.clear();
        (
            self.source.take(),
            std::mem::take(&mut self.cell_cache),
            std::mem::take(&mut self.row_number_cache),
        )
    }

    pub fn set_result(&mut self, result: Option<Arc<QueryResult>>) {
        if let (Some(existing), Some(incoming)) = (self.source.as_ref(), result.as_ref()) {
            if Arc::ptr_eq(existing, incoming) {
                return;
            }
        }

        self.source = None;
        self.columns.clear();
        self.column_types.clear();
        self.col_order.clear();
        self.cell_cache.clear();
        self.row_number_cache.clear();

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
        self.columns.reserve(res.columns.len() + 1);
        self.column_types.reserve(res.columns.len() + 1);
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

        self.source = Some(res);
    }

    /// Return the sanitized, preview-capped cell string for `(row_ix, src_col)`, formatting
    /// and caching lazily on first access.
    pub fn preview_cell(&mut self, row_ix: usize, src_col: usize) -> Option<SharedString> {
        if let Some(cached) = self.cell_cache.get(&(row_ix, src_col)) {
            return Some(cached.clone());
        }
        let raw = self
            .source
            .as_ref()?
            .rows
            .get(row_ix)?
            .get(src_col)?
            .as_deref()?;
        let formatted = format_cell_preview(raw);
        if self.cell_cache.len() >= MAX_CELL_CACHE_ENTRIES {
            self.cell_cache.clear();
        }
        self.cell_cache.insert((row_ix, src_col), formatted.clone());
        Some(formatted)
    }

    fn row_number_label(&mut self, row_ix: usize) -> SharedString {
        if let Some(cached) = self.row_number_cache.get(&row_ix) {
            return cached.clone();
        }
        let label = SharedString::from((row_ix + 1).to_string());
        if self.row_number_cache.len() >= MAX_ROW_NUMBER_CACHE_ENTRIES {
            self.row_number_cache.clear();
        }
        self.row_number_cache.insert(row_ix, label.clone());
        label
    }

    /// Format the active grid as tab-separated values for clipboard export (full un-truncated data).
    pub fn to_tsv(&self) -> String {
        let Some(src) = self.source.as_ref() else {
            return String::new();
        };
        if self.columns.len() <= 1 {
            return String::new();
        }
        let estimated = src
            .rows
            .len()
            .saturating_mul(self.col_order.len())
            .saturating_mul(12)
            .min(16 * 1024 * 1024);
        let mut out = String::with_capacity(estimated);
        for (i, col) in self.columns.iter().skip(1).enumerate() {
            if i > 0 {
                out.push('\t');
            }
            out.push_str(col.name.as_ref());
        }
        out.push('\n');

        for row in &src.rows {
            for (vis_idx, &src_col_idx) in self.col_order.iter().enumerate() {
                if vis_idx > 0 {
                    out.push('\t');
                }
                match row.get(src_col_idx).and_then(|v| v.as_deref()) {
                    Some(v) => {
                        if v.contains(['\t', '\r', '\n']) {
                            for ch in v.chars() {
                                match ch {
                                    '\t' | '\r' | '\n' => out.push(' '),
                                    other => out.push(other),
                                }
                            }
                        } else {
                            out.push_str(v);
                        }
                    }
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
        self.source.as_ref().map_or(0, |s| s.rows.len())
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
            let label = self.row_number_label(row_ix);
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
        let cell = self.preview_cell(row_ix, src_col);

        // `DataTable::render_cell` already applies `overflow_hidden().whitespace_nowrap()`,
        // so returning a single flat `div` with a cached `SharedString` avoids both
        // per-frame heap allocations and redundant DirectWrite ellipsis re-shaping.
        match cell {
            Some(text) => div()
                .w_full()
                .text_size(px(12.0))
                .font_family(mono)
                .text_color(theme.foreground)
                .child(text),
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
    use std::time::{Duration, Instant};

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

        // Attachment is lazy: nothing is formatted until rendered/queried.
        assert!(delegate.cell_cache.is_empty());
        assert!(delegate.row_number_cache.is_empty());

        let preview = delegate.preview_cell(0, 0).unwrap();
        assert_eq!(preview.chars().count(), MAX_CELL_PREVIEW_CHARS + 1);
        assert!(preview.ends_with('…'));
        assert!(delegate.preview_cell(1, 0).is_none());
        assert_eq!(delegate.cell_cache.len(), 1);

        let tsv = delegate.to_tsv();
        assert!(tsv.contains(&long_val));
        assert!(tsv.contains("NULL"));
    }

    #[test]
    fn large_result_attachment_stays_lazy_and_fast() {
        let rows = 2_699;
        let cols = 64;
        let columns: Vec<QueryColumn> = (0..cols)
            .map(|c| QueryColumn {
                name: format!("col_{c}"),
                data_type: Some("nvarchar".into()),
            })
            .collect();
        let data_rows: Vec<Vec<Option<String>>> = (0..rows)
            .map(|r| {
                (0..cols)
                    .map(|c| Some(format!("row_{r}_col_{c}_value")))
                    .collect()
            })
            .collect();
        let result = Arc::new(QueryResult {
            statement: None,
            columns,
            rows: data_rows,
            records_affected: Some(rows as i64),
            message: format!("{rows} rows"),
            messages: Vec::new(),
            is_error: false,
            is_truncated: false,
            row_limit: Some(5_000),
            elapsed: Duration::from_millis(10),
            database: None,
        });

        let started = Instant::now();
        let mut delegate = ResultTableDelegate::from_result(Arc::clone(&result));
        let attach_elapsed = started.elapsed();

        assert!(delegate.is_same_result(&result));
        assert_eq!(delegate.columns.len(), cols + 1);
        assert!(delegate.cell_cache.is_empty());
        assert!(delegate.row_number_cache.is_empty());
        // Attaching 2699x64 should be O(columns), well under 50ms even in unoptimized debug builds.
        assert!(
            attach_elapsed < Duration::from_millis(50),
            "expected O(columns) attachment, took {attach_elapsed:?}"
        );

        // Formatting a visible viewport (30 rows x 12 cols) only populates those 360 entries.
        for r in 0..30 {
            let _ = delegate.row_number_label(r);
            for c in 0..12 {
                assert!(delegate.preview_cell(r, c).is_some());
            }
        }
        assert_eq!(delegate.cell_cache.len(), 360);
        assert_eq!(delegate.row_number_cache.len(), 30);

        // Re-attaching the exact same Arc<QueryResult> is a no-op and preserves caches.
        delegate.set_result(Some(Arc::clone(&result)));
        assert_eq!(delegate.cell_cache.len(), 360);
    }
}
