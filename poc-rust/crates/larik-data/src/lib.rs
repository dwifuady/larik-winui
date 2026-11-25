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

/// Completion item kind for editor suggestions.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompletionKind {
    Table,
    Column,
    Keyword,
    Function,
    Join,
    Database,
    Variable,
    Cte,
    Snippet,
}

/// Reusable SQL snippet definition passed to completion.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SnippetDef {
    pub trigger: String,
    pub name: String,
    pub content: String,
    pub description: Option<String>,
    pub category: Option<String>,
    pub dialects: Option<Vec<String>>,
}

/// Result of expanding snippet placeholders (`${cursor}`, `${n:default}`, `${paste}`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExpandedSnippet {
    pub text: String,
    pub caret_offset: usize,
    pub selection: Option<(usize, usize)>,
}

/// Returns `true` when `content` contains a `${paste}` token (case-insensitive).
pub fn snippet_uses_clipboard(content: &str) -> bool {
    content
        .as_bytes()
        .windows(8)
        .any(|w| w.eq_ignore_ascii_case(b"${paste}"))
}

/// Expands snippet markup into insertable SQL text and post-insert caret/selection offsets
/// (UTF-8 byte offsets relative to the start of `text`):
/// - `${cursor}` -> records final caret byte offset
/// - `${paste}` -> appends `clipboard`
/// - `${n:default}` / `${n}` (non-empty ASCII digits `n`) -> first occurrence records `default`
///   and sets `selection` if still `None`; repeated `n` appends the remembered `default`
/// - Unknown `${...}` or unclosed `${` -> emitted verbatim
pub fn expand_snippet(content: &str, clipboard: &str) -> ExpandedSnippet {
    let mut text = String::with_capacity(content.len());
    let mut markers: BTreeMap<String, String> = BTreeMap::new();
    let mut selection: Option<(usize, usize)> = None;
    let mut caret: Option<usize> = None;

    let mut i = 0;
    while i < content.len() {
        if content[i..].starts_with("${") {
            if let Some(rel_close) = content[i + 2..].find('}') {
                let close = i + 2 + rel_close;
                let token = &content[i + 2..close];

                if token.eq_ignore_ascii_case("cursor") {
                    caret = Some(text.len());
                    i = close + 1;
                    continue;
                }

                if token.eq_ignore_ascii_case("paste") {
                    text.push_str(clipboard);
                    i = close + 1;
                    continue;
                }

                let (key, value) = match token.find(':') {
                    Some(colon) => (&token[..colon], &token[colon + 1..]),
                    None => (token, ""),
                };

                if !key.is_empty() && key.bytes().all(|b| b.is_ascii_digit()) {
                    if let Some(existing) = markers.get(key) {
                        text.push_str(existing);
                    } else {
                        markers.insert(key.to_string(), value.to_string());
                        let start_byte = text.len();
                        text.push_str(value);
                        if selection.is_none() {
                            selection = Some((start_byte, value.len()));
                        }
                    }
                    i = close + 1;
                    continue;
                }

                // Unknown `${...}`: emit verbatim.
                text.push_str(&content[i..=close]);
                i = close + 1;
                continue;
            }
        }

        let ch = content[i..].chars().next().unwrap();
        text.push(ch);
        i += ch.len_utf8();
    }

    let caret_offset = caret
        .or_else(|| selection.map(|(s, l)| s + l))
        .unwrap_or(text.len());

    ExpandedSnippet {
        text,
        caret_offset,
        selection,
    }
}

/// One completion candidate returned by an [`EditorLanguage`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CompletionCandidate {
    pub label: String,
    pub detail: Option<String>,
    pub insert_text: String,
    pub filter_text: Option<String>,
    pub kind: CompletionKind,
    pub caret_offset: Option<usize>,
    pub selection: Option<(usize, usize)>,
    pub documentation: Option<String>,
}

/// Provider-agnostic input context passed to [`EditorLanguage::suggest_completions`].
#[derive(Debug, Clone, Copy)]
pub struct CompletionRequest<'a> {
    pub text: &'a str,
    pub byte_offset: usize,
    pub tables: &'a [TableSchema],
    pub databases: &'a [DatabaseInfo],
    pub is_explicit: bool,
    pub snippets: &'a [SnippetDef],
    pub clipboard: &'a str,
}

