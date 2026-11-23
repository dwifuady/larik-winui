use std::collections::{HashMap, HashSet};
use std::rc::Rc;
use std::sync::{Arc, RwLock};
use std::time::Duration;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Editor, EditorState, Input, InputEvent, InputState};
use gpui_kit::component::resizable::{resizable_panel, v_resizable};
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::table::{DataTable, TableState};
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::base::Disableable as _;
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, AppContext as _, ClipboardItem, Context, Entity, FocusHandle, Focusable,
    InteractiveElement as _, IntoElement, KeyBinding, ParentElement as _, Render, SharedString,
    StatefulInteractiveElement as _, Styled as _, Subscription, Window, div, px,
};
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
        KeyBinding::new("ctrl-k", ToggleSchema, None),
        KeyBinding::new("ctrl-shift-l", ToggleTheme, None),
        KeyBinding::new("escape", DismissModal, Some("StudioModal")),
    ]);
}

#[derive(Default, Clone)]
struct SpaceRuntime {
    state: Option<ConnectionState>,
    server_version: Option<String>,
    databases: Vec<DatabaseInfo>,
    schema: Vec<TableSchema>,
    last_error: Option<String>,
}

impl SpaceRuntime {
    fn connection_state(&self) -> ConnectionState {
        self.state.unwrap_or(ConnectionState::Disconnected)
    }
}

#[derive(Default, Clone)]
struct TabRuntime {
    running: bool,
    results: Vec<QueryResult>,
    active_result_idx: usize,
    show_messages: bool,
    last_summary: Option<String>,
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

