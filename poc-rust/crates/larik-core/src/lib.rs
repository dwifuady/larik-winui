pub mod run_log;
pub mod secrets;
pub mod session;
pub mod snippets;
pub mod store;

use chrono::{DateTime, Duration, Local, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use larik_data::{ConnectionProfile, ProviderCatalog};

pub const SPACE_COLORS: &[(&str, &str)] = &[
    ("Sky Blue", "#82B7F8"),
    ("Indigo", "#6366F1"),
    ("Emerald", "#10B981"),
    ("Amber", "#F59E0B"),
    ("Coral", "#F27B5E"),
    ("Rose", "#F43F5E"),
    ("Sky", "#0EA5E9"),
    ("Violet", "#A855F7"),
];

pub const DEFAULT_AUTO_ARCHIVE_DAYS: u32 = 30;
pub const MIN_AUTO_ARCHIVE_DAYS: u32 = 1;
pub const MAX_AUTO_ARCHIVE_DAYS: u32 = 3650;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ThemePreference {
    System,
    Dark,
    Light,
}

impl Default for ThemePreference {
    fn default() -> Self {
        Self::Dark
    }
}

pub fn now_rfc3339() -> String {
    Utc::now().to_rfc3339()
}

fn default_auto_archive_days() -> u32 {
    DEFAULT_AUTO_ARCHIVE_DAYS
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct TabGroupState {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub color: Option<String>,
    #[serde(default)]
    pub is_pinned: bool,
    #[serde(default = "default_true")]
    pub is_expanded: bool,
    #[serde(default)]
    pub database: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TabState {
    pub id: String,
    pub title: String,
    pub database: Option<String>,
    pub content: String,
    #[serde(default)]
    pub is_pinned: bool,
    #[serde(default)]
    pub group_id: Option<String>,
    #[serde(default = "now_rfc3339")]
    pub last_accessed_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ArchivedTabState {
    pub id: String,
    pub space_id: String,
    #[serde(default)]
    pub space_name: Option<String>,
    #[serde(default)]
    pub color: Option<String>,
    pub title: String,
    pub database: Option<String>,
    pub content: String,
    #[serde(default)]
    pub is_pinned: bool,
    #[serde(default)]
    pub group_name: Option<String>,
    pub archived_at: String,
}

impl ArchivedTabState {
    /// First non-empty trimmed line of SQL, truncated to 140 chars (matching WinUI ArchivedTabViewModel.Preview).
    pub fn preview(&self) -> String {
        let Some(line) = self
            .content
            .lines()
            .map(str::trim)
            .find(|line| !line.is_empty())
        else {
            return "Empty query".to_string();
        };
        if line.chars().count() > 140 {
            let mut s: String = line.chars().take(140).collect();
            s.push('…');
            s
        } else {
            line.to_string()
        }
    }

    /// Relative age label matching WinUI ArchivedTabViewModel.AgeLabel.
    pub fn age_label(&self, now: DateTime<Utc>) -> String {
        let Ok(parsed) = DateTime::parse_from_rfc3339(&self.archived_at) else {
            // Backward compatibility with legacy "HH:MM" strings saved before RFC3339 timestamps.
            return self.archived_at.clone();
        };
        let archived_utc = parsed.with_timezone(&Utc);
        let age = now.signed_duration_since(archived_utc);
        if age < Duration::minutes(1) {
            "just now".to_string()
        } else if age < Duration::hours(1) {
            format!("{} min ago", age.num_minutes().max(1))
        } else if age < Duration::days(1) {
            format!("{} h ago", age.num_hours().max(1))
        } else if age < Duration::days(30) {
            format!("{} d ago", age.num_days().max(1))
        } else {
            archived_utc
                .with_timezone(&Local)
                .format("%b %-d, %Y")
                .to_string()
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SpaceState {
    pub id: String,
    pub name: String,
    pub color: String,
    #[serde(default)]
    pub icon: Option<String>,
    pub profile: ConnectionProfile,
    pub active_database: Option<String>,
    pub active_tab_id: Option<String>,
    pub tabs: Vec<TabState>,
    #[serde(default)]
    pub groups: Vec<TabGroupState>,
    #[serde(default)]
    pub is_hidden: bool,
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioState {
    pub spaces: Vec<SpaceState>,
    pub active_space_id: Option<String>,
    pub archived_tabs: Vec<ArchivedTabState>,
    pub query_counter: u32,
    pub theme: ThemePreference,
    pub schema_pane_open: bool,
    #[serde(default = "default_true")]
    pub rail_open: bool,
    #[serde(default = "default_true")]
    pub auto_archive_enabled: bool,
    #[serde(default = "default_auto_archive_days")]
    pub auto_archive_days: u32,
}

impl StudioState {
    pub fn seed_default(catalog: &ProviderCatalog) -> Self {
        let demo_space_id = Uuid::new_v4().to_string();
        let demo_tab1_id = Uuid::new_v4().to_string();
        let demo_tab2_id = Uuid::new_v4().to_string();
        let now = now_rfc3339();

        let demo_profile = catalog
            .find("demo")
            .map(|p| p.default_profile())
            .unwrap_or_else(|| ConnectionProfile::new("demo"));

        let demo_space = SpaceState {
            id: demo_space_id.clone(),
            name: "Demo Studio".into(),
            color: "#6366F1".into(),
            icon: None,
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
                    is_pinned: false,
                    group_id: None,
                    last_accessed_at: now.clone(),
                },
                TabState {
                    id: demo_tab2_id,
                    title: "Catalog (1000 rows)".into(),
                    database: Some("larik_prod".into()),
                    content: "-- Virtualized grid test (1,000 rows)\n\
SELECT TOP 1000 p.id, p.sku, p.name, p.price, p.stock\n\
FROM dbo.products p;\n"
                        .into(),
                    is_pinned: false,
                    group_id: None,
                    last_accessed_at: now.clone(),
                },
            ],
            groups: Vec::new(),
            is_hidden: false,
        };

        let mut spaces = vec![demo_space];

        if let Some(mssql) = catalog.find("mssql") {
            let mssql_space_id = Uuid::new_v4().to_string();
            let mssql_tab_id = Uuid::new_v4().to_string();
            spaces.push(SpaceState {
                id: mssql_space_id,
                name: "Local SQL Server".into(),
                color: "#10B981".into(),
                icon: None,
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
                    is_pinned: false,
                    group_id: None,
                    last_accessed_at: now,
                }],
                groups: Vec::new(),
                is_hidden: false,
            });
        }

        Self {
            spaces,
            active_space_id: Some(demo_space_id),
            archived_tabs: Vec::new(),
            query_counter: 3,
            theme: ThemePreference::Dark,
            schema_pane_open: false,
            rail_open: true,
            auto_archive_enabled: true,
            auto_archive_days: DEFAULT_AUTO_ARCHIVE_DAYS,
        }
    }

    pub fn effective_auto_archive_days(&self) -> u32 {
        self.auto_archive_days
            .clamp(MIN_AUTO_ARCHIVE_DAYS, MAX_AUTO_ARCHIVE_DAYS)
    }

    /// Runs the inactivity auto-archive sweep across all workspaces, matching WinUI
    /// `StudioViewModel.RunAutoArchive`:
    /// - Skips pinned tabs, tabs inside pinned folders, and the currently active tab of the active workspace
    /// - Keeps at least one tab per workspace
    /// - Returns IDs of archived tabs so the caller can close their runtime sessions.
    pub fn run_auto_archive(&mut self, now: DateTime<Utc>) -> Vec<String> {
        if !self.auto_archive_enabled {
            return Vec::new();
        }
        let days = i64::from(self.effective_auto_archive_days());
        let cutoff = now - Duration::days(days);
        let active_space_id = self.active_space_id.clone();
        let active_tab_id = self
            .active_space()
            .and_then(|s| s.active_tab())
            .map(|t| t.id.clone());

        let mut archived_ids = Vec::new();
        let now_str = now.to_rfc3339();

        for space in &mut self.spaces {
            let is_active_space = active_space_id.as_deref() == Some(space.id.as_str());
            let mut idx = 0;
            while idx < space.tabs.len() {
                if space.tabs.len() <= 1 {
                    break;
                }
                let tab = &space.tabs[idx];
                let is_active_tab =
                    is_active_space && active_tab_id.as_deref() == Some(tab.id.as_str());
                let is_protected = space.is_tab_protected(tab);
                let last_accessed = DateTime::parse_from_rfc3339(&tab.last_accessed_at)
                    .map(|dt| dt.with_timezone(&Utc))
                    .unwrap_or(now);

                if is_protected || is_active_tab || last_accessed > cutoff {
                    idx += 1;
                    continue;
                }

                let removed = space.tabs.remove(idx);
                let group_name = removed
                    .group_id
                    .as_deref()
                    .and_then(|gid| space.tab_group(gid))
                    .map(|g| g.name.clone());
                if space.active_tab_id.as_deref() == Some(removed.id.as_str()) {
                    space.active_tab_id = space
                        .tabs
                        .get(idx.saturating_sub(1))
                        .or_else(|| space.tabs.first())
                        .map(|t| t.id.clone());
                }
                archived_ids.push(removed.id.clone());
                self.archived_tabs.insert(
                    0,
                    ArchivedTabState {
                        id: removed.id,
                        space_id: space.id.clone(),
                        space_name: Some(space.name.clone()),
                        color: Some(space.color.clone()),
                        title: removed.title,
                        database: removed.database,
                        content: removed.content,
                        is_pinned: removed.is_pinned,
                        group_name,
                        archived_at: now_str.clone(),
                    },
                );
            }
            space.prune_empty_groups();
        }

        archived_ids
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

    /// Effective database for the currently active tab (falling back to workspace default).
    pub fn active_tab_database(&self) -> Option<String> {
        self.active_tab()
            .and_then(|t| t.database.clone())
            .or_else(|| self.active_database.clone())
    }

    pub fn tab_group(&self, group_id: &str) -> Option<&TabGroupState> {
        self.groups.iter().find(|g| g.id == group_id)
    }

    pub fn tab_group_mut(&mut self, group_id: &str) -> Option<&mut TabGroupState> {
        self.groups.iter_mut().find(|g| g.id == group_id)
    }

    /// True if the tab is pinned directly or belongs to a pinned folder.
    pub fn is_tab_protected(&self, tab: &TabState) -> bool {
        tab.is_pinned
            || tab
                .group_id
                .as_deref()
                .and_then(|gid| self.tab_group(gid))
                .is_some_and(|g| g.is_pinned)
    }

    /// Removes dangling `group_id` references and drops groups that no longer contain any tabs.
    pub fn prune_empty_groups(&mut self) {
        let valid_group_ids: std::collections::HashSet<&str> =
            self.groups.iter().map(|g| g.id.as_str()).collect();
        for tab in &mut self.tabs {
            if let Some(ref gid) = tab.group_id {
                if !valid_group_ids.contains(gid.as_str()) {
                    tab.group_id = None;
                }
            }
        }
        let populated: std::collections::HashSet<String> = self
            .tabs
            .iter()
            .filter_map(|t| t.group_id.clone())
            .collect();
        self.groups.retain(|g| populated.contains(&g.id));
    }

    /// Canonicalizes group and tab ordering to match WinUI `StudioViewModel.RebuildTabOrder`:
    /// 1. Pinned groups (folders) + their member tabs
    /// 2. Loose pinned tabs
    /// 3. Unpinned groups + their member tabs
    /// 4. Loose unpinned tabs
    pub fn rebuild_tab_order(&mut self) {
        self.prune_empty_groups();

        // Pinning lives on the group when a tab belongs to a group.
        for tab in &mut self.tabs {
            if tab.group_id.is_some() {
                tab.is_pinned = false;
            }
        }

        // Keep pinned groups first, then unpinned groups, preserving relative order.
        let mut pinned_groups = Vec::new();
        let mut unpinned_groups = Vec::new();
        for group in self.groups.drain(..) {
            if group.is_pinned {
                pinned_groups.push(group);
            } else {
                unpinned_groups.push(group);
            }
        }
        let pinned_ids: Vec<String> = pinned_groups.iter().map(|g| g.id.clone()).collect();
        let unpinned_ids: Vec<String> = unpinned_groups.iter().map(|g| g.id.clone()).collect();
        self.groups.extend(pinned_groups);
        self.groups.extend(unpinned_groups);

        let old_tabs = std::mem::take(&mut self.tabs);
        let mut by_group: std::collections::HashMap<String, Vec<TabState>> =
            std::collections::HashMap::new();
        let mut loose_pinned = Vec::new();
        let mut loose_unpinned = Vec::new();

        for tab in old_tabs {
            if let Some(ref gid) = tab.group_id {
                by_group.entry(gid.clone()).or_default().push(tab);
            } else if tab.is_pinned {
                loose_pinned.push(tab);
            } else {
                loose_unpinned.push(tab);
            }
        }

        for gid in pinned_ids {
            if let Some(list) = by_group.remove(&gid) {
                self.tabs.extend(list);
            }
        }
        self.tabs.extend(loose_pinned);
        for gid in unpinned_ids {
            if let Some(list) = by_group.remove(&gid) {
                self.tabs.extend(list);
            }
        }
        self.tabs.extend(loose_unpinned);
    }

    /// Inserts a newly created tab using WinUI `StudioViewModel.CreateTab` rules:
    /// - If a group is bound to the tab's database (case-insensitive), auto-join and expand it.
    /// - Otherwise insert at the top of the loose unpinned band.
    pub fn insert_new_tab(&mut self, mut tab: TabState) {
        if let Some(ref db) = tab.database {
            if let Some(group) = self.groups.iter_mut().find(|g| {
                g.database
                    .as_deref()
                    .is_some_and(|gdb| gdb.eq_ignore_ascii_case(db))
            }) {
                tab.group_id = Some(group.id.clone());
                tab.is_pinned = false;
                group.is_expanded = true;
                self.tabs.push(tab);
                self.rebuild_tab_order();
                return;
            }
        }
        tab.group_id = None;
        tab.is_pinned = false;
        let insert_idx = self
            .tabs
            .iter()
            .position(|t| t.group_id.is_none() && !t.is_pinned)
            .unwrap_or(self.tabs.len());
        self.tabs.insert(insert_idx, tab);
        self.rebuild_tab_order();
    }

    /// Pins or unpins a tab. If the tab is inside a group, toggles the group's pin state
    /// (converting between an unpinned group and a pinned folder) to match WinUI.
    pub fn toggle_pin_tab(&mut self, tab_id: &str) -> bool {
        let Some(pos) = self.tabs.iter().position(|t| t.id == tab_id) else {
            return false;
        };
        if let Some(gid) = self.tabs[pos].group_id.clone() {
            return self.toggle_group_pin(&gid);
        }
        self.tabs[pos].is_pinned = !self.tabs[pos].is_pinned;
        self.tabs[pos].last_accessed_at = now_rfc3339();
        self.rebuild_tab_order();
        true
    }

    pub fn toggle_group_pin(&mut self, group_id: &str) -> bool {
        let Some(group) = self.tab_group_mut(group_id) else {
            return false;
        };
        group.is_pinned = !group.is_pinned;
        self.rebuild_tab_order();
        true
    }

    pub fn toggle_group_expanded(&mut self, group_id: &str) -> bool {
        let Some(group) = self.tab_group_mut(group_id) else {
            return false;
        };
        group.is_expanded = !group.is_expanded;
        true
    }

    pub fn rename_group(&mut self, group_id: &str, new_name: &str) -> bool {
        let trimmed = new_name.trim();
        if trimmed.is_empty() {
            return false;
        }
        let Some(group) = self.tab_group_mut(group_id) else {
            return false;
        };
        if group.name == trimmed {
            return false;
        }
        group.name = trimmed.to_string();
        true
    }

    /// Creates a new group/folder containing the given tab (inheriting its pin state and database).
    pub fn new_group_from_tab(&mut self, tab_id: &str) -> Option<String> {
        let pos = self.tabs.iter().position(|t| t.id == tab_id)?;
        let was_pinned = self.tabs[pos].is_pinned
            || self.tabs[pos]
                .group_id
                .as_deref()
                .and_then(|gid| self.tab_group(gid))
                .is_some_and(|g| g.is_pinned);
        let db = self.tabs[pos].database.clone();
        let name = db
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string)
            .unwrap_or_else(|| "New group".to_string());
        let group_id = Uuid::new_v4().to_string();

        self.groups.push(TabGroupState {
            id: group_id.clone(),
            name,
            color: None,
            is_pinned: was_pinned,
            is_expanded: true,
            database: db,
        });
        self.tabs[pos].group_id = Some(group_id.clone());
        self.tabs[pos].is_pinned = false;
        self.rebuild_tab_order();
        Some(group_id)
    }

    /// Moves a tab into `target_group_id`, or out to loose tabs (`None`) with optional pin state.
    pub fn move_tab_to_group(
        &mut self,
        tab_id: &str,
        target_group_id: Option<&str>,
        loose_pinned: Option<bool>,
    ) -> bool {
        let Some(pos) = self.tabs.iter().position(|t| t.id == tab_id) else {
            return false;
        };
        match target_group_id {
            Some(gid) => {
                if !self.groups.iter().any(|g| g.id == gid) {
                    return false;
                }
                self.tabs[pos].group_id = Some(gid.to_string());
                self.tabs[pos].is_pinned = false;
                if let Some(group) = self.tab_group_mut(gid) {
                    group.is_expanded = true;
                }
            }
            None => {
                self.tabs[pos].group_id = None;
                if let Some(pinned) = loose_pinned {
                    self.tabs[pos].is_pinned = pinned;
                }
            }
        }
        self.rebuild_tab_order();
        true
    }

    /// Dissolves a group/folder, keeping its member tabs as loose tabs in the same band.
    pub fn ungroup(&mut self, group_id: &str) -> bool {
        let Some(pos) = self.groups.iter().position(|g| g.id == group_id) else {
            return false;
        };
        let was_pinned = self.groups[pos].is_pinned;
        self.groups.remove(pos);
        for tab in &mut self.tabs {
            if tab.group_id.as_deref() == Some(group_id) {
                tab.group_id = None;
                tab.is_pinned = was_pinned;
            }
        }
        self.rebuild_tab_order();
        true
    }

    /// Drag-and-drop: drops `dragged_id` onto a group/folder header, placing it at the top of that group.
    pub fn drop_tab_on_group(&mut self, dragged_id: &str, group_id: &str) -> bool {
        if !self.groups.iter().any(|g| g.id == group_id) {
            return false;
        }
        let Some(from) = self.tabs.iter().position(|t| t.id == dragged_id) else {
            return false;
        };
        let mut tab = self.tabs.remove(from);
        tab.group_id = Some(group_id.to_string());
        tab.is_pinned = false;
        self.tabs.insert(0, tab);
        if let Some(group) = self.tab_group_mut(group_id) {
            group.is_expanded = true;
        }
        self.rebuild_tab_order();
        true
    }

    /// Drag-and-drop: drops `dragged_id` onto `target_id`.
    /// Adopts the target's group/folder (or pulls out to loose tabs with the target's pin state)
    /// and positions `dragged_id` at `target_id`'s slot.
    pub fn drop_tab_on_tab(&mut self, dragged_id: &str, target_id: &str) -> bool {
        if dragged_id == target_id {
            return false;
        }
        let Some(target) = self.tabs.iter().find(|t| t.id == target_id) else {
            return false;
        };
        let target_group_id = target.group_id.clone();
        let target_pinned = target.is_pinned;

        let Some(from) = self.tabs.iter().position(|t| t.id == dragged_id) else {
            return false;
        };
        let mut tab = self.tabs.remove(from);
        tab.group_id = target_group_id.clone();
        tab.is_pinned = if target_group_id.is_some() {
            false
        } else {
            target_pinned
        };

        let to = self
            .tabs
            .iter()
            .position(|t| t.id == target_id)
            .unwrap_or(self.tabs.len());
        self.tabs.insert(to, tab);
        if let Some(ref gid) = target_group_id {
            if let Some(group) = self.tab_group_mut(gid) {
                group.is_expanded = true;
            }
        }
        self.rebuild_tab_order();
        true
    }

    /// Drag-and-drop: pulls `dragged_id` out of any group into loose pinned or loose unpinned tabs.
    pub fn drop_tab_to_loose(&mut self, dragged_id: &str, pinned: bool, at_top: bool) -> bool {
        let Some(from) = self.tabs.iter().position(|t| t.id == dragged_id) else {
            return false;
        };
        let mut tab = self.tabs.remove(from);
        tab.group_id = None;
        tab.is_pinned = pinned;
        if at_top {
            self.tabs.insert(0, tab);
        } else {
            self.tabs.push(tab);
        }
        self.rebuild_tab_order();
        true
    }

    /// True when at least two loose unpinned tabs share a database not already owned by a group.
    pub fn has_ungrouped_database_cluster(&self) -> bool {
        let owned: std::collections::HashSet<String> = self
            .groups
            .iter()
            .filter_map(|g| g.database.as_deref())
            .map(|db| db.to_ascii_lowercase())
            .collect();
        let mut counts: std::collections::HashMap<String, usize> = std::collections::HashMap::new();
        for tab in &self.tabs {
            if tab.group_id.is_some() || tab.is_pinned {
                continue;
            }
            if let Some(db) = tab.database.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
                let key = db.to_ascii_lowercase();
                if owned.contains(&key) {
                    continue;
                }
                let entry = counts.entry(key).or_insert(0);
                *entry += 1;
                if *entry >= 2 {
                    return true;
                }
            }
        }
        false
    }

    /// Groups loose unpinned tabs that share a database (>= 2 tabs per database, not already
    /// owned by an existing group). Returns newly created group IDs for Undo support.
    pub fn group_tabs_by_database(&mut self) -> Vec<String> {
        let owned: std::collections::HashSet<String> = self
            .groups
            .iter()
            .filter_map(|g| g.database.as_deref())
            .map(|db| db.to_ascii_lowercase())
            .collect();

        // Preserve first-seen database order and display casing.
        let mut order: Vec<String> = Vec::new();
        let mut display_names: std::collections::HashMap<String, String> =
            std::collections::HashMap::new();
        let mut tab_ids_by_db: std::collections::HashMap<String, Vec<String>> =
            std::collections::HashMap::new();

        for tab in &self.tabs {
            if tab.group_id.is_some() || tab.is_pinned {
                continue;
            }
            if let Some(db) = tab.database.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
                let key = db.to_ascii_lowercase();
                if owned.contains(&key) {
                    continue;
                }
                if !display_names.contains_key(&key) {
                    order.push(key.clone());
                    display_names.insert(key.clone(), db.to_string());
                }
                tab_ids_by_db.entry(key).or_default().push(tab.id.clone());
            }
        }

        let mut created_ids = Vec::new();
        for key in order {
            let Some(ids) = tab_ids_by_db.get(&key) else {
                continue;
            };
            if ids.len() < 2 {
                continue;
            }
            let db_name = display_names.get(&key).cloned().unwrap_or(key);
            let group_id = Uuid::new_v4().to_string();
            self.groups.push(TabGroupState {
                id: group_id.clone(),
                name: db_name.clone(),
                color: None,
                is_pinned: false,
                is_expanded: true,
                database: Some(db_name),
            });
            for tab in &mut self.tabs {
                if ids.iter().any(|id| id == &tab.id) {
                    tab.group_id = Some(group_id.clone());
                    tab.is_pinned = false;
                }
            }
            created_ids.push(group_id);
        }

        if !created_ids.is_empty() {
            self.rebuild_tab_order();
        }
        created_ids
    }

    pub fn undo_grouping(&mut self, group_ids: &[String]) -> bool {
        let mut changed = false;
        for gid in group_ids {
            if self.ungroup(gid) {
                changed = true;
            }
        }
        changed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_tab(id: &str, title: &str, db: Option<&str>) -> TabState {
        TabState {
            id: id.to_string(),
            title: title.to_string(),
            database: db.map(str::to_string),
            content: "SELECT 1;".to_string(),
            is_pinned: false,
            group_id: None,
            last_accessed_at: now_rfc3339(),
        }
    }

    fn make_space(tabs: Vec<TabState>) -> SpaceState {
        let first_id = tabs.first().map(|t| t.id.clone());
        SpaceState {
            id: "space-1".to_string(),
            name: "Test".to_string(),
            color: "#6366F1".to_string(),
            icon: None,
            profile: ConnectionProfile::new("demo"),
            active_database: Some("db_a".to_string()),
            active_tab_id: first_id,
            tabs,
            groups: Vec::new(),
            is_hidden: false,
        }
    }

    #[test]
    fn canonical_ordering_puts_pinned_folders_then_loose_pinned_then_groups_then_loose() {
        let mut space = make_space(vec![
            make_tab("t1", "Loose 1", Some("db_a")),
            make_tab("t2", "Pinned Loose", Some("db_a")),
            make_tab("t3", "Unpinned Group Tab", Some("db_b")),
            make_tab("t4", "Pinned Folder Tab", Some("db_c")),
        ]);

        assert!(space.toggle_pin_tab("t2"));
        let g_unpinned = space.new_group_from_tab("t3").unwrap();
        let g_pinned = space.new_group_from_tab("t4").unwrap();
        assert!(space.toggle_group_pin(&g_pinned));

        let ids: Vec<&str> = space.tabs.iter().map(|t| t.id.as_str()).collect();
        assert_eq!(ids, vec!["t4", "t2", "t3", "t1"]);
        assert_eq!(space.groups[0].id, g_pinned);
        assert!(space.groups[0].is_pinned);
        assert_eq!(space.groups[1].id, g_unpinned);
        assert!(!space.groups[1].is_pinned);
    }

    #[test]
    fn drag_tab_into_folder_reorder_inside_and_pull_out() {
        let mut space = make_space(vec![
            make_tab("t1", "One", Some("db_a")),
            make_tab("t2", "Two", Some("db_a")),
            make_tab("t3", "Three", Some("db_b")),
        ]);
        let folder = space.new_group_from_tab("t1").unwrap();
        assert!(space.toggle_group_pin(&folder));

        // Drop t2 onto folder header -> joins folder at top
        assert!(space.drop_tab_on_group("t2", &folder));
        assert_eq!(space.tabs[0].id, "t2");
        assert_eq!(space.tabs[0].group_id.as_deref(), Some(folder.as_str()));
        assert_eq!(space.tabs[1].id, "t1");
        assert!(space.is_tab_protected(&space.tabs[0]));

        // Drag t1 onto t2 inside the same folder -> reorders inside folder
        assert!(space.drop_tab_on_tab("t1", "t2"));
        assert_eq!(space.tabs[0].id, "t1");
        assert_eq!(space.tabs[1].id, "t2");

        // Drag t2 onto loose unpinned tab t3 -> pulls out of folder to loose unpinned
        assert!(space.drop_tab_on_tab("t2", "t3"));
        let t2 = space.tabs.iter().find(|t| t.id == "t2").unwrap();
        assert_eq!(t2.group_id, None);
        assert!(!t2.is_pinned);
        assert!(!space.is_tab_protected(t2));

        // Pull last remaining tab t1 out to loose unpinned -> empty folder auto-pruned
        assert!(space.drop_tab_to_loose("t1", false, false));
        assert!(space.groups.is_empty());
    }

    #[test]
    fn group_by_database_undo_and_new_tab_auto_join() {
        let mut space = make_space(vec![
            make_tab("t1", "A1", Some("alpha")),
            make_tab("t2", "A2", Some("ALPHA")),
            make_tab("t3", "B1", Some("beta")),
        ]);
        assert!(space.has_ungrouped_database_cluster());

        let created = space.group_tabs_by_database();
        assert_eq!(created.len(), 1);
        assert!(!space.has_ungrouped_database_cluster());

        // New tab for "alpha" auto-joins the existing alpha group
        space.insert_new_tab(make_tab("t4", "A3", Some("alpha")));
        let t4 = space.tabs.iter().find(|t| t.id == "t4").unwrap();
        assert_eq!(t4.group_id.as_deref(), Some(created[0].as_str()));

        // Undo dissolves the created group back to loose unpinned tabs
        assert!(space.undo_grouping(&created));
        assert!(space.groups.is_empty());
        assert!(space.tabs.iter().all(|t| t.group_id.is_none() && !t.is_pinned));
    }

    #[test]
    fn auto_archive_skips_tabs_inside_pinned_folder_and_prunes_unpinned_group() {
        let now = Utc::now();
        let stale = (now - Duration::days(60)).to_rfc3339();
        let mut active = make_tab("active", "Active", Some("db_a"));
        active.last_accessed_at = now.to_rfc3339();
        let mut in_pinned_folder = make_tab("pf", "Pinned Folder Query", Some("db_a"));
        in_pinned_folder.last_accessed_at = stale.clone();
        let mut in_unpinned_group = make_tab("ug", "Unpinned Group Query", Some("db_b"));
        in_unpinned_group.last_accessed_at = stale;

        let mut space = make_space(vec![active, in_pinned_folder, in_unpinned_group]);
        let pf_group = space.new_group_from_tab("pf").unwrap();
        assert!(space.toggle_group_pin(&pf_group));
        let ug_group = space.new_group_from_tab("ug").unwrap();
        space.active_tab_id = Some("active".to_string());

        let mut studio = StudioState {
            spaces: vec![space],
            active_space_id: Some("space-1".to_string()),
            archived_tabs: Vec::new(),
            query_counter: 3,
            theme: ThemePreference::Dark,
            schema_pane_open: false,
            rail_open: true,
            auto_archive_enabled: true,
            auto_archive_days: 30,
        };

        let archived = studio.run_auto_archive(now);
        assert_eq!(archived, vec!["ug".to_string()]);
        assert_eq!(studio.archived_tabs.len(), 1);
        assert_eq!(studio.archived_tabs[0].group_name.as_deref(), Some("db_b"));
        assert!(studio.spaces[0].tabs.iter().any(|t| t.id == "pf"));
        assert!(studio.spaces[0].tab_group(&pf_group).is_some());
        assert!(studio.spaces[0].tab_group(&ug_group).is_none());
    }

    #[test]
    fn backward_compatible_json_without_groups_deserializes_cleanly() {
        let json = r##"{
            "spaces": [{
                "id": "s1",
                "name": "Legacy",
                "color": "#6366F1",
                "profile": { "provider_id": "demo", "values": {} },
                "active_database": "db1",
                "active_tab_id": "t1",
                "tabs": [{
                    "id": "t1",
                    "title": "Query 1",
                    "database": "db1",
                    "content": "SELECT 1",
                    "is_pinned": true
                }]
            }],
            "active_space_id": "s1",
            "archived_tabs": [{
                "id": "a1",
                "space_id": "s1",
                "title": "Old",
                "database": "db1",
                "content": "SELECT 2",
                "archived_at": "12:00"
            }],
            "query_counter": 2,
            "theme": "dark",
            "schema_pane_open": false
        }"##;
        let state: StudioState = serde_json::from_str(json).expect("legacy json should parse");
        assert!(state.spaces[0].groups.is_empty());
        assert_eq!(state.spaces[0].tabs[0].group_id, None);
        assert_eq!(state.archived_tabs[0].group_name, None);
    }
}
