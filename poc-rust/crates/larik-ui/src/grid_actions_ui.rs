use std::collections::HashSet;
use std::ops::Range;
use std::sync::Arc;
use std::time::Duration;

use gpui_kit::assets::IconName;
use gpui_kit::base::Disableable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState, Textarea, TextareaState};
use gpui_kit::component::scroll::Scrollbar;
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::table::{TableEvent, TableState};
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, AppContext as _, ClipboardItem, Context, Entity, Focusable as _, FontStyle,
    FontWeight, HighlightStyle, InteractiveElement as _, IntoElement, KeyDownEvent,
    ListHorizontalSizingBehavior, MouseButton, ParentElement as _, Pixels, Point, ScrollStrategy,
    SharedString, StatefulInteractiveElement as _, Styled as _, StyledText, Subscription,
    UniformListScrollHandle, Window, div, hsla, px, uniform_list,
};
use larik_core::session::ReferenceLookupOutcome;
use larik_data::grid_actions::{
    CellFormat, CellSyntaxToken, CellSyntaxTokenKind, RowEditPlan, SqlDialectKind,
    build_reference_filters, format_cell_content, tokenize_cell_syntax_capped,
};

use crate::results_table::ResultTableDelegate;
use crate::studio::StudioView;
use crate::theme::StudioPalette;

const MAX_INLINE_EDIT_CHARS: usize = 8_000;
const MULTILINE_THRESHOLD_CHARS: usize = 160;

#[derive(Clone)]
pub(crate) struct GridContextMenu {
    pub(crate) result_id: String,
    pub(crate) row_ix: usize,
    pub(crate) vis_col_opt: Option<usize>,
    pub(crate) position: Point<Pixels>,
}

#[derive(Clone)]
pub(crate) struct GridStatusToast {
    pub(crate) message: SharedString,
    pub(crate) is_error: bool,
    pub(crate) seq: u64,
}

pub(crate) enum CellEditorInput {
    Single(Entity<InputState>),
    Multi(Entity<TextareaState>),
}

pub(crate) struct ActiveCellEditor {
    pub(crate) result_id: String,
    pub(crate) row_ix: usize,
    pub(crate) vis_col: usize,
    pub(crate) src_col: usize,
    pub(crate) col_name: SharedString,
    pub(crate) col_type: Option<SharedString>,
    pub(crate) input: CellEditorInput,
    pub(crate) is_null: bool,
    pub(crate) multiline: bool,
    pub(crate) _input_sub: Subscription,
}

impl ActiveCellEditor {
    fn current_text(&self, cx: &gpui_kit::App) -> String {
        match &self.input {
            CellEditorInput::Single(e) => e.read(cx).value().to_string(),
            CellEditorInput::Multi(e) => e.read(cx).value().to_string(),
        }
    }

    fn focus(&self, window: &mut Window, cx: &mut gpui_kit::App) {
        match &self.input {
            CellEditorInput::Single(e) => e.update(cx, |s, cx| s.focus(window, cx)),
            CellEditorInput::Multi(e) => e.update(cx, |s, cx| s.focus(window, cx)),
        }
    }
}