/// Completion candidates and replacement start byte offset returned by [`EditorLanguage::suggest_completions`].
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CompletionResult {
    pub replace_start: usize,
    pub items: Vec<CompletionCandidate>,
}

/// Provider-owned editor language seam (splitting, dialect keywords, & completion).
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

    /// Quote a SQL identifier using this dialect's delimiters (default: T-SQL `[name]`).
    fn quote_identifier(&self, identifier: &str) -> String {
        format!("[{}]", identifier.replace(']', "]]"))
    }

    /// Generate completion suggestions for the given editor context.
    fn suggest_completions(&self, request: &CompletionRequest<'_>) -> CompletionResult {
        let (replace_start, items) = crate::sql_language::suggest_completions_with_snippets(
            request.text,
            request.byte_offset,
            request.tables,
            request.databases,
            request.snippets,
            request.clipboard,
            self,
            request.is_explicit,
        );
        CompletionResult {
            replace_start,
            items,
        }
    }

    /// Return `true` if inserting `new_text` at `_byte_offset` should trigger auto-completion.
    fn is_completion_trigger(&self, _byte_offset: usize, new_text: &str) -> bool {
        matches!(new_text, "." | " " | "[" | "\"" | "`" | "@")
            || new_text
                .chars()
                .last()
                .is_some_and(|c| c.is_ascii_alphabetic() || c == '_')
    }
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

    /// Optional security/transport warning shown live in the workspace settings dialog
    /// (for example when TLS certificate validation is disabled on a remote host).
    fn transport_warning(&self, _profile: &ConnectionProfile) -> Option<&'static str> {
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

#[cfg(test)]
mod tests {
    use super::*;

    struct MockMongoLanguage;

    impl EditorLanguage for MockMongoLanguage {
        fn syntax_id(&self) -> &'static str {
            "javascript"
        }

        fn split_statements(&self, text: &str) -> Vec<TextSpan> {
            let trimmed = text.trim();
            if trimmed.is_empty() {
                Vec::new()
            } else {
                vec![TextSpan {
                    start: 0,
                    end: text.len(),
                    text: trimmed.to_string(),
                }]
            }
        }

        fn split_batches(&self, text: &str) -> Vec<TextSpan> {
            self.split_statements(text)
        }

