use std::collections::HashMap;

use crate::{EditorLanguage, TableSchema, TextSpan};

/// Generic SQL + T-SQL dialect implementation for statement/batch splitting and completion metadata.
#[derive(Debug, Clone)]
pub struct SqlLanguage {
    pub supports_go_batches: bool,
}

impl SqlLanguage {
    pub const fn generic() -> Self {
        Self {
            supports_go_batches: false,
        }
    }

    pub const fn tsql() -> Self {
        Self {
            supports_go_batches: true,
        }
    }
}

pub const SQL_KEYWORDS: &[&str] = &[
    "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL OUTER JOIN",
    "CROSS JOIN", "ON", "AS", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS NULL",
    "IS NOT NULL", "ORDER BY", "GROUP BY", "HAVING", "TOP", "DISTINCT", "UNION", "UNION ALL",
    "EXCEPT", "INTERSECT", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE",
    "ALTER TABLE", "DROP TABLE", "CREATE VIEW", "CREATE INDEX", "WITH", "CASE", "WHEN", "THEN",
    "ELSE", "END", "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION", "DECLARE", "USE", "EXEC", "GO",
    "OVER", "PARTITION BY", "ASC", "DESC", "OFFSET", "FETCH NEXT", "ROWS ONLY", "MERGE", "OUTPUT",
    "PRINT", "TRY", "CATCH", "THROW", "RAISERROR", "NULL", "TRUE", "FALSE",
];

pub const SQL_FUNCTIONS: &[&str] = &[
    "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "ISNULL", "NULLIF", "CAST", "CONVERT",
    "GETDATE", "GETUTCDATE", "SYSDATETIME", "DATEADD", "DATEDIFF", "DATENAME", "DATEPART",
    "FORMAT", "LEN", "DATALENGTH", "LOWER", "UPPER", "LTRIM", "RTRIM", "TRIM", "SUBSTRING",
    "CHARINDEX", "REPLACE", "STRING_AGG", "CONCAT", "CONCAT_WS", "ROW_NUMBER", "RANK",
    "DENSE_RANK", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "IIF", "CHOOSE", "NEWID",
    "SCOPE_IDENTITY", "OBJECT_ID", "DB_NAME", "SUSER_SNAME", "JSON_VALUE", "JSON_QUERY",
    "OPENJSON", "ABS", "ROUND", "CEILING", "FLOOR",
];

impl EditorLanguage for SqlLanguage {
    fn syntax_id(&self) -> &'static str {
        "sql"
    }

    fn split_statements(&self, text: &str) -> Vec<TextSpan> {
        let batches = if self.supports_go_batches {
            self.split_batches(text)
        } else {
            vec![TextSpan {
                start: 0,
                end: text.len(),
                text: text.to_string(),
            }]
        };

        let mut out = Vec::new();
        for batch in batches {
            split_semicolon_statements(&batch.text, batch.start, &mut out);
        }
        out
    }

    fn split_batches(&self, text: &str) -> Vec<TextSpan> {
        if !self.supports_go_batches {
            let trimmed = trim_span(text, 0, text.len());
            return trimmed.into_iter().collect();
        }
        split_go_batches(text)
    }

    fn keywords(&self) -> &'static [&'static str] {
        SQL_KEYWORDS
    }

    fn functions(&self) -> &'static [&'static str] {
        SQL_FUNCTIONS
    }
}

fn trim_span(full: &str, start: usize, end: usize) -> Option<TextSpan> {
    let slice = &full[start..end];
    let leading = slice.len() - slice.trim_start().len();
    let trimmed = slice.trim();
    if trimmed.is_empty() {
        return None;
    }
    let s = start + leading;
    let e = s + trimmed.len();
    Some(TextSpan {
        start: s,
        end: e,
        text: trimmed.to_string(),
    })
}

