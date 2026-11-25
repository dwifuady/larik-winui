use std::cmp::Ordering;
use std::collections::BTreeSet;

use larik_data::SnippetDef;
use serde::{Deserialize, Serialize};

pub const KNOWN_SNIPPET_DIALECTS: &[(&str, &str)] = &[
    ("mssql", "SQL Server"),
    ("postgres", "PostgreSQL"),
    ("mysql", "MySQL"),
    ("sqlite", "SQLite"),
];

pub const SNIPPET_CATEGORIES: &[&str] = &[
    "Select",
    "Insert",
    "Update",
    "Delete",
    "DDL",
    "CTE",
    "Procedure",
    "Safety",
    "Utility",
];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SnippetRecord {
    pub id: String,
    pub trigger: String,
    pub name: String,
    pub content: String,
    pub description: Option<String>,
    pub category: Option<String>,
    pub dialects: Option<Vec<String>>,
    pub is_builtin: bool,
    pub is_enabled: bool,
}

impl SnippetRecord {
    pub fn to_def(&self) -> SnippetDef {
        SnippetDef {
            trigger: self.trigger.clone(),
            name: self.name.clone(),
            content: self.content.clone(),
            description: self.description.clone(),
            category: self.category.clone(),
            dialects: self.dialects.clone(),
        }
    }

    pub fn applies_to(&self, dialect: &str) -> bool {
        match &self.dialects {
            None => true,
            Some(dialects) if dialects.is_empty() => true,
            Some(dialects) => dialects.iter().any(|d| d.eq_ignore_ascii_case(dialect)),
        }
    }
}

/// Normalizes a dialect scope list: trims, lowercases, deduplicates, sorts alphabetically,
/// and converts an empty list into `None` (= applies to all SQL dialects).
pub fn normalize_dialects(dialects: Option<Vec<String>>) -> Option<Vec<String>> {
    let list = dialects?;
    let set: BTreeSet<String> = list
        .into_iter()
        .map(|d| d.trim().to_ascii_lowercase())
        .filter(|d| !d.is_empty())
        .collect();
    if set.is_empty() {
        None
    } else {
        Some(set.into_iter().collect())
    }
}

/// Stable row ID for a built-in snippet (`builtin:{scope}:{trigger}`), so seeding with
/// `INSERT OR IGNORE` runs on every launch without creating duplicates.
pub fn builtin_id(def: &SnippetDef) -> String {
    let scope = match normalize_dialects(def.dialects.clone()) {
        Some(dialects) => dialects.join("+"),
        None => "all".to_string(),
    };
    format!("builtin:{scope}:{}", def.trigger)
}

pub fn builtin_to_record(def: &SnippetDef, enabled: bool) -> SnippetRecord {
    SnippetRecord {
        id: builtin_id(def),
        trigger: def.trigger.clone(),
        name: def.name.clone(),
        content: def.content.clone(),
        description: def.description.clone(),
        category: def.category.clone(),
        dialects: normalize_dialects(def.dialects.clone()),
        is_builtin: true,
        is_enabled: enabled,
    }
}

fn s(
    trigger: &str,
    name: &str,
    category: &str,
    description: &str,
    content: &str,
    dialects: Option<&[&str]>,
) -> SnippetDef {
    SnippetDef {
        trigger: trigger.to_string(),
        name: name.to_string(),
        content: content.to_string(),
        description: Some(description.to_string()),
        category: Some(category.to_string()),
        dialects: dialects.map(|list| list.iter().map(|d| (*d).to_string()).collect()),
    }
}

