use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::Icon;
use gpui_kit::component::table::{Column, ColumnFixed, TableDelegate, TableState};
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    App, Context, IntoElement, ParentElement, SharedString, Styled, TextAlign, Window, div, px,
};
use larik_data::grid_actions::{
    ColumnReference, RowEditPlan, RowEditResolution, SqlDialectKind, build_dml_plan,
    format_in_clause, format_tsv_selection, format_values_clause, resolve_column_origins,
    resolve_column_references, resolve_row_edit,
};
use larik_data::{QueryResult, TableSchema};

/// Maximum characters shaped per cell in the virtualized result grid.
/// Keeps DirectWrite shaping instant even on wide `NVARCHAR(MAX)` / JSON / XML columns,
/// while full original cell values remain intact in `source` for clipboard export and preview.
const MAX_CELL_PREVIEW_CHARS: usize = 160;

/// Upper bound on cached formatted cell previews per table instance.
const MAX_CELL_CACHE_ENTRIES: usize = 8_192;

/// Upper bound on cached 1-based row number labels per table instance.
const MAX_ROW_NUMBER_CACHE_ENTRIES: usize = 2_048;

/// Rectangular cell selection in visual data-column coordinates (`0..data_cols`, excluding `#` gutter).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct GridSelection {
    pub anchor_row: usize,
    pub anchor_col: usize,
    pub focus_row: usize,
    pub focus_col: usize,
}

impl GridSelection {
    pub fn single(row: usize, col: usize) -> Self {
        Self {
            anchor_row: row,
            anchor_col: col,
            focus_row: row,
            focus_col: col,
        }
    }

    pub fn normalized(self) -> (usize, usize, usize, usize) {
        (
            self.anchor_row.min(self.focus_row),
            self.anchor_col.min(self.focus_col),
            self.anchor_row.max(self.focus_row),
            self.anchor_col.max(self.focus_col),
        )
    }

    pub fn contains(self, row: usize, col: usize) -> bool {
        let (top, left, bottom, right) = self.normalized();
        row >= top && row <= bottom && col >= left && col <= right
    }

    pub fn is_single_cell(self) -> bool {
        self.anchor_row == self.focus_row && self.anchor_col == self.focus_col
    }

    pub fn dimensions(self) -> (usize, usize) {
        let (top, left, bottom, right) = self.normalized();
        (bottom - top + 1, right - left + 1)
    }
}

#[derive(Debug, Clone)]
enum PendingEditAction {
    Cell {
        row_ix: usize,
        src_col: usize,
        /// Previous staged entry before this edit (`None` = was unedited).
        previous: Option<Option<String>>,
    },
    DeleteRows {
        /// `(row_ix, was_deleted, cleared_cell_edits)`
        entries: Vec<(usize, bool, Vec<(usize, Option<String>)>)>,
    },
}