fn split_semicolon_statements(text: &str, base_offset: usize, out: &mut Vec<TextSpan>) {
    let bytes = text.as_bytes();
    let len = bytes.len();
    let mut i = 0;
    let mut seg_start = 0;
    let mut in_single = false;
    let mut in_double = false;
    let mut in_bracket = false;
    let mut in_line_comment = false;
    let mut block_comment_depth: usize = 0;

    while i < len {
        let b = bytes[i];
        let next = if i + 1 < len { bytes[i + 1] } else { 0 };

        if in_line_comment {
            if b == b'\n' {
                in_line_comment = false;
            }
            i += 1;
            continue;
        }

        if block_comment_depth > 0 {
            if b == b'/' && next == b'*' {
                block_comment_depth += 1;
                i += 2;
                continue;
            }
            if b == b'*' && next == b'/' {
                block_comment_depth -= 1;
                i += 2;
                continue;
            }
            i += 1;
            continue;
        }

        if in_single {
            if b == b'\'' {
                if next == b'\'' {
                    i += 2;
                    continue;
                }
                in_single = false;
            }
            i += 1;
            continue;
        }

        if in_double {
            if b == b'"' {
                if next == b'"' {
                    i += 2;
                    continue;
                }
                in_double = false;
            }
            i += 1;
            continue;
        }

        if in_bracket {
            if b == b']' {
                if next == b']' {
                    i += 2;
                    continue;
                }
                in_bracket = false;
            }
            i += 1;
            continue;
        }

        match b {
            b'-' if next == b'-' => {
                in_line_comment = true;
                i += 2;
            }
            b'/' if next == b'*' => {
                block_comment_depth = 1;
                i += 2;
            }
            b'\'' => {
                in_single = true;
                i += 1;
            }
            b'"' => {
                in_double = true;
                i += 1;
            }
            b'[' => {
                in_bracket = true;
                i += 1;
            }
            b';' => {
                if let Some(mut span) = trim_span(text, seg_start, i) {
                    span.start += base_offset;
                    span.end += base_offset;
                    out.push(span);
                }
                seg_start = i + 1;
                i += 1;
            }
            _ => {
                i += 1;
            }
        }
    }

    if let Some(mut span) = trim_span(text, seg_start, len) {
        span.start += base_offset;
        span.end += base_offset;
        out.push(span);
    }
}

fn split_go_batches(text: &str) -> Vec<TextSpan> {
    let mut batches = Vec::new();
    let mut batch_start = 0;
    let mut offset = 0;

    for raw_line in text.split_inclusive('\n') {
        let line_len = raw_line.len();
        let trimmed = raw_line.trim();
        let is_go = is_go_separator_line(trimmed);
        if is_go {
            if let Some(span) = trim_span(text, batch_start, offset) {
                batches.push(span);
            }
            batch_start = offset + line_len;
        }
        offset += line_len;
    }

    if let Some(span) = trim_span(text, batch_start, text.len()) {
        batches.push(span);
    }

    batches
}

fn is_go_separator_line(trimmed: &str) -> bool {
    let mut parts = trimmed.split_whitespace();
    let Some(first) = parts.next() else {
        return false;
    };
    if !first.eq_ignore_ascii_case("GO") {
        return false;
    }
    match parts.next() {
        None => true,
        Some(rest) if rest.starts_with("--") => true,
        Some(count) => count.chars().all(|c| c.is_ascii_digit()),
    }
}

/// Completion item kind for SQL editor suggestions.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompletionKind {
    Table,
    Column,
    Keyword,
    Function,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CompletionCandidate {
    pub label: String,
    pub detail: Option<String>,
    pub insert_text: String,
    pub kind: CompletionKind,
}

