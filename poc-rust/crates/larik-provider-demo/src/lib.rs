use std::sync::Arc;
use std::time::{Duration, Instant};

use async_trait::async_trait;
use larik_data::sql_language::SqlLanguage;
use larik_data::{
    ColumnSchema, ConnectionProfile, ConnectionState, DatabaseConnection, DatabaseInfo,
    DatabaseProvider, EditorLanguage, ForeignKeySchema, ProviderSetting, ProviderSettingKind,
    QueryColumn, QueryExecutionOptions, QueryResult, TableKind, TableSchema,
};

pub const DEMO_PROVIDER_ID: &str = "demo";

static DEMO_SETTINGS: &[ProviderSetting] = &[ProviderSetting {
    key: "databases",
    label: "Databases",
    kind: ProviderSettingKind::Text,
    required: false,
    default_value: Some("larik_prod,larik_reports"),
    placeholder: Some("comma,separated"),
    hint: Some(
        "Pretend databases exposed by this connection. Prefix with ! to mark as visible but inaccessible.",
    ),
    is_advanced: false,
}];

/// Offline in-memory provider matching Larik's DemoProvider.
pub struct DemoProvider;

impl DatabaseProvider for DemoProvider {
    fn id(&self) -> &'static str {
        DEMO_PROVIDER_ID
    }

    fn display_name(&self) -> &'static str {
        "Demo (in-memory)"
    }

    fn dialect(&self) -> &'static str {
        "mssql"
    }

    fn settings(&self) -> &'static [ProviderSetting] {
        DEMO_SETTINGS
    }

    fn editor_language(&self) -> Arc<dyn EditorLanguage> {
        Arc::new(SqlLanguage::tsql())
    }

    fn describe_endpoint(&self, _profile: &ConnectionProfile) -> Option<String> {
        Some("Demo · in-memory".to_string())
    }

    fn describe_identity(&self, _profile: &ConnectionProfile) -> Option<String> {
        Some("Demo session".to_string())
    }

    fn create_connection(&self, profile: ConnectionProfile) -> Box<dyn DatabaseConnection> {
        Box::new(DemoConnection {
            profile,
            state: ConnectionState::Disconnected,
        })
    }
}

struct DemoConnection {
    profile: ConnectionProfile,
    state: ConnectionState,
}

fn demo_tables() -> Vec<TableSchema> {
    vec![
        TableSchema {
            schema: "dbo".into(),
            name: "customers".into(),
            kind: TableKind::Table,
            columns: vec![
                id_col("id", "int"),
                col("full_name", "nvarchar(120)", false),
                col("email", "nvarchar(200)", true),
                col("country", "char(2)", false),
                col("created_at", "date", false),
            ],
            primary_key: vec!["id".into()],
            foreign_keys: vec![],
        },
        TableSchema {
            schema: "dbo".into(),
            name: "orders".into(),
            kind: TableKind::Table,
            columns: vec![
                id_col("id", "int"),
                col("customer_id", "int", false),
                col("total_amount", "decimal(12,2)", false),
                col("status", "varchar(32)", false),
                col("ordered_at", "datetime2", false),
            ],
            primary_key: vec!["id".into()],
            foreign_keys: vec![ForeignKeySchema {
                constraint: "FK_orders_customers".into(),
                column: "customer_id".into(),
                referenced_schema: Some("dbo".into()),
                referenced_table: "customers".into(),
                referenced_column: "id".into(),
            }],
        },
        TableSchema {
            schema: "dbo".into(),
            name: "order_items".into(),
            kind: TableKind::Table,
            columns: vec![
                id_col("id", "int"),
                col("order_id", "int", false),
                col("product_id", "int", false),
                col("quantity", "int", false),
                col("unit_price", "decimal(10,2)", false),
            ],
            primary_key: vec!["id".into()],
            foreign_keys: vec![
                ForeignKeySchema {
                    constraint: "FK_order_items_orders".into(),
                    column: "order_id".into(),
                    referenced_schema: Some("dbo".into()),
                    referenced_table: "orders".into(),
                    referenced_column: "id".into(),
                },
                ForeignKeySchema {
                    constraint: "FK_order_items_products".into(),
                    column: "product_id".into(),
                    referenced_schema: Some("dbo".into()),
                    referenced_table: "products".into(),
                    referenced_column: "id".into(),
                },
            ],
        },
        TableSchema {
            schema: "dbo".into(),
            name: "products".into(),
            kind: TableKind::Table,
            columns: vec![
                id_col("id", "int"),
                col("sku", "varchar(16)", false),
                col("name", "nvarchar(160)", false),
                col("price", "decimal(10,2)", false),
                col("stock", "int", false),
            ],
            primary_key: vec!["id".into()],
            foreign_keys: vec![],
        },
    ]
}

