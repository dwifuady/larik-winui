use std::path::{Path, PathBuf};

use anyhow::Context;
use directories::BaseDirs;
use larik_data::ProviderCatalog;
use rusqlite::{Connection, OptionalExtension, params};

use crate::StudioState;

/// Isolated SQLite persistence for the Rust PoC.
/// Stored under `%LOCALAPPDATA%\Larik Rust PoC\studio.db` so it never touches the WinUI app state.
pub struct StudioStore {
    db_path: PathBuf,
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
        let store = Self { db_path };
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
            );",
        )?;
        Ok(())
    }

    pub fn load(&self, catalog: &ProviderCatalog) -> StudioState {
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
        // Never persist provider-declared password fields to SQLite.
        let mut sanitized = state.clone();
        for space in &mut sanitized.spaces {
            if let Some(provider) = catalog.find(&space.profile.provider_id) {
                for key in provider.password_keys() {
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
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_state_without_persisting_passwords() {
        let dir = std::env::temp_dir().join(format!("larik_poc_test_{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let db = dir.join("test.db");

        let catalog = ProviderCatalog::new();
        let mut state = StudioState::seed_default(&catalog);
        state.spaces[0].name = "Custom Space".into();

        let store = StudioStore::open_at(db.clone()).unwrap();
        store.save(&state, &catalog).unwrap();
        let loaded = store.load(&catalog);
        assert_eq!(loaded.spaces[0].name, "Custom Space");

        let _ = std::fs::remove_dir_all(&dir);
    }
}
