use std::collections::BTreeMap;
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll};
use std::time::{Duration, Instant};

use anyhow::{Context as _, anyhow};
use async_trait::async_trait;
use futures_util::TryStreamExt;
use larik_data::sql_language::SqlLanguage;
use larik_data::{
    ColumnSchema, ConnectionProfile, ConnectionState, DatabaseConnection, DatabaseInfo,
    DatabaseProvider, EditorLanguage, ForeignKeySchema, ProviderSetting, ProviderSettingKind,
    QueryColumn, QueryExecutionOptions, QueryResult, TableKind, TableSchema,
};
use tiberius::{AuthMethod, Client, ColumnData, Config, EncryptionLevel, QueryItem, SqlBrowser};
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio::net::TcpStream;
use tokio_util::compat::{Compat, TokioAsyncWriteCompatExt};

pub const MSSQL_PROVIDER_ID: &str = "mssql";

static MSSQL_SETTINGS: &[ProviderSetting] = &[
    ProviderSetting {
        key: "server",
        label: "Server",
        kind: ProviderSettingKind::Text,
        required: true,
        default_value: Some(r"(localdb)\MSSQLLocalDB"),
        placeholder: Some(r"localhost,1433 or (localdb)\MSSQLLocalDB"),
        hint: Some("Supports TCP host[:port], host\\instance, and Windows LocalDB."),
        is_advanced: false,
    },
    ProviderSetting {
        key: "port",
        label: "Port",
        kind: ProviderSettingKind::Number,
        required: false,
        default_value: None,
        placeholder: Some("optional, e.g. 1433"),
        hint: None,
        is_advanced: true,
    },
    ProviderSetting {
        key: "database",
        label: "Initial database",
        kind: ProviderSettingKind::Text,
        required: false,
        default_value: None,
        placeholder: Some("optional, e.g. master"),
        hint: None,
        is_advanced: false,
    },
    ProviderSetting {
        key: "user",
        label: "User",
        kind: ProviderSettingKind::Text,
        required: false,
        default_value: None,
        placeholder: Some("leave empty for Windows authentication"),
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
    ProviderSetting {
        key: "encrypt",
        label: "Encrypt",
        kind: ProviderSettingKind::Boolean,
        required: false,
        default_value: Some("true"),
        placeholder: None,
        hint: None,
        is_advanced: true,
    },
    ProviderSetting {
        key: "trustServerCertificate",
        label: "Trust server certificate",
        kind: ProviderSettingKind::Boolean,
        required: false,
        default_value: Some("false"),
        placeholder: None,
        hint: Some("Local targets are trusted automatically; enable only for dev/self-signed remote servers."),
        is_advanced: true,
    },
    ProviderSetting {
        key: "connectTimeout",
        label: "Connect timeout (s)",
        kind: ProviderSettingKind::Number,
        required: false,
        default_value: Some("8"),
        placeholder: None,
        hint: None,
        is_advanced: true,
    },
];

pub struct SqlServerProvider;

impl DatabaseProvider for SqlServerProvider {
    fn id(&self) -> &'static str {
        MSSQL_PROVIDER_ID
    }

    fn display_name(&self) -> &'static str {
        "SQL Server"
    }

    fn dialect(&self) -> &'static str {
        "mssql"
    }

    fn settings(&self) -> &'static [ProviderSetting] {
        MSSQL_SETTINGS
    }

    fn editor_language(&self) -> Arc<dyn EditorLanguage> {
        Arc::new(SqlLanguage::tsql())
    }

    fn describe_endpoint(&self, profile: &ConnectionProfile) -> Option<String> {
        profile
            .get_non_empty("server")
            .map(|s| format!("SQL Server · {s}"))
    }

    fn describe_identity(&self, profile: &ConnectionProfile) -> Option<String> {
        Some(
            profile
                .get_non_empty("user")
                .map(str::to_string)
                .unwrap_or_else(|| "Windows authentication".to_string()),
        )
    }

    fn transport_warning(&self, profile: &ConnectionProfile) -> Option<&'static str> {
        let server = profile.get_non_empty("server").unwrap_or_default();
        if server.is_empty() || is_local_target(server) {
            return None;
        }
        if !profile.get_bool("encrypt", true) {
            return Some(
                "This connection is not encrypted. Credentials and query data travel over the network in plain text.",
            );
        }
        if profile.get_bool("trustServerCertificate", false) {
            return Some(
                "This connection trusts any server certificate, so someone on the network could impersonate the server.",
            );
        }
        None
    }

    fn create_connection(&self, profile: ConnectionProfile) -> Box<dyn DatabaseConnection> {
        Box::new(SqlServerConnection {
            profile,
            state: ConnectionState::Disconnected,
            client: None,
            version: None,
            current_database: None,
        })
    }
}

