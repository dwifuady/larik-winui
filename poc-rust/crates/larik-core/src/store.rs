use std::path::{Path, PathBuf};
use std::sync::Arc;

use anyhow::Context;
use directories::BaseDirs;
use larik_data::ProviderCatalog;
use rusqlite::{Connection, OptionalExtension, params};

use crate::run_log::{
    DEFAULT_RUN_LOG_RETENTION_DAYS, RUN_LOG_RETENTION_CHOICES, RunLogRecord, escape_like_pattern,
    normalize_retention_days, truncate_statement,
};
use crate::secrets::{SecretBackend, WindowsCredentialBackend, secret_target};
use crate::snippets::{
    SnippetRecord, builtin_catalog, builtin_to_record, normalize_dialects, sort_snippets,
};
use crate::{SpaceState, StudioState};

const RUN_LOG_RETENTION_META_KEY: &str = "run_log_retention_days";

/// Isolated SQLite persistence for the Rust PoC.
/// Stored under `%LOCALAPPDATA%\Larik Rust PoC\studio.db` so it never touches the WinUI app state.
pub struct StudioStore {
    db_path: PathBuf,
    secrets: Arc<dyn SecretBackend>,
}

impl StudioStore {
    pub fn open_default() -> anyhow::Result<Self> {
        let base = BaseDirs::new()
            .map(|b| b.data_local_dir().to_path_buf())
            .unwrap_or_else(|| PathBuf::from("."));
        let dir = base.join("Larik Rust PoC");
        std::fs::create_dir_all(&dir)
            .with_context(|| format!("Failed to create PoC data directory {}", dir.display()))?;
        Self::open_at(dir.join("studio.db"))
    }

    pub fn open_at(db_path: PathBuf) -> anyhow::Result<Self> {
        Self::open_at_with_secrets(db_path, Arc::new(WindowsCredentialBackend))
    }

    pub fn open_at_with_secrets(
        db_path: PathBuf,
        secrets: Arc<dyn SecretBackend>,
    ) -> anyhow::Result<Self> {
        let store = Self { db_path, secrets };
        store.ensure_schema()?;
        Ok(store)
    }

    pub fn db_path(&self) -> &Path {
        &self.db_path
    }

    fn connect(&self) -> anyhow::Result<Connection> {
        let conn = Connection::open(&self.db_path)?;
        conn.pragma_update(None, "journal_mode", "WAL")?;
        Ok(conn)
    }