fn id_col(name: &str, data_type: &str) -> ColumnSchema {
    ColumnSchema {
        name: name.into(),
        data_type: Some(data_type.into()),
        nullable: false,
        is_identity: true,
        is_computed: false,
    }
}

fn col(name: &str, data_type: &str, nullable: bool) -> ColumnSchema {
    ColumnSchema {
        name: name.into(),
        data_type: Some(data_type.into()),
        nullable,
        is_identity: false,
        is_computed: false,
    }
}

#[async_trait]
impl DatabaseConnection for DemoConnection {
    fn state(&self) -> ConnectionState {
        self.state
    }

    fn server_version(&self) -> Option<String> {
        (self.state == ConnectionState::Connected).then(|| "Demo 1.0".to_string())
    }

    async fn open(&mut self) -> anyhow::Result<()> {
        tokio::time::sleep(Duration::from_millis(60)).await;
        self.state = ConnectionState::Connected;
        Ok(())
    }

    async fn close(&mut self) -> anyhow::Result<()> {
        self.state = ConnectionState::Disconnected;
        Ok(())
    }

    async fn list_databases(&mut self) -> anyhow::Result<Vec<DatabaseInfo>> {
        tokio::time::sleep(Duration::from_millis(30)).await;
        let configured = self
            .profile
            .get_non_empty("databases")
            .unwrap_or("larik_prod,larik_reports");
        let list = configured
            .split(',')
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(|name| {
                if let Some(stripped) = name.strip_prefix('!') {
                    DatabaseInfo::inaccessible(stripped.trim())
                } else {
                    DatabaseInfo::accessible(name)
                }
            })
            .collect();
        Ok(list)
    }

    async fn get_schema(&mut self, _database: Option<&str>) -> anyhow::Result<Vec<TableSchema>> {
        tokio::time::sleep(Duration::from_millis(25)).await;
        Ok(demo_tables())
    }

    async fn execute_batch(
        &mut self,
        sql: &str,
        database: Option<&str>,
        options: &QueryExecutionOptions,
    ) -> anyhow::Result<Vec<QueryResult>> {
        tokio::time::sleep(Duration::from_millis(45)).await;
        let lang = SqlLanguage::tsql();
        let statements = lang.split_statements(sql);
        if statements.is_empty() {
            return Ok(vec![QueryResult::error(
                "Nothing to run — the editor is empty.",
                Duration::ZERO,
                database.map(str::to_string),
            )]);
        }

        let tables = demo_tables();
        let mut results = Vec::with_capacity(statements.len());
        for stmt in statements {
            let res = execute_demo_statement(&stmt.text, database, options, &tables);
            let is_err = res.is_error;
            results.push(res);
            if is_err {
                break;
            }
        }
        Ok(results)
    }

    async fn estimate_row_count(
        &mut self,
        _schema: Option<&str>,
        table: &str,
        _database: Option<&str>,
    ) -> Option<i64> {
        let tables = demo_tables();
        tables
            .iter()
            .any(|t| t.name.eq_ignore_ascii_case(table))
            .then_some(120)
    }