/// Transport abstraction so a single `tiberius::Client` handles both TCP and Windows LocalDB named pipes.
enum MssqlStream {
    Tcp(TcpStream),
    #[cfg(windows)]
    NamedPipe(tokio::net::windows::named_pipe::NamedPipeClient),
}

impl AsyncRead for MssqlStream {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<std::io::Result<()>> {
        match self.get_mut() {
            MssqlStream::Tcp(s) => Pin::new(s).poll_read(cx, buf),
            #[cfg(windows)]
            MssqlStream::NamedPipe(s) => Pin::new(s).poll_read(cx, buf),
        }
    }
}

impl AsyncWrite for MssqlStream {
    fn poll_write(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<std::io::Result<usize>> {
        match self.get_mut() {
            MssqlStream::Tcp(s) => Pin::new(s).poll_write(cx, buf),
            #[cfg(windows)]
            MssqlStream::NamedPipe(s) => Pin::new(s).poll_write(cx, buf),
        }
    }

    fn poll_flush(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        match self.get_mut() {
            MssqlStream::Tcp(s) => Pin::new(s).poll_flush(cx),
            #[cfg(windows)]
            MssqlStream::NamedPipe(s) => Pin::new(s).poll_flush(cx),
        }
    }

    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        match self.get_mut() {
            MssqlStream::Tcp(s) => Pin::new(s).poll_shutdown(cx),
            #[cfg(windows)]
            MssqlStream::NamedPipe(s) => Pin::new(s).poll_shutdown(cx),
        }
    }
}

struct SqlServerConnection {
    profile: ConnectionProfile,
    state: ConnectionState,
    client: Option<Client<Compat<MssqlStream>>>,
    version: Option<String>,
    current_database: Option<String>,
}

impl SqlServerConnection {
    async fn ensure_database(&mut self, database: Option<&str>) -> anyhow::Result<()> {
        let Some(target) = database.map(str::trim).filter(|s| !s.is_empty()) else {
            return Ok(());
        };
        if self
            .current_database
            .as_deref()
            .is_some_and(|cur| cur.eq_ignore_ascii_case(target))
        {
            return Ok(());
        }
        let client = self
            .client
            .as_mut()
            .ok_or_else(|| anyhow!("Not connected to SQL Server."))?;
        let escaped = quote_bracket_identifier(target);
        let sql = format!("USE {escaped};");
        let stream = client.simple_query(sql).await?;
        let _ = stream.into_results().await?;
        self.current_database = Some(target.to_string());
        Ok(())
    }
}

#[async_trait]
impl DatabaseConnection for SqlServerConnection {
    fn state(&self) -> ConnectionState {
        self.state
    }

    fn server_version(&self) -> Option<String> {
        self.version.clone()
    }