pub(crate) struct SaveReviewModal {
    pub(crate) tab_id: String,
    pub(crate) result_id: String,
    pub(crate) plan: RowEditPlan,
    pub(crate) saving: bool,
    pub(crate) error: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum PreviewMode {
    Value,
    Reference,
}

pub(crate) enum ReferencePreviewState {
    Idle,
    Loading,
    NullValue,
    Error(String),
    Loaded {
        outcome: ReferenceLookupOutcome,
        matched_set: HashSet<usize>,
        warning: Option<String>,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct PreviewValueCacheKey {
    result_id: String,
    row_ix: usize,
    src_col: usize,
    data_version: u64,
    has_fk: bool,
    format: CellFormat,
    is_dark: bool,
}

struct PreviewLine {
    text: SharedString,
    highlights: Vec<(Range<usize>, HighlightStyle)>,
}

enum PreviewBodyData {
    Null,
    Empty,
    /// Small plain text (<= 4 KB and <= 60 lines) rendered with natural word-wrap.
    WrappedPlain {
        text: SharedString,
        meta_label: Option<SharedString>,
    },
    /// Virtualized line list (all JSON/XML and all large text) rendered via `uniform_list`.
    VirtualizedLines {
        lines: Vec<PreviewLine>,
        longest_line_ix: usize,
        meta_label: Option<SharedString>,
    },
}

struct PreviewValueCache {
    key: PreviewValueCacheKey,
    row_ix: usize,
    col_name: SharedString,
    col_type: Option<SharedString>,
    is_edited: bool,
    fk: Option<larik_data::grid_actions::ColumnReference>,
    full_text: Option<SharedString>,
    body: PreviewBodyData,
}

pub(crate) struct GridPreviewState {
    pub(crate) mode: PreviewMode,
    pub(crate) format: CellFormat,
    pub(crate) ref_state: ReferencePreviewState,
    pub(crate) tracked_result_id: Option<String>,
    pub(crate) tracked_row: Option<usize>,
    pub(crate) tracked_src_col: Option<usize>,
    pub(crate) request_seq: u64,
    pub(crate) copied_toast: bool,
    value_cache: Option<PreviewValueCache>,
    value_scroll_handle: UniformListScrollHandle,
    ref_scroll_handle: UniformListScrollHandle,
}

impl GridPreviewState {
    fn new(mode: PreviewMode) -> Self {
        Self {
            mode,
            format: CellFormat::Auto,
            ref_state: ReferencePreviewState::Idle,
            tracked_result_id: None,
            tracked_row: None,
            tracked_src_col: None,
            request_seq: 0,
            copied_toast: false,
            value_cache: None,
            value_scroll_handle: UniformListScrollHandle::new(),
            ref_scroll_handle: UniformListScrollHandle::new(),
        }
    }
}

impl StudioView {
    // ---------------------------------------------------------------- helpers

    pub(crate) fn active_sql_dialect(&self) -> SqlDialectKind {
        let dialect = self
            .state
            .active_space()
            .and_then(|s| self.catalog.find(&s.profile.provider_id))
            .map(|p| p.dialect())
            .unwrap_or("mssql");
        SqlDialectKind::from_dialect_id(dialect)
    }

    pub(crate) fn active_result_table(
        &self,
    ) -> Option<(String, Entity<TableState<ResultTableDelegate>>)> {
        let tab_id = self.active_tab_id()?;
        let rt = self.tab_runtime.get(&tab_id)?;
        if rt.results_dismissed || rt.show_messages {
            return None;
        }
        let item = rt.active_result()?;
        if item.result.is_error || item.result.columns.is_empty() {
            return None;
        }
        let table = self.result_tables.get(&item.id)?.clone();
        Some((item.id.clone(), table))
    }

    pub(crate) fn focus_active_result_table(&self, window: &mut Window, cx: &mut gpui_kit::App) {
        if let Some((_, table)) = self.active_result_table() {
            let fh = table.read(cx).focus_handle(cx);
            fh.focus(window, cx);
        }
    }

    pub(crate) fn show_grid_toast(
        &mut self,
        message: impl Into<SharedString>,
        is_error: bool,
        cx: &mut Context<Self>,
    ) {
        let seq = self.grid_status_toast.as_ref().map_or(1, |t| t.seq + 1);
        self.grid_status_toast = Some(GridStatusToast {
            message: message.into(),
            is_error,
            seq,
        });
        cx.notify();

        let duration_ms = if is_error { 4200 } else { 2200 };
        cx.spawn(async move |this, cx| {
            cx.background_executor()
                .timer(Duration::from_millis(duration_ms))
                .await;
            let _ = this.update(cx, |this, cx| {
                if this.grid_status_toast.as_ref().is_some_and(|t| t.seq == seq) {
                    this.grid_status_toast = None;
                    cx.notify();
                }
            });
        })
        .detach();
    }

    pub(crate) fn ensure_table_schema_metadata(
        &self,
        table: &Entity<TableState<ResultTableDelegate>>,
        cx: &mut gpui_kit::App,
    ) {
        let Some(space_id) = self.active_space_id() else {
            return;
        };
        let Some(space_rt) = self.space_runtime.get(&space_id) else {
            return;
        };
        if space_rt.schema.is_empty() {
            return;
        }
        let fallback_stmt = self
            .state
            .active_space()
            .and_then(|s| s.active_tab())
            .map(|t| t.content.clone());
        let schema = Arc::clone(&space_rt.schema);
        table.update(cx, |t, _| {
            t.delegate_mut()
                .ensure_schema_metadata(fallback_stmt.as_deref(), &schema);
        });
    }

    // ---------------------------------------------------------------- table events & selection

    pub(crate) fn handle_result_table_event(
        &mut self,
        result_id: &str,
        table: Entity<TableState<ResultTableDelegate>>,
        event: &TableEvent,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match event {
            TableEvent::SelectCell(row_ix, col_ix) => {
                self.grid_context_menu = None;
                let extend = window.modifiers().shift;
                table.update(cx, |t, cx| {
                    if *col_ix == 0 {
                        t.delegate_mut().select_row(*row_ix, extend);
                    } else {
                        t.delegate_mut().select_cell(*row_ix, col_ix - 1, extend);
                    }
                    cx.notify();
                });
                let fh = table.read(cx).focus_handle(cx);
                fh.focus(window, cx);
                if self.preview_pane.is_some() {
                    self.refresh_grid_preview(false, cx);
                }
                cx.notify();
            }
            TableEvent::SelectColumn(col_ix) => {
                self.grid_context_menu = None;
                table.update(cx, |t, cx| {
                    if *col_ix == 0 {
                        t.delegate_mut().select_all();
                    } else {
                        t.delegate_mut().select_column(col_ix - 1);
                    }
                    cx.notify();
                });
                if self.preview_pane.is_some() {
                    self.refresh_grid_preview(false, cx);
                }
                cx.notify();
            }
            TableEvent::DoubleClickedCell(row_ix, col_ix) => {
                self.grid_context_menu = None;
                if let Some(vis_col) = col_ix.checked_sub(1) {
                    table.update(cx, |t, cx| {
                        t.delegate_mut().select_cell(*row_ix, vis_col, false);
                        cx.notify();
                    });
                    self.begin_cell_edit(result_id.to_string(), *row_ix, vis_col, window, cx);
                }
            }
            TableEvent::RightClickedCell(row_ix, col_ix) => {
                let vis_col_opt = col_ix.checked_sub(1);
                table.update(cx, |t, cx| {
                    t.delegate_mut().handle_right_click(*row_ix, vis_col_opt);
                    cx.notify();
                });
                let fh = table.read(cx).focus_handle(cx);
                fh.focus(window, cx);
                self.grid_context_menu = Some(GridContextMenu {
                    result_id: result_id.to_string(),
                    row_ix: *row_ix,
                    vis_col_opt,
                    position: window.mouse_position(),
                });
                if self.preview_pane.is_some() {
                    self.refresh_grid_preview(false, cx);
                }
                cx.notify();
            }
            TableEvent::RightClickedRow(Some(row_ix)) => {
                table.update(cx, |t, cx| {
                    t.delegate_mut().handle_right_click(*row_ix, None);
                    cx.notify();
                });
                self.grid_context_menu = Some(GridContextMenu {
                    result_id: result_id.to_string(),
                    row_ix: *row_ix,
                    vis_col_opt: None,
                    position: window.mouse_position(),
                });
                cx.notify();
            }
            _ => {}
        }
    }

    pub(crate) fn extend_grid_selection(
        &mut self,
        drow: isize,
        dcol: isize,
        cx: &mut Context<Self>,
    ) {
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        table.update(cx, |t, cx| {
            let rows = t.delegate().row_count();
            let cols = t.delegate().data_col_count();
            if rows == 0 || cols == 0 {
                return;
            }
            let (cur_r, cur_c) = t.delegate().focused_cell().unwrap_or((0, 0));
            let next_r = (cur_r as isize + drow).clamp(0, rows as isize - 1) as usize;
            let next_c = (cur_c as isize + dcol).clamp(0, cols as isize - 1) as usize;
            t.delegate_mut().select_cell(next_r, next_c, true);
            t.set_selected_cell(next_r, next_c + 1, cx);
            cx.notify();
        });
        if self.preview_pane.is_some() {
            self.refresh_grid_preview(false, cx);
        }
    }

    pub(crate) fn handle_grid_escape(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.save_review_modal.is_some() {
            if !self.save_review_modal.as_ref().is_some_and(|m| m.saving) {
                self.save_review_modal = None;
                cx.notify();
            }
        } else if self.grid_context_menu.is_some() {
            self.grid_context_menu = None;
            cx.notify();
        } else if self.cell_editor.is_some() {
            self.cancel_cell_edit(window, cx);
        } else if self.preview_pane.is_some() {
            self.close_grid_preview(cx);
        } else if let Some((_, table)) = self.active_result_table() {
            table.update(cx, |t, cx| {
                t.clear_selection(cx);
                cx.notify();
            });
        }
    }

    // ---------------------------------------------------------------- copy actions

    pub(crate) fn copy_grid_selection(&mut self, with_headers: bool, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        let copied = table.read(cx).delegate().copy_selection(with_headers);
        let Some((text, rows, cols)) = copied else {
            self.show_grid_toast("Select a cell first.", false, cx);
            return;
        };
        cx.write_to_clipboard(ClipboardItem::new_string(text));
        let label = if rows == 1 && cols == 1 && !with_headers {
            "Copied cell".to_string()
        } else if with_headers {
            format!("Copied {rows}×{cols} with headers")
        } else {
            format!("Copied {rows}×{cols} cells")
        };
        self.show_grid_toast(label, false, cx);
    }

    pub(crate) fn copy_grid_as_in(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        let dialect = self.active_sql_dialect();
        let copied = table.read(cx).delegate().copy_selection_as_in(dialect);
        let Some((text, count)) = copied else {
            self.show_grid_toast("Select one or more cells first.", false, cx);
            return;
        };
        cx.write_to_clipboard(ClipboardItem::new_string(text));
        self.show_grid_toast(
            format!(
                "Copied IN ({count} {})",
                if count == 1 { "value" } else { "values" }
            ),
            false,
            cx,
        );
    }

    pub(crate) fn copy_grid_as_values(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        let dialect = self.active_sql_dialect();
        let copied = table.read(cx).delegate().copy_selection_as_values(dialect);
        let Some((text, rows)) = copied else {
            self.show_grid_toast("Select one or more rows first.", false, cx);
            return;
        };
        cx.write_to_clipboard(ClipboardItem::new_string(text));
        self.show_grid_toast(
            format!(
                "Copied VALUES ({rows} {})",
                if rows == 1 { "row" } else { "rows" }
            ),
            false,
            cx,
        );
    }

    pub(crate) fn select_all_grid(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        table.update(cx, |t, cx| {
            t.delegate_mut().select_all();
            cx.notify();
        });
    }

    // ---------------------------------------------------------------- edit & delete actions

    pub(crate) fn begin_focused_cell_edit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((result_id, table)) = self.active_result_table() else {
            return;
        };
        let focused = table.read(cx).delegate().focused_cell();
        let Some((row_ix, vis_col)) = focused else {
            self.show_grid_toast("Select a cell to edit.", false, cx);
            return;
        };
        self.begin_cell_edit(result_id, row_ix, vis_col, window, cx);
    }

    pub(crate) fn begin_cell_edit(
        &mut self,
        result_id: String,
        row_ix: usize,
        vis_col: usize,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.grid_context_menu = None;
        let Some(table) = self.result_tables.get(&result_id).cloned() else {
            return;
        };
        self.ensure_table_schema_metadata(&table, cx);

        let check = {
            let state = table.read(cx);
            let d = state.delegate();
            let Some(src_col) = d.src_col_for_vis(vis_col) else {
                return;
            };
            if row_ix >= d.row_count() {
                return;
            }
            let res = d.edit_resolution();
            if !res.has_editable_targets() {
                Err(res.refuse_reason(src_col))
            } else if d.is_row_deleted(row_ix) {
                Err("Row is marked for deletion — press Del to unmark it first.".to_string())
            } else if !res.can_edit(src_col) {
                Err(res.refuse_reason(src_col))
            } else {
                let current = d.display_value(row_ix, src_col).map(str::to_string);
                if let Some(ref text) = current {
                    if text.chars().count() > MAX_INLINE_EDIT_CHARS {
                        Err(format!(
                            "Value exceeds {MAX_INLINE_EDIT_CHARS} characters; inline editing is disabled."
                        ))
                    } else {
                        Ok((
                            src_col,
                            d.column_name_for_src(src_col).unwrap_or("").to_string(),
                            d.column_type_for_src(src_col).map(str::to_string),
                            current,
                        ))
                    }
                } else {
                    Ok((
                        src_col,
                        d.column_name_for_src(src_col).unwrap_or("").to_string(),
                        d.column_type_for_src(src_col).map(str::to_string),
                        None,
                    ))
                }
            }
        };

        let (src_col, col_name, col_type, current_val) = match check {
            Ok(v) => v,
            Err(reason) => {
                self.show_grid_toast(reason, false, cx);
                return;
            }
        };

        let is_null = current_val.is_none();
        let initial_text = current_val.unwrap_or_default();
        let multiline = initial_text.len() > MULTILINE_THRESHOLD_CHARS || initial_text.contains('\n');

        let (input, sub) = if multiline {
            let init = initial_text.clone();
            let ta = cx.new(|cx| TextareaState::new(window, cx).default_value(init));
            let sub = cx.subscribe_in(&ta, window, |this, _, event: &InputEvent, window, cx| {
                match event {
                    InputEvent::Change => {
                        if let Some(ed) = this.cell_editor.as_mut() {
                            if ed.is_null {
                                ed.is_null = false;
                                cx.notify();
                            }
                        }
                    }
                    InputEvent::PressEnter { secondary: true, .. } => {
                        this.apply_cell_edit(None, window, cx);
                    }
                    _ => {}
                }
            });
            ta.update(cx, |s, cx| s.focus(window, cx));
            (CellEditorInput::Multi(ta), sub)
        } else {
            let init = initial_text.clone();
            let inp = cx.new(|cx| {
                InputState::new(window, cx)
                    .placeholder("NULL")
                    .default_value(init)
            });
            let sub = cx.subscribe_in(&inp, window, |this, _, event: &InputEvent, window, cx| {
                match event {
                    InputEvent::Change => {
                        if let Some(ed) = this.cell_editor.as_mut() {
                            if ed.is_null {
                                ed.is_null = false;
                                cx.notify();
                            }
                        }
                    }
                    InputEvent::PressEnter { .. } => {
                        this.apply_cell_edit(None, window, cx);
                    }
                    _ => {}
                }
            });
            inp.update(cx, |s, cx| s.focus(window, cx));
            (CellEditorInput::Single(inp), sub)
        };

        self.cell_editor = Some(ActiveCellEditor {
            result_id,
            row_ix,
            vis_col,
            src_col,
            col_name: SharedString::from(col_name),
            col_type: col_type.map(SharedString::from),
            input,
            is_null,
            multiline,
            _input_sub: sub,
        });
        cx.notify();
    }

    pub(crate) fn apply_cell_edit(
        &mut self,
        advance: Option<isize>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(editor) = self.cell_editor.take() else {
            return;
        };
        let new_val = if editor.is_null {
            None
        } else {
            Some(editor.current_text(cx))
        };

        let mut next_target: Option<(usize, usize)> = None;
        if let Some(table) = self.result_tables.get(&editor.result_id).cloned() {
            table.update(cx, |t, cx| {
                t.delegate_mut()
                    .stage_cell_edit(editor.row_ix, editor.src_col, new_val);
                if let Some(step) = advance {
                    let rows = t.delegate().row_count();
                    let cols = t.delegate().data_col_count();
                    if rows > 0 && cols > 0 {
                        let total = rows * cols;
                        let start = editor.row_ix * cols + editor.vis_col;
                        for offset in 1..=total {
                            let idx = if step > 0 {
                                (start + offset) % total
                            } else {
                                (start + total - (offset % total)) % total
                            };
                            let nr = idx / cols;
                            let nc = idx % cols;
                            if t.delegate().is_row_deleted(nr) {
                                continue;
                            }
                            if let Some(sc) = t.delegate().src_col_for_vis(nc) {
                                if t.delegate().edit_resolution().can_edit(sc) {
                                    t.delegate_mut().select_cell(nr, nc, false);
                                    t.set_selected_cell(nr, nc + 1, cx);
                                    next_target = Some((nr, nc));
                                    break;
                                }
                            }
                        }
                    }
                }
                cx.notify();
            });
        }

        if self.preview_pane.is_some() {
            self.refresh_grid_preview(true, cx);
        }

        if let Some((nr, nc)) = next_target {
            self.begin_cell_edit(editor.result_id, nr, nc, window, cx);
        } else {
            self.focus_active_result_table(window, cx);
            cx.notify();
        }
    }

    pub(crate) fn cancel_cell_edit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.cell_editor.take().is_some() {
            self.focus_active_result_table(window, cx);
            cx.notify();
        }
    }

    pub(crate) fn toggle_cell_editor_null(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(ed) = self.cell_editor.as_mut() {
            ed.is_null = !ed.is_null;
            if !ed.is_null {
                ed.focus(window, cx);
            }
            cx.notify();
        }
    }

    pub(crate) fn toggle_delete_selected_rows(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((result_id, table)) = self.active_result_table() else {
            return;
        };
        self.ensure_table_schema_metadata(&table, cx);

        let err_msg = {
            let state = table.read(cx);
            let d = state.delegate();
            let res = d.edit_resolution();
            if !res.has_editable_targets() {
                Some(
                    res.global_reason
                        .clone()
                        .unwrap_or_else(|| "This result set is read-only.".to_string()),
                )
            } else if d.selected_rows().is_empty() {
                Some("Select one or more rows first.".to_string())
            } else {
                None
            }
        };

        if let Some(msg) = err_msg {
            self.show_grid_toast(msg, false, cx);
            return;
        }

        let rows = table.read(cx).delegate().selected_rows();
        if self
            .cell_editor
            .as_ref()
            .is_some_and(|e| e.result_id == result_id && rows.contains(&e.row_ix))
        {
            self.cell_editor = None;
        }

        table.update(cx, |t, cx| {
            t.delegate_mut().toggle_delete_rows(rows);
            cx.notify();
        });
        cx.notify();
    }

    pub(crate) fn revert_focused_cell(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        let reverted = table.update(cx, |t, cx| {
            let Some((r, vc)) = t.delegate().focused_cell() else {
                return false;
            };
            let Some(sc) = t.delegate().src_col_for_vis(vc) else {
                return false;
            };
            let ok = t.delegate_mut().revert_cell(r, sc);
            if ok {
                cx.notify();
            }
            ok
        });
        if reverted {
            if self.preview_pane.is_some() {
                self.refresh_grid_preview(true, cx);
            }
            cx.notify();
        }
    }

    pub(crate) fn undo_grid_edit(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        let undone = table.update(cx, |t, cx| {
            let ok = t.delegate_mut().undo_last_edit();
            if ok {
                cx.notify();
            }
            ok
        });
        if undone {
            if self.preview_pane.is_some() {
                self.refresh_grid_preview(true, cx);
            }
            cx.notify();
        }
    }

    pub(crate) fn discard_grid_edits(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        self.cell_editor = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        table.update(cx, |t, cx| {
            t.delegate_mut().discard_all_edits();
            cx.notify();
        });
        if self.preview_pane.is_some() {
            self.refresh_grid_preview(true, cx);
        }
        self.show_grid_toast("Discarded unsaved changes", false, cx);
    }