/// Lightweight clause- and alias-aware SQL completion generator.
pub fn suggest_completions(
    sql: &str,
    byte_offset: usize,
    tables: &[TableSchema],
    language: &dyn EditorLanguage,
) -> (usize, Vec<CompletionCandidate>) {
    let clamped = byte_offset.min(sql.len());
    let before = &sql[..clamped];

    // Find current word prefix & optional qualifier (`alias.col` or `table.col`).
    let word_start = before
        .rfind(|c: char| !(c.is_ascii_alphanumeric() || c == '_' || c == '$'))
        .map(|idx| idx + 1)
        .unwrap_or(0);
    let prefix = &before[word_start..];
    let prefix_lower = prefix.to_ascii_lowercase();

    // Check if preceded by a dot: `<qualifier>.<prefix>`
    let qualifier = if word_start > 0 && before.as_bytes()[word_start - 1] == b'.' {
        let before_dot = &before[..word_start - 1];
        let q_start = before_dot
            .rfind(|c: char| !(c.is_ascii_alphanumeric() || c == '_' || c == '[' || c == ']'))
            .map(|idx| idx + 1)
            .unwrap_or(0);
        let q = before_dot[q_start..].trim_matches(|c| c == '[' || c == ']');
        (!q.is_empty()).then_some(q.to_ascii_lowercase())
    } else {
        None
    };

    let aliases = extract_table_aliases(sql);
    let mut results = Vec::new();

    if let Some(ref qual) = qualifier {
        // Resolve qualifier against aliases first, then exact table or schema name.
        let target_table_name = aliases.get(qual).map(String::as_str).unwrap_or(qual.as_str());

        for table in tables {
            if table.name.eq_ignore_ascii_case(target_table_name) {
                for col in &table.columns {
                    if matches_prefix(&col.name, &prefix_lower) {
                        results.push(CompletionCandidate {
                            label: col.name.clone(),
                            detail: Some(
                                col.data_type
                                    .clone()
                                    .unwrap_or_else(|| format!("{}.{}", table.schema, table.name)),
                            ),
                            insert_text: col.name.clone(),
                            kind: CompletionKind::Column,
                        });
                    }
                }
            } else if table.schema.eq_ignore_ascii_case(qual)
                && matches_prefix(&table.name, &prefix_lower)
            {
                results.push(CompletionCandidate {
                    label: table.name.clone(),
                    detail: Some(format!("Table ({})", table.schema)),
                    insert_text: table.name.clone(),
                    kind: CompletionKind::Table,
                });
            }
        }
        return (word_start, results);
    }

    // Unqualified completion: prioritize columns of referenced tables, then tables, then keywords & functions.
    let referenced_tables: Vec<&TableSchema> = tables
        .iter()
        .filter(|t| {
            aliases
                .values()
                .any(|referenced| referenced.eq_ignore_ascii_case(&t.name))
        })
        .collect();

    for table in &referenced_tables {
        for col in &table.columns {
            if matches_prefix(&col.name, &prefix_lower) {
                results.push(CompletionCandidate {
                    label: col.name.clone(),
                    detail: Some(match &col.data_type {
                        Some(dt) => format!("{} · {}", table.name, dt),
                        None => table.name.clone(),
                    }),
                    insert_text: col.name.clone(),
                    kind: CompletionKind::Column,
                });
            }
        }
    }

    for table in tables {
        if matches_prefix(&table.name, &prefix_lower) {
            results.push(CompletionCandidate {
                label: table.name.clone(),
                detail: Some(format!("{}.{}", table.schema, table.name)),
                insert_text: table.name.clone(),
                kind: CompletionKind::Table,
            });
        }
    }

    for kw in language.keywords() {
        if matches_prefix(kw, &prefix_lower) {
            results.push(CompletionCandidate {
                label: (*kw).to_string(),
                detail: Some("keyword".to_string()),
                insert_text: (*kw).to_string(),
                kind: CompletionKind::Keyword,
            });
        }
    }

    for func in language.functions() {
        if matches_prefix(func, &prefix_lower) {
            results.push(CompletionCandidate {
                label: (*func).to_string(),
                detail: Some("function".to_string()),
                insert_text: format!("{func}()"),
                kind: CompletionKind::Function,
            });
        }
    }

    // Deduplicate by lowercase label while preserving priority order.
    let mut seen = std::collections::HashSet::new();
    results.retain(|item| seen.insert(item.label.to_ascii_lowercase()));
    results.truncate(40);

    (word_start, results)
}

fn matches_prefix(candidate: &str, prefix_lower: &str) -> bool {
    if prefix_lower.is_empty() {
        return true;
    }
    let lower = candidate.to_ascii_lowercase();
    lower.starts_with(prefix_lower) || lower.contains(prefix_lower)
}