/// All 25 built-in snippets matching the WinUI `SnippetCatalog` 1:1.
pub fn builtin_catalog() -> Vec<SnippetDef> {
    vec![
        s("sel", "SELECT *", "Select", "Select all columns from a table", "SELECT * FROM ${cursor}", None),
        s("selt", "SELECT TOP", "Select", "Select top N rows", "SELECT TOP ${1:100} * FROM ${cursor}", Some(&["mssql"])),
        s("sell", "SELECT LIMIT", "Select", "Select with LIMIT", "SELECT * FROM ${1:table} LIMIT ${2:100}${cursor}", Some(&["postgres"])),
        s("selc", "SELECT COUNT", "Select", "Count rows in a table", "SELECT COUNT(*) FROM ${cursor}", None),
        s("seld", "SELECT DISTINCT", "Select", "Select distinct values", "SELECT DISTINCT ${1:column} FROM ${cursor}", None),
        s("selw", "SELECT WHERE", "Select", "Select with WHERE clause", "SELECT * FROM ${1:table} WHERE ${cursor}", None),
        s("selj", "SELECT JOIN", "Select", "Select with INNER JOIN", "SELECT ${1:columns}\nFROM ${2:table1} t1\nINNER JOIN ${3:table2} t2 ON t1.${4:id} = t2.${5:id}\nWHERE ${cursor}", None),
        s("sellj", "SELECT LEFT JOIN", "Select", "Select with LEFT JOIN", "SELECT ${1:columns}\nFROM ${2:table1} t1\nLEFT JOIN ${3:table2} t2 ON t1.${4:id} = t2.${5:id}\nWHERE ${cursor}", None),
        s("selg", "SELECT GROUP BY", "Select", "Select with GROUP BY", "SELECT ${1:column}, COUNT(*) AS Count\nFROM ${2:table}\nGROUP BY ${cursor}\nORDER BY Count DESC", None),

        s("ins", "INSERT INTO", "Insert", "Insert a single row", "INSERT INTO ${1:table} (${2:columns})\nVALUES (${cursor})", None),
        s("inss", "INSERT SELECT", "Insert", "Insert from a SELECT", "INSERT INTO ${1:target_table} (${2:columns})\nSELECT ${3:columns}\nFROM ${cursor}", None),

        s("upd", "UPDATE", "Update", "Update rows", "UPDATE ${1:table}\nSET ${2:column} = ${3:value}\nWHERE ${cursor}", None),
        s("updj", "UPDATE JOIN", "Update", "Update with JOIN", "UPDATE t1\nSET t1.${1:column} = t2.${2:column}\nFROM ${3:table1} t1\nINNER JOIN ${4:table2} t2 ON t1.${5:id} = t2.${6:id}\nWHERE ${cursor}", Some(&["mssql"])),

        s("del", "DELETE", "Delete", "Delete rows", "DELETE FROM ${1:table}\nWHERE ${cursor}", None),
        s("trunc", "TRUNCATE", "Delete", "Truncate a table", "TRUNCATE TABLE ${cursor}", None),

        s("ct", "CREATE TABLE", "DDL", "Create a new table", "CREATE TABLE ${1:table_name} (\n    ${2:id} INT IDENTITY(1,1) PRIMARY KEY,\n    ${cursor}\n)", Some(&["mssql"])),
        s("ata", "ALTER TABLE ADD", "DDL", "Add a column", "ALTER TABLE ${1:table}\nADD ${2:column} ${cursor}", None),
        s("atd", "ALTER TABLE DROP", "DDL", "Drop a column", "ALTER TABLE ${1:table}\nDROP COLUMN ${cursor}", None),
        s("ci", "CREATE INDEX", "DDL", "Create an index", "CREATE INDEX IX_${1:index_name}\nON ${2:table} (${cursor})", None),
        s("di", "DROP INDEX", "DDL", "Drop an index", "DROP INDEX ${1:index_name} ON ${cursor}", Some(&["mssql"])),

        s("cte", "CTE", "CTE", "Common table expression", ";WITH ${1:cte_name} AS (\n    ${2:SELECT * FROM table}\n)\nSELECT * FROM ${cursor}", None),
        s("rcte", "Recursive CTE", "CTE", "Recursive CTE", ";WITH ${1:cte_name} AS (\n    -- Anchor\n    SELECT ${2:columns}\n    FROM ${3:table}\n    WHERE ${4:condition}\n\n    UNION ALL\n\n    -- Recursive\n    SELECT ${5:columns}\n    FROM ${6:table} t\n    INNER JOIN ${7:cte_name} c ON t.${8:parent_id} = c.${9:id}\n)\nSELECT * FROM ${10:cte_name}${cursor}", Some(&["mssql"])),

        s("cp", "CREATE PROCEDURE", "Procedure", "Create a stored procedure", "CREATE PROCEDURE ${1:procedure_name}\n    @${2:param} ${3:INT}\nAS\nBEGIN\n    SET NOCOUNT ON;\n    ${cursor}\nEND", Some(&["mssql"])),
        s("tx", "TRANSACTION", "Safety", "Run work inside a transaction", "BEGIN TRANSACTION;\n\n${cursor}\n\nCOMMIT TRANSACTION;", None),
        s("paste", "Paste clipboard", "Utility", "Insert clipboard contents", "${paste}${cursor}", None),
    ]
}

