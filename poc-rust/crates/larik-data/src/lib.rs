use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use serde::{Deserialize, Serialize};

pub mod sql_language;

pub const DEFAULT_MAX_ROWS: usize = 5_000;
pub const MAX_ALLOWED_ROWS: usize = 100_000;

pub fn normalize_max_rows(max_rows: usize) -> usize {
    if max_rows == 0 {
        DEFAULT_MAX_ROWS
    } else {
        max_rows.min(MAX_ALLOWED_ROWS)
    }
}

/// How a provider setting should be rendered in the connection dialog.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ProviderSettingKind {
    Text,
    Password,
    Number,
    Boolean,
    File,
}

/// One connection field declared by a provider so the shell stays provider-agnostic.
#[derive(Debug, Clone)]
pub struct ProviderSetting {
    pub key: &'static str,
    pub label: &'static str,
    pub kind: ProviderSettingKind,
    pub required: bool,
    pub default_value: Option<&'static str>,
    pub placeholder: Option<&'static str>,
    pub hint: Option<&'static str>,
    pub is_advanced: bool,
}

/// Saved connection values for one workspace (passwords are excluded from disk persistence).
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ConnectionProfile {
    pub provider_id: String,
    pub values: BTreeMap<String, String>,
}

impl ConnectionProfile {
    pub fn new(provider_id: impl Into<String>) -> Self {
        Self {
            provider_id: provider_id.into(),
            values: BTreeMap::new(),
        }
    }

