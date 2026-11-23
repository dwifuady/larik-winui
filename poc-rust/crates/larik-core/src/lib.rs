pub mod session;
pub mod store;

use serde::{Deserialize, Serialize};
use uuid::Uuid;

use larik_data::{ConnectionProfile, ProviderCatalog};

pub const SPACE_COLORS: &[(&str, &str)] = &[
    ("Indigo", "#6366F1"),
    ("Emerald", "#10B981"),
    ("Amber", "#F59E0B"),
    ("Rose", "#F43F5E"),
    ("Sky", "#0EA5E9"),
    ("Violet", "#A855F7"),
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ThemePreference {
    Dark,
    Light,
}

impl Default for ThemePreference {
    fn default() -> Self {
        Self::Dark
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TabState {
    pub id: String,
    pub title: String,
    pub database: Option<String>,
    pub content: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ArchivedTabState {
    pub id: String,
    pub space_id: String,
    pub title: String,
    pub database: Option<String>,
    pub content: String,
    pub archived_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SpaceState {
    pub id: String,
    pub name: String,
    pub color: String,
    pub profile: ConnectionProfile,
    pub active_database: Option<String>,
    pub active_tab_id: Option<String>,
    pub tabs: Vec<TabState>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioState {
    pub spaces: Vec<SpaceState>,
    pub active_space_id: Option<String>,
    pub archived_tabs: Vec<ArchivedTabState>,
    pub query_counter: u32,
    pub theme: ThemePreference,
    pub schema_pane_open: bool,
}

impl StudioState {
    pub fn seed_default(catalog: &ProviderCatalog) -> Self {
        let demo_space_id = Uuid::new_v4().to_string();
        let demo_tab1_id = Uuid::new_v4().to_string();
        let demo_tab2_id = Uuid::new_v4().to_string();

        let demo_profile = catalog
            .find("demo")
            .map(|p| p.default_profile())
            .unwrap_or_else(|| ConnectionProfile::new("demo"));

        let demo_space = SpaceState {
            id: demo_space_id.clone(),
            name: "Demo Studio".into(),
            color: "#6366F1".into(),
            profile: demo_profile,
            active_database: Some("larik_prod".into()),
            active_tab_id: Some(demo_tab1_id.clone()),
            tabs: vec![
                TabState {
                    id: demo_tab1_id,
                    title: "Customers & Orders".into(),
                    database: Some("larik_prod".into()),
                    content: "-- Press Ctrl+Enter to run statement under caret, or F5 to run all\n\
PRINT 'Fetching customer & order summary...';\n\n\
SELECT c.id, c.full_name, c.email, c.country, c.created_at\n\
FROM dbo.customers c\n\
ORDER BY c.id;\n\n\
SELECT TOP 50 o.id, o.customer_id, o.total_amount, o.status, o.ordered_at\n\
FROM dbo.orders o;\n"
                        .into(),
                },
                TabState {
                    id: demo_tab2_id,
                    title: "Catalog (1000 rows)".into(),
                    database: Some("larik_prod".into()),
                    content: "-- Virtualized grid test (1,000 rows)\n\
SELECT TOP 1000 p.id, p.sku, p.name, p.price, p.stock\n\
FROM dbo.products p;\n"
                        .into(),
                },
            ],
        };

        let mut spaces = vec![demo_space];

        if let Some(mssql) = catalog.find("mssql") {
            let mssql_space_id = Uuid::new_v4().to_string();
            let mssql_tab_id = Uuid::new_v4().to_string();
            spaces.push(SpaceState {
                id: mssql_space_id,
                name: "Local SQL Server".into(),
                color: "#10B981".into(),
                profile: mssql.default_profile(),
                active_database: None,
                active_tab_id: Some(mssql_tab_id.clone()),
                tabs: vec![TabState {
                    id: mssql_tab_id,
                    title: "Server Info".into(),
                    database: None,
                    content: "-- Connect this workspace from the rail, then press Ctrl+Enter or F5\n\
SELECT\n\
    @@SERVERNAME AS server_name,\n\
    DB_NAME() AS current_database,\n\
    SUSER_SNAME() AS login_name,\n\
    SYSDATETIME() AS server_time;\n\n\
SELECT TOP 20 name, database_id, state_desc, create_date\n\
FROM sys.databases\n\
ORDER BY name;\n"
                        .into(),
                }],
            });
        }

        Self {
            spaces,
            active_space_id: Some(demo_space_id),
            archived_tabs: Vec::new(),
            query_counter: 3,
            theme: ThemePreference::Dark,
            schema_pane_open: false,
        }
    }

    pub fn active_space(&self) -> Option<&SpaceState> {
        self.active_space_id
            .as_deref()
            .and_then(|id| self.spaces.iter().find(|s| s.id == id))
            .or_else(|| self.spaces.first())
    }

    pub fn active_space_mut(&mut self) -> Option<&mut SpaceState> {
        let target_id = self
            .active_space_id
            .clone()
            .or_else(|| self.spaces.first().map(|s| s.id.clone()))?;
        self.spaces.iter_mut().find(|s| s.id == target_id)
    }

    pub fn next_query_title(&mut self) -> String {
        self.query_counter += 1;
        format!("Query {}", self.query_counter)
    }
}

impl SpaceState {
    pub fn active_tab(&self) -> Option<&TabState> {
        self.active_tab_id
            .as_deref()
            .and_then(|id| self.tabs.iter().find(|t| t.id == id))
            .or_else(|| self.tabs.first())
    }

    pub fn active_tab_mut(&mut self) -> Option<&mut TabState> {
        let target_id = self
            .active_tab_id
            .clone()
            .or_else(|| self.tabs.first().map(|t| t.id.clone()))?;
        self.tabs.iter_mut().find(|t| t.id == target_id)
    }
}