    fn ensure_schema(&self) -> anyhow::Result<()> {
        let conn = self.connect()?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS studio_snapshot (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                version INTEGER NOT NULL,
                payload TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS studio_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS snippets (
                id TEXT PRIMARY KEY,
                trigger TEXT NOT NULL,
                name TEXT NOT NULL,
                content TEXT NOT NULL,
                description TEXT,
                is_builtin INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 1,
                category TEXT,
                drivers TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_snippets_trigger ON snippets(trigger);
            CREATE INDEX IF NOT EXISTS idx_snippets_enabled ON snippets(enabled);

            CREATE TABLE IF NOT EXISTS run_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recorded_at TEXT NOT NULL DEFAULT (datetime('now')),
                actor TEXT NOT NULL,
                space_id TEXT,
                space_name TEXT,
                database_name TEXT,
                provider_id TEXT,
                tab_id TEXT,
                statement TEXT NOT NULL,
                status TEXT NOT NULL,
                reason TEXT,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                rows_returned INTEGER NOT NULL DEFAULT 0,
                row_limit INTEGER,
                is_truncated INTEGER NOT NULL DEFAULT 0,
                description TEXT,
                reference TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_run_log_recorded ON run_log(recorded_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS idx_run_log_space ON run_log(space_id, recorded_at DESC, id DESC);",
        )?;
        Ok(())
    }

    pub fn load(&self, catalog: &ProviderCatalog) -> StudioState {
        let mut state = self.load_snapshot(catalog);
        for space in &mut state.spaces {
            if let Some(provider) = catalog.find(&space.profile.provider_id) {
                for key in provider.password_keys() {
                    let target = secret_target(&space.id, key);
                    if let Ok(Some(secret)) = self.secrets.get(&target) {
                        if !secret.is_empty() {
                            space.profile.set(key, secret);
                        }
                    }
                }
            }
        }
        state
    }

    fn load_snapshot(&self, catalog: &ProviderCatalog) -> StudioState {
        let Ok(conn) = self.connect() else {
            return StudioState::seed_default(catalog);
        };
        let payload: Option<String> = conn
            .query_row(
                "SELECT payload FROM studio_snapshot WHERE id = 1",
                [],
                |row| row.get(0),
            )
            .optional()
            .ok()
            .flatten();

        payload
            .and_then(|json| serde_json::from_str::<StudioState>(&json).ok())
            .filter(|state| !state.spaces.is_empty())
            .unwrap_or_else(|| StudioState::seed_default(catalog))
    }

    pub fn save(&self, state: &StudioState, catalog: &ProviderCatalog) -> anyhow::Result<()> {
        // Persist provider-declared password fields in the secret backend, then strip them from SQLite JSON.
        let mut sanitized = state.clone();
        for space in &mut sanitized.spaces {
            if let Some(provider) = catalog.find(&space.profile.provider_id) {
                for key in provider.password_keys() {
                    let target = secret_target(&space.id, key);
                    match space.profile.get(key) {
                        Some(val) if !val.is_empty() => {
                            self.secrets.set(&target, val)?;
                        }
                        _ => {
                            self.secrets.delete(&target)?;
                        }
                    }
                    space
                        .profile
                        .values
                        .retain(|k, _| !k.eq_ignore_ascii_case(key));
                }
            }
        }

        let json = serde_json::to_string(&sanitized)?;
        let now = chrono::Utc::now().to_rfc3339();
        let conn = self.connect()?;
        conn.execute(
            "INSERT INTO studio_snapshot (id, version, payload, updated_at)
             VALUES (1, 1, ?1, ?2)
             ON CONFLICT(id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at",
            params![json, now],
        )?;
        Ok(())
    }

    pub fn delete_space_secrets(
        &self,
        space: &SpaceState,
        catalog: &ProviderCatalog,
    ) -> anyhow::Result<()> {
        if let Some(provider) = catalog.find(&space.profile.provider_id) {
            for key in provider.password_keys() {
                let target = secret_target(&space.id, key);
                self.secrets.delete(&target)?;
            }
        }
        Ok(())
    }

    pub fn load_snippets(&self) -> anyhow::Result<Vec<SnippetRecord>> {
        let mut conn = self.connect()?;
        {
            let tx = conn.transaction()?;
            let mut stmt = tx.prepare(
                "INSERT OR IGNORE INTO snippets
                    (id, trigger, name, content, description, is_builtin, enabled, category, drivers)
                 VALUES (?1, ?2, ?3, ?4, ?5, 1, 1, ?6, ?7)",
            )?;
            for def in builtin_catalog() {
                let rec = builtin_to_record(&def, true);
                let drivers = drivers_column(rec.dialects.as_deref());
                stmt.execute(params![
                    rec.id,
                    rec.trigger,
                    rec.name,
                    rec.content,
                    rec.description,
                    rec.category,
                    drivers,
                ])?;
            }
            drop(stmt);
            tx.commit()?;
        }

        let mut stmt = conn.prepare(
            "SELECT id, trigger, name, content, description, is_builtin, enabled, category, drivers
             FROM snippets",
        )?;
        let rows = stmt.query_map([], |row| {
            let is_builtin: i64 = row.get(5)?;
            let is_enabled: i64 = row.get(6)?;
            let drivers: Option<String> = row.get(8)?;
            Ok(SnippetRecord {
                id: row.get(0)?,
                trigger: row.get(1)?,
                name: row.get(2)?,
                content: row.get(3)?,
                description: row.get(4)?,
                category: row.get(7)?,
                dialects: parse_drivers(drivers.as_deref()),
                is_builtin: is_builtin != 0,
                is_enabled: is_enabled != 0,
            })
        })?;

        let mut records = Vec::new();
        for row in rows {
            records.push(row?);
        }
        sort_snippets(&mut records);
        Ok(records)
    }

    pub fn save_snippet(&self, record: &SnippetRecord) -> anyhow::Result<()> {
        let conn = self.connect()?;
        let normalized_dialects = normalize_dialects(record.dialects.clone());
        let drivers = drivers_column(normalized_dialects.as_deref());
        let description = record
            .description
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty());
        let category = record
            .category
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty());

        conn.execute(
            "INSERT INTO snippets
                (id, trigger, name, content, description, is_builtin, enabled, category, drivers)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
             ON CONFLICT(id) DO UPDATE SET
                trigger = excluded.trigger,
                name = excluded.name,
                content = excluded.content,
                description = excluded.description,
                enabled = excluded.enabled,
                category = excluded.category,
                drivers = excluded.drivers,
                updated_at = datetime('now')",
            params![
                record.id,
                record.trigger.trim(),
                record.name.trim(),
                record.content,
                description,
                if record.is_builtin { 1 } else { 0 },
                if record.is_enabled { 1 } else { 0 },
                category,
                drivers,
            ],
        )?;
        Ok(())
    }

    pub fn delete_snippet(&self, id: &str) -> anyhow::Result<bool> {
        let conn = self.connect()?;
        let deleted = conn.execute(
            "DELETE FROM snippets WHERE id = ?1 AND is_builtin = 0",
            params![id],
        )?;
        Ok(deleted > 0)
    }

    // ---------------------------------------------------------------- run_log

    /// Records a query execution in `run_log`. Best-effort by contract: never fails query execution.
    pub fn log_run(&self, record: &RunLogRecord) -> bool {
        let Ok(conn) = self.connect() else {
            return false;
        };
        let statement = truncate_statement(&record.statement);
        conn.execute(
            "INSERT INTO run_log (
                actor, space_id, space_name, database_name, provider_id, tab_id,
                statement, status, reason, duration_ms, rows_returned, row_limit,
                is_truncated, description, reference
             ) VALUES (
                ?1, ?2, ?3, ?4, ?5, ?6,
                ?7, ?8, ?9, ?10, ?11, ?12,
                ?13, ?14, ?15
             )",
            params![
                record.actor,
                record.space_id,
                record.space_name,
                record.database_name,
                record.provider_id,
                record.tab_id,
                statement,
                record.status,
                record.reason,
                record.duration_ms.max(0),
                record.rows_returned.max(0),
                record.row_limit,
                if record.is_truncated { 1i64 } else { 0i64 },
                record.description,
                record.reference,
            ],
        )
        .is_ok()
    }