fn extract_table_aliases(sql: &str) -> HashMap<String, String> {
    let mut map = HashMap::new();
    let tokens: Vec<&str> = sql
        .split(|c: char| c.is_whitespace() || matches!(c, '(' | ')' | ',' | ';'))
        .filter(|t| !t.is_empty())
        .collect();

    let mut i = 0;
    while i + 1 < tokens.len() {
        let kw = tokens[i].to_ascii_uppercase();
        if matches!(kw.as_str(), "FROM" | "JOIN" | "UPDATE" | "INTO") {
            let raw_table = tokens[i + 1].trim_matches(|c| c == '[' || c == ']' || c == '"');
            let table_name = raw_table
                .rsplit('.')
                .next()
                .unwrap_or(raw_table)
                .trim_matches(|c| c == '[' || c == ']' || c == '"');
            if !table_name.is_empty() && !table_name.starts_with('@') {
                let table_lower = table_name.to_ascii_lowercase();
                map.insert(table_lower.clone(), table_lower.clone());

                // Optional alias: `FROM orders o` or `FROM orders AS o`
                let alias_idx = if i + 2 < tokens.len() && tokens[i + 2].eq_ignore_ascii_case("AS")
                {
                    i + 3
                } else {
                    i + 2
                };
                if alias_idx < tokens.len() {
                    let alias = tokens[alias_idx].trim_matches(|c| c == '[' || c == ']');
                    let alias_upper = alias.to_ascii_uppercase();
                    let is_clause_kw = matches!(
                        alias_upper.as_str(),
                        "WHERE"
                            | "ON"
                            | "INNER"
                            | "LEFT"
                            | "RIGHT"
                            | "FULL"
                            | "CROSS"
                            | "JOIN"
                            | "ORDER"
                            | "GROUP"
                            | "HAVING"
                            | "SET"
                            | "VALUES"
                            | "UNION"
                            | "SELECT"
                            | "WITH"
                    );
                    if !alias.is_empty() && !is_clause_kw {
                        map.insert(alias.to_ascii_lowercase(), table_lower);
                    }
                }
            }
        }
        i += 1;
    }

    map
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{ColumnSchema, TableKind};

    #[test]
    fn splits_statements_ignoring_strings_and_comments() {
        let lang = SqlLanguage::tsql();
        let sql = "SELECT 'a;b' -- ; comment\nFROM customers;\nSELECT * FROM orders;";
        let stmts = lang.split_statements(sql);
        assert_eq!(stmts.len(), 2);
        assert!(stmts[0].text.contains("'a;b'"));
        assert_eq!(stmts[1].text, "SELECT * FROM orders");
    }

    #[test]
    fn splits_tsql_go_batches() {
        let lang = SqlLanguage::tsql();
        let sql = "SELECT 1;\nGO\nSELECT 2;\n  go  \nSELECT 3;";
        let batches = lang.split_batches(sql);
        assert_eq!(batches.len(), 3);
        assert_eq!(batches[0].text, "SELECT 1;");
        assert_eq!(batches[1].text, "SELECT 2;");
        assert_eq!(batches[2].text, "SELECT 3;");
    }

    #[test]
    fn statement_at_picks_caret_statement() {
        let lang = SqlLanguage::tsql();
        let sql = "SELECT * FROM customers;\nSELECT * FROM orders;";
        let second_offset = sql.find("orders").unwrap();
        let picked = lang.statement_at(sql, second_offset).unwrap();
        assert_eq!(picked.text, "SELECT * FROM orders");
    }

    #[test]
    fn completes_aliased_columns() {
        let lang = SqlLanguage::tsql();
        let tables = vec![TableSchema {
            schema: "dbo".into(),
            name: "orders".into(),
            kind: TableKind::Table,
            columns: vec![
                ColumnSchema {
                    name: "id".into(),
                    data_type: Some("int".into()),
                    nullable: false,
                },
                ColumnSchema {
                    name: "total_amount".into(),
                    data_type: Some("decimal".into()),
                    nullable: false,
                },
            ],
            primary_key: vec!["id".into()],
            foreign_keys: vec![],
        }];
        let sql = "SELECT o.tot FROM dbo.orders o";
        let offset = sql.find("tot").unwrap() + 3;
        let (_, items) = suggest_completions(sql, offset, &tables, &lang);
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].label, "total_amount");
    }
}
