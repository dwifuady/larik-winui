use std::collections::{HashMap, HashSet};
use std::ops::Range;
use std::rc::Rc;
use std::sync::{Arc, RwLock};
use std::time::{Duration, Instant};

use gpui_kit::assets::IconName;
use gpui_kit::base::Disableable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{
    Editor, EditorState, Enter, IndentInline, Input, InputEditsEvent, InputEvent, InputState,
    ShowCompletions,
};
use gpui_kit::component::resizable::{resizable_panel, v_resizable};
use gpui_kit::component::scroll::Scrollbar;
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::table::{DataTable, TableState};
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, AppContext as _, ClickEvent, ClipboardItem, Context, Entity, FocusHandle,
    Focusable, Hsla, Image, ImageFormat, InteractiveElement as _, IntoElement, KeyBinding,
    MouseButton, MouseDownEvent, ParentElement as _, Pixels, Point, Render, SharedString,
    StatefulInteractiveElement as _, Styled as _, Subscription, UniformListScrollHandle, Window,
    div, hsla, px, rgb, transparent_black, uniform_list,
};

const APP_ICON_PNG: &[u8] = include_bytes!("../assets/app_icon.png");
const FOOTER_MAX_WORKSPACES: usize = 3;

/// Computes the clamped `[start, end)` window slice for the sidebar footer workspace strip
/// (at most `FOOTER_MAX_WORKSPACES` icons visible at a time).
fn footer_space_window(total: usize, offset: usize) -> (usize, usize) {
    if total <= FOOTER_MAX_WORKSPACES {
        return (0, total);
    }
    let max_start = total - FOOTER_MAX_WORKSPACES;
    let start = offset.min(max_start);
    (start, start + FOOTER_MAX_WORKSPACES)
}

use larik_core::run_log::{RunLogRecord, run_status};
use larik_core::session::SessionManager;
use larik_core::snippets::{
    SnippetRecord, builtin_catalog, builtin_default_for, builtin_to_record, snippets_for_dialect,
    sort_snippets,
};
use larik_core::store::StudioStore;
use larik_core::{
    ArchivedTabState, SPACE_COLORS, SpaceState, StudioState, TabGroupState, TabState,
    ThemePreference, now_rfc3339,
};
use larik_data::sql_language::SqlLanguage;
use larik_data::{
    ConnectionState, DatabaseInfo, ProviderCatalog, QueryExecutionOptions, QueryResult, TableKind,
    TableSchema,
};
use uuid::Uuid;

use crate::completion::{CompletionSnapshot, PostInsertAction, SqlCompletionProvider};
use crate::connection_dialog::{
    ConnectionModal, ConnectionModalAction, render_connection_modal, workspace_icon_name,
    workspace_monogram,
};
use crate::history_page::{HistoryPageState, HistorySegment, render_history_page};
use crate::results_table::ResultTableDelegate;
use crate::settings_page::{SettingsPageState, render_settings_page};
use crate::snippets_page::SnippetsPageState;
use crate::theme::{StudioPalette, apply_larik_theme, parse_hex_color};
use crate::window_title_bar::render_app_title_bar;

gpui_kit::actions!(
    larik,
    [
        RunStatement,
        RunStatementNewResult,
        RunAll,
        NewTab,
        CloseTab,
        NextTab,
        PrevTab,
        ToggleRail,
        ToggleSchema,
        ToggleTheme,
        OpenHistory,
        DismissModal,
        AcceptCompletionOrIndent,
    ]
);

pub fn bind_studio_keys(cx: &mut App) {
    cx.bind_keys([
        KeyBinding::new("ctrl-enter", RunStatement, None),
        KeyBinding::new("ctrl-\\", RunStatementNewResult, None),
        KeyBinding::new("f5", RunAll, None),
        KeyBinding::new("ctrl-t", NewTab, None),
        KeyBinding::new("ctrl-w", CloseTab, None),
        KeyBinding::new("ctrl-tab", NextTab, None),
        KeyBinding::new("ctrl-shift-tab", PrevTab, None),
        KeyBinding::new("ctrl-b", ToggleRail, None),
        KeyBinding::new("ctrl-k", ToggleSchema, None),
        KeyBinding::new("ctrl-shift-l", ToggleTheme, None),
        KeyBinding::new("ctrl-shift-h", OpenHistory, None),
        KeyBinding::new("escape", DismissModal, Some("StudioModal")),
        KeyBinding::new("tab", AcceptCompletionOrIndent, Some("Input && mode == editor")),
        KeyBinding::new("ctrl-space", ShowCompletions, Some("Input && mode == editor")),
    ]);
}

#[derive(Clone)]
struct DraggedQueryTab {
    space_id: String,
    tab_id: String,
    title: SharedString,
}

#[derive(Clone)]
struct DraggedResultTab {
    tab_id: String,
    result_id: String,
    title: SharedString,
}

struct DragGhostView {
    label: SharedString,
    accent: Hsla,
    bg: Hsla,
    border: Hsla,
    text: Hsla,
}

impl Render for DragGhostView {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        h_flex()
            .px_2p5()
            .py_1()
            .rounded(px(6.0))
            .bg(self.bg)
            .border_1()
            .border_color(self.border)
            .shadow_lg()
            .items_center()
            .gap_1p5()
            .child(div().w(px(2.5)).h(px(12.0)).rounded_full().bg(self.accent))
            .child(
                div()
                    .text_xs()
                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                    .text_color(self.text)
                    .child(self.label.clone()),
            )
    }
}

#[derive(Clone)]
enum RailContextMenuTarget {
    Tab { tab_id: String },
    Group { group_id: String },
}

#[derive(Clone)]
struct RailContextMenu {
    target: RailContextMenuTarget,
    position: Point<Pixels>,
}

#[derive(Default, Clone)]
struct SpaceRuntime {
    state: Option<ConnectionState>,
    server_version: Option<SharedString>,
    databases: Arc<Vec<DatabaseInfo>>,
    schema: Arc<Vec<TableSchema>>,
    schema_cache: HashMap<String, Arc<Vec<TableSchema>>>,
    last_error: Option<SharedString>,
}

impl SpaceRuntime {
    fn connection_state(&self) -> ConnectionState {
        self.state.unwrap_or(ConnectionState::Disconnected)
    }

    fn cached_schema(&self, db: Option<&str>) -> Option<Arc<Vec<TableSchema>>> {
        let key = db?.trim().to_ascii_lowercase();
        if key.is_empty() {
            return None;
        }
        self.schema_cache.get(&key).cloned()
    }

    fn cache_schema(&mut self, db: Option<&str>, schema: Arc<Vec<TableSchema>>) {
        if let Some(key) = db.map(str::trim).filter(|s| !s.is_empty()) {
            self.schema_cache.insert(key.to_ascii_lowercase(), schema);
        }
    }
}

#[derive(Clone)]
struct ResultTabItem {
    id: String,
    title: String,
    result: Arc<QueryResult>,
}

impl ResultTabItem {
    fn is_messages_only(&self) -> bool {
        is_messages_only_result(&self.result)
    }

    fn detail_label(&self) -> String {
        if self.result.is_error {
            "Error".to_string()
        } else if self.is_messages_only() {
            let count = self.result.messages.len();
            if count == 1 {
                "1 message".to_string()
            } else {
                format!("{count} messages")
            }
        } else if self.result.columns.is_empty() {
            self.result.message.clone()
        } else {
            let count = self.result.rows.len();
            if count == 1 {
                "1 row".to_string()
            } else {
                format!("{count} rows")
            }
        }
    }
}

fn is_messages_only_result(result: &QueryResult) -> bool {
    !result.is_error && result.columns.is_empty() && !result.messages.is_empty()
}

#[derive(Default, Clone)]
struct TabRuntime {
    running: bool,
    results: Vec<ResultTabItem>,
    active_result_id: Option<String>,
    show_messages: bool,
    results_dismissed: bool,
    last_summary: Option<SharedString>,
}

impl TabRuntime {
    fn active_result(&self) -> Option<&ResultTabItem> {
        self.active_result_id
            .as_deref()
            .and_then(|id| self.results.iter().find(|r| r.id == id))
            .or_else(|| self.results.first())
    }

    fn next_result_title(&self, result: &QueryResult) -> String {
        if is_messages_only_result(result) {
            return "Messages".to_string();
        }
        let used: HashSet<&str> = self.results.iter().map(|r| r.title.as_str()).collect();
        let mut number = 1usize;
        while used.contains(format!("Result {number}").as_str()) {
            number += 1;
        }
        format!("Result {number}")
    }

    /// Mirrors WinUI `TabViewModel.ApplyResults(results, append)`:
    /// - `append == false`: replaces the active result tab at its current index (carrying its
    ///   title when `i == 0` and same kind — grid vs messages — so custom names survive re-runs).
    /// - `append == true` (`Ctrl+\`): appends new result tab(s) at the end without removing
    ///   existing result tabs.
    fn apply_results(&mut self, incoming: Vec<QueryResult>, append: bool) {
        if incoming.is_empty() {
            return;
        }

        let insert_index = if append || self.results.is_empty() {
            self.results.len()
        } else {
            self.active_result_id
                .as_deref()
                .and_then(|id| self.results.iter().position(|r| r.id == id))
                .unwrap_or(0)
                .min(self.results.len() - 1)
        };

        let replaced = if !append && !self.results.is_empty() {
            Some(self.results.remove(insert_index))
        } else {
            None
        };

        let incoming_len = incoming.len();
        for (i, res) in incoming.into_iter().enumerate() {
            let carry_name = i == 0
                && replaced.as_ref().is_some_and(|old| {
                    old.is_messages_only() == is_messages_only_result(&res)
                        && !old.result.is_error
                        && !res.is_error
                });
            let title = if carry_name {
                replaced.as_ref().unwrap().title.clone()
            } else {
                self.next_result_title(&res)
            };
            let id = if i == 0 {
                replaced
                    .as_ref()
                    .map(|old| old.id.clone())
                    .unwrap_or_else(|| Uuid::new_v4().to_string())
            } else {
                Uuid::new_v4().to_string()
            };
            self.results.insert(
                insert_index + i,
                ResultTabItem {
                    id,
                    title,
                    result: Arc::new(res),
                },
            );
        }

        // Prefer the first grid in the newly inserted slice when multiple results come back,
        // otherwise select the first newly inserted item.
        let slice_end = (insert_index + incoming_len).min(self.results.len());
        let preferred_idx = (insert_index..slice_end)
            .find(|&idx| !self.results[idx].result.columns.is_empty())
            .unwrap_or(insert_index.min(self.results.len() - 1));

        self.active_result_id = self.results.get(preferred_idx).map(|r| r.id.clone());
        self.show_messages = false;
        self.results_dismissed = false;
    }

    fn close_result(&mut self, result_id: &str) {
        let Some(pos) = self.results.iter().position(|r| r.id == result_id) else {
            return;
        };
        let was_active = self.active_result_id.as_deref() == Some(result_id);
        self.results.remove(pos);
        if self.results.is_empty() {
            self.active_result_id = None;
            self.show_messages = false;
            self.results_dismissed = true;
        } else if was_active {
            let next_pos = pos.min(self.results.len() - 1);
            self.active_result_id = self.results.get(next_pos).map(|r| r.id.clone());
        }
    }

    fn reorder_result(&mut self, dragged_id: &str, target_id: &str) -> bool {
        if dragged_id == target_id {
            return false;
        }
        let Some(from) = self.results.iter().position(|r| r.id == dragged_id) else {
            return false;
        };
        let Some(to) = self.results.iter().position(|r| r.id == target_id) else {
            return false;
        };
        let item = self.results.remove(from);
        self.results.insert(to, item);
        true
    }

    fn rename_result(&mut self, result_id: &str, new_title: &str) -> bool {
        let trimmed = new_title.trim();
        if trimmed.is_empty() {
            return false;
        }
        let Some(item) = self.results.iter_mut().find(|r| r.id == result_id) else {
            return false;
        };
        if item.title == trimmed {
            return false;
        }
        item.title = trimmed.to_string();
        true
    }
}

enum SchemaRow {
    Table {
        key: SharedString,
        is_view: bool,
        expanded: bool,
        col_count: SharedString,
    },
    Column {
        name: SharedString,
        data_type: Option<SharedString>,
        is_pk: bool,
        is_fk: bool,
    },
}

pub struct StudioView {
    focus_handle: FocusHandle,
    catalog: ProviderCatalog,
    store: StudioStore,
    sessions: Arc<SessionManager>,
    state: StudioState,

    space_runtime: HashMap<String, SpaceRuntime>,
    tab_runtime: HashMap<String, TabRuntime>,

    editor_state: Entity<EditorState>,
    completion_snapshot: Arc<RwLock<CompletionSnapshot>>,
    pending_post_insert: Arc<RwLock<HashMap<String, PostInsertAction>>>,
    table_state: Entity<TableState<ResultTableDelegate>>,
    _editor_sub: Subscription,
    _editor_edits_sub: Subscription,
    suppress_editor_sync: bool,
    snippets: Vec<SnippetRecord>,

    // Virtualized schema drawer state
    schema_filter_input: Entity<InputState>,
    _schema_filter_sub: Subscription,
    schema_filter_query: String,
    schema_rows: Vec<SchemaRow>,
    schema_matched_tables: usize,
    schema_scroll_handle: UniformListScrollHandle,

    // UI transient controls
    app_icon: Arc<Image>,
    managing_workspaces: bool,
    managing_settings: bool,
    pub(crate) settings_page: Option<SettingsPageState>,
    pub(crate) history_page: Option<HistoryPageState>,
    footer_space_offset: usize,
    workspace_dropdown_open: bool,
    db_dropdown_open: bool,
    expanded_tables: HashSet<String>,
    renaming_tab_id: Option<String>,
    rename_input: Option<Entity<InputState>>,
    _rename_sub: Option<Subscription>,
    renaming_group_id: Option<String>,
    rename_group_input: Option<Entity<InputState>>,
    _rename_group_sub: Option<Subscription>,
    renaming_result: Option<(String, String)>,
    rename_result_input: Option<Entity<InputState>>,
    _rename_result_sub: Option<Subscription>,
    rail_context_menu: Option<RailContextMenu>,
    last_grouping_space_id: Option<String>,
    last_grouping_ids: Vec<String>,
    db_palette_open: bool,
    db_palette_input: Option<Entity<InputState>>,
    _db_palette_sub: Option<Subscription>,
    modal: Option<ConnectionModal>,
    copied_toast: bool,
}

