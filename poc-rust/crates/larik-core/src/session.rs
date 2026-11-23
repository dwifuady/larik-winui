use std::collections::HashMap;
use std::sync::Arc;

use anyhow::anyhow;
use larik_data::{
    ConnectionProfile, DatabaseConnection, DatabaseInfo, ProviderCatalog, QueryExecutionOptions,
    QueryResult, TableSchema,
};
use tokio::runtime::Runtime;
use tokio::sync::Mutex;
use tokio::task::AbortHandle;

/// Outcome of connecting a workspace's control session.
#[derive(Debug, Clone)]
pub struct ConnectOutcome {
    pub server_version: Option<String>,
    pub databases: Vec<DatabaseInfo>,
    pub schema: Vec<TableSchema>,
    pub initial_database: Option<String>,
}

/// Manages a dedicated Tokio runtime plus:
/// - one control connection per workspace (connect state, database list, schema), and
/// - one dedicated `DatabaseConnection` per query tab (session isolation for temp tables / SET / TXN).
pub struct SessionManager {
    runtime: Arc<Runtime>,
    catalog: ProviderCatalog,
    space_controls: Arc<Mutex<HashMap<String, Arc<Mutex<Box<dyn DatabaseConnection>>>>>>,
    tab_sessions: Arc<Mutex<HashMap<String, Arc<Mutex<Box<dyn DatabaseConnection>>>>>>,
    in_flight: Arc<Mutex<HashMap<String, AbortHandle>>>,
}

impl SessionManager {
    pub fn new(catalog: ProviderCatalog) -> anyhow::Result<Self> {
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .worker_threads(4)
            .thread_name("larik-db")
            .build()?;
        Ok(Self {
            runtime: Arc::new(runtime),
            catalog,
            space_controls: Arc::new(Mutex::new(HashMap::new())),
            tab_sessions: Arc::new(Mutex::new(HashMap::new())),
            in_flight: Arc::new(Mutex::new(HashMap::new())),
        })
    }

    pub fn runtime(&self) -> &Arc<Runtime> {
        &self.runtime
    }

    pub fn catalog(&self) -> &ProviderCatalog {
        &self.catalog
    }

    /// Test a profile on a short-lived connection without altering any workspace state.
    pub fn test_connection(
        &self,
        profile: ConnectionProfile,
    ) -> tokio::sync::oneshot::Receiver<anyhow::Result<String>> {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let catalog = self.catalog.clone();
        self.runtime.spawn(async move {
            let outcome = async {
                let provider = catalog
                    .find(&profile.provider_id)
                    .ok_or_else(|| anyhow!("Unknown provider '{}'.", profile.provider_id))?;
                let mut conn = provider.create_connection(profile);
                conn.open().await?;
                let ver = conn
                    .server_version()
                    .unwrap_or_else(|| provider.display_name().to_string());
                let _ = conn.close().await;
                Ok(format!("Connected ({ver})"))
            }
            .await;
            let _ = tx.send(outcome);
        });
        rx
    }

    /// Open a workspace's control connection, list databases, and fetch initial schema.
    pub fn connect_space(
        &self,
        space_id: String,
        profile: ConnectionProfile,
        preferred_database: Option<String>,
    ) -> tokio::sync::oneshot::Receiver<anyhow::Result<ConnectOutcome>> {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let catalog = self.catalog.clone();
        let controls = self.space_controls.clone();

        self.runtime.spawn(async move {
            let outcome = async {
                let provider = catalog
                    .find(&profile.provider_id)
                    .ok_or_else(|| anyhow!("Unknown provider '{}'.", profile.provider_id))?;
                let mut conn = provider.create_connection(profile.clone());
                conn.open().await?;
                let server_version = conn.server_version();
                let databases = conn.list_databases().await.unwrap_or_default();

                let initial_db = preferred_database
                    .filter(|pref| {
                        databases
                            .iter()
                            .any(|d| d.is_accessible && d.name.eq_ignore_ascii_case(pref))
                    })
                    .or_else(|| {
                        profile
                            .get_non_empty("database")
                            .map(str::to_string)
                    })
                    .or_else(|| {
                        databases
                            .iter()
                            .find(|d| d.is_accessible)
                            .map(|d| d.name.clone())
                    });

                let schema = conn
                    .get_schema(initial_db.as_deref())
                    .await
                    .unwrap_or_default();

                let conn_arc = Arc::new(Mutex::new(conn));
                controls.lock().await.insert(space_id, conn_arc);

                Ok(ConnectOutcome {
                    server_version,
                    databases,
                    schema,
                    initial_database: initial_db,
                })
            }
            .await;

            let _ = tx.send(outcome);
        });

        rx
    }