    pub(crate) fn open_save_review_modal(&mut self, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        self.cell_editor = None;
        let Some(tab_id) = self.active_tab_id() else {
            return;
        };
        let Some(space_id) = self.active_space_id() else {
            return;
        };
        let Some((result_id, table)) = self.active_result_table() else {
            return;
        };
        self.ensure_table_schema_metadata(&table, cx);
        let dialect = self.active_sql_dialect();
        let tables = self
            .space_runtime
            .get(&space_id)
            .map(|rt| Arc::clone(&rt.schema))
            .unwrap_or_default();

        let plan_result = table.read(cx).delegate().build_save_plan(dialect, &tables);
        match plan_result {
            Ok(plan) => {
                self.save_review_modal = Some(SaveReviewModal {
                    tab_id,
                    result_id,
                    plan,
                    saving: false,
                    error: None,
                });
                cx.notify();
            }
            Err(err) => {
                self.show_grid_toast(err, true, cx);
            }
        }
    }

    pub(crate) fn confirm_save_edits(&mut self, cx: &mut Context<Self>) {
        let Some((tab_id, result_id, sql, total_stmts)) = self.save_review_modal.as_ref().map(|m| {
            (
                m.tab_id.clone(),
                m.result_id.clone(),
                m.plan.batch_sql.clone(),
                m.plan.statements.len(),
            )
        }) else {
            return;
        };

        let Some(profile) = self.state.active_space().map(|s| s.profile.clone()) else {
            return;
        };
        let database = self.active_tab_database();

        if let Some(modal) = self.save_review_modal.as_mut() {
            if modal.saving {
                return;
            }
            modal.saving = true;
            modal.error = None;
        }
        cx.notify();

        let rx = self
            .sessions
            .execute_tab_dml(tab_id.clone(), profile, database, sql);

        cx.spawn(async move |this, cx| {
            let outcome = rx.await;
            let _ = this.update(cx, |this, cx| {
                match outcome {
                    Ok(Ok(affected)) if affected > 0 => {
                        let patched = this.result_tables.get(&result_id).cloned().and_then(|tbl| {
                            tbl.update(cx, |t, cx| {
                                let updated = t.delegate_mut().apply_committed_changes();
                                t.refresh(cx);
                                cx.notify();
                                updated
                            })
                        });
                        if let Some(updated_arc) = patched {
                            if let Some(rt) = this.tab_runtime.get_mut(&tab_id) {
                                if let Some(item) =
                                    rt.results.iter_mut().find(|r| r.id == result_id)
                                {
                                    item.result = updated_arc;
                                }
                            }
                        }
                        this.save_review_modal = None;
                        if this.preview_pane.is_some() {
                            this.refresh_grid_preview(true, cx);
                        }
                        this.show_grid_toast(
                            format!(
                                "Saved {total_stmts} {} ({affected} rows affected)",
                                if total_stmts == 1 {
                                    "statement"
                                } else {
                                    "statements"
                                }
                            ),
                            false,
                            cx,
                        );
                    }
                    Ok(Ok(_zero)) => {
                        if let Some(modal) = this.save_review_modal.as_mut() {
                            modal.saving = false;
                            modal.error = Some(
                                "0 rows were affected. The row may have been changed or deleted externally."
                                    .to_string(),
                            );
                        }
                        cx.notify();
                    }
                    Ok(Err(err)) => {
                        if let Some(modal) = this.save_review_modal.as_mut() {
                            modal.saving = false;
                            modal.error = Some(err.to_string());
                        }
                        cx.notify();
                    }
                    Err(_) => {
                        if let Some(modal) = this.save_review_modal.as_mut() {
                            modal.saving = false;
                            modal.error = Some("Save operation was cancelled.".to_string());
                        }
                        cx.notify();
                    }
                }
            });
        })
        .detach();
    }

    // ---------------------------------------------------------------- preview pane & FK lookup

    pub(crate) fn toggle_grid_preview(&mut self, requested: PreviewMode, cx: &mut Context<Self>) {
        self.grid_context_menu = None;
        let Some((_, table)) = self.active_result_table() else {
            return;
        };
        self.ensure_table_schema_metadata(&table, cx);

        // Ensure a cell is focused if the grid has data.
        let focused = table.update(cx, |t, cx| {
            if t.delegate().focused_cell().is_none()
                && t.delegate().row_count() > 0
                && t.delegate().data_col_count() > 0
            {
                t.delegate_mut().select_cell(0, 0, false);
                cx.notify();
            }
            t.delegate().focused_cell()
        });
        let Some((_, vis_col)) = focused else {
            return;
        };

        let has_fk = {
            let state = table.read(cx);
            let d = state.delegate();
            d.src_col_for_vis(vis_col)
                .and_then(|sc| d.column_reference(sc))
                .is_some()
        };

        let effective_mode = if requested == PreviewMode::Reference && !has_fk {
            if self
                .preview_pane
                .as_ref()
                .is_some_and(|p| p.mode == PreviewMode::Value)
            {
                self.show_grid_toast("No foreign-key reference on this column.", false, cx);
                return;
            }
            PreviewMode::Value
        } else {
            requested
        };

        if let Some(existing) = self.preview_pane.as_mut() {
            if existing.mode == effective_mode {
                self.close_grid_preview(cx);
                return;
            }
            existing.mode = effective_mode;
            self.refresh_grid_preview(true, cx);
        } else {
            self.preview_pane = Some(GridPreviewState::new(effective_mode));
            self.refresh_grid_preview(true, cx);
        }
    }

    pub(crate) fn set_preview_mode(&mut self, mode: PreviewMode, cx: &mut Context<Self>) {
        if let Some(pane) = self.preview_pane.as_mut() {
            if pane.mode != mode {
                pane.mode = mode;
                self.refresh_grid_preview(true, cx);
            }
        }
    }

    pub(crate) fn set_preview_format(&mut self, format: CellFormat, cx: &mut Context<Self>) {
        if let Some(pane) = self.preview_pane.as_mut() {
            if pane.format != format {
                pane.format = format;
                cx.notify();
            }
        }
    }

    pub(crate) fn close_grid_preview(&mut self, cx: &mut Context<Self>) {
        if self.preview_pane.take().is_some() {
            if let Some(tab_id) = self.active_tab_id() {
                self.sessions.cancel_reference_lookup(tab_id);
            }
            cx.notify();
        }
    }

    pub(crate) fn copy_preview_value(&mut self, cx: &mut Context<Self>) {
        let cached_text = self
            .preview_pane
            .as_ref()
            .and_then(|p| p.value_cache.as_ref())
            .map(|c| {
                c.full_text
                    .as_ref()
                    .map(|s| s.to_string())
                    .unwrap_or_else(|| "NULL".to_string())
            });

        let text = if let Some(t) = cached_text {
            t
        } else {
            let Some((_, table)) = self.active_result_table() else {
                return;
            };
            let fmt = self
                .preview_pane
                .as_ref()
                .map(|p| p.format)
                .unwrap_or(CellFormat::Auto);
            let state = table.read(cx);
            let d = state.delegate();
            let Some((r, vc)) = d.focused_cell() else {
                return;
            };
            let Some(sc) = d.src_col_for_vis(vc) else {
                return;
            };
            match d.display_value(r, sc) {
                None => "NULL".to_string(),
                Some(s) => format_cell_content(s, fmt).full_text,
            }
        };
        cx.write_to_clipboard(ClipboardItem::new_string(text));
        if let Some(pane) = self.preview_pane.as_mut() {
            pane.copied_toast = true;
        }
        cx.notify();
        cx.spawn(async move |this, cx| {
            cx.background_executor()
                .timer(Duration::from_millis(1400))
                .await;
            let _ = this.update(cx, |this, cx| {
                if let Some(pane) = this.preview_pane.as_mut() {
                    pane.copied_toast = false;
                    cx.notify();
                }
            });
        })
        .detach();
    }

    /// Synchronize the cached formatted value + per-line syntax highlights for the active preview pane.
    /// Fast path on every render frame (scroll, resize drag, hover, cursor blink) is an O(1) key check
    /// with zero string clones, zero JSON/XML parsing, and zero tokenization.
    pub(crate) fn sync_preview_value_cache(
        &mut self,
        result_id: &str,
        table_state: &Entity<TableState<ResultTableDelegate>>,
        palette: StudioPalette,
        cx: &gpui_kit::App,
    ) {
        let Some(pane) = self.preview_pane.as_mut() else {
            return;
        };
        let is_dark = cx.theme().mode.is_dark();
        let requested_format = pane.format;

        // O(1) key check before cloning any cell data.
        let fast_probe = {
            let state = table_state.read(cx);
            let d = state.delegate();
            d.focused_cell().and_then(|(r, vc)| {
                let sc = d.src_col_for_vis(vc)?;
                Some((
                    r,
                    sc,
                    d.data_version(),
                    d.column_reference(sc).is_some(),
                ))
            })
        };

        let Some((row_ix, src_col, data_version, has_fk)) = fast_probe else {
            pane.value_cache = None;
            return;
        };

        if let Some(existing) = pane.value_cache.as_ref() {
            if existing.key.result_id == result_id
                && existing.key.row_ix == row_ix
                && existing.key.src_col == src_col
                && existing.key.data_version == data_version
                && existing.key.has_fk == has_fk
                && existing.key.format == requested_format
                && existing.key.is_dark == is_dark
            {
                return;
            }
        }

        let reset_scroll = pane.value_cache.as_ref().is_none_or(|prev| {
            prev.key.result_id != result_id
                || prev.key.row_ix != row_ix
                || prev.key.src_col != src_col
                || prev.key.format != requested_format
        });

        let styles = PreviewSyntaxStyles::from_theme(palette, cx);
        let cache = {
            let state = table_state.read(cx);
            let d = state.delegate();
            let col_name = SharedString::from(d.column_name_for_src(src_col).unwrap_or("").to_string());
            let col_type = d
                .column_type_for_src(src_col)
                .map(|s| SharedString::from(s.to_string()));
            let is_edited = d.is_cell_edited(row_ix, src_col);
            let fk = d.column_reference(src_col).cloned();
            let raw_val = d.display_value(row_ix, src_col);

            let (full_text, body) = match raw_val {
                None => (None, PreviewBodyData::Null),
                Some("") => (Some(SharedString::new_static("")), PreviewBodyData::Empty),
                Some(raw) => {
                    let fc = format_cell_content(raw, requested_format);
                    let meta_label = if fc.is_truncated
                        || fc.detected != CellFormat::Plain
                        || fc.error_note.is_some()
                    {
                        let mut meta = Vec::with_capacity(4);
                        if fc.detected == CellFormat::Json {
                            meta.push("JSON".to_string());
                        } else if fc.detected == CellFormat::Xml {
                            meta.push("XML".to_string());
                        }
                        meta.push(format!("{} chars", fc.total_chars));
                        if fc.is_truncated {
                            meta.push("display truncated".to_string());
                        }
                        if let Some(note) = fc.error_note.as_deref() {
                            meta.push(note.to_string());
                        }
                        Some(SharedString::from(meta.join(" · ")))
                    } else {
                        None
                    };

                    let full = SharedString::from(fc.full_text);
                    let use_wrapped_plain = fc.detected == CellFormat::Plain
                        && fc.display_text.len() <= 4_000
                        && fc.display_text.bytes().filter(|&b| b == b'\n').count() <= 60;

                    let body = if use_wrapped_plain {
                        PreviewBodyData::WrappedPlain {
                            text: SharedString::from(fc.display_text),
                            meta_label,
                        }
                    } else {
                        let tokens = if matches!(fc.detected, CellFormat::Json | CellFormat::Xml) {
                            tokenize_cell_syntax_capped(&fc.display_text, fc.detected)
                        } else {
                            Vec::new()
                        };
                        let (lines, longest_line_ix) =
                            build_preview_lines(&fc.display_text, &tokens, &styles);
                        PreviewBodyData::VirtualizedLines {
                            lines,
                            longest_line_ix,
                            meta_label,
                        }
                    };
                    (Some(full), body)
                }
            };

            PreviewValueCache {
                key: PreviewValueCacheKey {
                    result_id: result_id.to_string(),
                    row_ix,
                    src_col,
                    data_version,
                    has_fk,
                    format: requested_format,
                    is_dark,
                },
                row_ix,
                col_name,
                col_type,
                is_edited,
                fk,
                full_text,
                body,
            }
        };

        pane.value_cache = Some(cache);
        if reset_scroll {
            pane.value_scroll_handle.scroll_to_item(0, ScrollStrategy::Top);
        }
    }

