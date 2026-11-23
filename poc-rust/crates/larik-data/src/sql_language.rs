use std::collections::{BTreeMap, HashSet};

use crate::{DatabaseInfo, EditorLanguage, ForeignKeySchema, TableKind, TableSchema, TextSpan};

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

// ---------------------------------------------------------------------------
// WinUI CompletionEngine port (clause-, schema-, alias-, and FK/JOIN-aware)
// ---------------------------------------------------------------------------

/// Completion item kind for SQL editor suggestions.
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
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CompletionCandidate {
    pub label: String,
    pub detail: Option<String>,
    pub insert_text: String,
    pub filter_text: Option<String>,
    pub kind: CompletionKind,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ClauseScope {
    Table { alias_context: bool },
    Column,
    Database,
    Keyword,
}

#[derive(Debug, Clone)]
struct ScopeInfo {
    scope: ClauseScope,
    keyword: String,
    /// True when the token directly before the caret is a clause keyword (e.g. `FROM `, `JOIN `, `ON `, `USE `).
    immediately_after_clause: bool,
}

#[derive(Debug, Clone)]
struct TableRef<'a> {
    table: Option<&'a TableSchema>,
    cte_columns: Vec<String>,
    raw_name: String,
    alias: Option<String>,
}

impl<'a> TableRef<'a> {
    fn effective_qualifier(&self) -> &str {
        self.alias.as_deref().unwrap_or(&self.raw_name)
    }

    fn display_table_name(&self) -> &str {
        self.table.map(|t| t.name.as_str()).unwrap_or(&self.raw_name)
    }
}

struct RankedCandidate {
    rank: i32,
    candidate: CompletionCandidate,
}

/// Clause-, schema-, alias-, and JOIN-aware SQL completion generator (ported from WinUI CompletionEngine).
pub fn suggest_completions(
    sql: &str,
    byte_offset: usize,
    tables: &[TableSchema],
    language: &dyn EditorLanguage,
) -> (usize, Vec<CompletionCandidate>) {
    suggest_completions_with_context(sql, byte_offset, tables, &[], language, true)
}

/// Full SQL completion generator with database list (for `USE`) and explicit-vs-auto trigger handling.
pub fn suggest_completions_with_context(
    sql: &str,
    byte_offset: usize,
    tables: &[TableSchema],
    databases: &[DatabaseInfo],
    language: &dyn EditorLanguage,
    is_explicit: bool,
) -> (usize, Vec<CompletionCandidate>) {
    let caret = clamp_char_boundary(sql, byte_offset);
    let (word_start, word, qualifier) = extract_context(sql, caret);
    let word_lower = word.to_ascii_lowercase();

    let (stmt_start, stmt_end) = statement_bounds(sql, caret);
    let stmt_text = &sql[stmt_start..stmt_end];
    let rel_word_start = word_start.saturating_sub(stmt_start).min(stmt_text.len());
    let before_word_in_stmt = &stmt_text[..rel_word_start];

    let scope = detect_scope(before_word_in_stmt);

    // Avoid popping up an uninvited menu on trailing whitespace after a completed expression
    // unless we are immediately after a clause trigger (`FROM `, `JOIN `, `ON `, `USE `, `SELECT `, etc.)
    // or the user typed a qualifier dot or explicitly invoked Ctrl+Space.
    if !is_explicit
        && word.is_empty()
        && qualifier.is_none()
        && !scope.immediately_after_clause
    {
        return (word_start, Vec::new());
    }

    let ctes = find_ctes(stmt_text);
    let refs = find_table_references(stmt_text, tables, &ctes);
    let target_table = find_dml_target(stmt_text, tables);
    let variables = find_declared_variables(&sql[..caret]);

    let used_aliases: HashSet<String> = refs
        .iter()
        .filter_map(|r| r.alias.as_ref().map(|a| a.to_ascii_lowercase()))
        .collect();

    let mut ranked: Vec<RankedCandidate> = Vec::new();

    if let Some(ref qual) = qualifier {
        match scope.scope {
            ClauseScope::Table { alias_context } => {
                // Schema-qualified table lookup (`FROM dbo.<caret>` or `FROM [dbo].<caret>`).
                // Omit schema prefix from insert_text because the user already typed it!
                add_tables(
                    &mut ranked,
                    tables,
                    &ctes,
                    &word_lower,
                    Some(qual.as_str()),
                    alias_context,
                    &used_aliases,
                    language,
                );
            }
            _ => {
                // Qualified column lookup (`[o].<caret>`, `orders.<caret>`, `cte.<caret>`).
                if let Some(resolved_ref) = refs
                    .iter()
                    .find(|r| r.effective_qualifier().eq_ignore_ascii_case(qual))
                {
                    if let Some(table) = resolved_ref.table {
                        add_qualified_table_columns(&mut ranked, table, &word_lower, language, 0);
                    } else {
                        for col in &resolved_ref.cte_columns {
                            if let Some(match_rank) = fuzzy_rank(col, &word_lower) {
                                ranked.push(RankedCandidate {
                                    rank: match_rank,
                                    candidate: CompletionCandidate {
                                        label: col.clone(),
                                        detail: Some(format!("CTE · {}", resolved_ref.raw_name)),
                                        insert_text: language.quote_identifier(col),
                                        filter_text: Some(col.clone()),
                                        kind: CompletionKind::Column,
                                    },
                                });
                            }
                        }
                    }
                } else if let Some(table) = tables.iter().find(|t| t.name.eq_ignore_ascii_case(qual))
                {
                    add_qualified_table_columns(&mut ranked, table, &word_lower, language, 0);
                } else {
                    // Fallback: maybe qualifier is a schema name (`dbo.<caret>`).
                    add_tables(
                        &mut ranked,
                        tables,
                        &ctes,
                        &word_lower,
                        Some(qual.as_str()),
                        false,
                        &used_aliases,
                        language,
                    );
                }
            }
        }

        return (word_start, finalize_candidates(ranked));
    }

    match scope.scope {
        ClauseScope::Database => {
            add_databases(&mut ranked, databases, &word_lower, language);
        }
        ClauseScope::Table { alias_context } => {
            add_tables(
                &mut ranked,
                tables,
                &ctes,
                &word_lower,
                None,
                alias_context,
                &used_aliases,
                language,
            );
            add_keywords(&mut ranked, language.keywords(), &word_lower, 600);
        }
        ClauseScope::Column => {
            if scope.keyword.eq_ignore_ascii_case("ON") {
                add_join_conditions(&mut ranked, &refs, &word_lower, language);
            }

            add_variables(&mut ranked, &variables, &word_lower, 150);

            if !refs.is_empty() {
                add_scoped_columns(&mut ranked, &refs, &word_lower, language, 200);
            } else if let Some(target) = target_table {
                let is_set = scope.keyword.eq_ignore_ascii_case("SET");
                add_target_columns(&mut ranked, target, &word_lower, is_set, language, 200);
            }

            // Also offer table/alias qualifiers in column scope so typing `o` can complete `[o]`
            for r in &refs {
                let qual = r.effective_qualifier();
                if let Some(mr) = fuzzy_rank(qual, &word_lower) {
                    ranked.push(RankedCandidate {
                        rank: 380 + mr,
                        candidate: CompletionCandidate {
                            label: qual.to_string(),
                            detail: Some(format!("Alias · {}", r.display_table_name())),
                            insert_text: language.quote_identifier(qual),
                            filter_text: Some(qual.to_string()),
                            kind: CompletionKind::Table,
                        },
                    });
                }
            }

            add_functions(&mut ranked, language.functions(), &word_lower, 450);
            add_keywords(&mut ranked, language.keywords(), &word_lower, 550);
        }
        ClauseScope::Keyword => {
            add_keywords(&mut ranked, language.keywords(), &word_lower, 100);
            add_variables(&mut ranked, &variables, &word_lower, 200);
            if !refs.is_empty() {
                add_scoped_columns(&mut ranked, &refs, &word_lower, language, 300);
            }
            add_tables(
                &mut ranked,
                tables,
                &ctes,
                &word_lower,
                None,
                false,
                &used_aliases,
                language,
            );
            add_functions(&mut ranked, language.functions(), &word_lower, 500);
        }
    }

    (word_start, finalize_candidates(ranked))
}