    // UI transient controls
    db_dropdown_open: bool,
    archive_open: bool,
    expanded_tables: HashSet<String>,
    renaming_tab_id: Option<String>,
    rename_input: Option<Entity<InputState>>,
    _rename_sub: Option<Subscription>,
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
            ed.lsp_mut().completion_provider = Some(Rc::new(SqlCompletionProvider::new(provider_ref)));
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
            db_dropdown_open: false,
            archive_open: false,
            expanded_tables: HashSet::new(),
            renaming_tab_id: None,
            rename_input: None,
            _rename_sub: None,
            modal: None,
            copied_toast: false,
        };

        view.refresh_completion_snapshot();
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
        let tables = self
            .space_runtime
            .get(&space.id)
            .map(|rt| rt.schema.clone())
            .unwrap_or_default();
        let language = self
            .catalog
            .find(&space.profile.provider_id)
            .map(|p| p.editor_language())
            .unwrap_or_else(|| Arc::new(SqlLanguage::tsql()));

        if let Ok(mut guard) = self.completion_snapshot.write() {
            guard.tables = tables;
            guard.language = language;
        }
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
        let active_result = self.active_tab_id().and_then(|tab_id| {
            let rt = self.tab_runtime.get(&tab_id)?;
            if rt.show_messages {
                return None;
            }
            rt.results.get(rt.active_result_idx).cloned()
        });

        self.table_state.update(cx, |table, cx| {
            table.delegate_mut().set_result(active_result.as_ref());
            table.refresh(cx);
        });
    }

    fn switch_space(&mut self, space_id: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.active_space_id.as_deref() == Some(space_id.as_str()) {
            return;
        }
        self.state.active_space_id = Some(space_id);
        self.db_dropdown_open = false;
        self.cancel_rename(cx);
        self.sync_editor_from_active_tab(window, cx);
        self.refresh_completion_snapshot();
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
        });
        self.state.active_space_id = Some(space_id.clone());
        self.sync_editor_from_active_tab(window, cx);
        self.refresh_completion_snapshot();
        self.persist();
        self.open_connection_modal(space_id, window, cx);
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
                        rt.server_version = outcome.server_version;
                        rt.databases = outcome.databases;
                        rt.schema = outcome.schema;
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
                    }
                    Ok(Err(err)) => {
                        let rt = this.space_runtime.entry(space_id.clone()).or_default();
                        rt.state = Some(ConnectionState::Faulted);
                        rt.last_error = Some(err.to_string());
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
        rt.databases.clear();
        rt.schema.clear();
        rt.server_version = None;
        rt.last_error = None;
        self.db_dropdown_open = false;
        self.refresh_completion_snapshot();
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
        self.db_dropdown_open = false;
        self.persist();
        cx.notify();

        let rx = self
            .sessions
            .load_schema(space_id.clone(), Some(database));
        cx.spawn(async move |this, cx| {
            if let Ok(Ok(schema)) = rx.await {
                let _ = this.update(cx, |this, cx| {
                    this.space_runtime
                        .entry(space_id)
                        .or_default()
                        .schema = schema;
                    this.refresh_completion_snapshot();
                    cx.notify();
                });
            }
        })
        .detach();
    }

    fn new_tab(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let title = self.state.next_query_title();
        let tab_id = Uuid::new_v4().to_string();
        if let Some(space) = self.state.active_space_mut() {
            let db = space.active_database.clone();
            space.tabs.push(TabState {
                id: tab_id.clone(),
                title,
                database: db,
                content: String::new(),
            });
            space.active_tab_id = Some(tab_id);
        }
        self.sync_editor_from_active_tab(window, cx);
        self.persist();
        cx.notify();
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

        // Non-destructive close: file into archive if it has content or a custom title.
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
            self.new_tab(window, cx);
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
            let state = InputState::new(window, cx)
                .placeholder("Tab name")
                .default_value(current_title);
            state
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

        // Ensure connected first; if disconnected, auto-connect & report cleanly.
        let conn_state = self
            .space_runtime
            .get(&space.id)
            .map(SpaceRuntime::connection_state)
            .unwrap_or(ConnectionState::Disconnected);
        if conn_state != ConnectionState::Connected {
            let rt = self.tab_runtime.entry(tab.id.clone()).or_default();
            rt.results = vec![QueryResult::error(
                "Workspace is disconnected. Click Connect in the left rail first.",
                Duration::ZERO,
                tab.database.clone(),
            )];
            rt.active_result_idx = 0;
            rt.show_messages = false;
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

                match outcome {
                    Ok(Ok(results)) => {
                        let total_rows: usize = results.iter().map(|r| r.rows.len()).sum();
                        let total_ms: u128 = results.iter().map(|r| r.elapsed.as_millis()).sum();
                        let has_err = results.iter().any(|r| r.is_error);
                        let first_grid = results.iter().position(|r| !r.columns.is_empty());

                        rt.last_summary = Some(if has_err {
                            format!("Failed in {total_ms} ms")
                        } else {
                            format!("{total_rows} rows · {total_ms} ms")
                        });
                        rt.active_result_idx = first_grid.unwrap_or(0);
                        rt.show_messages = first_grid.is_none()
                            && !has_err
                            && results.iter().any(|r| !r.messages.is_empty());
                        rt.results = results;
                    }
                    Ok(Err(err)) => {
                        rt.last_summary = Some("Error".into());
                        rt.results = vec![QueryResult::error(
                            err.to_string(),
                            Duration::ZERO,
                            None,
                        )];
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
        let Some(space) = self.state.spaces.iter().find(|s| s.id == space_id) else {
            return;
        };
        self.modal = Some(ConnectionModal::open(
            space.id.clone(),
            &space.name,
            &space.color,
            &space.profile,
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
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = StudioPalette::for_mode(self.state.theme);
        let active_space = self.state.active_space().cloned();
        let active_tab = active_space.as_ref().and_then(|s| s.active_tab()).cloned();
        let space_rt = active_space
            .as_ref()
            .and_then(|s| self.space_runtime.get(&s.id))
            .cloned()
            .unwrap_or_default();
        let tab_rt = active_tab
            .as_ref()
            .and_then(|t| self.tab_runtime.get(&t.id))
            .cloned()
            .unwrap_or_default();

        let key_ctx = if self.modal.is_some() {
            "Studio StudioModal"
        } else {
            "Studio"
        };

        div()
            .id("larik-studio-root")
            .track_focus(&self.focus_handle)
            .key_context(key_ctx)
            .on_action(cx.listener(|this, _: &RunStatement, _, cx| {
                this.run_query(true, cx);
            }))
            .on_action(cx.listener(|this, _: &RunAll, _, cx| {
                this.run_query(false, cx);
            }))
            .on_action(cx.listener(|this, _: &NewTab, window, cx| {
                this.new_tab(window, cx);
            }))
            .on_action(cx.listener(|this, _: &CloseTab, window, cx| {
                if let Some(tab_id) = this.active_tab_id() {
                    this.archive_tab(tab_id, window, cx);
                }
            }))
            .on_action(cx.listener(|this, _: &ToggleSchema, _, cx| {
                this.toggle_schema_pane(cx);
            }))
            .on_action(cx.listener(|this, _: &ToggleTheme, window, cx| {
                this.toggle_theme(window, cx);
            }))
            .on_action(cx.listener(|this, _: &DismissModal, _, cx| {
                this.modal = None;
                cx.notify();
            }))
            .relative()
            .size_full()
            .bg(palette.background)
            .text_color(palette.text_primary)
            .flex()
            .flex_col()
            .overflow_hidden()
            // Body: Left Arc Rail + Main Workspace + Optional Right Schema Peek
            .child(
                h_flex()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .overflow_hidden()
                    .child(self.render_left_rail(
                        active_space.as_ref(),
                        active_tab.as_ref(),
                        &space_rt,
                        palette,
                        cx,
                    ))
                    .child(self.render_main_surface(
                        active_space.as_ref(),
                        active_tab.as_ref(),
                        &space_rt,
                        &tab_rt,
                        palette,
                        cx,
                    ))
                    .when(self.state.schema_pane_open, |el| {
                        el.child(self.render_schema_drawer(&space_rt, palette, cx))
                    }),
            )
            // Bottom Status Bar
            .child(self.render_status_bar(
                active_space.as_ref(),
                active_tab.as_ref(),
                &space_rt,
                &tab_rt,
                palette,
            ))
            // Provider-Generated Connection Modal
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
                ))
            })
    }
}