    pub(crate) fn refresh_grid_preview(&mut self, force_ref: bool, cx: &mut Context<Self>) {
        if self.preview_pane.is_none() {
            return;
        }
        let Some((result_id, table)) = self.active_result_table() else {
            return;
        };
        self.ensure_table_schema_metadata(&table, cx);

        let focus_info = {
            let state = table.read(cx);
            let d = state.delegate();
            d.focused_cell().and_then(|(r, vc)| {
                let sc = d.src_col_for_vis(vc)?;
                let fk = d.column_reference(sc).cloned();
                let filters_opt = fk.as_ref().and_then(|reference| {
                    let res = d.source()?;
                    Some(build_reference_filters(reference, res, r, d.staged_edits()))
                });
                Some((r, sc, fk, filters_opt))
            })
        };

        let Some((row_ix, src_col, fk_opt, filters_opt)) = focus_info else {
            return;
        };

        let active_tab_id_opt = self.active_tab_id();
        let (changed, mode, cancel_ref) = {
            let pane = self.preview_pane.as_mut().unwrap();
            let same = pane.tracked_result_id.as_deref() == Some(result_id.as_str())
                && pane.tracked_row == Some(row_ix)
                && pane.tracked_src_col == Some(src_col);
            pane.tracked_result_id = Some(result_id.clone());
            pane.tracked_row = Some(row_ix);
            pane.tracked_src_col = Some(src_col);

            // Matching WinUI: moving to a non-FK column while in Reference mode falls back to Value view.
            let mut cancel = false;
            if pane.mode == PreviewMode::Reference && fk_opt.is_none() {
                pane.mode = PreviewMode::Value;
                pane.ref_state = ReferencePreviewState::Idle;
                cancel = true;
            }
            (!same, pane.mode, cancel)
        };
        if cancel_ref {
            if let Some(tab_id) = active_tab_id_opt {
                self.sessions.cancel_reference_lookup(tab_id);
            }
        }

        if mode != PreviewMode::Reference {
            cx.notify();
            return;
        }
        if !changed && !force_ref {
            cx.notify();
            return;
        }

        let Some(reference) = fk_opt else {
            cx.notify();
            return;
        };
        let Some((filters, filter_warning)) = filters_opt else {
            cx.notify();
            return;
        };
        let Some(tab_id) = self.active_tab_id() else {
            return;
        };
        let Some(profile) = self.state.active_space().map(|s| s.profile.clone()) else {
            return;
        };
        let database = self.active_tab_database();
        let dialect = self.active_sql_dialect();

        let has_null = filters.is_empty() || filters.iter().any(|f| f.value.is_none());
        if has_null {
            self.sessions.cancel_reference_lookup(tab_id);
            if let Some(pane) = self.preview_pane.as_mut() {
                pane.request_seq += 1;
                pane.ref_state = ReferencePreviewState::NullValue;
            }
            cx.notify();
            return;
        }

        let seq = {
            let pane = self.preview_pane.as_mut().unwrap();
            pane.request_seq += 1;
            pane.ref_state = ReferencePreviewState::Loading;
            pane.request_seq
        };
        cx.notify();

        let rx = self.sessions.lookup_reference_rows(
            tab_id,
            profile,
            database,
            dialect,
            reference,
            filters,
        );

        cx.spawn(async move |this, cx| {
            let result = rx.await;
            let _ = this.update(cx, |this, cx| {
                let Some(pane) = this.preview_pane.as_mut() else {
                    return;
                };
                if pane.request_seq != seq || pane.mode != PreviewMode::Reference {
                    return;
                }
                match result {
                    Ok(Ok(outcome)) => {
                        let warning = if let Some(w) = filter_warning {
                            Some(w)
                        } else if outcome.result.rows.is_empty() {
                            Some("No matching referenced row found.".to_string())
                        } else if outcome.matched_rows.is_empty() && outcome.is_list {
                            Some("Referenced value not found in the first 200 rows.".to_string())
                        } else if outcome.is_truncated {
                            Some(format!(
                                "Showing first {} rows.",
                                outcome.result.rows.len()
                            ))
                        } else {
                            None
                        };
                        let matched_set: HashSet<usize> =
                            outcome.matched_rows.iter().copied().collect();
                        let scroll_target = outcome.matched_rows.first().copied().unwrap_or(0);
                        pane.ref_state = ReferencePreviewState::Loaded {
                            outcome,
                            matched_set,
                            warning,
                        };
                        pane.ref_scroll_handle
                            .scroll_to_item(scroll_target, ScrollStrategy::Center);
                    }
                    Ok(Err(err)) => {
                        pane.ref_state = ReferencePreviewState::Error(err.to_string());
                    }
                    Err(_) => {
                        // Aborted by a newer selection change; ignore.
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }

    // ---------------------------------------------------------------- UI: Cell Editor Bar

    pub(crate) fn render_cell_editor_bar(
        &self,
        editor: &ActiveCellEditor,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let is_null = editor.is_null;
        let multiline = editor.multiline;
        let col_label = editor.col_name.clone();
        let col_type = editor.col_type.clone();
        let row_num = editor.row_ix + 1;

        v_flex()
            .id("grid-cell-editor-bar")
            .w_full()
            .px_3()
            .py_2()
            .gap_1p5()
            .bg(palette.subtle_fill)
            .border_b_1()
            .border_color(palette.hairline_subtle)
            .on_key_down(cx.listener(|this, event: &KeyDownEvent, window, cx| {
                if event.keystroke.key == "escape" {
                    cx.stop_propagation();
                    this.cancel_cell_edit(window, cx);
                } else if event.keystroke.key == "tab" {
                    cx.stop_propagation();
                    let step = if event.keystroke.modifiers.shift {
                        -1
                    } else {
                        1
                    };
                    this.apply_cell_edit(Some(step), window, cx);
                }
            }))
            .child(
                h_flex()
                    .w_full()
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1p5()
                            .child(
                                Icon::new(IconName::Pencil)
                                    .size(px(12.0))
                                    .text_color(space_color),
                            )
                            .child(
                                div()
                                    .text_xs()
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                    .text_color(palette.text_primary)
                                    .child(format!("Edit {col_label}")),
                            )
                            .when_some(col_type, |el, ty| {
                                el.child(
                                    div()
                                        .px_1p5()
                                        .py(px(1.0))
                                        .rounded(px(4.0))
                                        .bg(palette.card_bg)
                                        .text_size(px(10.0))
                                        .font_family(cx.theme().mono_font_family.clone())
                                        .text_color(palette.text_muted)
                                        .child(ty),
                                )
                            })
                            .child(
                                div()
                                    .text_size(px(10.5))
                                    .text_color(palette.text_muted)
                                    .child(format!("· Row {row_num}")),
                            ),
                    )
                    .child(
                        div()
                            .text_size(px(10.5))
                            .text_color(palette.text_muted)
                            .child(if multiline {
                                "Ctrl+Enter apply · Tab next · Esc cancel"
                            } else {
                                "Enter apply · Tab next · Esc cancel"
                            }),
                    ),
            )
            .child(
                h_flex()
                    .w_full()
                    .items_start()
                    .gap_2()
                    .child(div().flex_1().min_w_0().child(match &editor.input {
                        CellEditorInput::Single(inp) => Input::new(inp)
                            .small()
                            .disabled(is_null)
                            .into_any_element(),
                        CellEditorInput::Multi(ta) => Textarea::new(ta)
                            .small()
                            .h(px(76.0))
                            .disabled(is_null)
                            .into_any_element(),
                    }))
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1()
                            .flex_shrink_0()
                            .child(
                                div()
                                    .id("cell-editor-null-toggle")
                                    .h(px(28.0))
                                    .px_2()
                                    .rounded(px(5.0))
                                    .flex()
                                    .items_center()
                                    .cursor_pointer()
                                    .border_1()
                                    .border_color(if is_null {
                                        space_color
                                    } else {
                                        palette.card_border
                                    })
                                    .bg(if is_null {
                                        space_color.opacity(0.16)
                                    } else {
                                        palette.card_bg
                                    })
                                    .text_xs()
                                    .font_family(cx.theme().mono_font_family.clone())
                                    .italic()
                                    .text_color(if is_null {
                                        space_color
                                    } else {
                                        palette.text_secondary
                                    })
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.toggle_cell_editor_null(window, cx);
                                    }))
                                    .child("NULL"),
                            )
                            .child(
                                Button::new("cell-editor-apply")
                                    .primary()
                                    .xsmall()
                                    .icon(IconName::Check)
                                    .label("Apply")
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.apply_cell_edit(None, window, cx);
                                    })),
                            )
                            .child(
                                Button::new("cell-editor-cancel")
                                    .ghost()
                                    .xsmall()
                                    .label("Cancel")
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.cancel_cell_edit(window, cx);
                                    })),
                            ),
                    ),
            )
            .into_any_element()
    }

    // ---------------------------------------------------------------- UI: Bottom Change Bar

    pub(crate) fn render_grid_change_bar(
        &self,
        edited_cells: usize,
        deleted_rows: usize,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let mut parts = Vec::new();
        if edited_cells > 0 {
            parts.push(format!(
                "{edited_cells} cell {}",
                if edited_cells == 1 {
                    "change"
                } else {
                    "changes"
                }
            ));
        }
        if deleted_rows > 0 {
            parts.push(format!(
                "{deleted_rows} {} to delete",
                if deleted_rows == 1 { "row" } else { "rows" }
            ));
        }
        let summary = parts.join(" · ");
        let has_deletes = deleted_rows > 0;

        h_flex()
            .id("grid-change-bar")
            .w_full()
            .h(px(40.0))
            .px_3()
            .bg(palette.subtle_fill)
            .border_t_1()
            .border_color(palette.hairline_subtle)
            .items_center()
            .justify_between()
            .gap_2()
            .child(
                h_flex()
                    .items_center()
                    .gap_2()
                    .child(
                        div()
                            .size(px(7.0))
                            .rounded_full()
                            .bg(if has_deletes {
                                palette.danger
                            } else {
                                space_color
                            }),
                    )
                    .child(
                        div()
                            .text_xs()
                            .font_weight(gpui_kit::FontWeight::MEDIUM)
                            .text_color(palette.text_primary)
                            .child(summary),
                    )
                    .child(
                        div()
                            .text_size(px(11.0))
                            .text_color(palette.text_muted)
                            .child("Staged locally — review SQL before committing"),
                    ),
            )
            .child(
                h_flex()
                    .items_center()
                    .gap_1p5()
                    .child(
                        Button::new("change-bar-undo")
                            .ghost()
                            .xsmall()
                            .icon(IconName::Undo2)
                            .label("Undo")
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.undo_grid_edit(cx);
                            })),
                    )
                    .child(
                        Button::new("change-bar-discard")
                            .ghost()
                            .xsmall()
                            .icon(IconName::RotateCcw)
                            .label("Discard")
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.discard_grid_edits(cx);
                            })),
                    )
                    .child({
                        let btn = Button::new("change-bar-save")
                            .xsmall()
                            .icon(IconName::Check)
                            .label("Save changes…")
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.open_save_review_modal(cx);
                            }));
                        if has_deletes {
                            btn.danger()
                        } else {
                            btn.primary()
                        }
                    }),
            )
            .into_any_element()
    }

    // ---------------------------------------------------------------- UI: Preview Pane

    pub(crate) fn render_grid_preview_pane(
        &self,
        pane: &GridPreviewState,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let Some(cache) = pane.value_cache.as_ref() else {
            return v_flex()
                .size_full()
                .border_l_1()
                .border_color(palette.hairline_subtle)
                .bg(palette.card_bg)
                .items_center()
                .justify_center()
                .p_4()
                .child(
                    div()
                        .text_xs()
                        .text_color(palette.text_muted)
                        .child("Select a cell to preview"),
                )
                .into_any_element();
        };

        let row_ix = cache.row_ix;
        let col_name = cache.col_name.clone();
        let col_type = cache.col_type.clone();
        let is_edited = cache.is_edited;
        let has_fk = cache.fk.is_some();
        let is_ref_mode = pane.mode == PreviewMode::Reference && has_fk;

        v_flex()
            .id("grid-preview-pane")
            .size_full()
            .border_l_1()
            .border_color(palette.hairline_subtle)
            .bg(palette.card_bg)
            .overflow_hidden()
            // Header
            .child(
                v_flex()
                    .px_3()
                    .py_2()
                    .gap_1p5()
                    .border_b_1()
                    .border_color(palette.hairline_subtle)
                    .child(
                        h_flex()
                            .w_full()
                            .items_center()
                            .justify_between()
                            .gap_1p5()
                            .child(
                                h_flex()
                                    .flex_1()
                                    .min_w_0()
                                    .items_center()
                                    .gap_1p5()
                                    .child(
                                        Icon::new(if is_ref_mode {
                                            IconName::Key
                                        } else {
                                            IconName::Eye
                                        })
                                        .size(px(13.0))
                                        .text_color(space_color),
                                    )
                                    .child(
                                        div()
                                            .text_xs()
                                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                            .text_color(palette.text_primary)
                                            .truncate()
                                            .child(col_name),
                                    )
                                    .when_some(col_type, |el, ty| {
                                        el.child(
                                            div()
                                                .flex_shrink_0()
                                                .px_1p5()
                                                .py(px(1.0))
                                                .rounded(px(4.0))
                                                .bg(palette.subtle_fill)
                                                .text_size(px(10.0))
                                                .font_family(cx.theme().mono_font_family.clone())
                                                .text_color(palette.text_muted)
                                                .child(ty),
                                        )
                                    }),
                            )
                            .child(
                                Button::new("preview-pane-close")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::X)
                                    .tooltip("Close preview (Esc)")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.close_grid_preview(cx);
                                    })),
                            ),
                    )
                    .child(
                        h_flex()
                            .w_full()
                            .items_center()
                            .justify_between()
                            .gap_2()
                            .child(
                                h_flex()
                                    .items_center()
                                    .gap_1p5()
                                    .child(
                                        div()
                                            .text_size(px(10.5))
                                            .text_color(palette.text_muted)
                                            .child(format!("Row {}", row_ix + 1)),
                                    )
                                    .when(is_edited, |el| {
                                        el.child(
                                            div()
                                                .px_1p5()
                                                .py(px(1.0))
                                                .rounded(px(4.0))
                                                .bg(space_color.opacity(0.15))
                                                .text_size(px(10.0))
                                                .text_color(space_color)
                                                .child("edited, not saved"),
                                        )
                                    }),
                            )
                            .when(has_fk, |el| {
                                el.child(
                                    h_flex()
                                        .p(px(2.0))
                                        .rounded(px(6.0))
                                        .bg(palette.subtle_fill)
                                        .gap(px(2.0))
                                        .child(
                                            div()
                                                .id("preview-mode-value")
                                                .px_2()
                                                .py(px(2.0))
                                                .rounded(px(4.0))
                                                .cursor_pointer()
                                                .text_size(px(10.5))
                                                .when(!is_ref_mode, |d| {
                                                    d.bg(palette.card_bg)
                                                        .text_color(palette.text_primary)
                                                        .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                })
                                                .when(is_ref_mode, |d| {
                                                    d.text_color(palette.text_muted)
                                                        .hover(|s| s.text_color(palette.text_primary))
                                                })
                                                .on_click(cx.listener(|this, _, _, cx| {
                                                    this.set_preview_mode(PreviewMode::Value, cx);
                                                }))
                                                .child("Value"),
                                        )
                                        .child(
                                            div()
                                                .id("preview-mode-ref")
                                                .px_2()
                                                .py(px(2.0))
                                                .rounded(px(4.0))
                                                .cursor_pointer()
                                                .text_size(px(10.5))
                                                .when(is_ref_mode, |d| {
                                                    d.bg(palette.card_bg)
                                                        .text_color(space_color)
                                                        .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                })
                                                .when(!is_ref_mode, |d| {
                                                    d.text_color(palette.text_muted)
                                                        .hover(|s| s.text_color(palette.text_primary))
                                                })
                                                .on_click(cx.listener(|this, _, _, cx| {
                                                    this.set_preview_mode(
                                                        PreviewMode::Reference,
                                                        cx,
                                                    );
                                                }))
                                                .child("Reference"),
                                        ),
                                )
                            }),
                    ),
            )
            // Body: Value OR Reference
            .child(if is_ref_mode {
                self.render_reference_preview_body(
                    pane,
                    cache.fk.as_ref().unwrap(),
                    space_color,
                    palette,
                    cx,
                )
            } else {
                self.render_value_preview_body(pane, cache, space_color, palette, cx)
            })
            .into_any_element()
    }

    fn render_value_preview_body(
        &self,
        pane: &GridPreviewState,
        cache: &PreviewValueCache,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let format_pills: [(CellFormat, &str, &str); 4] = [
            (CellFormat::Auto, "Auto", "preview-fmt-auto"),
            (CellFormat::Json, "JSON", "preview-fmt-json"),
            (CellFormat::Xml, "XML", "preview-fmt-xml"),
            (CellFormat::Plain, "Plain", "preview-fmt-plain"),
        ];

        v_flex()
            .flex_1()
            .min_h_0()
            .w_full()
            // Format toolbar
            .child(
                h_flex()
                    .px_3()
                    .py_1p5()
                    .border_b_1()
                    .border_color(palette.hairline_subtle)
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1()
                            .children(format_pills.into_iter().map(|(fmt, label, id)| {
                                let active = pane.format == fmt;
                                div()
                                    .id(id)
                                    .px_1p5()
                                    .py(px(2.0))
                                    .rounded(px(4.0))
                                    .cursor_pointer()
                                    .text_size(px(10.5))
                                    .when(active, |d| {
                                        d.bg(space_color.opacity(0.15))
                                            .text_color(space_color)
                                            .font_weight(gpui_kit::FontWeight::MEDIUM)
                                    })
                                    .when(!active, |d| {
                                        d.text_color(palette.text_muted)
                                            .hover(|s| s.bg(palette.subtle_hover))
                                    })
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        this.set_preview_format(fmt, cx);
                                    }))
                                    .child(label)
                            })),
                    )
                    .child(
                        Button::new("preview-copy-btn")
                            .ghost()
                            .xsmall()
                            .icon(if pane.copied_toast {
                                IconName::Check
                            } else {
                                IconName::Copy
                            })
                            .label(if pane.copied_toast { "Copied" } else { "Copy" })
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.copy_preview_value(cx);
                            })),
                    ),
            )
            // Content area (read directly from O(1) cached lines/text)
            .child(match &cache.body {
                PreviewBodyData::Null => div()
                    .flex_1()
                    .flex()
                    .items_center()
                    .justify_center()
                    .p_4()
                    .child(
                        div()
                            .px_2p5()
                            .py_1()
                            .rounded(px(6.0))
                            .bg(palette.subtle_fill)
                            .text_xs()
                            .font_family(cx.theme().mono_font_family.clone())
                            .italic()
                            .text_color(palette.text_muted)
                            .child("NULL"),
                    )
                    .into_any_element(),
                PreviewBodyData::Empty => div()
                    .flex_1()
                    .flex()
                    .items_center()
                    .justify_center()
                    .p_4()
                    .child(
                        div()
                            .text_xs()
                            .italic()
                            .text_color(palette.text_muted)
                            .child("Empty string"),
                    )
                    .into_any_element(),
                PreviewBodyData::WrappedPlain { text, meta_label } => v_flex()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .when_some(meta_label.clone(), |el, meta| {
                        el.child(
                            h_flex()
                                .px_3()
                                .py_1()
                                .bg(palette.subtle_fill)
                                .border_b_1()
                                .border_color(palette.hairline_subtle)
                                .text_size(px(10.5))
                                .text_color(palette.text_muted)
                                .child(meta),
                        )
                    })
                    .child(
                        div()
                            .id("preview-value-scroll")
                            .flex_1()
                            .min_h_0()
                            .w_full()
                            .overflow_y_scroll()
                            .p_3()
                            .font_family(cx.theme().mono_font_family.clone())
                            .text_size(px(12.0))
                            .line_height(px(18.0))
                            .text_color(palette.text_primary)
                            .child(text.clone()),
                    )
                    .into_any_element(),
                PreviewBodyData::VirtualizedLines {
                    lines,
                    longest_line_ix,
                    meta_label,
                } => {
                    let line_count = lines.len();
                    let longest_ix = *longest_line_ix;
                    v_flex()
                        .flex_1()
                        .min_h_0()
                        .w_full()
                        .when_some(meta_label.clone(), |el, meta| {
                            el.child(
                                h_flex()
                                    .px_3()
                                    .py_1()
                                    .bg(palette.subtle_fill)
                                    .border_b_1()
                                    .border_color(palette.hairline_subtle)
                                    .text_size(px(10.5))
                                    .text_color(palette.text_muted)
                                    .child(meta),
                            )
                        })
                        .child(
                            div()
                                .relative()
                                .flex_1()
                                .min_h_0()
                                .w_full()
                                .overflow_hidden()
                                .child(
                                    uniform_list(
                                        "preview-value-lines",
                                        line_count,
                                        cx.processor(move |this, range: Range<usize>, _window, cx| {
                                            let mono_font = cx.theme().mono_font_family.clone();
                                            let mut items = Vec::with_capacity(range.len());
                                            let lines_slice = this
                                                .preview_pane
                                                .as_ref()
                                                .and_then(|p| p.value_cache.as_ref())
                                                .and_then(|c| match &c.body {
                                                    PreviewBodyData::VirtualizedLines {
                                                        lines,
                                                        ..
                                                    } => Some(lines.as_slice()),
                                                    _ => None,
                                                });
                                            let Some(lines_slice) = lines_slice else {
                                                return items;
                                            };
                                            for idx in range {
                                                let Some(line) = lines_slice.get(idx) else {
                                                    continue;
                                                };
                                                let content = if line.text.is_empty() {
                                                    SharedString::new_static(" ").into_any_element()
                                                } else if line.highlights.is_empty() {
                                                    line.text.clone().into_any_element()
                                                } else {
                                                    StyledText::new(line.text.clone())
                                                        .with_highlights(
                                                            line.highlights.iter().cloned(),
                                                        )
                                                        .into_any_element()
                                                };
                                                items.push(
                                                    div()
                                                        .h(px(18.0))
                                                        .line_height(px(18.0))
                                                        .font_family(mono_font.clone())
                                                        .text_size(px(12.0))
                                                        .text_color(palette.text_primary)
                                                        .whitespace_nowrap()
                                                        .child(content)
                                                        .into_any_element(),
                                                );
                                            }
                                            items
                                        }),
                                    )
                                    .size_full()
                                    .px_3()
                                    .py_2()
                                    .with_horizontal_sizing_behavior(
                                        ListHorizontalSizingBehavior::Unconstrained,
                                    )
                                    .with_width_from_item(Some(longest_ix))
                                    .track_scroll(&pane.value_scroll_handle),
                                )
                                .child(Scrollbar::vertical(&pane.value_scroll_handle))
                                .child(Scrollbar::horizontal(&pane.value_scroll_handle)),
                        )
                        .into_any_element()
                }
            })
            .into_any_element()
    }

    fn render_reference_preview_body(
        &self,
        pane: &GridPreviewState,
        reference: &larik_data::grid_actions::ColumnReference,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let target_label = match reference.target_schema.as_deref() {
            Some(s) if !s.is_empty() => format!("{s}.{}", reference.target_table),
            _ => reference.target_table.clone(),
        };
        let key_cols: Vec<String> = reference
            .columns
            .iter()
            .map(|p| p.target_column.clone())
            .collect();

        v_flex()
            .flex_1()
            .min_h_0()
            .w_full()
            // Reference target banner
            .child(
                h_flex()
                    .px_3()
                    .py_1p5()
                    .bg(palette.subtle_fill)
                    .border_b_1()
                    .border_color(palette.hairline_subtle)
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .min_w_0()
                            .items_center()
                            .gap_1()
                            .child(
                                Icon::new(IconName::Table2)
                                    .size(px(12.0))
                                    .text_color(space_color),
                            )
                            .child(
                                div()
                                    .text_size(px(11.0))
                                    .font_family(cx.theme().mono_font_family.clone())
                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                    .text_color(palette.text_primary)
                                    .truncate()
                                    .child(target_label),
                            )
                            .child(
                                div()
                                    .text_size(px(10.5))
                                    .text_color(palette.text_muted)
                                    .truncate()
                                    .child(format!("({})", key_cols.join(", "))),
                            ),
                    )
                    .when_some(
                        match &pane.ref_state {
                            ReferencePreviewState::Loaded { outcome, .. } => Some(outcome),
                            _ => None,
                        },
                        |el, outcome| {
                            let badge = if outcome.is_list {
                                format!("List · {}", outcome.result.rows.len())
                            } else {
                                format!(
                                    "{} {}",
                                    outcome.result.rows.len(),
                                    if outcome.result.rows.len() == 1 {
                                        "match"
                                    } else {
                                        "matches"
                                    }
                                )
                            };
                            el.child(
                                div()
                                    .flex_shrink_0()
                                    .px_1p5()
                                    .py(px(1.0))
                                    .rounded(px(4.0))
                                    .bg(palette.card_bg)
                                    .text_size(px(10.0))
                                    .text_color(palette.text_muted)
                                    .child(badge),
                            )
                        },
                    ),
            )
            // Body by ref_state
            .child(match &pane.ref_state {
                ReferencePreviewState::Idle | ReferencePreviewState::Loading => h_flex()
                    .flex_1()
                    .items_center()
                    .justify_center()
                    .gap_2()
                    .p_4()
                    .child(Spinner::new().small())
                    .child(
                        div()
                            .text_xs()
                            .text_color(palette.text_muted)
                            .child("Loading referenced rows…"),
                    )
                    .into_any_element(),
                ReferencePreviewState::NullValue => div()
                    .flex_1()
                    .flex()
                    .items_center()
                    .justify_center()
                    .p_4()
                    .child(
                        div()
                            .text_xs()
                            .text_color(palette.text_muted)
                            .child("Cell value is NULL — no referenced row."),
                    )
                    .into_any_element(),
                ReferencePreviewState::Error(msg) => div()
                    .p_3()
                    .child(
                        h_flex()
                            .p_2p5()
                            .rounded(px(6.0))
                            .bg(palette.danger.opacity(0.10))
                            .border_1()
                            .border_color(palette.danger.opacity(0.30))
                            .gap_2()
                            .items_start()
                            .child(
                                Icon::new(IconName::CircleAlert)
                                    .size(px(14.0))
                                    .text_color(palette.danger),
                            )
                            .child(
                                div()
                                    .text_xs()
                                    .text_color(palette.text_primary)
                                    .child(msg.clone()),
                            ),
                    )
                    .into_any_element(),
                ReferencePreviewState::Loaded {
                    outcome,
                    matched_set,
                    warning,
                } => self.render_reference_loaded(
                    pane,
                    outcome,
                    matched_set,
                    warning.as_deref(),
                    space_color,
                    palette,
                    cx,
                ),
            })
            .into_any_element()
    }

    fn render_reference_loaded(
        &self,
        pane: &GridPreviewState,
        outcome: &ReferenceLookupOutcome,
        matched_set: &HashSet<usize>,
        warning: Option<&str>,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let mono = cx.theme().mono_font_family.clone();

        v_flex()
            .flex_1()
            .min_h_0()
            .w_full()
            .when_some(warning, |el, msg| {
                el.child(
                    h_flex()
                        .px_3()
                        .py_1p5()
                        .bg(palette.warning.opacity(0.12))
                        .border_b_1()
                        .border_color(palette.warning.opacity(0.28))
                        .gap_1p5()
                        .items_center()
                        .child(
                            Icon::new(IconName::Info)
                                .size(px(12.0))
                                .text_color(palette.warning),
                        )
                        .child(
                            div()
                                .text_size(px(11.0))
                                .text_color(palette.text_primary)
                                .child(msg.to_string()),
                        ),
                )
            })
            .child(if outcome.result.rows.is_empty() {
                div()
                    .flex_1()
                    .flex()
                    .items_center()
                    .justify_center()
                    .p_4()
                    .child(
                        div()
                            .text_xs()
                            .text_color(palette.text_muted)
                            .child("No rows returned."),
                    )
                    .into_any_element()
            } else if outcome.is_list {
                // Small table (<= 200 rows): virtualized compact list with sticky column header.
                let cols = &outcome.result.columns;
                let max_visible_cols = cols.len().min(5);
                let row_count = outcome.result.rows.len();
                v_flex()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    // Sticky header row
                    .child(
                        h_flex()
                            .w_full()
                            .px_2p5()
                            .py_1p5()
                            .bg(palette.subtle_fill)
                            .border_b_1()
                            .border_color(palette.hairline_subtle)
                            .gap_2()
                            .children(cols.iter().take(max_visible_cols).map(|c| {
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .text_size(px(10.5))
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                    .text_color(palette.text_secondary)
                                    .truncate()
                                    .child(c.name.clone())
                            })),
                    )
                    // Virtualized data rows
                    .child(
                        div()
                            .relative()
                            .flex_1()
                            .min_h_0()
                            .w_full()
                            .overflow_hidden()
                            .child(
                                uniform_list(
                                    "preview-ref-list",
                                    row_count,
                                    cx.processor(move |this, range: Range<usize>, _window, cx| {
                                        let mono_row = cx.theme().mono_font_family.clone();
                                        let mut items = Vec::with_capacity(range.len());
                                        let loaded = this
                                            .preview_pane
                                            .as_ref()
                                            .and_then(|p| match &p.ref_state {
                                                ReferencePreviewState::Loaded {
                                                    outcome,
                                                    matched_set,
                                                    ..
                                                } => Some((outcome, matched_set)),
                                                _ => None,
                                            });
                                        let Some((outcome, matched_set)) = loaded else {
                                            return items;
                                        };
                                        for r_ix in range {
                                            let Some(row) = outcome.result.rows.get(r_ix) else {
                                                continue;
                                            };
                                            let is_match = matched_set.contains(&r_ix);
                                            let mono_cell = mono_row.clone();
                                            items.push(
                                                h_flex()
                                                    .relative()
                                                    .w_full()
                                                    .h(px(26.0))
                                                    .px_2p5()
                                                    .items_center()
                                                    .gap_2()
                                                    .border_b_1()
                                                    .border_color(
                                                        palette.hairline_subtle.opacity(0.6),
                                                    )
                                                    .when(is_match, |d| {
                                                        d.bg(space_color.opacity(0.14))
                                                    })
                                                    .when(is_match, |d| {
                                                        d.child(
                                                            div()
                                                                .absolute()
                                                                .left_0()
                                                                .top_0()
                                                                .bottom_0()
                                                                .w(px(3.0))
                                                                .bg(space_color),
                                                        )
                                                    })
                                                    .children(row.iter().take(max_visible_cols).map(
                                                        move |cell| {
                                                            let text =
                                                                cell.as_deref().unwrap_or("NULL");
                                                            let is_null = cell.is_none();
                                                            div()
                                                                .flex_1()
                                                                .min_w_0()
                                                                .font_family(mono_cell.clone())
                                                                .text_size(px(11.0))
                                                                .truncate()
                                                                .when(is_null, |d| {
                                                                    d.italic().text_color(
                                                                        palette.text_muted,
                                                                    )
                                                                })
                                                                .when(!is_null, |d| {
                                                                    d.text_color(if is_match {
                                                                        palette.text_primary
                                                                    } else {
                                                                        palette.text_secondary
                                                                    })
                                                                    .when(is_match, |d| {
                                                                        d.font_weight(
                                                                            gpui_kit::FontWeight::MEDIUM,
                                                                        )
                                                                    })
                                                                })
                                                                .child(text.to_string())
                                                        },
                                                    ))
                                                    .into_any_element(),
                                            );
                                        }
                                        items
                                    }),
                                )
                                .size_full()
                                .track_scroll(&pane.ref_scroll_handle),
                            )
                            .child(Scrollbar::vertical(&pane.ref_scroll_handle)),
                    )
                    .into_any_element()
            } else {
                // Large table (> 200 rows): filtered lookup rendered as stacked Record Cards (matching WinUI)
                let cols = &outcome.result.columns;
                v_flex()
                    .id("preview-ref-cards")
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .overflow_y_scroll()
                    .p_3()
                    .gap_2p5()
                    .children(outcome.result.rows.iter().enumerate().map(|(r_ix, row)| {
                        let is_match = matched_set.contains(&r_ix);
                        let mono_card = mono.clone();
                        v_flex()
                            .w_full()
                            .rounded(px(6.0))
                            .bg(palette.subtle_fill)
                            .border_1()
                            .border_color(if is_match {
                                space_color.opacity(0.55)
                            } else {
                                palette.card_border
                            })
                            .overflow_hidden()
                            .child(
                                h_flex()
                                    .px_2p5()
                                    .py_1()
                                    .border_b_1()
                                    .border_color(palette.hairline_subtle)
                                    .items_center()
                                    .justify_between()
                                    .child(
                                        div()
                                            .text_size(px(10.5))
                                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                            .text_color(palette.text_secondary)
                                            .child(format!("Record #{}", r_ix + 1)),
                                    )
                                    .when(is_match, |el| {
                                        el.child(
                                            div()
                                                .px_1p5()
                                                .py(px(1.0))
                                                .rounded(px(4.0))
                                                .bg(space_color.opacity(0.18))
                                                .text_size(px(9.5))
                                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                .text_color(space_color)
                                                .child("MATCH"),
                                        )
                                    }),
                            )
                            .child(v_flex().p_2().gap_1().children(
                                cols.iter().enumerate().map(move |(c_ix, col)| {
                                    let val = row.get(c_ix).and_then(|v| v.as_deref());
                                    h_flex()
                                        .w_full()
                                        .items_start()
                                        .gap_2()
                                        .child(
                                            div()
                                                .w(px(116.0))
                                                .flex_shrink_0()
                                                .text_size(px(11.0))
                                                .text_color(palette.text_muted)
                                                .truncate()
                                                .child(col.name.clone()),
                                        )
                                        .child(
                                            div()
                                                .flex_1()
                                                .min_w_0()
                                                .font_family(mono_card.clone())
                                                .text_size(px(11.0))
                                                .when(val.is_none(), |d| {
                                                    d.italic().text_color(palette.text_muted)
                                                })
                                                .when(val.is_some(), |d| {
                                                    d.text_color(palette.text_primary)
                                                })
                                                .child(val.unwrap_or("NULL").to_string()),
                                        )
                                }),
                            ))
                    }))
                    .into_any_element()
            })
            .into_any_element()
    }

    // ---------------------------------------------------------------- UI: Right-Click Grid Context Menu

    pub(crate) fn render_grid_context_menu(
        &self,
        menu: GridContextMenu,
        palette: StudioPalette,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let Some(table) = self.result_tables.get(&menu.result_id).cloned() else {
            return div().id("grid-ctx-missing").into_any_element();
        };

        let info = {
            let state = table.read(cx);
            let d = state.delegate();
            let sel = d.selection();
            let (rows, cols) = sel.map_or((1, 1), |s| s.dimensions());
            let focus = d.focused_cell().unwrap_or((menu.row_ix, menu.vis_col_opt.unwrap_or(0)));
            let focus_src_col = d.src_col_for_vis(focus.1);
            let res = d.edit_resolution();
            let is_editable = res.has_editable_targets();
            let can_edit_cell = is_editable
                && menu.vis_col_opt.is_some()
                && !d.is_row_deleted(focus.0)
                && focus_src_col.is_some_and(|sc| res.can_edit(sc));
            let is_cell_edited = focus_src_col.is_some_and(|sc| d.is_cell_edited(focus.0, sc));
            let sel_rows = d.selected_rows();
            let all_deleted = !sel_rows.is_empty() && sel_rows.iter().all(|&r| d.is_row_deleted(r));
            let has_pending = d.has_pending_changes();
            let fk_target = focus_src_col
                .and_then(|sc| d.column_reference(sc))
                .map(|r| r.target_table.clone());
            (
                rows,
                cols,
                is_editable,
                can_edit_cell,
                is_cell_edited,
                all_deleted,
                has_pending,
                fk_target,
                focus.0,
                focus.1,
            )
        };
        let (
            sel_rows_count,
            sel_cols_count,
            is_editable,
            can_edit_cell,
            is_cell_edited,
            all_deleted,
            has_pending,
            fk_target,
            focus_row,
            focus_vis_col,
        ) = info;

        let make_row = |id: &'static str,
                        icon: IconName,
                        label: String,
                        shortcut: Option<&'static str>,
                        danger: bool,
                        disabled: bool| {
            h_flex()
                .id(id)
                .h(px(28.0))
                .px_2()
                .rounded(px(5.0))
                .items_center()
                .justify_between()
                .gap_3()
                .when(!disabled, |d| {
                    d.cursor_pointer().hover(|s| s.bg(palette.subtle_hover))
                })
                .when(disabled, |d| d.opacity(0.45))
                .child(
                    h_flex()
                        .items_center()
                        .gap_2()
                        .min_w_0()
                        .child(
                            Icon::new(icon)
                                .size(px(13.0))
                                .text_color(if danger && !disabled {
                                    palette.danger
                                } else {
                                    palette.text_secondary
                                }),
                        )
                        .child(
                            div()
                                .text_size(px(12.0))
                                .text_color(if danger && !disabled {
                                    palette.danger
                                } else {
                                    palette.text_primary
                                })
                                .truncate()
                                .child(label),
                        ),
                )
                .when_some(shortcut, |el, sc| {
                    el.child(
                        div()
                            .flex_shrink_0()
                            .text_size(px(10.5))
                            .text_color(palette.text_muted)
                            .child(sc),
                    )
                })
        };
        let make_divider = || {
            div()
                .my_1()
                .h(px(1.0))
                .w_full()
                .bg(palette.hairline_subtle)
                .into_any_element()
        };

        let mut items: Vec<AnyElement> = Vec::new();

        // Copy group
        let copy_label = if sel_rows_count == 1 && sel_cols_count == 1 {
            "Copy cell".to_string()
        } else {
            format!("Copy ({sel_rows_count}×{sel_cols_count})")
        };
        items.push(
            make_row("grid-ctx-copy", IconName::Copy, copy_label, Some("Ctrl+C"), false, false)
                .on_click(cx.listener(|this, _, _, cx| {
                    this.copy_grid_selection(false, cx);
                }))
                .into_any_element(),
        );
        items.push(
            make_row(
                "grid-ctx-copy-headers",
                IconName::Copy,
                "Copy with headers".to_string(),
                Some("Ctrl+Shift+C"),
                false,
                false,
            )
            .on_click(cx.listener(|this, _, _, cx| {
                this.copy_grid_selection(true, cx);
            }))
            .into_any_element(),
        );
        items.push(
            make_row(
                "grid-ctx-copy-in",
                IconName::List,
                "Copy as IN clause".to_string(),
                None,
                false,
                false,
            )
            .on_click(cx.listener(|this, _, _, cx| {
                this.copy_grid_as_in(cx);
            }))
            .into_any_element(),
        );
        items.push(
            make_row(
                "grid-ctx-copy-values",
                IconName::Code,
                "Copy as VALUES".to_string(),
                None,
                false,
                false,
            )
            .on_click(cx.listener(|this, _, _, cx| {
                this.copy_grid_as_values(cx);
            }))
            .into_any_element(),
        );

        items.push(make_divider());

        // Preview group
        items.push(
            make_row(
                "grid-ctx-preview",
                IconName::Eye,
                "Preview value".to_string(),
                Some("Space"),
                false,
                false,
            )
            .on_click(cx.listener(|this, _, _, cx| {
                this.toggle_grid_preview(PreviewMode::Value, cx);
            }))
            .into_any_element(),
        );
        if let Some(target_table) = fk_target {
            items.push(
                make_row(
                    "grid-ctx-preview-fk",
                    IconName::Key,
                    format!("Preview reference ({target_table})"),
                    Some("Shift+Space"),
                    false,
                    false,
                )
                .on_click(cx.listener(|this, _, _, cx| {
                    this.toggle_grid_preview(PreviewMode::Reference, cx);
                }))
                .into_any_element(),
            );
        }

        items.push(make_divider());

        // Edit / Delete group
        let res_id_edit = menu.result_id.clone();
        items.push(
            make_row(
                "grid-ctx-edit",
                IconName::Pencil,
                "Edit cell".to_string(),
                Some("F2"),
                false,
                !can_edit_cell,
            )
            .when(can_edit_cell, |el| {
                el.on_click(cx.listener(move |this, _, window, cx| {
                    this.begin_cell_edit(res_id_edit.clone(), focus_row, focus_vis_col, window, cx);
                }))
            })
            .into_any_element(),
        );
        if is_cell_edited {
            items.push(
                make_row(
                    "grid-ctx-revert",
                    IconName::RotateCcw,
                    "Revert cell".to_string(),
                    None,
                    false,
                    false,
                )
                .on_click(cx.listener(|this, _, _, cx| {
                    this.revert_focused_cell(cx);
                }))
                .into_any_element(),
            );
        }
        let delete_label = if all_deleted {
            if sel_rows_count == 1 {
                "Unmark row for delete".to_string()
            } else {
                format!("Unmark {sel_rows_count} rows for delete")
            }
        } else if sel_rows_count == 1 {
            "Delete row".to_string()
        } else {
            format!("Delete {sel_rows_count} rows")
        };
        items.push(
            make_row(
                "grid-ctx-delete",
                IconName::Trash,
                delete_label,
                Some("Del"),
                !all_deleted,
                !is_editable,
            )
            .when(is_editable, |el| {
                el.on_click(cx.listener(|this, _, _, cx| {
                    this.toggle_delete_selected_rows(cx);
                }))
            })
            .into_any_element(),
        );
        if has_pending {
            items.push(
                make_row(
                    "grid-ctx-undo",
                    IconName::Undo2,
                    "Undo last edit".to_string(),
                    Some("Ctrl+Z"),
                    false,
                    false,
                )
                .on_click(cx.listener(|this, _, _, cx| {
                    this.undo_grid_edit(cx);
                }))
                .into_any_element(),
            );
        }

        items.push(make_divider());

        items.push(
            make_row(
                "grid-ctx-select-all",
                IconName::Table2,
                "Select all".to_string(),
                Some("Ctrl+A"),
                false,
                false,
            )
            .on_click(cx.listener(|this, _, _, cx| {
                this.select_all_grid(cx);
            }))
            .into_any_element(),
        );

        let vp = window.viewport_size();
        let menu_w = px(256.0);
        let estimated_h = px((items.len() as f32 * 28.0 + 24.0).min(420.0));
        let left = menu
            .position
            .x
            .min((vp.width - menu_w - px(8.0)).max(px(8.0)))
            .max(px(8.0));
        let top = menu
            .position
            .y
            .min((vp.height - estimated_h - px(8.0)).max(px(8.0)))
            .max(px(8.0));

        div()
            .id("grid-ctx-backdrop")
            .absolute()
            .inset_0()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    this.grid_context_menu = None;
                    cx.notify();
                }),
            )
            .on_mouse_down(
                MouseButton::Right,
                cx.listener(|this, _, _, cx| {
                    this.grid_context_menu = None;
                    cx.notify();
                }),
            )
            .child(
                v_flex()
                    .id("grid-ctx-card")
                    .absolute()
                    .left(left)
                    .top(top)
                    .w(menu_w)
                    .max_h(px(420.0))
                    .overflow_y_scroll()
                    .p_1p5()
                    .rounded(px(8.0))
                    .bg(palette.modal_bg)
                    .border_1()
                    .border_color(palette.card_border)
                    .shadow_2xl()
                    .gap_0p5()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    .on_mouse_down(MouseButton::Right, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    .children(items),
            )
            .into_any_element()
    }

    // ---------------------------------------------------------------- UI: Save Review Modal

    pub(crate) fn render_save_review_modal(
        &self,
        modal: &SaveReviewModal,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let mono = cx.theme().mono_font_family.clone();
        let has_deletes = modal.plan.delete_count > 0;
        let saving = modal.saving;

        let mut summary_parts = Vec::new();
        if modal.plan.update_count > 0 {
            summary_parts.push(format!("{} UPDATE", modal.plan.update_count));
        }
        if modal.plan.delete_count > 0 {
            summary_parts.push(format!("{} DELETE", modal.plan.delete_count));
        }
        let summary_badge = summary_parts.join(" · ");

        div()
            .id("save-review-backdrop")
            .absolute()
            .inset_0()
            .bg(hsla(0.0, 0.0, 0.0, 0.45))
            .flex()
            .items_center()
            .justify_center()
            .p_6()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    if !this.save_review_modal.as_ref().is_some_and(|m| m.saving) {
                        this.save_review_modal = None;
                        cx.notify();
                    }
                }),
            )
            .child(
                v_flex()
                    .id("save-review-dialog")
                    .w(px(640.0))
                    .max_w_full()
                    .max_h(px(560.0))
                    .rounded(px(10.0))
                    .bg(palette.modal_bg)
                    .border_1()
                    .border_color(palette.card_border)
                    .shadow_2xl()
                    .overflow_hidden()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    // Header
                    .child(
                        h_flex()
                            .px_5()
                            .py_3p5()
                            .border_b_1()
                            .border_color(palette.hairline_subtle)
                            .items_center()
                            .justify_between()
                            .gap_3()
                            .child(
                                v_flex()
                                    .gap_0p5()
                                    .child(
                                        h_flex()
                                            .items_center()
                                            .gap_2()
                                            .child(
                                                div()
                                                    .text_sm()
                                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                    .text_color(palette.text_primary)
                                                    .child("Review changes"),
                                            )
                                            .child(
                                                div()
                                                    .px_2()
                                                    .py(px(1.5))
                                                    .rounded(px(4.0))
                                                    .bg(if has_deletes {
                                                        palette.danger.opacity(0.15)
                                                    } else {
                                                        space_color.opacity(0.15)
                                                    })
                                                    .text_size(px(10.5))
                                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                    .text_color(if has_deletes {
                                                        palette.danger
                                                    } else {
                                                        space_color
                                                    })
                                                    .child(summary_badge),
                                            ),
                                    )
                                    .child(
                                        div()
                                            .text_xs()
                                            .text_color(palette.text_muted)
                                            .child("All statements execute inside a single transaction on this tab's session."),
                                    ),
                            )
                            .child(
                                Button::new("save-review-close")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::X)
                                    .disabled(saving)
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        if !this.save_review_modal.as_ref().is_some_and(|m| m.saving) {
                                            this.save_review_modal = None;
                                            cx.notify();
                                        }
                                    })),
                            ),
                    )
                    // Error banner (if any)
                    .when_some(modal.error.as_ref(), |el, err| {
                        el.child(
                            h_flex()
                                .mx_5()
                                .mt_3()
                                .p_2p5()
                                .rounded(px(6.0))
                                .bg(palette.danger.opacity(0.10))
                                .border_1()
                                .border_color(palette.danger.opacity(0.35))
                                .gap_2()
                                .items_start()
                                .child(
                                    Icon::new(IconName::CircleAlert)
                                        .size(px(14.0))
                                        .text_color(palette.danger),
                                )
                                .child(
                                    div()
                                        .text_xs()
                                        .text_color(palette.text_primary)
                                        .child(err.clone()),
                                ),
                        )
                    })
                    // Scrollable statement list
                    .child(
                        v_flex()
                            .id("save-review-stmts")
                            .flex_1()
                            .min_h_0()
                            .overflow_y_scroll()
                            .p_5()
                            .gap_2p5()
                            .children(modal.plan.statements.iter().enumerate().map(|(ix, stmt)| {
                                let is_del = stmt.is_delete;
                                let mono_stmt = mono.clone();
                                v_flex()
                                    .w_full()
                                    .rounded(px(6.0))
                                    .bg(if is_del {
                                        palette.danger.opacity(0.06)
                                    } else {
                                        palette.subtle_fill
                                    })
                                    .border_1()
                                    .border_color(if is_del {
                                        palette.danger.opacity(0.32)
                                    } else {
                                        palette.card_border
                                    })
                                    .p_2p5()
                                    .gap_1p5()
                                    .child(
                                        h_flex()
                                            .items_center()
                                            .justify_between()
                                            .child(
                                                div()
                                                    .px_1p5()
                                                    .py(px(1.0))
                                                    .rounded(px(4.0))
                                                    .bg(if is_del {
                                                        palette.danger.opacity(0.16)
                                                    } else {
                                                        space_color.opacity(0.16)
                                                    })
                                                    .text_size(px(10.0))
                                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                    .text_color(if is_del {
                                                        palette.danger
                                                    } else {
                                                        space_color
                                                    })
                                                    .child(if is_del { "DELETE" } else { "UPDATE" }),
                                            )
                                            .child(
                                                div()
                                                    .text_size(px(10.5))
                                                    .text_color(palette.text_muted)
                                                    .child(format!("Statement #{}", ix + 1)),
                                            ),
                                    )
                                    .child(
                                        div()
                                            .font_family(mono_stmt)
                                            .text_size(px(11.5))
                                            .line_height(px(17.0))
                                            .text_color(palette.text_primary)
                                            .child(stmt.sql.clone()),
                                    )
                            })),
                    )
                    // Footer
                    .child(
                        h_flex()
                            .px_5()
                            .py_3()
                            .border_t_1()
                            .border_color(palette.hairline_subtle)
                            .items_center()
                            .justify_end()
                            .gap_2()
                            .child(
                                Button::new("save-review-cancel")
                                    .ghost()
                                    .small()
                                    .disabled(saving)
                                    .label("Cancel")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.save_review_modal = None;
                                        cx.notify();
                                    })),
                            )
                            .child({
                                let btn = Button::new("save-review-confirm")
                                    .small()
                                    .disabled(saving)
                                    .icon(IconName::Check)
                                    .label(if saving {
                                        "Saving…"
                                    } else if has_deletes {
                                        "Apply & delete"
                                    } else {
                                        "Save changes"
                                    })
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.confirm_save_edits(cx);
                                    }));
                                if has_deletes {
                                    btn.danger()
                                } else {
                                    btn.primary()
                                }
                            }),
                    ),
            )
            .into_any_element()
    }
}