fn finalize_candidates(mut ranked: Vec<RankedCandidate>) -> Vec<CompletionCandidate> {
    ranked.sort_by(|a, b| {
        a.rank
            .cmp(&b.rank)
            .then_with(|| a.candidate.label.to_ascii_lowercase().cmp(&b.candidate.label.to_ascii_lowercase()))
    });

    let mut seen = HashSet::new();
    let mut out = Vec::with_capacity(ranked.len().min(60));
    for item in ranked {
        let dedup_key = format!(
            "{:?}::{}",
            item.candidate.kind,
            item.candidate.insert_text.to_ascii_lowercase()
        );
        if seen.insert(dedup_key) {
            out.push(item.candidate);
            if out.len() >= 60 {
                break;
            }
        }
    }
    out
}

fn clamp_char_boundary(text: &str, offset: usize) -> usize {
    let mut idx = offset.min(text.len());
    while idx > 0 && !text.is_char_boundary(idx) {
        idx -= 1;
    }
    idx
}

fn is_word_char(c: char) -> bool {
    c.is_ascii_alphanumeric() || c == '_' || c == '@' || c == '$'
}

/// Extracts `(word_start, typed_word_unquoted, optional_qualifier)` at `caret`.
/// Handles bracketed qualifiers and prefixes like `[dbo].[Ord` or `[o].`.
fn extract_context(sql: &str, caret: usize) -> (usize, String, Option<String>) {
    let before = &sql[..caret];
    let bytes = before.as_bytes();
    let mut i = bytes.len();

    while i > 0 && is_word_char(bytes[i - 1] as char) {
        i -= 1;
    }
    let raw_word = &before[i..];

    // Include opening quote/bracket in replacement span so `[Ord` replaces cleanly with `[dbo].[Orders] AS [o]`.
    let word_start = if i > 0 && matches!(bytes[i - 1], b'[' | b'"' | b'`') {
        i - 1
    } else {
        i
    };

    // Check for preceding `.` qualifier (allowing optional whitespace isn't standard; check directly before word_start).
    let qualifier = if word_start > 0 && bytes[word_start - 1] == b'.' {
        let before_dot = &before[..word_start - 1];
        let trimmed = before_dot.trim_end();
        if let Some(last_byte) = trimmed.bytes().last() {
            if last_byte == b']' {
                trimmed
                    .rfind('[')
                    .map(|open| trimmed[open + 1..trimmed.len() - 1].to_string())
                    .filter(|s| !s.is_empty())
            } else if last_byte == b'"' || last_byte == b'`' {
                let q = last_byte as char;
                trimmed[..trimmed.len() - 1]
                    .rfind(q)
                    .map(|open| trimmed[open + 1..trimmed.len() - 1].to_string())
                    .filter(|s| !s.is_empty())
            } else {
                let mut q_start = trimmed.len();
                while q_start > 0 && is_word_char(trimmed.as_bytes()[q_start - 1] as char) {
                    q_start -= 1;
                }
                let q = &trimmed[q_start..];
                (!q.is_empty()).then(|| q.to_string())
            }
        } else {
            None
        }
    } else {
        None
    };

    (word_start, raw_word.to_string(), qualifier)
}

/// Finds `(start, end)` byte range of the SQL statement containing `caret` (split on `;` outside quotes/comments).
fn statement_bounds(sql: &str, caret: usize) -> (usize, usize) {
    let bytes = sql.as_bytes();
    let len = bytes.len();
    let mut i = 0;
    let mut start = 0;
    let mut in_single = false;
    let mut in_double = false;
    let mut in_bracket = false;
    let mut in_line_comment = false;
    let mut block_depth = 0usize;

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
        if block_depth > 0 {
            if b == b'/' && next == b'*' {
                block_depth += 1;
                i += 2;
                continue;
            }
            if b == b'*' && next == b'/' {
                block_depth -= 1;
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
                block_depth = 1;
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
                if i < caret {
                    start = i + 1;
                } else {
                    return (start, i);
                }
                i += 1;
            }
            _ => i += 1,
        }
    }

    (start, len)
}

#[derive(Debug, Clone)]
struct SqlToken {
    text: String,
    upper: String,
    depth: usize,
    quoted: bool,
}