    async fn open(&mut self) -> anyhow::Result<()> {
        self.state = ConnectionState::Connecting;
        let server_raw = self
            .profile
            .get_non_empty("server")
            .ok_or_else(|| anyhow!("Server is required."))?
            .to_string();

        let timeout_secs = self.profile.get_u64("connectTimeout", 8).max(1);
        let connect_fut = connect_tiberius(&server_raw, &self.profile);

        let result = tokio::time::timeout(Duration::from_secs(timeout_secs), connect_fut).await;
        match result {
            Ok(Ok(mut client)) => {
                // Capture server version & initial database.
                if let Ok(stream) = client
                    .simple_query("SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)), DB_NAME()")
                    .await
                {
                    if let Ok(Some(row)) = stream.into_row().await {
                        if let Some(ver) = row.get::<&str, _>(0) {
                            self.version = Some(format!("SQL Server {ver}"));
                        }
                        if let Some(db) = row.get::<&str, _>(1) {
                            self.current_database = Some(db.to_string());
                        }
                    }
                }
                self.client = Some(client);
                self.state = ConnectionState::Connected;
                Ok(())
            }
            Ok(Err(err)) => {
                self.state = ConnectionState::Faulted;
                Err(err)
            }
            Err(_) => {
                self.state = ConnectionState::Faulted;
                Err(anyhow!(
                    "Timed out connecting to '{server_raw}' after {timeout_secs}s."
                ))
            }
        }
    }

    async fn close(&mut self) -> anyhow::Result<()> {
        self.client = None;
        self.current_database = None;
        self.state = ConnectionState::Disconnected;
        Ok(())
    }

    async fn list_databases(&mut self) -> anyhow::Result<Vec<DatabaseInfo>> {
        let client = self
            .client
            .as_mut()
            .ok_or_else(|| anyhow!("Not connected to SQL Server."))?;

        let sql = "SELECT name, CAST(ISNULL(HAS_DBACCESS(name), 0) AS INT) AS accessible \
                   FROM sys.databases WHERE state = 0 ORDER BY name";
        let rows = client.simple_query(sql).await?.into_first_result().await?;
        let mut dbs = Vec::with_capacity(rows.len());
        for row in rows {
            let name = row.get::<&str, _>(0).unwrap_or_default().to_string();
            let accessible = row.get::<i32, _>(1).unwrap_or(0) != 0;
            if !name.is_empty() {
                dbs.push(DatabaseInfo {
                    name,
                    is_accessible: accessible,
                });
            }
        }
        Ok(dbs)
    }