const MAX_VIRTUAL_LINE_CHARS: usize = 1_024;

#[derive(Clone, Copy)]
struct PreviewSyntaxStyles {
    property: HighlightStyle,
    string: HighlightStyle,
    number: HighlightStyle,
    keyword: HighlightStyle,
    punctuation: HighlightStyle,
    attribute: HighlightStyle,
    comment: HighlightStyle,
}

impl PreviewSyntaxStyles {
    fn from_theme(palette: StudioPalette, cx: &gpui_kit::App) -> Self {
        let syntax = &cx.theme().highlight_theme.style.syntax;
        let style_or =
            |opt: Option<HighlightStyle>, fallback: HighlightStyle| opt.unwrap_or(fallback);

        Self {
            property: style_or(
                syntax.function.map(Into::into),
                HighlightStyle::color(palette.accent),
            ),
            string: style_or(
                syntax.string.map(Into::into),
                HighlightStyle::color(palette.success),
            ),
            number: style_or(
                syntax.number.map(Into::into),
                HighlightStyle::color(palette.warning),
            ),
            keyword: style_or(
                syntax.keyword.map(Into::into),
                HighlightStyle {
                    color: Some(palette.accent),
                    font_weight: Some(FontWeight::MEDIUM),
                    ..Default::default()
                },
            ),
            punctuation: style_or(
                syntax.punctuation.map(Into::into),
                HighlightStyle::color(palette.text_muted),
            ),
            attribute: style_or(
                syntax.attribute.map(Into::into),
                HighlightStyle::color(palette.warning),
            ),
            comment: style_or(
                syntax.comment.map(Into::into),
                HighlightStyle {
                    color: Some(palette.text_muted),
                    font_style: Some(FontStyle::Italic),
                    ..Default::default()
                },
            ),
        }
    }