    pub fn get(&self, key: &str) -> Option<&str> {
        self.values
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(key))
            .map(|(_, v)| v.as_str())
    }

    pub fn get_non_empty(&self, key: &str) -> Option<&str> {
        self.get(key).map(str::trim).filter(|s| !s.is_empty())
    }

    pub fn get_u16(&self, key: &str) -> Option<u16> {
        self.get_non_empty(key)?.parse().ok()
    }

    pub fn get_u64(&self, key: &str, fallback: u64) -> u64 {
        self.get_non_empty(key)
            .and_then(|v| v.parse().ok())
            .unwrap_or(fallback)
    }

    pub fn get_bool(&self, key: &str, fallback: bool) -> bool {
        self.get_non_empty(key)
            .and_then(|v| v.eq_ignore_ascii_case("true").then_some(true).or_else(|| {
                v.eq_ignore_ascii_case("false").then_some(false)
            }))
            .unwrap_or(fallback)
    }

    pub fn set(&mut self, key: impl Into<String>, value: impl Into<String>) {
        self.values.insert(key.into(), value.into());
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConnectionState {
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

#[derive(Debug, Clone)]
pub struct QueryColumn {
    pub name: String,
    pub data_type: Option<String>,
}

#[derive(Debug, Clone)]
pub struct QueryExecutionOptions {
    pub max_rows: usize,
}

impl Default for QueryExecutionOptions {
    fn default() -> Self {
        Self {
            max_rows: DEFAULT_MAX_ROWS,
        }
    }
}

impl QueryExecutionOptions {
    pub fn effective_max_rows(&self) -> usize {
        normalize_max_rows(self.max_rows)
    }
}

/// One executed result set or command outcome.
#[derive(Debug, Clone)]
pub struct QueryResult {
    pub columns: Vec<QueryColumn>,
    pub rows: Vec<Vec<Option<String>>>,
    pub records_affected: Option<i64>,
    pub message: String,
    pub is_error: bool,
    pub elapsed: Duration,
    pub database: Option<String>,
    pub is_truncated: bool,
    pub row_limit: Option<usize>,
    pub messages: Vec<String>,
    pub statement: Option<String>,
}

impl QueryResult {
    pub fn error(message: impl Into<String>, elapsed: Duration, database: Option<String>) -> Self {
        Self {
            columns: Vec::new(),
            rows: Vec::new(),
            records_affected: None,
            message: message.into(),
            is_error: true,
            elapsed,
            database,
            is_truncated: false,
            row_limit: None,
            messages: Vec::new(),
            statement: None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DatabaseInfo {
    pub name: String,
    pub is_accessible: bool,
}

impl DatabaseInfo {
    pub fn accessible(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            is_accessible: true,
        }
    }

    pub fn inaccessible(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            is_accessible: false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TableKind {
    Table,
    View,
}

#[derive(Debug, Clone)]
pub struct ColumnSchema {
    pub name: String,
    pub data_type: Option<String>,
    pub nullable: bool,
}

#[derive(Debug, Clone)]
pub struct ForeignKeySchema {
    pub constraint: String,
    pub column: String,
    pub referenced_schema: Option<String>,
    pub referenced_table: String,
    pub referenced_column: String,
}

#[derive(Debug, Clone)]
pub struct TableSchema {
    pub schema: String,
    pub name: String,
    pub kind: TableKind,
    pub columns: Vec<ColumnSchema>,
    pub primary_key: Vec<String>,
    pub foreign_keys: Vec<ForeignKeySchema>,
}

/// A single text span returned by statement or batch splitting.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TextSpan {
    pub start: usize,
    pub end: usize,
    pub text: String,
}

/// Provider-owned editor language seam (splitting & dialect keywords).
pub trait EditorLanguage: Send + Sync {
    /// Language id passed to syntax highlighters (e.g. `"sql"`).
    fn syntax_id(&self) -> &'static str {
        "sql"
    }

    /// Split a document into individual statements (for Ctrl+Enter).
    fn split_statements(&self, text: &str) -> Vec<TextSpan>;

    /// Find the statement containing `byte_offset` (Ctrl+Enter target).
    fn statement_at(&self, text: &str, byte_offset: usize) -> Option<TextSpan> {
        let statements = self.split_statements(text);
        if statements.is_empty() {
            return None;
        }
        let clamped = byte_offset.min(text.len());
        if let Some(found) = statements
            .iter()
            .find(|s| clamped >= s.start && clamped <= s.end)
        {
            return Some(found.clone());
        }
        // Fallback: closest preceding statement, or first statement.
        statements
            .iter()
            .rev()
            .find(|s| s.start <= clamped)
            .or_else(|| statements.first())
            .cloned()
    }

    /// Split a document into execution batches (e.g. `GO` for T-SQL).
    fn split_batches(&self, text: &str) -> Vec<TextSpan>;

    /// Dialect keywords offered in editor completion.
    fn keywords(&self) -> &'static [&'static str];

    /// Dialect built-in functions offered in editor completion.
    fn functions(&self) -> &'static [&'static str];
}

/// One live session to a database engine. Each query tab owns its own connection.
#[async_trait]
pub trait DatabaseConnection: Send + Sync {
    fn state(&self) -> ConnectionState;

    fn server_version(&self) -> Option<String> {
        None
    }

    async fn open(&mut self) -> anyhow::Result<()>;

    async fn close(&mut self) -> anyhow::Result<()>;

    async fn list_databases(&mut self) -> anyhow::Result<Vec<DatabaseInfo>>;

    async fn get_schema(&mut self, database: Option<&str>) -> anyhow::Result<Vec<TableSchema>>;

    async fn execute_batch(
        &mut self,
        sql: &str,
        database: Option<&str>,
        options: &QueryExecutionOptions,
    ) -> anyhow::Result<Vec<QueryResult>>;
}

/// Pluggable database provider. The shell interacts with all databases solely through this trait.
pub trait DatabaseProvider: Send + Sync {
    fn id(&self) -> &'static str;
    fn display_name(&self) -> &'static str;
    fn dialect(&self) -> &'static str;
    fn settings(&self) -> &'static [ProviderSetting];
    fn editor_language(&self) -> Arc<dyn EditorLanguage>;

    fn describe_endpoint(&self, _profile: &ConnectionProfile) -> Option<String> {
        None
    }

    fn describe_identity(&self, _profile: &ConnectionProfile) -> Option<String> {
        None
    }

    fn default_profile(&self) -> ConnectionProfile {
        let mut profile = ConnectionProfile::new(self.id());
        for setting in self.settings() {
            if let Some(default) = setting.default_value {
                profile.set(setting.key, default);
            }
        }
        profile
    }

    fn password_keys(&self) -> Vec<&'static str> {
        self.settings()
            .iter()
            .filter(|s| s.kind == ProviderSettingKind::Password)
            .map(|s| s.key)
            .collect()
    }

    fn create_connection(&self, profile: ConnectionProfile) -> Box<dyn DatabaseConnection>;
}

/// Registry of available providers discovered by the host.
#[derive(Default, Clone)]
pub struct ProviderCatalog {
    providers: Vec<Arc<dyn DatabaseProvider>>,
}

impl ProviderCatalog {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn register(&mut self, provider: Arc<dyn DatabaseProvider>) {
        self.providers.push(provider);
    }

    pub fn all(&self) -> &[Arc<dyn DatabaseProvider>] {
        &self.providers
    }

    pub fn find(&self, id: &str) -> Option<Arc<dyn DatabaseProvider>> {
        self.providers
            .iter()
            .find(|p| p.id().eq_ignore_ascii_case(id))
            .cloned()
    }
}