impl StudioView {
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

        let (status_color, status_text) = match conn_state {
            ConnectionState::Connected => (palette.success, "Connected"),
            ConnectionState::Connecting => (palette.warning, "Connecting…"),
            ConnectionState::Faulted => (palette.danger, "Failed"),
            ConnectionState::Disconnected => (palette.text_muted, "Disconnected"),
        };

        let endpoint_text = active_space
            .and_then(|s| {
                self.catalog
                    .find(&s.profile.provider_id)
                    .and_then(|p| p.describe_endpoint(&s.profile))
            })
            .unwrap_or_else(|| "Configure connection".to_string());

        let space_initial = active_space
            .and_then(|s| s.name.chars().next())
            .map(|c| c.to_uppercase().to_string())
            .unwrap_or_else(|| "L".into());

        let space_id_for_connect = active_space.map(|s| s.id.clone());
        let space_id_for_edit = active_space.map(|s| s.id.clone());
        let archived_for_space: Vec<ArchivedTabState> = active_space
            .map(|s| {
                self.state
                    .archived_tabs
                    .iter()
                    .filter(|a| a.space_id == s.id)
                    .cloned()
                    .collect()
            })
            .unwrap_or_default();

        v_flex()
            .w(px(252.0))
            .h_full()
            .flex_shrink_0()
            .bg(palette.rail_bg)
            .border_r_1()
            .border_color(palette.hairline)
            // Workspace Header Card
            .child(
                v_flex()
                    .px_3()
                    .pt_3()
                    .pb_2p5()
                    .gap_2()
                    .border_b_1()
                    .border_color(palette.hairline_subtle)
                    .child(
                        h_flex()
                            .items_center()
                            .gap_2p5()
                            .child(
                                div()
                                    .size(px(28.0))
                                    .rounded(px(8.0))
                                    .bg(space_color.opacity(0.22))
                                    .border_1()
                                    .border_color(space_color.opacity(0.5))
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .text_xs()
                                    .font_weight(gpui_kit::FontWeight::BOLD)
                                    .text_color(space_color)
                                    .child(space_initial),
                            )
                            .child(
                                v_flex()
                                    .flex_1()
                                    .min_w_0()
                                    .child(
                                        div()
                                            .text_sm()
                                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                            .text_color(palette.text_primary)
                                            .truncate()
                                            .child(
                                                active_space
                                                    .map(|s| s.name.clone())
                                                    .unwrap_or_else(|| "Larik".into()),
                                            ),
                                    )
                                    .child(
                                        div()
                                            .text_xs()
                                            .text_color(palette.text_muted)
                                            .truncate()
                                            .child(endpoint_text),
                                    ),
                            )
                            .child(
                                Button::new("edit-space-connection")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::Settings2)
                                    .tooltip("Workspace connection settings")
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        if let Some(id) = space_id_for_edit.clone() {
                                            this.open_connection_modal(id, window, cx);
                                        }
                                    })),
                            ),
                    )
                    // Status pill + Connect/Disconnect button (text + colour, never colour alone)
                    .child(
                        h_flex()
                            .items_center()
                            .justify_between()
                            .gap_2()
                            .child(
                                h_flex()
                                    .items_center()
                                    .gap_1p5()
                                    .child(
                                        div()
                                            .size(px(7.0))
                                            .rounded_full()
                                            .bg(status_color),
                                    )
                                    .child(
                                        div()
                                            .text_xs()
                                            .text_color(status_color)
                                            .font_weight(gpui_kit::FontWeight::MEDIUM)
                                            .child(status_text),
                                    ),
                            )
                            .child(match conn_state {
                                ConnectionState::Connected => Button::new("rail-disconnect")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::Unplug)
                                    .label("Disconnect")
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        if let Some(id) = space_id_for_connect.clone() {
                                            this.disconnect_space(id, cx);
                                        }
                                    })),
                                ConnectionState::Connecting => Button::new("rail-connecting")
                                    .ghost()
                                    .xsmall()
                                    .loading(true)
                                    .label("Connecting"),
                                _ => Button::new("rail-connect")
                                    .primary()
                                    .xsmall()
                                    .icon(IconName::Plug)
                                    .label("Connect")
                                    .on_click(cx.listener(move |this, _, _, cx| {
                                        if let Some(id) = space_id_for_connect.clone() {
                                            this.connect_space(id, cx);
                                        }
                                    })),
                            }),
                    )
                    // Inline error hint if connection failed
                    .when_some(space_rt.last_error.clone(), |el, err| {
                        el.child(
                            div()
                                .px_2()
                                .py_1p5()
                                .rounded(px(6.0))
                                .bg(palette.danger.opacity(0.12))
                                .border_1()
                                .border_color(palette.danger.opacity(0.3))
                                .text_xs()
                                .text_color(palette.danger)
                                .child(err),
                        )
                    })
                    // Database selector (when connected)
                    .when(
                        conn_state == ConnectionState::Connected && !space_rt.databases.is_empty(),
                        |el| {
                            let current_db = active_space
                                .and_then(|s| s.active_database.clone())
                                .unwrap_or_else(|| "Select database".into());
                            let dropdown_open = self.db_dropdown_open;
                            el.child(
                                v_flex()
                                    .gap_1()
                                    .child(
                                        div()
                                            .id("rail-db-selector")
                                            .px_2p5()
                                            .py_1p5()
                                            .rounded(px(6.0))
                                            .bg(palette.elevated)
                                            .border_1()
                                            .border_color(palette.hairline)
                                            .cursor_pointer()
                                            .flex()
                                            .items_center()
                                            .justify_between()
                                            .gap_2()
                                            .on_click(cx.listener(|this, _, _, cx| {
                                                this.db_dropdown_open = !this.db_dropdown_open;
                                                cx.notify();
                                            }))
                                            .child(
                                                h_flex()
                                                    .gap_1p5()
                                                    .items_center()
                                                    .min_w_0()
                                                    .child(
                                                        Icon::new(IconName::Database)
                                                            .size(px(13.0))
                                                            .text_color(space_color),
                                                    )
                                                    .child(
                                                        div()
                                                            .text_xs()
                                                            .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                            .text_color(palette.text_primary)
                                                            .truncate()
                                                            .child(current_db),
                                                    ),
                                            )
                                            .child(
                                                Icon::new(if dropdown_open {
                                                    IconName::ChevronUp
                                                } else {
                                                    IconName::ChevronDown
                                                })
                                                .size(px(12.0))
                                                .text_color(palette.text_muted),
                                            ),
                                    )
                                    .when(dropdown_open, |el| {
                                        el.child(
                                            v_flex()
                                                .max_h(px(160.0))
                                                .id("rail-db-list")
                                                .overflow_y_scroll()
                                                .p_1()
                                                .rounded(px(6.0))
                                                .bg(palette.background)
                                                .border_1()
                                                .border_color(palette.hairline)
                                                .gap_0p5()
                                                .children(space_rt.databases.iter().map(|db| {
                                                    let db_name = db.name.clone();
                                                    let is_selected = active_space
                                                        .and_then(|s| s.active_database.as_deref())
                                                        .is_some_and(|cur| {
                                                            cur.eq_ignore_ascii_case(&db_name)
                                                        });
                                                    let accessible = db.is_accessible;
                                                    div()
                                                        .id(SharedString::from(format!(
                                                            "db-opt-{db_name}"
                                                        )))
                                                        .px_2()
                                                        .py_1()
                                                        .rounded(px(4.0))
                                                        .text_xs()
                                                        .when(accessible, |d| {
                                                            d.cursor_pointer()
                                                                .hover(|s| s.bg(palette.elevated))
                                                                .on_click(cx.listener(
                                                                    move |this, _, _, cx| {
                                                                        this.select_database(
                                                                            db_name.clone(),
                                                                            cx,
                                                                        );
                                                                    },
                                                                ))
                                                        })
                                                        .when(is_selected, |d| {
                                                            d.bg(space_color.opacity(0.16))
                                                                .text_color(palette.text_primary)
                                                                .font_weight(
                                                                    gpui_kit::FontWeight::SEMIBOLD,
                                                                )
                                                        })
                                                        .when(!accessible, |d| {
                                                            d.text_color(
                                                                palette.text_muted.opacity(0.55),
                                                            )
                                                        })
                                                        .child(if accessible {
                                                            db.name.clone()
                                                        } else {
                                                            format!("{} (no access)", db.name)
                                                        })
                                                })),
                                        )
                                    }),
                            )
                        },
                    ),
            )
            // Vertical Tabs Section
            .child(
                v_flex()
                    .flex_1()
                    .min_h_0()
                    .px_2()
                    .py_2()
                    .gap_1()
                    .child(
                        h_flex()
                            .px_1p5()
                            .pb_1()
                            .items_center()
                            .justify_between()
                            .child(
                                div()
                                    .text_xs()
                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                    .text_color(palette.text_muted)
                                    .child("QUERIES"),
                            )
                            .child(
                                Button::new("rail-new-tab")
                                    .ghost()
                                    .xsmall()
                                    .icon(IconName::Plus)
                                    .label("New")
                                    .tooltip("New query tab (Ctrl+T)")
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.new_tab(window, cx);
                                    })),
                            ),
                    )
                    .child(
                        div()
                            .id("rail-tabs-scroll")
                            .flex_1()
                            .overflow_y_scroll()
                            .child(v_flex().gap_0p5().children({
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
                    // Collapsible Archive Band
                    .when(!archived_for_space.is_empty(), |el| {
                        let open = self.archive_open;
                        let count = archived_for_space.len();
                        el.child(
                            v_flex()
                                .pt_1p5()
                                .border_t_1()
                                .border_color(palette.hairline_subtle)
                                .gap_1()
                                .child(
                                    Button::new("toggle-archive")
                                        .ghost()
                                        .xsmall()
                                        .icon(IconName::Archive)
                                        .label(format!("Archive ({count})"))
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.archive_open = !this.archive_open;
                                            cx.notify();
                                        })),
                                )
                                .when(open, |el| {
                                    el.child(v_flex().max_h(px(120.0)).id("archive-list").overflow_y_scroll().gap_0p5().children(
                                        archived_for_space.into_iter().map(|arch| {
                                            let aid = arch.id.clone();
                                            h_flex()
                                                .id(SharedString::from(format!("arch-{aid}")))
                                                .px_2()
                                                .py_1()
                                                .rounded(px(5.0))
                                                .items_center()
                                                .justify_between()
                                                .cursor_pointer()
                                                .hover(|s| s.bg(palette.elevated))
                                                .on_click(cx.listener(
                                                    move |this, _, window, cx| {
                                                        this.restore_archived_tab(
                                                            aid.clone(),
                                                            window,
                                                            cx,
                                                        );
                                                    },
                                                ))
                                                .child(
                                                    div()
                                                        .text_xs()
                                                        .text_color(palette.text_secondary)
                                                        .truncate()
                                                        .child(arch.title),
                                                )
                                                .child(
                                                    div()
                                                        .text_xs()
                                                        .text_color(palette.text_muted)
                                                        .child("Restore"),
                                                )
                                        }),
                                    ))
                                }),
                        )
                    }),
            )
            // Bottom Arc Workspace Dots Switcher
            .child(
                h_flex()
                    .px_3()
                    .py_2p5()
                    .border_t_1()
                    .border_color(palette.hairline)
                    .items_center()
                    .justify_between()
                    .child(
                        h_flex()
                            .gap_2()
                            .items_center()
                            .children(self.state.spaces.iter().map(|space| {
                                let is_active =
                                    active_space.is_some_and(|a| a.id == space.id);
                                let dot_color = parse_hex_color(&space.color);
                                let sid = space.id.clone();
                                div()
                                    .id(SharedString::from(format!("space-dot-{sid}")))
                                    .size(px(22.0))
                                    .rounded_full()
                                    .cursor_pointer()
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .when(is_active, |el| {
                                        el.border_2().border_color(dot_color)
                                    })
                                    .when(!is_active, |el| {
                                        el.hover(|s| s.bg(palette.elevated))
                                    })
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.switch_space(sid.clone(), window, cx);
                                    }))
                                    .child(
                                        div()
                                            .size(px(10.0))
                                            .rounded_full()
                                            .bg(dot_color.opacity(if is_active { 1.0 } else { 0.55 })),
                                    )
                            })),
                    )
                    .child(
                        Button::new("add-workspace-btn")
                            .ghost()
                            .xsmall()
                            .icon(IconName::Plus)
                            .tooltip("New workspace")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.create_workspace(window, cx);
                            })),
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
        let tab_id = tab.id.clone();
        let tab_id_rename = tab.id.clone();
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
            .px_2p5()
            .py_1p5()
            .rounded(px(7.0))
            .items_center()
            .gap_2()
            .cursor_pointer()
            .when(is_active, |el| el.bg(space_color.opacity(0.15)))
            .when(!is_active, |el| el.hover(|s| s.bg(palette.elevated)))
            .on_click(cx.listener(move |this, _, window, cx| {
                this.switch_tab(tab_id.clone(), window, cx);
            }))
            // Arc 2px active indicator strip on the left edge
            .when(is_active, |el| {
                el.child(
                    div()
                        .absolute()
                        .left_0()
                        .top(px(5.0))
                        .bottom(px(5.0))
                        .w(px(2.5))
                        .rounded_full()
                        .bg(space_color),
                )
            })
            // Leading icon or spinner
            .child(if is_running {
                Spinner::new().xsmall().into_any_element()
            } else {
                Icon::new(IconName::FileCode)
                    .size(px(13.0))
                    .text_color(if is_active {
                        space_color
                    } else {
                        palette.text_muted
                    })
                    .into_any_element()
            })
            // Title + DB subtitle OR inline rename input
            .child(if is_renaming {
                if let Some(input) = self.rename_input.as_ref() {
                    div()
                        .flex_1()
                        .min_w_0()
                        .child(Input::new(input).xsmall())
                        .into_any_element()
                } else {
                    div().flex_1().into_any_element()
                }
            } else {
                v_flex()
                    .flex_1()
                    .min_w_0()
                    .child(
                        div()
                            .text_xs()
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
                    .when_some(tab.database.clone(), |el, db| {
                        el.child(
                            div()
                                .text_xs()
                                .text_color(palette.text_muted)
                                .truncate()
                                .child(db),
                        )
                    })
                    .into_any_element()
            })
            // Subtle hover actions: rename + close-to-archive
            .when(!is_renaming, |el| {
                el.child(
                    h_flex()
                        .gap_0p5()
                        .invisible()
                        .group_hover("tab-row", |s| s.visible())
                        .child(
                            Button::new(SharedString::from(format!("rename-{}", tab.id)))
                                .ghost()
                                .xsmall()
                                .icon(IconName::Pencil)
                                .tooltip("Rename tab")
                                .on_click(cx.listener(move |this, _, window, cx| {
                                    cx.stop_propagation();
                                    this.start_rename_tab(tab_id_rename.clone(), window, cx);
                                })),
                        )
                        .child(
                            Button::new(SharedString::from(format!("close-{}", tab.id)))
                                .ghost()
                                .xsmall()
                                .icon(IconName::X)
                                .tooltip("Archive tab (Ctrl+W)")
                                .on_click(cx.listener(move |this, _, window, cx| {
                                    cx.stop_propagation();
                                    this.archive_tab(tab_id_close.clone(), window, cx);
                                })),
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
        let is_connected = space_rt.connection_state() == ConnectionState::Connected;
        let active_db = active_tab
            .and_then(|t| t.database.clone())
            .or_else(|| active_space.and_then(|s| s.active_database.clone()));

        v_flex()
            .flex_1()
            .min_w_0()
            .h_full()
            .overflow_hidden()
            // Top Editor Toolbar
            .child(
                h_flex()
                    .h(px(44.0))
                    .px_3p5()
                    .border_b_1()
                    .border_color(palette.hairline)
                    .bg(palette.background)
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .items_center()
                            .gap_2()
                            .child(if is_running {
                                Button::new("toolbar-stop")
                                    .danger()
                                    .small()
                                    .icon(IconName::Square)
                                    .label("Stop")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.cancel_active_query(cx);
                                    }))
                            } else {
                                Button::new("toolbar-run")
                                    .primary()
                                    .small()
                                    .icon(IconName::Play)
                                    .label("Run")
                                    .tooltip("Run statement under caret (Ctrl+Enter)")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.run_query(true, cx);
                                    }))
                            })
                            .child(
                                Button::new("toolbar-run-all")
                                    .outline()
                                    .small()
                                    .label("Run All (F5)")
                                    .disabled(is_running)
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.run_query(false, cx);
                                    })),
                            )
                            .when(is_running, |el| {
                                el.child(
                                    h_flex()
                                        .px_2p5()
                                        .py_1()
                                        .rounded_full()
                                        .bg(palette.warning.opacity(0.15))
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
                            .when_some(active_db, |el, db| {
                                el.child(
                                    h_flex()
                                        .px_2()
                                        .py_0p5()
                                        .rounded(px(5.0))
                                        .bg(palette.elevated)
                                        .border_1()
                                        .border_color(palette.hairline_subtle)
                                        .gap_1()
                                        .items_center()
                                        .child(
                                            Icon::new(IconName::Database)
                                                .size(px(11.0))
                                                .text_color(if is_connected {
                                                    space_color
                                                } else {
                                                    palette.text_muted
                                                }),
                                        )
                                        .child(
                                            div()
                                                .text_xs()
                                                .text_color(palette.text_secondary)
                                                .child(db),
                                        ),
                                )
                            }),
                    )
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1p5()
                            .child(
                                Button::new("toolbar-schema")
                                    .ghost()
                                    .small()
                                    .icon(IconName::PanelRight)
                                    .label("Schema")
                                    .tooltip("Toggle schema peek (Ctrl+K)")
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.toggle_schema_pane(cx);
                                    })),
                            )
                            .child(
                                Button::new("toolbar-theme")
                                    .ghost()
                                    .small()
                                    .icon(match self.state.theme {
                                        ThemePreference::Dark => IconName::Sun,
                                        ThemePreference::Light => IconName::Moon,
                                    })
                                    .tooltip("Switch Light / Dark theme (Ctrl+Shift+L)")
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.toggle_theme(window, cx);
                                    })),
                            ),
                    ),
            )
            // Vertical Split: SQL Code Editor (top) + Virtualized Results Pane (bottom)
            .child(
                div().flex_1().min_h_0().w_full().child(
                    v_resizable("editor-results-split")
                        .child(
                            resizable_panel().size(px(400.0)).child(
                                div().size_full().bg(palette.background).child(
                                    Editor::new(&self.editor_state)
                                        .appearance(false)
                                        .bordered(false)
                                        .h_full(),
                                ),
                            ),
                        )
                        .child(
                            resizable_panel()
                                .size(px(320.0))
                                .child(self.render_results_pane(tab_rt, palette, cx)),
                        ),
                ),
            )
    }

    fn render_results_pane(
        &self,
        tab_rt: &TabRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let active_result = tab_rt.results.get(tab_rt.active_result_idx);
        let all_messages: Vec<String> = tab_rt
            .results
            .iter()
            .flat_map(|r| r.messages.iter().cloned())
            .collect();
        let has_grid = !tab_rt.show_messages
            && active_result.is_some_and(|r| !r.is_error && !r.columns.is_empty());

        v_flex()
            .size_full()
            .bg(palette.background)
            .border_t_1()
            .border_color(palette.hairline)
            // Result tabs bar
            .child(
                h_flex()
                    .h(px(36.0))
                    .px_3()
                    .bg(palette.rail_bg)
                    .border_b_1()
                    .border_color(palette.hairline)
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .child(
                        h_flex()
                            .items_center()
                            .gap_1()
                            .children(tab_rt.results.iter().enumerate().map(|(idx, res)| {
                                let selected =
                                    !tab_rt.show_messages && idx == tab_rt.active_result_idx;
                                let label = if res.is_error {
                                    format!("Error {}", idx + 1)
                                } else {
                                    format!("Result {} ({})", idx + 1, res.rows.len())
                                };
                                Button::new(SharedString::from(format!("res-tab-{idx}")))
                                    .xsmall()
                                    .label(label)
                                    .when(selected, |b| b.primary())
                                    .when(!selected, |b| b.ghost())
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
                            }))
                            .when(!all_messages.is_empty(), |el| {
                                let selected = tab_rt.show_messages;
                                el.child(
                                    Button::new("res-tab-messages")
                                        .xsmall()
                                        .label(format!("Messages ({})", all_messages.len()))
                                        .when(selected, |b| b.primary())
                                        .when(!selected, |b| b.ghost())
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            if let Some(tab_id) = this.active_tab_id() {
                                                if let Some(rt) = this.tab_runtime.get_mut(&tab_id)
                                                {
                                                    rt.show_messages = true;
                                                }
                                                this.sync_table_from_active_tab(cx);
                                                cx.notify();
                                            }
                                        })),
                                )
                            })
                            .when(tab_rt.results.is_empty(), |el| {
                                el.child(
                                    div()
                                        .text_xs()
                                        .text_color(palette.text_muted)
                                        .child("Results"),
                                )
                            }),
                    )
                    .child(
                        h_flex()
                            .items_center()
                            .gap_2()
                            .when_some(active_result.filter(|r| r.is_truncated), |el, r| {
                                el.child(
                                    div()
                                        .px_2()
                                        .py_0p5()
                                        .rounded(px(4.0))
                                        .bg(palette.warning.opacity(0.15))
                                        .text_xs()
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
                                        .label(if copied { "Copied TSV" } else { "Copy TSV" })
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            this.copy_active_tsv(cx);
                                        })),
                                )
                            }),
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
                        .children(all_messages.into_iter().map(|m| div().child(m)))
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
                                                    .font_family(cx.theme().mono_font_family.clone())
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
                            .stripe(true)
                            .bordered(false)
                            .into_any_element()
                    }
                } else {
                    v_flex()
                        .size_full()
                        .items_center()
                        .justify_center()
                        .gap_1p5()
                        .child(
                            Icon::new(IconName::Table2)
                                .size(px(22.0))
                                .text_color(palette.text_muted.opacity(0.6)),
                        )
                        .child(
                            div()
                                .text_xs()
                                .text_color(palette.text_muted)
                                .child("Press Ctrl+Enter to run the statement under the caret, or F5 to run all."),
                        )
                        .into_any_element()
                },
            ))
    }

    fn render_schema_drawer(
        &self,
        space_rt: &SpaceRuntime,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        v_flex()
            .w(px(280.0))
            .h_full()
            .flex_shrink_0()
            .bg(palette.rail_bg)
            .border_l_1()
            .border_color(palette.hairline)
            .child(
                h_flex()
                    .h(px(44.0))
                    .px_3()
                    .border_b_1()
                    .border_color(palette.hairline)
                    .items_center()
                    .justify_between()
                    .child(
                        div()
                            .text_xs()
                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            .text_color(palette.text_primary)
                            .child(format!("Schema ({})", space_rt.schema.len())),
                    )
                    .child(
                        Button::new("close-schema-drawer")
                            .ghost()
                            .xsmall()
                            .icon(IconName::X)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.toggle_schema_pane(cx);
                            })),
                    ),
            )
            .child(
                div()
                    .id("schema-tree-scroll")
                    .flex_1()
                    .overflow_y_scroll()
                    .p_2()
                    .child(if space_rt.schema.is_empty() {
                        div()
                            .p_3()
                            .text_xs()
                            .text_color(palette.text_muted)
                            .child("Connect a workspace to browse tables and columns.")
                            .into_any_element()
                    } else {
                        let mut items: Vec<AnyElement> = Vec::new();
                        for table in &space_rt.schema {
                            items.push(
                                self.render_schema_table_item(table, palette, cx)
                                    .into_any_element(),
                            );
                        }
                        v_flex()
                            .gap_1()
                            .children(items)
                            .into_any_element()
                    }),
            )
    }

    fn render_schema_table_item(
        &self,
        table: &TableSchema,
        palette: StudioPalette,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let key = format!("{}.{}", table.schema, table.name);
        let expanded = self.expanded_tables.contains(&key);
        let key_toggle = key.clone();
        let select_sql = format!("\nSELECT TOP 100 * FROM {}.{};\n", table.schema, table.name);

        v_flex()
            .gap_0p5()
            .child(
                h_flex()
                    .id(SharedString::from(format!("schema-tbl-{key}")))
                    .px_2()
                    .py_1()
                    .rounded(px(6.0))
                    .items_center()
                    .justify_between()
                    .cursor_pointer()
                    .hover(|s| s.bg(palette.elevated))
                    .on_click(cx.listener(move |this, _, _, cx| {
                        if !this.expanded_tables.remove(&key_toggle) {
                            this.expanded_tables.insert(key_toggle.clone());
                        }
                        cx.notify();
                    }))
                    .child(
                        h_flex()
                            .gap_1p5()
                            .items_center()
                            .min_w_0()
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
                                Icon::new(if table.kind == TableKind::View {
                                    IconName::Eye
                                } else {
                                    IconName::Table2
                                })
                                .size(px(12.0))
                                .text_color(palette.accent),
                            )
                            .child(
                                div()
                                    .text_xs()
                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                    .text_color(palette.text_primary)
                                    .truncate()
                                    .child(key.clone()),
                            ),
                    )
                    .child(
                        Button::new(SharedString::from(format!("schema-sel-{key}")))
                            .ghost()
                            .xsmall()
                            .icon(IconName::Play)
                            .tooltip("Insert SELECT TOP 100")
                            .on_click(cx.listener(move |this, _, window, cx| {
                                cx.stop_propagation();
                                this.insert_into_editor(select_sql.clone(), window, cx);
                            })),
                    ),
            )
            .when(expanded, |el| {
                el.child(
                    v_flex()
                        .pl_5()
                        .gap_0p5()
                        .children(table.columns.iter().map(|col| {
                            let is_pk = table
                                .primary_key
                                .iter()
                                .any(|pk| pk.eq_ignore_ascii_case(&col.name));
                            let is_fk = table
                                .foreign_keys
                                .iter()
                                .any(|fk| fk.column.eq_ignore_ascii_case(&col.name));
                            let col_insert = col.name.clone();

                            h_flex()
                                .id(SharedString::from(format!("col-{key}-{}", col.name)))
                                .px_2()
                                .py_0p5()
                                .rounded(px(4.0))
                                .items_center()
                                .justify_between()
                                .gap_1p5()
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.elevated))
                                .on_click(cx.listener(move |this, _, window, cx| {
                                    this.insert_into_editor(col_insert.clone(), window, cx);
                                }))
                                .child(
                                    h_flex()
                                        .gap_1()
                                        .items_center()
                                        .min_w_0()
                                        .child(
                                            div()
                                                .text_xs()
                                                .text_color(palette.text_secondary)
                                                .truncate()
                                                .child(col.name.clone()),
                                        )
                                        .when(is_pk, |d| {
                                            d.child(
                                                div()
                                                    .px_1()
                                                    .rounded(px(3.0))
                                                    .bg(palette.warning.opacity(0.18))
                                                    .text_xs()
                                                    .text_color(palette.warning)
                                                    .child("PK"),
                                            )
                                        })
                                        .when(is_fk, |d| {
                                            d.child(
                                                div()
                                                    .px_1()
                                                    .rounded(px(3.0))
                                                    .bg(palette.accent.opacity(0.18))
                                                    .text_xs()
                                                    .text_color(palette.accent)
                                                    .child("FK"),
                                            )
                                        }),
                                )
                                .when_some(col.data_type.clone(), |d, dt| {
                                    d.child(
                                        div()
                                            .text_xs()
                                            .text_color(palette.text_muted)
                                            .child(dt),
                                    )
                                })
                        })),
                )
            })
    }

    fn render_status_bar(
        &self,
        active_space: Option<&SpaceState>,
        active_tab: Option<&TabState>,
        space_rt: &SpaceRuntime,
        tab_rt: &TabRuntime,
        palette: StudioPalette,
    ) -> impl IntoElement {
        let identity = active_space
            .and_then(|s| {
                self.catalog
                    .find(&s.profile.provider_id)
                    .and_then(|p| p.describe_identity(&s.profile))
            })
            .unwrap_or_default();
        let db = active_tab
            .and_then(|t| t.database.as_deref())
            .or_else(|| active_space.and_then(|s| s.active_database.as_deref()))
            .unwrap_or("—");
        let version = space_rt.server_version.as_deref().unwrap_or("Not connected");
        let summary = tab_rt
            .last_summary
            .as_deref()
            .unwrap_or("Ready · Ctrl+Enter Run · Ctrl+Space Complete · Ctrl+F Find");

        h_flex()
            .h(px(28.0))
            .px_3()
            .bg(palette.rail_bg)
            .border_t_1()
            .border_color(palette.hairline)
            .items_center()
            .justify_between()
            .text_xs()
            .text_color(palette.text_muted)
            .child(
                h_flex()
                    .gap_3()
                    .items_center()
                    .child(version.to_string())
                    .when(!identity.is_empty(), |el| el.child(format!("· {identity}")))
                    .child(format!("· DB: {db}")),
            )
            .child(
                h_flex()
                    .gap_3()
                    .items_center()
                    .child(summary.to_string())
                    .child("· Larik GPUI PoC"),
            )
    }
}