/// Finds the built-in catalog definition for an existing built-in record (matching by ID first,
/// then trigger + dialect scope).
pub fn builtin_default_for(existing: &SnippetRecord) -> Option<SnippetDef> {
    if !existing.is_builtin {
        return None;
    }
    let catalog = builtin_catalog();
    catalog
        .iter()
        .find(|def| builtin_id(def) == existing.id)
        .cloned()
        .or_else(|| {
            let existing_scope = normalize_dialects(existing.dialects.clone());
            catalog.into_iter().find(|def| {
                def.trigger.eq_ignore_ascii_case(&existing.trigger)
                    && normalize_dialects(def.dialects.clone()) == existing_scope
            })
        })
}

fn scopes_overlap(left: Option<&[String]>, right: Option<&[String]>) -> bool {
    match (left, right) {
        (None, _) | (_, None) => true,
        (Some(l), _) if l.is_empty() => true,
        (_, Some(r)) if r.is_empty() => true,
        (Some(l), Some(r)) => l
            .iter()
            .any(|ld| r.iter().any(|rd| ld.eq_ignore_ascii_case(rd))),
    }
}

/// Returns `true` when another snippet already answers to `trigger` in an overlapping dialect scope.
pub fn has_trigger_conflict(
    snippets: &[SnippetRecord],
    trigger: &str,
    dialects: Option<&[String]>,
    except_id: Option<&str>,
) -> bool {
    let candidate = normalize_dialects(dialects.map(|d| d.to_vec()));
    let normalized_trigger = trigger.trim();
    if normalized_trigger.is_empty() {
        return false;
    }

    snippets.iter().any(|snippet| {
        except_id.is_none_or(|id| snippet.id != id)
            && snippet.trigger.eq_ignore_ascii_case(normalized_trigger)
            && scopes_overlap(candidate.as_deref(), snippet.dialects.as_deref())
    })
}

/// Filters enabled snippets applicable to `dialect` and converts them to `SnippetDef`.
pub fn snippets_for_dialect(snippets: &[SnippetRecord], dialect: &str) -> Vec<SnippetDef> {
    let target = if dialect.trim().is_empty() {
        "mssql"
    } else {
        dialect.trim()
    };
    snippets
        .iter()
        .filter(|s| s.is_enabled && s.applies_to(target))
        .map(SnippetRecord::to_def)
        .collect()
}

/// Sorts snippets by `(category lowercase, trigger lowercase, builtin before custom)`.
pub fn sort_snippets(snippets: &mut [SnippetRecord]) {
    snippets.sort_by(|a, b| {
        let cat_a = a.category.as_deref().unwrap_or("").to_ascii_lowercase();
        let cat_b = b.category.as_deref().unwrap_or("").to_ascii_lowercase();
        match cat_a.cmp(&cat_b) {
            Ordering::Equal => match a
                .trigger
                .to_ascii_lowercase()
                .cmp(&b.trigger.to_ascii_lowercase())
            {
                Ordering::Equal => b.is_builtin.cmp(&a.is_builtin),
                other => other,
            },
            other => other,
        }
    });
}