    fn style_for(&self, kind: CellSyntaxTokenKind) -> HighlightStyle {
        match kind {
            CellSyntaxTokenKind::Property | CellSyntaxTokenKind::Tag => self.property,
            CellSyntaxTokenKind::String => self.string,
            CellSyntaxTokenKind::Number => self.number,
            CellSyntaxTokenKind::Keyword | CellSyntaxTokenKind::Declaration => self.keyword,
            CellSyntaxTokenKind::Punctuation => self.punctuation,
            CellSyntaxTokenKind::Attribute => self.attribute,
            CellSyntaxTokenKind::Comment => self.comment,
        }
    }
}

/// Split `text` into virtualized `PreviewLine` entries with per-line rebased syntax highlight
/// spans. Any single line longer than `MAX_VIRTUAL_LINE_CHARS` is chunked on UTF-8 char
/// boundaries so pathological single-line blobs never force the text shaper to lay out a huge run.
/// Returns `(lines, longest_line_ix)`.
fn build_preview_lines(
    text: &str,
    tokens: &[CellSyntaxToken],
    styles: &PreviewSyntaxStyles,
) -> (Vec<PreviewLine>, usize) {
    let mut segments: Vec<(usize, usize, usize)> = Vec::new();
    let bytes = text.as_bytes();
    let mut pos = 0usize;

    while pos <= text.len() {
        let nl_rel = text[pos..].find('\n');
        let raw_end = nl_rel.map_or(text.len(), |r| pos + r);
        let line_end = if raw_end > pos && bytes[raw_end - 1] == b'\r' {
            raw_end - 1
        } else {
            raw_end
        };

        if line_end == pos {
            segments.push((pos, pos, 0));
        } else if line_end - pos <= MAX_VIRTUAL_LINE_CHARS {
            let char_len = text[pos..line_end].chars().count();
            segments.push((pos, line_end, char_len));
        } else {
            let slice = &text[pos..line_end];
            let mut seg_start_rel = 0usize;
            let mut count = 0usize;
            for (rel_idx, _) in slice.char_indices() {
                if count == MAX_VIRTUAL_LINE_CHARS {
                    segments.push((pos + seg_start_rel, pos + rel_idx, count));
                    seg_start_rel = rel_idx;
                    count = 0;
                }
                count += 1;
            }
            if seg_start_rel < slice.len() {
                segments.push((pos + seg_start_rel, line_end, count));
            }
        }

        match nl_rel {
            Some(_) => {
                pos = raw_end + 1;
                if pos == text.len() {
                    break;
                }
            }
            None => break,
        }
    }

    let mut lines = Vec::with_capacity(segments.len());
    let mut tok_ix = 0usize;
    let mut longest_line_ix = 0usize;
    let mut longest_chars = 0usize;

    for (line_ix, (seg_start, seg_end, char_len)) in segments.into_iter().enumerate() {
        if char_len > longest_chars {
            longest_chars = char_len;
            longest_line_ix = line_ix;
        }

        while tok_ix < tokens.len() && tokens[tok_ix].end <= seg_start {
            tok_ix += 1;
        }

        let mut highlights = Vec::new();
        let mut j = tok_ix;
        while j < tokens.len() && tokens[j].start < seg_end {
            let tok = tokens[j];
            let s = tok.start.max(seg_start);
            let e = tok.end.min(seg_end);
            if s < e {
                highlights.push(((s - seg_start)..(e - seg_start), styles.style_for(tok.kind)));
            }
            j += 1;
        }
        while tok_ix < j && tokens[tok_ix].end <= seg_end {
            tok_ix += 1;
        }

        let line_str = &text[seg_start..seg_end];
        let shared = if line_str.is_empty() {
            SharedString::new_static("")
        } else {
            SharedString::from(line_str.to_string())
        };
        lines.push(PreviewLine {
            text: shared,
            highlights,
        });
    }

    (lines, longest_line_ix)
}