        fn keywords(&self) -> &'static [&'static str] {
            &["$match", "$group", "$project"]
        }

        fn functions(&self) -> &'static [&'static str] {
            &["find", "aggregate"]
        }

        fn suggest_completions(&self, request: &CompletionRequest<'_>) -> CompletionResult {
            let prefix_start = request.text[..request.byte_offset]
                .rfind(|c: char| !c.is_ascii_alphanumeric() && c != '$' && c != '_')
                .map(|idx| idx + 1)
                .unwrap_or(0);
            let typed = &request.text[prefix_start..request.byte_offset];

            let mut items = Vec::new();
            if typed.starts_with('$') {
                for op in self.keywords() {
                    if op.starts_with(typed) {
                        items.push(CompletionCandidate {
                            label: (*op).to_string(),
                            detail: Some("MongoDB operator".into()),
                            insert_text: (*op).to_string(),
                            filter_text: Some((*op).to_string()),
                            kind: CompletionKind::Keyword,
                            caret_offset: None,
                            selection: None,
                            documentation: None,
                        });
                    }
                }
            } else {
                for table in request.tables {
                    items.push(CompletionCandidate {
                        label: table.name.clone(),
                        detail: Some("Collection".into()),
                        insert_text: format!("db.getCollection(\"{}\")", table.name),
                        filter_text: Some(table.name.clone()),
                        kind: CompletionKind::Table,
                        caret_offset: None,
                        selection: None,
                        documentation: None,
                    });
                }
            }

            CompletionResult {
                replace_start: prefix_start,
                items,
            }
        }

        fn is_completion_trigger(&self, _byte_offset: usize, new_text: &str) -> bool {
            matches!(new_text, "{" | "$" | ":" | "\"")
        }
    }

    #[test]
    fn custom_non_sql_language_plugs_into_editor_language_seam() {
        let lang: Arc<dyn EditorLanguage> = Arc::new(MockMongoLanguage);

        assert_eq!(lang.syntax_id(), "javascript");
        assert!(lang.is_completion_trigger(0, "{"));
        assert!(lang.is_completion_trigger(0, "$"));
        assert!(lang.is_completion_trigger(0, ":"));
        assert!(lang.is_completion_trigger(0, "\""));
        assert!(!lang.is_completion_trigger(0, "["));
        assert!(!lang.is_completion_trigger(0, "a"));

        let collections = vec![TableSchema {
            schema: String::new(),
            name: "orders".into(),
            kind: TableKind::Table,
            columns: Vec::new(),
            primary_key: Vec::new(),
            foreign_keys: Vec::new(),
        }];

        let op_query = "db.orders.aggregate([{ $ma";
        let op_res = lang.suggest_completions(&CompletionRequest {
            text: op_query,
            byte_offset: op_query.len(),
            tables: &collections,
            databases: &[],
            is_explicit: false,
            snippets: &[],
            clipboard: "",
        });
        assert_eq!(op_res.replace_start, "db.orders.aggregate([{ ".len());
        assert_eq!(op_res.items.len(), 1);
        assert_eq!(op_res.items[0].label, "$match");
        assert_eq!(op_res.items[0].kind, CompletionKind::Keyword);

        let col_query = "db.";
        let col_res = lang.suggest_completions(&CompletionRequest {
            text: col_query,
            byte_offset: col_query.len(),
            tables: &collections,
            databases: &[],
            is_explicit: true,
            snippets: &[],
            clipboard: "",
        });
        assert_eq!(col_res.items.len(), 1);
        assert_eq!(
            col_res.items[0].insert_text,
            "db.getCollection(\"orders\")"
        );
    }

    #[test]
    fn expand_snippet_handles_cursor_placeholders_paste_and_unknown_tokens() {
        // ${cursor}
        let sel = expand_snippet("SELECT * FROM ${cursor}", "");
        assert_eq!(sel.text, "SELECT * FROM ");
        assert_eq!(sel.caret_offset, "SELECT * FROM ".len());
        assert_eq!(sel.selection, None);

        // ${1:default} selection sets caret at end of first selection when no ${cursor}
        let selt = expand_snippet("SELECT TOP ${1:100} * FROM dbo.Orders", "");
        assert_eq!(selt.text, "SELECT TOP 100 * FROM dbo.Orders");
        assert_eq!(selt.selection, Some(("SELECT TOP ".len(), 3)));
        assert_eq!(selt.caret_offset, "SELECT TOP 100".len());

        // Repeated ${1} reuses first default without moving selection
        let rep = expand_snippet("SELECT ${1:col} FROM ${2:tbl} GROUP BY ${1}", "");
        assert_eq!(rep.text, "SELECT col FROM tbl GROUP BY col");
        assert_eq!(rep.selection, Some(("SELECT ".len(), 3)));
        assert_eq!(rep.caret_offset, "SELECT col".len());

        // ${paste} clipboard substitution (case-insensitive)
        assert!(snippet_uses_clipboard("${PASTE}${cursor}"));
        assert!(!snippet_uses_clipboard("SELECT * FROM ${cursor}"));
        let pasted = expand_snippet("WHERE id IN (${PaStE})${CURSOR}", "1, 2, 3");
        assert_eq!(pasted.text, "WHERE id IN (1, 2, 3)");
        assert_eq!(pasted.caret_offset, "WHERE id IN (1, 2, 3)".len());
        assert_eq!(pasted.selection, None);

        // Combined ${1:table} + ${cursor} keeps first selection AND final caret at ${cursor}
        let selw = expand_snippet("SELECT * FROM ${1:table} WHERE ${cursor}", "");
        assert_eq!(selw.text, "SELECT * FROM table WHERE ");
        assert_eq!(selw.selection, Some(("SELECT * FROM ".len(), "table".len())));
        assert_eq!(selw.caret_offset, "SELECT * FROM table WHERE ".len());

        // Unknown ${...} and unclosed ${ preserved verbatim
        let unknown = expand_snippet("SELECT ${foo}, ${bar FROM ${unclosed", "");
        assert_eq!(unknown.text, "SELECT ${foo}, ${bar FROM ${unclosed");
        assert_eq!(unknown.caret_offset, unknown.text.len());
        assert_eq!(unknown.selection, None);
    }
}