    async fn get_schema(&mut self, database: Option<&str>) -> anyhow::Result<Vec<TableSchema>> {
        self.ensure_database(database).await?;
        let client = self
            .client
            .as_mut()
            .ok_or_else(|| anyhow!("Not connected to SQL Server."))?;

        // 1. Tables & Views
        let tables_sql = "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE \
                          FROM INFORMATION_SCHEMA.TABLES \
                          ORDER BY TABLE_SCHEMA, TABLE_NAME";
        let table_rows = client
            .simple_query(tables_sql)
            .await?
            .into_first_result()
            .await?;

        let mut map: BTreeMap<(String, String), TableSchema> = BTreeMap::new();
        for row in table_rows {
            let schema = row.get::<&str, _>(0).unwrap_or("dbo").to_string();
            let name = row.get::<&str, _>(1).unwrap_or_default().to_string();
            let kind = match row.get::<&str, _>(2).unwrap_or("BASE TABLE") {
                "VIEW" => TableKind::View,
                _ => TableKind::Table,
            };
            if !name.is_empty() {
                map.insert(
                    (schema.clone(), name.clone()),
                    TableSchema {
                        schema,
                        name,
                        kind,
                        columns: Vec::new(),
                        primary_key: Vec::new(),
                        foreign_keys: Vec::new(),
                    },
                );
            }
        }

        // 2. Columns (using sys.columns so is_identity / is_computed are available for safe row editing)
        let cols_sql = "SELECT s.name, o.name, c.name, ty.name, \
                               CAST(c.is_nullable AS INT), \
                               CAST(c.is_identity AS INT), \
                               CAST(c.is_computed AS INT) \
                        FROM sys.columns c \
                        INNER JOIN sys.objects o ON c.object_id = o.object_id \
                        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id \
                        INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id \
                        WHERE o.type IN ('U', 'V') \
                        ORDER BY s.name, o.name, c.column_id";
        if let Ok(stream) = client.simple_query(cols_sql).await {
            if let Ok(rows) = stream.into_first_result().await {
                for row in rows {
                    let schema = row.get::<&str, _>(0).unwrap_or("dbo").to_string();
                    let table = row.get::<&str, _>(1).unwrap_or_default().to_string();
                    let col_name = row.get::<&str, _>(2).unwrap_or_default().to_string();
                    let data_type = row.get::<&str, _>(3).map(str::to_string);
                    let nullable = row.get::<i32, _>(4).unwrap_or(1) != 0;
                    let is_identity = row.get::<i32, _>(5).unwrap_or(0) != 0;
                    let is_computed = row.get::<i32, _>(6).unwrap_or(0) != 0;
                    if let Some(entry) = map.get_mut(&(schema, table)) {
                        entry.columns.push(ColumnSchema {
                            name: col_name,
                            data_type,
                            nullable,
                            is_identity,
                            is_computed,
                        });
                    }
                }
            }
        }

        // 3. Primary Keys
        let pk_sql = "SELECT s.name, t.name, c.name \
                      FROM sys.indexes i \
                      INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id \
                      INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id \
                      INNER JOIN sys.tables t ON i.object_id = t.object_id \
                      INNER JOIN sys.schemas s ON t.schema_id = s.schema_id \
                      WHERE i.is_primary_key = 1 \
                      ORDER BY s.name, t.name, ic.key_ordinal";
        if let Ok(stream) = client.simple_query(pk_sql).await {
            if let Ok(rows) = stream.into_first_result().await {
                for row in rows {
                    let schema = row.get::<&str, _>(0).unwrap_or("dbo").to_string();
                    let table = row.get::<&str, _>(1).unwrap_or_default().to_string();
                    let col_name = row.get::<&str, _>(2).unwrap_or_default().to_string();
                    if let Some(entry) = map.get_mut(&(schema, table)) {
                        entry.primary_key.push(col_name);
                    }
                }
            }
        }

        // 4. Foreign Keys
        let fk_sql = "SELECT fk.name, ps.name, pt.name, pc.name, rs.name, rt.name, rc.name \
                      FROM sys.foreign_keys fk \
                      INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id \
                      INNER JOIN sys.tables pt ON fkc.parent_object_id = pt.object_id \
                      INNER JOIN sys.schemas ps ON pt.schema_id = ps.schema_id \
                      INNER JOIN sys.columns pc ON fkc.parent_object_id = pc.object_id AND fkc.parent_column_id = pc.column_id \
                      INNER JOIN sys.tables rt ON fkc.referenced_object_id = rt.object_id \
                      INNER JOIN sys.schemas rs ON rt.schema_id = rs.schema_id \
                      INNER JOIN sys.columns rc ON fkc.referenced_object_id = rc.object_id AND fkc.referenced_column_id = rc.column_id";
        if let Ok(stream) = client.simple_query(fk_sql).await {
            if let Ok(rows) = stream.into_first_result().await {
                for row in rows {
                    let constraint = row.get::<&str, _>(0).unwrap_or_default().to_string();
                    let schema = row.get::<&str, _>(1).unwrap_or("dbo").to_string();
                    let table = row.get::<&str, _>(2).unwrap_or_default().to_string();
                    let column = row.get::<&str, _>(3).unwrap_or_default().to_string();
                    let ref_schema = row.get::<&str, _>(4).map(str::to_string);
                    let ref_table = row.get::<&str, _>(5).unwrap_or_default().to_string();
                    let ref_column = row.get::<&str, _>(6).unwrap_or_default().to_string();
                    if let Some(entry) = map.get_mut(&(schema, table)) {
                        entry.foreign_keys.push(ForeignKeySchema {
                            constraint,
                            column,
                            referenced_schema: ref_schema,
                            referenced_table: ref_table,
                            referenced_column: ref_column,
                        });
                    }
                }
            }
        }

        Ok(map.into_values().collect())
    }