#[cfg(test)]
mod preview_perf_tests {
    use super::*;

    fn dummy_styles() -> PreviewSyntaxStyles {
        let base = HighlightStyle::default();
        PreviewSyntaxStyles {
            property: base,
            string: base,
            number: base,
            keyword: base,
            punctuation: base,
            attribute: base,
            comment: base,
        }
    }

    #[test]
    fn build_preview_lines_slices_multiline_tokens_and_chunks_long_lines() {
        let xml = "<!-- line one\r\nline two -->\r\n<tag attr=\"v\">日本語</tag>";
        let tokens = tokenize_cell_syntax_capped(xml, CellFormat::Xml);
        let (lines, longest_ix) = build_preview_lines(xml, &tokens, &dummy_styles());

        assert_eq!(lines.len(), 3);
        assert_eq!(lines[0].text.as_ref(), "<!-- line one");
        assert_eq!(lines[1].text.as_ref(), "line two -->");
        assert_eq!(lines[2].text.as_ref(), "<tag attr=\"v\">日本語</tag>");
        assert_eq!(longest_ix, 2);

        // Multi-line comment token spans lines 0 and 1 cleanly with rebased offsets.
        assert_eq!(lines[0].highlights.len(), 1);
        assert_eq!(lines[0].highlights[0].0, 0..lines[0].text.len());
        assert_eq!(lines[1].highlights.len(), 1);
        assert_eq!(lines[1].highlights[0].0, 0..lines[1].text.len());
        // All highlight ranges on line 2 stay strictly ordered and within valid UTF-8 boundaries.
        let mut prev_end = 0usize;
        for (range, _) in &lines[2].highlights {
            assert!(range.start >= prev_end && range.start < range.end);
            assert!(lines[2].text.is_char_boundary(range.start));
            assert!(lines[2].text.is_char_boundary(range.end));
            prev_end = range.end;
        }

        // Single-line multi-byte blob larger than MAX_VIRTUAL_LINE_CHARS gets chunked cleanly.
        let blob = "é".repeat(MAX_VIRTUAL_LINE_CHARS + 50);
        let (blob_lines, _) = build_preview_lines(&blob, &[], &dummy_styles());
        assert_eq!(blob_lines.len(), 2);
        assert_eq!(blob_lines[0].text.chars().count(), MAX_VIRTUAL_LINE_CHARS);
        assert_eq!(blob_lines[1].text.chars().count(), 50);
    }
}