/// Tokenizes a SQL statement while ignoring comments/string literals and tracking parenthesis depth.
/// Preserves qualified chains (`[dbo].[Orders]`) as separate tokens with `"."` between them.
fn tokenize_sql(text: &str) -> Vec<SqlToken> {
    let bytes = text.as_bytes();
    let len = bytes.len();
    let mut i = 0;
    let mut depth = 0usize;
    let mut tokens = Vec::new();

    while i < len {
        let b = bytes[i];
        let next = if i + 1 < len { bytes[i + 1] } else { 0 };

        if b.is_ascii_whitespace() {
            i += 1;
            continue;
        }
        // Line comment
        if b == b'-' && next == b'-' {
            i += 2;
            while i < len && bytes[i] != b'\n' {
                i += 1;
            }
            continue;
        }
        // Block comment
        if b == b'/' && next == b'*' {
            i += 2;
            let mut comment_depth = 1usize;
            while i < len && comment_depth > 0 {
                let cb = bytes[i];
                let cn = if i + 1 < len { bytes[i + 1] } else { 0 };
                if cb == b'/' && cn == b'*' {
                    comment_depth += 1;
                    i += 2;
                } else if cb == b'*' && cn == b'/' {
                    comment_depth -= 1;
                    i += 2;
                } else {
                    i += 1;
                }
            }
            continue;
        }
        // Single-quoted string literal (skip content, emit placeholder)
        if b == b'\'' {
            i += 1;
            while i < len {
                if bytes[i] == b'\'' {
                    if i + 1 < len && bytes[i + 1] == b'\'' {
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                i += 1;
            }
            tokens.push(SqlToken {
                text: "''".into(),
                upper: "''".into(),
                depth,
                quoted: true,
            });
            continue;
        }
        // Bracketed identifier `[name]`
        if b == b'[' {
            i += 1;
            let mut val = String::new();
            while i < len {
                if bytes[i] == b']' {
                    if i + 1 < len && bytes[i + 1] == b']' {
                        val.push(']');
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                let ch = text[i..].chars().next().unwrap();
                val.push(ch);
                i += ch.len_utf8();
            }
            let upper = val.to_ascii_uppercase();
            tokens.push(SqlToken {
                text: val,
                upper,
                depth,
                quoted: true,
            });
            continue;
        }
        // Double-quoted or backtick identifier
        if b == b'"' || b == b'`' {
            let delim = b;
            i += 1;
            let mut val = String::new();
            while i < len {
                if bytes[i] == delim {
                    if i + 1 < len && bytes[i + 1] == delim {
                        val.push(delim as char);
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                let ch = text[i..].chars().next().unwrap();
                val.push(ch);
                i += ch.len_utf8();
            }
            let upper = val.to_ascii_uppercase();
            tokens.push(SqlToken {
                text: val,
                upper,
                depth,
                quoted: true,
            });
            continue;
        }
        if b == b'(' {
            tokens.push(SqlToken {
                text: "(".into(),
                upper: "(".into(),
                depth,
                quoted: false,
            });
            depth += 1;
            i += 1;
            continue;
        }
        if b == b')' {
            depth = depth.saturating_sub(1);
            tokens.push(SqlToken {
                text: ")".into(),
                upper: ")".into(),
                depth,
                quoted: false,
            });
            i += 1;
            continue;
        }
        if matches!(b, b'.' | b',' | b'=' | b'<' | b'>' | b'!' | b'+' | b'-' | b'*' | b'/') {
            let s = (b as char).to_string();
            tokens.push(SqlToken {
                text: s.clone(),
                upper: s,
                depth,
                quoted: false,
            });
            i += 1;
            continue;
        }
        if is_word_char(b as char) {
            let start = i;
            while i < len && is_word_char(bytes[i] as char) {
                i += 1;
            }
            let word = &text[start..i];
            tokens.push(SqlToken {
                text: word.to_string(),
                upper: word.to_ascii_uppercase(),
                depth,
                quoted: false,
            });
            continue;
        }

        let ch = text[i..].chars().next().unwrap();
        i += ch.len_utf8();
    }

    tokens
}

fn detect_scope(before_word: &str) -> ScopeInfo {
    let tokens = tokenize_sql(before_word);
    if tokens.is_empty() {
        return ScopeInfo {
            scope: ClauseScope::Keyword,
            keyword: String::new(),
            immediately_after_clause: false,
        };
    }

    let last = tokens.last().unwrap();
    let prev = if tokens.len() >= 2 {
        Some(&tokens[tokens.len() - 2])
    } else {
        None
    };

    // Immediately after operator or comma or opening paren -> Column scope
    if matches!(last.text.as_str(), "=" | "<" | ">" | "!" | "," | "(") {
        let clause = last_clause_keyword(&tokens).unwrap_or_default();
        return ScopeInfo {
            scope: ClauseScope::Column,
            keyword: clause,
            immediately_after_clause: matches!(last.text.as_str(), "," | "(" | "="),
        };
    }

    // Check immediately-preceding clause keyword first (`FROM `, `JOIN `, `ON `, `USE `, etc.)
    if !last.quoted {
        if last.upper == "USE" {
            return ScopeInfo {
                scope: ClauseScope::Database,
                keyword: "USE".into(),
                immediately_after_clause: true,
            };
        }
        if matches!(last.upper.as_str(), "FROM" | "JOIN" | "APPLY") {
            let is_delete_from = last.upper == "FROM"
                && prev.is_some_and(|p| !p.quoted && p.upper == "DELETE");
            return ScopeInfo {
                scope: ClauseScope::Table {
                    alias_context: !is_delete_from,
                },
                keyword: last.upper.clone(),
                immediately_after_clause: true,
            };
        }
        if matches!(last.upper.as_str(), "INTO" | "UPDATE" | "TABLE") {
            return ScopeInfo {
                scope: ClauseScope::Table {
                    alias_context: false,
                },
                keyword: last.upper.clone(),
                immediately_after_clause: true,
            };
        }
        if last.upper == "ON" {
            return ScopeInfo {
                scope: ClauseScope::Column,
                keyword: "ON".into(),
                immediately_after_clause: true,
            };
        }
        if matches!(
            last.upper.as_str(),
            "SELECT"
                | "WHERE"
                | "AND"
                | "OR"
                | "SET"
                | "HAVING"
                | "BY"
                | "DISTINCT"
                | "LIKE"
                | "IN"
                | "BETWEEN"
                | "IS"
                | "NOT"
                | "THEN"
                | "WHEN"
                | "ELSE"
                | "CASE"
        ) {
            return ScopeInfo {
                scope: ClauseScope::Column,
                keyword: last.upper.clone(),
                immediately_after_clause: true,
            };
        }
    }

    // Otherwise scan backward for the active enclosing clause at the same paren depth
    let cur_depth = last.depth;
    for idx in (0..tokens.len()).rev() {
        let tok = &tokens[idx];
        if tok.quoted || tok.depth != cur_depth {
            continue;
        }
        match tok.upper.as_str() {
            "USE" => {
                return ScopeInfo {
                    scope: ClauseScope::Database,
                    keyword: "USE".into(),
                    immediately_after_clause: false,
                };
            }
            "FROM" | "JOIN" | "APPLY" => {
                // If user already wrote `FROM [dbo].[Orders] AS [o] <word>`, they're moving to the next clause (Keyword).
                // If only a partial schema/table token is there (e.g. `FROM dbo.`), stay in Table scope.
                let after = &tokens[idx + 1..];
                let trailing_dot = after.last().is_some_and(|t| t.text == ".");
                let is_delete_from = tok.upper == "FROM"
                    && idx > 0
                    && !tokens[idx - 1].quoted
                    && tokens[idx - 1].upper == "DELETE";
                if trailing_dot || after.is_empty() {
                    return ScopeInfo {
                        scope: ClauseScope::Table {
                            alias_context: !is_delete_from,
                        },
                        keyword: tok.upper.clone(),
                        immediately_after_clause: trailing_dot,
                    };
                }
                return ScopeInfo {
                    scope: ClauseScope::Keyword,
                    keyword: tok.upper.clone(),
                    immediately_after_clause: false,
                };
            }
            "INTO" | "UPDATE" | "TABLE" => {
                let after = &tokens[idx + 1..];
                let trailing_dot = after.last().is_some_and(|t| t.text == ".");
                if trailing_dot || after.is_empty() {
                    return ScopeInfo {
                        scope: ClauseScope::Table {
                            alias_context: false,
                        },
                        keyword: tok.upper.clone(),
                        immediately_after_clause: trailing_dot,
                    };
                }
                return ScopeInfo {
                    scope: ClauseScope::Keyword,
                    keyword: tok.upper.clone(),
                    immediately_after_clause: false,
                };
            }
            "ON" => {
                return ScopeInfo {
                    scope: ClauseScope::Column,
                    keyword: "ON".into(),
                    immediately_after_clause: false,
                };
            }
            "SELECT" | "WHERE" | "AND" | "OR" | "SET" | "HAVING" | "BY" | "WHEN" | "THEN" => {
                return ScopeInfo {
                    scope: ClauseScope::Column,
                    keyword: tok.upper.clone(),
                    immediately_after_clause: false,
                };
            }
            _ => {}
        }
    }

    ScopeInfo {
        scope: ClauseScope::Keyword,
        keyword: String::new(),
        immediately_after_clause: false,
    }
}

fn last_clause_keyword(tokens: &[SqlToken]) -> Option<String> {
    for tok in tokens.iter().rev() {
        if tok.quoted {
            continue;
        }
        if matches!(
            tok.upper.as_str(),
            "SELECT"
                | "FROM"
                | "JOIN"
                | "ON"
                | "WHERE"
                | "SET"
                | "HAVING"
                | "BY"
                | "VALUES"
                | "INTO"
                | "UPDATE"
        ) {
            return Some(tok.upper.clone());
        }
    }
    None
}

fn is_clause_stop_word(upper: &str) -> bool {
    matches!(
        upper,
        "WHERE"
            | "ON"
            | "INNER"
            | "LEFT"
            | "RIGHT"
            | "FULL"
            | "OUTER"
            | "CROSS"
            | "JOIN"
            | "APPLY"
            | "ORDER"
            | "GROUP"
            | "HAVING"
            | "SET"
            | "VALUES"
            | "UNION"
            | "EXCEPT"
            | "INTERSECT"
            | "SELECT"
            | "FROM"
            | "WITH"
            | "INTO"
            | "OPTION"
            | "FOR"
            | "OFFSET"
            | "FETCH"
            | "TOP"
            | "GO"
    )
}

/// Parses `WITH cte1 AS (SELECT ...), cte2 AS (...)` definitions in the current statement.
fn find_ctes(stmt: &str) -> Vec<(String, Vec<String>)> {
    let tokens = tokenize_sql(stmt);
    let mut ctes = Vec::new();
    let mut i = 0;
    while i < tokens.len() {
        if tokens[i].depth == 0 && !tokens[i].quoted && tokens[i].upper == "WITH" {
            i += 1;
            while i < tokens.len() {
                if tokens[i].depth != 0 {
                    i += 1;
                    continue;
                }
                let name = tokens[i].text.clone();
                if is_clause_stop_word(&tokens[i].upper) && !tokens[i].quoted {
                    break;
                }
                i += 1;
                let mut cols = Vec::new();
                // Optional explicit column list `cte(a, b)`
                if i < tokens.len() && tokens[i].text == "(" {
                    i += 1;
                    while i < tokens.len() && tokens[i].text != ")" {
                        if tokens[i].text != "," {
                            cols.push(tokens[i].text.clone());
                        }
                        i += 1;
                    }
                    if i < tokens.len() && tokens[i].text == ")" {
                        i += 1;
                    }
                }
                if i < tokens.len() && !tokens[i].quoted && tokens[i].upper == "AS" {
                    i += 1;
                    if i < tokens.len() && tokens[i].text == "(" {
                        let body_depth = tokens[i].depth + 1;
                        i += 1;
                        while i < tokens.len() && tokens[i].depth >= body_depth {
                            i += 1;
                        }
                        if i < tokens.len() && tokens[i].text == ")" {
                            i += 1;
                        }
                        ctes.push((name, cols));
                        if i < tokens.len() && tokens[i].text == "," {
                            i += 1;
                            continue;
                        }
                    }
                }
                break;
            }
        }
        i += 1;
    }
    ctes
}

/// Scans the entire current statement at top level for `FROM` / `JOIN` table references and aliases.
fn find_table_references<'a>(
    stmt: &str,
    tables: &'a [TableSchema],
    ctes: &[(String, Vec<String>)],
) -> Vec<TableRef<'a>> {
    let tokens = tokenize_sql(stmt);
    let mut refs = Vec::new();
    let mut i = 0;

    while i + 1 < tokens.len() {
        let tok = &tokens[i];
        if tok.depth == 0 && !tok.quoted && matches!(tok.upper.as_str(), "FROM" | "JOIN") {
            let mut cursor = i + 1;
            if cursor >= tokens.len() || tokens[cursor].text == "(" {
                i += 1;
                continue;
            }

            // Parse `[schema].[table]` or `[db].[schema].[table]` or `table`
            let mut parts = vec![tokens[cursor].text.clone()];
            cursor += 1;
            while cursor + 1 < tokens.len() && tokens[cursor].text == "." {
                parts.push(tokens[cursor + 1].text.clone());
                cursor += 2;
            }

            let (schema, table_name) = match parts.as_slice() {
                [name] => (None, name.clone()),
                [.., schema, name] => (Some(schema.clone()), name.clone()),
                [] => {
                    i += 1;
                    continue;
                }
            };

            if table_name.is_empty() || table_name.starts_with('@') {
                i = cursor;
                continue;
            }

            // Parse optional `[AS] [alias]`
            let mut alias = None;
            if cursor < tokens.len() && tokens[cursor].depth == 0 {
                if !tokens[cursor].quoted && tokens[cursor].upper == "AS" {
                    if cursor + 1 < tokens.len()
                        && tokens[cursor + 1].depth == 0
                        && (tokens[cursor + 1].quoted
                            || !is_clause_stop_word(&tokens[cursor + 1].upper))
                        && !matches!(tokens[cursor + 1].text.as_str(), "," | "(" | ")" | ".")
                    {
                        alias = Some(tokens[cursor + 1].text.clone());
                        cursor += 2;
                    }
                } else if (tokens[cursor].quoted || !is_clause_stop_word(&tokens[cursor].upper))
                    && !matches!(
                        tokens[cursor].text.as_str(),
                        "," | "(" | ")" | "." | "=" | "<" | ">"
                    )
                {
                    alias = Some(tokens[cursor].text.clone());
                    cursor += 1;
                }
            }

            let matched_cte = ctes
                .iter()
                .find(|(cte_name, _)| cte_name.eq_ignore_ascii_case(&table_name));
            let matched_table = if matched_cte.is_some() {
                None
            } else {
                find_table(&table_name, schema.as_deref(), tables)
            };

            refs.push(TableRef {
                table: matched_table,
                cte_columns: matched_cte.map(|(_, cols)| cols.clone()).unwrap_or_default(),
                raw_name: table_name,
                alias,
            });

            i = cursor;
            continue;
        }
        i += 1;
    }

    refs
}

fn find_dml_target<'a>(stmt: &str, tables: &'a [TableSchema]) -> Option<&'a TableSchema> {
    let tokens = tokenize_sql(stmt);
    for i in 0..tokens.len().saturating_sub(1) {
        if tokens[i].depth == 0
            && !tokens[i].quoted
            && matches!(tokens[i].upper.as_str(), "UPDATE" | "INTO")
        {
            let mut cursor = i + 1;
            let mut parts = vec![tokens[cursor].text.clone()];
            cursor += 1;
            while cursor + 1 < tokens.len() && tokens[cursor].text == "." {
                parts.push(tokens[cursor + 1].text.clone());
                cursor += 2;
            }
            let (schema, name) = match parts.as_slice() {
                [name] => (None, name.as_str()),
                [.., schema, name] => (Some(schema.as_str()), name.as_str()),
                [] => continue,
            };
            if let Some(table) = find_table(name, schema, tables) {
                return Some(table);
            }
        }
    }
    None
}

fn find_declared_variables(sql_before: &str) -> Vec<String> {
    let tokens = tokenize_sql(sql_before);
    let mut vars = Vec::new();
    let mut seen = HashSet::new();
    for i in 0..tokens.len().saturating_sub(1) {
        if !tokens[i].quoted && tokens[i].upper == "DECLARE" {
            let candidate = &tokens[i + 1].text;
            if candidate.starts_with('@')
                && candidate.len() > 1
                && seen.insert(candidate.to_ascii_lowercase())
            {
                vars.push(candidate.clone());
            }
        }
    }
    vars
}

fn find_table<'a>(
    name: &str,
    schema: Option<&str>,
    tables: &'a [TableSchema],
) -> Option<&'a TableSchema> {
    if let Some(sc) = schema {
        if let Some(exact) = tables
            .iter()
            .find(|t| t.name.eq_ignore_ascii_case(name) && t.schema.eq_ignore_ascii_case(sc))
        {
            return Some(exact);
        }
    }
    tables.iter().find(|t| t.name.eq_ignore_ascii_case(name))
}