    async fn execute_batch(
        &mut self,
        sql: &str,
        database: Option<&str>,
        options: &QueryExecutionOptions,
    ) -> anyhow::Result<Vec<QueryResult>> {
        self.ensure_database(database).await?;
        let lang = SqlLanguage::tsql();
        let batches = lang.split_batches(sql);
        if batches.is_empty() {
            return Ok(vec![QueryResult::error(
                "Nothing to run — the editor is empty.",
                Duration::ZERO,
                database.map(str::to_string),
            )]);
        }

        let max_rows = options.effective_max_rows();
        let mut all_results = Vec::new();
        let active_db = database
            .map(str::to_string)
            .or_else(|| self.current_database.clone());

        for batch in batches {
            let started = Instant::now();
            let client = self
                .client
                .as_mut()
                .ok_or_else(|| anyhow!("Not connected to SQL Server."))?;

            match execute_single_batch(client, &batch.text, max_rows, active_db.clone(), started).await
            {
                Ok(mut batch_results) => {
                    // Refresh tracked database if the user ran `USE ...`.
                    if batch.text.to_ascii_uppercase().contains("USE ") {
                        if let Ok(stream) = client.simple_query("SELECT DB_NAME()").await {
                            if let Ok(Some(row)) = stream.into_row().await {
                                if let Some(db) = row.get::<&str, _>(0) {
                                    self.current_database = Some(db.to_string());
                                }
                            }
                        }
                    }
                    all_results.append(&mut batch_results);
                }
                Err(err) => {
                    all_results.push(QueryResult::error(
                        err.to_string(),
                        started.elapsed(),
                        active_db.clone(),
                    ));
                    break;
                }
            }
        }

        Ok(all_results)
    }

    async fn estimate_row_count(
        &mut self,
        schema: Option<&str>,
        table: &str,
        database: Option<&str>,
    ) -> Option<i64> {
        if self.ensure_database(database).await.is_err() {
            return None;
        }
        let client = self.client.as_mut()?;
        let qualified = match schema.map(str::trim).filter(|s| !s.is_empty()) {
            Some(s) => format!(
                "{}.{}",
                quote_bracket_identifier(s),
                quote_bracket_identifier(table)
            ),
            None => quote_bracket_identifier(table),
        };
        let escaped = qualified.replace('\'', "''");
        let sql = format!(
            "SELECT CAST(SUM(p.[rows]) AS BIGINT) FROM sys.partitions AS p \
             WHERE p.[object_id] = OBJECT_ID(N'{escaped}') AND p.[index_id] IN (0, 1);"
        );
        let fut = async {
            let stream = client.simple_query(sql).await.ok()?;
            let row = stream.into_row().await.ok()??;
            row.get::<i64, _>(0)
        };
        tokio::time::timeout(Duration::from_secs(5), fut)
            .await
            .ok()
            .flatten()
    }

    async fn execute_dml(
        &mut self,
        sql: &str,
        database: Option<&str>,
    ) -> anyhow::Result<u64> {
        self.ensure_database(database).await?;
        let client = self
            .client
            .as_mut()
            .ok_or_else(|| anyhow!("Not connected to SQL Server."))?;
        let outcome = client.execute(sql, &[]).await?;
        Ok(outcome.total())
    }
}