    async fn execute_dml(
        &mut self,
        sql: &str,
        _database: Option<&str>,
    ) -> anyhow::Result<u64> {
        tokio::time::sleep(Duration::from_millis(30)).await;
        // Count UPDATE / DELETE statements inside the batch as 1 affected row each in Demo mode.
        let upper = sql.to_ascii_uppercase();
        let count = upper
            .lines()
            .filter(|line| {
                let t = line.trim();
                t.starts_with("UPDATE ") || t.starts_with("DELETE ")
            })
            .count()
            .max(1);
        Ok(count as u64)
    }
}

fn execute_demo_statement(
    sql: &str,
    database: Option<&str>,
    options: &QueryExecutionOptions,
    tables: &[TableSchema],
) -> QueryResult {
    let started = Instant::now();
    let trimmed = sql.trim();
    let db = database.map(str::to_string);

    // Support PRINT '...' for testing the Messages tab.
    if trimmed.to_ascii_uppercase().starts_with("PRINT ") {
        let msg = trimmed[6..].trim().trim_matches('\'').to_string();
        return QueryResult {
            columns: vec![],
            rows: vec![],
            records_affected: None,
            message: "Command completed.".into(),
            is_error: false,
            elapsed: started.elapsed(),
            database: db,
            is_truncated: false,
            row_limit: None,
            messages: vec![msg],
            statement: Some(trimmed.to_string()),
        };
    }

    let table_name = find_target_table(trimmed);
    let table = table_name.as_deref().and_then(|name| {
        tables
            .iter()
            .find(|t| t.name.eq_ignore_ascii_case(name))
    });

    let Some(table) = table else {
        let err = match table_name {
            None => "No table in this statement — try SELECT * FROM dbo.customers;".to_string(),
            Some(name) => format!("Invalid object name '{name}'."),
        };
        return QueryResult::error(err, started.elapsed(), db);
    };

    // Allow `SELECT TOP 500 ...` to stress-test the virtualized DataTable easily!
    let requested_count = parse_top_count(trimmed).unwrap_or(120);
    let limit = options.effective_max_rows();
    let generate_count = requested_count.min(limit + 1);
    let all_rows = generate_rows(table, generate_count);
    let is_truncated = all_rows.len() > limit;
    let rows = if is_truncated {
        all_rows.into_iter().take(limit).collect()
    } else {
        all_rows
    };

    let row_count = rows.len();
    let columns = table
        .columns
        .iter()
        .map(|c| QueryColumn {
            name: c.name.clone(),
            data_type: c.data_type.clone(),
        })
        .collect();

    QueryResult {
        columns,
        rows,
        records_affected: Some(row_count as i64),
        message: format!("{row_count} rows"),
        is_error: false,
        elapsed: started.elapsed(),
        database: db,
        is_truncated,
        row_limit: Some(limit),
        messages: vec![format!(
            "({row_count} rows affected) · Demo engine ({})",
            table.name
        )],
        statement: Some(trimmed.to_string()),
    }
}

fn find_target_table(sql: &str) -> Option<String> {
    let tokens: Vec<&str> = sql
        .split(|c: char| c.is_whitespace() || matches!(c, '(' | ')' | ',' | ';'))
        .filter(|t| !t.is_empty())
        .collect();

    for pair in tokens.windows(2) {
        let kw = pair[0].to_ascii_uppercase();
        if matches!(kw.as_str(), "FROM" | "JOIN" | "INTO" | "UPDATE" | "TABLE") {
            let candidate = pair[1].trim_matches(|c| c == '[' || c == ']' || c == '"');
            let base = candidate
                .rsplit('.')
                .next()
                .unwrap_or(candidate)
                .trim_matches(|c| c == '[' || c == ']' || c == '"');
            if !base.is_empty() && !base.starts_with('@') {
                return Some(base.to_string());
            }
        }
    }
    None
}