impl StudioView {
    pub fn new(
        catalog: ProviderCatalog,
        store: StudioStore,
        sessions: Arc<SessionManager>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let mut state = store.load(&catalog);
        for space in &mut state.spaces {
            space.rebuild_tab_order();
        }
        // Launch maintenance matching WinUI StudioViewModel: prune expired run logs and run auto-archive sweep.
        let _ = store.prune_run_log();
        let auto_archived = state.run_auto_archive(chrono::Utc::now());
        if !auto_archived.is_empty() {
            let _ = store.save(&state, &catalog);
        }
        let snippets = store.load_snippets().unwrap_or_else(|_| {
            builtin_catalog()
                .into_iter()
                .map(|d| builtin_to_record(&d, true))
                .collect()
        });
        apply_larik_theme(state.theme, Some(window), cx);

        let initial_sql = state
            .active_space()
            .and_then(|s| s.active_tab())
            .map(|t| t.content.clone())
            .unwrap_or_default();

        let completion_snapshot = Arc::new(RwLock::new(CompletionSnapshot::default()));
        let provider_ref = completion_snapshot.clone();
        let pending_post_insert: Arc<RwLock<HashMap<String, PostInsertAction>>> =
            Arc::new(RwLock::new(HashMap::new()));
        let post_insert_ref = pending_post_insert.clone();

        let editor_state = cx.new(|cx| {
            let mut ed = EditorState::new(window, cx)
                .language("sql")
                .placeholder("-- Write SQL here. Ctrl+Enter runs statement under caret, F5 runs all.")
                .default_value(initial_sql);
            ed.lsp_mut().completion_provider =
                Some(Rc::new(SqlCompletionProvider::new(provider_ref, post_insert_ref)));
            ed
        });

        let table_state = cx.new(|cx| {
            TableState::new(ResultTableDelegate::empty(), window, cx)
                .col_resizable(true)
                .col_movable(true)
                .sortable(false)
        });

        let _editor_sub = cx.subscribe(&editor_state, |this, entity, event: &InputEvent, cx| {
            if matches!(event, InputEvent::Change) && !this.suppress_editor_sync {
                let text = entity.read(cx).value().to_string();
                if let Some(tab) = this
                    .state
                    .active_space_mut()
                    .and_then(|s| s.active_tab_mut())
                {
                    tab.content = text;
                    tab.last_accessed_at = now_rfc3339();
                    this.persist();
                }
            }
        });

        let _editor_edits_sub = cx.subscribe_in(
            &editor_state,
            window,
            |this, entity, event: &InputEditsEvent, window, cx| {
                if this.suppress_editor_sync {
                    return;
                }
                let Some((edit, rope)) = event.edits().last() else {
                    return;
                };
                if edit.new_end_byte <= edit.start_byte || edit.new_end_byte > rope.len() {
                    if let Ok(mut guard) = this.pending_post_insert.write() {
                        guard.clear();
                    }
                    return;
                }
                let inserted = rope
                    .slice(edit.start_byte..edit.new_end_byte)
                    .to_string();
                let action = this.pending_post_insert.write().ok().and_then(|mut guard| {
                    let act = guard.remove(&inserted);
                    guard.clear();
                    act
                });
                let Some(action) = action else {
                    return;
                };

                let base = edit.start_byte;
                let range = if let Some((sel_start, sel_len)) =
                    action.selection.filter(|(_, len)| *len > 0)
                {
                    (base + sel_start)..(base + sel_start + sel_len)
                } else {
                    let pos = base + action.caret_offset;
                    pos..pos
                };
                let reopen = action.reopen_completion;
                let editor_entity = entity.clone();

                window.defer(cx, move |window, cx| {
                    editor_entity.update(cx, |ed, cx| {
                        let max_len = ed.text().len();
                        let clamped = range.start.min(max_len)..range.end.min(max_len);
                        ed.set_selected_range(clamped, cx);
                        if reopen {
                            ed.show_completions(window, cx);
                        }
                    });
                });
            },
        );

        let schema_filter_input = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Filter tables & columns…")
        });
        let _schema_filter_sub =
            cx.subscribe(&schema_filter_input, |this, entity, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    let next = entity.read(cx).value().trim().to_ascii_lowercase();
                    if this.schema_filter_query != next {
                        this.schema_filter_query = next;
                        this.rebuild_schema_rows();
                        cx.notify();
                    }
                }
            });

        let app_icon = Arc::new(Image::from_bytes(ImageFormat::Png, APP_ICON_PNG.to_vec()));

        let mut view = Self {
            focus_handle: cx.focus_handle(),
            catalog,
            store,
            sessions,
            state,
            space_runtime: HashMap::new(),
            tab_runtime: HashMap::new(),
            editor_state,
            completion_snapshot,
            pending_post_insert,
            table_state,
            _editor_sub,
            _editor_edits_sub,
            suppress_editor_sync: false,
            snippets,
            schema_filter_input,
            _schema_filter_sub,
            schema_filter_query: String::new(),
            schema_rows: Vec::new(),
            schema_matched_tables: 0,
            schema_scroll_handle: UniformListScrollHandle::new(),
            app_icon,
            managing_workspaces: false,
            managing_settings: false,
            settings_page: None,
            history_page: None,
            footer_space_offset: 0,
            workspace_dropdown_open: false,
            db_dropdown_open: false,
            expanded_tables: HashSet::new(),
            renaming_tab_id: None,
            rename_input: None,
            _rename_sub: None,
            renaming_group_id: None,
            rename_group_input: None,
            _rename_group_sub: None,
            renaming_result: None,
            rename_result_input: None,
            _rename_result_sub: None,
            rail_context_menu: None,
            last_grouping_space_id: None,
            last_grouping_ids: Vec::new(),
            db_palette_open: false,
            db_palette_input: None,
            _db_palette_sub: None,
            modal: None,
            copied_toast: false,
        };

        view.ensure_active_space_in_footer_window();
        view.refresh_completion_snapshot();
        view.rebuild_schema_rows();
        // Auto-connect the offline Demo workspace on first open for an instant "try it" experience,
        // while real SQL Server workspaces stay disconnected until the user clicks Connect.
        if let Some(space) = view.state.active_space() {
            if space.profile.provider_id.eq_ignore_ascii_case("demo") {
                let id = space.id.clone();
                view.connect_space(id, cx);
            }
        }
        view
    }

    fn persist(&self) {
        let _ = self.store.save(&self.state, &self.catalog);
    }

    fn active_space_id(&self) -> Option<String> {
        self.state.active_space().map(|s| s.id.clone())
    }

    fn active_tab_id(&self) -> Option<String> {
        self.state
            .active_space()
            .and_then(|s| s.active_tab())
            .map(|t| t.id.clone())
    }

    fn active_tab_database(&self) -> Option<String> {
        self.state
            .active_space()
            .and_then(SpaceState::active_tab_database)
    }

    /// Synchronizes workspace schema, Schema Inspector, and autocomplete tables with the
    /// currently active tab's bound database. Uses the per-database `schema_cache` when
    /// available and guards async fetches against stale tab/database switches.
    fn sync_database_for_active_tab(&mut self, cx: &mut Context<Self>) {
        let Some((space_id, target_db)) = self
            .state
            .active_space()
            .map(|s| (s.id.clone(), s.active_tab_database()))
        else {
            self.refresh_completion_snapshot();
            self.rebuild_schema_rows();
            return;
        };

        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            if let Some(ref db) = target_db {
                space.active_database = Some(db.clone());
            }
        }

        let is_connected = self
            .space_runtime
            .get(&space_id)
            .is_some_and(|rt| rt.connection_state() == ConnectionState::Connected);
        if !is_connected {
            self.refresh_completion_snapshot();
            self.rebuild_schema_rows();
            return;
        }

        let Some(db_name) = target_db else {
            self.refresh_completion_snapshot();
            self.rebuild_schema_rows();
            return;
        };

        if let Some(cached) = self
            .space_runtime
            .get(&space_id)
            .and_then(|rt| rt.cached_schema(Some(&db_name)))
        {
            if let Some(rt) = self.space_runtime.get_mut(&space_id) {
                if !Arc::ptr_eq(&rt.schema, &cached) {
                    rt.schema = cached;
                    self.expanded_tables.clear();
                }
            }
            self.refresh_completion_snapshot();
            self.rebuild_schema_rows();
            return;
        }

        self.expanded_tables.clear();
        if let Some(rt) = self.space_runtime.get_mut(&space_id) {
            rt.schema = Arc::default();
        }
        self.refresh_completion_snapshot();
        self.rebuild_schema_rows();

        let rx = self
            .sessions
            .load_schema(space_id.clone(), Some(db_name.clone()));
        cx.spawn(async move |this, cx| {
            if let Ok(Ok(schema)) = rx.await {
                let _ = this.update(cx, |this, cx| {
                    let schema_arc = Arc::new(schema);
                    let still_matches = this.active_space_id().as_deref() == Some(space_id.as_str())
                        && this
                            .active_tab_database()
                            .as_deref()
                            .is_some_and(|cur| cur.eq_ignore_ascii_case(&db_name));
                    {
                        let rt = this.space_runtime.entry(space_id.clone()).or_default();
                        if rt.connection_state() != ConnectionState::Connected {
                            return;
                        }
                        rt.cache_schema(Some(&db_name), Arc::clone(&schema_arc));
                        if still_matches {
                            rt.schema = schema_arc;
                        }
                    }
                    if still_matches {
                        this.expanded_tables.clear();
                        this.refresh_completion_snapshot();
                        this.rebuild_schema_rows();
                        cx.notify();
                    }
                });
            }
        })
        .detach();
    }

    fn refresh_completion_snapshot(&self) {
        let Some(space) = self.state.active_space() else {
            return;
        };
        let (tables, databases) = self
            .space_runtime
            .get(&space.id)
            .map(|rt| (Arc::clone(&rt.schema), Arc::clone(&rt.databases)))
            .unwrap_or_default();
        let provider = self.catalog.find(&space.profile.provider_id);
        let dialect = provider.as_ref().map(|p| p.dialect()).unwrap_or("mssql");
        let language = provider
            .map(|p| p.editor_language())
            .unwrap_or_else(|| Arc::new(SqlLanguage::tsql()));
        let active_snippets = Arc::new(snippets_for_dialect(&self.snippets, dialect));

        if let Ok(mut guard) = self.completion_snapshot.write() {
            guard.tables = tables;
            guard.databases = databases;
            guard.language = language;
            guard.snippets = active_snippets;
        }
    }

    /// Rebuilds the flat virtualized list backing the Schema Peek drawer.
    /// Called only when the active schema, expanded set, filter query, or drawer visibility changes.
    fn rebuild_schema_rows(&mut self) {
        self.schema_rows.clear();
        self.schema_matched_tables = 0;

        if !self.state.schema_pane_open {
            self.schema_rows.shrink_to_fit();
            return;
        }

        let Some(space_id) = self.active_space_id() else {
            return;
        };
        let Some(rt) = self.space_runtime.get(&space_id) else {
            return;
        };

        let query = self.schema_filter_query.as_str();
        let filtering = !query.is_empty();

        self.schema_rows.reserve(rt.schema.len().min(512));

        for table in rt.schema.iter() {
            let qualified = format!("{}.{}", table.schema, table.name);
            let table_matches = !filtering
                || table.name.to_ascii_lowercase().contains(query)
                || table.schema.to_ascii_lowercase().contains(query)
                || qualified.to_ascii_lowercase().contains(query);

            let matched_cols: Vec<usize> = if filtering && !table_matches {
                table
                    .columns
                    .iter()
                    .enumerate()
                    .filter(|(_, c)| c.name.to_ascii_lowercase().contains(query))
                    .map(|(i, _)| i)
                    .collect()
            } else {
                Vec::new()
            };

            if !table_matches && matched_cols.is_empty() {
                continue;
            }

            self.schema_matched_tables += 1;

            let manually_expanded = self.expanded_tables.contains(&qualified);
            let auto_expanded = filtering && !table_matches && !matched_cols.is_empty();
            let expanded = manually_expanded || auto_expanded;

            self.schema_rows.push(SchemaRow::Table {
                key: SharedString::from(qualified),
                is_view: table.kind == TableKind::View,
                expanded,
                col_count: SharedString::from(table.columns.len().to_string()),
            });

            if expanded {
                let push_col = |rows: &mut Vec<SchemaRow>, col: &larik_data::ColumnSchema| {
                    let is_pk = table
                        .primary_key
                        .iter()
                        .any(|pk| pk.eq_ignore_ascii_case(&col.name));
                    let is_fk = table
                        .foreign_keys
                        .iter()
                        .any(|fk| fk.column.eq_ignore_ascii_case(&col.name));
                    rows.push(SchemaRow::Column {
                        name: SharedString::from(col.name.clone()),
                        data_type: col.data_type.as_ref().map(|dt| SharedString::from(dt.clone())),
                        is_pk,
                        is_fk,
                    });
                };

                if auto_expanded && !manually_expanded {
                    for col_idx in matched_cols {
                        if let Some(col) = table.columns.get(col_idx) {
                            push_col(&mut self.schema_rows, col);
                        }
                    }
                } else {
                    for col in &table.columns {
                        push_col(&mut self.schema_rows, col);
                    }
                }
            }
        }
    }

    fn toggle_schema_table(&mut self, key: &str, cx: &mut Context<Self>) {
        if !self.expanded_tables.remove(key) {
            self.expanded_tables.insert(key.to_string());
        }
        self.rebuild_schema_rows();
        cx.notify();
    }

    fn clear_schema_filter(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.schema_filter_query.is_empty() {
            return;
        }
        self.schema_filter_query.clear();
        self.schema_filter_input
            .update(cx, |input, cx| input.set_value("", window, cx));
        self.rebuild_schema_rows();
        cx.notify();
    }

    fn sync_editor_from_active_tab(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let content = self
            .state
            .active_space()
            .and_then(|s| s.active_tab())
            .map(|t| t.content.clone())
            .unwrap_or_default();

        self.suppress_editor_sync = true;
        self.editor_state.update(cx, |ed, cx| {
            ed.set_value(content, window, cx);
        });
        self.suppress_editor_sync = false;
        self.sync_table_from_active_tab(cx);
    }

    fn sync_table_from_active_tab(&mut self, cx: &mut Context<Self>) {
        let active_result: Option<Arc<QueryResult>> = self.active_tab_id().and_then(|tab_id| {
            let rt = self.tab_runtime.get(&tab_id)?;
            if rt.show_messages {
                return None;
            }
            rt.active_result().map(|item| item.result.clone())
        });

        self.table_state.update(cx, |table, cx| {
            table.delegate_mut().set_result(active_result);
            table.refresh(cx);
        });
    }

    fn ensure_active_space_in_footer_window(&mut self) {
        let active_id = self.state.active_space_id.as_deref();
        let visible_ids: Vec<&str> = self
            .state
            .spaces
            .iter()
            .filter(|s| !s.is_hidden || active_id == Some(s.id.as_str()))
            .map(|s| s.id.as_str())
            .collect();
        let max_offset = visible_ids.len().saturating_sub(FOOTER_MAX_WORKSPACES);
        self.footer_space_offset = self.footer_space_offset.min(max_offset);

        if let Some(active) = active_id {
            if let Some(idx) = visible_ids.iter().position(|id| *id == active) {
                if idx < self.footer_space_offset {
                    self.footer_space_offset = idx;
                } else if idx >= self.footer_space_offset + FOOTER_MAX_WORKSPACES {
                    self.footer_space_offset = (idx + 1).saturating_sub(FOOTER_MAX_WORKSPACES);
                }
            }
        }
    }

    fn switch_space(&mut self, space_id: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.active_space_id.as_deref() == Some(space_id.as_str()) {
            self.workspace_dropdown_open = false;
            self.rail_context_menu = None;
            self.ensure_active_space_in_footer_window();
            cx.notify();
            return;
        }
        self.state.active_space_id = Some(space_id);
        self.ensure_active_space_in_footer_window();
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.rail_context_menu = None;
        self.expanded_tables.clear();
        self.close_db_palette(cx);
        self.cancel_rename(cx);
        self.cancel_group_rename(cx);
        self.cancel_result_rename(cx);
        self.sync_editor_from_active_tab(window, cx);
        self.sync_database_for_active_tab(cx);
        if self.history_page.is_some() {
            self.refresh_history_runs(cx);
        }
        self.persist();
        cx.notify();
    }

    fn create_workspace(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let idx = self.state.spaces.len();
        let (_, color) = SPACE_COLORS[idx % SPACE_COLORS.len()];
        let default_provider = self
            .catalog
            .find("mssql")
            .or_else(|| self.catalog.all().first().cloned());

        let profile = default_provider
            .as_ref()
            .map(|p| p.default_profile())
            .unwrap_or_else(|| larik_data::ConnectionProfile::new("demo"));

        let space_id = Uuid::new_v4().to_string();
        let tab_id = Uuid::new_v4().to_string();
        let tab_title = self.state.next_query_title();
        let name = format!("Workspace {}", idx + 1);

        self.state.spaces.push(SpaceState {
            id: space_id.clone(),
            name,
            color: color.to_string(),
            icon: None,
            profile,
            active_database: None,
            active_tab_id: Some(tab_id.clone()),
            tabs: vec![TabState {
                id: tab_id,
                title: tab_title,
                database: None,
                content: String::new(),
                is_pinned: false,
                group_id: None,
                last_accessed_at: now_rfc3339(),
            }],
            groups: Vec::new(),
            is_hidden: false,
        });
        self.state.active_space_id = Some(space_id.clone());
        self.ensure_active_space_in_footer_window();
        self.rail_context_menu = None;
        self.expanded_tables.clear();
        self.history_page = None;
        self.sync_editor_from_active_tab(window, cx);
        self.sync_database_for_active_tab(cx);
        self.persist();
        self.open_connection_modal(space_id, window, cx);
        cx.notify();
    }

    fn open_manage_workspaces(&mut self, cx: &mut Context<Self>) {
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.rail_context_menu = None;
        self.history_page = None;
        self.close_db_palette(cx);
        self.cancel_rename(cx);
        self.cancel_group_rename(cx);
        self.cancel_result_rename(cx);
        self.managing_settings = false;
        self.settings_page = None;
        self.managing_workspaces = true;
        cx.notify();
    }

    fn close_manage_workspaces(&mut self, cx: &mut Context<Self>) {
        if !self.managing_workspaces {
            return;
        }
        self.managing_workspaces = false;
        cx.notify();
    }

    pub(crate) fn settings_snippets(&self) -> Option<&SnippetsPageState> {
        self.settings_page.as_ref().map(|p| &p.snippets)
    }

    pub(crate) fn settings_snippets_mut(&mut self) -> Option<&mut SnippetsPageState> {
        self.settings_page.as_mut().map(|p| &mut p.snippets)
    }

    fn open_settings_page(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.rail_context_menu = None;
        self.history_page = None;
        self.close_db_palette(cx);
        self.cancel_rename(cx);
        self.cancel_group_rename(cx);
        self.cancel_result_rename(cx);
        self.managing_workspaces = false;
        if let Ok(loaded) = self.store.load_snippets() {
            self.snippets = loaded;
        }
        self.settings_page = Some(SettingsPageState::new(self.snippets.clone(), window, cx));
        self.managing_settings = true;
        cx.notify();
    }

    pub(crate) fn close_settings_page(&mut self, cx: &mut Context<Self>) {
        if !self.managing_settings {
            return;
        }
        self.managing_settings = false;
        self.settings_page = None;
        cx.notify();
    }

    pub(crate) fn toggle_active_snippet_enabled(&mut self, enabled: bool, cx: &mut Context<Self>) {
        let editing_id = {
            let Some(page) = self.settings_snippets_mut() else {
                return;
            };
            page.editing_enabled = enabled;
            if page.creating_new {
                cx.notify();
                return;
            }
            page.editing_id.clone()
        };
        let Some(id) = editing_id else {
            cx.notify();
            return;
        };
        if let Some(rec) = self.snippets.iter_mut().find(|s| s.id == id) {
            rec.is_enabled = enabled;
            if let Err(err) = self.store.save_snippet(rec) {
                if let Some(page) = self.settings_snippets_mut() {
                    page.error_message = Some(format!("Failed to update snippet: {err}"));
                }
                cx.notify();
                return;
            }
        }
        if let Some(page) = self.settings_snippets_mut() {
            if let Some(rec) = page.snippets.iter_mut().find(|s| s.id == id) {
                rec.is_enabled = enabled;
            }
        }
        self.refresh_completion_snapshot();
        cx.notify();
    }

    pub(crate) fn save_active_snippet(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let validated = {
            let Some(page) = self.settings_snippets() else {
                return;
            };
            page.validate_and_collect(cx)
        };
        let record = match validated {
            Ok(rec) => rec,
            Err(msg) => {
                if let Some(page) = self.settings_snippets_mut() {
                    page.error_message = Some(msg);
                }
                cx.notify();
                return;
            }
        };
        if let Err(err) = self.store.save_snippet(&record) {
            if let Some(page) = self.settings_snippets_mut() {
                page.error_message = Some(format!("Failed to save snippet: {err}"));
            }
            cx.notify();
            return;
        }

        let saved_id = record.id.clone();
        if let Some(existing) = self.snippets.iter_mut().find(|s| s.id == saved_id) {
            *existing = record;
        } else {
            self.snippets.push(record);
        }
        sort_snippets(&mut self.snippets);

        let updated = self.snippets.clone();
        if let Some(page) = self.settings_snippets_mut() {
            page.snippets = updated;
            page.select_snippet(&saved_id, window, cx);
        }
        self.refresh_completion_snapshot();
        cx.notify();
    }

    pub(crate) fn reset_active_snippet(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some((id, is_enabled)) = self
            .settings_snippets()
            .and_then(|p| p.editing_id.clone().map(|id| (id, p.editing_enabled)))
        else {
            return;
        };
        let Some(existing) = self.snippets.iter().find(|s| s.id == id) else {
            return;
        };
        let Some(def) = builtin_default_for(existing) else {
            return;
        };
        let mut default_rec = builtin_to_record(&def, is_enabled);
        default_rec.id = id.clone();

        if let Err(err) = self.store.save_snippet(&default_rec) {
            if let Some(page) = self.settings_snippets_mut() {
                page.error_message = Some(format!("Failed to reset snippet: {err}"));
            }
            cx.notify();
            return;
        }
        if let Some(existing) = self.snippets.iter_mut().find(|s| s.id == id) {
            *existing = default_rec;
        } else {
            self.snippets.push(default_rec);
        }
        sort_snippets(&mut self.snippets);

        let updated = self.snippets.clone();
        if let Some(page) = self.settings_snippets_mut() {
            page.snippets = updated;
            page.select_snippet(&id, window, cx);
        }
        self.refresh_completion_snapshot();
        cx.notify();
    }

    pub(crate) fn delete_active_snippet(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(id) = self
            .settings_snippets()
            .and_then(|p| p.editing_id.clone())
        else {
            return;
        };
        if let Err(err) = self.store.delete_snippet(&id) {
            if let Some(page) = self.settings_snippets_mut() {
                page.error_message = Some(format!("Failed to delete snippet: {err}"));
            }
            cx.notify();
            return;
        }
        self.snippets.retain(|s| s.id != id);
        let updated = self.snippets.clone();
        let next_id = self.snippets.first().map(|s| s.id.clone());
        if let Some(page) = self.settings_snippets_mut() {
            page.snippets = updated;
            page.confirming_delete = false;
            page.error_message = None;
            if let Some(next) = next_id {
                page.select_snippet(&next, window, cx);
            } else {
                page.selected_id = None;
                page.editing_id = None;
                page.creating_new = false;
            }
        }
        self.refresh_completion_snapshot();
        cx.notify();
    }

    fn toggle_hide_workspace(&mut self, space_id: String, cx: &mut Context<Self>) {
        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            space.is_hidden = !space.is_hidden;
            self.ensure_active_space_in_footer_window();
            self.persist();
            cx.notify();
        }
    }

    fn move_workspace(&mut self, space_id: String, delta: isize, cx: &mut Context<Self>) {
        let Some(pos) = self.state.spaces.iter().position(|s| s.id == space_id) else {
            return;
        };
        let target = pos as isize + delta;
        if target < 0 || target >= self.state.spaces.len() as isize {
            return;
        }
        self.state.spaces.swap(pos, target as usize);
        self.ensure_active_space_in_footer_window();
        self.persist();
        cx.notify();
    }

    fn delete_workspace(
        &mut self,
        space_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.state.spaces.len() <= 1 {
            return;
        }
        let Some(pos) = self.state.spaces.iter().position(|s| s.id == space_id) else {
            return;
        };

        let removed = self.state.spaces.remove(pos);
        let tab_ids: Vec<String> = removed.tabs.iter().map(|t| t.id.clone()).collect();
        for tid in &tab_ids {
            self.tab_runtime.remove(tid);
        }
        self.sessions.disconnect_space(removed.id.clone(), tab_ids);
        self.space_runtime.remove(&removed.id);
        let _ = self.store.delete_space_secrets(&removed, &self.catalog);
        // Archived tabs from this workspace survive deletion intentionally.

        if self.state.active_space_id.as_deref() == Some(removed.id.as_str()) {
            let next_id = self
                .state
                .spaces
                .iter()
                .find(|s| !s.is_hidden)
                .or_else(|| self.state.spaces.get(pos.saturating_sub(1)))
                .or_else(|| self.state.spaces.first())
                .map(|s| s.id.clone());
            self.state.active_space_id = next_id;
            self.expanded_tables.clear();
            self.rail_context_menu = None;
            self.sync_editor_from_active_tab(window, cx);
            self.sync_database_for_active_tab(cx);
        }

        self.ensure_active_space_in_footer_window();
        self.modal = None;
        self.persist();
        cx.notify();
    }

    fn connect_space(&mut self, space_id: String, cx: &mut Context<Self>) {
        let Some(space) = self.state.spaces.iter().find(|s| s.id == space_id) else {
            return;
        };
        let profile = space.profile.clone();
        let preferred_db = space.active_tab_database();

        let rt = self.space_runtime.entry(space_id.clone()).or_default();
        rt.state = Some(ConnectionState::Connecting);
        rt.last_error = None;
        cx.notify();

        let rx = self
            .sessions
            .connect_space(space_id.clone(), profile, preferred_db);

        cx.spawn(async move |this, cx| {
            let result = rx.await;
            let _ = this.update(cx, |this, cx| {
                match result {
                    Ok(Ok(outcome)) => {
                        let initial_db = outcome.initial_database.clone();
                        let schema_arc = Arc::new(outcome.schema);
                        let rt = this.space_runtime.entry(space_id.clone()).or_default();
                        rt.state = Some(ConnectionState::Connected);
                        rt.server_version = outcome.server_version.map(SharedString::from);
                        rt.databases = Arc::new(outcome.databases);
                        rt.schema = Arc::clone(&schema_arc);
                        rt.schema_cache.clear();
                        rt.cache_schema(initial_db.as_deref(), schema_arc);
                        rt.last_error = None;

                        if let Some(ref db) = initial_db {
                            if let Some(space) =
                                this.state.spaces.iter_mut().find(|s| s.id == space_id)
                            {
                                for tab in &mut space.tabs {
                                    if tab.database.is_none() {
                                        tab.database = Some(db.clone());
                                    }
                                }
                                space.active_database = space
                                    .active_tab_database()
                                    .or_else(|| Some(db.clone()));
                            }
                            this.persist();
                        }

                        let active_tab_db = this
                            .state
                            .spaces
                            .iter()
                            .find(|s| s.id == space_id)
                            .and_then(SpaceState::active_tab_database);
                        let is_active_space =
                            this.active_space_id().as_deref() == Some(space_id.as_str());
                        let needs_tab_resync = is_active_space
                            && match (active_tab_db.as_deref(), initial_db.as_deref()) {
                                (Some(a), Some(b)) => !a.eq_ignore_ascii_case(b),
                                (Some(_), None) => true,
                                _ => false,
                            };

                        if needs_tab_resync {
                            this.sync_database_for_active_tab(cx);
                        } else {
                            this.refresh_completion_snapshot();
                            this.rebuild_schema_rows();
                        }
                    }
                    Ok(Err(err)) => {
                        let rt = this.space_runtime.entry(space_id.clone()).or_default();
                        rt.state = Some(ConnectionState::Faulted);
                        rt.last_error = Some(err.to_string().into());
                    }
                    Err(_) => {
                        let rt = this.space_runtime.entry(space_id.clone()).or_default();
                        rt.state = Some(ConnectionState::Faulted);
                        rt.last_error = Some("Connection task was cancelled.".into());
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }

    fn disconnect_space(&mut self, space_id: String, cx: &mut Context<Self>) {
        let tab_ids = self
            .state
            .spaces
            .iter()
            .find(|s| s.id == space_id)
            .map(|s| s.tabs.iter().map(|t| t.id.clone()).collect())
            .unwrap_or_default();

        self.sessions.disconnect_space(space_id.clone(), tab_ids);
        let rt = self.space_runtime.entry(space_id).or_default();
        rt.state = Some(ConnectionState::Disconnected);
        rt.databases = Arc::default();
        rt.schema = Arc::default();
        rt.schema_cache.clear();
        rt.server_version = None;
        rt.last_error = None;
        self.expanded_tables.clear();
        self.db_dropdown_open = false;
        self.close_db_palette(cx);
        self.refresh_completion_snapshot();
        self.rebuild_schema_rows();
        cx.notify();
    }

    /// Switches the database ONLY for the currently active tab (other tabs keep their own bound DB).
    fn select_database(&mut self, database: String, window: &mut Window, cx: &mut Context<Self>) {
        let Some(space_id) = self.active_space_id() else {
            return;
        };
        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            if let Some(tab) = space.active_tab_mut() {
                tab.database = Some(database.clone());
                tab.last_accessed_at = now_rfc3339();
            }
            space.active_database = Some(database);
        }
        self.db_dropdown_open = false;
        self.rail_context_menu = None;
        self.sync_database_for_active_tab(cx);
        self.editor_state.update(cx, |ed, cx| ed.focus(window, cx));
        self.persist();
        cx.notify();
    }

    /// Trigger the Arc-style "+ New query" flow: when connected with multiple accessible databases,
    /// open a searchable New-Tab Database Palette; otherwise create a tab immediately.
    fn prompt_new_tab(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        let accessible_count = self
            .active_space_id()
            .and_then(|id| self.space_runtime.get(&id))
            .filter(|rt| rt.connection_state() == ConnectionState::Connected)
            .map(|rt| rt.databases.iter().filter(|d| d.is_accessible).count())
            .unwrap_or(0);

        if accessible_count > 1 {
            self.open_db_palette(window, cx);
        } else {
            let default_db = self.active_tab_database();
            self.new_tab_for_database(default_db, window, cx);
        }
    }

    fn open_db_palette(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.db_dropdown_open = false;
        self.rail_context_menu = None;
        self.cancel_rename(cx);
        self.cancel_group_rename(cx);

        let input = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Filter databases… (Enter to open)")
        });
        let sub = cx.subscribe_in(&input, window, |this, entity, event: &InputEvent, window, cx| {
            match event {
                InputEvent::Change => {
                    cx.notify();
                }
                InputEvent::PressEnter { .. } => {
                    let query = entity.read(cx).value().trim().to_ascii_lowercase();
                    let picked = this.first_matching_accessible_db(&query);
                    if let Some(db) = picked {
                        this.close_db_palette(cx);
                        this.new_tab_for_database(Some(db), window, cx);
                    }
                }
                _ => {}
            }
        });
        input.update(cx, |s, cx| s.focus(window, cx));
        self.db_palette_open = true;
        self.db_palette_input = Some(input);
        self._db_palette_sub = Some(sub);
        cx.notify();
    }

    fn close_db_palette(&mut self, cx: &mut Context<Self>) {
        if !self.db_palette_open {
            return;
        }
        self.db_palette_open = false;
        self.db_palette_input = None;
        self._db_palette_sub = None;
        cx.notify();
    }

    fn first_matching_accessible_db(&self, query_lower: &str) -> Option<String> {
        let space_id = self.active_space_id()?;
        let rt = self.space_runtime.get(&space_id)?;
        rt.databases
            .iter()
            .filter(|d| d.is_accessible)
            .find(|d| query_lower.is_empty() || d.name.to_ascii_lowercase().contains(query_lower))
            .map(|d| d.name.clone())
    }

    fn new_tab_for_database(
        &mut self,
        database: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.new_tab_with_content(database, String::new(), window, cx);
    }

    fn new_tab_with_content(
        &mut self,
        database: Option<String>,
        content: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let title = self.state.next_query_title();
        let tab_id = Uuid::new_v4().to_string();

        if let Some(space) = self.state.active_space_mut() {
            let bound_db = database.or_else(|| space.active_tab_database());
            space.insert_new_tab(TabState {
                id: tab_id.clone(),
                title,
                database: bound_db,
                content,
                is_pinned: false,
                group_id: None,
                last_accessed_at: now_rfc3339(),
            });
            space.active_tab_id = Some(tab_id);
        }

        self.history_page = None;
        self.rail_context_menu = None;
        self.cancel_result_rename(cx);
        self.sync_editor_from_active_tab(window, cx);
        self.sync_database_for_active_tab(cx);
        self.editor_state.update(cx, |ed, cx| ed.focus(window, cx));
        self.persist();
        cx.notify();
    }

    fn switch_tab(&mut self, tab_id: String, window: &mut Window, cx: &mut Context<Self>) {
        let was_history = self.history_page.take().is_some();
        self.rail_context_menu = None;
        if self.active_tab_id().as_deref() == Some(tab_id.as_str()) {
            // Ensure collapsed group expands if user re-selects active tab.
            if let Some(space) = self.state.active_space_mut() {
                if let Some(gid) = space
                    .tabs
                    .iter()
                    .find(|t| t.id == tab_id)
                    .and_then(|t| t.group_id.clone())
                {
                    if let Some(group) = space.tab_group_mut(&gid) {
                        if !group.is_expanded {
                            group.is_expanded = true;
                            self.persist();
                            cx.notify();
                        }
                    }
                }
            }
            if was_history {
                cx.notify();
            }
            return;
        }
        if let Some(space) = self.state.active_space_mut() {
            space.active_tab_id = Some(tab_id.clone());
            let mut expand_group_id = None;
            if let Some(tab) = space.tabs.iter_mut().find(|t| t.id == tab_id) {
                tab.last_accessed_at = now_rfc3339();
                expand_group_id = tab.group_id.clone();
            }
            if let Some(gid) = expand_group_id {
                if let Some(group) = space.tab_group_mut(&gid) {
                    group.is_expanded = true;
                }
            }
        }
        self.cancel_rename(cx);
        self.cancel_group_rename(cx);
        self.cancel_result_rename(cx);
        self.sync_editor_from_active_tab(window, cx);
        self.sync_database_for_active_tab(cx);
        self.persist();
        cx.notify();
    }

    fn toggle_pin_tab(&mut self, tab_id: String, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        if let Some(space) = self.state.active_space_mut() {
            if space.toggle_pin_tab(&tab_id) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn toggle_group_expanded(&mut self, group_id: String, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        if let Some(space) = self.state.active_space_mut() {
            if space.toggle_group_expanded(&group_id) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn toggle_group_pin(&mut self, group_id: String, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        if let Some(space) = self.state.active_space_mut() {
            if space.toggle_group_pin(&group_id) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn new_group_from_tab(&mut self, tab_id: String, window: &mut Window, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        let created_id = self
            .state
            .active_space_mut()
            .and_then(|space| space.new_group_from_tab(&tab_id));
        if let Some(group_id) = created_id {
            self.persist();
            self.start_rename_group(group_id, window, cx);
        }
    }

    fn move_tab_to_group(
        &mut self,
        tab_id: String,
        target_group_id: Option<String>,
        loose_pinned: Option<bool>,
        cx: &mut Context<Self>,
    ) {
        self.rail_context_menu = None;
        if let Some(space) = self.state.active_space_mut() {
            if space.move_tab_to_group(&tab_id, target_group_id.as_deref(), loose_pinned) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn ungroup(&mut self, group_id: String, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        if self.renaming_group_id.as_deref() == Some(group_id.as_str()) {
            self.cancel_group_rename(cx);
        }
        if let Some(space) = self.state.active_space_mut() {
            if space.ungroup(&group_id) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn group_tabs_by_database(&mut self, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        let Some(space_id) = self.active_space_id() else {
            return;
        };
        let created = self
            .state
            .spaces
            .iter_mut()
            .find(|s| s.id == space_id)
            .map(SpaceState::group_tabs_by_database)
            .unwrap_or_default();
        if !created.is_empty() {
            self.last_grouping_space_id = Some(space_id);
            self.last_grouping_ids = created;
            self.persist();
            cx.notify();
        }
    }

    fn undo_grouping(&mut self, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        let Some(space_id) = self.last_grouping_space_id.take() else {
            self.last_grouping_ids.clear();
            return;
        };
        let group_ids = std::mem::take(&mut self.last_grouping_ids);
        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            if space.undo_grouping(&group_ids) {
                self.persist();
            }
        }
        cx.notify();
    }

    fn drop_tab_on_group(&mut self, dragged_id: &str, group_id: &str, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        if let Some(space) = self.state.active_space_mut() {
            if space.drop_tab_on_group(dragged_id, group_id) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn drop_tab_on_tab(&mut self, dragged_id: &str, target_id: &str, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        if let Some(space) = self.state.active_space_mut() {
            if space.drop_tab_on_tab(dragged_id, target_id) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn drop_tab_to_loose(
        &mut self,
        dragged_id: &str,
        pinned: bool,
        at_top: bool,
        cx: &mut Context<Self>,
    ) {
        self.rail_context_menu = None;
        if let Some(space) = self.state.active_space_mut() {
            if space.drop_tab_to_loose(dragged_id, pinned, at_top) {
                self.persist();
                cx.notify();
            }
        }
    }

    fn open_tab_context_menu(
        &mut self,
        tab_id: String,
        position: Point<Pixels>,
        cx: &mut Context<Self>,
    ) {
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.rail_context_menu = Some(RailContextMenu {
            target: RailContextMenuTarget::Tab { tab_id },
            position,
        });
        cx.notify();
    }

    fn open_group_context_menu(
        &mut self,
        group_id: String,
        position: Point<Pixels>,
        cx: &mut Context<Self>,
    ) {
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.rail_context_menu = Some(RailContextMenu {
            target: RailContextMenuTarget::Group { group_id },
            position,
        });
        cx.notify();
    }

    fn close_rail_context_menu(&mut self, cx: &mut Context<Self>) {
        if self.rail_context_menu.take().is_some() {
            cx.notify();
        }
    }

    fn cycle_tab(&mut self, delta: isize, window: &mut Window, cx: &mut Context<Self>) {
        let next_id = {
            let Some(space) = self.state.active_space() else {
                return;
            };
            if space.tabs.len() <= 1 {
                return;
            }
            let cur_idx = space
                .active_tab_id
                .as_deref()
                .and_then(|id| space.tabs.iter().position(|t| t.id == id))
                .unwrap_or(0);
            let len = space.tabs.len() as isize;
            let target = (cur_idx as isize + delta).rem_euclid(len) as usize;
            space.tabs[target].id.clone()
        };
        self.switch_tab(next_id, window, cx);
    }

    fn archive_tab(&mut self, tab_id: String, window: &mut Window, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        let Some(space_id) = self.active_space_id() else {
            return;
        };
        let (removed, group_name, space_name, space_color) = {
            let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) else {
                return;
            };
            let Some(pos) = space.tabs.iter().position(|t| t.id == tab_id) else {
                return;
            };
            let tab = space.tabs.remove(pos);
            let group_name = tab
                .group_id
                .as_deref()
                .and_then(|gid| space.tab_group(gid))
                .map(|g| g.name.clone());
            if space.active_tab_id.as_deref() == Some(tab.id.as_str()) {
                space.active_tab_id = space
                    .tabs
                    .get(pos.saturating_sub(1))
                    .or_else(|| space.tabs.first())
                    .map(|t| t.id.clone());
            }
            space.prune_empty_groups();
            (tab, group_name, space.name.clone(), space.color.clone())
        };

        self.sessions.close_tab_session(removed.id.clone());
        self.tab_runtime.remove(&removed.id);
        self.cancel_result_rename(cx);

        // Non-destructive close: file into archive if it has content.
        if !removed.content.trim().is_empty() {
            self.state.archived_tabs.insert(
                0,
                ArchivedTabState {
                    id: removed.id,
                    space_id: space_id.clone(),
                    space_name: Some(space_name),
                    color: Some(space_color),
                    title: removed.title,
                    database: removed.database,
                    content: removed.content,
                    is_pinned: removed.is_pinned,
                    group_name,
                    archived_at: now_rfc3339(),
                },
            );
            self.state.archived_tabs.truncate(200);
        }

        // Keep at least one open tab in the workspace.
        let needs_tab = self
            .state
            .spaces
            .iter()
            .find(|s| s.id == space_id)
            .is_some_and(|s| s.tabs.is_empty());
        if needs_tab {
            let default_db = self
                .state
                .spaces
                .iter()
                .find(|s| s.id == space_id)
                .and_then(|s| s.active_database.clone());
            self.new_tab_for_database(default_db, window, cx);
            return;
        }

        self.sync_editor_from_active_tab(window, cx);
        self.sync_database_for_active_tab(cx);
        self.persist();
        cx.notify();
    }

    pub(crate) fn restore_archived_tab(
        &mut self,
        archived_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(pos) = self
            .state
            .archived_tabs
            .iter()
            .position(|a| a.id == archived_id)
        else {
            return;
        };
        let archived = self.state.archived_tabs.remove(pos);
        if let Some(space) = self
            .state
            .spaces
            .iter_mut()
            .find(|s| s.id == archived.space_id)
        {
            // Restore back into its original group if a group with that name still exists.
            let matched_group_id = archived.group_name.as_deref().and_then(|gname| {
                space
                    .groups
                    .iter_mut()
                    .find(|g| g.name.eq_ignore_ascii_case(gname))
                    .map(|g| {
                        g.is_expanded = true;
                        g.id.clone()
                    })
            });
            let is_pinned = if matched_group_id.is_some() {
                false
            } else {
                archived.is_pinned
            };
            space.tabs.push(TabState {
                id: archived.id.clone(),
                title: archived.title,
                database: archived.database,
                content: archived.content,
                is_pinned,
                group_id: matched_group_id,
                last_accessed_at: now_rfc3339(),
            });
            space.rebuild_tab_order();
            space.active_tab_id = Some(archived.id);
            self.state.active_space_id = Some(space.id.clone());
            self.ensure_active_space_in_footer_window();
        }
        self.history_page = None;
        self.rail_context_menu = None;
        self.sync_editor_from_active_tab(window, cx);
        self.sync_database_for_active_tab(cx);
        self.persist();
        cx.notify();
    }

    pub(crate) fn delete_archived_tab(&mut self, archived_id: String, cx: &mut Context<Self>) {
        let prev_len = self.state.archived_tabs.len();
        self.state.archived_tabs.retain(|a| a.id != archived_id);
        if self.state.archived_tabs.len() != prev_len {
            self.persist();
            cx.notify();
        }
    }

    pub(crate) fn clear_all_archived_tabs(&mut self, cx: &mut Context<Self>) {
        let count = self.state.archived_tabs.len();
        self.state.archived_tabs.clear();
        if let Some(h) = self.history_page.as_mut() {
            h.confirming_clear_archive = false;
            h.banner_status = Some(
                format!(
                    "Cleared {count} archived tab{}",
                    if count == 1 { "" } else { "s" }
                )
                .into(),
            );
        }
        self.persist();
        cx.notify();
    }

    // ---------------------------------------------------------------- history page

    pub(crate) fn open_history_page(
        &mut self,
        segment: HistorySegment,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.rail_context_menu = None;
        self.close_db_palette(cx);
        self.cancel_rename(cx);
        self.cancel_group_rename(cx);
        self.cancel_result_rename(cx);
        self.managing_workspaces = false;
        self.managing_settings = false;
        self.settings_page = None;

        let retention_days = self.store.get_run_log_retention_days();
        let auto_days = self.state.effective_auto_archive_days();
        if segment == HistorySegment::Archived {
            let archived = self.state.run_auto_archive(chrono::Utc::now());
            for id in &archived {
                self.sessions.close_tab_session(id.clone());
                self.tab_runtime.remove(id);
            }
            if !archived.is_empty() {
                self.persist();
            }
        }

        if let Some(existing) = self.history_page.as_mut() {
            existing.segment = segment;
            existing.retention_days = retention_days;
            existing.scope_menu_open = false;
            existing.retention_menu_open = false;
            existing.confirming_clear_archive = false;
            existing.banner_status = None;
        } else {
            self.history_page = Some(HistoryPageState::open(
                segment,
                retention_days,
                auto_days,
                window,
                cx,
            ));
        }

        self.refresh_history_runs(cx);
    }

    pub(crate) fn toggle_history_archive_page(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.history_page.is_some() {
            self.close_history_page(cx);
        } else {
            self.open_history_page(HistorySegment::Archived, window, cx);
        }
    }

    pub(crate) fn close_history_page(&mut self, cx: &mut Context<Self>) {
        if self.history_page.take().is_some() {
            cx.notify();
        }
    }

    pub(crate) fn switch_history_segment(
        &mut self,
        segment: HistorySegment,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_history_page(segment, window, cx);
    }

    pub(crate) fn refresh_history_runs(&mut self, cx: &mut Context<Self>) {
        let active_space_id = self.active_space_id();
        let Some((all_workspaces, query)) = self
            .history_page
            .as_ref()
            .map(|h| (h.runs_all_workspaces, h.runs_search_query(cx)))
        else {
            return;
        };

        let scope = if all_workspaces {
            None
        } else {
            active_space_id.as_deref()
        };

        let runs = if query.is_empty() {
            self.store.get_run_log(scope, 500)
        } else {
            self.store.search_run_log(scope, &query, 500)
        };
        let scoped_count = self.store.count_run_log(scope);
        let all_count = self.store.count_run_log(None);
        let retention_days = self.store.get_run_log_retention_days();

        if let Some(h) = self.history_page.as_mut() {
            h.runs = runs;
            h.runs_total_count = scoped_count;
            h.runs_all_workspaces_count = all_count;
            h.retention_days = retention_days;
        }
        cx.notify();
    }

    pub(crate) fn set_history_runs_all_workspaces(&mut self, all: bool, cx: &mut Context<Self>) {
        if let Some(h) = self.history_page.as_mut() {
            h.runs_all_workspaces = all;
            h.scope_menu_open = false;
        }
        self.refresh_history_runs(cx);
    }

    pub(crate) fn set_history_run_retention(&mut self, days: i32, cx: &mut Context<Self>) {
        if self.store.set_run_log_retention_days(days) {
            let _ = self.store.prune_run_log();
            if let Some(h) = self.history_page.as_mut() {
                h.retention_days = self.store.get_run_log_retention_days();
                h.retention_menu_open = false;
                h.banner_status = Some(format!("Keeping {} days", h.retention_days).into());
            }
            self.refresh_history_runs(cx);
        } else {
            if let Some(h) = self.history_page.as_mut() {
                h.retention_menu_open = false;
                h.banner_status = Some("Could not save the history window.".into());
            }
            cx.notify();
        }
    }

    pub(crate) fn open_history_run(
        &mut self,
        run: RunLogRecord,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(space_id) = run.space_id.clone().filter(|id| {
            self.state.spaces.iter().any(|s| &s.id == id)
        }) else {
            if let Some(h) = self.history_page.as_mut() {
                h.banner_status = Some("That run's workspace no longer exists.".into());
            }
            cx.notify();
            return;
        };

        let existing_tab = run.tab_id.as_deref().and_then(|tid| {
            self.state
                .spaces
                .iter()
                .find(|s| s.id == space_id)
                .and_then(|s| s.tabs.iter().find(|t| t.id == tid))
                .map(|t| t.id.clone())
        });

        self.history_page = None;
        self.switch_space(space_id, window, cx);
        if let Some(tab_id) = existing_tab {
            self.switch_tab(tab_id, window, cx);
        } else {
            let db = run
                .database_name
                .filter(|d| !d.trim().is_empty());
            self.new_tab_with_content(db, run.statement, window, cx);
        }
    }

    pub(crate) fn toggle_auto_archive_enabled(&mut self, cx: &mut Context<Self>) {
        self.state.auto_archive_enabled = !self.state.auto_archive_enabled;
        self.persist();
        cx.notify();
    }

    pub(crate) fn auto_archive_days(&self) -> u32 {
        self.state.effective_auto_archive_days()
    }

    pub(crate) fn set_auto_archive_days(
        &mut self,
        days: u32,
        sync_input: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.state.auto_archive_days = days;
        let effective = self.state.effective_auto_archive_days();
        if sync_input {
            if let Some(input) = self
                .history_page
                .as_ref()
                .map(|h| h.auto_archive_days_input.clone())
            {
                input.update(cx, |state, cx| {
                    state.set_value(effective.to_string(), window, cx);
                });
            }
        }
        self.persist();
        cx.notify();
    }

    pub(crate) fn run_auto_archive_now(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let archived = self.state.run_auto_archive(chrono::Utc::now());
        for id in &archived {
            self.sessions.close_tab_session(id.clone());
            self.tab_runtime.remove(id);
        }
        let count = archived.len();
        if count > 0 {
            self.sync_editor_from_active_tab(window, cx);
            self.sync_database_for_active_tab(cx);
            self.persist();
        }
        if let Some(h) = self.history_page.as_mut() {
            h.banner_status = Some(
                if count == 0 {
                    "No inactive tabs to archive".to_string()
                } else {
                    format!(
                        "Archived {count} inactive tab{}",
                        if count == 1 { "" } else { "s" }
                    )
                }
                .into(),
            );
        }
        cx.notify();
    }

    // ---------------------------------------------------------------- inline renames

    fn start_rename_tab(&mut self, tab_id: String, window: &mut Window, cx: &mut Context<Self>) {
        self.rail_context_menu = None;
        self.cancel_group_rename(cx);
        self.cancel_result_rename(cx);
        let current_title = self
            .state
            .active_space()
            .and_then(|s| s.tabs.iter().find(|t| t.id == tab_id))
            .map(|t| t.title.clone())
            .unwrap_or_default();

        let input = cx.new(|cx| {
            InputState::new(window, cx)
                .placeholder("Tab name")
                .default_value(current_title)
        });
        let sub = cx.subscribe_in(&input, window, |this, _, event: &InputEvent, _window, cx| {
            match event {
                InputEvent::PressEnter { .. } => this.commit_rename(cx),
                InputEvent::Blur => this.commit_rename(cx),
                _ => {}
            }
        });
        input.update(cx, |s, cx| s.focus(window, cx));
        self.renaming_tab_id = Some(tab_id);
        self.rename_input = Some(input);
        self._rename_sub = Some(sub);
        cx.notify();
    }

    fn commit_rename(&mut self, cx: &mut Context<Self>) {
        let Some(tab_id) = self.renaming_tab_id.take() else {
            return;
        };
        if let Some(input) = self.rename_input.take() {
            let new_title = input.read(cx).value().trim().to_string();
            if !new_title.is_empty() {
                if let Some(tab) = self
                    .state
                    .active_space_mut()
                    .and_then(|s| s.tabs.iter_mut().find(|t| t.id == tab_id))
                {
                    tab.title = new_title;
                    self.persist();
                }
            }
        }
        self._rename_sub = None;
        cx.notify();
    }

    fn cancel_rename(&mut self, cx: &mut Context<Self>) {
        if self.renaming_tab_id.is_none() {
            return;
        }
        self.renaming_tab_id = None;
        self.rename_input = None;
        self._rename_sub = None;
        cx.notify();
    }

    fn start_rename_group(
        &mut self,
        group_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.rail_context_menu = None;
        self.cancel_rename(cx);
        self.cancel_result_rename(cx);
        let current_name = self
            .state
            .active_space()
            .and_then(|s| s.tab_group(&group_id))
            .map(|g| g.name.clone())
            .unwrap_or_default();

        let input = cx.new(|cx| {
            InputState::new(window, cx)
                .placeholder("Group name")
                .default_value(current_name)
        });
        let sub = cx.subscribe_in(&input, window, |this, _, event: &InputEvent, _window, cx| {
            match event {
                InputEvent::PressEnter { .. } | InputEvent::Blur => this.commit_group_rename(cx),
                _ => {}
            }
        });
        input.update(cx, |s, cx| s.focus(window, cx));
        self.renaming_group_id = Some(group_id);
        self.rename_group_input = Some(input);
        self._rename_group_sub = Some(sub);
        cx.notify();
    }

    fn commit_group_rename(&mut self, cx: &mut Context<Self>) {
        let Some(group_id) = self.renaming_group_id.take() else {
            return;
        };
        if let Some(input) = self.rename_group_input.take() {
            let new_name = input.read(cx).value().to_string();
            if let Some(space) = self.state.active_space_mut() {
                if space.rename_group(&group_id, &new_name) {
                    self.persist();
                }
            }
        }
        self._rename_group_sub = None;
        cx.notify();
    }

    fn cancel_group_rename(&mut self, cx: &mut Context<Self>) {
        if self.renaming_group_id.is_none() {
            return;
        }
        self.renaming_group_id = None;
        self.rename_group_input = None;
        self._rename_group_sub = None;
        cx.notify();
    }

    fn start_rename_result_tab(
        &mut self,
        tab_id: String,
        result_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.cancel_rename(cx);
        self.cancel_group_rename(cx);
        let current_title = self
            .tab_runtime
            .get(&tab_id)
            .and_then(|rt| rt.results.iter().find(|r| r.id == result_id))
            .map(|r| r.title.clone())
            .unwrap_or_default();

        let input = cx.new(|cx| {
            InputState::new(window, cx)
                .placeholder("Result name")
                .default_value(current_title)
        });
        let sub = cx.subscribe_in(&input, window, |this, _, event: &InputEvent, _window, cx| {
            match event {
                InputEvent::PressEnter { .. } | InputEvent::Blur => this.commit_result_rename(cx),
                _ => {}
            }
        });
        input.update(cx, |s, cx| s.focus(window, cx));
        self.renaming_result = Some((tab_id, result_id));
        self.rename_result_input = Some(input);
        self._rename_result_sub = Some(sub);
        cx.notify();
    }

    fn commit_result_rename(&mut self, cx: &mut Context<Self>) {
        let Some((tab_id, result_id)) = self.renaming_result.take() else {
            return;
        };
        if let Some(input) = self.rename_result_input.take() {
            let new_title = input.read(cx).value().to_string();
            if let Some(rt) = self.tab_runtime.get_mut(&tab_id) {
                rt.rename_result(&result_id, &new_title);
            }
        }
        self._rename_result_sub = None;
        cx.notify();
    }

    fn cancel_result_rename(&mut self, cx: &mut Context<Self>) {
        if self.renaming_result.is_none() {
            return;
        }
        self.renaming_result = None;
        self.rename_result_input = None;
        self._rename_result_sub = None;
        cx.notify();
    }

    fn select_result_tab(&mut self, result_id: String, cx: &mut Context<Self>) {
        if let Some(tab_id) = self.active_tab_id() {
            if let Some(rt) = self.tab_runtime.get_mut(&tab_id) {
                rt.active_result_id = Some(result_id);
                rt.show_messages = false;
            }
            self.sync_table_from_active_tab(cx);
            cx.notify();
        }
    }

    fn close_result_tab(&mut self, result_id: String, cx: &mut Context<Self>) {
        if self
            .renaming_result
            .as_ref()
            .is_some_and(|(_, rid)| rid == &result_id)
        {
            self.cancel_result_rename(cx);
        }
        if let Some(tab_id) = self.active_tab_id() {
            if let Some(rt) = self.tab_runtime.get_mut(&tab_id) {
                rt.close_result(&result_id);
            }
            self.sync_table_from_active_tab(cx);
            cx.notify();
        }
    }

    fn reorder_result_tab(
        &mut self,
        tab_id: &str,
        dragged_id: &str,
        target_id: &str,
        cx: &mut Context<Self>,
    ) {
        if let Some(rt) = self.tab_runtime.get_mut(tab_id) {
            if rt.reorder_result(dragged_id, target_id) {
                cx.notify();
            }
        }
    }

    // ---------------------------------------------------------------- query execution

    fn run_query(
        &mut self,
        statement_at_caret: bool,
        append_result: bool,
        cx: &mut Context<Self>,
    ) {
        let Some(space) = self.state.active_space().cloned() else {
            return;
        };
        let Some(tab) = space.active_tab().cloned() else {
            return;
        };

        let conn_state = self
            .space_runtime
            .get(&space.id)
            .map(SpaceRuntime::connection_state)
            .unwrap_or(ConnectionState::Disconnected);
        if conn_state != ConnectionState::Connected {
            let rt = self.tab_runtime.entry(tab.id.clone()).or_default();
            rt.apply_results(
                vec![QueryResult::error(
                    "Workspace is disconnected. Click Connect in the left sidebar first.",
                    Duration::ZERO,
                    tab.database.clone(),
                )],
                append_result,
            );
            self.sync_table_from_active_tab(cx);
            cx.notify();
            return;
        }

        let (full_sql, cursor_offset, selected_range) = {
            let ed = self.editor_state.read(cx);
            (ed.value().to_string(), ed.cursor(), ed.selected_range())
        };

        let language = self
            .catalog
            .find(&space.profile.provider_id)
            .map(|p| p.editor_language())
            .unwrap_or_else(|| Arc::new(SqlLanguage::tsql()));

        let sql_to_run = if selected_range.start < selected_range.end
            && selected_range.end <= full_sql.len()
        {
            full_sql[selected_range].to_string()
        } else if statement_at_caret {
            language
                .statement_at(&full_sql, cursor_offset)
                .map(|s| s.text)
                .unwrap_or(full_sql)
        } else {
            full_sql
        };

        let tab_id = tab.id.clone();
        let space_id = space.id.clone();
        let space_name = space.name.clone();
        let provider_id = space.profile.provider_id.clone();
        let database = tab.database.clone().or(space.active_database.clone());
        let logged_sql = sql_to_run.clone();
        let started_at = Instant::now();

        if let Some(active_tab) = self
            .state
            .active_space_mut()
            .and_then(|s| s.tabs.iter_mut().find(|t| t.id == tab_id))
        {
            active_tab.last_accessed_at = now_rfc3339();
        }

        {
            let rt = self.tab_runtime.entry(tab_id.clone()).or_default();
            rt.running = true;
            rt.last_summary = Some("Running…".into());
        }
        cx.notify();

        let rx = self.sessions.execute_tab_query(
            tab_id.clone(),
            space.profile,
            database.clone(),
            sql_to_run,
            QueryExecutionOptions::default(),
        );

        cx.spawn(async move |this, cx| {
            let outcome = rx.await;
            let wall_ms = started_at.elapsed().as_millis() as i64;
            let _ = this.update(cx, |this, cx| {
                let rt = this.tab_runtime.entry(tab_id.clone()).or_default();
                rt.running = false;
                rt.results_dismissed = false;

                match outcome {
                    Ok(Ok(results)) => {
                        let total_rows: usize = results.iter().map(|r| r.rows.len()).sum();
                        let total_ms: u128 = results.iter().map(|r| r.elapsed.as_millis()).sum();
                        let first_error = results
                            .iter()
                            .find(|r| r.is_error)
                            .map(|r| r.message.clone());
                        let truncated = results.iter().find(|r| r.is_truncated);
                        let status = if first_error.is_some() {
                            run_status::FAILED
                        } else {
                            run_status::OK
                        };
                        let duration_ms = if total_ms > 0 {
                            total_ms as i64
                        } else {
                            wall_ms
                        };

                        rt.last_summary = Some(
                            if first_error.is_some() {
                                format!("Failed · {duration_ms} ms")
                            } else {
                                format!("{total_rows} rows · {duration_ms} ms")
                            }
                            .into(),
                        );

                        this.store.log_run(&RunLogRecord::new_user_run(
                            Some(space_id.clone()),
                            Some(space_name.clone()),
                            database.clone(),
                            Some(provider_id.clone()),
                            Some(tab_id.clone()),
                            logged_sql.clone(),
                            status,
                            first_error,
                            duration_ms,
                            total_rows as i64,
                            truncated.and_then(|r| r.row_limit).map(|l| l as i64),
                            truncated.is_some(),
                        ));

                        rt.apply_results(results, append_result);
                    }
                    Ok(Err(err)) => {
                        let err_msg = err.to_string();
                        rt.last_summary = Some("Error".into());
                        this.store.log_run(&RunLogRecord::new_user_run(
                            Some(space_id.clone()),
                            Some(space_name.clone()),
                            database.clone(),
                            Some(provider_id.clone()),
                            Some(tab_id.clone()),
                            logged_sql.clone(),
                            run_status::FAILED,
                            Some(err_msg.clone()),
                            wall_ms,
                            0,
                            None,
                            false,
                        ));
                        rt.apply_results(
                            vec![QueryResult::error(err_msg, Duration::ZERO, database.clone())],
                            append_result,
                        );
                    }
                    Err(_) => {
                        rt.last_summary = Some("Cancelled".into());
                        this.store.log_run(&RunLogRecord::new_user_run(
                            Some(space_id.clone()),
                            Some(space_name.clone()),
                            database.clone(),
                            Some(provider_id.clone()),
                            Some(tab_id.clone()),
                            logged_sql.clone(),
                            run_status::CANCELLED,
                            Some("Cancelled".into()),
                            wall_ms,
                            0,
                            None,
                            false,
                        ));
                    }
                }

                if this.active_tab_id().as_deref() == Some(tab_id.as_str()) {
                    this.sync_table_from_active_tab(cx);
                }
                if this.history_page.is_some() {
                    this.refresh_history_runs(cx);
                } else {
                    cx.notify();
                }
            });
        })
        .detach();
    }

    fn cancel_active_query(&mut self, cx: &mut Context<Self>) {
        let Some(tab_id) = self.active_tab_id() else {
            return;
        };
        self.sessions.cancel_tab_query(tab_id.clone());
        if let Some(rt) = self.tab_runtime.get_mut(&tab_id) {
            rt.running = false;
            rt.last_summary = Some("Cancelled".into());
        }
        cx.notify();
    }

    fn toggle_results_pane(&mut self, cx: &mut Context<Self>) {
        let Some(tab_id) = self.active_tab_id() else {
            return;
        };
        let rt = self.tab_runtime.entry(tab_id).or_default();
        if rt.results.is_empty() {
            return;
        }
        rt.results_dismissed = !rt.results_dismissed;
        cx.notify();
    }

    fn dismiss_results_pane(&mut self, cx: &mut Context<Self>) {
        let Some(tab_id) = self.active_tab_id() else {
            return;
        };
        if let Some(rt) = self.tab_runtime.get_mut(&tab_id) {
            rt.results_dismissed = true;
            cx.notify();
        }
    }

    fn editor_line_col(&self, cx: &App) -> (usize, usize) {
        let ed = self.editor_state.read(cx);
        let text = ed.value();
        let mut offset = ed.cursor().min(text.len());
        while offset > 0 && !text.is_char_boundary(offset) {
            offset -= 1;
        }
        let prefix = &text[..offset];
        let mut line = 1usize;
        let mut last_nl = 0usize;
        for (idx, b) in prefix.bytes().enumerate() {
            if b == b'\n' {
                line += 1;
                last_nl = idx + 1;
            }
        }
        let col = prefix[last_nl..].chars().count() + 1;
        (line, col)
    }

    fn toggle_rail(&mut self, cx: &mut Context<Self>) {
        self.state.rail_open = !self.state.rail_open;
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.persist();
        cx.notify();
    }

    pub(crate) fn set_theme(
        &mut self,
        pref: ThemePreference,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.state.theme == pref {
            return;
        }
        self.state.theme = pref;
        apply_larik_theme(self.state.theme, Some(window), cx);
        self.persist();
        cx.notify();
    }

    fn toggle_theme(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let next = if StudioPalette::resolve_is_dark(self.state.theme, Some(window)) {
            ThemePreference::Light
        } else {
            ThemePreference::Dark
        };
        self.set_theme(next, window, cx);
    }

    fn toggle_schema_pane(&mut self, cx: &mut Context<Self>) {
        self.state.schema_pane_open = !self.state.schema_pane_open;
        self.rebuild_schema_rows();
        self.persist();
        cx.notify();
    }

    fn insert_into_editor(&mut self, snippet: String, window: &mut Window, cx: &mut Context<Self>) {
        self.editor_state.update(cx, |ed, cx| {
            ed.insert(&snippet, window, cx);
            ed.focus(window, cx);
        });
    }

    fn copy_active_tsv(&mut self, cx: &mut Context<Self>) {
        let tsv = self.table_state.read(cx).delegate().to_tsv();
        if tsv.is_empty() {
            return;
        }
        cx.write_to_clipboard(ClipboardItem::new_string(tsv));
        self.copied_toast = true;
        cx.notify();

        cx.spawn(async move |this, cx| {
            cx.background_executor()
                .timer(Duration::from_millis(1600))
                .await;
            let _ = this.update(cx, |this, cx| {
                this.copied_toast = false;
                cx.notify();
            });
        })
        .detach();
    }

    fn open_connection_modal(
        &mut self,
        space_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.close_db_palette(cx);
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.rail_context_menu = None;
        self.history_page = None;
        let can_delete = self.state.spaces.len() > 1;
        let Some(space) = self.state.spaces.iter().find(|s| s.id == space_id) else {
            return;
        };
        self.modal = Some(ConnectionModal::open(
            space.id.clone(),
            &space.name,
            &space.color,
            space.icon.as_deref(),
            &space.profile,
            can_delete,
            &self.catalog,
            window,
            cx,
        ));
        cx.notify();
    }

    fn handle_connection_modal_action(
        &mut self,
        action: ConnectionModalAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match action {
            ConnectionModalAction::Close => {
                self.modal = None;
                cx.notify();
            }
            ConnectionModalAction::ClearName => {
                if let Some(m) = self.modal.as_ref() {
                    m.name_input
                        .update(cx, |input, cx| input.set_value("", window, cx));
                }
                cx.notify();
            }
            ConnectionModalAction::ToggleIconPicker => {
                if let Some(m) = self.modal.as_mut() {
                    m.show_icon_picker = !m.show_icon_picker;
                    m.show_color_picker = false;
                    m.show_provider_dropdown = false;
                }
                cx.notify();
            }
            ConnectionModalAction::PickIcon(icon) => {
                if let Some(m) = self.modal.as_mut() {
                    m.icon = icon;
                    m.show_icon_picker = false;
                }
                cx.notify();
            }
            ConnectionModalAction::ToggleColorPicker => {
                if let Some(m) = self.modal.as_mut() {
                    m.show_color_picker = !m.show_color_picker;
                    m.show_icon_picker = false;
                    m.show_provider_dropdown = false;
                }
                cx.notify();
            }
            ConnectionModalAction::PickColor(hex) => {
                if let Some(m) = self.modal.as_mut() {
                    m.color = hex;
                    m.show_color_picker = false;
                }
                cx.notify();
            }
            ConnectionModalAction::ToggleProviderDropdown => {
                if let Some(m) = self.modal.as_mut() {
                    m.show_provider_dropdown = !m.show_provider_dropdown;
                    m.show_icon_picker = false;
                    m.show_color_picker = false;
                }
                cx.notify();
            }
            ConnectionModalAction::SwitchProvider(pid) => {
                if let Some(m) = self.modal.as_mut() {
                    m.switch_provider(&pid, &self.catalog, window, cx);
                }
                cx.notify();
            }
            ConnectionModalAction::ToggleBool(key, val) => {
                if let Some(m) = self.modal.as_mut() {
                    m.bool_values.insert(key, val);
                }
                cx.notify();
            }
            ConnectionModalAction::ToggleAdvanced => {
                if let Some(m) = self.modal.as_mut() {
                    m.show_advanced = !m.show_advanced;
                    m.show_icon_picker = false;
                    m.show_color_picker = false;
                    m.show_provider_dropdown = false;
                }
                cx.notify();
            }
            ConnectionModalAction::Test => {
                self.test_modal_connection(cx);
            }
            ConnectionModalAction::Save => {
                self.save_modal_and_connect(cx);
            }
            ConnectionModalAction::SetConfirmDelete(confirm) => {
                if let Some(m) = self.modal.as_mut() {
                    m.confirming_delete = confirm;
                }
                cx.notify();
            }
            ConnectionModalAction::ConfirmDelete => {
                if let Some(id) = self.modal.as_ref().map(|m| m.space_id.clone()) {
                    self.delete_workspace(id, window, cx);
                }
            }
        }
    }

    fn test_modal_connection(&mut self, cx: &mut Context<Self>) {
        let Some(modal) = self.modal.as_mut() else {
            return;
        };
        let profile = modal.collect_profile(&self.catalog, cx);
        modal.testing = true;
        modal.test_status = None;
        cx.notify();

        let rx = self.sessions.test_connection(profile);
        cx.spawn(async move |this, cx| {
            let outcome = rx.await;
            let _ = this.update(cx, |this, cx| {
                if let Some(modal) = this.modal.as_mut() {
                    modal.testing = false;
                    modal.test_status = Some(match outcome {
                        Ok(Ok(msg)) => Ok(msg),
                        Ok(Err(err)) => Err(err.to_string()),
                        Err(_) => Err("Connection test was cancelled.".into()),
                    });
                }
                cx.notify();
            });
        })
        .detach();
    }

    fn save_modal_and_connect(&mut self, cx: &mut Context<Self>) {
        let Some(modal) = self.modal.take() else {
            return;
        };
        let space_id = modal.space_id.clone();
        let new_name = modal.workspace_name(cx);
        let new_color = modal.color.clone();
        let new_icon = modal.icon.clone();
        let new_profile = modal.collect_profile(&self.catalog, cx);

        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            space.name = new_name;
            space.color = new_color;
            space.icon = new_icon;
            space.profile = new_profile;
        }
        self.disconnect_space(space_id.clone(), cx);
        self.persist();
        self.connect_space(space_id, cx);
        cx.notify();
    }
}

impl Focusable for StudioView {
    fn focus_handle(&self, _cx: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for StudioView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = StudioPalette::for_window(self.state.theme, window);
        let active_space = self.state.active_space();
        let active_tab = active_space.and_then(|s| s.active_tab());
        let default_space_rt = SpaceRuntime::default();
        let default_tab_rt = TabRuntime::default();
        let space_rt = active_space
            .and_then(|s| self.space_runtime.get(&s.id))
            .unwrap_or(&default_space_rt);
        let tab_rt = active_tab
            .and_then(|t| self.tab_runtime.get(&t.id))
            .unwrap_or(&default_tab_rt);

        let has_overlay = self.modal.is_some()
            || self.db_palette_open
            || self.renaming_tab_id.is_some()
            || self.renaming_group_id.is_some()
            || self.renaming_result.is_some()
            || self.rail_context_menu.is_some()
            || self.workspace_dropdown_open
            || self.db_dropdown_open
            || self.history_page.is_some()
            || self.managing_workspaces
            || self.managing_settings;
        let key_ctx = if has_overlay {
            "Studio StudioModal"
        } else {
            "Studio"
        };

        div()
            .id("larik-studio-root")
            .track_focus(&self.focus_handle)
            .key_context(key_ctx)
            .on_action(cx.listener(|this, _: &AcceptCompletionOrIndent, window, cx| {
                let menu_open = !this.managing_settings
                    && !this.managing_workspaces
                    && this.history_page.is_none()
                    && this.modal.is_none()
                    && this.editor_state.read(cx).completion_menu_state().open;
                if menu_open {
                    window.dispatch_action(
                        Box::new(Enter {
                            secondary: false,
                            shift: false,
                        }),
                        cx,
                    );
                } else {
                    window.dispatch_action(Box::new(IndentInline), cx);
                }
            }))
            .on_action(cx.listener(|this, _: &RunStatement, _, cx| {
                if !this.managing_workspaces
                    && !this.managing_settings
                    && this.history_page.is_none()
                {
                    this.run_query(true, false, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &RunStatementNewResult, _, cx| {
                if !this.managing_workspaces
                    && !this.managing_settings
                    && this.history_page.is_none()
                {
                    this.run_query(true, true, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &RunAll, _, cx| {
                if !this.managing_workspaces
                    && !this.managing_settings
                    && this.history_page.is_none()
                {
                    this.run_query(false, false, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &NewTab, window, cx| {
                if !this.managing_workspaces && !this.managing_settings {
                    this.prompt_new_tab(window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &CloseTab, window, cx| {
                if !this.managing_workspaces && !this.managing_settings {
                    if let Some(tab_id) = this.active_tab_id() {
                        this.archive_tab(tab_id, window, cx);
                    }
                }
            }))
            .on_action(cx.listener(|this, _: &NextTab, window, cx| {
                if !this.managing_workspaces && !this.managing_settings {
                    this.cycle_tab(1, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &PrevTab, window, cx| {
                if !this.managing_workspaces && !this.managing_settings {
                    this.cycle_tab(-1, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleRail, _, cx| {
                if !this.managing_workspaces && !this.managing_settings {
                    this.toggle_rail(cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleSchema, _, cx| {
                if !this.managing_workspaces && !this.managing_settings {
                    this.toggle_schema_pane(cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleTheme, window, cx| {
                this.toggle_theme(window, cx);
            }))
            .on_action(cx.listener(|this, _: &OpenHistory, window, cx| {
                if !this.managing_workspaces && !this.managing_settings {
                    this.open_history_page(HistorySegment::Runs, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &DismissModal, _, cx| {
                if this.modal.is_some() {
                    this.modal = None;
                    cx.notify();
                } else if this.rail_context_menu.is_some() {
                    this.close_rail_context_menu(cx);
                } else if this.db_palette_open {
                    this.close_db_palette(cx);
                } else if this.renaming_tab_id.is_some() {
                    this.cancel_rename(cx);
                } else if this.renaming_group_id.is_some() {
                    this.cancel_group_rename(cx);
                } else if this.renaming_result.is_some() {
                    this.cancel_result_rename(cx);
                } else if this.workspace_dropdown_open || this.db_dropdown_open {
                    this.workspace_dropdown_open = false;
                    this.db_dropdown_open = false;
                    cx.notify();
                } else if let Some(page) = this.history_page.as_mut() {
                    if page.scope_menu_open
                        || page.retention_menu_open
                        || page.confirming_clear_archive
                    {
                        page.scope_menu_open = false;
                        page.retention_menu_open = false;
                        page.confirming_clear_archive = false;
                        cx.notify();
                    } else {
                        this.close_history_page(cx);
                    }
                } else if this.managing_workspaces {
                    this.close_manage_workspaces(cx);
                } else if this.managing_settings {
                    this.close_settings_page(cx);
                }
            }))
            .relative()
            .size_full()
            .bg(palette.window_bg)
            .text_color(palette.text_primary)
            .flex()
            .flex_row()
            .overflow_hidden()
            // Full-page Manage Workspaces view OR Settings view OR main Studio workspace
            .when(self.managing_workspaces, |el| {
                el.child(self.render_manage_workspaces_page(active_space, palette, window, cx))
            })
            .when(!self.managing_workspaces && self.managing_settings, |el| {
                el.when_some(self.settings_page.as_ref(), |el, page| {
                    el.child(render_settings_page(
                        page,
                        self.state.theme,
                        self.store.db_path(),
                        &self.app_icon,
                        palette,
                        window,
                        cx,
                    ))
                })
            })
            .when(!self.managing_workspaces && !self.managing_settings, |el| {
                el
                    // Column 0: Full-height left sidebar over pure Windows 11 Mica (WinUI NavigationView rail)
                    .when(self.state.rail_open, |el| {
                        el.child(self.render_left_rail(
                            active_space,
                            active_tab,
                            space_rt,
                            palette,
                            cx,
                        ))
                    })
                    // Column 1 (optional): Docked Schema Inspector beside sidebar (matching MainPage.xaml)
                    .when(self.state.schema_pane_open, |el| {
                        el.child(self.render_schema_drawer(
                            active_space,
                            active_tab,
                            space_rt,
                            palette,
                            cx,
                        ))
                    })
                    // Column 2: Main right workspace column (TitleBar + History Page OR Query Workspace + StatusBar)
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .h_full()
                            .overflow_hidden()
                            .child(self.render_title_bar(palette, window, cx))
                            .when_some(self.history_page.as_ref(), |col, page| {
                                col.child(render_history_page(
                                    page,
                                    &self.state,
                                    active_space,
                                    palette,
                                    cx,
                                ))
                            })
                            .when(self.history_page.is_none(), |col| {
                                col.child(self.render_main_surface(
                                    active_space,
                                    active_tab,
                                    space_rt,
                                    tab_rt,
                                    palette,
                                    cx,
                                ))
                                .child(self.render_status_bar(
                                    active_space,
                                    active_tab,
                                    space_rt,
                                    tab_rt,
                                    palette,
                                    cx,
                                ))
                            }),
                    )
                    // Floating Workspace Switcher Flyout (overlays UI, never pushes sidebar elements)
                    .when(self.workspace_dropdown_open, |el| {
                        el.child(self.render_workspace_flyout(
                            active_space,
                            space_rt,
                            palette,
                            window,
                            cx,
                        ))
                    })
                    // Floating Database Selector Flyout
                    .when(self.db_dropdown_open, |el| {
                        el.child(self.render_db_flyout(
                            active_space,
                            active_tab,
                            space_rt,
                            palette,
                            cx,
                        ))
                    })
                    // Floating Right-Click Context Menu for Tabs & Tab Folders/Groups
                    .when_some(self.rail_context_menu.clone(), |el, menu| {
                        el.child(self.render_rail_context_menu(
                            menu,
                            active_space,
                            palette,
                            window,
                            cx,
                        ))
                    })
                    // Searchable New-Tab Database Palette Overlay
                    .when(self.db_palette_open, |el| {
                        el.child(self.render_db_palette(
                            active_space,
                            active_tab,
                            space_rt,
                            palette,
                            cx,
                        ))
                    })
            })
            // Provider-Generated Workspace Settings Modal (works from both Studio and Manage Workspaces)
            .when_some(self.modal.as_ref(), |el, modal| {
                el.child(render_connection_modal(
                    modal,
                    &self.catalog,
                    palette,
                    cx,
                    |this, action, window, cx| {
                        this.handle_connection_modal_action(action, window, cx);
                    },
                ))
            })
    }
}

impl StudioView {
    /// Transparent Windows 11 TitleBar placed at the top of the right main column so the
    /// left sidebar spans top-to-bottom cleanly over Mica just like the WinUI version.
    fn render_title_bar(
        &self,
        palette: StudioPalette,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        render_app_title_bar(
            "studio",
            IconName::Menu,
            &self.app_icon,
            "Larik SQL Studio",
            palette,
            window,
            cx,
            cx.listener(|this, _, _, cx| {
                this.toggle_rail(cx);
            }),
        )
    }

    fn render_left_rail(
        &self,
        active_space: Option<&SpaceState>,
        active_tab: Option<&TabState>,
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let space_color = active_space
            .map(|s| parse_hex_color(&s.color))
            .unwrap_or(palette.accent);
        let conn_state = space_rt.connection_state();

        let identity = active_space
            .and_then(|s| {
                self.catalog
                    .find(&s.profile.provider_id)
                    .and_then(|p| p.describe_identity(&s.profile))
            })
            .unwrap_or_default();

        let (status_color, status_text) = match conn_state {
            ConnectionState::Connected => (
                palette.success,
                if identity.is_empty() {
                    "Connected".to_string()
                } else {
                    format!("Connected · {identity}")
                },
            ),
            ConnectionState::Connecting => (palette.warning, "Connecting…".to_string()),
            ConnectionState::Faulted => (palette.danger, "Failed".to_string()),
            ConnectionState::Disconnected => (palette.text_muted, "Disconnected".to_string()),
        };

        let space_name = active_space
            .map(|s| s.name.clone())
            .unwrap_or_else(|| "Workspace".into());
        let space_initial = workspace_monogram(&space_name);
        let space_custom_icon =
            workspace_icon_name(active_space.and_then(|s| s.icon.as_deref()));

        let space_id_for_connect = active_space.map(|s| s.id.clone());
        let has_archived = !self.state.archived_tabs.is_empty();
        let history_open = self.history_page.is_some();

        let ws_open = self.workspace_dropdown_open;
        let schema_open = self.state.schema_pane_open;

        // Collect visible workspaces for the sidebar footer (max 3 shown at once with left/right arrows)
        let rail_spaces: Vec<&SpaceState> = self
            .state
            .spaces
            .iter()
            .filter(|space| {
                let is_active = active_space.is_some_and(|a| a.id == space.id);
                !space.is_hidden || is_active
            })
            .collect();
        let (footer_start, footer_end) =
            footer_space_window(rail_spaces.len(), self.footer_space_offset);
        let can_page_left = footer_start > 0;
        let can_page_right = footer_end < rail_spaces.len();

        v_flex()
            .w(px(252.0))
            .h_full()
            .flex_shrink_0()
            .bg(transparent_black())
            .border_r_1()
            .border_color(palette.hairline)
            // Row 0: Workspace Switcher Button (P Proxmox ⌄) — opens floating flyout overlay
            .child(
                v_flex()
                    .px_1p5()
                    .pt_1p5()
                    .child(
                        h_flex()
                            .id("rail-workspace-switcher")
                            .h(px(34.0))
                            .px_2p5()
                            .rounded(px(6.0))
                            .items_center()
                            .justify_between()
                            .gap_2()
                            .cursor_pointer()
                            .when(ws_open, |el| el.bg(palette.subtle_hover))
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.workspace_dropdown_open = !this.workspace_dropdown_open;
                                this.db_dropdown_open = false;
                                this.rail_context_menu = None;
                                cx.notify();
                            }))
                            .child(
                                h_flex()
                                    .flex_1()
                                    .min_w_0()
                                    .items_center()
                                    .gap_2()
                                    .child(
                                        div()
                                            .size(px(18.0))
                                            .rounded(px(4.0))
                                            .bg(palette.subtle_fill)
                                            .flex()
                                            .items_center()
                                            .justify_center()
                                            .text_size(px(10.0))
                                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                            .text_color(palette.text_secondary)
                                            .child(match space_custom_icon {
                                                Some(icon) => Icon::new(icon)
                                                    .size(px(11.0))
                                                    .text_color(palette.text_secondary)
                                                    .into_any_element(),
                                                None => space_initial.into_any_element(),
                                            }),
                                    )
                                    .child(
                                        div()
                                            .text_size(px(12.5))
                                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                            .text_color(palette.text_primary)
                                            .truncate()
                                            .child(space_name),
                                    ),
                            )
                            .child(
                                Icon::new(IconName::ChevronDown)
                                    .size(px(11.0))
                                    .text_color(palette.text_muted),
                            ),
                    ),
            )
            // Row 1: Quiet connection status + inline Connect / Disconnect text action
            .child(
                h_flex()
                    .px_3()
                    .pt_1()
                    .pb_1p5()
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .flex_1()
                            .min_w_0()
                            .items_center()
                            .gap_1p5()
                            .child(
                                div()
                                    .flex_shrink_0()
                                    .size(px(5.5))
                                    .rounded_full()
                                    .bg(status_color),
                            )
                            .child(
                                div()
                                    .text_size(px(11.0))
                                    .text_color(palette.text_muted)
                                    .truncate()
                                    .child(status_text),
                            ),
                    )
                    .child(match conn_state {
                        ConnectionState::Connected => div()
                            .id("rail-disconnect")
                            .flex_shrink_0()
                            .px_1p5()
                            .py(px(2.0))
                            .rounded(px(4.0))
                            .cursor_pointer()
                            .text_size(px(11.0))
                            .text_color(palette.text_muted)
                            .hover(|s| {
                                s.bg(palette.subtle_hover).text_color(palette.text_primary)
                            })
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(id) = space_id_for_connect.clone() {
                                    this.disconnect_space(id, cx);
                                }
                            }))
                            .child("Disconnect")
                            .into_any_element(),
                        ConnectionState::Connecting => div()
                            .flex_shrink_0()
                            .px_1p5()
                            .py(px(2.0))
                            .text_size(px(11.0))
                            .text_color(palette.text_muted)
                            .child("Connecting…")
                            .into_any_element(),
                        _ => div()
                            .id("rail-connect")
                            .flex_shrink_0()
                            .px_1p5()
                            .py(px(2.0))
                            .rounded(px(4.0))
                            .cursor_pointer()
                            .text_size(px(11.0))
                            .font_weight(gpui_kit::FontWeight::MEDIUM)
                            .text_color(palette.accent)
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(move |this, _, _, cx| {
                                if let Some(id) = space_id_for_connect.clone() {
                                    this.connect_space(id, cx);
                                }
                            }))
                            .child("Connect")
                            .into_any_element(),
                    }),
            )
            // Inline error banner if connection failed
            .when_some(space_rt.last_error.clone(), |el, err| {
                el.child(
                    div()
                        .mx_2p5()
                        .mb_1p5()
                        .px_2()
                        .py_1p5()
                        .rounded(px(6.0))
                        .bg(palette.danger.opacity(0.14))
                        .border_1()
                        .border_color(palette.danger.opacity(0.3))
                        .text_xs()
                        .text_color(palette.danger)
                        .child(err),
                )
            })
            // Row 2: [DB] Database Selector (per-tab binding) + Schema Icon Button on one row
            .when(
                conn_state == ConnectionState::Connected && !space_rt.databases.is_empty(),
                |el| {
                    let current_db = active_tab
                        .and_then(|t| t.database.clone())
                        .or_else(|| active_space.and_then(|s| s.active_database.clone()))
                        .unwrap_or_else(|| "Select database".into());
                    let dropdown_open = self.db_dropdown_open;

                    el.child(
                        h_flex()
                            .px_2()
                            .pb_1()
                            .items_center()
                            .gap_1()
                            .child(
                                h_flex()
                                    .id("rail-db-selector")
                                    .flex_1()
                                    .min_w_0()
                                    .h(px(30.0))
                                    .px_2()
                                    .rounded(px(6.0))
                                    .items_center()
                                    .justify_between()
                                    .gap_1p5()
                                    .cursor_pointer()
                                    .when(dropdown_open, |d| d.bg(palette.subtle_hover))
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.db_dropdown_open = !this.db_dropdown_open;
                                        this.workspace_dropdown_open = false;
                                        this.rail_context_menu = None;
                                        cx.notify();
                                    }))
                                    .child(
                                        h_flex()
                                            .flex_1()
                                            .min_w_0()
                                            .items_center()
                                            .gap_1p5()
                                            .child(
                                                div()
                                                    .flex_shrink_0()
                                                    .px_1()
                                                    .py(px(1.0))
                                                    .rounded(px(3.0))
                                                    .bg(palette.subtle_fill)
                                                    .text_size(px(8.5))
                                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                    .text_color(palette.text_muted)
                                                    .child("DB"),
                                            )
                                            .child(
                                                div()
                                                    .text_size(px(12.0))
                                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                    .text_color(palette.text_primary)
                                                    .truncate()
                                                    .child(current_db),
                                            ),
                                    )
                                    .child(
                                        Icon::new(IconName::ChevronDown)
                                            .size(px(11.0))
                                            .text_color(palette.text_muted),
                                    ),
                            )
                            // Schema Inspector toggle button right beside the DB selector
                            .child(
                                div()
                                    .id("rail-schema-toggle")
                                    .size(px(30.0))
                                    .flex_shrink_0()
                                    .rounded(px(6.0))
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .cursor_pointer()
                                    .when(schema_open, |d| d.bg(palette.subtle_fill))
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.toggle_schema_pane(cx);
                                    }))
                                    .child(
                                        Icon::new(IconName::List)
                                            .size(px(14.0))
                                            .text_color(if schema_open {
                                                palette.accent
                                            } else {
                                                palette.text_secondary
                                            }),
                                    ),
                            ),
                    )
                },
            )
            // Row 2.5: WinUI Pinned Band (pinned folders + loose pinned tabs)
            .when_some(
                active_space.filter(|space| {
                    space.groups.iter().any(|g| g.is_pinned)
                        || space.tabs.iter().any(|t| t.is_pinned && t.group_id.is_none())
                }),
                |el, space| {
                    let space_id_pinned_over = space.id.clone();
                    let space_id_pinned_drop = space.id.clone();
                    let mut pinned_rows: Vec<AnyElement> = Vec::new();
                    for group in space.groups.iter().filter(|g| g.is_pinned) {
                        let members: Vec<&TabState> = space
                            .tabs
                            .iter()
                            .filter(|t| t.group_id.as_deref() == Some(group.id.as_str()))
                            .collect();
                        if members.is_empty() {
                            continue;
                        }
                        pinned_rows.push(
                            self.render_group_header_row(
                                &space.id,
                                group,
                                members.len(),
                                space_color,
                                palette,
                                cx,
                            )
                            .into_any_element(),
                        );
                        if group.is_expanded {
                            for tab in members {
                                pinned_rows.push(
                                    self.render_tab_row(
                                        &space.id,
                                        tab,
                                        active_tab.is_some_and(|a| a.id == tab.id),
                                        true,
                                        true,
                                        space_color,
                                        palette,
                                        cx,
                                    )
                                    .into_any_element(),
                                );
                            }
                        }
                    }
                    for tab in space
                        .tabs
                        .iter()
                        .filter(|t| t.is_pinned && t.group_id.is_none())
                    {
                        pinned_rows.push(
                            self.render_tab_row(
                                &space.id,
                                tab,
                                active_tab.is_some_and(|a| a.id == tab.id),
                                false,
                                true,
                                space_color,
                                palette,
                                cx,
                            )
                            .into_any_element(),
                        );
                    }

                    el.child(
                        v_flex()
                            .pt_1()
                            .child(
                                div()
                                    .id("rail-pinned-scroll")
                                    .max_h(px(220.0))
                                    .overflow_y_scroll()
                                    .px_1p5()
                                    .child(
                                        v_flex()
                                            .gap_0p5()
                                            .children(pinned_rows)
                                            // Compact drop strip at bottom of pinned band to pull tabs out of pinned folders into loose pinned
                                            .child(
                                                div()
                                                    .id("rail-pinned-loose-drop")
                                                    .h(px(10.0))
                                                    .w_full()
                                                    .rounded(px(4.0))
                                                    .drag_over::<DraggedQueryTab>(
                                                        move |style, dragged, _, _| {
                                                            if dragged.space_id == space_id_pinned_over
                                                            {
                                                                style
                                                                    .border_t_2()
                                                                    .border_color(space_color)
                                                                    .bg(palette.accent_soft)
                                                            } else {
                                                                style
                                                            }
                                                        },
                                                    )
                                                    .on_drop(cx.listener(
                                                        move |this, dragged: &DraggedQueryTab, _, cx| {
                                                            if dragged.space_id == space_id_pinned_drop
                                                            {
                                                                this.drop_tab_to_loose(
                                                                    &dragged.tab_id,
                                                                    true,
                                                                    false,
                                                                    cx,
                                                                );
                                                            }
                                                        },
                                                    )),
                                            ),
                                    ),
                            )
                            .child(
                                div()
                                    .h(px(1.0))
                                    .mx_3()
                                    .mt_1()
                                    .bg(palette.hairline_subtle),
                            ),
                    )
                },
            )
            // Row 3: TABS Section Header + Group by DB / Undo / History Toggle Buttons
            .child({
                let can_undo_grouping = active_space.is_some_and(|space| {
                    self.last_grouping_space_id.as_deref() == Some(space.id.as_str())
                        && self
                            .last_grouping_ids
                            .iter()
                            .any(|gid| space.tab_group(gid).is_some())
                });
                let can_group_by_db =
                    active_space.is_some_and(SpaceState::has_ungrouped_database_cluster);
                let space_id_hdr_over = active_space.map(|s| s.id.clone()).unwrap_or_default();
                let space_id_hdr_drop = space_id_hdr_over.clone();

                h_flex()
                    .id("rail-tabs-header")
                    .pl_3p5()
                    .pr_2p5()
                    .pt_2()
                    .pb_1()
                    .items_center()
                    .justify_between()
                    .drag_over::<DraggedQueryTab>(move |style, dragged, _, _| {
                        if !space_id_hdr_over.is_empty() && dragged.space_id == space_id_hdr_over {
                            style.border_b_2().border_color(space_color)
                        } else {
                            style
                        }
                    })
                    .on_drop(cx.listener(move |this, dragged: &DraggedQueryTab, _, cx| {
                        if !space_id_hdr_drop.is_empty() && dragged.space_id == space_id_hdr_drop {
                            this.drop_tab_to_loose(&dragged.tab_id, false, true, cx);
                        }
                    }))
                    .child(
                        div()
                            .text_size(px(10.0))
                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            .text_color(palette.text_muted)
                            .child("TABS"),
                    )
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1()
                            .when(can_undo_grouping, |row| {
                                row.child(
                                    h_flex()
                                        .id("rail-undo-grouping")
                                        .h(px(22.0))
                                        .px_1p5()
                                        .rounded(px(5.0))
                                        .items_center()
                                        .gap_1()
                                        .cursor_pointer()
                                        .bg(palette.subtle_fill)
                                        .hover(|s| s.bg(palette.subtle_hover))
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.undo_grouping(cx);
                                        }))
                                        .child(
                                            Icon::new(IconName::Undo2)
                                                .size(px(10.5))
                                                .text_color(palette.accent),
                                        )
                                        .child(
                                            div()
                                                .text_size(px(10.5))
                                                .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                .text_color(palette.text_primary)
                                                .child("Undo"),
                                        ),
                                )
                            })
                            .when(can_group_by_db, |row| {
                                row.child(
                                    Button::new("rail-group-by-db")
                                        .ghost()
                                        .xsmall()
                                        .icon(IconName::Layers)
                                        .tooltip("Group tabs by database")
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.group_tabs_by_database(cx);
                                        })),
                                )
                            })
                            .child(
                                div()
                                    .id("toggle-archive-icon")
                                    .relative()
                                    .size(px(24.0))
                                    .rounded(px(5.0))
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .cursor_pointer()
                                    .when(history_open, |d| d.bg(palette.subtle_fill))
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.toggle_history_archive_page(window, cx);
                                    }))
                                    .child(
                                        Icon::new(IconName::RotateCcw)
                                            .size(px(12.5))
                                            .text_color(if history_open {
                                                palette.accent
                                            } else {
                                                palette.text_muted
                                            }),
                                    )
                                    .when(has_archived, |d| {
                                        d.child(
                                            div()
                                                .absolute()
                                                .top(px(3.0))
                                                .right(px(3.0))
                                                .size(px(5.0))
                                                .rounded_full()
                                                .bg(palette.accent),
                                        )
                                    }),
                            ),
                    )
            })
            // Row 4: Full-width "+ New tab    Ctrl+T" row (also drops dragged tabs to top of unpinned loose list)
            .child({
                let space_id_new_over = active_space.map(|s| s.id.clone()).unwrap_or_default();
                let space_id_new_drop = space_id_new_over.clone();
                h_flex()
                    .id("rail-new-query-row")
                    .h(px(34.0))
                    .mx_1p5()
                    .mb_1()
                    .px_2p5()
                    .rounded(px(6.0))
                    .items_center()
                    .justify_between()
                    .cursor_pointer()
                    .hover(|s| s.bg(palette.subtle_hover))
                    .drag_over::<DraggedQueryTab>(move |style, dragged, _, _| {
                        if !space_id_new_over.is_empty() && dragged.space_id == space_id_new_over {
                            style
                                .border_b_2()
                                .border_color(space_color)
                                .bg(palette.subtle_hover)
                        } else {
                            style
                        }
                    })
                    .on_drop(cx.listener(move |this, dragged: &DraggedQueryTab, _, cx| {
                        if !space_id_new_drop.is_empty() && dragged.space_id == space_id_new_drop {
                            this.drop_tab_to_loose(&dragged.tab_id, false, true, cx);
                        }
                    }))
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.prompt_new_tab(window, cx);
                    }))
                    .child(
                        h_flex()
                            .items_center()
                            .gap_2()
                            .child(
                                Icon::new(IconName::Plus)
                                    .size(px(13.0))
                                    .text_color(palette.text_secondary),
                            )
                            .child(
                                div()
                                    .text_size(px(12.0))
                                    .text_color(palette.text_primary)
                                    .child("New tab"),
                            ),
                    )
                    .child(
                        div()
                            .text_size(px(10.5))
                            .text_color(palette.text_muted)
                            .child("Ctrl+T"),
                    )
            })
            // Row 5: Unpinned Vertical Tabs List (unpinned groups + loose unpinned tabs + drag-out drop zone)
            .child({
                let space_id_drop_over = active_space.map(|s| s.id.clone()).unwrap_or_default();
                let space_id_drop_end = space_id_drop_over.clone();
                let mut rows: Vec<AnyElement> = Vec::new();
                if let Some(space) = active_space {
                    for group in space.groups.iter().filter(|g| !g.is_pinned) {
                        let members: Vec<&TabState> = space
                            .tabs
                            .iter()
                            .filter(|t| t.group_id.as_deref() == Some(group.id.as_str()))
                            .collect();
                        if members.is_empty() {
                            continue;
                        }
                        rows.push(
                            self.render_group_header_row(
                                &space.id,
                                group,
                                members.len(),
                                space_color,
                                palette,
                                cx,
                            )
                            .into_any_element(),
                        );
                        if group.is_expanded {
                            for tab in members {
                                rows.push(
                                    self.render_tab_row(
                                        &space.id,
                                        tab,
                                        active_tab.is_some_and(|a| a.id == tab.id),
                                        true,
                                        false,
                                        space_color,
                                        palette,
                                        cx,
                                    )
                                    .into_any_element(),
                                );
                            }
                        }
                    }
                    for tab in space
                        .tabs
                        .iter()
                        .filter(|t| !t.is_pinned && t.group_id.is_none())
                    {
                        rows.push(
                            self.render_tab_row(
                                &space.id,
                                tab,
                                active_tab.is_some_and(|a| a.id == tab.id),
                                false,
                                false,
                                space_color,
                                palette,
                                cx,
                            )
                            .into_any_element(),
                        );
                    }
                }

                div()
                    .id("rail-tabs-scroll")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .px_1p5()
                    .pb_2()
                    .child(
                        v_flex()
                            .min_h_full()
                            .gap_1()
                            .children(rows)
                            // Bottom empty-space drop target: always allows dragging a tab out of a folder or pinned band
                            .child(
                                div()
                                    .id("rail-unpinned-drop-zone")
                                    .flex_1()
                                    .min_h(px(52.0))
                                    .w_full()
                                    .rounded(px(6.0))
                                    .drag_over::<DraggedQueryTab>(move |style, dragged, _, _| {
                                        if !space_id_drop_over.is_empty()
                                            && dragged.space_id == space_id_drop_over
                                        {
                                            style
                                                .border_t_2()
                                                .border_color(space_color)
                                                .bg(palette.subtle_hover.opacity(0.6))
                                        } else {
                                            style
                                        }
                                    })
                                    .on_drop(cx.listener(
                                        move |this, dragged: &DraggedQueryTab, _, cx| {
                                            if !space_id_drop_end.is_empty()
                                                && dragged.space_id == space_id_drop_end
                                            {
                                                this.drop_tab_to_loose(
                                                    &dragged.tab_id,
                                                    false,
                                                    false,
                                                    cx,
                                                );
                                            }
                                        },
                                    )),
                            ),
                    )
            })
            // Row 6: WinUI Sidebar Footer (Settings gear | < max 3 workspace chips > | balancing spacer)
            .child(
                h_flex()
                    .h(px(40.0))
                    .px_2p5()
                    .border_t_1()
                    .border_color(palette.hairline_subtle)
                    .items_center()
                    .justify_between()
                    .child(
                        div()
                            .id("footer-settings-btn")
                            .size(px(28.0))
                            .rounded(px(5.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.open_settings_page(window, cx);
                            }))
                            .child(
                                Icon::new(IconName::Settings)
                                    .size(px(13.5))
                                    .text_color(palette.text_muted),
                            ),
                    )
                    .child(
                        h_flex()
                            .flex_1()
                            .justify_center()
                            .items_center()
                            .gap_1()
                            .when(can_page_left, |el| {
                                el.child(
                                    div()
                                        .id("footer-spaces-prev")
                                        .w(px(20.0))
                                        .h(px(24.0))
                                        .rounded(px(5.0))
                                        .flex()
                                        .items_center()
                                        .justify_center()
                                        .cursor_pointer()
                                        .hover(|s| s.bg(palette.subtle_hover))
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.footer_space_offset =
                                                this.footer_space_offset.saturating_sub(1);
                                            cx.notify();
                                        }))
                                        .child(
                                            Icon::new(IconName::ChevronLeft)
                                                .size(px(12.0))
                                                .text_color(palette.text_muted),
                                        ),
                                )
                            })
                            .children(rail_spaces[footer_start..footer_end].iter().map(|space| {
                                let is_active = active_space.is_some_and(|a| a.id == space.id);
                                let sid = space.id.clone();
                                let initial = workspace_monogram(&space.name);
                                let custom_icon = workspace_icon_name(space.icon.as_deref());
                                let fg = if is_active {
                                    palette.text_primary
                                } else {
                                    palette.text_muted
                                };

                                div()
                                    .id(SharedString::from(format!("space-chip-{sid}")))
                                    .w(px(26.0))
                                    .h(px(24.0))
                                    .rounded(px(6.0))
                                    .cursor_pointer()
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .text_size(px(11.0))
                                    .when(is_active, |el| {
                                        el.bg(palette.chip_active_bg)
                                            .border_1()
                                            .border_color(palette.card_border)
                                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                            .text_color(palette.text_primary)
                                    })
                                    .when(!is_active, |el| {
                                        el.text_color(palette.text_muted).hover(|s| {
                                            s.bg(palette.subtle_hover)
                                                .text_color(palette.text_primary)
                                        })
                                    })
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.switch_space(sid.clone(), window, cx);
                                    }))
                                    .child(match custom_icon {
                                        Some(icon) => Icon::new(icon)
                                            .size(px(12.0))
                                            .text_color(fg)
                                            .into_any_element(),
                                        None => initial.into_any_element(),
                                    })
                            }))
                            .when(can_page_right, |el| {
                                el.child(
                                    div()
                                        .id("footer-spaces-next")
                                        .w(px(20.0))
                                        .h(px(24.0))
                                        .rounded(px(5.0))
                                        .flex()
                                        .items_center()
                                        .justify_center()
                                        .cursor_pointer()
                                        .hover(|s| s.bg(palette.subtle_hover))
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.footer_space_offset =
                                                this.footer_space_offset.saturating_add(1);
                                            cx.notify();
                                        }))
                                        .child(
                                            Icon::new(IconName::ChevronRight)
                                                .size(px(12.0))
                                                .text_color(palette.text_muted),
                                        ),
                                )
                            }),
                    )
                    // Right balancing spacer matching WinUI MainPage.xaml (28px opposite Settings gear)
                    .child(div().w(px(28.0)).flex_shrink_0()),
            )
    }

    /// Floating Workspace Switcher Flyout matching WinUI Larik reference:
    /// overlays the UI without expanding or shifting sidebar elements below.
    fn render_workspace_flyout(
        &self,
        active_space: Option<&SpaceState>,
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let conn_state = space_rt.connection_state();
        let active_id = active_space.map(|s| s.id.clone());

        let header_caption = active_space
            .map(|space| {
                let provider = self.catalog.find(&space.profile.provider_id);
                let provider_name = provider
                    .as_ref()
                    .map(|p| p.display_name().to_string())
                    .unwrap_or_else(|| space.profile.provider_id.clone());
                let endpoint = provider
                    .as_ref()
                    .and_then(|p| p.describe_endpoint(&space.profile));
                let state_str = match conn_state {
                    ConnectionState::Connected => "Connected",
                    ConnectionState::Connecting => "Connecting…",
                    ConnectionState::Faulted => "Failed",
                    ConnectionState::Disconnected => "Disconnected",
                };
                match endpoint {
                    Some(ep) if !ep.is_empty() => format!("{provider_name} · {ep} · {state_str}"),
                    _ => format!("{provider_name} · {state_str}"),
                }
            })
            .unwrap_or_else(|| "Disconnected".into());

        let is_dark = StudioPalette::resolve_is_dark(self.state.theme, Some(window));
        let active_pill_bg = if is_dark {
            hsla(212.0 / 360.0, 0.24, 0.36, 0.52)
        } else {
            palette.accent_soft
        };

        div()
            .id("workspace-flyout-backdrop")
            .absolute()
            .inset_0()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    this.workspace_dropdown_open = false;
                    cx.notify();
                }),
            )
            .child(
                v_flex()
                    .id("workspace-flyout-card")
                    .absolute()
                    .top(px(44.0))
                    .left(px(8.0))
                    .w(px(286.0))
                    .p_3()
                    .rounded(px(10.0))
                    .bg(palette.modal_bg)
                    .border_1()
                    .border_color(palette.card_border)
                    .shadow_2xl()
                    .gap_1p5()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    // Status caption (`SQL Server · 192.168.0.251 · Connected`)
                    .child(
                        div()
                            .px_1p5()
                            .pt_0p5()
                            .pb_1()
                            .text_size(px(11.0))
                            .text_color(palette.text_muted)
                            .truncate()
                            .child(header_caption),
                    )
                    // Connect / Disconnect action row
                    .child({
                        let id_for_action = active_id.clone();
                        let (label, enabled) = match conn_state {
                            ConnectionState::Connected => ("Disconnect", true),
                            ConnectionState::Connecting => ("Connecting…", false),
                            _ => ("Connect", true),
                        };
                        h_flex()
                            .id("ws-flyout-power")
                            .h(px(34.0))
                            .px_2()
                            .rounded(px(6.0))
                            .items_center()
                            .gap_2p5()
                            .when(enabled, |el| {
                                el.cursor_pointer()
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        this.workspace_dropdown_open = false;
                                        if let Some(id) = id_for_action.clone() {
                                            if conn_state == ConnectionState::Connected {
                                                this.disconnect_space(id, cx);
                                            } else {
                                                this.connect_space(id, cx);
                                            }
                                        }
                                    }))
                            })
                            .child(
                                Icon::new(IconName::Power)
                                    .size(px(14.0))
                                    .text_color(if conn_state == ConnectionState::Connected {
                                        palette.text_secondary
                                    } else {
                                        palette.accent
                                    }),
                            )
                            .child(
                                div()
                                    .text_size(px(13.0))
                                    .text_color(if enabled {
                                        palette.text_primary
                                    } else {
                                        palette.text_muted
                                    })
                                    .child(label),
                            )
                    })
                    // Divider
                    .child(
                        div()
                            .my_1()
                            .h(px(1.0))
                            .w_full()
                            .bg(palette.hairline_subtle),
                    )
                    // WORKSPACES section header
                    .child(
                        div()
                            .px_1p5()
                            .py_0p5()
                            .text_size(px(10.0))
                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            .text_color(palette.text_muted)
                            .child("WORKSPACES"),
                    )
                    // Visible workspaces list
                    .child(
                        v_flex()
                            .max_h(px(260.0))
                            .id("ws-flyout-list")
                            .overflow_y_scroll()
                            .gap_1()
                            .children(self.state.spaces.iter().filter_map(|space| {
                                let is_active = active_space.is_some_and(|a| a.id == space.id);
                                if space.is_hidden && !is_active {
                                    return None;
                                }
                                let sid = space.id.clone();
                                let initial = workspace_monogram(&space.name);
                                let custom_icon = workspace_icon_name(space.icon.as_deref());
                                let badge_fg = if is_active {
                                    palette.text_primary
                                } else {
                                    palette.text_secondary
                                };
                                let provider_label = self
                                    .catalog
                                    .find(&space.profile.provider_id)
                                    .map(|p| p.display_name().to_string())
                                    .unwrap_or_else(|| space.profile.provider_id.clone());

                                Some(
                                    h_flex()
                                        .id(SharedString::from(format!("ws-flyout-item-{sid}")))
                                        .h(px(38.0))
                                        .px_2p5()
                                        .rounded(px(8.0))
                                        .items_center()
                                        .justify_between()
                                        .gap_2()
                                        .cursor_pointer()
                                        .when(is_active, |d| {
                                            d.bg(active_pill_bg)
                                                .border_1()
                                                .border_color(palette.card_border)
                                        })
                                        .when(!is_active, |d| {
                                            d.hover(|s| s.bg(palette.subtle_hover))
                                        })
                                        .on_click(cx.listener(move |this, _, window, cx| {
                                            this.switch_space(sid.clone(), window, cx);
                                        }))
                                        .child(
                                            h_flex()
                                                .flex_1()
                                                .min_w_0()
                                                .items_center()
                                                .gap_2p5()
                                                .child(
                                                    div()
                                                        .w(px(16.0))
                                                        .flex()
                                                        .items_center()
                                                        .justify_center()
                                                        .text_size(px(11.0))
                                                        .font_weight(if is_active {
                                                            gpui_kit::FontWeight::BOLD
                                                        } else {
                                                            gpui_kit::FontWeight::SEMIBOLD
                                                        })
                                                        .text_color(badge_fg)
                                                        .child(match custom_icon {
                                                            Some(icon) => Icon::new(icon)
                                                                .size(px(12.5))
                                                                .text_color(badge_fg)
                                                                .into_any_element(),
                                                            None => initial.into_any_element(),
                                                        }),
                                                )
                                                .child(
                                                    div()
                                                        .text_size(px(13.0))
                                                        .font_weight(if is_active {
                                                            gpui_kit::FontWeight::SEMIBOLD
                                                        } else {
                                                            gpui_kit::FontWeight::NORMAL
                                                        })
                                                        .text_color(palette.text_primary)
                                                        .truncate()
                                                        .child(space.name.clone()),
                                                ),
                                        )
                                        .child(
                                            h_flex()
                                                .flex_shrink_0()
                                                .items_center()
                                                .gap_2()
                                                .child(
                                                    div()
                                                        .px_2()
                                                        .py(px(2.0))
                                                        .rounded(px(4.0))
                                                        .border_1()
                                                        .border_color(palette.hairline)
                                                        .text_size(px(10.5))
                                                        .text_color(palette.text_secondary)
                                                        .child(provider_label),
                                                )
                                                .when(is_active, |el| {
                                                    el.child(
                                                        Icon::new(IconName::Check)
                                                            .size(px(13.0))
                                                            .text_color(palette.accent),
                                                    )
                                                }),
                                        ),
                                )
                            })),
                    )
                    // Divider
                    .child(
                        div()
                            .my_1()
                            .h(px(1.0))
                            .w_full()
                            .bg(palette.hairline_subtle),
                    )
                    // + New workspace
                    .child(
                        h_flex()
                            .id("ws-flyout-new")
                            .h(px(34.0))
                            .px_2()
                            .rounded(px(6.0))
                            .items_center()
                            .gap_2p5()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.workspace_dropdown_open = false;
                                this.create_workspace(window, cx);
                            }))
                            .child(
                                Icon::new(IconName::Plus)
                                    .size(px(14.0))
                                    .text_color(palette.text_secondary),
                            )
                            .child(
                                div()
                                    .text_size(px(13.0))
                                    .text_color(palette.text_primary)
                                    .child("New workspace"),
                            ),
                    )
                    // Connection settings…
                    .when_some(active_id.clone(), |el, sid| {
                        el.child(
                            h_flex()
                                .id("ws-flyout-conn-settings")
                                .h(px(34.0))
                                .px_2()
                                .rounded(px(6.0))
                                .items_center()
                                .gap_2p5()
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.subtle_hover))
                                .on_click(cx.listener(move |this, _, window, cx| {
                                    this.open_connection_modal(sid.clone(), window, cx);
                                }))
                                .child(
                                    Icon::new(IconName::Settings)
                                        .size(px(14.0))
                                        .text_color(palette.text_secondary),
                                )
                                .child(
                                    div()
                                        .text_size(px(13.0))
                                        .text_color(palette.text_primary)
                                        .child("Connection settings…"),
                                ),
                        )
                    })
                    // Manage workspaces...
                    .child(
                        h_flex()
                            .id("ws-flyout-manage")
                            .h(px(34.0))
                            .px_2()
                            .rounded(px(6.0))
                            .items_center()
                            .gap_2p5()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.open_manage_workspaces(cx);
                            }))
                            .child(
                                Icon::new(IconName::Folder)
                                    .size(px(14.0))
                                    .text_color(palette.text_secondary),
                            )
                            .child(
                                div()
                                    .text_size(px(13.0))
                                    .text_color(palette.text_primary)
                                    .child("Manage workspaces..."),
                            ),
                    ),
            )
    }

    /// Floating Database Selector Flyout positioned directly below the DB picker row.
    fn render_db_flyout(
        &self,
        active_space: Option<&SpaceState>,
        active_tab: Option<&TabState>,
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let selected_db = active_tab
            .and_then(|t| t.database.as_deref())
            .or_else(|| active_space.and_then(|s| s.active_database.as_deref()));

        div()
            .id("db-flyout-backdrop")
            .absolute()
            .inset_0()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    this.db_dropdown_open = false;
                    cx.notify();
                }),
            )
            .child(
                v_flex()
                    .id("db-flyout-card")
                    .absolute()
                    .top(px(108.0))
                    .left(px(8.0))
                    .w(px(236.0))
                    .max_h(px(260.0))
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
                    .children(space_rt.databases.iter().map(|db| {
                        let db_name = db.name.clone();
                        let is_selected =
                            selected_db.is_some_and(|cur| cur.eq_ignore_ascii_case(&db_name));
                        let accessible = db.is_accessible;
                        h_flex()
                            .id(SharedString::from(format!("db-opt-{db_name}")))
                            .px_2()
                            .py_1p5()
                            .rounded(px(5.0))
                            .items_center()
                            .justify_between()
                            .text_xs()
                            .when(accessible, |d| {
                                d.cursor_pointer()
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.select_database(db_name.clone(), window, cx);
                                    }))
                            })
                            .when(is_selected, |d| {
                                d.bg(palette.accent_soft)
                                    .text_color(palette.text_primary)
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            })
                            .when(!accessible, |d| {
                                d.text_color(palette.text_muted.opacity(0.5))
                            })
                            .child(div().truncate().child(if accessible {
                                db.name.clone()
                            } else {
                                format!("{} (no access)", db.name)
                            }))
                            .when(is_selected, |d| {
                                d.child(
                                    Icon::new(IconName::Check)
                                        .size(px(11.5))
                                        .text_color(palette.accent),
                                )
                            })
                    })),
            )
    }

    /// Floating right-click context menu for query tabs and tab folders/groups in the left rail.
    fn render_rail_context_menu(
        &self,
        menu: RailContextMenu,
        active_space: Option<&SpaceState>,
        palette: StudioPalette,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let Some(space) = active_space else {
            return div().id("rail-ctx-empty").into_any_element();
        };

        let mut items: Vec<AnyElement> = Vec::new();
        let make_row = |id: String,
                        icon: IconName,
                        label: String,
                        danger: bool,
                        palette: StudioPalette| {
            h_flex()
                .id(SharedString::from(id))
                .h(px(30.0))
                .px_2()
                .rounded(px(5.0))
                .items_center()
                .gap_2()
                .cursor_pointer()
                .hover(|s| s.bg(palette.subtle_hover))
                .child(
                    Icon::new(icon)
                        .size(px(13.0))
                        .text_color(if danger {
                            palette.danger
                        } else {
                            palette.text_secondary
                        }),
                )
                .child(
                    div()
                        .text_size(px(12.0))
                        .text_color(if danger {
                            palette.danger
                        } else {
                            palette.text_primary
                        })
                        .truncate()
                        .child(label),
                )
        };
        let make_divider = |palette: StudioPalette| {
            div()
                .my_1()
                .h(px(1.0))
                .w_full()
                .bg(palette.hairline_subtle)
                .into_any_element()
        };

        match menu.target {
            RailContextMenuTarget::Tab { tab_id } => {
                let Some(tab) = space.tabs.iter().find(|t| t.id == tab_id) else {
                    return div().id("rail-ctx-missing-tab").into_any_element();
                };
                let parent_group = tab
                    .group_id
                    .as_deref()
                    .and_then(|gid| space.tab_group(gid));
                let is_grouped = parent_group.is_some();
                let parent_pinned = parent_group.is_some_and(|g| g.is_pinned);

                // Rename
                let tid_rename = tab.id.clone();
                items.push(
                    make_row(
                        "ctx-tab-rename".into(),
                        IconName::Pencil,
                        "Rename".into(),
                        false,
                        palette,
                    )
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.start_rename_tab(tid_rename.clone(), window, cx);
                    }))
                    .into_any_element(),
                );

                // New folder/group from tab
                let tid_new_group = tab.id.clone();
                let new_group_label = if tab.is_pinned || parent_pinned {
                    "New folder from tab"
                } else {
                    "New group from tab"
                };
                items.push(
                    make_row(
                        "ctx-tab-new-group".into(),
                        IconName::FolderPlus,
                        new_group_label.into(),
                        false,
                        palette,
                    )
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.new_group_from_tab(tid_new_group.clone(), window, cx);
                    }))
                    .into_any_element(),
                );

                // Pin / Unpin (targets folder when grouped, or tab when loose)
                if let Some(group) = parent_group {
                    let gid_pin = group.id.clone();
                    let pin_label = if group.is_pinned {
                        "Unpin folder"
                    } else {
                        "Pin folder"
                    };
                    items.push(
                        make_row(
                            "ctx-tab-pin-group".into(),
                            if group.is_pinned {
                                IconName::PinOff
                            } else {
                                IconName::Pin
                            },
                            pin_label.into(),
                            false,
                            palette,
                        )
                        .on_click(cx.listener(move |this, _, _, cx| {
                            this.toggle_group_pin(gid_pin.clone(), cx);
                        }))
                        .into_any_element(),
                    );
                } else {
                    let tid_pin = tab.id.clone();
                    items.push(
                        make_row(
                            "ctx-tab-pin".into(),
                            if tab.is_pinned {
                                IconName::PinOff
                            } else {
                                IconName::Pin
                            },
                            if tab.is_pinned {
                                "Unpin tab".into()
                            } else {
                                "Pin tab".into()
                            },
                            false,
                            palette,
                        )
                        .on_click(cx.listener(move |this, _, _, cx| {
                            this.toggle_pin_tab(tid_pin.clone(), cx);
                        }))
                        .into_any_element(),
                    );
                }

                // Move to group / Remove from group
                let other_groups: Vec<&TabGroupState> = space
                    .groups
                    .iter()
                    .filter(|g| tab.group_id.as_deref() != Some(g.id.as_str()))
                    .collect();
                if is_grouped || !other_groups.is_empty() {
                    items.push(make_divider(palette));
                    if is_grouped {
                        let tid_remove = tab.id.clone();
                        items.push(
                            make_row(
                                "ctx-tab-remove-group".into(),
                                IconName::Ungroup,
                                if parent_pinned {
                                    "Remove from folder".into()
                                } else {
                                    "Remove from group".into()
                                },
                                false,
                                palette,
                            )
                            .on_click(cx.listener(move |this, _, _, cx| {
                                this.move_tab_to_group(
                                    tid_remove.clone(),
                                    None,
                                    Some(parent_pinned),
                                    cx,
                                );
                            }))
                            .into_any_element(),
                        );
                    }
                    if !other_groups.is_empty() {
                        items.push(
                            div()
                                .px_2()
                                .pt_1()
                                .pb_0p5()
                                .text_size(px(9.5))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(palette.text_muted)
                                .child("MOVE TO")
                                .into_any_element(),
                        );
                        for target in other_groups {
                            let tid_move = tab.id.clone();
                            let gid_move = target.id.clone();
                            let icon = if target.is_pinned {
                                IconName::Folder
                            } else {
                                IconName::Layers
                            };
                            items.push(
                                make_row(
                                    format!("ctx-tab-move-{}", target.id),
                                    icon,
                                    target.name.clone(),
                                    false,
                                    palette,
                                )
                                .on_click(cx.listener(move |this, _, _, cx| {
                                    this.move_tab_to_group(
                                        tid_move.clone(),
                                        Some(gid_move.clone()),
                                        None,
                                        cx,
                                    );
                                }))
                                .into_any_element(),
                            );
                        }
                    }
                }

                // Close tab
                items.push(make_divider(palette));
                let tid_close = tab.id.clone();
                items.push(
                    make_row(
                        "ctx-tab-close".into(),
                        IconName::X,
                        "Close tab".into(),
                        false,
                        palette,
                    )
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.archive_tab(tid_close.clone(), window, cx);
                    }))
                    .into_any_element(),
                );
            }
            RailContextMenuTarget::Group { group_id } => {
                let Some(group) = space.tab_group(&group_id) else {
                    return div().id("rail-ctx-missing-group").into_any_element();
                };

                // Rename group/folder
                let gid_rename = group.id.clone();
                items.push(
                    make_row(
                        "ctx-group-rename".into(),
                        IconName::Pencil,
                        "Rename".into(),
                        false,
                        palette,
                    )
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.start_rename_group(gid_rename.clone(), window, cx);
                    }))
                    .into_any_element(),
                );

                // Collapse / Expand
                let gid_toggle = group.id.clone();
                items.push(
                    make_row(
                        "ctx-group-expand".into(),
                        if group.is_expanded {
                            IconName::ChevronRight
                        } else {
                            IconName::ChevronDown
                        },
                        if group.is_expanded {
                            "Collapse".into()
                        } else {
                            "Expand".into()
                        },
                        false,
                        palette,
                    )
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.toggle_group_expanded(gid_toggle.clone(), cx);
                    }))
                    .into_any_element(),
                );

                // Pin / Unpin folder
                let gid_pin = group.id.clone();
                items.push(
                    make_row(
                        "ctx-group-pin".into(),
                        if group.is_pinned {
                            IconName::PinOff
                        } else {
                            IconName::Pin
                        },
                        if group.is_pinned {
                            "Unpin folder".into()
                        } else {
                            "Pin folder".into()
                        },
                        false,
                        palette,
                    )
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.toggle_group_pin(gid_pin.clone(), cx);
                    }))
                    .into_any_element(),
                );

                // Ungroup
                items.push(make_divider(palette));
                let gid_ungroup = group.id.clone();
                items.push(
                    make_row(
                        "ctx-group-ungroup".into(),
                        IconName::Ungroup,
                        "Ungroup".into(),
                        false,
                        palette,
                    )
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.ungroup(gid_ungroup.clone(), cx);
                    }))
                    .into_any_element(),
                );
            }
        }

        let vp = window.viewport_size();
        let menu_w = px(216.0);
        let estimated_h = px((items.len() as f32 * 30.0 + 20.0).min(340.0));
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
            .id("rail-ctx-backdrop")
            .absolute()
            .inset_0()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    this.close_rail_context_menu(cx);
                }),
            )
            .on_mouse_down(
                MouseButton::Right,
                cx.listener(|this, _, _, cx| {
                    this.close_rail_context_menu(cx);
                }),
            )
            .child(
                v_flex()
                    .id("rail-ctx-card")
                    .absolute()
                    .left(left)
                    .top(top)
                    .w(menu_w)
                    .max_h(px(340.0))
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

    /// Full-page Manage Workspaces view matching the WinUI Larik reference screenshot.
    fn render_manage_workspaces_page(
        &self,
        active_space: Option<&SpaceState>,
        palette: StudioPalette,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let workspace_count = self.state.spaces.len();
        let distinct_providers: HashSet<String> = self
            .state
            .spaces
            .iter()
            .map(|s| s.profile.provider_id.to_ascii_lowercase())
            .collect();
        let provider_count = distinct_providers.len();
        let summary_text = format!(
            "{} {} · {} {}",
            workspace_count,
            if workspace_count == 1 {
                "workspace"
            } else {
                "workspaces"
            },
            provider_count,
            if provider_count == 1 {
                "provider"
            } else {
                "providers"
            }
        );

        let coral_bg = rgb(0xF27B5E);
        let coral_hover = rgb(0xF68E74);
        let coral_fg = rgb(0x1C1412);

        v_flex()
            .size_full()
            .overflow_hidden()
            // Top Windows-safe transparent TitleBar: clickable Back arrow + Larik icon + "Workspaces"
            .child(render_app_title_bar(
                "manage-ws",
                IconName::ArrowLeft,
                &self.app_icon,
                "Workspaces",
                palette,
                window,
                cx,
                cx.listener(|this, _, _, cx| {
                    this.close_manage_workspaces(cx);
                }),
            ))
            // Centered workspace management canvas
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .flex()
                    .justify_center()
                    .px_8()
                    .pt_4()
                    .pb_6()
                    .child(
                        v_flex()
                            .w_full()
                            .max_w(px(940.0))
                            .h_full()
                            .gap_3()
                            // Header row: count summary + coral "+ New workspace" CTA
                            .child(
                                h_flex()
                                    .items_center()
                                    .justify_between()
                                    .child(
                                        div()
                                            .text_size(px(12.0))
                                            .text_color(palette.text_muted)
                                            .child(summary_text),
                                    )
                                    .child(
                                        h_flex()
                                            .id("manage-ws-new-btn")
                                            .h(px(32.0))
                                            .px_3p5()
                                            .rounded(px(6.0))
                                            .bg(coral_bg)
                                            .hover(|s| s.bg(coral_hover))
                                            .items_center()
                                            .gap_1p5()
                                            .cursor_pointer()
                                            .on_click(cx.listener(|this, _, window, cx| {
                                                this.create_workspace(window, cx);
                                            }))
                                            .child(
                                                Icon::new(IconName::Plus)
                                                    .size(px(13.0))
                                                    .text_color(coral_fg),
                                            )
                                            .child(
                                                div()
                                                    .text_size(px(12.5))
                                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                    .text_color(coral_fg)
                                                    .child("New workspace"),
                                            ),
                                    ),
                            )
                            // Main translucent WinUI workspace list card
                            .child(
                                div()
                                    .id("manage-ws-card")
                                    .flex_1()
                                    .min_h_0()
                                    .rounded(px(10.0))
                                    .bg(palette.card_bg)
                                    .border_1()
                                    .border_color(palette.card_border)
                                    .overflow_y_scroll()
                                    .py_1()
                                    .child(v_flex().children(
                                        self.state.spaces.iter().enumerate().map(|(idx, space)| {
                                            let is_active =
                                                active_space.is_some_and(|a| a.id == space.id);
                                            let is_hidden = space.is_hidden;
                                            let provider =
                                                self.catalog.find(&space.profile.provider_id);
                                            let is_unverified = provider
                                                .as_ref()
                                                .and_then(|p| p.transport_warning(&space.profile))
                                                .is_some();
                                            let can_move_up = idx > 0;
                                            let can_move_down = idx + 1 < workspace_count;

                                            let sid_open = space.id.clone();
                                            let sid_up = space.id.clone();
                                            let sid_down = space.id.clone();
                                            let sid_hide = space.id.clone();
                                            let sid_edit = space.id.clone();

                                            let initial = workspace_monogram(&space.name);
                                            let custom_icon =
                                                workspace_icon_name(space.icon.as_deref());
                                            let tint = parse_hex_color(&space.color);

                                            let mut sub_parts = Vec::new();
                                            sub_parts.push(
                                                provider
                                                    .as_ref()
                                                    .map(|p| p.display_name().to_string())
                                                    .unwrap_or_else(|| {
                                                        space.profile.provider_id.clone()
                                                    }),
                                            );
                                            if let Some(ep) = provider
                                                .as_ref()
                                                .and_then(|p| p.describe_endpoint(&space.profile))
                                            {
                                                if !ep.is_empty() {
                                                    sub_parts.push(ep);
                                                }
                                            }
                                            if let Some(id_str) = provider
                                                .as_ref()
                                                .and_then(|p| p.describe_identity(&space.profile))
                                            {
                                                if !id_str.is_empty() {
                                                    sub_parts.push(id_str);
                                                }
                                            }
                                            let subtitle = sub_parts.join(" · ");

                                            h_flex()
                                                .id(SharedString::from(format!(
                                                    "manage-ws-row-{}",
                                                    space.id
                                                )))
                                                .px_3p5()
                                                .py_3()
                                                .items_center()
                                                .justify_between()
                                                .gap_3()
                                                .cursor_pointer()
                                                .when(idx > 0, |el| {
                                                    el.border_t_1()
                                                        .border_color(palette.hairline_subtle)
                                                })
                                                .hover(|s| s.bg(palette.subtle_hover))
                                                .on_click(cx.listener(
                                                    move |this, _, window, cx| {
                                                        this.switch_space(
                                                            sid_open.clone(),
                                                            window,
                                                            cx,
                                                        );
                                                        this.close_manage_workspaces(cx);
                                                    },
                                                ))
                                                // Left group: reorder controls + 30x30 icon/monogram + title/subtitle
                                                .child(
                                                    h_flex()
                                                        .flex_1()
                                                        .min_w_0()
                                                        .items_center()
                                                        .gap_3()
                                                        .child(
                                                            h_flex()
                                                                .items_center()
                                                                .gap_0p5()
                                                                .on_mouse_down(
                                                                    MouseButton::Left,
                                                                    |_, _, cx| {
                                                                        cx.stop_propagation();
                                                                    },
                                                                )
                                                                .child(
                                                                    Icon::new(
                                                                        IconName::GripVertical,
                                                                    )
                                                                    .size(px(13.0))
                                                                    .text_color(
                                                                        palette
                                                                            .text_muted
                                                                            .opacity(0.6),
                                                                    ),
                                                                )
                                                                .when(workspace_count > 1, |el| {
                                                                    el.child(
                                                                        v_flex()
                                                                            .child(
                                                                                div()
                                                                                    .id(SharedString::from(format!("ws-up-{}", space.id)))
                                                                                    .size(px(14.0))
                                                                                    .rounded(px(3.0))
                                                                                    .flex()
                                                                                    .items_center()
                                                                                    .justify_center()
                                                                                    .when(can_move_up, |b| {
                                                                                        b.cursor_pointer()
                                                                                            .hover(|s| s.bg(palette.subtle_fill))
                                                                                            .on_click(cx.listener(move |this, _, _, cx| {
                                                                                                this.move_workspace(sid_up.clone(), -1, cx);
                                                                                            }))
                                                                                    })
                                                                                    .child(
                                                                                        Icon::new(IconName::ChevronUp)
                                                                                            .size(px(10.0))
                                                                                            .text_color(if can_move_up {
                                                                                                palette.text_muted
                                                                                            } else {
                                                                                                palette.text_muted.opacity(0.25)
                                                                                            }),
                                                                                    ),
                                                                            )
                                                                            .child(
                                                                                div()
                                                                                    .id(SharedString::from(format!("ws-down-{}", space.id)))
                                                                                    .size(px(14.0))
                                                                                    .rounded(px(3.0))
                                                                                    .flex()
                                                                                    .items_center()
                                                                                    .justify_center()
                                                                                    .when(can_move_down, |b| {
                                                                                        b.cursor_pointer()
                                                                                            .hover(|s| s.bg(palette.subtle_fill))
                                                                                            .on_click(cx.listener(move |this, _, _, cx| {
                                                                                                this.move_workspace(sid_down.clone(), 1, cx);
                                                                                            }))
                                                                                    })
                                                                                    .child(
                                                                                        Icon::new(IconName::ChevronDown)
                                                                                            .size(px(10.0))
                                                                                            .text_color(if can_move_down {
                                                                                                palette.text_muted
                                                                                            } else {
                                                                                                palette.text_muted.opacity(0.25)
                                                                                            }),
                                                                                    ),
                                                                            ),
                                                                    )
                                                                }),
                                                        )
                                                        .child(
                                                            div()
                                                                .size(px(30.0))
                                                                .flex_shrink_0()
                                                                .rounded(px(6.0))
                                                                .bg(tint.opacity(0.26))
                                                                .border_1()
                                                                .border_color(tint.opacity(0.35))
                                                                .flex()
                                                                .items_center()
                                                                .justify_center()
                                                                .text_size(px(12.5))
                                                                .font_weight(
                                                                    gpui_kit::FontWeight::SEMIBOLD,
                                                                )
                                                                .text_color(palette.text_primary)
                                                                .child(match custom_icon {
                                                                    Some(icon) => Icon::new(icon)
                                                                        .size(px(14.0))
                                                                        .text_color(
                                                                            palette.text_primary,
                                                                        )
                                                                        .into_any_element(),
                                                                    None => initial.into_any_element(),
                                                                }),
                                                        )
                                                        .child(
                                                            v_flex()
                                                                .flex_1()
                                                                .min_w_0()
                                                                .gap_0p5()
                                                                .child(
                                                                    div()
                                                                        .text_size(px(13.0))
                                                                        .font_weight(
                                                                            gpui_kit::FontWeight::SEMIBOLD,
                                                                        )
                                                                        .text_color(if is_hidden {
                                                                            palette.text_muted
                                                                        } else {
                                                                            palette.text_primary
                                                                        })
                                                                        .truncate()
                                                                        .child(space.name.clone()),
                                                                )
                                                                .child(
                                                                    div()
                                                                        .text_size(px(11.0))
                                                                        .text_color(
                                                                            palette.text_muted,
                                                                        )
                                                                        .truncate()
                                                                        .child(subtitle),
                                                                ),
                                                        ),
                                                )
                                                // Right group: status badges + Hide/Show + Edit
                                                .child(
                                                    h_flex()
                                                        .flex_shrink_0()
                                                        .items_center()
                                                        .gap_2()
                                                        .when(is_active, |el| {
                                                            el.child(
                                                                div()
                                                                    .px_2()
                                                                    .py(px(2.0))
                                                                    .rounded(px(4.0))
                                                                    .bg(palette.accent_soft)
                                                                    .text_size(px(9.5))
                                                                    .font_weight(
                                                                        gpui_kit::FontWeight::BOLD,
                                                                    )
                                                                    .text_color(palette.accent)
                                                                    .child("OPEN"),
                                                            )
                                                        })
                                                        .when(is_hidden, |el| {
                                                            el.child(
                                                                div()
                                                                    .px_2()
                                                                    .py(px(2.0))
                                                                    .rounded(px(4.0))
                                                                    .bg(palette.subtle_fill)
                                                                    .text_size(px(9.5))
                                                                    .font_weight(
                                                                        gpui_kit::FontWeight::BOLD,
                                                                    )
                                                                    .text_color(palette.text_muted)
                                                                    .child("HIDDEN"),
                                                            )
                                                        })
                                                        .when(is_unverified, |el| {
                                                            el.child(
                                                                div()
                                                                    .px_2()
                                                                    .py(px(2.0))
                                                                    .rounded(px(4.0))
                                                                    .bg(palette
                                                                        .warning
                                                                        .opacity(0.18))
                                                                    .text_size(px(9.5))
                                                                    .font_weight(
                                                                        gpui_kit::FontWeight::BOLD,
                                                                    )
                                                                    .text_color(palette.warning)
                                                                    .child("UNVERIFIED"),
                                                            )
                                                        })
                                                        .child(
                                                            div()
                                                                .id(SharedString::from(format!(
                                                                    "manage-ws-hide-{}",
                                                                    space.id
                                                                )))
                                                                .px_2()
                                                                .py_1()
                                                                .rounded(px(5.0))
                                                                .cursor_pointer()
                                                                .text_size(px(12.0))
                                                                .text_color(palette.text_secondary)
                                                                .hover(|s| {
                                                                    s.bg(palette.subtle_fill)
                                                                        .text_color(
                                                                            palette.text_primary,
                                                                        )
                                                                })
                                                                .on_mouse_down(
                                                                    MouseButton::Left,
                                                                    |_, _, cx| {
                                                                        cx.stop_propagation();
                                                                    },
                                                                )
                                                                .on_click(cx.listener(
                                                                    move |this, _, _, cx| {
                                                                        this.toggle_hide_workspace(
                                                                            sid_hide.clone(),
                                                                            cx,
                                                                        );
                                                                    },
                                                                ))
                                                                .child(if is_hidden {
                                                                    "Show"
                                                                } else {
                                                                    "Hide"
                                                                }),
                                                        )
                                                        .child(
                                                            div()
                                                                .id(SharedString::from(format!(
                                                                    "manage-ws-edit-{}",
                                                                    space.id
                                                                )))
                                                                .px_2()
                                                                .py_1()
                                                                .rounded(px(5.0))
                                                                .cursor_pointer()
                                                                .text_size(px(12.0))
                                                                .text_color(palette.text_secondary)
                                                                .hover(|s| {
                                                                    s.bg(palette.subtle_fill)
                                                                        .text_color(
                                                                            palette.text_primary,
                                                                        )
                                                                })
                                                                .on_mouse_down(
                                                                    MouseButton::Left,
                                                                    |_, _, cx| {
                                                                        cx.stop_propagation();
                                                                    },
                                                                )
                                                                .on_click(cx.listener(
                                                                    move |this, _, window, cx| {
                                                                        this.open_connection_modal(
                                                                            sid_edit.clone(),
                                                                            window,
                                                                            cx,
                                                                        );
                                                                    },
                                                                ))
                                                                .child("Edit"),
                                                        ),
                                                )
                                        }),
                                    )),
                            )
                            // Bottom helper notes (matching WinUI screenshot)
                            .child(
                                v_flex()
                                    .pt_1()
                                    .gap_1()
                                    .text_size(px(11.0))
                                    .text_color(palette.text_muted)
                                    .child("Reorder rows to set the order used by the switcher and the rail footer. Hidden workspaces keep their place in that order.")
                                    .child("Hiding the open workspace keeps it open; it leaves the rail after you switch away.")
                                    .child("Delete stays inside workspace settings, confirmed, and archived tabs survive it."),
                            ),
                    ),
            )
    }

    fn render_group_header_row(
        &self,
        space_id: &str,
        group: &TabGroupState,
        member_count: usize,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let space_id_over = space_id.to_string();
        let space_id_drop = space_id.to_string();
        let group_id_click = group.id.clone();
        let group_id_ctx = group.id.clone();
        let group_id_drop = group.id.clone();
        let group_id_pin = group.id.clone();
        let is_pinned = group.is_pinned;
        let is_expanded = group.is_expanded;
        let is_renaming = self.renaming_group_id.as_deref() == Some(group.id.as_str());
        let group_accent = group
            .color
            .as_deref()
            .map(parse_hex_color)
            .unwrap_or(space_color);
        let folder_icon = if is_pinned {
            if is_expanded {
                IconName::FolderOpen
            } else {
                IconName::Folder
            }
        } else {
            IconName::Layers
        };
        let folder_color = if is_pinned {
            group_accent
        } else {
            palette.text_secondary
        };

        h_flex()
            .id(SharedString::from(format!("group-row-{}", group.id)))
            .group("group-row")
            .px_2()
            .py(px(5.0))
            .rounded(px(6.0))
            .items_center()
            .gap_1p5()
            .cursor_pointer()
            .hover(|s| s.bg(palette.subtle_hover))
            // Drag a tab onto a folder/group header to move it into this group
            .drag_over::<DraggedQueryTab>(move |style, dragged, _, _| {
                if dragged.space_id == space_id_over {
                    style
                        .border_1()
                        .border_color(space_color)
                        .bg(palette.accent_soft)
                } else {
                    style
                }
            })
            .on_drop(cx.listener(move |this, dragged: &DraggedQueryTab, _, cx| {
                if dragged.space_id == space_id_drop {
                    this.drop_tab_on_group(&dragged.tab_id, &group_id_drop, cx);
                }
            }))
            // Right-click opens group/folder context menu
            .on_mouse_down(
                MouseButton::Right,
                cx.listener(move |this, event: &MouseDownEvent, _, cx| {
                    cx.stop_propagation();
                    this.open_group_context_menu(group_id_ctx.clone(), event.position, cx);
                }),
            )
            // Single click toggles expand/collapse; double-click enters inline rename
            .on_click(cx.listener(move |this, event: &ClickEvent, window, cx| {
                if event.click_count() >= 2 {
                    this.start_rename_group(group_id_click.clone(), window, cx);
                } else {
                    this.toggle_group_expanded(group_id_click.clone(), cx);
                }
            }))
            // Expand/collapse chevron
            .child(
                Icon::new(if is_expanded {
                    IconName::ChevronDown
                } else {
                    IconName::ChevronRight
                })
                .size(px(11.0))
                .text_color(palette.text_muted),
            )
            // Folder (pinned) or group (unpinned) icon
            .child(
                Icon::new(folder_icon)
                    .size(px(12.5))
                    .text_color(folder_color),
            )
            // Name or inline rename input
            .child(if is_renaming {
                if let Some(input) = self.rename_group_input.as_ref() {
                    div()
                        .flex_1()
                        .min_w_0()
                        .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                        .child(Input::new(input).xsmall())
                        .into_any_element()
                } else {
                    div().flex_1().into_any_element()
                }
            } else {
                div()
                    .flex_1()
                    .min_w_0()
                    .text_size(px(12.0))
                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                    .text_color(palette.text_primary)
                    .truncate()
                    .child(group.name.clone())
                    .into_any_element()
            })
            // Member count badge
            .when(!is_renaming, |el| {
                el.child(
                    div()
                        .px_1()
                        .rounded(px(4.0))
                        .text_size(px(10.0))
                        .text_color(palette.text_muted)
                        .child(member_count.to_string()),
                )
            })
            // Pin / unpin folder button (always visible when pinned; hover otherwise)
            .when(!is_renaming, |el| {
                el.child(
                    div()
                        .id(SharedString::from(format!("pin-group-{}", group.id)))
                        .size(px(20.0))
                        .flex_shrink_0()
                        .rounded(px(4.0))
                        .flex()
                        .items_center()
                        .justify_center()
                        .when(!is_pinned, |d| {
                            d.invisible().group_hover("group-row", |s| s.visible())
                        })
                        .hover(|s| s.bg(palette.subtle_hover))
                        .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                        .on_click(cx.listener(move |this, _, _, cx| {
                            cx.stop_propagation();
                            this.toggle_group_pin(group_id_pin.clone(), cx);
                        }))
                        .child(
                            Icon::new(IconName::Pin)
                                .size(px(10.5))
                                .text_color(if is_pinned {
                                    group_accent
                                } else {
                                    palette.text_muted
                                }),
                        ),
                )
            })
    }

    fn render_tab_row(
        &self,
        space_id: &str,
        tab: &TabState,
        is_active: bool,
        in_group: bool,
        is_pinned_band: bool,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let space_id_owned = space_id.to_string();
        let tab_id_click = tab.id.clone();
        let tab_id_mid = tab.id.clone();
        let tab_id_ctx = tab.id.clone();
        let tab_id_pin = tab.id.clone();
        let tab_id_close = tab.id.clone();
        let tab_id_drag = tab.id.clone();
        let tab_id_drop = tab.id.clone();
        let tab_id_over = tab.id.clone();
        let space_id_over = space_id_owned.clone();
        let space_id_drop = space_id_owned.clone();
        let is_pinned = tab.is_pinned;
        let is_running = self
            .tab_runtime
            .get(&tab.id)
            .is_some_and(|rt| rt.running);
        let is_renaming = self.renaming_tab_id.as_deref() == Some(tab.id.as_str());

        h_flex()
            .id(SharedString::from(format!("tab-row-{}", tab.id)))
            .group("tab-row")
            .relative()
            .when(in_group, |el| el.ml(px(14.0)))
            .pl_3()
            .pr_1p5()
            .py(if is_pinned_band { px(5.0) } else { px(7.0) })
            .rounded(px(6.0))
            .items_center()
            .gap_1p5()
            .cursor_pointer()
            .when(is_active, |el| {
                el.bg(palette.tab_active_bg)
                    .border_1()
                    .border_color(palette.card_border)
            })
            .when(!is_active, |el| el.hover(|s| s.bg(palette.subtle_hover)))
            // Drag-and-drop reorder / move into or out of folders & pinned band
            .when(!is_renaming, |el| {
                let drag_payload = DraggedQueryTab {
                    space_id: space_id_owned,
                    tab_id: tab_id_drag,
                    title: SharedString::from(tab.title.clone()),
                };
                el.on_drag(drag_payload, move |dragged, _, _, cx| {
                    cx.new(|_| DragGhostView {
                        label: dragged.title.clone(),
                        accent: space_color,
                        bg: palette.modal_bg,
                        border: palette.card_border,
                        text: palette.text_primary,
                    })
                })
                .drag_over::<DraggedQueryTab>(move |style, dragged, _, _| {
                    if dragged.space_id == space_id_over && dragged.tab_id != tab_id_over {
                        style
                            .border_t_2()
                            .border_color(space_color)
                            .bg(palette.subtle_hover)
                    } else {
                        style
                    }
                })
                .on_drop(cx.listener(move |this, dragged: &DraggedQueryTab, _, cx| {
                    if dragged.space_id == space_id_drop && dragged.tab_id != tab_id_drop {
                        this.drop_tab_on_tab(&dragged.tab_id, &tab_id_drop, cx);
                    }
                }))
            })
            // Right-click opens tab context menu (Rename, New folder/group, Move to, Pin, Close)
            .on_mouse_down(
                MouseButton::Right,
                cx.listener(move |this, event: &MouseDownEvent, _, cx| {
                    cx.stop_propagation();
                    this.open_tab_context_menu(tab_id_ctx.clone(), event.position, cx);
                }),
            )
            // Middle-click archives tab immediately
            .on_mouse_down(
                MouseButton::Middle,
                cx.listener(move |this, _, window, cx| {
                    cx.stop_propagation();
                    this.archive_tab(tab_id_mid.clone(), window, cx);
                }),
            )
            // Single click switches tab; double-click enters inline rename
            .on_click(cx.listener(move |this, event: &ClickEvent, window, cx| {
                if event.click_count() >= 2 {
                    this.start_rename_tab(tab_id_click.clone(), window, cx);
                } else {
                    this.switch_tab(tab_id_click.clone(), window, cx);
                }
            }))
            // Subtle WinUI 2.5px left accent pill on active tab
            .when(is_active, |el| {
                el.child(
                    div()
                        .absolute()
                        .left(px(1.5))
                        .top(px(7.0))
                        .bottom(px(7.0))
                        .w(px(2.5))
                        .rounded_full()
                        .bg(space_color),
                )
            })
            // Running indicator (only when actively executing)
            .when(is_running, |el| el.child(Spinner::new().xsmall()))
            // Clean Title + DB subtitle (no left file icon clutter, matching WinUI screenshot)
            .child(if is_renaming {
                if let Some(input) = self.rename_input.as_ref() {
                    div()
                        .flex_1()
                        .min_w_0()
                        .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                        .child(Input::new(input).xsmall())
                        .into_any_element()
                } else {
                    div().flex_1().into_any_element()
                }
            } else {
                v_flex()
                    .flex_1()
                    .min_w_0()
                    .gap(px(1.0))
                    .child(
                        div()
                            .text_size(px(12.0))
                            .font_weight(if is_active {
                                gpui_kit::FontWeight::SEMIBOLD
                            } else {
                                gpui_kit::FontWeight::NORMAL
                            })
                            .text_color(if is_active {
                                palette.text_primary
                            } else {
                                palette.text_secondary
                            })
                            .truncate()
                            .child(tab.title.clone()),
                    )
                    .when(is_active, |col| {
                        col.when_some(tab.database.clone(), |el, db| {
                            el.child(
                                div()
                                    .text_size(px(10.5))
                                    .text_color(palette.text_muted)
                                    .truncate()
                                    .child(db),
                            )
                        })
                    })
                    .into_any_element()
            })
            // Pin toggle on loose tabs (pinning for grouped tabs lives on the folder header)
            .when(!is_renaming && !in_group, |el| {
                el.child(
                    div()
                        .id(SharedString::from(format!("pin-{}", tab.id)))
                        .size(px(20.0))
                        .flex_shrink_0()
                        .rounded(px(4.0))
                        .flex()
                        .items_center()
                        .justify_center()
                        .when(!is_pinned, |d| {
                            d.invisible().group_hover("tab-row", |s| s.visible())
                        })
                        .hover(|s| s.bg(palette.subtle_hover))
                        .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                        .on_click(cx.listener(move |this, _, _, cx| {
                            cx.stop_propagation();
                            this.toggle_pin_tab(tab_id_pin.clone(), cx);
                        }))
                        .child(
                            Icon::new(IconName::Pin)
                                .size(px(10.5))
                                .text_color(if is_pinned {
                                    space_color
                                } else {
                                    palette.text_muted
                                }),
                        ),
                )
            })
            // Close button (visible on active tab or on hover)
            .when(!is_renaming, |el| {
                el.child(
                    div()
                        .id(SharedString::from(format!("close-{}", tab.id)))
                        .size(px(20.0))
                        .flex_shrink_0()
                        .rounded(px(4.0))
                        .flex()
                        .items_center()
                        .justify_center()
                        .when(!is_active, |d| {
                            d.invisible().group_hover("tab-row", |s| s.visible())
                        })
                        .hover(|s| s.bg(palette.subtle_hover))
                        .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
                        .on_click(cx.listener(move |this, _, window, cx| {
                            cx.stop_propagation();
                            this.archive_tab(tab_id_close.clone(), window, cx);
                        }))
                        .child(
                            Icon::new(IconName::X)
                                .size(px(11.5))
                                .text_color(palette.text_muted),
                        ),
                )
            })
    }

    fn render_main_surface(
        &self,
        active_space: Option<&SpaceState>,
        active_tab: Option<&TabState>,
        space_rt: &SpaceRuntime,
        tab_rt: &TabRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let space_color = active_space
            .map(|s| parse_hex_color(&s.color))
            .unwrap_or(palette.accent);
        let is_running = tab_rt.running;
        let conn_state = space_rt.connection_state();
        let status_dot = match conn_state {
            ConnectionState::Connected => palette.success,
            ConnectionState::Connecting => palette.warning,
            ConnectionState::Faulted => palette.danger,
            ConnectionState::Disconnected => palette.text_muted,
        };
        let active_db = active_tab
            .and_then(|t| t.database.as_deref())
            .or_else(|| active_space.and_then(|s| s.active_database.as_deref()));
        let tab_title = active_tab
            .map(|t| t.title.clone())
            .unwrap_or_else(|| "Query 1".into());
        let has_results = !tab_rt.results.is_empty();
        let show_results = has_results && !tab_rt.results_dismissed;

        v_flex()
            .flex_1()
            .min_h_0()
            .min_w_0()
            .w_full()
            .overflow_hidden()
            // WinUI Query Toolbar: Left = Tab Title (+ collapsed ContextBadge), Right = Compact Icon Buttons + Accent Run
            .child(
                h_flex()
                    .h(px(38.0))
                    .pl_4()
                    .pr_3p5()
                    .pb_1()
                    .items_center()
                    .justify_between()
                    .gap_3()
                    .child(
                        h_flex()
                            .items_center()
                            .gap_2p5()
                            .min_w_0()
                            .child(
                                div()
                                    .text_size(px(13.5))
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                    .text_color(palette.text_primary)
                                    .truncate()
                                    .child(tab_title),
                            )
                            // Calm ContextBadge pill only when sidebar is collapsed
                            .when(!self.state.rail_open, |el| {
                                let space_name = active_space
                                    .map(|s| s.name.clone())
                                    .unwrap_or_else(|| "Workspace".into());
                                let db_label = active_db.map(|d| d.to_string());
                                el.child(
                                    h_flex()
                                        .id("toolbar-collapsed-context")
                                        .px_2()
                                        .py(px(2.5))
                                        .rounded(px(5.0))
                                        .bg(palette.card_bg)
                                        .border_1()
                                        .border_color(palette.card_border)
                                        .items_center()
                                        .gap_1p5()
                                        .cursor_pointer()
                                        .hover(|s| s.bg(palette.subtle_hover))
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.toggle_rail(cx);
                                        }))
                                        .child(
                                            div()
                                                .size(px(6.0))
                                                .rounded_full()
                                                .bg(status_dot),
                                        )
                                        .child(
                                            div()
                                                .text_xs()
                                                .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                .text_color(palette.text_secondary)
                                                .child(space_name),
                                        )
                                        .when_some(db_label, |chip, db| {
                                            chip.child(
                                                div()
                                                    .text_xs()
                                                    .text_color(palette.text_muted)
                                                    .child(format!("· {db}")),
                                            )
                                        }),
                                )
                            }),
                    )
                    // Right icon cluster matching WinUI SqlEditorView.xaml
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1p5()
                            .when(is_running, |el| {
                                el.child(
                                    h_flex()
                                        .px_2()
                                        .py(px(3.0))
                                        .rounded(px(5.0))
                                        .bg(palette.warning.opacity(0.14))
                                        .gap_1p5()
                                        .items_center()
                                        .child(Spinner::new().xsmall())
                                        .child(
                                            div()
                                                .text_xs()
                                                .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                .text_color(palette.warning)
                                                .child("Running…"),
                                        ),
                                )
                            })
                            // Toggle results/messages drawer
                            .child(
                                Button::new("toolbar-toggle-results")
                                    .ghost()
                                    .xsmall()
                                    .w(px(30.0))
                                    .h(px(28.0))
                                    .icon(IconName::MessageSquare)
                                    .disabled(!has_results)
                                    .tooltip(if show_results {
                                        "Hide results pane"
                                    } else {
                                        "Show results pane"
                                    })
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.toggle_results_pane(cx);
                                    })),
                            )
                            // Run all statements (F5)
                            .child(
                                Button::new("toolbar-run-all")
                                    .ghost()
                                    .xsmall()
                                    .w(px(30.0))
                                    .h(px(28.0))
                                    .icon(IconName::FastForward)
                                    .disabled(is_running)
                                    .tooltip("Run entire script (F5)")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.run_query(false, false, cx);
                                    })),
                            )
                            // Primary Fluent Accent Run / Stop Icon Button (32x28px rounded 6px)
                            .child(if is_running {
                                div()
                                    .id("toolbar-stop")
                                    .w(px(32.0))
                                    .h(px(28.0))
                                    .rounded(px(6.0))
                                    .bg(palette.danger)
                                    .hover(|s| s.bg(palette.danger.opacity(0.88)))
                                    .cursor_pointer()
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.cancel_active_query(cx);
                                    }))
                                    .child(
                                        Icon::new(IconName::Square)
                                            .size(px(12.0))
                                            .text_color(gpui_kit::rgb(0xFFFFFF)),
                                    )
                                    .into_any_element()
                            } else {
                                div()
                                    .id("toolbar-run")
                                    .w(px(32.0))
                                    .h(px(28.0))
                                    .rounded(px(6.0))
                                    .bg(palette.accent)
                                    .hover(|s| s.bg(palette.accent_hover))
                                    .cursor_pointer()
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.run_query(true, false, cx);
                                    }))
                                    .child(
                                        Icon::new(IconName::Play)
                                            .size(px(13.0))
                                            .text_color(palette.accent_on),
                                    )
                                    .into_any_element()
                            }),
                    ),
            )
            // WinUI Rounded Cards Area: Editor Card fills 100% until Results Card is open
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .px_3()
                    .pb_2()
                    .child(if show_results {
                        v_resizable("editor-results-split")
                            .child(
                                resizable_panel().size(px(380.0)).child(
                                    div().size_full().pb_1p5().child(
                                        self.render_editor_card(is_running, palette),
                                    ),
                                ),
                            )
                            .child(
                                resizable_panel().size(px(320.0)).child(
                                    div().size_full().pt_1p5().child(
                                        self.render_results_pane(
                                            active_tab.map(|t| t.id.as_str()),
                                            tab_rt,
                                            space_color,
                                            palette,
                                            cx,
                                        ),
                                    ),
                                ),
                            )
                            .into_any_element()
                    } else {
                        self.render_editor_card(is_running, palette)
                            .into_any_element()
                    }),
            )
    }

    fn render_editor_card(&self, is_running: bool, palette: StudioPalette) -> impl IntoElement {
        div()
            .relative()
            .size_full()
            .rounded(px(8.0))
            .bg(palette.card_bg)
            .border_1()
            .border_color(palette.card_border)
            .overflow_hidden()
            .child(
                Editor::new(&self.editor_state)
                    .appearance(false)
                    .bordered(false)
                    .h_full(),
            )
            .when(is_running, |el| {
                el.child(
                    div()
                        .absolute()
                        .left_0()
                        .right_0()
                        .top_0()
                        .h(px(2.0))
                        .bg(palette.accent),
                )
            })
    }

    fn render_results_pane(
        &self,
        tab_id: Option<&str>,
        tab_rt: &TabRuntime,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let active_item = tab_rt.active_result();
        let active_result = active_item.map(|item| item.result.as_ref());
        let active_result_id = tab_rt.active_result_id.as_deref();
        let grid_message_count: usize = tab_rt
            .results
            .iter()
            .filter(|item| !item.is_messages_only())
            .map(|item| item.result.messages.len())
            .sum();
        let has_grid = !tab_rt.show_messages
            && active_result.is_some_and(|r| !r.is_error && !r.columns.is_empty());

        v_flex()
            .size_full()
            .rounded(px(8.0))
            .bg(palette.card_bg)
            .border_1()
            .border_color(palette.card_border)
            .overflow_hidden()
            // Quiet Fluent Result Tabs Bar (supports rename, close, and drag-and-drop reorder)
            .child(
                h_flex()
                    .h(px(36.0))
                    .px_3()
                    .border_b_1()
                    .border_color(palette.hairline_subtle)
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .id("res-tabs-strip")
                            .flex_1()
                            .min_w_0()
                            .h_full()
                            .overflow_x_scroll()
                            .items_center()
                            .gap_1()
                            .children(tab_rt.results.iter().map(|item| {
                                let selected = !tab_rt.show_messages
                                    && active_result_id == Some(item.id.as_str());
                                let is_err = item.result.is_error;
                                let is_renaming = tab_id.is_some_and(|tid| {
                                    self.renaming_result.as_ref().is_some_and(|(rtid, rid)| {
                                        rtid == tid && rid == &item.id
                                    })
                                });
                                let detail = item.detail_label();
                                let show_detail = !detail.is_empty()
                                    && !detail.eq_ignore_ascii_case(&item.title);
                                let item_id_click = item.id.clone();
                                let item_id_mid = item.id.clone();
                                let item_id_close = item.id.clone();
                                let tab_id_for_rename = tab_id.map(str::to_string);
                                let tab_id_for_drag = tab_id.map(str::to_string);
                                let item_id_drag = item.id.clone();
                                let item_id_over = item.id.clone();
                                let item_id_drop = item.id.clone();
                                let item_title_drag = SharedString::from(item.title.clone());

                                h_flex()
                                    .id(SharedString::from(format!("res-tab-{}", item.id)))
                                    .group("res-tab")
                                    .relative()
                                    .flex_shrink_0()
                                    .h_full()
                                    .pl_2p5()
                                    .pr_1p5()
                                    .items_center()
                                    .gap_1p5()
                                    .cursor_pointer()
                                    .when(!selected && !is_renaming, |d| {
                                        d.hover(|s| s.bg(palette.subtle_hover))
                                    })
                                    // Drag-and-drop reorder for result tabs within this query tab
                                    .when(!is_renaming && tab_id_for_drag.is_some(), |el| {
                                        let tid = tab_id_for_drag.clone().unwrap_or_default();
                                        let tid_over = tid.clone();
                                        let tid_drop = tid.clone();
                                        let drag_payload = DraggedResultTab {
                                            tab_id: tid,
                                            result_id: item_id_drag,
                                            title: item_title_drag,
                                        };
                                        el.on_drag(drag_payload, move |dragged, _, _, cx| {
                                            cx.new(|_| DragGhostView {
                                                label: dragged.title.clone(),
                                                accent: if is_err {
                                                    palette.danger
                                                } else {
                                                    space_color
                                                },
                                                bg: palette.modal_bg,
                                                border: palette.card_border,
                                                text: palette.text_primary,
                                            })
                                        })
                                        .drag_over::<DraggedResultTab>(
                                            move |style, dragged, _, _| {
                                                if dragged.tab_id == tid_over
                                                    && dragged.result_id != item_id_over
                                                {
                                                    style
                                                        .border_l_2()
                                                        .border_color(space_color)
                                                        .bg(palette.subtle_hover)
                                                } else {
                                                    style
                                                }
                                            },
                                        )
                                        .on_drop(cx.listener(
                                            move |this, dragged: &DraggedResultTab, _, cx| {
                                                if dragged.tab_id == tid_drop
                                                    && dragged.result_id != item_id_drop
                                                {
                                                    this.reorder_result_tab(
                                                        &dragged.tab_id,
                                                        &dragged.result_id,
                                                        &item_id_drop,
                                                        cx,
                                                    );
                                                }
                                            },
                                        ))
                                    })
                                    // Middle-click closes this result tab
                                    .on_mouse_down(
                                        MouseButton::Middle,
                                        cx.listener(move |this, _, _, cx| {
                                            cx.stop_propagation();
                                            this.close_result_tab(item_id_mid.clone(), cx);
                                        }),
                                    )
                                    // Single-click selects result tab; double-click enters inline rename
                                    .on_click(cx.listener(
                                        move |this, event: &ClickEvent, window, cx| {
                                            if event.click_count() >= 2 {
                                                if let Some(tid) = tab_id_for_rename.clone() {
                                                    this.select_result_tab(
                                                        item_id_click.clone(),
                                                        cx,
                                                    );
                                                    this.start_rename_result_tab(
                                                        tid,
                                                        item_id_click.clone(),
                                                        window,
                                                        cx,
                                                    );
                                                }
                                            } else {
                                                this.select_result_tab(item_id_click.clone(), cx);
                                            }
                                        },
                                    ))
                                    .when(is_err, |d| {
                                        d.child(
                                            div()
                                                .size(px(6.0))
                                                .rounded_full()
                                                .bg(palette.danger),
                                        )
                                    })
                                    .child(if is_renaming {
                                        if let Some(input) = self.rename_result_input.as_ref() {
                                            div()
                                                .w(px(128.0))
                                                .on_mouse_down(MouseButton::Left, |_, _, cx| {
                                                    cx.stop_propagation();
                                                })
                                                .child(Input::new(input).xsmall())
                                                .into_any_element()
                                        } else {
                                            div().into_any_element()
                                        }
                                    } else {
                                        h_flex()
                                            .items_center()
                                            .gap_1p5()
                                            .child(
                                                div()
                                                    .text_xs()
                                                    .font_weight(if selected {
                                                        gpui_kit::FontWeight::SEMIBOLD
                                                    } else {
                                                        gpui_kit::FontWeight::NORMAL
                                                    })
                                                    .text_color(if selected {
                                                        if is_err {
                                                            palette.danger
                                                        } else {
                                                            palette.text_primary
                                                        }
                                                    } else {
                                                        palette.text_muted
                                                    })
                                                    .child(item.title.clone()),
                                            )
                                            .when(show_detail, |el| {
                                                el.child(
                                                    div()
                                                        .text_size(px(10.5))
                                                        .text_color(palette.text_muted)
                                                        .child(format!("· {detail}")),
                                                )
                                            })
                                            .into_any_element()
                                    })
                                    // Per-tab close button (visible on selected tab or on hover)
                                    .when(!is_renaming, |el| {
                                        el.child(
                                            div()
                                                .id(SharedString::from(format!(
                                                    "close-res-{}",
                                                    item.id
                                                )))
                                                .size(px(18.0))
                                                .rounded(px(4.0))
                                                .flex()
                                                .items_center()
                                                .justify_center()
                                                .when(!selected, |d| {
                                                    d.invisible()
                                                        .group_hover("res-tab", |s| s.visible())
                                                })
                                                .hover(|s| s.bg(palette.subtle_hover))
                                                .on_mouse_down(MouseButton::Left, |_, _, cx| {
                                                    cx.stop_propagation();
                                                })
                                                .on_click(cx.listener(move |this, _, _, cx| {
                                                    cx.stop_propagation();
                                                    this.close_result_tab(
                                                        item_id_close.clone(),
                                                        cx,
                                                    );
                                                }))
                                                .child(
                                                    Icon::new(IconName::X)
                                                        .size(px(10.5))
                                                        .text_color(palette.text_muted),
                                                ),
                                        )
                                    })
                                    .when(selected, |d| {
                                        d.child(
                                            div()
                                                .absolute()
                                                .left_1()
                                                .right_1()
                                                .bottom_0()
                                                .h(px(2.0))
                                                .rounded_full()
                                                .bg(if is_err {
                                                    palette.danger
                                                } else {
                                                    space_color
                                                }),
                                        )
                                    })
                            }))
                            .when(grid_message_count > 0, |el| {
                                let selected = tab_rt.show_messages;
                                el.child(
                                    h_flex()
                                        .id("res-tab-messages")
                                        .relative()
                                        .flex_shrink_0()
                                        .h_full()
                                        .px_2p5()
                                        .items_center()
                                        .cursor_pointer()
                                        .when(!selected, |d| {
                                            d.hover(|s| s.bg(palette.subtle_hover))
                                        })
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            if let Some(tab_id) = this.active_tab_id() {
                                                if let Some(rt) = this.tab_runtime.get_mut(&tab_id)
                                                {
                                                    rt.show_messages = true;
                                                }
                                                this.sync_table_from_active_tab(cx);
                                                cx.notify();
                                            }
                                        }))
                                        .child(
                                            div()
                                                .text_xs()
                                                .font_weight(if selected {
                                                    gpui_kit::FontWeight::SEMIBOLD
                                                } else {
                                                    gpui_kit::FontWeight::NORMAL
                                                })
                                                .text_color(if selected {
                                                    palette.text_primary
                                                } else {
                                                    palette.text_muted
                                                })
                                                .child(format!("Messages · {grid_message_count}")),
                                        )
                                        .when(selected, |d| {
                                            d.child(
                                                div()
                                                    .absolute()
                                                    .left_1()
                                                    .right_1()
                                                    .bottom_0()
                                                    .h(px(2.0))
                                                    .rounded_full()
                                                    .bg(space_color),
                                            )
                                        }),
                                )
                            }),
                    )
                    .child(
                        h_flex()
                            .flex_shrink_0()
                            .items_center()
                            .gap_1p5()
                            .when_some(active_result, |el, r| {
                                let ms = r.elapsed.as_millis();
                                el.child(
                                    div()
                                        .px_2()
                                        .py(px(2.0))
                                        .rounded(px(4.0))
                                        .bg(palette.subtle_fill)
                                        .text_size(px(10.5))
                                        .font_family(cx.theme().mono_font_family.clone())
                                        .text_color(palette.text_muted)
                                        .child(format!("{ms} ms")),
                                )
                            })
                            .when_some(active_result.filter(|r| r.is_truncated), |el, r| {
                                el.child(
                                    div()
                                        .px_2()
                                        .py(px(2.0))
                                        .rounded(px(4.0))
                                        .bg(palette.warning.opacity(0.15))
                                        .text_size(px(10.5))
                                        .text_color(palette.warning)
                                        .child(format!(
                                            "Capped at {} rows",
                                            r.row_limit.unwrap_or(5000)
                                        )),
                                )
                            })
                            .when(has_grid, |el| {
                                let copied = self.copied_toast;
                                el.child(
                                    Button::new("copy-tsv-btn")
                                        .ghost()
                                        .xsmall()
                                        .icon(if copied {
                                            IconName::Check
                                        } else {
                                            IconName::Copy
                                        })
                                        .label(if copied { "Copied" } else { "Copy TSV" })
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.copy_active_tsv(cx);
                                        })),
                                )
                            })
                            .child(
                                Button::new("close-results-pane")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::X)
                                    .tooltip("Hide results pane")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.dismiss_results_pane(cx);
                                    })),
                            ),
                    ),
            )
            // Result body
            .child(div().flex_1().min_h_0().w_full().child(
                if tab_rt.show_messages {
                    v_flex()
                        .id("res-messages-all")
                        .size_full()
                        .overflow_y_scroll()
                        .p_4()
                        .gap_1p5()
                        .font_family(cx.theme().mono_font_family.clone())
                        .text_xs()
                        .text_color(palette.text_secondary)
                        .children(
                            tab_rt
                                .results
                                .iter()
                                .flat_map(|item| item.result.messages.iter())
                                .map(|m| div().child(m.clone())),
                        )
                        .into_any_element()
                } else if let Some(item) = active_item {
                    let res = item.result.as_ref();
                    if res.is_error {
                        div()
                            .p_4()
                            .child(
                                h_flex()
                                    .p_3p5()
                                    .rounded(px(8.0))
                                    .bg(palette.danger.opacity(0.10))
                                    .border_1()
                                    .border_color(palette.danger.opacity(0.35))
                                    .gap_2p5()
                                    .items_start()
                                    .child(
                                        Icon::new(IconName::CircleAlert)
                                            .size(px(16.0))
                                            .text_color(palette.danger),
                                    )
                                    .child(
                                        v_flex()
                                            .gap_1()
                                            .child(
                                                div()
                                                    .text_xs()
                                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                    .text_color(palette.danger)
                                                    .child("Query error"),
                                            )
                                            .child(
                                                div()
                                                    .text_xs()
                                                    .font_family(
                                                        cx.theme().mono_font_family.clone(),
                                                    )
                                                    .text_color(palette.text_primary)
                                                    .child(res.message.clone()),
                                            ),
                                    ),
                            )
                            .into_any_element()
                    } else if item.is_messages_only() {
                        v_flex()
                            .id("res-messages-single")
                            .size_full()
                            .overflow_y_scroll()
                            .p_4()
                            .gap_1p5()
                            .font_family(cx.theme().mono_font_family.clone())
                            .text_xs()
                            .text_color(palette.text_secondary)
                            .children(res.messages.iter().map(|m| div().child(m.clone())))
                            .into_any_element()
                    } else if res.columns.is_empty() {
                        div()
                            .size_full()
                            .flex()
                            .items_center()
                            .justify_center()
                            .text_xs()
                            .text_color(palette.text_secondary)
                            .child(res.message.clone())
                            .into_any_element()
                    } else {
                        DataTable::new(&self.table_state)
                            .small()
                            .stripe(true)
                            .bordered(false)
                            .scroll_bounce(false)
                            .into_any_element()
                    }
                } else {
                    div().size_full().into_any_element()
                },
            ))
    }

    fn render_db_palette(
        &self,
        active_space: Option<&SpaceState>,
        active_tab: Option<&TabState>,
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let space_color = active_space
            .map(|s| parse_hex_color(&s.color))
            .unwrap_or(palette.accent);
        let current_db = active_tab
            .and_then(|t| t.database.clone())
            .or_else(|| active_space.and_then(|s| s.active_database.clone()));
        let query = self
            .db_palette_input
            .as_ref()
            .map(|i| i.read(cx).value().trim().to_ascii_lowercase())
            .unwrap_or_default();

        let filtered: Vec<&DatabaseInfo> = space_rt
            .databases
            .iter()
            .filter(|d| query.is_empty() || d.name.to_ascii_lowercase().contains(&query))
            .collect();

        div()
            .id("db-palette-backdrop")
            .absolute()
            .inset_0()
            .bg(hsla(0.0, 0.0, 0.0, 0.48))
            .flex()
            .items_start()
            .justify_center()
            .pt(px(96.0))
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    this.close_db_palette(cx);
                }),
            )
            .child(
                v_flex()
                    .id("db-palette-card")
                    .w(px(420.0))
                    .max_h(px(440.0))
                    .rounded(px(10.0))
                    .bg(palette.modal_bg)
                    .border_1()
                    .border_color(palette.card_border)
                    .shadow_2xl()
                    .overflow_hidden()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    // Search header
                    .child(
                        h_flex()
                            .px_3p5()
                            .py_2p5()
                            .gap_2()
                            .items_center()
                            .border_b_1()
                            .border_color(palette.hairline)
                            .child(
                                Icon::new(IconName::Search)
                                    .size(px(14.0))
                                    .text_color(palette.text_muted),
                            )
                            .child(if let Some(input) = self.db_palette_input.as_ref() {
                                div()
                                    .flex_1()
                                    .child(Input::new(input).small().appearance(false))
                                    .into_any_element()
                            } else {
                                div().flex_1().into_any_element()
                            })
                            .child(
                                Button::new("db-palette-close")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::X)
                                    .tooltip("Close (Esc)")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.close_db_palette(cx);
                                    })),
                            ),
                    )
                    // Sub-caption
                    .child(
                        div()
                            .px_3p5()
                            .pt_2()
                            .pb_1()
                            .text_size(px(10.0))
                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            .text_color(palette.text_muted)
                            .child("NEW TAB · CHOOSE DATABASE"),
                    )
                    // Filtered DB list
                    .child(
                        div()
                            .id("db-palette-list")
                            .flex_1()
                            .overflow_y_scroll()
                            .px_2()
                            .pb_2()
                            .child(if filtered.is_empty() {
                                div()
                                    .px_3()
                                    .py_4()
                                    .text_xs()
                                    .text_color(palette.text_muted)
                                    .child("No matching databases.")
                                    .into_any_element()
                            } else {
                                let mut items: Vec<AnyElement> = Vec::new();
                                for db in filtered {
                                    let db_name = db.name.clone();
                                    let accessible = db.is_accessible;
                                    let is_current = current_db
                                        .as_deref()
                                        .is_some_and(|c| c.eq_ignore_ascii_case(&db_name));
                                    items.push(
                                        h_flex()
                                            .id(SharedString::from(format!(
                                                "palette-db-{db_name}"
                                            )))
                                            .px_2p5()
                                            .py_2()
                                            .rounded(px(6.0))
                                            .items_center()
                                            .justify_between()
                                            .gap_2()
                                            .when(accessible, |d| {
                                                d.cursor_pointer()
                                                    .hover(|s| s.bg(palette.subtle_hover))
                                                    .on_click(cx.listener(
                                                        move |this, _, window, cx| {
                                                            this.close_db_palette(cx);
                                                            this.new_tab_for_database(
                                                                Some(db_name.clone()),
                                                                window,
                                                                cx,
                                                            );
                                                        },
                                                    ))
                                            })
                                            .child(
                                                h_flex()
                                                    .items_center()
                                                    .gap_2()
                                                    .min_w_0()
                                                    .child(
                                                        Icon::new(IconName::Database)
                                                            .size(px(13.0))
                                                            .text_color(if accessible {
                                                                space_color
                                                            } else {
                                                                palette.text_muted.opacity(0.45)
                                                            }),
                                                    )
                                                    .child(
                                                        div()
                                                            .text_xs()
                                                            .font_weight(if is_current {
                                                                gpui_kit::FontWeight::SEMIBOLD
                                                            } else {
                                                                gpui_kit::FontWeight::NORMAL
                                                            })
                                                            .text_color(if accessible {
                                                                palette.text_primary
                                                            } else {
                                                                palette.text_muted.opacity(0.5)
                                                            })
                                                            .truncate()
                                                            .child(db.name.clone()),
                                                    ),
                                            )
                                            .child(if !accessible {
                                                div()
                                                    .text_size(px(10.5))
                                                    .text_color(palette.text_muted.opacity(0.55))
                                                    .child("No access")
                                                    .into_any_element()
                                            } else if is_current {
                                                div()
                                                    .px_1p5()
                                                    .py(px(1.0))
                                                    .rounded(px(4.0))
                                                    .bg(palette.accent_soft)
                                                    .text_size(px(10.5))
                                                    .text_color(palette.accent)
                                                    .child("Current")
                                                    .into_any_element()
                                            } else {
                                                div().into_any_element()
                                            })
                                            .into_any_element(),
                                    );
                                }
                                v_flex().gap_0p5().children(items).into_any_element()
                            }),
                    ),
            )
    }

    fn render_schema_drawer(
        &self,
        active_space: Option<&SpaceState>,
        active_tab: Option<&TabState>,
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let total_tables = space_rt.schema.len();
        let count_label = if self.schema_filter_query.is_empty() {
            total_tables.to_string()
        } else {
            format!("{} / {total_tables}", self.schema_matched_tables)
        };
        let has_filter = !self.schema_filter_query.is_empty();
        let active_db = active_tab
            .and_then(|t| t.database.as_deref())
            .or_else(|| active_space.and_then(|s| s.active_database.as_deref()))
            .map(str::to_string);

        v_flex()
            .w(px(292.0))
            .h_full()
            .flex_shrink_0()
            .bg(palette.panel_bg)
            .border_r_1()
            .border_color(palette.hairline)
            // Header
            .child(
                h_flex()
                    .h(px(38.0))
                    .px_3()
                    .border_b_1()
                    .border_color(palette.hairline_subtle)
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .flex_1()
                            .min_w_0()
                            .items_center()
                            .gap_1p5()
                            .child(
                                div()
                                    .flex_shrink_0()
                                    .text_xs()
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                    .text_color(palette.text_primary)
                                    .child("Schema"),
                            )
                            .when_some(active_db, |row, db| {
                                row.child(
                                    div()
                                        .text_size(px(11.0))
                                        .text_color(palette.text_muted)
                                        .truncate()
                                        .child(format!("· {db}")),
                                )
                            })
                            .child(
                                div()
                                    .flex_shrink_0()
                                    .px_1p5()
                                    .py(px(1.0))
                                    .rounded(px(4.0))
                                    .bg(palette.subtle_fill)
                                    .text_size(px(10.5))
                                    .text_color(palette.text_muted)
                                    .child(count_label),
                            ),
                    )
                    .child(
                        Button::new("close-schema-drawer")
                            .ghost()
                            .xsmall()
                            .icon(IconName::X)
                            .tooltip("Close schema peek (Ctrl+K)")
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.toggle_schema_pane(cx);
                            })),
                    ),
            )
            // Instant filter input bar
            .when(total_tables > 0, |el| {
                el.child(
                    h_flex()
                        .px_2p5()
                        .py_1p5()
                        .gap_1p5()
                        .items_center()
                        .border_b_1()
                        .border_color(palette.hairline_subtle)
                        .child(
                            Icon::new(IconName::Search)
                                .size(px(12.5))
                                .text_color(palette.text_muted),
                        )
                        .child(
                            div().flex_1().min_w_0().child(
                                Input::new(&self.schema_filter_input)
                                    .xsmall()
                                    .appearance(false),
                            ),
                        )
                        .when(has_filter, |bar| {
                            bar.child(
                                Button::new("clear-schema-filter")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::X)
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.clear_schema_filter(window, cx);
                                    })),
                            )
                        }),
                )
            })
            // Virtualized list body (only visible rows are mounted/laid out!)
            .child(if total_tables == 0 {
                div()
                    .flex_1()
                    .p_4()
                    .text_xs()
                    .text_color(palette.text_muted)
                    .child("Connect a workspace to browse tables and columns.")
                    .into_any_element()
            } else if self.schema_rows.is_empty() {
                div()
                    .flex_1()
                    .p_4()
                    .text_xs()
                    .text_color(palette.text_muted)
                    .child("No tables or columns match your filter.")
                    .into_any_element()
            } else {
                div()
                    .relative()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .child(
                        uniform_list(
                            "schema-uniform-list",
                            self.schema_rows.len(),
                            cx.processor(move |this, range: Range<usize>, _window, cx| {
                                let mono_font = cx.theme().mono_font_family.clone();
                                let mut items = Vec::with_capacity(range.len());
                                for idx in range {
                                    if let Some(row) = this.schema_rows.get(idx) {
                                        items.push(this.render_schema_row(
                                            idx,
                                            row,
                                            palette,
                                            &mono_font,
                                            cx,
                                        ));
                                    }
                                }
                                items
                            }),
                        )
                        .size_full()
                        .px_1p5()
                        .py_1()
                        .track_scroll(&self.schema_scroll_handle),
                    )
                    .child(Scrollbar::vertical(&self.schema_scroll_handle))
                    .into_any_element()
            })
    }

    fn render_schema_row(
        &self,
        idx: usize,
        row: &SchemaRow,
        palette: StudioPalette,
        mono_font: &SharedString,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        match row {
            SchemaRow::Table {
                key,
                is_view,
                expanded,
                col_count,
            } => {
                let key_toggle = key.clone();
                let key_select = key.clone();
                let key_label = key.clone();
                let col_count_label = col_count.clone();
                let is_view = *is_view;
                let expanded = *expanded;

                h_flex()
                    .id(("schema-row", idx))
                    .group("schema-tbl")
                    .h(px(28.0))
                    .w_full()
                    .px_1p5()
                    .rounded(px(5.0))
                    .items_center()
                    .justify_between()
                    .gap_1()
                    .cursor_pointer()
                    .hover(|s| s.bg(palette.subtle_hover))
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.toggle_schema_table(key_toggle.as_ref(), cx);
                    }))
                    .child(
                        h_flex()
                            .flex_1()
                            .min_w_0()
                            .gap_1p5()
                            .items_center()
                            .child(
                                Icon::new(if expanded {
                                    IconName::ChevronDown
                                } else {
                                    IconName::ChevronRight
                                })
                                .size(px(12.0))
                                .text_color(palette.text_muted),
                            )
                            .child(
                                Icon::new(if is_view {
                                    IconName::Eye
                                } else {
                                    IconName::Table2
                                })
                                .size(px(12.0))
                                .text_color(palette.accent),
                            )
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .overflow_hidden()
                                    .whitespace_nowrap()
                                    .text_xs()
                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                    .text_color(palette.text_primary)
                                    .child(key_label),
                            ),
                    )
                    .child(
                        h_flex()
                            .flex_shrink_0()
                            .items_center()
                            .gap_1()
                            .child(
                                div()
                                    .text_size(px(10.0))
                                    .text_color(palette.text_muted)
                                    .child(col_count_label),
                            )
                            .child(
                                div()
                                    .id(("schema-play", idx))
                                    .size(px(20.0))
                                    .rounded(px(4.0))
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .opacity(0.0)
                                    .group_hover("schema-tbl", |s| s.opacity(1.0))
                                    .hover(|s| s.bg(palette.accent_soft))
                                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                                        cx.stop_propagation();
                                    })
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        cx.stop_propagation();
                                        let sql =
                                            format!("\nSELECT TOP 100 * FROM {key_select};\n");
                                        this.insert_into_editor(sql, window, cx);
                                    }))
                                    .child(
                                        Icon::new(IconName::Play)
                                            .size(px(11.0))
                                            .text_color(palette.accent),
                                    ),
                            ),
                    )
                    .into_any_element()
            }
            SchemaRow::Column {
                name,
                data_type,
                is_pk,
                is_fk,
            } => {
                let col_insert = name.clone();
                let col_label = name.clone();
                let dt_opt = data_type.clone();
                let is_pk = *is_pk;
                let is_fk = *is_fk;

                h_flex()
                    .id(("schema-row", idx))
                    .h(px(28.0))
                    .w_full()
                    .pl_6()
                    .pr_2()
                    .rounded(px(5.0))
                    .items_center()
                    .justify_between()
                    .gap_1p5()
                    .cursor_pointer()
                    .hover(|s| s.bg(palette.subtle_hover))
                    .on_click(cx.listener(move |this, _, window, cx| {
                        this.insert_into_editor(col_insert.to_string(), window, cx);
                    }))
                    .child(
                        h_flex()
                            .flex_1()
                            .min_w_0()
                            .gap_1()
                            .items_center()
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .overflow_hidden()
                                    .whitespace_nowrap()
                                    .text_xs()
                                    .text_color(palette.text_secondary)
                                    .child(col_label),
                            )
                            .when(is_pk, |d| {
                                d.child(
                                    div()
                                        .flex_shrink_0()
                                        .px_1()
                                        .rounded(px(3.0))
                                        .bg(palette.warning.opacity(0.18))
                                        .text_size(px(10.0))
                                        .text_color(palette.warning)
                                        .child("PK"),
                                )
                            })
                            .when(is_fk, |d| {
                                d.child(
                                    div()
                                        .flex_shrink_0()
                                        .px_1()
                                        .rounded(px(3.0))
                                        .bg(palette.accent_soft)
                                        .text_size(px(10.0))
                                        .text_color(palette.accent)
                                        .child("FK"),
                                )
                            }),
                    )
                    .when_some(dt_opt, |d, dt| {
                        d.child(
                            div()
                                .flex_shrink_0()
                                .text_size(px(10.5))
                                .font_family(mono_font.clone())
                                .text_color(palette.text_muted)
                                .child(dt),
                        )
                    })
                    .into_any_element()
            }
        }
    }

    fn render_status_bar(
        &self,
        active_space: Option<&SpaceState>,
        active_tab: Option<&TabState>,
        space_rt: &SpaceRuntime,
        tab_rt: &TabRuntime,
        palette: StudioPalette,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        let conn_state = space_rt.connection_state();
        let db = active_tab
            .and_then(|t| t.database.as_deref())
            .or_else(|| active_space.and_then(|s| s.active_database.as_deref()));

        let left_status = match conn_state {
            ConnectionState::Connected => {
                if let Some(summary) = tab_rt.last_summary.as_deref() {
                    if let Some(db_name) = db {
                        format!("Connected · {db_name} · {summary}")
                    } else {
                        format!("Connected · {summary}")
                    }
                } else if let Some(ver) = space_rt.server_version.as_deref() {
                    format!("Connected · {ver}")
                } else {
                    "Connected".to_string()
                }
            }
            ConnectionState::Connecting => "Connecting…".to_string(),
            ConnectionState::Faulted => "Connection failed".to_string(),
            ConnectionState::Disconnected => "Disconnected".to_string(),
        };

        let (line, col) = self.editor_line_col(cx);

        h_flex()
            .h(px(26.0))
            .px_4()
            .bg(transparent_black())
            .border_t_1()
            .border_color(palette.hairline_subtle)
            .items_center()
            .justify_between()
            .text_size(px(11.0))
            .child(
                div()
                    .text_color(palette.text_secondary)
                    .truncate()
                    .child(left_status),
            )
            .child(
                div()
                    .flex_shrink_0()
                    .text_color(palette.text_muted)
                    .child(format!("Ln {line}, Col {col}")),
            )
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;
    use std::time::Duration;

    use larik_data::{QueryColumn, QueryResult, TableKind, TableSchema};

    use super::{SpaceRuntime, TabRuntime, footer_space_window};

    fn grid_result(rows: usize) -> QueryResult {
        QueryResult {
            columns: vec![QueryColumn {
                name: "id".to_string(),
                data_type: Some("int".to_string()),
            }],
            rows: (0..rows).map(|i| vec![Some(i.to_string())]).collect(),
            records_affected: None,
            message: format!("{rows} rows"),
            is_error: false,
            elapsed: Duration::from_millis(5),
            database: Some("demo".to_string()),
            is_truncated: false,
            row_limit: None,
            messages: vec![format!("({rows} rows returned)")],
            statement: Some("SELECT 1".to_string()),
        }
    }

    fn messages_only_result(lines: &[&str]) -> QueryResult {
        QueryResult {
            columns: Vec::new(),
            rows: Vec::new(),
            records_affected: None,
            message: lines.join("\n"),
            is_error: false,
            elapsed: Duration::from_millis(2),
            database: Some("demo".to_string()),
            is_truncated: false,
            row_limit: None,
            messages: lines.iter().map(|s| s.to_string()).collect(),
            statement: Some("PRINT 'hello'".to_string()),
        }
    }

    #[test]
    fn footer_space_window_caps_to_three_and_clamps_offset() {
        assert_eq!(footer_space_window(0, 0), (0, 0));
        assert_eq!(footer_space_window(2, 0), (0, 2));
        assert_eq!(footer_space_window(2, 5), (0, 2));
        assert_eq!(footer_space_window(3, 1), (0, 3));
        assert_eq!(footer_space_window(5, 0), (0, 3));
        assert_eq!(footer_space_window(5, 1), (1, 4));
        assert_eq!(footer_space_window(5, 2), (2, 5));
        assert_eq!(footer_space_window(5, 99), (2, 5));
    }

    #[test]
    fn apply_results_append_adds_new_result_tabs_and_preserves_existing() {
        let mut rt = TabRuntime::default();
        rt.apply_results(vec![grid_result(3)], false);
        assert_eq!(rt.results.len(), 1);
        assert_eq!(rt.results[0].title, "Result 1");
        let first_id = rt.results[0].id.clone();

        // Ctrl+\ appends a second result tab and activates it
        rt.apply_results(vec![grid_result(7)], true);
        assert_eq!(rt.results.len(), 2);
        assert_eq!(rt.results[0].id, first_id);
        assert_eq!(rt.results[1].title, "Result 2");
        assert_eq!(rt.active_result_id.as_deref(), Some(rt.results[1].id.as_str()));
        assert_eq!(rt.active_result().unwrap().result.rows.len(), 7);
    }

    #[test]
    fn apply_results_in_place_preserves_custom_name_for_same_kind_and_switches_for_messages() {
        let mut rt = TabRuntime::default();
        rt.apply_results(vec![grid_result(2)], false);
        let id = rt.results[0].id.clone();
        assert!(rt.rename_result(&id, "Orders"));
        assert_eq!(rt.results[0].title, "Orders");

        // Re-running a grid query in place keeps the custom name
        rt.apply_results(vec![grid_result(9)], false);
        assert_eq!(rt.results.len(), 1);
        assert_eq!(rt.results[0].id, id);
        assert_eq!(rt.results[0].title, "Orders");
        assert_eq!(rt.results[0].result.rows.len(), 9);

        // Switching to a messages-only result updates default title to Messages
        rt.apply_results(vec![messages_only_result(&["hello"])], false);
        assert_eq!(rt.results.len(), 1);
        assert_eq!(rt.results[0].title, "Messages");
    }

    #[test]
    fn reorder_and_close_result_tabs_maintain_active_selection() {
        let mut rt = TabRuntime::default();
        rt.apply_results(vec![grid_result(1)], true);
        rt.apply_results(vec![grid_result(2)], true);
        rt.apply_results(vec![grid_result(3)], true);

        let id1 = rt.results[0].id.clone();
        let id2 = rt.results[1].id.clone();
        let id3 = rt.results[2].id.clone();
        rt.rename_result(&id2, "Middle");

        // Drag Result 3 before Result 1
        assert!(rt.reorder_result(&id3, &id1));
        assert_eq!(
            rt.results.iter().map(|r| r.id.as_str()).collect::<Vec<_>>(),
            vec![id3.as_str(), id1.as_str(), id2.as_str()]
        );
        assert_eq!(rt.results[2].title, "Middle");
        assert_eq!(rt.active_result_id.as_deref(), Some(id3.as_str()));

        // Close active (first) tab -> falls back to neighbor
        rt.close_result(&id3);
        assert_eq!(rt.results.len(), 2);
        assert_eq!(rt.active_result_id.as_deref(), Some(id1.as_str()));
    }

    #[test]
    fn space_runtime_caches_schema_per_database_case_insensitively() {
        let mut rt = SpaceRuntime::default();
        let sales_tables = Arc::new(vec![TableSchema {
            schema: "dbo".into(),
            name: "Orders".into(),
            kind: TableKind::Table,
            columns: vec![],
            primary_key: vec![],
            foreign_keys: vec![],
        }]);
        let analytics_tables = Arc::new(vec![TableSchema {
            schema: "dbo".into(),
            name: "Events".into(),
            kind: TableKind::Table,
            columns: vec![],
            primary_key: vec![],
            foreign_keys: vec![],
        }]);

        rt.cache_schema(Some("SalesDB"), sales_tables);
        rt.cache_schema(Some("AnalyticsDB"), analytics_tables);

        assert_eq!(
            rt.cached_schema(Some("salesdb"))
                .map(|s| s[0].name.clone()),
            Some("Orders".to_string())
        );
        assert_eq!(
            rt.cached_schema(Some("ANALYTICSDB"))
                .map(|s| s[0].name.clone()),
            Some("Events".to_string())
        );
        assert!(rt.cached_schema(Some("OtherDB")).is_none());
        assert!(rt.cached_schema(None).is_none());
    }
}