async fn execute_single_batch(
    client: &mut Client<Compat<MssqlStream>>,
    sql: &str,
    max_rows: usize,
    database: Option<String>,
    started: Instant,
) -> anyhow::Result<Vec<QueryResult>> {
    let mut stream = client.simple_query(sql).await?;
    let mut results: Vec<QueryResult> = Vec::new();
    let mut current_cols: Option<Vec<QueryColumn>> = None;
    let mut current_rows: Vec<Vec<Option<String>>> = Vec::new();
    let mut current_truncated = false;

    while let Some(item) = stream.try_next().await? {
        match item {
            QueryItem::Metadata(meta) => {
                if let Some(cols) = current_cols.take() {
                    let count = current_rows.len();
                    results.push(QueryResult {
                        columns: cols,
                        rows: std::mem::take(&mut current_rows),
                        records_affected: Some(count as i64),
                        message: format!("{count} rows"),
                        is_error: false,
                        elapsed: started.elapsed(),
                        database: database.clone(),
                        is_truncated: current_truncated,
                        row_limit: Some(max_rows),
                        messages: vec![format!("({count} rows returned)")],
                        statement: Some(sql.to_string()),
                    });
                    current_truncated = false;
                }

                let cols = meta
                    .columns()
                    .iter()
                    .enumerate()
                    .map(|(idx, col)| {
                        let name = if col.name().is_empty() {
                            format!("(column {})", idx + 1)
                        } else {
                            col.name().to_string()
                        };
                        QueryColumn {
                            name,
                            data_type: Some(format!("{:?}", col.column_type())),
                        }
                    })
                    .collect();
                current_cols = Some(cols);
            }
            QueryItem::Row(row) => {
                if current_rows.len() >= max_rows {
                    current_truncated = true;
                    continue;
                }
                let formatted: Vec<Option<String>> =
                    row.into_iter().map(format_column_data).collect();
                current_rows.push(formatted);
            }
        }
    }

    if let Some(cols) = current_cols.take() {
        let count = current_rows.len();
        results.push(QueryResult {
            columns: cols,
            rows: current_rows,
            records_affected: Some(count as i64),
            message: format!("{count} rows"),
            is_error: false,
            elapsed: started.elapsed(),
            database,
            is_truncated: current_truncated,
            row_limit: Some(max_rows),
            messages: vec![format!("({count} rows returned)")],
            statement: Some(sql.to_string()),
        });
    } else if results.is_empty() {
        results.push(QueryResult {
            columns: Vec::new(),
            rows: Vec::new(),
            records_affected: None,
            message: "Commands completed successfully.".into(),
            is_error: false,
            elapsed: started.elapsed(),
            database,
            is_truncated: false,
            row_limit: None,
            messages: vec!["Commands completed successfully.".into()],
            statement: Some(sql.to_string()),
        });
    }

    Ok(results)
}

fn format_column_data(data: ColumnData<'_>) -> Option<String> {
    match data {
        ColumnData::U8(v) => v.map(|x| x.to_string()),
        ColumnData::I16(v) => v.map(|x| x.to_string()),
        ColumnData::I32(v) => v.map(|x| x.to_string()),
        ColumnData::I64(v) => v.map(|x| x.to_string()),
        ColumnData::F32(v) => v.map(|x| x.to_string()),
        ColumnData::F64(v) => v.map(|x| x.to_string()),
        ColumnData::Bit(v) => v.map(|x| if x { "1".into() } else { "0".into() }),
        ColumnData::String(v) => v.map(|s| s.into_owned()),
        ColumnData::Guid(v) => v.map(|g| g.to_string()),
        ColumnData::Binary(v) => v.map(|bytes| {
            let mut hex = String::with_capacity(2 + bytes.len().min(64) * 2);
            hex.push_str("0x");
            for b in bytes.iter().take(64) {
                use std::fmt::Write;
                let _ = write!(&mut hex, "{b:02X}");
            }
            if bytes.len() > 64 {
                hex.push_str("…");
            }
            hex
        }),
        ColumnData::Numeric(v) => v.map(|n| n.to_string()),
        ColumnData::Xml(v) => v.map(|x| x.into_owned().into_string()),
        ColumnData::DateTime(v) => v.map(|dt| format!("{dt:?}")),
        ColumnData::SmallDateTime(v) => v.map(|dt| format!("{dt:?}")),
        ColumnData::DateTime2(v) => v.map(|dt| format!("{dt:?}")),
        ColumnData::Date(v) => v.map(|d| format!("{d:?}")),
        ColumnData::Time(v) => v.map(|t| format!("{t:?}")),
        ColumnData::DateTimeOffset(v) => v.map(|dto| format!("{dto:?}")),
    }
}