    /// Disconnect a workspace's control connection and any tab sessions belonging to `tab_ids`.
    pub fn disconnect_space(&self, space_id: String, tab_ids: Vec<String>) {
        let controls = self.space_controls.clone();
        let tabs = self.tab_sessions.clone();
        let in_flight = self.in_flight.clone();
        self.runtime.spawn(async move {
            if let Some(conn) = controls.lock().await.remove(&space_id) {
                let _ = conn.lock().await.close().await;
            }
            for tab_id in tab_ids {
                if let Some(handle) = in_flight.lock().await.remove(&tab_id) {
                    handle.abort();
                }
                if let Some(conn) = tabs.lock().await.remove(&tab_id) {
                    let _ = conn.lock().await.close().await;
                }
            }
        });
    }

    /// Close a single tab's dedicated session when the tab is archived/closed.
    pub fn close_tab_session(&self, tab_id: String) {
        let tabs = self.tab_sessions.clone();
        let in_flight = self.in_flight.clone();
        self.runtime.spawn(async move {
            if let Some(handle) = in_flight.lock().await.remove(&tab_id) {
                handle.abort();
            }
            if let Some(conn) = tabs.lock().await.remove(&tab_id) {
                let _ = conn.lock().await.close().await;
            }
        });
    }

    /// Load schema for a selected database using the workspace's control session.
    pub fn load_schema(
        &self,
        space_id: String,
        database: Option<String>,
    ) -> tokio::sync::oneshot::Receiver<anyhow::Result<Vec<TableSchema>>> {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let controls = self.space_controls.clone();
        self.runtime.spawn(async move {
            let outcome = async {
                let conn_arc = controls
                    .lock()
                    .await
                    .get(&space_id)
                    .cloned()
                    .ok_or_else(|| anyhow!("Workspace is not connected."))?;
                let mut conn = conn_arc.lock().await;
                conn.get_schema(database.as_deref()).await
            }
            .await;
            let _ = tx.send(outcome);
        });
        rx
    }

    /// Execute SQL on a tab's dedicated persistent session. Returns a oneshot receiver for results.
    pub fn execute_tab_query(
        &self,
        tab_id: String,
        profile: ConnectionProfile,
        database: Option<String>,
        sql: String,
        options: QueryExecutionOptions,
    ) -> tokio::sync::oneshot::Receiver<anyhow::Result<Vec<QueryResult>>> {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let catalog = self.catalog.clone();
        let tabs = self.tab_sessions.clone();
        let in_flight = self.in_flight.clone();
        let tab_key = tab_id.clone();

        let handle = self.runtime.spawn(async move {
            let outcome = async {
                let existing = tabs.lock().await.get(&tab_key).cloned();
                let conn_arc = match existing {
                    Some(c) => c,
                    None => {
                        let provider = catalog
                            .find(&profile.provider_id)
                            .ok_or_else(|| anyhow!("Unknown provider '{}'.", profile.provider_id))?;
                        let mut conn = provider.create_connection(profile);
                        conn.open().await?;
                        let arc = Arc::new(Mutex::new(conn));
                        tabs.lock().await.insert(tab_key.clone(), arc.clone());
                        arc
                    }
                };

                let mut conn = conn_arc.lock().await;
                conn.execute_batch(&sql, database.as_deref(), &options)
                    .await
            }
            .await;

            in_flight.lock().await.remove(&tab_key);
            let _ = tx.send(outcome);
        });

        let abort_handle = handle.abort_handle();
        let in_flight_map = self.in_flight.clone();
        self.runtime.spawn(async move {
            if let Some(prev) = in_flight_map.lock().await.insert(tab_id, abort_handle) {
                prev.abort();
            }
        });

        rx
    }

    /// Cancel an in-flight execution on `tab_id` and drop the interrupted connection so the next run opens cleanly.
    pub fn cancel_tab_query(&self, tab_id: String) {
        let tabs = self.tab_sessions.clone();
        let in_flight = self.in_flight.clone();
        self.runtime.spawn(async move {
            if let Some(handle) = in_flight.lock().await.remove(&tab_id) {
                handle.abort();
            }
            tabs.lock().await.remove(&tab_id);
        });
    }
}