// ---------------------------------------------------------------------------
// Candidate builders (exact WinUI behavior: schema + brackets + alias + JOIN ON)
// ---------------------------------------------------------------------------

#[allow(clippy::too_many_arguments)]
fn add_tables(
    out: &mut Vec<RankedCandidate>,
    tables: &[TableSchema],
    ctes: &[(String, Vec<String>)],
    word_lower: &str,
    typed_schema: Option<&str>,
    alias_context: bool,
    used_aliases: &HashSet<String>,
    language: &dyn EditorLanguage,
) {
    // CTEs first when no schema was typed
    if typed_schema.is_none() {
        for (cte_name, _) in ctes {
            if let Some(mr) = fuzzy_rank(cte_name, word_lower) {
                let quoted = language.quote_identifier(cte_name);
                if alias_context {
                    let alias = generate_unique_alias(cte_name, used_aliases);
                    let quoted_alias = language.quote_identifier(&alias);
                    out.push(RankedCandidate {
                        rank: mr,
                        candidate: CompletionCandidate {
                            label: format!("{cte_name} AS {alias}"),
                            detail: Some("CTE".into()),
                            insert_text: format!("{quoted} AS {quoted_alias}"),
                            filter_text: Some(cte_name.clone()),
                            kind: CompletionKind::Cte,
                        },
                    });
                } else {
                    out.push(RankedCandidate {
                        rank: mr,
                        candidate: CompletionCandidate {
                            label: cte_name.clone(),
                            detail: Some("CTE".into()),
                            insert_text: quoted,
                            filter_text: Some(cte_name.clone()),
                            kind: CompletionKind::Cte,
                        },
                    });
                }
            }
        }
    }

    for table in tables {
        if let Some(sc) = typed_schema {
            if !table.schema.eq_ignore_ascii_case(sc) {
                continue;
            }
        }

        let qualified_plain = if table.schema.is_empty() {
            table.name.clone()
        } else {
            format!("{}.{}", table.schema, table.name)
        };

        let match_rank = fuzzy_rank(&table.name, word_lower)
            .or_else(|| fuzzy_rank(&qualified_plain, word_lower).map(|r| r + 5));
        let Some(mr) = match_rank else {
            continue;
        };

        // Always bracket schema & table; omit schema only if user already typed `dbo.`
        let quoted_table = if typed_schema.is_some() || table.schema.is_empty() {
            language.quote_identifier(&table.name)
        } else {
            format!(
                "{}.{}",
                language.quote_identifier(&table.schema),
                language.quote_identifier(&table.name)
            )
        };

        let kind_label = match table.kind {
            TableKind::Table => "Table",
            TableKind::View => "View",
        };

        let display_base = if typed_schema.is_some() || table.schema.is_empty() {
            table.name.clone()
        } else {
            qualified_plain.clone()
        };

        if alias_context {
            let alias = generate_unique_alias(&table.name, used_aliases);
            let quoted_alias = language.quote_identifier(&alias);

            // Primary entry: `[dbo].[Orders] AS [o]`
            out.push(RankedCandidate {
                rank: 20 + mr,
                candidate: CompletionCandidate {
                    label: format!("{display_base} AS {alias}"),
                    detail: Some(format!("{kind_label} · {} cols", table.columns.len())),
                    insert_text: format!("{quoted_table} AS {quoted_alias}"),
                    filter_text: Some(format!("{} {}", table.name, qualified_plain)),
                    kind: CompletionKind::Table,
                },
            });

            // Secondary entry: `[dbo].[Orders]` (no alias)
            out.push(RankedCandidate {
                rank: 25 + mr,
                candidate: CompletionCandidate {
                    label: format!("{display_base} (no alias)"),
                    detail: Some(format!("{kind_label} · no alias")),
                    insert_text: quoted_table,
                    filter_text: Some(format!("{} {}", table.name, qualified_plain)),
                    kind: CompletionKind::Table,
                },
            });
        } else {
            out.push(RankedCandidate {
                rank: 20 + mr,
                candidate: CompletionCandidate {
                    label: display_base,
                    detail: Some(format!("{kind_label} · {} cols", table.columns.len())),
                    insert_text: quoted_table,
                    filter_text: Some(format!("{} {}", table.name, qualified_plain)),
                    kind: CompletionKind::Table,
                },
            });
        }
    }
}