fn parse_top_count(sql: &str) -> Option<usize> {
    let tokens: Vec<&str> = sql
        .split(|c: char| c.is_whitespace() || matches!(c, '(' | ')' | ',' | ';'))
        .filter(|t| !t.is_empty())
        .collect();
    for pair in tokens.windows(2) {
        if pair[0].eq_ignore_ascii_case("TOP") {
            if let Ok(n) = pair[1].parse::<usize>() {
                return Some(n.max(1));
            }
        }
    }
    None
}

fn generate_rows(table: &TableSchema, count: usize) -> Vec<Vec<Option<String>>> {
    const PEOPLE: &[&str] = &[
        "Ada Lovelace",
        "Grace Hopper",
        "Alan Turing",
        "Katherine Johnson",
        "Linus Torvalds",
        "Radia Perlman",
        "Barbara Liskov",
        "Ken Thompson",
    ];
    const COUNTRIES: &[&str] = &["ID", "SG", "US", "DE", "JP", "BR", "NL", "GB"];
    const STATUSES: &[&str] = &["paid", "pending", "fulfilled", "refunded"];
    const PRODUCTS: &[&str] = &[
        "Mechanical Keyboard",
        "27\" Studio Monitor",
        "USB-C Thunderbolt Dock",
        "Condenser Studio Mic",
        "Ergonomic Mouse",
        "Aluminum Laptop Stand",
        "2TB NVMe SSD",
        "4K Desk Webcam",
    ];
    const SKUS: &[&str] = &[
        "KB-01", "MN-27", "DK-04", "MC-11", "MS-02", "LS-08", "NV-2T", "WC-4K",
    ];

    let mut rows = Vec::with_capacity(count);
    for i in 0..count {
        let n = i + 1;
        let row = match table.name.as_str() {
            "customers" => {
                let person = PEOPLE[i % PEOPLE.len()];
                let first = person.split_whitespace().next().unwrap_or("user").to_ascii_lowercase();
                // Sprinkle occasional NULL emails to showcase NULL rendering.
                let email = if i % 7 == 4 {
                    None
                } else {
                    Some(format!("{first}.{n}@example.com"))
                };
                vec![
                    Some(n.to_string()),
                    Some(person.to_string()),
                    email,
                    Some(COUNTRIES[i % COUNTRIES.len()].to_string()),
                    Some(format!("2026-{:02}-{:02}", (i % 12) + 1, (i % 27) + 1)),
                ]
            }
            "orders" => vec![
                Some(n.to_string()),
                Some(((i % 8) + 1).to_string()),
                Some(format!("{:.2}", 49.90 + (i as f64) * 23.40)),
                Some(STATUSES[i % STATUSES.len()].to_string()),
                Some(format!(
                    "2026-{:02}-{:02} {:02}:{:02}:00",
                    (i % 12) + 1,
                    (i % 27) + 1,
                    (i * 3) % 24,
                    (i * 11) % 60
                )),
            ],
            "order_items" => vec![
                Some(n.to_string()),
                Some(((i % 24) + 1).to_string()),
                Some(((i % 8) + 1).to_string()),
                Some(((i % 5) + 1).to_string()),
                Some(format!("{:.2}", 19.90 + (i as f64) * 7.50)),
            ],
            "products" => vec![
                Some(n.to_string()),
                Some(SKUS[i % SKUS.len()].to_string()),
                Some(PRODUCTS[i % PRODUCTS.len()].to_string()),
                Some(format!("{:.2}", 89.00 + (i as f64) * 31.75)),
                Some(((i * 17) % 120).to_string()),
            ],
            _ => table
                .columns
                .iter()
                .enumerate()
                .map(|(c, _)| Some(format!("r{n}c{c}")))
                .collect(),
        };
        rows.push(row);
    }
    rows
}
