use std::collections::{HashMap, HashSet};
use std::ops::Range;
use std::rc::Rc;
use std::sync::{Arc, RwLock};
use std::time::Duration;

use gpui_kit::assets::IconName;
use gpui_kit::base::Disableable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Editor, EditorState, Input, InputEvent, InputState};
use gpui_kit::component::resizable::{resizable_panel, v_resizable};
use gpui_kit::component::scroll::Scrollbar;
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::table::{DataTable, TableState};
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, TitleBar, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, AppContext as _, ClickEvent, ClipboardItem, Context, Entity, FocusHandle,
    Focusable, Image, ImageFormat, InteractiveElement as _, IntoElement, KeyBinding, MouseButton,
    ParentElement as _, Render, SharedString, StatefulInteractiveElement as _, Styled as _,
    Subscription, UniformListScrollHandle, Window, div, hsla, img, px, rgb, transparent_black,
    uniform_list,
};

const APP_ICON_PNG: &[u8] = include_bytes!("../assets/app_icon.png");
use larik_core::session::SessionManager;
use larik_core::store::StudioStore;
use larik_core::{
    ArchivedTabState, SPACE_COLORS, SpaceState, StudioState, TabState, ThemePreference,
};
use larik_data::sql_language::SqlLanguage;
use larik_data::{
    ConnectionState, DatabaseInfo, ProviderCatalog, QueryExecutionOptions, QueryResult, TableKind,
    TableSchema,
};
use uuid::Uuid;

use crate::completion::{CompletionSnapshot, SqlCompletionProvider};
use crate::connection_dialog::{ConnectionModal, render_connection_modal};
use crate::results_table::ResultTableDelegate;
use crate::theme::{StudioPalette, apply_larik_theme, parse_hex_color};

gpui_kit::actions!(
    larik,
    [
        RunStatement,
        RunAll,
        NewTab,
        CloseTab,
        NextTab,
        PrevTab,
        ToggleRail,
        ToggleSchema,
        ToggleTheme,
        DismissModal,
    ]
);

pub fn bind_studio_keys(cx: &mut App) {
    cx.bind_keys([
        KeyBinding::new("ctrl-enter", RunStatement, None),
        KeyBinding::new("f5", RunAll, None),
        KeyBinding::new("ctrl-t", NewTab, None),
        KeyBinding::new("ctrl-w", CloseTab, None),
        KeyBinding::new("ctrl-tab", NextTab, None),
        KeyBinding::new("ctrl-shift-tab", PrevTab, None),
        KeyBinding::new("ctrl-b", ToggleRail, None),
        KeyBinding::new("ctrl-k", ToggleSchema, None),
        KeyBinding::new("ctrl-shift-l", ToggleTheme, None),
        KeyBinding::new("escape", DismissModal, Some("StudioModal")),
    ]);
}

#[derive(Default, Clone)]
struct SpaceRuntime {
    state: Option<ConnectionState>,
    server_version: Option<SharedString>,
    databases: Arc<Vec<DatabaseInfo>>,
    schema: Arc<Vec<TableSchema>>,
    last_error: Option<SharedString>,
}

impl SpaceRuntime {
    fn connection_state(&self) -> ConnectionState {
        self.state.unwrap_or(ConnectionState::Disconnected)
    }
}

#[derive(Default, Clone)]
struct TabRuntime {
    running: bool,
    results: Arc<Vec<Arc<QueryResult>>>,
    active_result_idx: usize,
    show_messages: bool,
    results_dismissed: bool,
    last_summary: Option<SharedString>,
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
    table_state: Entity<TableState<ResultTableDelegate>>,
    _editor_sub: Subscription,
    suppress_editor_sync: bool,

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
    workspace_dropdown_open: bool,
    db_dropdown_open: bool,
    archive_open: bool,
    expanded_tables: HashSet<String>,
    renaming_tab_id: Option<String>,
    rename_input: Option<Entity<InputState>>,
    _rename_sub: Option<Subscription>,
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
        let state = store.load(&catalog);
        apply_larik_theme(state.theme, Some(window), cx);

        let initial_sql = state
            .active_space()
            .and_then(|s| s.active_tab())
            .map(|t| t.content.clone())
            .unwrap_or_default();

        let completion_snapshot = Arc::new(RwLock::new(CompletionSnapshot::default()));
        let provider_ref = completion_snapshot.clone();