async fn connect_tiberius(
    server_raw: &str,
    profile: &ConnectionProfile,
) -> anyhow::Result<Client<Compat<MssqlStream>>> {
    let mut config = Config::new();
    config.application_name("Larik Rust PoC");

    if let Some(db) = profile.get_non_empty("database") {
        config.database(db);
    }

    if let Some(user) = profile.get_non_empty("user") {
        let pass = profile.get("password").unwrap_or_default();
        config.authentication(AuthMethod::sql_server(user, pass));
    } else {
        config.authentication(AuthMethod::Integrated);
    }

    let is_local = is_local_target(server_raw);
    let encrypt = profile.get_bool("encrypt", true);
    let trust_cert = is_local || profile.get_bool("trustServerCertificate", false);

    if encrypt {
        config.encryption(EncryptionLevel::Required);
    } else {
        config.encryption(EncryptionLevel::NotSupported);
    }
    if trust_cert {
        config.trust_cert();
    }

    // Handle Windows LocalDB: `(localdb)\MSSQLLocalDB`
    if let Some(instance) = parse_localdb_instance(server_raw) {
        #[cfg(windows)]
        {
            let pipe_name = resolve_localdb_pipe(&instance).await?;
            // LocalDB uses local IPC over a named pipe; disable strict TLS if not required.
            config.encryption(EncryptionLevel::Off);
            config.trust_cert();
            let pipe = tokio::net::windows::named_pipe::ClientOptions::new()
                .open(&pipe_name)
                .with_context(|| format!("Failed to open LocalDB named pipe '{pipe_name}'"))?;
            let client = Client::connect(config, MssqlStream::NamedPipe(pipe).compat_write())
                .await
                .with_context(|| format!("TDS handshake failed on LocalDB '{instance}'"))?;
            return Ok(client);
        }
        #[cfg(not(windows))]
        {
            return Err(anyhow!(
                "LocalDB ('{instance}') is only available on Windows."
            ));
        }
    }

    // Standard TCP / Named Instance parsing (`tcp:host,port`, `host:port`, `host\instance`)
    let cleaned = server_raw
        .trim()
        .strip_prefix("tcp:")
        .or_else(|| server_raw.trim().strip_prefix("TCP:"))
        .unwrap_or(server_raw.trim());

    let mut host = cleaned;
    let mut explicit_port = profile.get_u16("port");
    let mut instance_name: Option<&str> = None;

    if let Some((h, p)) = host.split_once(',') {
        host = h.trim();
        if explicit_port.is_none() {
            explicit_port = p.trim().parse::<u16>().ok();
        }
    } else if let Some((h, p)) = host.rsplit_once(':') {
        if !h.contains(':') {
            if let Ok(parsed) = p.trim().parse::<u16>() {
                host = h.trim();
                if explicit_port.is_none() {
                    explicit_port = Some(parsed);
                }
            }
        }
    }

    if let Some((h, inst)) = host.split_once('\\') {
        host = if h.trim().is_empty() || h.trim() == "." {
            "localhost"
        } else {
            h.trim()
        };
        if !inst.trim().is_empty() {
            instance_name = Some(inst.trim());
        }
    } else if host == "." {
        host = "localhost";
    }

    config.host(host);
    if let Some(port) = explicit_port {
        config.port(port);
    } else if let Some(inst) = instance_name {
        config.instance_name(inst);
    }

    let tcp = if instance_name.is_some() && explicit_port.is_none() {
        TcpStream::connect_named(&config)
            .await
            .with_context(|| format!("Could not connect to named SQL Server instance '{server_raw}'"))?
    } else {
        TcpStream::connect(config.get_addr())
            .await
            .with_context(|| format!("Could not connect to TCP '{}'", config.get_addr()))?
    };
    let _ = tcp.set_nodelay(true);

    let client = Client::connect(config, MssqlStream::Tcp(tcp).compat_write())
        .await
        .with_context(|| format!("SQL Server login/handshake failed for '{server_raw}'"))?;
    Ok(client)
}