    /// Reads the most recent run-log rows, optionally scoped to a workspace.
    pub fn get_run_log(&self, space_id: Option<&str>, limit: usize) -> Vec<RunLogRecord> {
        let Ok(conn) = self.connect() else {
            return Vec::new();
        };
        let limit = limit.clamp(1, 5_000) as i64;
        let sql = match space_id {
            Some(_) => {
                "SELECT id, recorded_at, actor, space_id, space_name, database_name, provider_id,
                        tab_id, statement, status, reason, duration_ms, rows_returned, row_limit,
                        is_truncated, description, reference
                 FROM run_log
                 WHERE space_id = ?1
                 ORDER BY recorded_at DESC, id DESC
                 LIMIT ?2"
            }
            None => {
                "SELECT id, recorded_at, actor, space_id, space_name, database_name, provider_id,
                        tab_id, statement, status, reason, duration_ms, rows_returned, row_limit,
                        is_truncated, description, reference
                 FROM run_log
                 ORDER BY recorded_at DESC, id DESC
                 LIMIT ?1"
            }
        };

        let Ok(mut stmt) = conn.prepare(sql) else {
            return Vec::new();
        };
        let mapped = match space_id {
            Some(sid) => stmt.query_map(params![sid, limit], map_run_log_row),
            None => stmt.query_map(params![limit], map_run_log_row),
        };
        match mapped {
            Ok(rows) => rows.filter_map(Result::ok).collect(),
            Err(_) => Vec::new(),
        }
    }

    /// Counts all persisted run-log rows in the given scope (or across all workspaces when `None`).
    pub fn count_run_log(&self, space_id: Option<&str>) -> usize {
        let Ok(conn) = self.connect() else {
            return 0;
        };
        let count: rusqlite::Result<i64> = match space_id {
            Some(sid) => conn.query_row(
                "SELECT COUNT(*) FROM run_log WHERE space_id = ?1",
                params![sid],
                |r| r.get(0),
            ),
            None => conn.query_row("SELECT COUNT(*) FROM run_log", [], |r| r.get(0)),
        };
        count.unwrap_or(0).max(0) as usize
    }