        let editor_state = cx.new(|cx| {
            let mut ed = EditorState::new(window, cx)
                .language("sql")
                .placeholder("-- Write SQL here. Ctrl+Enter runs statement under caret, F5 runs all.")
                .default_value(initial_sql);
            ed.lsp_mut().completion_provider =
                Some(Rc::new(SqlCompletionProvider::new(provider_ref)));
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
                    this.persist();
                }
            }
        });

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
            table_state,
            _editor_sub,
            suppress_editor_sync: false,
            schema_filter_input,
            _schema_filter_sub,
            schema_filter_query: String::new(),
            schema_rows: Vec::new(),
            schema_matched_tables: 0,
            schema_scroll_handle: UniformListScrollHandle::new(),
            app_icon,
            managing_workspaces: false,
            workspace_dropdown_open: false,
            db_dropdown_open: false,
            archive_open: false,
            expanded_tables: HashSet::new(),
            renaming_tab_id: None,
            rename_input: None,
            _rename_sub: None,
            db_palette_open: false,
            db_palette_input: None,
            _db_palette_sub: None,
            modal: None,
            copied_toast: false,
        };

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

    fn refresh_completion_snapshot(&self) {
        let Some(space) = self.state.active_space() else {
            return;
        };
        let (tables, databases) = self
            .space_runtime
            .get(&space.id)
            .map(|rt| (Arc::clone(&rt.schema), Arc::clone(&rt.databases)))
            .unwrap_or_default();
        let language = self
            .catalog
            .find(&space.profile.provider_id)
            .map(|p| p.editor_language())
            .unwrap_or_else(|| Arc::new(SqlLanguage::tsql()));

        if let Ok(mut guard) = self.completion_snapshot.write() {
            guard.tables = tables;
            guard.databases = databases;
            guard.language = language;
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
            rt.results.get(rt.active_result_idx).cloned()
        });

        self.table_state.update(cx, |table, cx| {
            table.delegate_mut().set_result(active_result);
            table.refresh(cx);
        });
    }

    fn switch_space(&mut self, space_id: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.active_space_id.as_deref() == Some(space_id.as_str()) {
            self.workspace_dropdown_open = false;
            cx.notify();
            return;
        }
        self.state.active_space_id = Some(space_id);
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.expanded_tables.clear();
        self.close_db_palette(cx);
        self.cancel_rename(cx);
        self.sync_editor_from_active_tab(window, cx);
        self.refresh_completion_snapshot();
        self.rebuild_schema_rows();
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
            profile,
            active_database: None,
            active_tab_id: Some(tab_id.clone()),
            tabs: vec![TabState {
                id: tab_id,
                title: tab_title,
                database: None,
                content: String::new(),
            }],
            is_hidden: false,
        });
        self.state.active_space_id = Some(space_id.clone());
        self.expanded_tables.clear();
        self.sync_editor_from_active_tab(window, cx);
        self.refresh_completion_snapshot();
        self.rebuild_schema_rows();
        self.persist();
        self.open_connection_modal(space_id, window, cx);
        cx.notify();
    }

    fn open_manage_workspaces(&mut self, cx: &mut Context<Self>) {
        self.workspace_dropdown_open = false;
        self.db_dropdown_open = false;
        self.archive_open = false;
        self.close_db_palette(cx);
        self.cancel_rename(cx);
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

    fn toggle_hide_workspace(&mut self, space_id: String, cx: &mut Context<Self>) {
        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            space.is_hidden = !space.is_hidden;
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
            self.sync_editor_from_active_tab(window, cx);
            self.refresh_completion_snapshot();
            self.rebuild_schema_rows();
        }

        self.modal = None;
        self.persist();
        cx.notify();
    }

    fn connect_space(&mut self, space_id: String, cx: &mut Context<Self>) {
        let Some(space) = self.state.spaces.iter().find(|s| s.id == space_id) else {
            return;
        };
        let profile = space.profile.clone();
        let preferred_db = space.active_database.clone();

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
                        let rt = this.space_runtime.entry(space_id.clone()).or_default();
                        rt.state = Some(ConnectionState::Connected);
                        rt.server_version = outcome.server_version.map(SharedString::from);
                        rt.databases = Arc::new(outcome.databases);
                        rt.schema = Arc::new(outcome.schema);
                        rt.last_error = None;

                        if let Some(db) = outcome.initial_database {
                            if let Some(space) =
                                this.state.spaces.iter_mut().find(|s| s.id == space_id)
                            {
                                space.active_database = Some(db.clone());
                                for tab in &mut space.tabs {
                                    if tab.database.is_none() {
                                        tab.database = Some(db.clone());
                                    }
                                }
                            }
                            this.persist();
                        }
                        this.refresh_completion_snapshot();
                        this.rebuild_schema_rows();
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
        rt.server_version = None;
        rt.last_error = None;
        self.expanded_tables.clear();
        self.db_dropdown_open = false;
        self.close_db_palette(cx);
        self.refresh_completion_snapshot();
        self.rebuild_schema_rows();
        cx.notify();
    }

    fn select_database(&mut self, database: String, cx: &mut Context<Self>) {
        let Some(space_id) = self.active_space_id() else {
            return;
        };
        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            space.active_database = Some(database.clone());
            if let Some(tab) = space.active_tab_mut() {
                tab.database = Some(database.clone());
            }
        }
        self.expanded_tables.clear();
        self.db_dropdown_open = false;
        self.persist();
        cx.notify();

        let rx = self.sessions.load_schema(space_id.clone(), Some(database));
        cx.spawn(async move |this, cx| {
            if let Ok(Ok(schema)) = rx.await {
                let _ = this.update(cx, |this, cx| {
                    this.space_runtime.entry(space_id).or_default().schema = Arc::new(schema);
                    this.refresh_completion_snapshot();
                    this.rebuild_schema_rows();
                    cx.notify();
                });
            }
        })
        .detach();
    }

    /// Trigger the Arc-style "+ New query" flow: when connected with multiple accessible databases,
    /// open a searchable New-Tab Database Palette; otherwise create a tab immediately.
    fn prompt_new_tab(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let accessible_count = self
            .active_space_id()
            .and_then(|id| self.space_runtime.get(&id))
            .filter(|rt| rt.connection_state() == ConnectionState::Connected)
            .map(|rt| rt.databases.iter().filter(|d| d.is_accessible).count())
            .unwrap_or(0);

        if accessible_count > 1 {
            self.open_db_palette(window, cx);
        } else {
            let default_db = self
                .state
                .active_space()
                .and_then(|s| s.active_database.clone());
            self.new_tab_for_database(default_db, window, cx);
        }
    }

    fn open_db_palette(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.db_dropdown_open = false;
        self.cancel_rename(cx);

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
        let title = self.state.next_query_title();
        let tab_id = Uuid::new_v4().to_string();
        let mut db_changed_for_space: Option<(String, String)> = None;

        if let Some(space) = self.state.active_space_mut() {
            let bound_db = database.or_else(|| space.active_database.clone());
            if let Some(ref db) = bound_db {
                if space.active_database.as_deref() != Some(db.as_str()) {
                    space.active_database = Some(db.clone());
                    db_changed_for_space = Some((space.id.clone(), db.clone()));
                }
            }
            space.tabs.push(TabState {
                id: tab_id.clone(),
                title,
                database: bound_db,
                content: String::new(),
            });
            space.active_tab_id = Some(tab_id);
        }

        self.sync_editor_from_active_tab(window, cx);
        self.editor_state.update(cx, |ed, cx| ed.focus(window, cx));
        self.persist();
        cx.notify();

        if let Some((space_id, db)) = db_changed_for_space {
            self.expanded_tables.clear();
            let rx = self.sessions.load_schema(space_id.clone(), Some(db));
            cx.spawn(async move |this, cx| {
                if let Ok(Ok(schema)) = rx.await {
                    let _ = this.update(cx, |this, cx| {
                        this.space_runtime.entry(space_id).or_default().schema = Arc::new(schema);
                        this.refresh_completion_snapshot();
                        this.rebuild_schema_rows();
                        cx.notify();
                    });
                }
            })
            .detach();
        }
    }

    fn switch_tab(&mut self, tab_id: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.active_tab_id().as_deref() == Some(tab_id.as_str()) {
            return;
        }
        if let Some(space) = self.state.active_space_mut() {
            space.active_tab_id = Some(tab_id);
        }
        self.cancel_rename(cx);
        self.sync_editor_from_active_tab(window, cx);
        self.persist();
        cx.notify();
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
        let Some(space_id) = self.active_space_id() else {
            return;
        };
        let removed = {
            let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) else {
                return;
            };
            let Some(pos) = space.tabs.iter().position(|t| t.id == tab_id) else {
                return;
            };
            let tab = space.tabs.remove(pos);
            if space.active_tab_id.as_deref() == Some(tab.id.as_str()) {
                space.active_tab_id = space
                    .tabs
                    .get(pos.saturating_sub(1))
                    .or_else(|| space.tabs.first())
                    .map(|t| t.id.clone());
            }
            tab
        };

        self.sessions.close_tab_session(removed.id.clone());
        self.tab_runtime.remove(&removed.id);

        // Non-destructive close: file into archive if it has content.
        if !removed.content.trim().is_empty() {
            self.state.archived_tabs.insert(
                0,
                ArchivedTabState {
                    id: removed.id,
                    space_id: space_id.clone(),
                    title: removed.title,
                    database: removed.database,
                    content: removed.content,
                    archived_at: chrono::Utc::now().format("%H:%M").to_string(),
                },
            );
            self.state.archived_tabs.truncate(50);
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
        self.persist();
        cx.notify();
    }

    fn restore_archived_tab(
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
            space.tabs.push(TabState {
                id: archived.id.clone(),
                title: archived.title,
                database: archived.database,
                content: archived.content,
            });
            space.active_tab_id = Some(archived.id);
            self.state.active_space_id = Some(space.id.clone());
        }
        self.sync_editor_from_active_tab(window, cx);
        self.persist();
        cx.notify();
    }

    fn start_rename_tab(&mut self, tab_id: String, window: &mut Window, cx: &mut Context<Self>) {
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

    fn run_query(&mut self, statement_at_caret: bool, cx: &mut Context<Self>) {
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
            rt.results = Arc::new(vec![Arc::new(QueryResult::error(
                "Workspace is disconnected. Click Connect in the left sidebar first.",
                Duration::ZERO,
                tab.database.clone(),
            ))]);
            rt.active_result_idx = 0;
            rt.show_messages = false;
            rt.results_dismissed = false;
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
        let database = tab.database.clone().or(space.active_database.clone());

        {
            let rt = self.tab_runtime.entry(tab_id.clone()).or_default();
            rt.running = true;
            rt.last_summary = Some("Running…".into());
        }
        cx.notify();

        let rx = self.sessions.execute_tab_query(
            tab_id.clone(),
            space.profile,
            database,
            sql_to_run,
            QueryExecutionOptions::default(),
        );

        cx.spawn(async move |this, cx| {
            let outcome = rx.await;
            let _ = this.update(cx, |this, cx| {
                let rt = this.tab_runtime.entry(tab_id.clone()).or_default();
                rt.running = false;
                rt.results_dismissed = false;

                match outcome {
                    Ok(Ok(results)) => {
                        let total_rows: usize = results.iter().map(|r| r.rows.len()).sum();
                        let total_ms: u128 = results.iter().map(|r| r.elapsed.as_millis()).sum();
                        let has_err = results.iter().any(|r| r.is_error);
                        let first_grid = results.iter().position(|r| !r.columns.is_empty());

                        rt.last_summary = Some(
                            if has_err {
                                format!("Failed · {total_ms} ms")
                            } else {
                                format!("{total_rows} rows · {total_ms} ms")
                            }
                            .into(),
                        );
                        rt.active_result_idx = first_grid.unwrap_or(0);
                        rt.show_messages = first_grid.is_none()
                            && !has_err
                            && results.iter().any(|r| !r.messages.is_empty());
                        rt.results = Arc::new(results.into_iter().map(Arc::new).collect());
                    }
                    Ok(Err(err)) => {
                        rt.last_summary = Some("Error".into());
                        rt.results = Arc::new(vec![Arc::new(QueryResult::error(
                            err.to_string(),
                            Duration::ZERO,
                            None,
                        ))]);
                        rt.active_result_idx = 0;
                        rt.show_messages = false;
                    }
                    Err(_) => {
                        rt.last_summary = Some("Cancelled".into());
                    }
                }

                if this.active_tab_id().as_deref() == Some(tab_id.as_str()) {
                    this.sync_table_from_active_tab(cx);
                }
                cx.notify();
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

    fn toggle_theme(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.state.theme = match self.state.theme {
            ThemePreference::Dark => ThemePreference::Light,
            ThemePreference::Light => ThemePreference::Dark,
        };
        apply_larik_theme(self.state.theme, Some(window), cx);
        self.persist();
        cx.notify();
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
        self.archive_open = false;
        let can_delete = self.state.spaces.len() > 1;
        let Some(space) = self.state.spaces.iter().find(|s| s.id == space_id) else {
            return;
        };
        self.modal = Some(ConnectionModal::open(
            space.id.clone(),
            &space.name,
            &space.color,
            &space.profile,
            can_delete,
            &self.catalog,
            window,
            cx,
        ));
        cx.notify();
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
        let new_profile = modal.collect_profile(&self.catalog, cx);

        if let Some(space) = self.state.spaces.iter_mut().find(|s| s.id == space_id) {
            space.name = new_name;
            space.color = new_color;
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
            || self.workspace_dropdown_open
            || self.db_dropdown_open
            || self.archive_open
            || self.managing_workspaces;
        let key_ctx = if has_overlay {
            "Studio StudioModal"
        } else {
            "Studio"
        };

        div()
            .id("larik-studio-root")
            .track_focus(&self.focus_handle)
            .key_context(key_ctx)
            .on_action(cx.listener(|this, _: &RunStatement, _, cx| {
                if !this.managing_workspaces {
                    this.run_query(true, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &RunAll, _, cx| {
                if !this.managing_workspaces {
                    this.run_query(false, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &NewTab, window, cx| {
                if !this.managing_workspaces {
                    this.prompt_new_tab(window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &CloseTab, window, cx| {
                if !this.managing_workspaces {
                    if let Some(tab_id) = this.active_tab_id() {
                        this.archive_tab(tab_id, window, cx);
                    }
                }
            }))
            .on_action(cx.listener(|this, _: &NextTab, window, cx| {
                if !this.managing_workspaces {
                    this.cycle_tab(1, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &PrevTab, window, cx| {
                if !this.managing_workspaces {
                    this.cycle_tab(-1, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleRail, _, cx| {
                if !this.managing_workspaces {
                    this.toggle_rail(cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleSchema, _, cx| {
                if !this.managing_workspaces {
                    this.toggle_schema_pane(cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleTheme, window, cx| {
                this.toggle_theme(window, cx);
            }))
            .on_action(cx.listener(|this, _: &DismissModal, _, cx| {
                if this.modal.is_some() {
                    this.modal = None;
                    cx.notify();
                } else if this.db_palette_open {
                    this.close_db_palette(cx);
                } else if this.renaming_tab_id.is_some() {
                    this.cancel_rename(cx);
                } else if this.workspace_dropdown_open
                    || this.db_dropdown_open
                    || this.archive_open
                {
                    this.workspace_dropdown_open = false;
                    this.db_dropdown_open = false;
                    this.archive_open = false;
                    cx.notify();
                } else if this.managing_workspaces {
                    this.close_manage_workspaces(cx);
                }
            }))
            .relative()
            .size_full()
            .bg(palette.window_bg)
            .text_color(palette.text_primary)
            .flex()
            .flex_row()
            .overflow_hidden()
            // Full-page Manage Workspaces view OR main Studio workspace
            .when(self.managing_workspaces, |el| {
                el.child(self.render_manage_workspaces_page(active_space, palette, cx))
            })
            .when(!self.managing_workspaces, |el| {
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
                        el.child(self.render_schema_drawer(space_rt, palette, cx))
                    })
                    // Column 2: Main right workspace column (TitleBar + Query Toolbar + Cards + StatusBar)
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .h_full()
                            .overflow_hidden()
                            .child(self.render_title_bar(palette, cx))
                            .child(self.render_main_surface(
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
                            )),
                    )
                    // Floating Workspace Switcher Flyout (overlays UI, never pushes sidebar elements)
                    .when(self.workspace_dropdown_open, |el| {
                        el.child(self.render_workspace_flyout(active_space, space_rt, palette, cx))
                    })
                    // Floating Database Selector Flyout
                    .when(self.db_dropdown_open, |el| {
                        el.child(self.render_db_flyout(active_space, space_rt, palette, cx))
                    })
                    // Floating Archived Tabs Flyout
                    .when(self.archive_open, |el| {
                        el.child(self.render_archive_flyout(active_space, palette, cx))
                    })
                    // Searchable New-Tab Database Palette Overlay
                    .when(self.db_palette_open, |el| {
                        el.child(self.render_db_palette(active_space, space_rt, palette, cx))
                    })
            })
            // Provider-Generated Connection Modal (works from both Studio and Manage Workspaces)
            .when_some(self.modal.as_ref(), |el, modal| {
                el.child(render_connection_modal(
                    modal,
                    &self.catalog,
                    palette,
                    cx,
                    |this, _, cx| {
                        this.modal = None;
                        cx.notify();
                    },
                    |this, pid, window, cx| {
                        if let Some(m) = this.modal.as_mut() {
                            m.switch_provider(&pid, &this.catalog, window, cx);
                        }
                        cx.notify();
                    },
                    |this, hex, _, cx| {
                        if let Some(m) = this.modal.as_mut() {
                            m.color = hex;
                        }
                        cx.notify();
                    },
                    |this, key, val, _, cx| {
                        if let Some(m) = this.modal.as_mut() {
                            m.bool_values.insert(key, val);
                        }
                        cx.notify();
                    },
                    |this, _, cx| {
                        if let Some(m) = this.modal.as_mut() {
                            m.show_advanced = !m.show_advanced;
                        }
                        cx.notify();
                    },
                    |this, _, cx| this.test_modal_connection(cx),
                    |this, _, cx| this.save_modal_and_connect(cx),
                    |this, confirm, _, cx| {
                        if let Some(m) = this.modal.as_mut() {
                            m.confirming_delete = confirm;
                        }
                        cx.notify();
                    },
                    |this, window, cx| {
                        if let Some(id) = this.modal.as_ref().map(|m| m.space_id.clone()) {
                            this.delete_workspace(id, window, cx);
                        }
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
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        TitleBar::new()
            .bg(transparent_black())
            .border_b_0()
            .child(
                h_flex()
                    .w_full()
                    .items_center()
                    .gap_2()
                    .pl_1()
                    .child(
                        div()
                            .id("titlebar-toggle-rail")
                            .size(px(28.0))
                            .rounded(px(5.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.toggle_rail(cx);
                            }))
                            .child(
                                Icon::new(IconName::Menu)
                                    .size(px(14.0))
                                    .text_color(palette.text_secondary),
                            ),
                    )
                    .child(
                        h_flex()
                            .items_center()
                            .gap_2()
                            .child(
                                img(self.app_icon.clone())
                                    .size(px(18.0))
                                    .rounded(px(4.0)),
                            )
                            .child(
                                div()
                                    .text_size(px(12.0))
                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                    .text_color(palette.text_primary)
                                    .child("Larik SQL Studio"),
                            ),
                    ),
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

        let space_initial = active_space
            .and_then(|s| s.name.chars().next())
            .map(|c| c.to_uppercase().to_string())
            .unwrap_or_else(|| "L".into());

        let space_name = active_space
            .map(|s| s.name.clone())
            .unwrap_or_else(|| "Workspace".into());

        let space_id_for_connect = active_space.map(|s| s.id.clone());
        let space_id_for_settings = active_space.map(|s| s.id.clone());
        let has_archived_for_space = active_space.is_some_and(|s| {
            self.state
                .archived_tabs
                .iter()
                .any(|a| a.space_id == s.id)
        });

        let ws_open = self.workspace_dropdown_open;
        let schema_open = self.state.schema_pane_open;

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
                                this.archive_open = false;
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
                                            .child(space_initial),
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
            // Row 2: [DB] Database Selector + Schema Icon Button on one row (floating flyout)
            .when(
                conn_state == ConnectionState::Connected && !space_rt.databases.is_empty(),
                |el| {
                    let current_db = active_space
                        .and_then(|s| s.active_database.clone())
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
                                        this.archive_open = false;
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
            // Row 3: TABS Section Header + Archive/History Icon Button (floating flyout)
            .child(
                h_flex()
                    .pl_3p5()
                    .pr_2p5()
                    .pt_2()
                    .pb_1()
                    .items_center()
                    .justify_between()
                    .child(
                        div()
                            .text_size(px(10.0))
                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            .text_color(palette.text_muted)
                            .child("TABS"),
                    )
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
                            .when(self.archive_open, |d| d.bg(palette.subtle_fill))
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.archive_open = !this.archive_open;
                                this.workspace_dropdown_open = false;
                                this.db_dropdown_open = false;
                                cx.notify();
                            }))
                            .child(
                                Icon::new(IconName::RotateCcw)
                                    .size(px(12.5))
                                    .text_color(if self.archive_open {
                                        palette.accent
                                    } else {
                                        palette.text_muted
                                    }),
                            )
                            .when(has_archived_for_space, |d| {
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
            // Row 4: Full-width "+ New tab    Ctrl+T" row (transparent over Mica, hover fill)
            .child(
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
                    ),
            )
            // Row 5: Vertical Tabs List
            .child(
                div()
                    .id("rail-tabs-scroll")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .px_1p5()
                    .pb_2()
                    .child(v_flex().gap_1().children({
                        let mut rows: Vec<AnyElement> = Vec::new();
                        if let Some(space) = active_space {
                            for tab in &space.tabs {
                                rows.push(
                                    self.render_tab_row(
                                        tab,
                                        active_tab.is_some_and(|a| a.id == tab.id),
                                        space_color,
                                        palette,
                                        cx,
                                    )
                                    .into_any_element(),
                                );
                            }
                        }
                        rows
                    })),
            )
            // Row 6: WinUI Sidebar Footer (Settings gear | Centered Monogram Chips + | Theme)
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
                            .size(px(26.0))
                            .rounded(px(5.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(move |this, _, window, cx| {
                                if let Some(id) = space_id_for_settings.clone() {
                                    this.open_connection_modal(id, window, cx);
                                }
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
                            .children(self.state.spaces.iter().filter_map(|space| {
                                let is_active = active_space.is_some_and(|a| a.id == space.id);
                                if space.is_hidden && !is_active {
                                    return None;
                                }
                                let sid = space.id.clone();
                                let initial = space
                                    .name
                                    .chars()
                                    .next()
                                    .map(|c| c.to_uppercase().to_string())
                                    .unwrap_or_else(|| "W".into());

                                Some(
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
                                        .child(initial),
                                )
                            }))
                            .child(
                                div()
                                    .id("add-workspace-btn")
                                    .size(px(22.0))
                                    .rounded(px(5.0))
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .cursor_pointer()
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.create_workspace(window, cx);
                                    }))
                                    .child(
                                        Icon::new(IconName::Plus)
                                            .size(px(11.5))
                                            .text_color(palette.text_muted),
                                    ),
                            ),
                    )
                    .child(
                        div()
                            .id("footer-theme-btn")
                            .size(px(26.0))
                            .rounded(px(5.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.toggle_theme(window, cx);
                            }))
                            .child(
                                Icon::new(match self.state.theme {
                                    ThemePreference::Dark => IconName::Sun,
                                    ThemePreference::Light => IconName::Moon,
                                })
                                .size(px(13.0))
                                .text_color(palette.text_muted),
                            ),
                    ),
            )
    }

    /// Floating Workspace Switcher Flyout matching WinUI Larik reference:
    /// overlays the UI without expanding or shifting sidebar elements below.
    fn render_workspace_flyout(
        &self,
        active_space: Option<&SpaceState>,
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
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

        let is_dark = self.state.theme == ThemePreference::Dark;
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
                                let initial = space
                                    .name
                                    .chars()
                                    .next()
                                    .map(|c| c.to_uppercase().to_string())
                                    .unwrap_or_else(|| "W".into());
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
                                                        .text_color(if is_active {
                                                            palette.text_primary
                                                        } else {
                                                            palette.text_secondary
                                                        })
                                                        .child(initial),
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
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
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
                        let is_selected = active_space
                            .and_then(|s| s.active_database.as_deref())
                            .is_some_and(|cur| cur.eq_ignore_ascii_case(&db_name));
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
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        this.select_database(db_name.clone(), cx);
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

    /// Floating Archived Tabs Flyout positioned near the TABS history button.
    fn render_archive_flyout(
        &self,
        active_space: Option<&SpaceState>,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let archived_for_space: Vec<&ArchivedTabState> = active_space
            .map(|s| {
                self.state
                    .archived_tabs
                    .iter()
                    .filter(|a| a.space_id == s.id)
                    .collect()
            })
            .unwrap_or_default();

        div()
            .id("archive-flyout-backdrop")
            .absolute()
            .inset_0()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    this.archive_open = false;
                    cx.notify();
                }),
            )
            .child(
                v_flex()
                    .id("archive-flyout-card")
                    .absolute()
                    .top(px(142.0))
                    .left(px(8.0))
                    .w(px(244.0))
                    .max_h(px(220.0))
                    .overflow_y_scroll()
                    .p_2()
                    .rounded(px(8.0))
                    .bg(palette.modal_bg)
                    .border_1()
                    .border_color(palette.card_border)
                    .shadow_2xl()
                    .gap_1()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    .child(
                        div()
                            .px_1p5()
                            .pb_0p5()
                            .text_size(px(10.0))
                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            .text_color(palette.text_muted)
                            .child("ARCHIVED TABS"),
                    )
                    .when(archived_for_space.is_empty(), |box_el| {
                        box_el.child(
                            div()
                                .px_1p5()
                                .py_1()
                                .text_xs()
                                .text_color(palette.text_muted)
                                .child("No archived tabs yet."),
                        )
                    })
                    .children(archived_for_space.into_iter().map(|arch| {
                        let aid = arch.id.clone();
                        h_flex()
                            .id(SharedString::from(format!("arch-{aid}")))
                            .px_2()
                            .py_1p5()
                            .rounded(px(5.0))
                            .items_center()
                            .justify_between()
                            .gap_2()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .on_click(cx.listener(move |this, _, window, cx| {
                                this.archive_open = false;
                                this.restore_archived_tab(aid.clone(), window, cx);
                            }))
                            .child(
                                div()
                                    .text_xs()
                                    .text_color(palette.text_secondary)
                                    .truncate()
                                    .child(arch.title.clone()),
                            )
                            .child(
                                div()
                                    .flex_shrink_0()
                                    .text_size(px(10.5))
                                    .text_color(palette.accent)
                                    .child("Restore"),
                            )
                    })),
            )
    }

    /// Full-page Manage Workspaces view matching the WinUI Larik reference screenshot.
    fn render_manage_workspaces_page(
        &self,
        active_space: Option<&SpaceState>,
        palette: StudioPalette,
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
            // Top transparent TitleBar: Back arrow + Larik icon + "Workspaces" + native window controls
            .child(
                TitleBar::new()
                    .bg(transparent_black())
                    .border_b_0()
                    .child(
                        h_flex()
                            .w_full()
                            .items_center()
                            .gap_2()
                            .pl_1()
                            .child(
                                div()
                                    .id("manage-ws-back-btn")
                                    .size(px(28.0))
                                    .rounded(px(5.0))
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .cursor_pointer()
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.close_manage_workspaces(cx);
                                    }))
                                    .child(
                                        Icon::new(IconName::ArrowLeft)
                                            .size(px(14.0))
                                            .text_color(palette.text_secondary),
                                    ),
                            )
                            .child(
                                h_flex()
                                    .items_center()
                                    .gap_2()
                                    .child(
                                        img(self.app_icon.clone())
                                            .size(px(18.0))
                                            .rounded(px(4.0)),
                                    )
                                    .child(
                                        div()
                                            .text_size(px(12.0))
                                            .font_weight(gpui_kit::FontWeight::MEDIUM)
                                            .text_color(palette.text_primary)
                                            .child("Workspaces"),
                                    ),
                            ),
                    ),
            )
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
                                            let is_unverified = space
                                                .profile
                                                .get_bool("trustServerCertificate", false);
                                            let can_move_up = idx > 0;
                                            let can_move_down = idx + 1 < workspace_count;

                                            let sid_open = space.id.clone();
                                            let sid_up = space.id.clone();
                                            let sid_down = space.id.clone();
                                            let sid_hide = space.id.clone();
                                            let sid_edit = space.id.clone();

                                            let initial = space
                                                .name
                                                .chars()
                                                .next()
                                                .map(|c| c.to_uppercase().to_string())
                                                .unwrap_or_else(|| "W".into());
                                            let tint = parse_hex_color(&space.color);

                                            let provider =
                                                self.catalog.find(&space.profile.provider_id);
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
                                                // Left group: reorder controls + 30x30 monogram + title/subtitle
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
                                                                .child(initial),
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

    fn render_tab_row(
        &self,
        tab: &TabState,
        is_active: bool,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let tab_id_click = tab.id.clone();
        let tab_id_mid = tab.id.clone();
        let tab_id_close = tab.id.clone();
        let is_running = self
            .tab_runtime
            .get(&tab.id)
            .is_some_and(|rt| rt.running);
        let is_renaming = self.renaming_tab_id.as_deref() == Some(tab.id.as_str());

        h_flex()
            .id(SharedString::from(format!("tab-row-{}", tab.id)))
            .group("tab-row")
            .relative()
            .pl_3()
            .pr_2()
            .py_2()
            .rounded(px(6.0))
            .items_center()
            .gap_2()
            .cursor_pointer()
            .when(is_active, |el| {
                el.bg(palette.tab_active_bg)
                    .border_1()
                    .border_color(palette.card_border)
            })
            .when(!is_active, |el| el.hover(|s| s.bg(palette.subtle_hover)))
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
                        .top(px(8.0))
                        .bottom(px(8.0))
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
            // Close button (visible on active tab or on hover)
            .when(!is_renaming, |el| {
                el.child(
                    div()
                        .id(SharedString::from(format!("close-{}", tab.id)))
                        .size(px(20.0))
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
                                        this.run_query(false, cx);
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
                                        this.run_query(true, cx);
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
        tab_rt: &TabRuntime,
        space_color: gpui_kit::Hsla,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let active_result = tab_rt
            .results
            .get(tab_rt.active_result_idx)
            .map(Arc::as_ref);
        let message_count: usize = tab_rt.results.iter().map(|r| r.messages.len()).sum();
        let has_grid = !tab_rt.show_messages
            && active_result.is_some_and(|r| !r.is_error && !r.columns.is_empty());

        v_flex()
            .size_full()
            .rounded(px(8.0))
            .bg(palette.card_bg)
            .border_1()
            .border_color(palette.card_border)
            .overflow_hidden()
            // Quiet Fluent Result Tabs Bar
            .child(
                h_flex()
                    .h(px(34.0))
                    .px_3()
                    .border_b_1()
                    .border_color(palette.hairline_subtle)
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .h_full()
                            .items_center()
                            .gap_1()
                            .children(tab_rt.results.iter().enumerate().map(|(idx, res)| {
                                let selected =
                                    !tab_rt.show_messages && idx == tab_rt.active_result_idx;
                                let is_err = res.is_error;
                                let label = if is_err {
                                    format!("Error {}", idx + 1)
                                } else {
                                    format!("Result {} · {}", idx + 1, res.rows.len())
                                };
                                h_flex()
                                    .id(SharedString::from(format!("res-tab-{idx}")))
                                    .relative()
                                    .h_full()
                                    .px_2p5()
                                    .items_center()
                                    .gap_1p5()
                                    .cursor_pointer()
                                    .when(!selected, |d| {
                                        d.hover(|s| s.bg(palette.subtle_hover))
                                    })
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        if let Some(tab_id) = this.active_tab_id() {
                                            if let Some(rt) = this.tab_runtime.get_mut(&tab_id) {
                                                rt.active_result_idx = idx;
                                                rt.show_messages = false;
                                            }
                                            this.sync_table_from_active_tab(cx);
                                            cx.notify();
                                        }
                                    }))
                                    .when(is_err, |d| {
                                        d.child(
                                            div()
                                                .size(px(6.0))
                                                .rounded_full()
                                                .bg(palette.danger),
                                        )
                                    })
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
                                            .child(label),
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
                                                .bg(if is_err {
                                                    palette.danger
                                                } else {
                                                    space_color
                                                }),
                                        )
                                    })
                            }))
                            .when(message_count > 0, |el| {
                                let selected = tab_rt.show_messages;
                                el.child(
                                    h_flex()
                                        .id("res-tab-messages")
                                        .relative()
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
                                                .child(format!("Messages · {message_count}")),
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
                        .size_full()
                        .p_4()
                        .gap_1p5()
                        .font_family(cx.theme().mono_font_family.clone())
                        .text_xs()
                        .text_color(palette.text_secondary)
                        .children(
                            tab_rt
                                .results
                                .iter()
                                .flat_map(|r| r.messages.iter())
                                .map(|m| div().child(m.clone())),
                        )
                        .into_any_element()
                } else if let Some(res) = active_result {
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
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let space_color = active_space
            .map(|s| parse_hex_color(&s.color))
            .unwrap_or(palette.accent);
        let current_db = active_space.and_then(|s| s.active_database.clone());
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
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1p5()
                            .child(
                                div()
                                    .text_xs()
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                    .text_color(palette.text_primary)
                                    .child("Schema"),
                            )
                            .child(
                                div()
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