/// Generates JOIN ... ON condition suggestions using declared FKs (forward & reverse, including
/// composite FKs) plus name-based heuristics (`customer_id -> customers.id`, shared columns).
fn add_join_conditions(
    out: &mut Vec<RankedCandidate>,
    refs: &[TableRef<'_>],
    word_lower: &str,
    language: &dyn EditorLanguage,
) {
    if refs.len() < 2 {
        return;
    }

    let joined = refs.last().unwrap();
    let Some(joined_table) = joined.table else {
        return;
    };
    let joined_qual = language.quote_identifier(joined.effective_qualifier());

    let mut suggestions: Vec<(i32, String, String)> = Vec::new();

    for other in &refs[..refs.len() - 1] {
        let Some(other_table) = other.table else {
            continue;
        };
        let other_qual = language.quote_identifier(other.effective_qualifier());

        // 1. Declared FKs: joined -> other (score 1000, grouped by constraint for composite FKs)
        let mut forward_groups: BTreeMap<String, Vec<&ForeignKeySchema>> = BTreeMap::new();
        for fk in &joined_table.foreign_keys {
            if foreign_key_targets(fk, other_table) {
                forward_groups
                    .entry(fk.constraint.clone())
                    .or_default()
                    .push(fk);
            }
        }
        for (constraint, keys) in forward_groups {
            let expr = keys
                .iter()
                .map(|fk| {
                    format!(
                        "{joined_qual}.{} = {other_qual}.{}",
                        language.quote_identifier(&fk.column),
                        language.quote_identifier(&fk.referenced_column)
                    )
                })
                .collect::<Vec<_>>()
                .join(" AND ");
            let detail = if constraint.is_empty() {
                format!("FK · {}", other_table.name)
            } else {
                format!("FK · {constraint}")
            };
            suggestions.push((1000, expr, detail));
        }

        // 2. Reverse declared FKs: other -> joined (score 980)
        let mut reverse_groups: BTreeMap<String, Vec<&ForeignKeySchema>> = BTreeMap::new();
        for fk in &other_table.foreign_keys {
            if foreign_key_targets(fk, joined_table) {
                reverse_groups
                    .entry(fk.constraint.clone())
                    .or_default()
                    .push(fk);
            }
        }
        for (constraint, keys) in reverse_groups {
            let expr = keys
                .iter()
                .map(|fk| {
                    format!(
                        "{joined_qual}.{} = {other_qual}.{}",
                        language.quote_identifier(&fk.referenced_column),
                        language.quote_identifier(&fk.column)
                    )
                })
                .collect::<Vec<_>>()
                .join(" AND ");
            let detail = if constraint.is_empty() {
                format!("FK · {}", other_table.name)
            } else {
                format!("FK · {constraint}")
            };
            suggestions.push((980, expr, detail));
        }

        // 3. Name heuristic: joined.xxx_id -> other.pk/id (score 900)
        let other_keys = preferred_key_columns(other_table);
        if other_keys.len() == 1 {
            let other_pk = &other_keys[0];
            for col in &joined_table.columns {
                if let Some(stem) = column_stem(&col.name) {
                    if matches_table_stem(&stem, &other_table.name) {
                        let expr = format!(
                            "{joined_qual}.{} = {other_qual}.{}",
                            language.quote_identifier(&col.name),
                            language.quote_identifier(other_pk)
                        );
                        suggestions.push((900, expr, format!("Join · {}", other_table.name)));
                    }
                }
            }
        }

        // 4. Reverse name heuristic: other.xxx_id -> joined.pk/id (score 840)
        let joined_keys = preferred_key_columns(joined_table);
        if joined_keys.len() == 1 {
            let joined_pk = &joined_keys[0];
            for col in &other_table.columns {
                if let Some(stem) = column_stem(&col.name) {
                    if matches_table_stem(&stem, &joined_table.name) {
                        let expr = format!(
                            "{joined_qual}.{} = {other_qual}.{}",
                            language.quote_identifier(joined_pk),
                            language.quote_identifier(&col.name)
                        );
                        suggestions.push((840, expr, format!("Join · {}", other_table.name)));
                    }
                }
            }
        }

        // 5. Shared column names (score 760 if PK else 720, skipping bare "id")
        for col in &joined_table.columns {
            if col.name.eq_ignore_ascii_case("id") {
                continue;
            }
            if let Some(other_col) = other_table
                .columns
                .iter()
                .find(|oc| oc.name.eq_ignore_ascii_case(&col.name))
            {
                let is_pk = joined_table
                    .primary_key
                    .iter()
                    .any(|pk| pk.eq_ignore_ascii_case(&col.name))
                    || other_table
                        .primary_key
                        .iter()
                        .any(|pk| pk.eq_ignore_ascii_case(&other_col.name));
                let score = if is_pk { 760 } else { 720 };
                let expr = format!(
                    "{joined_qual}.{} = {other_qual}.{}",
                    language.quote_identifier(&col.name),
                    language.quote_identifier(&other_col.name)
                );
                suggestions.push((score, expr, format!("Shared column · {}", col.name)));
            }
        }
    }

    // Highest score first -> lowest rank number first
    suggestions.sort_by(|a, b| b.0.cmp(&a.0));
    let mut seen_expr = HashSet::new();
    for (score, expr, detail) in suggestions {
        if !seen_expr.insert(expr.to_ascii_lowercase()) {
            continue;
        }
        if !word_lower.is_empty() && !expr.to_ascii_lowercase().contains(word_lower) {
            continue;
        }
        out.push(RankedCandidate {
            rank: 1000 - score,
            candidate: CompletionCandidate {
                label: expr.clone(),
                detail: Some(detail),
                insert_text: expr.clone(),
                filter_text: Some(expr),
                kind: CompletionKind::Join,
            },
        });
    }
}

fn add_qualified_table_columns(
    out: &mut Vec<RankedCandidate>,
    table: &TableSchema,
    word_lower: &str,
    language: &dyn EditorLanguage,
    base_rank: i32,
) {
    for col in &table.columns {
        if let Some(mr) = fuzzy_rank(&col.name, word_lower) {
            let is_pk = table
                .primary_key
                .iter()
                .any(|pk| pk.eq_ignore_ascii_case(&col.name));
            let detail = match (&col.data_type, is_pk) {
                (Some(dt), true) => format!("PK · {dt}"),
                (Some(dt), false) => dt.clone(),
                (None, true) => format!("PK · {}", table.name),
                (None, false) => table.name.clone(),
            };
            out.push(RankedCandidate {
                rank: base_rank + mr + if is_pk { 0 } else { 2 },
                candidate: CompletionCandidate {
                    label: col.name.clone(),
                    detail: Some(detail),
                    insert_text: language.quote_identifier(&col.name),
                    filter_text: Some(col.name.clone()),
                    kind: CompletionKind::Column,
                },
            });
        }
    }
}

fn add_scoped_columns(
    out: &mut Vec<RankedCandidate>,
    refs: &[TableRef<'_>],
    word_lower: &str,
    language: &dyn EditorLanguage,
    base_rank: i32,
) {
    let multi = refs.len() > 1;
    for r in refs {
        let qual = r.alias.as_deref().or_else(|| multi.then_some(r.raw_name.as_str()));
        let quoted_qual = qual.map(|q| language.quote_identifier(q));

        if let Some(table) = r.table {
            for col in &table.columns {
                let display = match qual {
                    Some(q) => format!("{q}.{}", col.name),
                    None => col.name.clone(),
                };
                let mr = fuzzy_rank(&col.name, word_lower)
                    .or_else(|| fuzzy_rank(&display, word_lower).map(|r| r + 4));
                let Some(mr) = mr else {
                    continue;
                };
                let insert_text = match &quoted_qual {
                    Some(qq) => format!("{qq}.{}", language.quote_identifier(&col.name)),
                    None => language.quote_identifier(&col.name),
                };
                let detail = match &col.data_type {
                    Some(dt) => format!("{} · {dt}", table.name),
                    None => table.name.clone(),
                };
                out.push(RankedCandidate {
                    rank: base_rank + mr,
                    candidate: CompletionCandidate {
                        label: display.clone(),
                        detail: Some(detail),
                        insert_text,
                        filter_text: Some(format!("{} {}", col.name, display)),
                        kind: CompletionKind::Column,
                    },
                });
            }
        } else {
            for col_name in &r.cte_columns {
                let display = match qual {
                    Some(q) => format!("{q}.{col_name}"),
                    None => col_name.clone(),
                };
                let mr = fuzzy_rank(col_name, word_lower)
                    .or_else(|| fuzzy_rank(&display, word_lower).map(|r| r + 4));
                let Some(mr) = mr else {
                    continue;
                };
                let insert_text = match &quoted_qual {
                    Some(qq) => format!("{qq}.{}", language.quote_identifier(col_name)),
                    None => language.quote_identifier(col_name),
                };
                out.push(RankedCandidate {
                    rank: base_rank + mr,
                    candidate: CompletionCandidate {
                        label: display.clone(),
                        detail: Some(format!("CTE · {}", r.raw_name)),
                        insert_text,
                        filter_text: Some(format!("{col_name} {display}")),
                        kind: CompletionKind::Column,
                    },
                });
            }
        }
    }
}

fn add_target_columns(
    out: &mut Vec<RankedCandidate>,
    table: &TableSchema,
    word_lower: &str,
    append_equals: bool,
    language: &dyn EditorLanguage,
    base_rank: i32,
) {
    for col in &table.columns {
        if let Some(mr) = fuzzy_rank(&col.name, word_lower) {
            let quoted = language.quote_identifier(&col.name);
            let insert_text = if append_equals {
                format!("{quoted} = ")
            } else {
                quoted
            };
            out.push(RankedCandidate {
                rank: base_rank + mr,
                candidate: CompletionCandidate {
                    label: col.name.clone(),
                    detail: Some(
                        col.data_type
                            .clone()
                            .unwrap_or_else(|| table.name.clone()),
                    ),
                    insert_text,
                    filter_text: Some(col.name.clone()),
                    kind: CompletionKind::Column,
                },
            });
        }
    }
}

fn add_databases(
    out: &mut Vec<RankedCandidate>,
    databases: &[DatabaseInfo],
    word_lower: &str,
    language: &dyn EditorLanguage,
) {
    for db in databases {
        if !db.is_accessible {
            continue;
        }
        if let Some(mr) = fuzzy_rank(&db.name, word_lower) {
            out.push(RankedCandidate {
                rank: 10 + mr,
                candidate: CompletionCandidate {
                    label: db.name.clone(),
                    detail: Some("Database".into()),
                    insert_text: language.quote_identifier(&db.name),
                    filter_text: Some(db.name.clone()),
                    kind: CompletionKind::Database,
                },
            });
        }
    }
}

fn add_variables(
    out: &mut Vec<RankedCandidate>,
    variables: &[String],
    word_lower: &str,
    base_rank: i32,
) {
    for var in variables {
        if let Some(mr) = fuzzy_rank(var, word_lower) {
            out.push(RankedCandidate {
                rank: base_rank + mr,
                candidate: CompletionCandidate {
                    label: var.clone(),
                    detail: Some("Variable".into()),
                    insert_text: var.clone(),
                    filter_text: Some(var.clone()),
                    kind: CompletionKind::Variable,
                },
            });
        }
    }
}

fn add_keywords(
    out: &mut Vec<RankedCandidate>,
    keywords: &[&str],
    word_lower: &str,
    base_rank: i32,
) {
    for kw in keywords {
        if let Some(mr) = fuzzy_rank(kw, word_lower) {
            out.push(RankedCandidate {
                rank: base_rank + mr,
                candidate: CompletionCandidate {
                    label: (*kw).to_string(),
                    detail: Some("Keyword".into()),
                    insert_text: (*kw).to_string(),
                    filter_text: Some((*kw).to_string()),
                    kind: CompletionKind::Keyword,
                },
            });
        }
    }
}

fn add_functions(
    out: &mut Vec<RankedCandidate>,
    functions: &[&str],
    word_lower: &str,
    base_rank: i32,
) {
    for func in functions {
        if let Some(mr) = fuzzy_rank(func, word_lower) {
            out.push(RankedCandidate {
                rank: base_rank + mr,
                candidate: CompletionCandidate {
                    label: (*func).to_string(),
                    detail: Some("Function".into()),
                    insert_text: format!("{func}()"),
                    filter_text: Some((*func).to_string()),
                    kind: CompletionKind::Function,
                },
            });
        }
    }
}

fn fuzzy_rank(candidate: &str, query_lower: &str) -> Option<i32> {
    if query_lower.is_empty() {
        return Some(0);
    }
    let lower = candidate.to_ascii_lowercase();
    if lower == query_lower {
        Some(0)
    } else if lower.starts_with(query_lower) {
        Some(1)
    } else if lower.contains(query_lower) {
        Some(10)
    } else {
        None
    }
}

// ---------------------------------------------------------------------------
// Alias + JOIN heuristics (ported directly from WinUI CompletionEngine.cs)
// ---------------------------------------------------------------------------

/// Generates an alias for a table name:
/// - CamelCase initials (`ApplicationUser` -> `au`)
/// - snake_case initials (`order_items` -> `oi`)
/// - Otherwise first letter (`Orders` -> `o`, `customers` -> `c`)
pub fn generate_table_alias(table_name: &str) -> String {
    let capitals: Vec<char> = table_name.chars().filter(|c| c.is_uppercase()).collect();
    if capitals.len() > 1 {
        return capitals
            .into_iter()
            .map(|c| c.to_ascii_lowercase())
            .collect();
    }

    let parts: Vec<&str> = table_name.split('_').filter(|p| !p.is_empty()).collect();
    if parts.len() > 1 {
        return parts
            .into_iter()
            .filter_map(|p| p.chars().next())
            .map(|c| c.to_ascii_lowercase())
            .collect();
    }

    table_name
        .chars()
        .next()
        .map(|c| c.to_ascii_lowercase().to_string())
        .unwrap_or_else(|| "t".to_string())
}

fn generate_unique_alias(table_name: &str, used_aliases: &HashSet<String>) -> String {
    let base = generate_table_alias(table_name);
    let mut alias = base.clone();
    let mut counter = 1usize;
    while used_aliases.contains(&alias) {
        alias = format!("{base}{counter}");
        counter += 1;
    }
    alias
}

fn foreign_key_targets(fk: &ForeignKeySchema, table: &TableSchema) -> bool {
    fk.referenced_table.eq_ignore_ascii_case(&table.name)
        && match fk.referenced_schema.as_deref() {
            None => true,
            Some(s) if s.trim().is_empty() => true,
            Some(s) => s.eq_ignore_ascii_case(&table.schema),
        }
}

fn preferred_key_columns(table: &TableSchema) -> Vec<String> {
    if !table.primary_key.is_empty() {
        return table.primary_key.clone();
    }
    table
        .columns
        .iter()
        .filter(|c| c.name.eq_ignore_ascii_case("id"))
        .map(|c| c.name.clone())
        .collect()
}

fn column_stem(column: &str) -> Option<String> {
    let norm = normalize_name(column);
    if norm.len() > 2 && norm.ends_with("id") {
        Some(norm[..norm.len() - 2].to_string())
    } else {
        None
    }
}

fn matches_table_stem(stem: &str, table_name: &str) -> bool {
    let norm = normalize_name(table_name);
    if norm.is_empty() || stem.is_empty() {
        return false;
    }
    let sing = singularize(&norm);
    for variant in [norm.as_str(), sing.as_str()] {
        if !variant.is_empty()
            && (stem == variant || stem.ends_with(variant) || variant.ends_with(stem))
        {
            return true;
        }
    }
    false
}

fn singularize(value: &str) -> String {
    if let Some(prefix) = value.strip_suffix("ies") {
        return format!("{prefix}y");
    }
    if let Some(prefix) = value.strip_suffix("ses") {
        return format!("{prefix}s");
    }
    if value.ends_with('s') && !value.ends_with("ss") {
        return value[..value.len() - 1].to_string();
    }
    value.to_string()
}

fn normalize_name(value: &str) -> String {
    value
        .chars()
        .filter(|c| c.is_alphanumeric())
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{ColumnSchema, TableKind};

    fn sample_schema() -> Vec<TableSchema> {
        vec![
            TableSchema {
                schema: "dbo".into(),
                name: "Customers".into(),
                kind: TableKind::Table,
                columns: vec![
                    ColumnSchema {
                        name: "id".into(),
                        data_type: Some("int".into()),
                        nullable: false,
                    },
                    ColumnSchema {
                        name: "full_name".into(),
                        data_type: Some("nvarchar(100)".into()),
                        nullable: false,
                    },
                ],
                primary_key: vec!["id".into()],
                foreign_keys: vec![],
            },
            TableSchema {
                schema: "dbo".into(),
                name: "Orders".into(),
                kind: TableKind::Table,
                columns: vec![
                    ColumnSchema {
                        name: "id".into(),
                        data_type: Some("int".into()),
                        nullable: false,
                    },
                    ColumnSchema {
                        name: "customer_id".into(),
                        data_type: Some("int".into()),
                        nullable: false,
                    },
                    ColumnSchema {
                        name: "total_amount".into(),
                        data_type: Some("decimal(18,2)".into()),
                        nullable: false,
                    },
                ],
                primary_key: vec!["id".into()],
                foreign_keys: vec![ForeignKeySchema {
                    constraint: "FK_Orders_Customers".into(),
                    column: "customer_id".into(),
                    referenced_schema: Some("dbo".into()),
                    referenced_table: "Customers".into(),
                    referenced_column: "id".into(),
                }],
            },
            TableSchema {
                schema: "sales".into(),
                name: "OrderItems".into(),
                kind: TableKind::Table,
                columns: vec![
                    ColumnSchema {
                        name: "id".into(),
                        data_type: Some("int".into()),
                        nullable: false,
                    },
                    ColumnSchema {
                        name: "order_id".into(),
                        data_type: Some("int".into()),
                        nullable: false,
                    },
                ],
                primary_key: vec!["id".into()],
                foreign_keys: vec![],
            },
        ]
    }

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
    fn from_table_adds_schema_brackets_and_alias_plus_no_alias() {
        let lang = SqlLanguage::tsql();
        let schema = sample_schema();
        let sql = "SELECT * FROM Ord";
        let (_, items) = suggest_completions(sql, sql.len(), &schema, &lang);
        assert_eq!(items[0].insert_text, "[dbo].[Orders] AS [o]");
        assert_eq!(items[1].insert_text, "[sales].[OrderItems] AS [oi]");
        assert!(items.iter().any(|i| i.insert_text == "[dbo].[Orders]"));
    }

    #[test]
    fn from_schema_dot_omits_schema_and_keeps_brackets_and_alias() {
        let lang = SqlLanguage::tsql();
        let schema = sample_schema();
        let sql = "SELECT * FROM dbo.Ord";
        let (_, items) = suggest_completions(sql, sql.len(), &schema, &lang);
        assert_eq!(items[0].insert_text, "[Orders] AS [o]");
        assert_eq!(items[1].insert_text, "[Orders]");
    }

    #[test]
    fn alias_deduplicates_when_already_used() {
        let lang = SqlLanguage::tsql();
        let schema = sample_schema();
        let sql = "SELECT * FROM [dbo].[Orders] AS [o] JOIN Ord";
        let (_, items) = suggest_completions(sql, sql.len(), &schema, &lang);
        assert_eq!(items[0].insert_text, "[dbo].[Orders] AS [o1]");
    }

    #[test]
    fn join_on_suggests_declared_foreign_key_condition() {
        let lang = SqlLanguage::tsql();
        let schema = sample_schema();
        let sql = "SELECT * FROM [dbo].[Customers] AS [c] INNER JOIN [dbo].[Orders] AS [o] ON ";
        let (_, items) = suggest_completions(sql, sql.len(), &schema, &lang);
        assert_eq!(items[0].kind, CompletionKind::Join);
        assert_eq!(items[0].insert_text, "[o].[customer_id] = [c].[id]");
    }

    #[test]
    fn join_on_suggests_heuristic_when_no_declared_fk() {
        let lang = SqlLanguage::tsql();
        let schema = sample_schema();
        let sql = "SELECT * FROM [dbo].[Orders] AS [o] LEFT JOIN [sales].[OrderItems] AS [oi] ON ";
        let (_, items) = suggest_completions(sql, sql.len(), &schema, &lang);
        assert_eq!(items[0].kind, CompletionKind::Join);
        assert_eq!(items[0].insert_text, "[oi].[order_id] = [o].[id]");
    }

    #[test]
    fn dot_qualified_alias_quotes_column_names() {
        let lang = SqlLanguage::tsql();
        let schema = sample_schema();
        let sql = "SELECT [o]. FROM [dbo].[Orders] AS [o]";
        let caret = "SELECT [o].".len();
        let (_, items) = suggest_completions(sql, caret, &schema, &lang);
        assert!(items.iter().any(|i| i.insert_text == "[customer_id]"));
        assert!(items.iter().any(|i| i.insert_text == "[total_amount]"));
    }

    #[test]
    fn scoped_select_columns_prefix_with_quoted_alias() {
        let lang = SqlLanguage::tsql();
        let schema = sample_schema();
        let sql = "SELECT cust FROM [dbo].[Orders] AS [o]";
        let caret = "SELECT cust".len();
        let (_, items) = suggest_completions(sql, caret, &schema, &lang);
        assert_eq!(items[0].insert_text, "[o].[customer_id]");
    }

    #[test]
    fn use_clause_suggests_accessible_databases_bracketed() {
        let lang = SqlLanguage::tsql();
        let dbs = vec![
            DatabaseInfo::accessible("larik_prod"),
            DatabaseInfo::inaccessible("secret_db"),
        ];
        let sql = "USE ";
        let (_, items) =
            suggest_completions_with_context(sql, sql.len(), &[], &dbs, &lang, false);
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].insert_text, "[larik_prod]");
    }
}