fn parse_localdb_instance(server: &str) -> Option<String> {
    let trimmed = server.trim();
    let lower = trimmed.to_ascii_lowercase();
    if let Some(rest) = lower.strip_prefix(r"(localdb)\") {
        let idx = trimmed.len() - rest.len();
        let instance = trimmed[idx..].trim();
        if !instance.is_empty() {
            return Some(instance.to_string());
        }
    }
    None
}

#[cfg(windows)]
async fn resolve_localdb_pipe(instance: &str) -> anyhow::Result<String> {
    if let Some(pipe) = query_localdb_pipe(instance).await? {
        return Ok(pipe);
    }
    // Instance might be stopped — try starting it once.
    let _ = tokio::process::Command::new("sqllocaldb")
        .args(["start", instance])
        .output()
        .await;
    query_localdb_pipe(instance)
        .await?
        .ok_or_else(|| anyhow!("LocalDB instance '{instance}' is not running or `sqllocaldb` did not return a pipe name."))
}

#[cfg(windows)]
async fn query_localdb_pipe(instance: &str) -> anyhow::Result<Option<String>> {
    let output = tokio::process::Command::new("sqllocaldb")
        .args(["info", instance])
        .output()
        .await
        .context("Failed to run `sqllocaldb`. Is SQL Server Express LocalDB installed?")?;
    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr);
        return Err(anyhow!(
            "sqllocaldb info '{instance}' failed: {}",
            stderr.trim()
        ));
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    for line in stdout.lines() {
        let lower = line.to_ascii_lowercase();
        if let Some(pos) = lower.find("np:") {
            let raw = line[pos + 3..].trim();
            if !raw.is_empty() {
                return Ok(Some(raw.to_string()));
            }
        }
    }
    Ok(None)
}

fn is_local_target(server: &str) -> bool {
    let s = server
        .trim()
        .strip_prefix("tcp:")
        .or_else(|| server.trim().strip_prefix("TCP:"))
        .unwrap_or(server.trim());
    let host = s
        .split(['\\', ',', ':'])
        .next()
        .unwrap_or("")
        .trim()
        .to_ascii_lowercase();
    matches!(
        host.as_str(),
        "" | "." | "(local)" | "(localdb)" | "localhost" | "127.0.0.1" | "::1"
    )
}

fn quote_bracket_identifier(name: &str) -> String {
    format!("[{}]", name.replace(']', "]]"))
}

#[cfg(test)]
mod tests {
    use larik_data::{ConnectionProfile, DatabaseProvider};

    use super::SqlServerProvider;

    #[test]
    fn transport_warning_only_flags_non_local_unverified_or_unencrypted_targets() {
        let provider = SqlServerProvider;

        // Localhost with trustServerCertificate = true should not warn.
        let mut local = ConnectionProfile::new("mssql");
        local.set("server", "localhost");
        local.set("encrypt", "true");
        local.set("trustServerCertificate", "true");
        assert!(provider.transport_warning(&local).is_none());

        // Remote server with encrypt = true and verified certificate should not warn.
        let mut remote_secure = ConnectionProfile::new("mssql");
        remote_secure.set("server", "192.168.0.251");
        remote_secure.set("encrypt", "true");
        remote_secure.set("trustServerCertificate", "false");
        assert!(provider.transport_warning(&remote_secure).is_none());

        // Remote server trusting any server certificate should warn about impersonation.
        let mut remote_trust = ConnectionProfile::new("mssql");
        remote_trust.set("server", "192.168.0.251");
        remote_trust.set("encrypt", "true");
        remote_trust.set("trustServerCertificate", "true");
        let warning = provider
            .transport_warning(&remote_trust)
            .expect("expected warning for remote trustServerCertificate");
        assert!(warning.contains("trusts any server certificate"));

        // Remote server with encrypt = false should warn about unencrypted traffic.
        let mut remote_plain = ConnectionProfile::new("mssql");
        remote_plain.set("server", "db.example.com");
        remote_plain.set("encrypt", "false");
        remote_plain.set("trustServerCertificate", "false");
        let warning = provider
            .transport_warning(&remote_plain)
            .expect("expected warning for remote unencrypted connection");
        assert!(warning.contains("not encrypted"));
    }
}