/// Virtualized `DataTable` delegate rendering a `QueryResult` with O(columns) attachment,
/// bounded lazy `SharedString` caching, rectangular selection, staged cell edits / row deletes,
/// and DB foreign-key reference metadata.
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
    /// Rectangular selection in visual data-column coordinates (`0..col_order.len()`).
    selection: Option<GridSelection>,
    /// Resolved row editability for this result.
    edit_resolution: RowEditResolution,
    /// Resolved DB foreign-key references keyed by `src_col`.
    col_references: HashMap<usize, ColumnReference>,
    /// Schema identity key (`Arc::as_ptr` + len) so metadata is resolved at most once per schema.
    metadata_schema_key: Option<(usize, usize)>,
    /// Staged cell edits keyed by `(row_ix, src_col)`.
    staged_edits: HashMap<(usize, usize), Option<String>>,
    /// Row indices staged for deletion.
    deleted_rows: HashSet<usize>,
    /// Undo history for staged cell edits and row deletions.
    undo_stack: Vec<PendingEditAction>,
    /// Monotonically incremented whenever backing result data or staged edits change.
    data_version: u64,
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
            selection: None,
            edit_resolution: RowEditResolution::default(),
            col_references: HashMap::new(),
            metadata_schema_key: None,
            staged_edits: HashMap::new(),
            deleted_rows: HashSet::new(),
            undo_stack: Vec::new(),
            data_version: 0,
        }
    }

    pub fn data_version(&self) -> u64 {
        self.data_version
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

    pub fn source(&self) -> Option<&Arc<QueryResult>> {
        self.source.as_ref()
    }

    pub fn data_col_count(&self) -> usize {
        self.col_order.len()
    }

    pub fn row_count(&self) -> usize {
        self.source.as_ref().map_or(0, |s| s.rows.len())
    }

    pub fn src_col_for_vis(&self, vis_col: usize) -> Option<usize> {
        self.col_order.get(vis_col).copied()
    }

    pub fn column_name_for_src(&self, src_col: usize) -> Option<&str> {
        self.source
            .as_ref()?
            .columns
            .get(src_col)
            .map(|c| c.name.as_str())
    }

    pub fn column_type_for_src(&self, src_col: usize) -> Option<&str> {
        self.source
            .as_ref()?
            .columns
            .get(src_col)
            .and_then(|c| c.data_type.as_deref())
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
        self.selection = None;
        self.edit_resolution = RowEditResolution::default();
        self.col_references.clear();
        self.metadata_schema_key = None;
        self.staged_edits.clear();
        self.deleted_rows.clear();
        self.undo_stack.clear();
        self.data_version = self.data_version.wrapping_add(1);
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
        self.selection = None;
        self.edit_resolution = RowEditResolution::default();
        self.col_references.clear();
        self.metadata_schema_key = None;
        self.staged_edits.clear();
        self.deleted_rows.clear();
        self.undo_stack.clear();
        self.data_version = self.data_version.wrapping_add(1);

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
            let width = px((char_len as f32 * 8.2 + 34.0).clamp(104.0, 340.0));

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

    /// Resolve row-editability and DB foreign-key references once per `(result, schema)`.
    pub fn ensure_schema_metadata(
        &mut self,
        fallback_statement: Option<&str>,
        tables: &Arc<Vec<TableSchema>>,
    ) {
        if tables.is_empty() {
            return;
        }
        let key = (Arc::as_ptr(tables) as usize, tables.len());
        if self.metadata_schema_key == Some(key) {
            return;
        }
        let Some(res) = self.source.as_ref() else {
            return;
        };
        let stmt = res
            .statement
            .as_deref()
            .filter(|s| !s.trim().is_empty())
            .or(fallback_statement);
        let origins = resolve_column_origins(stmt, res, tables);
        self.edit_resolution = resolve_row_edit(stmt, res, tables, &origins);
        self.col_references = resolve_column_references(&origins, tables);
        self.metadata_schema_key = Some(key);
    }

    pub fn edit_resolution(&self) -> &RowEditResolution {
        &self.edit_resolution
    }

    pub fn column_reference(&self, src_col: usize) -> Option<&ColumnReference> {
        self.col_references.get(&src_col)
    }

    pub fn staged_edits(&self) -> &HashMap<(usize, usize), Option<String>> {
        &self.staged_edits
    }

    // ---------------------------------------------------------------- selection

    pub fn selection(&self) -> Option<GridSelection> {
        self.selection
    }

    pub fn focused_cell(&self) -> Option<(usize, usize)> {
        self.selection.map(|s| (s.focus_row, s.focus_col))
    }

    pub fn select_cell(&mut self, row: usize, vis_col: usize, extend: bool) {
        let rows = self.row_count();
        let cols = self.data_col_count();
        if rows == 0 || cols == 0 {
            self.selection = None;
            return;
        }
        let r = row.min(rows - 1);
        let c = vis_col.min(cols - 1);
        self.selection = Some(match (extend, self.selection) {
            (true, Some(prev)) => GridSelection {
                anchor_row: prev.anchor_row.min(rows - 1),
                anchor_col: prev.anchor_col.min(cols - 1),
                focus_row: r,
                focus_col: c,
            },
            _ => GridSelection::single(r, c),
        });
    }

    pub fn select_row(&mut self, row: usize, extend: bool) {
        let rows = self.row_count();
        let cols = self.data_col_count();
        if rows == 0 || cols == 0 {
            self.selection = None;
            return;
        }
        let r = row.min(rows - 1);
        let anchor_row = if extend {
            self.selection.map_or(r, |s| s.anchor_row.min(rows - 1))
        } else {
            r
        };
        self.selection = Some(GridSelection {
            anchor_row,
            anchor_col: 0,
            focus_row: r,
            focus_col: cols - 1,
        });
    }

    pub fn select_column(&mut self, vis_col: usize) {
        let rows = self.row_count();
        let cols = self.data_col_count();
        if rows == 0 || cols == 0 {
            self.selection = None;
            return;
        }
        let c = vis_col.min(cols - 1);
        self.selection = Some(GridSelection {
            anchor_row: 0,
            anchor_col: c,
            focus_row: rows - 1,
            focus_col: c,
        });
    }

    pub fn select_all(&mut self) {
        let rows = self.row_count();
        let cols = self.data_col_count();
        if rows == 0 || cols == 0 {
            self.selection = None;
            return;
        }
        self.selection = Some(GridSelection {
            anchor_row: 0,
            anchor_col: 0,
            focus_row: rows - 1,
            focus_col: cols - 1,
        });
    }

    /// WinUI right-click rule: preserve existing block if clicked cell is inside it (moving focus);
    /// otherwise collapse selection to the clicked cell (or full row when gutter is right-clicked).
    pub fn handle_right_click(&mut self, row: usize, vis_col_opt: Option<usize>) {
        let rows = self.row_count();
        let cols = self.data_col_count();
        if rows == 0 || cols == 0 {
            return;
        }
        let r = row.min(rows - 1);
        match vis_col_opt {
            Some(c_raw) => {
                let c = c_raw.min(cols - 1);
                if let Some(prev) = self.selection {
                    if prev.contains(r, c) {
                        self.selection = Some(GridSelection {
                            focus_row: r,
                            focus_col: c,
                            ..prev
                        });
                        return;
                    }
                }
                self.selection = Some(GridSelection::single(r, c));
            }
            None => {
                if let Some(prev) = self.selection {
                    let (top, left, bottom, right) = prev.normalized();
                    if r >= top && r <= bottom && left == 0 && right + 1 == cols {
                        self.selection = Some(GridSelection {
                            focus_row: r,
                            ..prev
                        });
                        return;
                    }
                }
                self.select_row(r, false);
            }
        }
    }

    pub fn selected_rows(&self) -> Vec<usize> {
        let Some(sel) = self.selection else {
            return Vec::new();
        };
        let (top, _, bottom, _) = sel.normalized();
        (top..=bottom).collect()
    }

    // ---------------------------------------------------------------- staged edits & deletes

    /// Current display value for `(row_ix, src_col)`, honoring unsaved staged edits.
    pub fn display_value(&self, row_ix: usize, src_col: usize) -> Option<&str> {
        if let Some(staged) = self.staged_edits.get(&(row_ix, src_col)) {
            return staged.as_deref();
        }
        self.source
            .as_ref()?
            .rows
            .get(row_ix)?
            .get(src_col)?
            .as_deref()
    }

    pub fn original_value(&self, row_ix: usize, src_col: usize) -> Option<&str> {
        self.source
            .as_ref()?
            .rows
            .get(row_ix)?
            .get(src_col)?
            .as_deref()
    }

    pub fn is_cell_edited(&self, row_ix: usize, src_col: usize) -> bool {
        self.staged_edits.contains_key(&(row_ix, src_col))
    }

    pub fn is_row_deleted(&self, row_ix: usize) -> bool {
        self.deleted_rows.contains(&row_ix)
    }

    pub fn has_pending_changes(&self) -> bool {
        !self.staged_edits.is_empty() || !self.deleted_rows.is_empty()
    }

    pub fn pending_counts(&self) -> (usize, usize) {
        (self.staged_edits.len(), self.deleted_rows.len())
    }

    /// Stage a new value for `(row_ix, src_col)`. Editing back to original removes the staged edit.
    pub fn stage_cell_edit(
        &mut self,
        row_ix: usize,
        src_col: usize,
        new_value: Option<String>,
    ) -> bool {
        let orig = self.original_value(row_ix, src_col).map(str::to_string);
        let prev = self.staged_edits.get(&(row_ix, src_col)).cloned();
        let current = prev.clone().unwrap_or_else(|| orig.clone());
        if current == new_value {
            return false;
        }
        if new_value == orig {
            self.staged_edits.remove(&(row_ix, src_col));
        } else {
            self.staged_edits.insert((row_ix, src_col), new_value);
        }
        self.cell_cache.remove(&(row_ix, src_col));
        self.undo_stack.push(PendingEditAction::Cell {
            row_ix,
            src_col,
            previous: prev,
        });
        self.data_version = self.data_version.wrapping_add(1);
        true
    }

    pub fn revert_cell(&mut self, row_ix: usize, src_col: usize) -> bool {
        if self.staged_edits.remove(&(row_ix, src_col)).is_some() {
            self.cell_cache.remove(&(row_ix, src_col));
            self.undo_stack.retain(|act| {
                !matches!(
                    act,
                    PendingEditAction::Cell {
                        row_ix: r,
                        src_col: c,
                        ..
                    } if *r == row_ix && *c == src_col
                )
            });
            self.data_version = self.data_version.wrapping_add(1);
            true
        } else {
            false
        }
    }

    /// Toggle deletion state for `rows`. Marking a row deleted clears any staged edits on that row.
    pub fn toggle_delete_rows(&mut self, rows: Vec<usize>) -> bool {
        let max_rows = self.row_count();
        let mut unique: Vec<usize> = rows.into_iter().filter(|&r| r < max_rows).collect();
        unique.sort_unstable();
        unique.dedup();
        if unique.is_empty() {
            return false;
        }

        // If every selected row is already marked deleted, unmark all; otherwise mark all.
        let unmark_all = unique.iter().all(|r| self.deleted_rows.contains(r));
        let mut entries = Vec::with_capacity(unique.len());

        for r in unique {
            let was_deleted = self.deleted_rows.contains(&r);
            if unmark_all {
                self.deleted_rows.remove(&r);
                entries.push((r, was_deleted, Vec::new()));
            } else {
                self.deleted_rows.insert(r);
                let mut cleared = Vec::new();
                let keys: Vec<(usize, usize)> = self
                    .staged_edits
                    .keys()
                    .copied()
                    .filter(|&(row_ix, _)| row_ix == r)
                    .collect();
                for k in keys {
                    if let Some(val) = self.staged_edits.remove(&k) {
                        cleared.push((k.1, val));
                        self.cell_cache.remove(&k);
                    }
                }
                entries.push((r, was_deleted, cleared));
            }
        }

        self.undo_stack
            .push(PendingEditAction::DeleteRows { entries });
        self.data_version = self.data_version.wrapping_add(1);
        true
    }

    pub fn undo_last_edit(&mut self) -> bool {
        let Some(action) = self.undo_stack.pop() else {
            return false;
        };
        match action {
            PendingEditAction::Cell {
                row_ix,
                src_col,
                previous,
            } => {
                match previous {
                    Some(prev_val) => {
                        self.staged_edits.insert((row_ix, src_col), prev_val);
                    }
                    None => {
                        self.staged_edits.remove(&(row_ix, src_col));
                    }
                }
                self.cell_cache.remove(&(row_ix, src_col));
            }
            PendingEditAction::DeleteRows { entries } => {
                for (row_ix, was_deleted, cleared) in entries {
                    if was_deleted {
                        self.deleted_rows.insert(row_ix);
                    } else {
                        self.deleted_rows.remove(&row_ix);
                    }
                    for (src_col, val) in cleared {
                        self.staged_edits.insert((row_ix, src_col), val);
                        self.cell_cache.remove(&(row_ix, src_col));
                    }
                }
            }
        }
        self.data_version = self.data_version.wrapping_add(1);
        true
    }

    pub fn discard_all_edits(&mut self) {
        for &(r, c) in self.staged_edits.keys() {
            self.cell_cache.remove(&(r, c));
        }
        self.staged_edits.clear();
        self.deleted_rows.clear();
        self.undo_stack.clear();
        self.data_version = self.data_version.wrapping_add(1);
    }

    pub fn build_save_plan(
        &self,
        dialect: SqlDialectKind,
        tables: &[TableSchema],
    ) -> Result<RowEditPlan, String> {
        let Some(res) = self.source.as_ref() else {
            return Err("No active result set.".to_string());
        };
        let focus_src_col = self
            .selection
            .and_then(|s| self.src_col_for_vis(s.focus_col));
        build_dml_plan(
            dialect,
            &self.edit_resolution,
            res,
            tables,
            &self.staged_edits,
            &self.deleted_rows,
            focus_src_col,
        )
    }

    /// Apply committed staged edits & row deletions into a freshly updated `Arc<QueryResult>`,
    /// clearing pending state and clamping selection (matches WinUI post-save patching).
    pub fn apply_committed_changes(&mut self) -> Option<Arc<QueryResult>> {
        let existing = self.source.as_ref()?;
        let mut updated = (**existing).clone();

        for (&(row_ix, src_col), new_val) in &self.staged_edits {
            if self.deleted_rows.contains(&row_ix) {
                continue;
            }
            if let Some(row) = updated.rows.get_mut(row_ix) {
                if let Some(cell) = row.get_mut(src_col) {
                    *cell = new_val.clone();
                }
            }
        }

        if !self.deleted_rows.is_empty() {
            let mut del: Vec<usize> = self.deleted_rows.iter().copied().collect();
            del.sort_unstable_by(|a, b| b.cmp(a));
            for row_ix in del {
                if row_ix < updated.rows.len() {
                    updated.rows.remove(row_ix);
                }
            }
            updated.message = format!("{} rows", updated.rows.len());
        }

        let arc = Arc::new(updated);
        self.source = Some(Arc::clone(&arc));
        self.staged_edits.clear();
        self.deleted_rows.clear();
        self.undo_stack.clear();
        self.cell_cache.clear();
        self.row_number_cache.clear();
        self.data_version = self.data_version.wrapping_add(1);

        // Clamp selection to new row count.
        let new_rows = arc.rows.len();
        if new_rows == 0 {
            self.selection = None;
        } else if let Some(sel) = self.selection {
            let max_r = new_rows - 1;
            self.selection = Some(GridSelection {
                anchor_row: sel.anchor_row.min(max_r),
                anchor_col: sel.anchor_col,
                focus_row: sel.focus_row.min(max_r),
                focus_col: sel.focus_col,
            });
        }

        Some(arc)
    }

    // ---------------------------------------------------------------- copy operations

    /// Copy the current selection (or focused cell) as TSV (honoring staged edits).
    /// For a 1x1 cell without headers, copies the raw cell text directly (no extra CSV quotes).
    pub fn copy_selection(&self, with_headers: bool) -> Option<(String, usize, usize)> {
        let src = self.source.as_ref()?;
        let sel = self.selection?;
        let (top, left, bottom, right) = sel.normalized();
        if top >= src.rows.len() || left >= self.col_order.len() {
            return None;
        }
        let bottom = bottom.min(src.rows.len() - 1);
        let right = right.min(self.col_order.len() - 1);

        // 1x1 single cell copy without headers: copy clean cell text directly.
        if !with_headers && top == bottom && left == right {
            let src_col = self.col_order[left];
            let text = self
                .display_value(top, src_col)
                .unwrap_or("")
                .to_string();
            return Some((text, 1, 1));
        }

        let headers: Vec<&str> = (left..=right)
            .map(|vc| {
                self.columns
                    .get(vc + 1)
                    .map(|c| c.name.as_ref())
                    .unwrap_or("")
            })
            .collect();
        let mut rows_data: Vec<Vec<Option<&str>>> = Vec::with_capacity(bottom - top + 1);
        for r in top..=bottom {
            let mut row_cells = Vec::with_capacity(right - left + 1);
            for vc in left..=right {
                let src_col = self.col_order[vc];
                row_cells.push(self.display_value(r, src_col));
            }
            rows_data.push(row_cells);
        }
        let text = format_tsv_selection(&headers, &rows_data, with_headers);
        Some((text, bottom - top + 1, right - left + 1))
    }

    /// Copy selected cells flattened into `IN (v1, v2, ...)` (WinUI `Copy as IN clause`).
    pub fn copy_selection_as_in(&self, dialect: SqlDialectKind) -> Option<(String, usize)> {
        let src = self.source.as_ref()?;
        let sel = self.selection?;
        let (top, left, bottom, right) = sel.normalized();
        if top >= src.rows.len() || left >= self.col_order.len() {
            return None;
        }
        let bottom = bottom.min(src.rows.len() - 1);
        let right = right.min(self.col_order.len() - 1);

        let mut cells = Vec::with_capacity((bottom - top + 1) * (right - left + 1));
        for r in top..=bottom {
            for vc in left..=right {
                let src_col = self.col_order[vc];
                let val = self.display_value(r, src_col);
                let ty = src.columns.get(src_col).and_then(|c| c.data_type.as_deref());
                cells.push((val, ty));
            }
        }
        let count = cells.len();
        Some((format_in_clause(dialect, &cells), count))
    }

    /// Copy selected rows as `VALUES (...),\n       (...)` (WinUI `Copy as INSERT` / VALUES).
    /// Matching WinUI: a 1x1 cell selection expands horizontally to the entire row.
    pub fn copy_selection_as_values(&self, dialect: SqlDialectKind) -> Option<(String, usize)> {
        let src = self.source.as_ref()?;
        let sel = self.selection?;
        let (top, mut left, bottom, mut right) = sel.normalized();
        if top >= src.rows.len() || left >= self.col_order.len() {
            return None;
        }
        let bottom = bottom.min(src.rows.len() - 1);
        if top == bottom && left == right {
            left = 0;
            right = self.col_order.len() - 1;
        } else {
            right = right.min(self.col_order.len() - 1);
        }

        let mut rows = Vec::with_capacity(bottom - top + 1);
        for r in top..=bottom {
            let mut row = Vec::with_capacity(right - left + 1);
            for vc in left..=right {
                let src_col = self.col_order[vc];
                let val = self.display_value(r, src_col);
                let ty = src.columns.get(src_col).and_then(|c| c.data_type.as_deref());
                row.push((val, ty));
            }
            rows.push(row);
        }
        let count = rows.len();
        Some((format_values_clause(dialect, &rows), count))
    }

    // ---------------------------------------------------------------- preview cache & TSV

    /// Return the sanitized, preview-capped cell string for `(row_ix, src_col)`, formatting
    /// and caching lazily on first access (honoring staged edits).
    pub fn preview_cell(&mut self, row_ix: usize, src_col: usize) -> Option<SharedString> {
        if let Some(staged) = self.staged_edits.get(&(row_ix, src_col)) {
            return staged.as_deref().map(format_cell_preview);
        }
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

        for row_ix in 0..src.rows.len() {
            for (vis_idx, &src_col_idx) in self.col_order.iter().enumerate() {
                if vis_idx > 0 {
                    out.push('\t');
                }
                match self.display_value(row_ix, src_col_idx) {
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

        let has_fk = self
            .col_order
            .get(col_ix - 1)
            .is_some_and(|src_col| self.col_references.contains_key(src_col));

        div()
            .w_full()
            .flex()
            .items_center()
            .justify_between()
            .gap_1p5()
            .overflow_hidden()
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .min_w_0()
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
                    .when(has_fk, |el| {
                        el.child(
                            Icon::new(IconName::Key)
                                .size(px(10.5))
                                .text_color(theme.primary.opacity(0.85)),
                        )
                    }),
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
        let is_deleted = self.deleted_rows.contains(&row_ix);

        if col_ix == 0 {
            let label = self.row_number_label(row_ix);
            return div()
                .w_full()
                .h_full()
                .flex()
                .items_center()
                .justify_end()
                .pr_1()
                .text_size(px(11.5))
                .font_family(mono)
                .when(is_deleted, |el| {
                    el.bg(theme.danger.opacity(0.18))
                        .text_color(theme.danger)
                })
                .when(!is_deleted, |el| {
                    el.text_color(theme.muted_foreground.opacity(0.75))
                })
                .child(label);
        }

        let vis_col = col_ix - 1;
        let src_col = self.col_order.get(vis_col).copied().unwrap_or(vis_col);
        let is_edited = self.staged_edits.contains_key(&(row_ix, src_col));
        let in_multi_selection = self.selection.is_some_and(|sel| {
            !sel.is_single_cell() && sel.contains(row_ix, vis_col)
        });
        let cell = self.preview_cell(row_ix, src_col);

        let mut wrapper = div()
            .relative()
            .w_full()
            .h_full()
            .flex()
            .items_center()
            .font_family(mono)
            .when(is_deleted, |el| el.bg(theme.danger.opacity(0.15)))
            .when(!is_deleted && is_edited, |el| {
                el.bg(theme.primary.opacity(0.16))
            })
            .when(!is_deleted && !is_edited && in_multi_selection, |el| {
                el.bg(theme.primary.opacity(0.12))
            });

        wrapper = match cell {
            Some(text) => wrapper
                .text_size(px(12.0))
                .text_color(if is_deleted {
                    theme.muted_foreground
                } else {
                    theme.foreground
                })
                .when(is_deleted, |el| el.line_through())
                .child(text),
            None => wrapper
                .text_size(px(11.5))
                .italic()
                .text_color(theme.muted_foreground.opacity(0.65))
                .when(is_deleted, |el| el.line_through())
                .child(SharedString::new_static("NULL")),
        };

        // 5x5 top-right status dot matching WinUI ResultRowView edited/deleted indicator.
        if is_deleted || is_edited {
            wrapper = wrapper.child(
                div()
                    .absolute()
                    .top(px(2.0))
                    .right(px(2.0))
                    .size(px(5.0))
                    .rounded_full()
                    .bg(if is_deleted {
                        theme.danger
                    } else {
                        theme.primary
                    }),
            );
        }

        wrapper
    }

    fn cell_text(&self, row_ix: usize, col_ix: usize, _cx: &App) -> String {
        if col_ix == 0 {
            return (row_ix + 1).to_string();
        }
        let src_col = self.col_order.get(col_ix - 1).copied().unwrap_or(col_ix - 1);
        self.display_value(row_ix, src_col)
            .unwrap_or("NULL")
            .to_string()
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

    #[test]
    fn selection_copy_and_staged_edits_work_together() {
        let result = Arc::new(QueryResult {
            statement: Some("SELECT id, name FROM dbo.customers".into()),
            columns: vec![
                QueryColumn {
                    name: "id".into(),
                    data_type: Some("int".into()),
                },
                QueryColumn {
                    name: "name".into(),
                    data_type: Some("nvarchar(120)".into()),
                },
            ],
            rows: vec![
                vec![Some("1".into()), Some("Alice".into())],
                vec![Some("2".into()), None],
            ],
            records_affected: Some(2),
            message: "2 rows".into(),
            messages: Vec::new(),
            is_error: false,
            is_truncated: false,
            row_limit: None,
            elapsed: Duration::from_millis(1),
            database: None,
        });

        let mut delegate = ResultTableDelegate::from_result(result);
        delegate.select_cell(0, 1, false);
        assert!(delegate.stage_cell_edit(0, 1, Some("Alicia".into())));
        assert_eq!(delegate.pending_counts(), (1, 0));

        // Single cell copy reflects staged edit
        let (cell_copy, r, c) = delegate.copy_selection(false).unwrap();
        assert_eq!((cell_copy.as_str(), r, c), ("Alicia", 1, 1));

        // Copy as VALUES on 1x1 selection expands to entire row
        let (vals, rows) = delegate.copy_selection_as_values(SqlDialectKind::TSql).unwrap();
        assert_eq!(rows, 1);
        assert_eq!(vals, "VALUES (1, N'Alicia')");

        // Multi-row copy as IN
        delegate.select_cell(1, 1, true);
        let (in_sql, count) = delegate.copy_selection_as_in(SqlDialectKind::TSql).unwrap();
        assert_eq!(count, 2);
        assert_eq!(in_sql, "IN (N'Alicia', NULL)");

        // Undo reverts staged edit
        assert!(delegate.undo_last_edit());
        assert_eq!(delegate.pending_counts(), (0, 0));
        assert_eq!(delegate.display_value(0, 1), Some("Alice"));
    }
}