    /// Substring search over statement, space_name, database_name, description, and reference.
    pub fn search_run_log(
        &self,
        space_id: Option<&str>,
        query: &str,
        limit: usize,
    ) -> Vec<RunLogRecord> {
        let trimmed = query.trim();
        if trimmed.is_empty() {
            return self.get_run_log(space_id, limit);
        }
        let Ok(conn) = self.connect() else {
            return Vec::new();
        };
        let pattern = escape_like_pattern(trimmed);
        let limit = limit.clamp(1, 5_000) as i64;
        let sql = match space_id {
            Some(_) => {
                "SELECT id, recorded_at, actor, space_id, space_name, database_name, provider_id,
                        tab_id, statement, status, reason, duration_ms, rows_returned, row_limit,
                        is_truncated, description, reference
                 FROM run_log
                 WHERE space_id = ?1
                   AND (statement LIKE ?2 ESCAPE '\\'
                        OR COALESCE(space_name, '') LIKE ?2 ESCAPE '\\'
                        OR COALESCE(database_name, '') LIKE ?2 ESCAPE '\\'
                        OR COALESCE(description, '') LIKE ?2 ESCAPE '\\'
                        OR COALESCE(reference, '') LIKE ?2 ESCAPE '\\')
                 ORDER BY recorded_at DESC, id DESC
                 LIMIT ?3"
            }
            None => {
                "SELECT id, recorded_at, actor, space_id, space_name, database_name, provider_id,
                        tab_id, statement, status, reason, duration_ms, rows_returned, row_limit,
                        is_truncated, description, reference
                 FROM run_log
                 WHERE statement LIKE ?1 ESCAPE '\\'
                    OR COALESCE(space_name, '') LIKE ?1 ESCAPE '\\'
                    OR COALESCE(database_name, '') LIKE ?1 ESCAPE '\\'
                    OR COALESCE(description, '') LIKE ?1 ESCAPE '\\'
                    OR COALESCE(reference, '') LIKE ?1 ESCAPE '\\'
                 ORDER BY recorded_at DESC, id DESC
                 LIMIT ?2"
            }
        };

        let Ok(mut stmt) = conn.prepare(sql) else {
            return Vec::new();
        };
        let mapped = match space_id {
            Some(sid) => stmt.query_map(params![sid, pattern, limit], map_run_log_row),
            None => stmt.query_map(params![pattern, limit], map_run_log_row),
        };
        match mapped {
            Ok(rows) => rows.filter_map(Result::ok).collect(),
            Err(_) => Vec::new(),
        }
    }

    pub fn get_run_log_retention_days(&self) -> i32 {
        let Ok(conn) = self.connect() else {
            return DEFAULT_RUN_LOG_RETENTION_DAYS;
        };
        let raw: Option<String> = conn
            .query_row(
                "SELECT value FROM studio_meta WHERE key = ?1",
                params![RUN_LOG_RETENTION_META_KEY],
                |r| r.get(0),
            )
            .optional()
            .ok()
            .flatten();
        raw.and_then(|s| s.parse::<i32>().ok())
            .map(normalize_retention_days)
            .unwrap_or(DEFAULT_RUN_LOG_RETENTION_DAYS)
    }

    pub fn set_run_log_retention_days(&self, days: i32) -> bool {
        if !RUN_LOG_RETENTION_CHOICES.contains(&days) {
            return false;
        }
        let Ok(conn) = self.connect() else {
            return false;
        };
        conn.execute(
            "INSERT INTO studio_meta (key, value) VALUES (?1, ?2)
             ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            params![RUN_LOG_RETENTION_META_KEY, days.to_string()],
        )
        .is_ok()
    }

    /// Removes run-log rows older than the configured retention window.
    pub fn prune_run_log(&self) -> usize {
        let Ok(conn) = self.connect() else {
            return 0;
        };
        let days = self.get_run_log_retention_days();
        let modifier = format!("-{days} days");
        conn.execute(
            "DELETE FROM run_log WHERE recorded_at < datetime('now', ?1)",
            params![modifier],
        )
        .unwrap_or(0)
    }
}

fn map_run_log_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<RunLogRecord> {
    let is_truncated: i64 = row.get(14)?;
    Ok(RunLogRecord {
        id: row.get(0)?,
        recorded_at: row.get(1)?,
        actor: row.get(2)?,
        space_id: row.get(3)?,
        space_name: row.get(4)?,
        database_name: row.get(5)?,
        provider_id: row.get(6)?,
        tab_id: row.get(7)?,
        statement: row.get(8)?,
        status: row.get(9)?,
        reason: row.get(10)?,
        duration_ms: row.get(11)?,
        rows_returned: row.get(12)?,
        row_limit: row.get(13)?,
        is_truncated: is_truncated != 0,
        description: row.get(15)?,
        reference: row.get(16)?,
    })
}

fn drivers_column(dialects: Option<&[String]>) -> Option<String> {
    dialects.filter(|d| !d.is_empty()).map(|d| d.join(","))
}

fn parse_drivers(value: Option<&str>) -> Option<Vec<String>> {
    let raw = value?.trim();
    if raw.is_empty() {
        return None;
    }
    let parts: Vec<String> = raw
        .split(',')
        .map(|p| p.trim().to_string())
        .filter(|p| !p.is_empty())
        .collect();
    normalize_dialects(Some(parts))
}

#[cfg(test)]
mod tests {
    use super::*;

    use larik_data::sql_language::SqlLanguage;
    use larik_data::{
        ConnectionProfile, DatabaseConnection, DatabaseProvider, EditorLanguage, ProviderSetting,
        ProviderSettingKind,
    };

    use crate::secrets::InMemorySecretBackend;
    use crate::snippets::{has_trigger_conflict, snippets_for_dialect};

    struct TestSqlProvider;

    impl DatabaseProvider for TestSqlProvider {
        fn id(&self) -> &'static str {
            "test-sql"
        }

        fn display_name(&self) -> &'static str {
            "Test SQL"
        }

        fn dialect(&self) -> &'static str {
            "mssql"
        }

        fn settings(&self) -> &'static [ProviderSetting] {
            static SETTINGS: [ProviderSetting; 2] = [
                ProviderSetting {
                    key: "server",
                    label: "Server",
                    kind: ProviderSettingKind::Text,
                    required: true,
                    default_value: Some("localhost"),
                    placeholder: None,
                    hint: None,
                    is_advanced: false,
                },
                ProviderSetting {
                    key: "password",
                    label: "Password",
                    kind: ProviderSettingKind::Password,
                    required: false,
                    default_value: None,
                    placeholder: None,
                    hint: None,
                    is_advanced: false,
                },
            ];
            &SETTINGS
        }

        fn editor_language(&self) -> Arc<dyn EditorLanguage> {
            Arc::new(SqlLanguage::tsql())
        }

        fn describe_endpoint(&self, profile: &ConnectionProfile) -> Option<String> {
            profile.get("server").map(str::to_string)
        }

        fn create_connection(&self, _profile: ConnectionProfile) -> Box<dyn DatabaseConnection> {
            unimplemented!("not needed for store unit tests")
        }
    }

    #[test]
    fn round_trips_state_and_secrets_without_persisting_passwords_in_sqlite() {
        let dir = std::env::temp_dir().join(format!("larik_poc_test_{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let db = dir.join("test.db");

        let mut catalog = ProviderCatalog::new();
        catalog.register(Arc::new(TestSqlProvider));

        let mut state = StudioState::seed_default(&catalog);
        state.spaces[0].name = "Custom Space".into();
        state.spaces[0].profile = catalog.find("test-sql").unwrap().default_profile();
        state.spaces[0].profile.set("password", "s3cr3t-p@ss-🔑");

        let secrets = Arc::new(InMemorySecretBackend::new());
        let store = StudioStore::open_at_with_secrets(db.clone(), secrets.clone()).unwrap();
        store.save(&state, &catalog).unwrap();

        // Password must NOT appear anywhere in SQLite snapshot payload.
        let conn = Connection::open(&db).unwrap();
        let raw_payload: String = conn
            .query_row("SELECT payload FROM studio_snapshot WHERE id = 1", [], |r| {
                r.get(0)
            })
            .unwrap();
        assert!(!raw_payload.contains("s3cr3t-p@ss-🔑"));

        // Password MUST be saved in the secret backend and rehydrated on load.
        assert_eq!(secrets.len(), 1);
        let loaded = store.load(&catalog);
        assert_eq!(loaded.spaces[0].name, "Custom Space");
        assert_eq!(
            loaded.spaces[0].profile.get("password"),
            Some("s3cr3t-p@ss-🔑")
        );

        // Clearing password deletes the secret from the backend.
        let mut cleared = loaded.clone();
        cleared.spaces[0].profile.set("password", "");
        store.save(&cleared, &catalog).unwrap();
        assert_eq!(secrets.len(), 0);
        assert_eq!(store.load(&catalog).spaces[0].profile.get("password"), None);

        // Deleting workspace secrets removes stored credentials.
        store.save(&loaded, &catalog).unwrap();
        assert_eq!(secrets.len(), 1);
        store
            .delete_space_secrets(&loaded.spaces[0], &catalog)
            .unwrap();
        assert_eq!(secrets.len(), 0);

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(windows)]
    #[test]
    fn windows_credential_backend_round_trips_and_cleans_up() {
        let backend = WindowsCredentialBackend;
        let target = format!("larik-poc-test-{}", uuid::Uuid::new_v4());
        let secret_value = "p@ssword_é_🔑_123!";

        assert_eq!(backend.get(&target).unwrap(), None);
        backend.set(&target, secret_value).unwrap();
        assert_eq!(backend.get(&target).unwrap().as_deref(), Some(secret_value));

        backend.delete(&target).unwrap();
        assert_eq!(backend.get(&target).unwrap(), None);
        // Deleting missing credential is idempotent.
        backend.delete(&target).unwrap();
    }

    #[test]
    fn snippets_seed_crud_conflict_and_dialect_filtering() {
        let dir = std::env::temp_dir().join(format!("larik_snip_test_{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let db = dir.join("snippets.db");

        let store = StudioStore::open_at(db).unwrap();

        // Seeding twice produces the exact 25 built-ins without duplicates.
        let first = store.load_snippets().unwrap();
        assert_eq!(first.len(), 25);
        let second = store.load_snippets().unwrap();
        assert_eq!(second.len(), 25);

        // Dialect filtering: mssql gets `selt` not `sell`; postgres gets `sell` not `selt`.
        let mssql_defs = snippets_for_dialect(&first, "mssql");
        assert!(mssql_defs.iter().any(|d| d.trigger == "selt"));
        assert!(!mssql_defs.iter().any(|d| d.trigger == "sell"));

        let pg_defs = snippets_for_dialect(&first, "postgres");
        assert!(pg_defs.iter().any(|d| d.trigger == "sell"));
        assert!(!pg_defs.iter().any(|d| d.trigger == "selt"));

        // Built-ins cannot be deleted.
        let builtin_sel = first.iter().find(|s| s.trigger == "sel").unwrap();
        assert!(!store.delete_snippet(&builtin_sel.id).unwrap());

        // Trigger conflict detection across overlapping scopes.
        assert!(has_trigger_conflict(&first, "SEL", None, None));
        assert!(has_trigger_conflict(
            &first,
            "selt",
            Some(&["mssql".to_string()]),
            None
        ));
        assert!(!has_trigger_conflict(
            &first,
            "selt",
            Some(&["postgres".to_string()]),
            None
        ));
        assert!(!has_trigger_conflict(
            &first,
            "sel",
            None,
            Some(&builtin_sel.id)
        ));

        // Custom snippet CRUD.
        let custom = SnippetRecord {
            id: uuid::Uuid::new_v4().to_string(),
            trigger: "mytop".to_string(),
            name: "My Top 10".to_string(),
            content: "SELECT TOP 10 * FROM ${cursor}".to_string(),
            description: Some("Quick top 10".to_string()),
            category: Some("Select".to_string()),
            dialects: Some(vec!["mssql".to_string()]),
            is_builtin: false,
            is_enabled: true,
        };
        store.save_snippet(&custom).unwrap();
        let after_create = store.load_snippets().unwrap();
        assert_eq!(after_create.len(), 26);
        assert!(
            snippets_for_dialect(&after_create, "mssql")
                .iter()
                .any(|d| d.trigger == "mytop")
        );
        assert!(
            !snippets_for_dialect(&after_create, "postgres")
                .iter()
                .any(|d| d.trigger == "mytop")
        );

        let mut updated = custom.clone();
        updated.is_enabled = false;
        store.save_snippet(&updated).unwrap();
        let after_disable = store.load_snippets().unwrap();
        assert!(
            !snippets_for_dialect(&after_disable, "mssql")
                .iter()
                .any(|d| d.trigger == "mytop")
        );

        assert!(store.delete_snippet(&custom.id).unwrap());
        let after_delete = store.load_snippets().unwrap();
        assert_eq!(after_delete.len(), 25);

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn run_log_records_queries_searches_and_prunes_by_retention() {
        use crate::run_log::{RunLogRecord, run_status};

        let dir = std::env::temp_dir().join(format!("larik_runlog_test_{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let db = dir.join("test.db");

        let store =
            StudioStore::open_at_with_secrets(db.clone(), Arc::new(InMemorySecretBackend::new()))
                .unwrap();

        assert_eq!(store.get_run_log_retention_days(), 30);
        assert!(!store.set_run_log_retention_days(45));
        assert!(store.set_run_log_retention_days(90));
        assert_eq!(store.get_run_log_retention_days(), 90);

        assert!(store.log_run(&RunLogRecord::new_user_run(
            Some("space-1".into()),
            Some("Proxmox".into()),
            Some("blazex-hub".into()),
            Some("mssql".into()),
            Some("tab-1".into()),
            "SELECT * FROM dbo.customers WHERE name LIKE '50%'".into(),
            run_status::OK,
            None,
            42,
            12,
            None,
            false,
        )));

        assert!(store.log_run(&RunLogRecord::new_user_run(
            Some("space-2".into()),
            Some("Demo Studio".into()),
            Some("larik_prod".into()),
            Some("demo".into()),
            Some("tab-2".into()),
            "SELECT * FROM dbo.orders".into(),
            run_status::FAILED,
            Some("Invalid object name".into()),
            7,
            0,
            None,
            false,
        )));

        assert_eq!(store.count_run_log(None), 2);
        assert_eq!(store.count_run_log(Some("space-1")), 1);
        assert_eq!(store.get_run_log(Some("space-1"), 50).len(), 1);
        assert_eq!(store.get_run_log(None, 50).len(), 2);

        // Escaped '%' search must match literal "50%" and NOT match the orders query.
        let pct_hits = store.search_run_log(None, "50%", 50);
        assert_eq!(pct_hits.len(), 1);
        assert_eq!(pct_hits[0].space_id.as_deref(), Some("space-1"));

        // Backdate one record older than 90 days and verify pruning removes only that row.
        let conn = Connection::open(&db).unwrap();
        conn.execute(
            "UPDATE run_log SET recorded_at = datetime('now', '-100 days') WHERE space_id = 'space-2'",
            [],
        )
        .unwrap();
        drop(conn);

        let pruned = store.prune_run_log();
        assert_eq!(pruned, 1);
        assert_eq!(store.count_run_log(None), 1);
        assert_eq!(store.get_run_log(None, 50)[0].space_id.as_deref(), Some("space-1"));

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn auto_archive_skips_pinned_and_active_tabs_and_keeps_one_per_space() {
        let mut catalog = ProviderCatalog::new();
        catalog.register(Arc::new(TestSqlProvider));

        let mut state = StudioState::seed_default(&catalog);
        let now = chrono::Utc::now();
        let old = (now - chrono::Duration::days(45)).to_rfc3339();
        let recent = (now - chrono::Duration::days(5)).to_rfc3339();

        let space = &mut state.spaces[0];
        space.tabs = vec![
            crate::TabState {
                id: "t-active".into(),
                title: "Active Old".into(),
                database: Some("db1".into()),
                content: "SELECT 1".into(),
                is_pinned: false,
                group_id: None,
                last_accessed_at: old.clone(),
            },
            crate::TabState {
                id: "t-pinned".into(),
                title: "Pinned Old".into(),
                database: Some("db1".into()),
                content: "SELECT 2".into(),
                is_pinned: true,
                group_id: None,
                last_accessed_at: old.clone(),
            },
            crate::TabState {
                id: "t-stale".into(),
                title: "Stale Tab".into(),
                database: Some("db1".into()),
                content: "SELECT 3".into(),
                is_pinned: false,
                group_id: None,
                last_accessed_at: old,
            },
            crate::TabState {
                id: "t-recent".into(),
                title: "Recent Tab".into(),
                database: Some("db1".into()),
                content: "SELECT 4".into(),
                is_pinned: false,
                group_id: None,
                last_accessed_at: recent,
            },
        ];
        space.active_tab_id = Some("t-active".into());
        state.auto_archive_enabled = true;
        state.auto_archive_days = 30;

        let archived = state.run_auto_archive(now);
        assert_eq!(archived, vec!["t-stale".to_string()]);
        assert_eq!(state.archived_tabs.len(), 1);
        assert_eq!(state.archived_tabs[0].id, "t-stale");
        assert_eq!(state.spaces[0].tabs.len(), 3);
    }
}
